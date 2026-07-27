using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Puffs tire smoke out of any corner that's on the ground while the car is sliding. One particle
/// system covers all four: each particle is emitted manually at that corner's contact point with
/// a velocity that trails the way the car is scrubbing.
///
/// There is no per-wheel slip to read any more — the car has one sideways velocity and one grip
/// number — so every grounded corner smokes together, which is what a car held sideways looks
/// like anyway.
/// </summary>
[GlobalClass]
public partial class WheelSmoke : GpuParticles3D
{
    /// <summary>The vehicle to watch. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>Sideways speed in m/s past which the tires start to smoke.</summary>
    [Export] public float SlipThreshold { get; set; } = 3.0f;

    /// <summary>How far past the threshold sideways speed reaches full smoke, in m/s.</summary>
    [Export] public float FullSlip { get; set; } = 9.0f;

    /// <summary>
    /// How much of the car's scrub velocity a particle inherits. Well under 1: smoke is left
    /// behind by the car, it doesn't travel with it.
    /// </summary>
    [Export] public float ScrubInherit { get; set; } = 0.25f;

    public override void _Process(double delta)
    {
        if (VehicleNode is not { IsVehicleReady: true } vehicle)
            return;

        foreach (GroundRay ray in vehicle.WheelArray)
        {
            if (TireSlip.Intensity(vehicle, ray, SlipThreshold, FullSlip) <= 0.0f)
                continue;

            Transform3D smokeTransform = ray.GlobalTransform;
            smokeTransform.Origin = ray.LastCollisionPoint;

            // Sideways only: what makes smoke is the tire being dragged across the road, and
            // with no wheel spin to model that is the whole of it.
            Vector3 scrub = vehicle.GlobalTransform.Basis.X * (-vehicle.LateralSpeed * ScrubInherit);
            Vector3 velocity = GlobalTransform.Basis.Transposed() * scrub;

            EmitParticle(smokeTransform, velocity, Colors.White, Colors.White,
                         (uint)(EmitFlags.Position | EmitFlags.Velocity));
        }
    }
}
