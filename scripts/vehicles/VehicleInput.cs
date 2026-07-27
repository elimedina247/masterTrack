// C# port of addons/gevp/scripts/vehicle_controllergd.gd from Godot-Easy-Vehicle-Physics
// (MIT — see assets/gevp/LICENSE), reshaped so the same mapping can be driven from local
// input or from a value that arrived over the network.

using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// The five axes a <see cref="Vehicle"/> is driven by, as one plain value.
///
/// Keeping this separate from where it came from is what lets the same mapping serve local
/// driving and networked driving: a client samples its own keyboard into one of these, and
/// the same struct is what would go over the wire for server reconciliation later.
/// </summary>
public readonly struct VehicleInputState
{
    /// <summary>0..1.</summary>
    public float Throttle { get; init; }

    /// <summary>0..1.</summary>
    public float Brake { get; init; }

    /// <summary>-1..1. Positive steers <b>left</b>, matching the vehicle's convention.</summary>
    public float Steering { get; init; }

    /// <summary>
    /// Whether the drift button is held. What used to be the handbrake: the lever is gone, and
    /// the button now commits the car to a drift angle rather than locking an axle.
    /// </summary>
    public bool Drift { get; init; }

    /// <summary>
    /// Whether the nitro button is *held*, not whether it was just pressed. The vehicle finds
    /// the rising edge itself, so this stays a piece of state rather than an event and survives
    /// the trip over the wire intact.
    /// </summary>
    public bool Nitro { get; init; }

    public static VehicleInputState Idle => default;

    /// <summary>
    /// Read the mapped actions into a fresh input state. Throttle is squared, which gives a
    /// finer touch off the bottom of the pedal — a keyboard-friendly trick from the original.
    /// </summary>
    public static VehicleInputState Sample(VehicleInputActions actions)
        => new()
        {
            Throttle = Mathf.Pow(Strength(actions.Throttle), 2.0f),
            Brake = Strength(actions.Brake),
            Steering = Strength(actions.SteerLeft) - Strength(actions.SteerRight),
            Drift = Pressed(actions.Drift),
            Nitro = Pressed(actions.Nitro),
        };

    /// <summary>
    /// Push this state onto a vehicle for one physics step.
    ///
    /// In reverse the pedals swap over, so "forward on the stick" always means "away from
    /// where the nose is pointing". The raw (un-squared) action strengths are used for the
    /// swap, exactly as upstream does it.
    /// </summary>
    public void ApplyTo(Vehicle vehicle, VehicleInputActions actions)
    {
        vehicle.ThrottleInput = Throttle;
        vehicle.BrakeInput = Brake;
        vehicle.SteeringInput = Steering;
        vehicle.DriftInput = Drift;
        vehicle.NitroInput = Nitro;

        if (vehicle.CurrentGear != -1)
            return;

        vehicle.BrakeInput = Strength(actions.Throttle);
        vehicle.ThrottleInput = Strength(actions.Brake);
    }

    private static float Strength(string action)
        => string.IsNullOrEmpty(action) ? 0.0f : Input.GetActionStrength(action);

    private static bool Pressed(string action)
        => !string.IsNullOrEmpty(action) && Input.IsActionPressed(action);
}

/// <summary>
/// Which input actions drive a vehicle. Every name must exist in
/// <c>Project &gt; Project Settings &gt; Input Map</c>; leave one blank to disable that axis.
/// </summary>
[GlobalClass]
public partial class VehicleInputActions : Resource
{
    [Export] public string Throttle { get; set; } = "racer_accelerate";
    [Export] public string Brake { get; set; } = "racer_brake";
    [Export] public string SteerLeft { get; set; } = "racer_steer_left";
    [Export] public string SteerRight { get; set; } = "racer_steer_right";
    /// <summary>
    /// Commit to a drift. Still mapped to the old <c>racer_handbrake</c> action so existing
    /// keyboard and pad bindings carry over — the button is in the same place, it just does
    /// something else now.
    /// </summary>
    [Export] public string Drift { get; set; } = "racer_handbrake";

    [Export] public string Nitro { get; set; } = "racer_nitro";

    /// <summary>
    /// Put the car back on its wheels. Deliberately its own action rather than a <c>ui_*</c> one:
    /// Godot's <c>ui_accept</c> includes Space, which would fight the drift button every time you
    /// tried to slide the car.
    /// </summary>
    [Export] public string Reset { get; set; } = "racer_reset";

    // No clutch or shift actions: there is no gearbox and no drive curve. Drive force is a
    // clamped solve toward a target speed, and reverse is selected by holding the brake.
}
