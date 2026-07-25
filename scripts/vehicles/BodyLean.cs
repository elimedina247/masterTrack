using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Leans the car's visible shell into a turn: a small sideways shift plus a touch of roll.
///
/// This is feedback, not simulation. The rigid body's own weight transfer is real but subtle —
/// especially with a low centre of gravity — and from a chase camera directly behind the car
/// there's very little to tell you how hard you're actually cornering. Exaggerating it on the
/// mesh gives the player that read without changing how the car behaves.
///
/// Put this on a <see cref="Node3D"/> that sits between the vehicle and its body mesh, so the
/// mesh keeps whatever transform it was authored with and this only ever applies a delta on
/// top. Nothing here touches the collision shape or the wheels.
/// </summary>
[GlobalClass]
public partial class BodyLean : Node3D
{
    /// <summary>The vehicle to read cornering from. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>Sideways shift at full lean, in metres. This is the "offset" part.</summary>
    [Export] public float LeanOffset { get; set; } = 0.06f;

    /// <summary>Roll at full lean, in degrees. Set to 0 for a pure sideways slide.</summary>
    [Export] public float LeanRoll { get; set; } = 3.5f;

    /// <summary>
    /// Height the roll pivots around, in metres. Rolling about the car's origin (down at road
    /// level) swings the roof a long way for very little tilt; pivoting nearer the middle of
    /// the car looks like a body on springs.
    /// </summary>
    [Export] public float RollPivotHeight { get; set; } = 0.25f;

    /// <summary>
    /// True leans into the corner, like a kart or a motorbike — clearer to read, and what
    /// arcade racers tend to do. False leans out, the way real weight transfer works.
    /// </summary>
    [Export] public bool LeanIntoTurn { get; set; } = true;

    /// <summary>How much of the lean comes from raw steering input (0..1).</summary>
    [Export] public float SteeringWeight { get; set; } = 0.35f;

    /// <summary>How much of the lean comes from actual cornering force (0..1).</summary>
    [Export] public float LateralGWeight { get; set; } = 0.65f;

    /// <summary>Lateral g that counts as a full lean.</summary>
    [Export] public float MaxLateralG { get; set; } = 1.0f;

    /// <summary>Speed in m/s at which the steering-driven part reaches full strength.</summary>
    [Export] public float FullLeanSpeed { get; set; } = 14.0f;

    /// <summary>How quickly the lean chases its target. Higher is snappier.</summary>
    [Export] public float Responsiveness { get; set; } = 6.0f;

    private Transform3D _rest = Transform3D.Identity;
    private float _lean;

    public override void _Ready()
    {
        // Whatever this node was placed at is the neutral pose; the lean is applied on top.
        _rest = Transform;

        if (VehicleNode == null)
            GD.PushWarning($"[BodyLean] {Name} has no VehicleNode assigned; the body won't lean.");
    }

    public override void _Process(double delta)
    {
        if (VehicleNode is not { IsVehicleReady: true } vehicle)
            return;

        _lean = Mathf.Lerp(_lean, TargetLean(vehicle),
                           1.0f - Mathf.Exp(-Responsiveness * (float)delta));

        float direction = LeanIntoTurn ? 1.0f : -1.0f;

        // Positive lean means turning left, and local +X is the car's right, so leaning into
        // a left turn moves the body toward -X.
        float sideways = -_lean * LeanOffset * direction;
        float roll = _lean * Mathf.DegToRad(LeanRoll) * direction;

        // Roll about a pivot above the origin rather than through the floor.
        var basis = new Basis(Vector3.Back, roll);
        var pivot = new Vector3(0.0f, RollPivotHeight, 0.0f);
        Vector3 origin = pivot - basis * pivot + new Vector3(sideways, 0.0f, 0.0f);

        Transform = _rest * new Transform3D(basis, origin);
    }

    /// <summary>
    /// Where the lean wants to be, in -1..1, positive for a left-hand turn.
    ///
    /// Blends two sources on purpose: steering input responds the instant the player turns,
    /// which keeps the car feeling connected to their hands, while actual cornering force is
    /// what makes a fast corner read as heavier than a slow one.
    /// </summary>
    private float TargetLean(Vehicle vehicle)
    {
        // Centripetal acceleration: yaw rate times how fast we're going down the road.
        float forwardSpeed = -vehicle.LocalVelocity.Z;
        float lateralG = vehicle.AngularVelocity.Y * forwardSpeed / 9.8f;
        float fromCornering = Mathf.Clamp(lateralG / Mathf.Max(MaxLateralG, 0.001f), -1.0f, 1.0f);

        // Ramped in with speed, so turning the wheel while parked doesn't rock the car.
        float speedFactor = Mathf.Clamp(vehicle.Speed / Mathf.Max(FullLeanSpeed, 0.001f), 0.0f, 1.0f);
        float fromSteering = Mathf.Clamp(vehicle.SteeringInput, -1.0f, 1.0f) * speedFactor;

        return Mathf.Clamp(fromSteering * SteeringWeight + fromCornering * LateralGWeight,
                           -1.0f, 1.0f);
    }
}
