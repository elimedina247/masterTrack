// Originally a C# port of addons/gevp/scripts/engine_sound.gd from Godot-Easy-Vehicle-Physics
// (MIT — see assets/gevp/LICENSE). Reworked to drive off road speed once the gearbox was
// removed from the physics.

using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Pitches a single looping engine sample by the revs of an <i>imaginary</i> gearbox.
///
/// The car has no transmission any more — drive force comes off a speed curve — so there are
/// no real revs to read. Instead the note sweeps up and drops back as road speed passes
/// through fake gear bands (see <see cref="FakeGearbox"/>). It sounds like a geared car, and
/// gives the player something to judge speed by, without a gearbox existing in the physics.
///
/// Two inputs, not one. <b>Speed</b> sets where in a gear band the note sits; <b>load</b> —
/// throttle, minus any brake — decides how far the revs sag below that. Speed alone is not
/// enough: the car coasts so freely that lifting off barely changes it for seconds, so a
/// purely speed-driven note goes on sounding like acceleration long after the player let go.
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

    /// <summary>
    /// Revs a closed throttle settles to, as a fraction of the way from <see cref="IdleRpm"/>
    /// up to the revs the current speed would otherwise call for.
    ///
    /// Without this the note is a pure function of road speed, and lifting off changes nothing
    /// audible — the car coasts at about 0.36 m/s², so speed (and therefore pitch) barely
    /// moves for seconds after the player lets go. Sagging the revs on a closed throttle is
    /// what actually tells the player they are off the power. Lower is a more dramatic drop;
    /// too low and it sounds like the clutch went in rather than the throttle closing.
    /// </summary>
    [Export] public float CoastRevRatio { get; set; } = 0.8f;

    /// <summary>How fast the note picks back up when the player gets on the throttle, per second.</summary>
    [Export] public float LoadAttack { get; set; } = 9.0f;

    /// <summary>
    /// How fast the note sags when the player lifts, per second. Deliberately quicker than
    /// <see cref="LoadAttack"/>: a throttle closes faster than an engine pulls.
    /// </summary>
    [Export] public float LoadRelease { get; set; } = 16.0f;

    /// <summary>
    /// Current fake revs. Exposed for a rev counter or shift effects; nothing reads it today
    /// — <c>VehicleHud</c> deliberately shows speed only, since a gauge fed backwards
    /// from road speed is just a second speedometer.
    /// </summary>
    public float Rpm { get; private set; }

    /// <summary>Current fake gear, 1-based.</summary>
    public int Gear { get; private set; } = 1;

    /// <summary>Smoothed engine load, 0..1. 1 is on the power, 0 is coasting or braking.</summary>
    public float Load { get; private set; }

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

        // Braking reads as a closed throttle even if the player is riding both pedals.
        float throttle = vehicle.ThrottleAmount * (1.0f - Mathf.Clamp(vehicle.BrakeAmount, 0.0f, 1.0f));
        float loadRate = throttle > Load ? LoadAttack : LoadRelease;
        Load = Mathf.Lerp(Load, throttle, 1.0f - Mathf.Exp(-loadRate * (float)delta));

        // Speed still sets where in the band the note sits; load decides how far the revs sag
        // below that. Gear stays purely speed-derived, so lifting off never fakes a shift.
        float coastRpm = Mathf.Lerp(IdleRpm, reading.Rpm, CoastRevRatio);
        float targetRpm = Mathf.Lerp(coastRpm, reading.Rpm, Load);

        // Smoothed so the rev drop between bands sounds like a shift rather than a glitch.
        Rpm = Mathf.Lerp(Rpm, targetRpm, 1.0f - Mathf.Exp(-Responsiveness * (float)delta));

        PitchScale = Rpm / SampleRpm;
        VolumeDb = Mathf.LinearToDb(Load * 0.55f + 0.3f);
    }
}
