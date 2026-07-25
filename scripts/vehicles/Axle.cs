// C# port of the Axle inner class from addons/gevp/scripts/vehicle.gd
// (Godot-Easy-Vehicle-Physics, MIT — see assets/gevp/LICENSE).

using System.Collections.Generic;
using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// A pair of wheels the drivetrain treats as one unit. Holds the brake bias, the
/// differential state and how torque is currently split between its two wheels.
/// Built by <see cref="Vehicle.Initialize"/>; not a scene node.
/// </summary>
public sealed class Axle
{
    public readonly List<Wheel> Wheels = new();

    /// <summary>True when the drivetrain sends any torque to this axle.</summary>
    public bool IsDriveAxle;

    /// <summary>Combined rotational inertia of both wheels.</summary>
    public float Inertia;

    /// <summary>True for the axle the handbrake acts on (the rear axle).</summary>
    public bool Handbrake;

    public float BrakeBias = 0.5f;

    /// <summary>Working value in -1..1 for how torque leans to one wheel.</summary>
    public float RotationSplit = 0.5f;

    /// <summary>Last split actually used, kept for the debug overlay.</summary>
    public float AppliedSplit = 0.5f;

    public float TorqueVectoring;
    public float SuspensionCompressionLeft;
    public float SuspensionCompressionRight;

    /// <summary>Scales this axle's spin so axles with different tire sizes compare fairly.</summary>
    public float TireSizeCorrection;

    /// <summary>Torque above which the differential locks the two wheels together.</summary>
    public float DifferentialLockTorque;

    /// <summary>Fastest-spinning wheel on the axle, corrected for tire size.</summary>
    public float GetSpin()
    {
        float spin = 0.0f;
        foreach (Wheel wheel in Wheels)
            spin = Mathf.Max(spin, wheel.Spin);
        return spin * TireSizeCorrection;
    }

    public float GetAverageSpin()
    {
        float spin = 0.0f;
        foreach (Wheel wheel in Wheels)
            spin += wheel.Spin;
        return spin / Wheels.Count;
    }

    /// <summary>Worst longitudinal slip on the axle — what traction control watches.</summary>
    public float GetMaxWheelSlipY()
    {
        float slip = 0.0f;
        foreach (Wheel wheel in Wheels)
            slip = Mathf.Max(slip, wheel.SlipVector.Y);
        return slip;
    }
}
