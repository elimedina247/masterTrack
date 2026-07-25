// C# port of addons/gevp/scripts/engine_sound.gd from Godot-Easy-Vehicle-Physics
// (MIT — see assets/gevp/LICENSE).

using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Pitches a single looping engine sample by motor RPM and rides the volume with the
/// throttle. Crude next to a real multi-sample engine, but it reads the revs well enough to
/// hear a shift, and it costs nothing.
/// </summary>
[GlobalClass]
public partial class EngineSound : AudioStreamPlayer3D
{
    /// <summary>The vehicle whose revs drive the pitch. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>The RPM the sample was recorded at — the pitch is 1.0 here.</summary>
    [Export] public float SampleRpm { get; set; } = 4000.0f;

    public override void _Ready()
    {
        if (VehicleNode == null)
            GD.PushWarning($"[EngineSound] {Name} has no VehicleNode assigned; staying silent.");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (VehicleNode == null || SampleRpm <= 0.0f)
            return;

        PitchScale = VehicleNode.MotorRpm / SampleRpm;
        VolumeDb = Mathf.LinearToDb(VehicleNode.ThrottleAmount * 0.5f + 0.5f);
    }
}
