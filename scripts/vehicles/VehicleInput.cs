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

    /// <summary>0..1.</summary>
    public float Handbrake { get; init; }

    /// <summary>0..1, where 1 is fully disengaged.</summary>
    public float Clutch { get; init; }

    public static VehicleInputState Idle => default;

    /// <summary>
    /// Read the mapped actions into a fresh input state. Throttle is squared, which gives a
    /// finer touch off the bottom of the pedal — a keyboard-friendly trick from the original.
    /// </summary>
    public static VehicleInputState Sample(VehicleInputActions actions)
    {
        float handbrake = Strength(actions.Handbrake);
        return new VehicleInputState
        {
            Throttle = Mathf.Pow(Strength(actions.Throttle), 2.0f),
            Brake = Strength(actions.Brake),
            Steering = Strength(actions.SteerLeft) - Strength(actions.SteerRight),
            Handbrake = handbrake,
            // Yanking the handbrake also kicks the clutch in, so the motor doesn't fight it.
            Clutch = Mathf.Clamp(Strength(actions.Clutch) + handbrake, 0.0f, 1.0f),
        };
    }

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
        vehicle.HandbrakeInput = Handbrake;
        vehicle.ClutchInput = Clutch;

        if (vehicle.CurrentGear != -1)
            return;

        vehicle.BrakeInput = Strength(actions.Throttle);
        vehicle.ThrottleInput = Strength(actions.Brake);
    }

    private static float Strength(string action)
        => string.IsNullOrEmpty(action) ? 0.0f : Input.GetActionStrength(action);
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
    [Export] public string Handbrake { get; set; } = "racer_handbrake";
    [Export] public string Clutch { get; set; } = "racer_clutch";
    [Export] public string ToggleTransmission { get; set; } = "racer_toggle_transmission";
    [Export] public string ShiftUp { get; set; } = "racer_shift_up";
    [Export] public string ShiftDown { get; set; } = "racer_shift_down";

    /// <summary>
    /// Handle the discrete, one-shot gearbox controls. Kept apart from
    /// <see cref="VehicleInputState"/> because these are edge-triggered events rather than
    /// axes, so they don't belong in a value that gets sampled and replayed.
    /// </summary>
    public void ProcessGearboxActions(Vehicle vehicle)
    {
        if (!string.IsNullOrEmpty(ToggleTransmission) && Input.IsActionJustPressed(ToggleTransmission))
            vehicle.AutomaticTransmission = !vehicle.AutomaticTransmission;

        if (!string.IsNullOrEmpty(ShiftUp) && Input.IsActionJustPressed(ShiftUp))
            vehicle.ManualShift(1);

        if (!string.IsNullOrEmpty(ShiftDown) && Input.IsActionJustPressed(ShiftDown))
            vehicle.ManualShift(-1);
    }
}
