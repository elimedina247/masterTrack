using Godot;

namespace MasterTrack.Tiles.Hazards;

/// <summary>
/// The launch pad as a slot component: one sprung plate that fires whatever drives over it
/// straight off the road surface. The proof piece for the hazard framework — it already
/// existed as tile geometry (see <c>TrackTile.BuildLaunchPads</c>), it is visually
/// unmistakable, and it needs no new physics: if this fires correctly from a slot, the
/// framework works.
///
/// Launches along the slot's own up rather than the world's, so a pad in a banked corner
/// throws you off the bank, which is both correct and funnier. The impulse scales with the
/// car's mass — a change in velocity, not momentum, so every car leaves at the same speed —
/// which is also what carries it past the tire solve: a launched car is airborne, and the
/// solve has nothing to say to a car with no road under it.
/// </summary>
public partial class LaunchPadHazard : TrackHazard
{
    /// <summary>Same number the tile-geometry pads use: about 28 m of air.</summary>
    private const float LaunchImpulse = 24.0f;

    /// <summary>Plate footprint, car-scale like every hazard measurement.</summary>
    private const float PadSize = 11.0f;

    private const float TriggerHeight = 5.0f;

    private StandardMaterial3D _plate = null!;

    public override void _Ready()
    {
        // The pad: a bright plate set into the road, 2 cm proud — seen from board altitude,
        // not felt by the suspension. Warm sprung-steel yellow, its own colour so a racer
        // learns it the way they learned the boost chevrons.
        _plate = HouseMaterial(new Color(0.95f, 0.78f, 0.12f));

        AddChild(new CsgBox3D
        {
            Size = new Vector3(PadSize, 0.5f, PadSize),
            Position = new Vector3(0.0f, 0.02f - 0.25f, 0.0f),
            Material = _plate,
        });

        // The trigger, standing on the plate. Mask is everything: the pad should fire for
        // anything that can drive, and which layer the car sits on is not its business.
        var trigger = new Area3D
        {
            Name = "PadTrigger",
            Position = new Vector3(0.0f, TriggerHeight * 0.5f, 0.0f),
            CollisionMask = uint.MaxValue,
        };
        trigger.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(PadSize, TriggerHeight, PadSize) },
        });
        AddChild(trigger);

        trigger.BodyEntered += body =>
        {
            if (body is RigidBody3D car)
                car.ApplyCentralImpulse(GlobalBasis.Y.Normalized() * (LaunchImpulse * car.Mass));
        };
    }

    /// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
    /// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
    public override void _ExitTree()
    {
        _plate.Dispose();
        _plate = null!;
    }
}
