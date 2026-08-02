using Godot;
using MasterTrack.Racer;
using MasterTrack.Tiles;

namespace MasterTrack.Sentry;

/// <summary>
/// A bomb sat on the road: inert for a moment after it lands, then armed, then gone the instant
/// a car comes near it. The arming delay is what keeps it a trap rather than a strike — the
/// sentry cannot drop it into a pack for a free hit; it has to be planted <i>ahead</i> of
/// someone, and a racer watching the road sees it blinking before it ever goes live.
///
/// Placeholder art on purpose: a red faceted cylinder in the house material, to be replaced by
/// an authored model later the way the missile's was. The fuse lamp on top is the state the
/// racers actually need to read — dark while it is still safe, burning once it is not.
///
/// Not replicated, same as the missile: every peer plants one off the same broadcast and runs
/// the same trigger check against the same (replicated) car positions, so they all agree within
/// a network beat. Each machine's blast only ever throws its own cars — see
/// <see cref="SentryBlast"/> — so a frame of disagreement about the trigger moment changes
/// nothing about who got launched.
/// </summary>
public partial class SentryBarrelBomb : Node3D
{
    /// <summary>Seconds between landing and being live. The window a racer gets to see it.</summary>
    private const float ArmDelay = 2.0f;

    /// <summary>How close a car may come to a live barrel, in metres. Roughly touching it.</summary>
    private const float TriggerRadius = 5.0f;

    /// <summary>Smaller than the missile's — this is a landmine, not an airstrike.</summary>
    private const float ExplosionRadius = TrackTile.Size * 0.55f;

    /// <summary>Speed the blast adds to a car at the centre of it, in m/s. A barrel goes off at
    /// trigger range, so whoever tripped it always eats close to the full number.</summary>
    private const float ExplosionStrength = 65.0f;

    private const float BarrelRadius = 1.6f;
    private const float BarrelHeight = 3.0f;

    private StandardMaterial3D _paint = null!;
    private StandardMaterial3D _fuseLamp = null!;
    private float _age;

    private bool Armed => _age >= ArmDelay;

    /// <summary>The point the trigger and the blast measure from: the barrel's middle.</summary>
    private Vector3 Center => GlobalPosition + Vector3.Up * (BarrelHeight * 0.5f);

    public override void _Ready()
    {
        // The barrel — "just a red cylinder for now", in the game's own facets and light.
        _paint = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.78f, 0.13f, 0.11f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            Metallic = 0.0f,
            Roughness = 1.0f,
        };
        AddChild(new CsgCylinder3D
        {
            Sides = 10, SmoothFaces = false,
            Radius = BarrelRadius, Height = BarrelHeight, Material = _paint,
            Position = new Vector3(0.0f, BarrelHeight * 0.5f, 0.0f),
        });

        // The fuse lamp: unshaded like every glow on the board, dark until the barrel is live.
        _fuseLamp = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.25f, 0.05f, 0.05f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        AddChild(new CsgSphere3D
        {
            RadialSegments = 8, Rings = 4, SmoothFaces = false,
            Radius = 0.45f, Material = _fuseLamp,
            Position = new Vector3(0.0f, BarrelHeight + 0.35f, 0.0f),
        });
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;

        if (!Armed)
        {
            // Arming: the whole barrel blinks, quickening toward live — the same countdown
            // language as a condemned tile or the missile's ring.
            float blink = Mathf.Sin(_age * (8.0f + _age * 6.0f)) > 0.0f ? 1.0f : 0.45f;
            _paint.AlbedoColor = new Color(0.78f * blink, 0.13f * blink, 0.11f * blink);
            return;
        }

        _paint.AlbedoColor = new Color(0.78f, 0.13f, 0.11f);

        // Live: the lamp burns, breathing slightly so it reads as lit rather than painted.
        float glow = 0.8f + 0.2f * Mathf.Sin(_age * 5.0f);
        _fuseLamp.AlbedoColor = new Color(1.0f * glow, 0.25f * glow, 0.1f * glow);
    }

    /// <summary>
    /// The tripwire. On the physics tick rather than the frame because it is a question about
    /// where the cars are, and that is when their answers move. Remote cars are checked at
    /// their replicated positions — which is where everyone can see them anyway.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!Armed)
            return;

        foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
        {
            if (node is not RacerController racer || !racer.IsInsideTree())
                continue;

            if (racer.GlobalPosition.DistanceTo(Center) > TriggerRadius)
                continue;

            SentryBlast.Explode(this, Center, ExplosionRadius, ExplosionStrength);
            QueueFree();
            return;
        }
    }
}
