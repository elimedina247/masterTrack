// C# port of addons/gevp/scripts/wheel_smoke.gd from Godot-Easy-Vehicle-Physics
// (MIT — see assets/gevp/LICENSE).

using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Puffs tire smoke out of any wheel that's slipping past a threshold. One particle system
/// covers all four wheels: each particle is emitted manually at the wheel's contact point
/// with a velocity that trails the way the tire is scrubbing.
/// </summary>
[GlobalClass]
public partial class WheelSmoke : GpuParticles3D
{
    /// <summary>The vehicle whose wheels to watch. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>Longitudinal slip ratio above which a wheel smokes (wheelspin / lockup).</summary>
    [Export] public float LongitudinalSlipThreshold { get; set; } = 0.5f;

    /// <summary>Slip angle in radians above which a wheel smokes (sliding sideways).</summary>
    [Export] public float LateralSlipThreshold { get; set; } = 1.0f;

    public override void _Process(double delta)
    {
        if (VehicleNode == null || !IsInstanceValid(VehicleNode))
            return;

        foreach (Wheel wheel in VehicleNode.WheelArray)
        {
            if (Mathf.Abs(wheel.SlipVector.X) <= LateralSlipThreshold
                && Mathf.Abs(wheel.SlipVector.Y) <= LongitudinalSlipThreshold)
                continue;

            Transform3D smokeTransform = wheel.GlobalTransform;
            smokeTransform.Origin = wheel.LastCollisionPoint;

            Vector3 scrub = wheel.LocalVelocity * 0.2f
                            - Vector3.Forward * wheel.Spin * wheel.TireRadius * 0.2f;
            Vector3 velocity = GlobalTransform.Basis.Transposed() * (wheel.GlobalTransform.Basis * scrub);

            // 5 == EMIT_FLAG_POSITION | EMIT_FLAG_VELOCITY
            EmitParticle(smokeTransform, velocity, Colors.White, Colors.White,
                         (uint)(EmitFlags.Position | EmitFlags.Velocity));
        }
    }
}
