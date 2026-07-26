// C# port of addons/gevp/scripts/wheel.gd from Godot-Easy-Vehicle-Physics.
//
//   https://github.com/DAShoe1/Godot-Easy-Vehicle-Physics
//   Copyright (c) 2024 David Shoemaker
//   Portions are Copyright (c) 2021 Dechode      https://github.com/Dechode/Godot-Advanced-Vehicle
//   Portions are Copyright (c) 2024 Baron Wittman
//     https://lupine-vidya.itch.io/gdsim/devlog/677572/series-driving-simulator-workshop-mirror
//
// MIT licensed — see assets/gevp/LICENSE. The maths below is a faithful translation of the
// original GDScript; see docs/vehicle-physics.md for the deliberate deviations.

using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// One ray-cast wheel. The ray points straight down from the top of the suspension travel
/// and reaches <c>SpringLength + TireRadius</c>; where it hits decides both the suspension
/// compression and the tire's contact patch. All tire and suspension forces are applied to
/// the parent <see cref="Vehicle"/> body from here.
///
/// Most of the fields on this class are *not* exported: the parent <see cref="Vehicle"/>
/// owns the tuning (per axle) and pushes it down into its wheels in
/// <see cref="Vehicle.Initialize"/>. That keeps every knob in one inspector.
/// </summary>
[GlobalClass]
public partial class Wheel : RayCast3D
{
    /// <summary>
    /// The <see cref="Node3D"/> holding this wheel's visual mesh. It gets moved down the
    /// suspension travel and spun about its X axis.
    ///
    /// The mesh must face <b>+Z</b> (Godot's forward). If the mesh's own pivot makes that
    /// awkward, parent it to a plain Node3D and point this at the Node3D instead.
    /// </summary>
    [Export] public Node3D? WheelNode { get; set; }

    // ---- Pushed in by the parent Vehicle (per axle) ----

    public float WheelMass = 15.0f;
    public float TireRadius = 0.3f;
    public float TireWidth = 205.0f;
    public float Ackermann = 0.15f;
    public float ContactPatch = 0.2f;
    public float BrakingGripMultiplier = 1.4f;
    public float HandbrakeLockedGrip = 0.15f;
    public float HandbrakeLockSlip = 0.7f;
    public string SurfaceType = "";

    public Godot.Collections.Dictionary<string, float> TireStiffnesses = new()
        { { "Road", 5.0f }, { "Dirt", 0.5f }, { "Grass", 0.5f } };
    public Godot.Collections.Dictionary<string, float> CoefficientOfFriction = new()
        { { "Road", 2.0f }, { "Dirt", 1.4f }, { "Grass", 1.0f } };
    public Godot.Collections.Dictionary<string, float> RollingResistance = new()
        { { "Road", 1.0f }, { "Dirt", 2.0f }, { "Grass", 4.0f } };
    public Godot.Collections.Dictionary<string, float> LateralGripAssist = new()
        { { "Road", 0.05f }, { "Dirt", 0.0f }, { "Grass", 0.0f } };
    public Godot.Collections.Dictionary<string, float> LongitudinalGripRatio = new()
        { { "Road", 0.5f }, { "Dirt", 0.5f }, { "Grass", 0.5f } };

    public float SpringLength = 0.15f;
    public float SpringRate;
    public float SlowBump;
    public float FastBump;
    public float SlowRebound;
    public float FastRebound;
    public float FastDampThreshold = 127.0f;
    public float Antiroll;
    public float Toe;
    public float BumpStopMultiplier = 1.0f;
    public float WheelToBodyTorqueMultiplier;
    public float MassOverWheel;

    // ---- Runtime state (read by the vehicle, the debug overlay and the smoke effect) ----

    public float WheelMoment;
    public float Spin;
    public float SpinVelocityDiff;
    public float SpringForce;
    public float AppliedTorque;
    public Vector3 LocalVelocity = Vector3.Zero;
    public Vector3 PreviousVelocity = Vector3.Zero;
    public Vector3 PreviousGlobalPosition = Vector3.Zero;

    /// <summary>Tire force in the contact plane: X = lateral, Y = longitudinal.</summary>
    public Vector2 ForceVector = Vector2.Zero;

    /// <summary>Tire slip in the contact plane: X = slip angle (rad), Y = longitudinal slip ratio.</summary>
    public Vector2 SlipVector = Vector2.Zero;

    public float PreviousCompression;
    public float SpringCurrentLength;
    public float MaxSpringLength;
    public float AntirollForce;
    public float DampingForce;
    public float SteeringRatio;
    public GodotObject? LastCollider;
    public Vector3 LastCollisionPoint = Vector3.Zero;
    public Vector3 LastCollisionNormal = Vector3.Zero;
    public float CurrentCof;
    public float CurrentRollingResistance;
    public float CurrentLateralGripAssist;
    public float CurrentLongitudinalGripRatio;
    public float CurrentTireStiffness;

    /// <summary>
    /// How hard the handbrake is being pulled on this wheel, 0..1. Set every physics step by
    /// the parent vehicle as it drives the axles, and only ever non-zero on the handbrake axle.
    /// </summary>
    public float HandbrakeAmount;

    /// <summary>How locked this tire currently is for grip purposes, 0..1. Drives the slide.</summary>
    public float HandbrakeLockFactor;

    public float AbsEnableTime;
    public float AbsPulseTime = 0.3f;
    public float AbsSpinDifferenceThreshold = -12.0f;
    public bool LimitSpin;
    public bool IsDriven;
    public Wheel? OppositeWheel;

    /// <summary>+1 / -1 when this wheel is on a beam axle, 0 for independent suspension.</summary>
    public float BeamAxle;

    private Vehicle _vehicle = null!;

    /// <summary>Surface groups we've already warned about, so a bad group logs once, not every frame.</summary>
    private string _unknownSurfaceWarned = "";

    public override void _Process(double delta)
    {
        if (WheelNode == null)
            return;

        Vector3 nodePosition = WheelNode.Position;
        nodePosition.Y = Mathf.Min(0.0f, -SpringCurrentLength);
        WheelNode.Position = nodePosition;

        Vector3 nodeRotation = WheelNode.Rotation;

        if (!Mathf.IsZeroApprox(BeamAxle) && OppositeWheel?.WheelNode != null)
        {
            Vector3 wheelLookatVector = OppositeWheel.Transform * OppositeWheel.WheelNode.Position
                                        - Transform * nodePosition;
            nodeRotation.Z = wheelLookatVector.AngleTo(Vector3.Right * BeamAxle)
                             * VehicleMath.SignF(wheelLookatVector.Y * BeamAxle);
        }

        nodeRotation.X -= Mathf.Wrap(Spin * (float)delta, 0.0f, Mathf.Tau);
        WheelNode.Rotation = nodeRotation;
    }

    /// <summary>
    /// Called by the parent <see cref="Vehicle"/> once it has pushed the axle tuning in.
    /// Derives the wheel's moment of inertia, aims the ray cast and caches the surface
    /// coefficients for the starting surface.
    /// </summary>
    public void Initialize()
    {
        if (WheelNode == null)
        {
            GD.PushError($"[Wheel] {Name} has no WheelNode assigned; the wheel will not render or spin.");
            return;
        }

        if (GetParent() is not Vehicle parentVehicle)
        {
            GD.PushError($"[Wheel] {Name} must be a direct child of a Vehicle node.");
            return;
        }

        WheelNode.RotationOrder = EulerOrder.Zxy;
        WheelMoment = 0.5f * WheelMass * Mathf.Pow(TireRadius, 2.0f);
        TargetPosition = Vector3.Down * (SpringLength + TireRadius);
        _vehicle = parentVehicle;

        // The ray starts inside the chassis' own collision shape. Godot won't report a hit
        // from inside a shape, but that leaves the result on a knife edge whenever the ray
        // origin sits on the body's surface — so rule the car out explicitly.
        AddException(parentVehicle);

        // If a hard landing ever drives the ray origin below the surface, a ray that ignores
        // hits from inside reports nothing, the spring force drops to zero and the car settles
        // onto its chassis with no way back — the suspension can never see the ground again.
        // Detecting the hit instead reports zero distance, which bottoms the spring out and
        // lets the bump stop push the car back up.
        HitFromInside = true;

        MaxSpringLength = SpringLength;
        ApplySurface(SurfaceType);
    }

    /// <summary>Point the wheel at a steering input in the -1..1 range, with Ackermann + toe.</summary>
    public void Steer(float input, float maxSteeringAngle)
    {
        input *= SteeringRatio;
        Vector3 r = Rotation;
        r.Y = maxSteeringAngle * (input + (1.0f - Mathf.Cos(input * 0.5f * Mathf.Pi)) * Ackermann) + Toe;
        Rotation = r;
    }

    /// <summary>
    /// Spin the wheel for one physics step from the drivetrain torque, the brakes and the
    /// reaction torque the tire produced last step. Returns a measure of how much of the
    /// applied torque actually went into spinning this wheel, which the axle uses to
    /// split torque between its two wheels.
    /// </summary>
    public float ProcessTorque(float drive, float driveInertia, float brakeTorque, bool allowAbs, float delta)
    {
        // Start from the torque the tire fed back through the contact patch last frame.
        float netTorque = ForceVector.Y * TireRadius;
        float previousSpin = Spin;
        netTorque += drive;

        // While an ABS pulse is still running, this wheel gets no brake torque at all.
        if (AbsEnableTime > _vehicle.DeltaTime)
        {
            brakeTorque = 0.0f;
            allowAbs = false;
        }

        // Wheel is locking up relative to the road -> start an ABS pulse.
        if (Mathf.Abs(Spin) > 5.0f && SpinVelocityDiff < AbsSpinDifferenceThreshold)
        {
            if (allowAbs && brakeTorque > 0.0f)
            {
                brakeTorque = 0.0f;
                AbsEnableTime = _vehicle.DeltaTime + AbsPulseTime;
            }
        }

        // Remembered so the tire model can't generate more force than the motor/brakes put in.
        if (Mathf.IsZeroApprox(Spin))
            AppliedTorque = Mathf.Abs(drive - brakeTorque);
        else
            AppliedTorque = Mathf.Abs(drive - brakeTorque * VehicleMath.SignF(Spin));

        if (Mathf.Abs(Spin) < 5.0f && brakeTorque > Mathf.Abs(netTorque))
        {
            // Nearly stopped under braking: either pulse the ABS or just lock the wheel.
            if (allowAbs && Mathf.Abs(LocalVelocity.Z) > 2.0f)
                AbsEnableTime = _vehicle.DeltaTime + AbsPulseTime;
            else
                Spin = 0.0f;
        }
        else
        {
            netTorque -= brakeTorque * VehicleMath.SignF(Spin);
            float newSpin = Spin + netTorque / (WheelMoment + driveInertia) * delta;
            // Don't let the brakes spin the wheel backwards.
            if (VehicleMath.SignF(Spin) != VehicleMath.SignF(newSpin) && brakeTorque > Mathf.Abs(drive))
                newSpin = 0.0f;
            Spin = newSpin;
        }

        if (Mathf.IsZeroApprox(drive * delta))
            return 0.5f;

        return (Spin - previousSpin) * (WheelMoment + driveInertia) / (drive * delta);
    }

    /// <summary>
    /// Update the ray cast, work out the suspension and tire forces, and apply them to the
    /// vehicle body. Returns this wheel's spring compression in mm, which the axle feeds to
    /// the opposite wheel for the antiroll bar.
    /// </summary>
    public float ProcessForces(float oppositeCompression, bool braking, float delta)
    {
        ForceRaycastUpdate();
        PreviousVelocity = LocalVelocity;
        // GDScript's `vec * basis` is the inverse (world -> local) transform: the transpose.
        LocalVelocity = GlobalTransform.Basis.Transposed()
                        * ((GlobalPosition - PreviousGlobalPosition) / delta);
        PreviousGlobalPosition = GlobalPosition;

        // The surface under the wheel is identified by the collider's first node group.
        if (IsColliding())
        {
            LastCollider = GetCollider();
            LastCollisionPoint = GetCollisionPoint();
            LastCollisionNormal = GetCollisionNormal();
            if (LastCollider is Node colliderNode)
            {
                Godot.Collections.Array<StringName> surfaceGroups = colliderNode.GetGroups();
                if (surfaceGroups.Count > 0)
                {
                    string group = surfaceGroups[0].ToString();
                    if (SurfaceType != group)
                        ApplySurface(group);
                }
            }
        }
        else
        {
            LastCollider = null;
        }

        float compression = ProcessSuspension(oppositeCompression, delta);

        if (!IsColliding() || LastCollider == null)
        {
            ForceVector = Vector2.Zero;
            SlipVector = Vector2.Zero;
            Spin -= VehicleMath.SignF(Spin) * delta * 2.0f / WheelMoment;
            return 0.0f;
        }

        ProcessTires(braking, delta);

        Vector3 contact = LastCollisionPoint - _vehicle.GlobalPosition;
        if (SpringForce > 0.0f)
            _vehicle.ApplyForce(LastCollisionNormal * SpringForce, contact);
        else
            // No spring force (wheel off the ground): nudge it down so it settles.
            _vehicle.ApplyForce(-GlobalTransform.Basis.Y * _vehicle.Mass,
                                GlobalPosition - _vehicle.GlobalPosition);

        _vehicle.ApplyForce(GlobalTransform.Basis.X * ForceVector.X, contact);
        _vehicle.ApplyForce(GlobalTransform.Basis.Z * ForceVector.Y, contact);

        // A torque about the wheel, so the body still transfers weight when the centre of
        // gravity sits very low.
        if (braking)
            WheelToBodyTorqueMultiplier = 1.0f / (BrakingGripMultiplier + 1.0f);

        _vehicle.ApplyForce(-GlobalTransform.Basis.Y * ForceVector.Y * 0.5f * WheelToBodyTorqueMultiplier,
                            ToGlobal(Vector3.Forward * TireRadius));
        _vehicle.ApplyForce(GlobalTransform.Basis.Y * ForceVector.Y * 0.5f * WheelToBodyTorqueMultiplier,
                            ToGlobal(Vector3.Back * TireRadius));

        return compression;
    }

    private float ProcessSuspension(float oppositeCompression, float delta)
    {
        if (IsColliding() && LastCollider != null)
            SpringCurrentLength = LastCollisionPoint.DistanceTo(GlobalPosition) - TireRadius;
        else
            SpringCurrentLength = SpringLength;

        bool noContact = false;
        if (SpringCurrentLength > MaxSpringLength)
        {
            SpringCurrentLength = MaxSpringLength;
            noContact = true;
        }

        bool bottomOut = false;
        if (SpringCurrentLength < 0.0f)
        {
            SpringCurrentLength = 0.0f;
            bottomOut = true;
        }

        // Compression is carried in millimetres, which is what the spring rates assume.
        float compression = (SpringLength - SpringCurrentLength) * 1000.0f;

        float springSpeedMmPerSecond = (compression - PreviousCompression) / delta;
        PreviousCompression = compression;

        SpringForce = compression * SpringRate;
        AntirollForce = Antiroll * (compression - oppositeCompression);
        SpringForce += AntirollForce;

        // Bottomed out: add a bump stop so the body doesn't punch through the surface.
        float bottomOutDamping = 0.0f;
        float bottomOutDampingFast = 0.0f;
        float bottomOutForce = 0.0f;
        if (bottomOut)
        {
            float gravityOnSpring = Mathf.Clamp(
                GlobalTransform.Basis.Y.Dot(-_vehicle.CurrentGravity.Normalized()), 0.0f, 1.0f);
            bottomOutForce = (MassOverWheel * Mathf.Clamp(springSpeedMmPerSecond * 0.001f, 0.0f, 5.0f) / delta
                              + MassOverWheel * _vehicle.CurrentGravity.Length() * gravityOnSpring)
                             * BumpStopMultiplier;
            bottomOutDamping = -SlowBump;
            bottomOutDampingFast = -FastBump;
        }

        // Bump (compressing) and rebound (extending) get separate rates, each split into a
        // slow and a fast region either side of FastDampThreshold.
        if (springSpeedMmPerSecond >= 0.0f)
        {
            if (springSpeedMmPerSecond > FastDampThreshold)
                DampingForce = (springSpeedMmPerSecond - FastDampThreshold) * (FastBump + bottomOutDampingFast)
                               + FastDampThreshold * (SlowBump + bottomOutDamping);
            else
                DampingForce = springSpeedMmPerSecond * (SlowBump + bottomOutDamping);
        }
        else
        {
            if (springSpeedMmPerSecond < -FastDampThreshold)
                DampingForce = (springSpeedMmPerSecond + FastDampThreshold) * FastRebound
                               + -FastDampThreshold * SlowRebound;
            else
                DampingForce = springSpeedMmPerSecond * SlowRebound;
        }

        SpringForce += DampingForce;
        SpringForce = Mathf.Max(0.0f, SpringForce + bottomOutForce);

        // Limits how fast the wheel can extend back down, so it doesn't snap out in one step.
        MaxSpringLength = Mathf.Clamp(
            (SpringForce / WheelMass - springSpeedMmPerSecond) * delta * 0.001f + SpringCurrentLength,
            0.0f, SpringLength);

        if (noContact)
            SpringForce = 0.0f;

        return compression;
    }

    /// <summary>
    /// A brush tire model with the friction fall-off past peak grip removed, so the tire
    /// keeps its grip instead of letting go all at once.
    /// </summary>
    private void ProcessTires(bool braking, float delta)
    {
        Vector2 localPlanar = new Vector2(LocalVelocity.X, LocalVelocity.Z).Normalized()
                              * Mathf.Clamp(LocalVelocity.Length(), 0.0f, 1.0f);
        SlipVector.X = Mathf.Asin(Mathf.Clamp(-localPlanar.X, -1.0f, 1.0f));
        SlipVector.Y = 0.0f;

        float wheelVelocity = Spin * TireRadius;
        SpinVelocityDiff = wheelVelocity + LocalVelocity.Z;
        float neededRollingForce = SpinVelocityDiff * WheelMoment / TireRadius / delta;

        // Cap the force by whatever the motor/brakes actually applied, so slip alone can
        // never conjure more force than the drivetrain put in.
        float maxYForce = Mathf.Abs(AppliedTorque) > Mathf.Abs(neededRollingForce)
            ? Mathf.Abs(AppliedTorque / TireRadius)
            : Mathf.Abs(neededRollingForce / TireRadius);

        float maxXForce = Mathf.Abs(MassOverWheel * LocalVelocity.X) / delta;

        float zSign = VehicleMath.SignF(-LocalVelocity.Z);
        if (LocalVelocity.Z == 0.0f)
            zSign = 1.0f;

        SlipVector.Y = (Mathf.Abs(LocalVelocity.Z) - wheelVelocity * zSign)
                       / (1.0f + Mathf.Abs(LocalVelocity.Z));

        if (SlipVector.IsZeroApprox())
            SlipVector = new Vector2(0.0001f, 0.0001f);

        float corneringStiffness = 0.5f * CurrentTireStiffness * Mathf.Pow(ContactPatch, 2.0f);
        float friction = CurrentCof * SpringForce - SpringForce / (TireWidth * ContactPatch * 0.2f);
        float deflect = 1.0f / Mathf.Sqrt(Mathf.Pow(corneringStiffness * SlipVector.Y, 2.0f)
                                          + Mathf.Pow(corneringStiffness * SlipVector.X, 2.0f));

        // How locked the handbrake has this tire, faded in with the slip it actually produced
        // rather than with the button. A stationary car keeps its grip however hard the lever
        // is pulled, because a wheel that isn't turning at a standstill isn't sliding.
        HandbrakeLockFactor = HandbrakeAmount > 0.0f
            ? HandbrakeAmount * Mathf.Clamp(SlipVector.Y / Mathf.Max(HandbrakeLockSlip, 0.001f), 0.0f, 1.0f)
            : 0.0f;

        // The braking grip bonus is an assist for hauling the car down in a straight line. A
        // wheel the handbrake has locked should be giving grip up, not being handed more.
        float brakingHelp = 1.0f;
        if (SlipVector.Y > 0.3f && braking)
            brakingHelp = 1.0f + BrakingGripMultiplier * Mathf.Clamp(Mathf.Abs(SlipVector.Y), 0.0f, 1.0f)
                                 * (1.0f - HandbrakeLockFactor);

        float critLength = friction * (1.0f - SlipVector.Y) * ContactPatch * (0.5f * deflect);
        if (critLength >= ContactPatch)
        {
            // Still in the linear part of the curve.
            ForceVector.Y = corneringStiffness * SlipVector.Y / (1.0f - SlipVector.Y);
            ForceVector.X = corneringStiffness * SlipVector.X / (1.0f - SlipVector.Y);
        }
        else
        {
            float brushx = (1.0f - friction * (1.0f - SlipVector.Y) * (0.25f * deflect)) * deflect;
            ForceVector.Y = friction * CurrentLongitudinalGripRatio * corneringStiffness * SlipVector.Y
                            * brushx * brakingHelp * zSign;
            ForceVector.X = friction * corneringStiffness * SlipVector.X * brushx
                            * (Mathf.Abs(SlipVector.X * CurrentLateralGripAssist) + 1.0f);
        }

        // The friction budget the two axes are supposed to share. This model has the past-peak
        // fall-off taken out so the tire never lets go on its own, which is what makes the car
        // forgiving everywhere else — but it also means a locked rear tire keeps enough
        // cornering stiffness to hold the back end in line. Under the handbrake, and only
        // there, put the coupling back: the lateral force decays towards HandbrakeLockedGrip as
        // the tire locks, and the rear axle stops being able to resist the yaw.
        if (HandbrakeLockFactor > 0.0f)
            ForceVector.X *= Mathf.Lerp(1.0f, HandbrakeLockedGrip, HandbrakeLockFactor);

        if (Mathf.Abs(ForceVector.Y) > Mathf.Abs(maxYForce))
        {
            ForceVector.Y = maxYForce * VehicleMath.SignF(ForceVector.Y);
            LimitSpin = true;
        }
        else
        {
            LimitSpin = false;
        }

        if (Mathf.Abs(ForceVector.X) > maxXForce)
            ForceVector.X = maxXForce * VehicleMath.SignF(ForceVector.X);

        ForceVector.Y -= ProcessRollingResistance() * VehicleMath.SignF(LocalVelocity.Z);
    }

    private float ProcessRollingResistance()
    {
        float coefficient = 0.005f + 0.5f * (0.01f + 0.0095f * Mathf.Pow(LocalVelocity.Z * 0.036f, 2.0f));
        return coefficient * SpringForce * CurrentRollingResistance;
    }

    public float GetReactionTorque() => ForceVector.Y * TireRadius;

    public float GetFriction(float normalForce, string surface)
    {
        float surfaceCof = CoefficientOfFriction.TryGetValue(surface, out float cof) ? cof : 1.0f;
        return surfaceCof * normalForce - normalForce / (TireWidth * ContactPatch * 0.2f);
    }

    /// <summary>
    /// Cache the friction/grip numbers for a surface. Unlike the original, an unrecognised
    /// group leaves the previous surface in place and warns once instead of throwing — a
    /// stray group on a collider shouldn't take the whole car out.
    /// </summary>
    private void ApplySurface(string surface)
    {
        if (!CoefficientOfFriction.ContainsKey(surface)
            || !RollingResistance.ContainsKey(surface)
            || !LateralGripAssist.ContainsKey(surface)
            || !LongitudinalGripRatio.ContainsKey(surface)
            || !TireStiffnesses.ContainsKey(surface))
        {
            if (_unknownSurfaceWarned != surface)
            {
                _unknownSurfaceWarned = surface;
                GD.PushWarning($"[Wheel] {Name}: no tire data for surface group '{surface}'. " +
                               $"Keeping '{SurfaceType}'. Add it to the Vehicle's tire dictionaries, " +
                               "or make sure the collider's first group is a surface name.");
            }
            return;
        }

        SurfaceType = surface;
        CurrentCof = CoefficientOfFriction[surface];
        CurrentRollingResistance = RollingResistance[surface];
        CurrentLateralGripAssist = LateralGripAssist[surface];
        CurrentLongitudinalGripRatio = LongitudinalGripRatio[surface];
        CurrentTireStiffness = 1000000.0f + 8000000.0f * TireStiffnesses[surface];
    }
}
