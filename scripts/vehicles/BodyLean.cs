using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Poses the car's visible shell: roll into a corner, squat and dive under power and braking, and
/// extra yaw into a drift.
///
/// <b>This is feedback, not simulation.</b> Walaber calls it "posing the car" and it is a separate
/// system from the physics on purpose — the rigid body's own weight transfer is real but far too
/// subtle to read from a chase camera directly behind the car, especially with the low centre of
/// mass an arcade racer needs to stop it flipping. Exaggerating it on the mesh gives the player
/// the read without touching how the car behaves.
///
/// The old version derived everything from measured lateral g, which is why it was so restrained:
/// measured g is honest, and honest is not the goal. This one is driven mostly by what the player
/// is <i>asking for</i> — steering input, drift state, throttle — so the car answers the stick
/// visually before the physics has caught up.
///
/// Put this on a <see cref="Node3D"/> between the vehicle and its body mesh, so the mesh keeps
/// whatever transform it was authored with and this only ever applies a delta. Nothing here
/// touches the collision shape or the ground rays.
/// </summary>
[GlobalClass]
public partial class BodyLean : Node3D
{
    /// <summary>The vehicle to read from. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    // ---------------------------------------------------------------- Roll

    /// <summary>
    /// Roll at full cornering, in degrees. Much larger than a real car — this is the main thing
    /// that tells the player how hard they are cornering, so it wants to be seen.
    /// </summary>
    [ExportGroup("Roll")]
    [Export] public float LeanRoll { get; set; } = 9.0f;

    /// <summary>Extra roll on top of that while fully drifting, in degrees.</summary>
    [Export] public float DriftRoll { get; set; } = 7.0f;

    /// <summary>
    /// True leans into the corner, like a kart or a motorbike — clearer to read, and what arcade
    /// racers tend to do. False leans out, the way real weight transfer works.
    /// </summary>
    [Export] public bool LeanIntoTurn { get; set; } = true;

    /// <summary>
    /// Height the roll pivots around, in metres. Rolling about the car's origin (down at road
    /// level) swings the roof a long way for very little tilt; pivoting nearer the middle of the
    /// car looks like a body on springs.
    /// </summary>
    [Export] public float RollPivotHeight { get; set; } = 0.35f;

    /// <summary>Sideways shift at full lean, in metres.</summary>
    [Export] public float LeanOffset { get; set; } = 0.09f;

    // ---------------------------------------------------------------- Drift yaw

    /// <summary>
    /// Extra yaw into the slide at full drift, in degrees. The shell sits further sideways than
    /// the body actually is, which is what makes a drift read as a drift from behind rather than
    /// as the car merely cornering hard.
    /// </summary>
    [ExportGroup("Drift")]
    [Export] public float DriftYaw { get; set; } = 8.0f;

    // ---------------------------------------------------------------- Pitch

    /// <summary>Nose-up squat at full acceleration, in degrees.</summary>
    [ExportGroup("Pitch")]
    [Export] public float SquatPitch { get; set; } = 2.5f;

    /// <summary>Nose-down dive at full braking, in degrees.</summary>
    [Export] public float DivePitch { get; set; } = 4.0f;

    /// <summary>Extra squat while a boost is burning, in degrees. Sells the shove.</summary>
    [Export] public float BoostPitch { get; set; } = 3.0f;

    // ---------------------------------------------------------------- Response

    /// <summary>How much of the lean comes from raw steering input, 0..1.</summary>
    [ExportGroup("Response")]
    [Export] public float SteeringWeight { get; set; } = 0.55f;

    /// <summary>How much of the lean comes from actual cornering force, 0..1.</summary>
    [Export] public float LateralGWeight { get; set; } = 0.45f;

    /// <summary>Lateral g that counts as a full lean.</summary>
    [Export] public float MaxLateralG { get; set; } = 1.0f;

    /// <summary>Speed in m/s at which the steering-driven part reaches full strength.</summary>
    [Export] public float FullLeanSpeed { get; set; } = 12.0f;

    /// <summary>How quickly the pose chases its target. Higher is snappier.</summary>
    [Export] public float Responsiveness { get; set; } = 8.0f;

    private Transform3D _rest = Transform3D.Identity;
    private float _lean;
    private float _drift;
    private float _pitch;

    public override void _Ready()
    {
        // Whatever this node was placed at is the neutral pose; everything below is applied on top.
        _rest = Transform;

        if (VehicleNode == null)
            GD.PushWarning($"[BodyLean] {Name} has no VehicleNode assigned; the body won't lean.");
    }

    public override void _Process(double delta)
    {
        if (VehicleNode is not { IsVehicleReady: true } vehicle)
            return;

        float t = 1.0f - Mathf.Exp(-Responsiveness * (float)delta);
        _lean = Mathf.Lerp(_lean, TargetLean(vehicle), t);
        _pitch = Mathf.Lerp(_pitch, TargetPitch(vehicle), t);

        // Signed by the drift direction rather than by the lean, so the shell keeps pointing into
        // the slide even through the moment the player countersteers and the lean crosses zero.
        _drift = Mathf.Lerp(_drift, vehicle.DriftBlend * vehicle.DriftDirection, t);

        float direction = LeanIntoTurn ? 1.0f : -1.0f;

        // Positive lean means turning left, and local +X is the car's right, so leaning into a
        // left turn moves the body toward −X.
        float sideways = -_lean * LeanOffset * direction;
        float roll = (_lean * Mathf.DegToRad(LeanRoll)
                      + _drift * Mathf.DegToRad(DriftRoll)) * direction;
        float yaw = _drift * Mathf.DegToRad(DriftYaw);

        // Roll about a pivot above the origin rather than through the floor, then yaw and pitch
        // about the same point so the whole shell moves as one piece.
        var basis = Basis.FromEuler(new Vector3(_pitch, yaw, roll));
        var pivot = new Vector3(0.0f, RollPivotHeight, 0.0f);
        Vector3 origin = pivot - basis * pivot + new Vector3(sideways, 0.0f, 0.0f);

        Transform = _rest * new Transform3D(basis, origin);
    }

    /// <summary>
    /// Where the roll wants to be, in −1..1, positive for a left-hand turn.
    ///
    /// Blends two sources on purpose: steering input responds the instant the player turns, which
    /// keeps the car feeling connected to their hands, while actual cornering force is what makes
    /// a fast corner read as heavier than a slow one.
    /// </summary>
    private float TargetLean(Vehicle vehicle)
    {
        // Centripetal acceleration: yaw rate times how fast we're going down the road.
        float lateralG = vehicle.AngularVelocity.Y * vehicle.ForwardSpeed / 9.8f;
        float fromCornering = Mathf.Clamp(lateralG / Mathf.Max(MaxLateralG, 0.001f), -1.0f, 1.0f);

        // Ramped in with speed, so turning the wheel while parked doesn't rock the car.
        float speedFactor = Mathf.Clamp(vehicle.Speed / Mathf.Max(FullLeanSpeed, 0.001f), 0.0f, 1.0f);
        float fromSteering = Mathf.Clamp(vehicle.SteeringInput, -1.0f, 1.0f) * speedFactor;

        return Mathf.Clamp(fromSteering * SteeringWeight + fromCornering * LateralGWeight,
                           -1.0f, 1.0f);
    }

    /// <summary>
    /// Where the pitch wants to be, in radians. Positive is nose-up.
    ///
    /// Braking wins over throttle when both are held, because that is the one the player needs to
    /// see — a car that dives when you stamp on it reads as having weight.
    /// </summary>
    private float TargetPitch(Vehicle vehicle)
    {
        if (vehicle.BrakeAmount > 0.05f && vehicle.CurrentGear == 1)
            return -Mathf.DegToRad(DivePitch) * vehicle.BrakeAmount;

        float squat = Mathf.DegToRad(SquatPitch) * vehicle.ThrottleAmount;
        if (vehicle.IsBoosting)
            squat += Mathf.DegToRad(BoostPitch) * vehicle.NitroFraction;

        return squat;
    }
}
