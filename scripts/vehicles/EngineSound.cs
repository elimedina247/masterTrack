// Originally a C# port of addons/gevp/scripts/engine_sound.gd from Godot-Easy-Vehicle-Physics
// (MIT — see assets/gevp/LICENSE). Reworked to drive off road speed once the gearbox was
// removed from the physics.

using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Pitches a single looping engine sample by the revs of an <i>imaginary</i> gearbox, and
/// rides the volume with the throttle.
///
/// The car has no transmission any more — drive force comes off a speed curve — so there are
/// no real revs to read. Instead the note sweeps up and drops back as road speed passes
/// through fake gear bands (see <see cref="FakeGearbox"/>). It sounds like a geared car, and
/// gives the player something to judge speed by, without a gearbox existing in the physics.
/// </summary>
[GlobalClass]
public partial class EngineSound : AudioStreamPlayer3D
{
    /// <summary>The vehicle whose speed drives the pitch. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>The RPM the sample was recorded at — the pitch is 1.0 here.</summary>
    [Export] public float SampleRpm { get; set; } = 4000.0f;

    /// <summary>How many bands to divide the speed range into. Purely a sound choice.</summary>
    [Export] public int GearCount { get; set; } = 6;

    [Export] public float IdleRpm { get; set; } = 900.0f;
    [Export] public float MaxRpm { get; set; } = 7200.0f;

    /// <summary>Where revs drop to on an imaginary upshift, across the idle-to-max range.</summary>
    [Export] public float ShiftDownFraction { get; set; } = 0.42f;

    /// <summary>Higher = shorter low gears and a longer top gear, like a real ratio spread.</summary>
    [Export] public float BandExponent { get; set; } = 1.7f;

    /// <summary>How quickly the note chases its target, so imaginary shifts aren't instant clicks.</summary>
    [Export] public float Responsiveness { get; set; } = 18.0f;

    /// <summary>Current fake revs. Read by the HUD so both agree on what gear you're "in".</summary>
    public float Rpm { get; private set; }

    /// <summary>Current fake gear, 1-based.</summary>
    public int Gear { get; private set; } = 1;

    public override void _Ready()
    {
        Rpm = IdleRpm;

        if (VehicleNode == null)
            GD.PushWarning($"[EngineSound] {Name} has no VehicleNode assigned; staying silent.");
    }

    public override void _Process(double delta)
    {
        if (VehicleNode is not { IsVehicleReady: true } vehicle || SampleRpm <= 0.0f)
            return;

        FakeGearbox.Reading reading = FakeGearbox.Sample(
            vehicle.SpeedFraction, GearCount, IdleRpm, MaxRpm, ShiftDownFraction, BandExponent);

        Gear = reading.Gear;
        // Smoothed so the rev drop between bands sounds like a shift rather than a glitch.
        Rpm = Mathf.Lerp(Rpm, reading.Rpm, 1.0f - Mathf.Exp(-Responsiveness * (float)delta));

        PitchScale = Rpm / SampleRpm;
        VolumeDb = Mathf.LinearToDb(vehicle.ThrottleAmount * 0.5f + 0.5f);
    }
}
