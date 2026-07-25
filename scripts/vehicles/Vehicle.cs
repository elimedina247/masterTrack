// C# port of addons/gevp/scripts/vehicle.gd from Godot-Easy-Vehicle-Physics.
//
//   https://github.com/DAShoe1/Godot-Easy-Vehicle-Physics
//   Copyright (c) 2024 David Shoemaker
//   Portions are Copyright (c) 2021 Dechode  https://github.com/Dechode/Godot-Advanced-Vehicle
//
// MIT licensed — see assets/gevp/LICENSE. The maths below is a faithful translation of the
// original GDScript; see docs/vehicle-physics.md for the deliberate deviations.

using System.Collections.Generic;
using System.Linq;
using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// A ray-cast rigid-body vehicle: suspension, brush-model tires, a motor with a torque
/// curve, a clutch, a gearbox and a pile of driver assists. The body is a plain
/// <see cref="RigidBody3D"/> — all the forces come from the four <see cref="Wheel"/>
/// ray casts, which must be direct children of this node.
///
/// Drive it by writing to <see cref="ThrottleInput"/>, <see cref="SteeringInput"/>,
/// <see cref="BrakeInput"/>, <see cref="HandbrakeInput"/> and <see cref="ClutchInput"/>
/// each physics step — see <see cref="VehicleInput"/> for the keyboard/pad sampler.
///
/// Surfaces are identified by the *first node group* on whatever a wheel's ray cast hits,
/// looked up in the tire dictionaries below. Road bodies therefore need to be in a "Road"
/// group (see <see cref="SurfaceGroups"/>).
/// </summary>
[GlobalClass]
public partial class Vehicle : RigidBody3D
{
    // ---------------------------------------------------------------- Wheel nodes

    [ExportGroup("Wheel Nodes")]
    [Export] public Wheel? FrontLeftWheel { get; set; }
    [Export] public Wheel? FrontRightWheel { get; set; }
    [Export] public Wheel? RearLeftWheel { get; set; }
    [Export] public Wheel? RearRightWheel { get; set; }

    // ---------------------------------------------------------------- Steering

    /// <summary>How fast steering input ramps toward the stick/key position, per second.</summary>
    [ExportGroup("Steering")]
    [Export] public float SteeringSpeed { get; set; } = 4.25f;

    /// <summary>How fast steering returns toward centre, per second.</summary>
    [Export] public float CountersteerSpeed { get; set; } = 11.0f;

    /// <summary>Steering speed is divided by velocity times this; bigger = slower steering at speed.</summary>
    [Export] public float SteeringSpeedDecay { get; set; } = 0.20f;

    /// <summary>Further steering input is blocked once front lateral slip passes this.</summary>
    [Export] public float SteeringSlipAssist { get; set; } = 0.15f;

    /// <summary>How hard steering is nudged toward the direction of travel.</summary>
    [Export] public float CountersteerAssist { get; set; } = 0.9f;

    /// <summary>Steering input is raised to this power, which softens it near full lock.</summary>
    [Export] public float SteeringExponent { get; set; } = 1.5f;

    /// <summary>Maximum steering angle. Shown in the inspector in degrees.</summary>
    [Export(PropertyHint.Range, "0,360,0.1,radians_as_degrees")]
    public float MaxSteeringAngle { get; set; } = Mathf.DegToRad(40.0f);

    /// <summary>How much the front wheels turn per unit of steering input.</summary>
    [ExportSubgroup("Front Axle")]
    [Export] public float FrontSteeringRatio { get; set; } = 1.0f;

    /// <summary>How much the rear wheels turn per unit of steering input (rear-steer).</summary>
    [ExportSubgroup("Rear Axle")]
    [Export] public float RearSteeringRatio { get; set; }

    // ---------------------------------------------------------------- Throttle and braking

    /// <summary>How fast throttle input ramps, per second.</summary>
    [ExportGroup("Throttle and Braking")]
    [Export] public float ThrottleSpeed { get; set; } = 20.0f;

    /// <summary>
    /// Scales throttle ramp speed by steering input.
    /// <b>Inert:</b> exposed upstream but never read by the physics. Left in for parity.
    /// </summary>
    [Export] public float ThrottleSteeringAdjust { get; set; } = 0.1f;

    /// <summary>How fast brake input ramps, per second.</summary>
    [Export] public float BrakingSpeed { get; set; } = 10.0f;

    /// <summary>
    /// Scales the brake force derived from tire friction. Upstream declares this but never
    /// applies it; here it does multiply the calculated force, so the default of 1.0 behaves
    /// exactly like the original and anything else is a real adjustment.
    /// </summary>
    [Export] public float BrakeForceMultiplier { get; set; } = 1.0f;

    /// <summary>Front:rear brake split. Negative means "work it out from the spring rates".</summary>
    [Export] public float FrontBrakeBias { get; set; } = -1.0f;

    /// <summary>Traction control ceiling on longitudinal slip. Negative disables it.</summary>
    [Export] public float TractionControlMaxSlip { get; set; } = 8.0f;

    /// <summary>How long the front ABS holds the brake off, in seconds.</summary>
    [ExportSubgroup("Front Axle")]
    [Export] public float FrontAbsPulseTime { get; set; } = 0.03f;

    /// <summary>Wheel-vs-road speed difference that trips the front ABS.</summary>
    [Export] public float FrontAbsSpinDifferenceThreshold { get; set; } = 12.0f;

    /// <summary>How long the rear ABS holds the brake off, in seconds.</summary>
    [ExportSubgroup("Rear Axle")]
    [Export] public float RearAbsPulseTime { get; set; } = 0.03f;

    /// <summary>Wheel-vs-road speed difference that trips the rear ABS.</summary>
    [Export] public float RearAbsSpinDifferenceThreshold { get; set; } = 12.0f;

    // ---------------------------------------------------------------- Stability

    /// <summary>Torque the body back in line when it yaws away from its direction of travel.</summary>
    [ExportGroup("Stability")]
    [Export] public bool EnableStability { get; set; } = true;

    /// <summary>Yaw angle before stability kicks in: 0 = straight ahead, 1 = 90 degrees.</summary>
    [Export] public float StabilityYawEngageAngle { get; set; }

    [Export] public float StabilityYawStrength { get; set; } = 6.0f;

    /// <summary>Extra yaw strength while grounded, to overcome tire grip.</summary>
    [Export] public float StabilityYawGroundMultiplier { get; set; } = 2.0f;

    /// <summary>Torque keeping the car upright while airborne.</summary>
    [Export] public float StabilityUprightSpring { get; set; } = 1.0f;

    /// <summary>Damping on rotation while airborne.</summary>
    [Export] public float StabilityUprightDamping { get; set; } = 1000.0f;

    // ---------------------------------------------------------------- Motor

    /// <summary>Peak motor torque in Nm.</summary>
    [ExportGroup("Motor")]
    [Export] public float MaxTorque { get; set; } = 300.0f;

    [Export] public float MaxRpm { get; set; } = 7000.0f;
    [Export] public float IdleRpm { get; set; } = 1000.0f;

    /// <summary>Fraction of <see cref="MaxTorque"/> produced across the RPM range (X = rpm/max).</summary>
    [Export] public Curve? TorqueCurve { get; set; }

    /// <summary>Motor drag that scales with RPM.</summary>
    [Export] public float MotorDrag { get; set; } = 0.005f;

    /// <summary>
    /// Constant motor drag.
    /// <b>Inert:</b> exposed upstream but never read by the physics — only the RPM-scaled
    /// <see cref="MotorDrag"/> is applied. Left in for parity.
    /// </summary>
    [Export] public float MotorBrake { get; set; } = 10.0f;

    [Export] public float MotorMoment { get; set; } = 0.5f;

    /// <summary>RPM the motor holds while launching from a stop.</summary>
    [Export] public float ClutchOutRpm { get; set; } = 3000.0f;

    /// <summary>Peak clutch torque as a ratio of <see cref="MaxTorque"/>.</summary>
    [Export] public float MaxClutchTorqueRatio { get; set; } = 1.6f;

    // ---------------------------------------------------------------- Gearbox

    /// <summary>Forward gear ratios; the array length is the number of gears.</summary>
    [ExportGroup("Gearbox")]
    [Export] public float[] GearRatios { get; set; } = { 3.8f, 2.3f, 1.7f, 1.3f, 1.0f, 0.8f };

    [Export] public float FinalDrive { get; set; } = 3.2f;
    [Export] public float ReverseRatio { get; set; } = 3.3f;

    /// <summary>Seconds an upshift takes.</summary>
    [Export] public float ShiftTime { get; set; } = 0.3f;

    [Export] public bool AutomaticTransmission { get; set; } = true;

    /// <summary>
    /// Minimum gap between automatic shifts, in milliseconds.
    /// <b>Inert:</b> exposed upstream but never read — the automatic gearbox spaces shifts by
    /// <see cref="ShiftTime"/> instead. Left in for parity.
    /// </summary>
    [Export] public float AutomaticTimeBetweenShifts { get; set; } = 1000.0f;

    [Export] public float GearInertia { get; set; } = 0.02f;

    // ---------------------------------------------------------------- Drivetrain

    /// <summary>Torque to the front wheels: 1 = FWD, 0 = RWD, in between = AWD.</summary>
    [ExportGroup("Drivetrain")]
    [Export] public float FrontTorqueSplit { get; set; }

    /// <summary>Shift the torque split around based on wheel slip.</summary>
    [Export] public bool VariableTorqueSplit { get; set; }

    /// <summary>Split to blend toward when a wheel slips (needs <see cref="VariableTorqueSplit"/>).</summary>
    [Export] public float FrontVariableSplit { get; set; }

    /// <summary>Seconds to blend between torque splits.</summary>
    [Export] public float VariableSplitSpeed { get; set; } = 1.0f;

    /// <summary>Torque at which the front differential locks. Negative disables it.</summary>
    [ExportSubgroup("Front Axle")]
    [Export] public float FrontLockingDifferentialEngageTorque { get; set; } = 200.0f;

    /// <summary>Torque vectoring on the front axle: 1.0 sends everything to the outside wheel.</summary>
    [Export] public float FrontTorqueVectoring { get; set; }

    /// <summary>Torque at which the rear differential locks. Negative disables it.</summary>
    [ExportSubgroup("Rear Axle")]
    [Export] public float RearLockingDifferentialEngageTorque { get; set; } = 200.0f;

    /// <summary>Torque vectoring on the rear axle: 1.0 sends everything to the outside wheel.</summary>
    [Export] public float RearTorqueVectoring { get; set; }

    // ---------------------------------------------------------------- Suspension

    /// <summary>Vehicle mass in kg. Drives the calculated spring and brake rates.</summary>
    [ExportGroup("Suspension")]
    [Export] public float VehicleMass { get; set; } = 1500.0f;

    /// <summary>Fraction of the mass over the front axle.</summary>
    [Export] public float FrontWeightDistribution { get; set; } = 0.5f;

    /// <summary>Raises/lowers the centre of gravity from the wheel ray-cast plane.</summary>
    [Export] public float CenterOfGravityHeightOffset { get; set; } = -0.2f;

    /// <summary>Scales the body's calculated inertia; higher resists rotation more.</summary>
    [Export] public float InertiaMultiplier { get; set; } = 1.2f;

    /// <summary>Front suspension travel in metres.</summary>
    [ExportSubgroup("Front Axle")]
    [Export] public float FrontSpringLength { get; set; } = 0.15f;

    /// <summary>How compressed the front spring sits at rest; 0 = fully compressed.</summary>
    [Export] public float FrontRestingRatio { get; set; } = 0.5f;

    /// <summary>Front damping ratio; 1 = critically damped. Road car ~0.3, race car ~0.9.</summary>
    [Export] public float FrontDampingRatio { get; set; } = 0.4f;

    [Export] public float FrontBumpDampMultiplier { get; set; } = 0.6667f;
    [Export] public float FrontReboundDampMultiplier { get; set; } = 1.5f;

    /// <summary>Front antiroll bar stiffness as a ratio of spring stiffness.</summary>
    [Export] public float FrontArbRatio { get; set; } = 0.25f;

    /// <summary>Front camber in radians. Not simulated; a slight angle helps stability.</summary>
    [Export] public float FrontCamber { get; set; } = 0.01745329f;

    /// <summary>Front toe in radians.</summary>
    [Export] public float FrontToe { get; set; } = 0.01f;

    /// <summary>Front bump-stop force multiplier. Lower it if the car bounces off big bumps.</summary>
    [Export] public float FrontBumpStopMultiplier { get; set; } = 1.0f;

    /// <summary>Align the front wheels as a beam axle. Cosmetic only.</summary>
    [Export] public bool FrontBeamAxle { get; set; }

    /// <summary>Rear suspension travel in metres; usually a little more than the front.</summary>
    [ExportSubgroup("Rear Axle")]
    [Export] public float RearSpringLength { get; set; } = 0.2f;

    /// <summary>How compressed the rear spring sits at rest; 0 = fully compressed.</summary>
    [Export] public float RearRestingRatio { get; set; } = 0.5f;

    /// <summary>Rear damping ratio; 1 = critically damped.</summary>
    [Export] public float RearDampingRatio { get; set; } = 0.4f;

    [Export] public float RearBumpDampMultiplier { get; set; } = 0.6667f;
    [Export] public float RearReboundDampMultiplier { get; set; } = 1.5f;

    /// <summary>Rear antiroll bar stiffness as a ratio of spring stiffness.</summary>
    [Export] public float RearArbRatio { get; set; } = 0.25f;

    /// <summary>Rear camber in radians.</summary>
    [Export] public float RearCamber { get; set; } = 0.01745329f;

    /// <summary>Rear toe in radians.</summary>
    [Export] public float RearToe { get; set; } = 0.01f;

    /// <summary>Rear bump-stop force multiplier.</summary>
    [Export] public float RearBumpStopMultiplier { get; set; } = 1.0f;

    /// <summary>Align the rear wheels as a beam axle. Cosmetic only.</summary>
    [Export] public bool RearBeamAxle { get; set; }

    // ---------------------------------------------------------------- Tires

    /// <summary>Length of the tire contact patch in the brush model.</summary>
    [ExportGroup("Tires")]
    [Export] public float ContactPatch { get; set; } = 0.2f;

    /// <summary>Extra longitudinal grip under braking.</summary>
    [Export] public float BrakingGripMultiplier { get; set; } = 1.5f;

    /// <summary>How much tire force also acts as a torque about the wheel on the body.</summary>
    [Export] public float WheelToBodyTorqueMultiplier { get; set; } = 1.0f;

    /// <summary>Tire stiffness per surface group; higher = more responsive.</summary>
    [Export] public Godot.Collections.Dictionary<string, float> TireStiffnesses { get; set; } = new()
    {
        { SurfaceGroups.Road, 10.0f }, { SurfaceGroups.Dirt, 0.5f },
        { SurfaceGroups.Grass, 0.5f }, { SurfaceGroups.Ice, 0.3f },
    };

    /// <summary>Grip multiplier per surface group.</summary>
    [Export] public Godot.Collections.Dictionary<string, float> CoefficientOfFriction { get; set; } = new()
    {
        { SurfaceGroups.Road, 3.0f }, { SurfaceGroups.Dirt, 2.4f },
        { SurfaceGroups.Grass, 2.0f }, { SurfaceGroups.Ice, 0.6f },
    };

    /// <summary>Rolling resistance multiplier per surface group.</summary>
    [Export] public Godot.Collections.Dictionary<string, float> RollingResistance { get; set; } = new()
    {
        { SurfaceGroups.Road, 1.0f }, { SurfaceGroups.Dirt, 2.0f },
        { SurfaceGroups.Grass, 4.0f }, { SurfaceGroups.Ice, 0.8f },
    };

    /// <summary>Bonus grip proportional to lateral slip. Stops long slides; can feel unrealistic.</summary>
    [Export] public Godot.Collections.Dictionary<string, float> LateralGripAssist { get; set; } = new()
    {
        { SurfaceGroups.Road, 0.05f }, { SurfaceGroups.Dirt, 0.0f },
        { SurfaceGroups.Grass, 0.0f }, { SurfaceGroups.Ice, 0.0f },
    };

    /// <summary>Longitudinal grip as a ratio of lateral grip; lets a car spin up without losing cornering.</summary>
    [Export] public Godot.Collections.Dictionary<string, float> LongitudinalGripRatio { get; set; } = new()
    {
        { SurfaceGroups.Road, 0.5f }, { SurfaceGroups.Dirt, 0.5f },
        { SurfaceGroups.Grass, 0.5f }, { SurfaceGroups.Ice, 0.5f },
    };

    [ExportSubgroup("Front Axle")]
    [Export] public float FrontTireRadius { get; set; } = 0.3f;

    /// <summary>Front tire width in mm. Doesn't set grip directly; it softens load sensitivity.</summary>
    [Export] public float FrontTireWidth { get; set; } = 245.0f;

    [Export] public float FrontWheelMass { get; set; } = 15.0f;

    [ExportSubgroup("Rear Axle")]
    [Export] public float RearTireRadius { get; set; } = 0.3f;

    /// <summary>Rear tire width in mm.</summary>
    [Export] public float RearTireWidth { get; set; } = 245.0f;

    [Export] public float RearWheelMass { get; set; } = 15.0f;

    // ---------------------------------------------------------------- Aerodynamics

    /// <summary>Drag coefficient. Most cars are around 0.40; a slippery shape can reach 0.05.</summary>
    [ExportGroup("Aerodynamics")]
    [Export] public float CoefficientOfDrag { get; set; } = 0.3f;

    /// <summary>Air density in kg/m³.</summary>
    [Export] public float AirDensity { get; set; } = 1.225f;

    /// <summary>Frontal area in m². A rough estimate is fine.</summary>
    [Export] public float FrontalArea { get; set; } = 2.0f;

    // ---------------------------------------------------------------- Runtime state

    private const float AngularVelocityToRpm = 60.0f / Mathf.Tau;

    public readonly List<Wheel> WheelArray = new();
    public readonly List<Axle> Axles = new();
    public Axle FrontAxle { get; private set; } = null!;
    public Axle RearAxle { get; private set; } = null!;
    public readonly List<Wheel> DriveWheels = new();

    // ---- Controller inputs: an external script writes these every physics step ----

    /// <summary>0..1 throttle.</summary>
    public float ThrottleInput;

    /// <summary>-1..1; positive steers left, matching the source project.</summary>
    public float SteeringInput;

    /// <summary>0..1 brake.</summary>
    public float BrakeInput;

    /// <summary>0..1 handbrake.</summary>
    public float HandbrakeInput;

    /// <summary>0..1 clutch; 1 is fully disengaged.</summary>
    public float ClutchInput;

    // ---- Derived state, safe to read for HUD / effects / debug ----

    public bool IsVehicleReady { get; private set; }
    public Vector3 LocalVelocity { get; private set; } = Vector3.Zero;

    /// <summary>Speed in m/s. Multiply by 3.6 for km/h.</summary>
    public float Speed { get; private set; }

    public float MotorRpm { get; private set; }
    public float SteeringAmount { get; private set; }

    /// <summary>Steering after the <see cref="SteeringExponent"/> curve, before assists.</summary>
    public float SteeringExponentAmount { get; private set; }

    /// <summary>Final steering angle in radians, after the exponent and the countersteer assist.</summary>
    public float TrueSteeringAmount { get; private set; }
    public float ThrottleAmount { get; private set; }
    public float BrakeAmount { get; private set; }
    public float ClutchAmount { get; private set; }

    /// <summary>0 = neutral, -1 = reverse, 1..n = forward gears.</summary>
    public int CurrentGear { get; private set; }

    public int RequestedGear { get; private set; }
    public float TorqueOutput { get; private set; }
    public float ClutchTorque { get; private set; }
    public float MaxClutchTorque { get; private set; }
    public float TrueTorqueSplit { get; private set; }
    public bool IsBraking { get; private set; }
    public bool MotorIsRedline { get; private set; }
    public bool IsShifting { get; private set; }
    public bool IsUpShifting { get; private set; }
    public bool TcsActive { get; private set; }
    public bool StabilityActive { get; private set; }
    public float StabilityYawTorque { get; private set; }
    public Vector3 StabilityTorqueVector { get; private set; } = Vector3.Zero;
    public Vector3 FrontAxlePosition { get; private set; } = Vector3.Zero;
    public Vector3 RearAxlePosition { get; private set; } = Vector3.Zero;

    /// <summary>Seconds of physics time since the vehicle came up. Used for ABS/shift timing.</summary>
    public float DeltaTime { get; private set; }

    /// <summary>Gravity as reported by the physics server, used by the bump stops.</summary>
    public Vector3 CurrentGravity { get; private set; } = Vector3.Zero;

    public Vector3 VehicleInertia { get; private set; } = Vector3.Zero;

    private Vector3 _previousGlobalPosition = Vector3.Zero;
    private float _driveAxlesInertia;
    private float _completeShiftDeltaTime;
    private float _lastShiftDeltaTime;
    private float _averageDriveWheelRadius;
    private float _currentTorqueSplit;
    private float _brakeForce;
    private float _maxBrakeForce;
    private float _handbrakeForce;
    private float _maxHandbrakeForce;
    private bool _needClutch;

    public override void _Ready()
    {
        Initialize();
    }

    public override void _IntegrateForces(PhysicsDirectBodyState3D state)
    {
        CurrentGravity = state.TotalGravity;
    }

    /// <summary>
    /// Build the axles, push the per-axle tuning down into the wheels, and derive the spring
    /// rates, damping, Ackermann, brake bias and centre of gravity from the setup. Safe to
    /// call again after changing the tuning at runtime.
    /// </summary>
    public void Initialize()
    {
        if (FrontLeftWheel is not { } frontLeft || FrontRightWheel is not { } frontRight
            || RearLeftWheel is not { } rearLeft || RearRightWheel is not { } rearRight)
        {
            GD.PushError($"[Vehicle] {Name}: all four wheel nodes must be assigned. " +
                         "The vehicle will not simulate.");
            return;
        }

        if (TireStiffnesses.Count == 0 || CoefficientOfFriction.Count == 0
            || RollingResistance.Count == 0 || LateralGripAssist.Count == 0
            || LongitudinalGripRatio.Count == 0)
        {
            GD.PushError($"[Vehicle] {Name}: every tire dictionary needs at least one surface type.");
            return;
        }

        if (TorqueCurve == null)
        {
            // The original hard-crashes here. A flat curve is a much friendlier default and
            // makes an unconfigured car obviously wrong rather than broken.
            GD.PushWarning($"[Vehicle] {Name}: no TorqueCurve assigned; falling back to a flat curve.");
            TorqueCurve = BuildFallbackTorqueCurve();
        }

        WheelArray.Clear();
        Axles.Clear();
        DriveWheels.Clear();
        _driveAxlesInertia = 0.0f;
        _averageDriveWheelRadius = 0.0f;

        string defaultSurface = TireStiffnesses.Keys.First();

        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        Mass = VehicleMass;
        Vector3 centerOfGravity = CalculateCenterOfGravity(
            FrontWeightDistribution, frontLeft, frontRight, rearLeft, rearRight);
        centerOfGravity.Y += CenterOfGravityHeightOffset;
        CenterOfMass = centerOfGravity;
        MaxClutchTorque = MaxTorque * MaxClutchTorqueRatio;

        FrontAxle = new Axle { TorqueVectoring = FrontTorqueVectoring };
        FrontAxle.Wheels.Add(frontLeft);
        FrontAxle.Wheels.Add(frontRight);
        frontLeft.OppositeWheel = frontRight;
        frontLeft.BeamAxle = FrontBeamAxle ? 1.0f : 0.0f;
        frontRight.OppositeWheel = frontLeft;
        frontRight.BeamAxle = FrontBeamAxle ? -1.0f : 0.0f;

        RearAxle = new Axle { TorqueVectoring = RearTorqueVectoring, Handbrake = true };
        RearAxle.Wheels.Add(rearLeft);
        RearAxle.Wheels.Add(rearRight);
        rearLeft.OppositeWheel = rearRight;
        rearLeft.BeamAxle = RearBeamAxle ? 1.0f : 0.0f;
        rearRight.OppositeWheel = rearLeft;
        rearRight.BeamAxle = RearBeamAxle ? -1.0f : 0.0f;

        Axles.Add(FrontAxle);
        Axles.Add(RearAxle);

        WheelArray.Add(frontLeft);
        WheelArray.Add(frontRight);
        WheelArray.Add(rearLeft);
        WheelArray.Add(rearRight);

        float maxTireRadius = Mathf.Max(FrontTireRadius, RearTireRadius);
        FrontAxle.TireSizeCorrection = maxTireRadius / FrontTireRadius;
        RearAxle.TireSizeCorrection = maxTireRadius / RearTireRadius;

        FrontAxle.DifferentialLockTorque = FrontLockingDifferentialEngageTorque;
        RearAxle.DifferentialLockTorque = RearLockingDifferentialEngageTorque;

        foreach (Wheel wheel in WheelArray)
        {
            wheel.SurfaceType = defaultSurface;
            wheel.TireStiffnesses = TireStiffnesses;
            wheel.ContactPatch = ContactPatch;
            wheel.BrakingGripMultiplier = BrakingGripMultiplier;
            wheel.CoefficientOfFriction = CoefficientOfFriction;
            wheel.RollingResistance = RollingResistance;
            wheel.LateralGripAssist = LateralGripAssist;
            wheel.LongitudinalGripRatio = LongitudinalGripRatio;
            wheel.WheelToBodyTorqueMultiplier = WheelToBodyTorqueMultiplier;
        }

        // 4.9 = half of g, i.e. the static load on one of the two wheels on the axle.
        float frontWeightPerWheel = VehicleMass * FrontWeightDistribution * 4.9f;
        float frontSpringRate = CalculateSpringRate(frontWeightPerWheel, FrontSpringLength, FrontRestingRatio);
        float frontDampingRate = CalculateDamping(frontWeightPerWheel, frontSpringRate, FrontDampingRatio);

        foreach (Wheel wheel in FrontAxle.Wheels)
        {
            wheel.WheelMass = FrontWheelMass;
            wheel.TireRadius = FrontTireRadius;
            wheel.TireWidth = FrontTireWidth;
            wheel.SteeringRatio = FrontSteeringRatio;
            wheel.SpringLength = FrontSpringLength;
            wheel.SpringRate = frontSpringRate;
            wheel.Antiroll = frontSpringRate * FrontArbRatio;
            wheel.SlowBump = frontDampingRate * FrontBumpDampMultiplier;
            wheel.SlowRebound = frontDampingRate * FrontReboundDampMultiplier;
            wheel.FastBump = frontDampingRate * FrontBumpDampMultiplier * 0.5f;
            wheel.FastRebound = frontDampingRate * FrontReboundDampMultiplier * 0.5f;
            wheel.BumpStopMultiplier = FrontBumpStopMultiplier;
            wheel.MassOverWheel = VehicleMass * FrontWeightDistribution * 0.5f;
            wheel.AbsPulseTime = FrontAbsPulseTime;
            wheel.AbsSpinDifferenceThreshold = -Mathf.Abs(FrontAbsSpinDifferenceThreshold);
        }

        float rearWeightPerWheel = VehicleMass * (1.0f - FrontWeightDistribution) * 4.9f;
        float rearSpringRate = CalculateSpringRate(rearWeightPerWheel, RearSpringLength, RearRestingRatio);
        float rearDampingRate = CalculateDamping(rearWeightPerWheel, rearSpringRate, RearDampingRatio);

        foreach (Wheel wheel in RearAxle.Wheels)
        {
            wheel.WheelMass = RearWheelMass;
            wheel.TireRadius = RearTireRadius;
            wheel.TireWidth = RearTireWidth;
            wheel.SteeringRatio = RearSteeringRatio;
            wheel.SpringLength = RearSpringLength;
            wheel.SpringRate = rearSpringRate;
            wheel.Antiroll = rearSpringRate * RearArbRatio;
            wheel.SlowBump = rearDampingRate * RearBumpDampMultiplier;
            wheel.SlowRebound = rearDampingRate * RearReboundDampMultiplier;
            wheel.FastBump = rearDampingRate * RearBumpDampMultiplier * 0.5f;
            wheel.FastRebound = rearDampingRate * RearReboundDampMultiplier * 0.5f;
            wheel.BumpStopMultiplier = RearBumpStopMultiplier;
            wheel.MassOverWheel = VehicleMass * (1.0f - FrontWeightDistribution) * 0.5f;
            wheel.AbsPulseTime = RearAbsPulseTime;
            wheel.AbsSpinDifferenceThreshold = -Mathf.Abs(RearAbsSpinDifferenceThreshold);
        }

        // Ackermann: the inside wheel has to turn tighter than the outside one.
        float wheelBase = rearLeft.Position.Z - frontLeft.Position.Z;
        float frontTrackWidth = frontRight.Position.X - frontLeft.Position.X;
        float frontAckermann = CalculateAckermann(wheelBase, frontTrackWidth);
        float rearTrackWidth = rearRight.Position.X - rearLeft.Position.X;
        float rearAckermann = CalculateAckermann(wheelBase, rearTrackWidth);

        ApplyWheelAlignment(frontLeft, frontAckermann, -FrontCamber, -FrontToe);
        ApplyWheelAlignment(frontRight, -frontAckermann, FrontCamber, FrontToe);
        ApplyWheelAlignment(rearLeft, rearAckermann, -RearCamber, -RearToe);
        ApplyWheelAlignment(rearRight, -rearAckermann, RearCamber, RearToe);

        if (FrontBrakeBias < 0.0f)
        {
            // Split the brakes the way the springs carry load under a 0.6/0.4 forward pitch.
            float frontAxleSpringForce = CalculateAxleSpringForce(0.6f, FrontSpringLength, frontSpringRate);
            float totalSpringForce = frontAxleSpringForce
                                     + CalculateAxleSpringForce(0.4f, RearSpringLength, rearSpringRate);
            FrontBrakeBias = frontAxleSpringForce / totalSpringForce;
        }

        FrontAxle.BrakeBias = FrontBrakeBias;
        RearAxle.BrakeBias = 1.0f - FrontBrakeBias;

        foreach (Wheel wheel in WheelArray)
            wheel.Initialize();

        if (FrontTorqueSplit > 0.0f || VariableTorqueSplit)
            FrontAxle.IsDriveAxle = true;
        if (FrontTorqueSplit < 1.0f || VariableTorqueSplit)
            RearAxle.IsDriveAxle = true;

        foreach (Axle axle in Axles)
        {
            axle.Inertia = 0.0f;
            foreach (Wheel wheel in axle.Wheels)
            {
                axle.Inertia += wheel.WheelMoment;
                if (!axle.IsDriveAxle)
                    continue;
                _driveAxlesInertia += wheel.WheelMoment;
                DriveWheels.Add(wheel);
                wheel.IsDriven = true;
                _averageDriveWheelRadius += wheel.TireRadius;
            }
        }

        if (DriveWheels.Count == 0)
        {
            GD.PushError($"[Vehicle] {Name}: no driven wheels. Check FrontTorqueSplit / VariableTorqueSplit.");
            return;
        }

        _averageDriveWheelRadius /= DriveWheels.Count;
        _previousGlobalPosition = GlobalPosition;

        CalculateBrakeForce();

        IsVehicleReady = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsVehicleReady)
            return;

        float dt = (float)delta;

        // The body's inertia isn't available on the first frame, and the stability assist
        // needs it, so grab it as soon as the physics server can tell us.
        if (VehicleInertia == Vector3.Zero)
        {
            PhysicsDirectBodyState3D? state = PhysicsServer3D.BodyGetDirectState(GetRid());
            if (state != null)
            {
                Vector3 rigidbodyInertia = VehicleMath.Inverse(state.InverseInertia);
                if (rigidbodyInertia.IsFinite())
                {
                    VehicleInertia = rigidbodyInertia * InertiaMultiplier;
                    Inertia = VehicleInertia;
                }
            }
        }

        DeltaTime += dt;
        // Velocity is measured from the actual movement rather than LinearVelocity so the
        // wheels and the body agree, then smoothed to take the edge off collision spikes.
        LocalVelocity = (GlobalTransform.Basis.Transposed()
                         * ((GlobalTransform.Origin - _previousGlobalPosition) / dt))
                        .Lerp(LocalVelocity, 0.5f);
        _previousGlobalPosition = GlobalPosition;
        Speed = LocalVelocity.Length();

        ProcessDrag();
        ProcessBraking(dt);
        ProcessSteering(dt);
        ProcessThrottle(dt);
        ProcessMotor(dt);
        ProcessClutch(dt);
        ProcessTransmission();
        ProcessDrive(dt);
        ProcessForces(dt);
        ProcessStability();
    }

    private void ProcessDrag()
    {
        float drag = 0.5f * AirDensity * Mathf.Pow(Speed, 2.0f) * FrontalArea * CoefficientOfDrag;
        if (drag > 0.0f)
            ApplyCentralForce(-LinearVelocity.Normalized() * drag);
    }

    private void ProcessBraking(float delta)
    {
        if (BrakeInput < BrakeAmount)
        {
            BrakeAmount -= BrakingSpeed * delta;
            if (BrakeInput > BrakeAmount)
                BrakeAmount = BrakeInput;
        }
        else if (BrakeInput > BrakeAmount)
        {
            BrakeAmount += BrakingSpeed * delta;
            if (BrakeInput < BrakeAmount)
                BrakeAmount = BrakeInput;
        }

        IsBraking = BrakeAmount > 0.0f;

        _brakeForce = BrakeAmount * _maxBrakeForce;
        _handbrakeForce = HandbrakeInput * _maxHandbrakeForce;
    }

    private void ProcessSteering(float delta)
    {
        bool steerAssistEngaged = false;
        float steeringSlip = GetMaxSteeringSlipAngle();

        // Slower steering the faster you go, scaled by the lock available.
        // At a standstill this divides by zero and goes infinite, which just means the
        // steering snaps to the input — same as the original.
        float steerSpeedCorrection = SteeringSpeed / (Speed * SteeringSpeedDecay) / MaxSteeringAngle;

        // Turning back the other way uses the (usually faster) countersteer rate.
        if (VehicleMath.SignF(SteeringInput) != VehicleMath.SignF(SteeringAmount))
            steerSpeedCorrection = CountersteerSpeed / (Speed * SteeringSpeedDecay);

        if (Mathf.Abs(steeringSlip) > SteeringSlipAssist)
            steerAssistEngaged = true;

        if (SteeringInput < SteeringAmount)
        {
            if (!steerAssistEngaged || steeringSlip < 0.0f)
            {
                SteeringAmount -= steerSpeedCorrection * delta;
                if (SteeringInput > SteeringAmount)
                    SteeringAmount = SteeringInput;
            }
            else
            {
                // Already sliding: unwind toward centre instead of adding more lock.
                SteeringAmount += steerSpeedCorrection * delta;
                if (SteeringAmount > 0.0f)
                    SteeringAmount = 0.0f;
            }
        }
        else if (SteeringInput > SteeringAmount)
        {
            if (!steerAssistEngaged || steeringSlip > 0.0f)
            {
                SteeringAmount += steerSpeedCorrection * delta;
                if (SteeringInput < SteeringAmount)
                    SteeringAmount = SteeringInput;
            }
            else
            {
                SteeringAmount -= steerSpeedCorrection * delta;
                if (SteeringAmount < 0.0f)
                    SteeringAmount = 0.0f;
            }
        }

        float steeringAdjust = Mathf.Pow(Mathf.Abs(SteeringAmount), SteeringExponent)
                               * VehicleMath.SignF(SteeringAmount);
        SteeringExponentAmount = steeringAdjust;

        // Countersteer assist: bias the wheels toward where the car is actually going.
        float steerCorrection = (1.0f - Mathf.Abs(steeringAdjust))
                                * Mathf.Clamp(Mathf.Asin(LocalVelocity.Normalized().X),
                                              -MaxSteeringAngle, MaxSteeringAngle)
                                * CountersteerAssist;

        // Not moving forward fast enough for the assist to make sense.
        if (LocalVelocity.Z > -0.5f)
            steerCorrection = 0.0f;
        else
            steerCorrection /= -MaxSteeringAngle;

        // Keeps the correction from latching on when it fights the driver's input.
        float steerCorrectionAmount = 1.0f;
        if (VehicleMath.SignF(steeringAdjust + steerCorrection) != VehicleMath.SignF(SteeringInput)
            && 1.0f - Mathf.Abs(SteeringInput) < steerCorrectionAmount)
            steerCorrectionAmount = Mathf.Clamp(steerCorrectionAmount - SteeringSpeed * delta, 0.0f, 1.0f);
        else
            steerCorrectionAmount = Mathf.Clamp(steerCorrectionAmount + SteeringSpeed * delta, 0.0f, 1.0f);

        steerCorrection *= steerCorrectionAmount;

        TrueSteeringAmount = Mathf.Clamp(steeringAdjust + steerCorrection,
                                         -MaxSteeringAngle, MaxSteeringAngle);

        foreach (Wheel wheel in WheelArray)
            wheel.Steer(steeringAdjust + steerCorrection, MaxSteeringAngle);
    }

    private void ProcessThrottle(float delta)
    {
        float throttleDelta = ThrottleSpeed * delta;

        if (ThrottleInput < ThrottleAmount)
        {
            ThrottleAmount -= throttleDelta;
            if (ThrottleInput > ThrottleAmount)
                ThrottleAmount = ThrottleInput;
        }
        else
        {
            ThrottleAmount += throttleDelta;
            if (ThrottleInput < ThrottleAmount)
                ThrottleAmount = ThrottleInput;
        }

        // Cut the throttle on the limiter and through a shift.
        if (MotorIsRedline || IsShifting)
            ThrottleAmount = 0.0f;

        // Slip the clutch while shifting or lugging below idle.
        ClutchAmount = _needClutch || IsShifting ? 1.0f : ClutchInput;
    }

    private void ProcessMotor(float delta)
    {
        float dragTorque = MotorRpm * MotorDrag;
        TorqueOutput = GetTorqueAtRpm(MotorRpm) * ThrottleAmount;
        TorqueOutput -= dragTorque * (1.0f + ClutchAmount * (1.0f - ThrottleAmount));

        // Look ahead one step so the motor can't produce torque below idle or past the limiter.
        float newRpm = MotorRpm + AngularVelocityToRpm * delta * TorqueOutput / MotorMoment;
        MotorIsRedline = false;
        if (newRpm > MaxRpm * 1.1f || newRpm <= IdleRpm)
        {
            TorqueOutput = 0.0f;
            if (newRpm > MaxRpm * 1.1f)
                MotorIsRedline = true;
        }

        MotorRpm += AngularVelocityToRpm * delta * (TorqueOutput - dragTorque) / MotorMoment;

        if (MotorRpm < IdleRpm + 100.0f)
            _needClutch = true;
        else if (newRpm > Mathf.Max(ClutchOutRpm, IdleRpm))
            _needClutch = false;

        MotorRpm = Mathf.Max(MotorRpm, IdleRpm);
    }

    /// <summary>
    /// Keeps the motor and the drivetrain closely coupled through the clutch, and pulls the
    /// power when traction control decides the drive wheels are spinning too much.
    /// </summary>
    private void ProcessClutch(float delta)
    {
        if (CurrentGear == 0)
            return;

        float currentGearRatio = GetGearRatio(CurrentGear);
        float driveInertia = MotorMoment
                             + Mathf.Pow(Mathf.Abs(currentGearRatio), 2.0f) * GearInertia
                             + _driveAxlesInertia;
        float driveInertiaR = driveInertia / (currentGearRatio * currentGearRatio);
        float reactionTorque = GetDriveWheelsReactionTorque() / currentGearRatio;
        float speedDifference = MotorRpm / AngularVelocityToRpm - GetDrivetrainSpin() * currentGearRatio;
        if (speedDifference < 0.0f)
            speedDifference = -Mathf.Sqrt(-speedDifference);

        float a = MotorMoment * driveInertiaR * speedDifference / delta;
        float b = MotorMoment * reactionTorque;
        float c = driveInertiaR * TorqueOutput;
        float clutchFactor = 1.0f - ClutchAmount;
        float tcsTorqueReduction = 0.0f;

        ClutchTorque = (a - b + c) / (MotorMoment + driveInertiaR) * clutchFactor;
        ClutchTorque = Mathf.Clamp(ClutchTorque,
                                   -MaxClutchTorque * clutchFactor,
                                   MaxClutchTorque * clutchFactor);

        if (TractionControlMaxSlip > 0.0f)
        {
            float slipY = 0.0f;
            foreach (Axle axle in Axles)
                slipY = Mathf.Max(slipY, axle.GetMaxWheelSlipY());

            if (slipY > TractionControlMaxSlip)
            {
                tcsTorqueReduction = TorqueOutput;
                ClutchTorque = 0.0f;
                TcsActive = true;
            }
            else
            {
                TcsActive = false;
            }
        }

        float clutchReactionTorque = ClutchTorque + tcsTorqueReduction;
        float newRpm = MotorRpm - AngularVelocityToRpm * delta * clutchReactionTorque / MotorMoment;
        if (newRpm < IdleRpm)
            newRpm = IdleRpm;
        if (newRpm < IdleRpm + 100.0f)
            _needClutch = true;
        else if (newRpm > Mathf.Max(ClutchOutRpm, IdleRpm))
            _needClutch = false;
        if (newRpm > MaxRpm * 1.1f)
            newRpm = MaxRpm * 1.1f;

        MotorRpm = newRpm;
    }

    /// <summary>
    /// Automatic gear selection. It compares the wheel speed the car is actually doing with
    /// the speed it *would* be doing without slip, so spinning the tires doesn't force an
    /// immediate upshift.
    /// </summary>
    private void ProcessTransmission()
    {
        if (IsShifting)
        {
            if (DeltaTime > _completeShiftDeltaTime)
                CompleteShift();
            return;
        }

        if (!AutomaticTransmission)
            return;

        bool reversing = CurrentGear == -1;
        float idealWheelSpin = Speed / _averageDriveWheelRadius;
        float drivetrainSpin = GetDrivetrainSpin();
        float realWheelSpin = drivetrainSpin * GetGearRatio(CurrentGear);
        float currentIdealGearRpm = GearRatioAt(CurrentGear - 1) * FinalDrive * idealWheelSpin
                                    * AngularVelocityToRpm;
        float currentRealGearRpm = realWheelSpin * AngularVelocityToRpm;

        if (!reversing)
        {
            float previousGearRpm = 0.0f;
            if (CurrentGear - 1 > 0)
                previousGearRpm = GetGearRatio(CurrentGear - 1)
                                  * Mathf.Max(drivetrainSpin, idealWheelSpin) * AngularVelocityToRpm;

            if (CurrentGear < GearRatios.Length)
            {
                if (CurrentGear > 0)
                {
                    if (currentIdealGearRpm > MaxRpm && DeltaTime - _lastShiftDeltaTime > ShiftTime)
                        Shift(1);
                    if (currentIdealGearRpm > MaxRpm * 0.8f && currentRealGearRpm > MaxRpm
                        && DeltaTime - _lastShiftDeltaTime > ShiftTime)
                        Shift(1);
                }
                else if (CurrentGear == 0 && MotorRpm > Mathf.Max(ClutchOutRpm, IdleRpm))
                {
                    Shift(1);
                }
            }

            if (CurrentGear - 1 > 0 && CurrentGear > 1 && previousGearRpm < 0.75f * MaxRpm
                && DeltaTime - _lastShiftDeltaTime > ShiftTime)
                Shift(-1);
        }

        // Holding the brake at a standstill swaps between first and reverse.
        if (Mathf.Abs(CurrentGear) <= 1 && BrakeInput > 0.75f)
        {
            if (!reversing)
            {
                if ((Speed < 1.0f || LocalVelocity.Z > 0.0f)
                    && DeltaTime - _lastShiftDeltaTime > ShiftTime)
                    Shift(-1);
            }
            else
            {
                if ((Speed < 1.0f || LocalVelocity.Z < 0.0f)
                    && DeltaTime - _lastShiftDeltaTime > ShiftTime)
                    Shift(1);
            }
        }
    }

    private void ProcessDrive(float delta)
    {
        float currentGearRatio = GetGearRatio(CurrentGear);
        float driveTorque = 0.0f;
        float driveInertia = MotorMoment + Mathf.Pow(currentGearRatio, 2.0f) * GearInertia;
        bool isSlipping = GetIsAWheelSlipping();

        if (CurrentGear != 0)
            driveTorque = ClutchTorque * currentGearRatio;

        if (VariableTorqueSplit)
        {
            if (isSlipping && ThrottleAmount > 0.1f)
                _currentTorqueSplit = Mathf.Clamp(_currentTorqueSplit + delta / VariableSplitSpeed, 0.0f, 1.0f);
            else
                _currentTorqueSplit = Mathf.Clamp(_currentTorqueSplit - delta / VariableSplitSpeed, 0.0f, 1.0f);
        }

        // Same coupling formula as the clutch, but keeping the two axles together, with a
        // split so one axle can be favoured.
        TrueTorqueSplit = Mathf.Lerp(FrontTorqueSplit, FrontVariableSplit, _currentTorqueSplit);
        Axle axleA = FrontAxle;
        Axle axleB = RearAxle;
        if (TrueTorqueSplit <= 0.5f)
        {
            axleA = RearAxle;
            axleB = FrontAxle;
        }

        float axleDifference = axleA.GetSpin() - axleB.GetSpin();
        float a = axleA.Inertia * axleB.Inertia * axleDifference / delta;
        float b = axleA.Inertia;
        float c = axleB.Inertia * driveTorque;
        float transferTorque = (a - b + c) / (axleA.Inertia + axleB.Inertia);
        transferTorque = Mathf.Clamp(transferTorque, -Mathf.Abs(driveTorque), Mathf.Abs(driveTorque))
                         * (1.0f - Mathf.Abs((0.5f - TrueTorqueSplit) * 2.0f));
        float transferTorque2 = driveTorque - transferTorque;

        ProcessAxleDrive(axleB, transferTorque, driveInertia, delta);
        ProcessAxleDrive(axleA, transferTorque2, driveInertia, delta);
    }

    private void ProcessAxleDrive(Axle axle, float torque, float driveInertia, float delta)
    {
        if (!axle.IsDriveAxle)
        {
            torque = 0.0f;
            driveInertia = 0.0f;
        }

        bool allowAbs = true;

        // The handbrake is a cable — no ABS on the axle it acts on.
        if (axle.Handbrake)
        {
            _brakeForce += _handbrakeForce;
            allowAbs = false;
        }

        if (axle.IsDriveAxle && axle.DifferentialLockTorque >= 0.0f)
        {
            if (Mathf.Abs(torque) > axle.DifferentialLockTorque)
            {
                // Locked: force both wheels to the same speed, then vector torque to the
                // outside wheel based on steering.
                axle.RotationSplit = 0.5f + axle.TorqueVectoring * -SteeringInput;
                float coupleSpin = axle.GetAverageSpin();
                axle.Wheels[0].Spin = coupleSpin * axle.RotationSplit * 2.0f;
                axle.Wheels[1].Spin = coupleSpin * (1.0f - axle.RotationSplit) * 2.0f;
                axle.RotationSplit = axle.RotationSplit * 2.0f - 1.0f;
            }
            else if (torque != 0.0f)
            {
                // Open: let the wheel with less grip take the torque, within limits.
                float leftReactionTorqueRatio = -Mathf.Abs(axle.Wheels[0].GetReactionTorque() / torque);
                float rightReactionTorqueRatio = Mathf.Abs(axle.Wheels[1].GetReactionTorque() / torque);
                axle.RotationSplit = Mathf.Max(axle.RotationSplit, leftReactionTorqueRatio);
                axle.RotationSplit = Mathf.Min(axle.RotationSplit, rightReactionTorqueRatio);
            }
        }

        float rotationSum = 0.0f;
        float split = (axle.RotationSplit + 1.0f) * 0.5f;
        axle.AppliedSplit = axle.RotationSplit;
        rotationSum += axle.Wheels[0].ProcessTorque(
            torque * split, driveInertia, _brakeForce * 0.5f * axle.BrakeBias, allowAbs, delta);
        rotationSum += axle.Wheels[1].ProcessTorque(
            torque * (1.0f - split), driveInertia, _brakeForce * 0.5f * axle.BrakeBias, allowAbs, delta);
        axle.RotationSplit = Mathf.Clamp(rotationSum, -1.0f, 1.0f);
    }

    private void ProcessForces(float delta)
    {
        // Each wheel needs the *other* wheel's compression for the antiroll bar, so the left
        // value is stashed before it gets overwritten.
        foreach (Axle axle in Axles)
        {
            float previousCompressionLeft = axle.SuspensionCompressionLeft;
            axle.SuspensionCompressionLeft =
                axle.Wheels[0].ProcessForces(axle.SuspensionCompressionRight, IsBraking, delta);
            axle.SuspensionCompressionRight =
                axle.Wheels[1].ProcessForces(previousCompressionLeft, IsBraking, delta);
        }
    }

    private void ProcessStability()
    {
        bool isStabilityOn = false;

        if (EnableStability)
        {
            StabilityYawTorque = 0.0f;
            var planeXz = new Vector2(LocalVelocity.X, LocalVelocity.Z);
            if (planeXz.Y < 0.0f && planeXz.Length() > 1.0f)
            {
                planeXz = planeXz.Normalized();
                float yawAngle = 1.0f - Mathf.Abs(planeXz.Dot(Vector2.Up));
                if (yawAngle > StabilityYawEngageAngle
                    && VehicleMath.SignF(AngularVelocity.Y) == VehicleMath.SignF(planeXz.X))
                {
                    StabilityYawTorque = (yawAngle - StabilityYawEngageAngle) * StabilityYawStrength;
                    StabilityYawTorque *= VehicleInertia.Y
                                          * Mathf.Clamp(Mathf.Abs(AngularVelocity.Y) - 0.5f, 0.0f, 1.0f);
                }
            }

            StabilityTorqueVector = Vector3.Zero;
            if (GetWheelContactCount() < 3)
            {
                // Airborne: spring the roof back toward up and damp the tumble.
                StabilityTorqueVector =
                    GlobalTransform.Basis.Y.Cross(Vector3.Up) * VehicleInertia * StabilityUprightSpring
                    + -AngularVelocity * StabilityUprightDamping;
                ApplyTorque(StabilityTorqueVector);
            }
            else
            {
                StabilityYawTorque *= StabilityYawGroundMultiplier;
            }

            if (StabilityYawTorque != 0.0f)
            {
                isStabilityOn = true;
                StabilityYawTorque *= VehicleMath.SignF(-LocalVelocity.X);
                ApplyTorque(GlobalTransform.Basis.Y * StabilityYawTorque);
            }
        }

        StabilityActive = isStabilityOn;
    }

    // ---------------------------------------------------------------- Gearbox API

    /// <summary>Shift by <paramref name="count"/> gears, but only in manual mode.</summary>
    public void ManualShift(int count)
    {
        if (!AutomaticTransmission)
            Shift(count);
    }

    private void Shift(int count)
    {
        if (IsShifting)
            return;

        RequestedGear = CurrentGear + count;

        if (RequestedGear > GearRatios.Length || RequestedGear < -1)
            return;

        if (CurrentGear == 0)
        {
            // Coming out of neutral is instant; there's no drive to interrupt.
            CompleteShift();
            return;
        }

        _completeShiftDeltaTime = DeltaTime + ShiftTime;
        ClutchAmount = 1.0f;
        IsShifting = true;
        if (count > 0)
            IsUpShifting = true;
    }

    private void CompleteShift()
    {
        if (CurrentGear == -1)
            BrakeAmount = 0.0f;

        if (RequestedGear < CurrentGear)
        {
            // Blip the revs toward where the new gear wants them on a downshift.
            float wheelSpin = Speed / _averageDriveWheelRadius;
            float requestedGearRpm = GearRatioAt(RequestedGear - 1) * FinalDrive * wheelSpin
                                     * AngularVelocityToRpm;
            MotorRpm = Mathf.Lerp(MotorRpm, requestedGearRpm, 0.5f);
        }

        CurrentGear = RequestedGear;
        _lastShiftDeltaTime = DeltaTime;
        IsShifting = false;
        IsUpShifting = false;
    }

    // ---------------------------------------------------------------- Queries

    public int GetWheelContactCount()
    {
        int contactCount = 0;
        foreach (Wheel wheel in WheelArray)
        {
            if (wheel.IsColliding())
                contactCount++;
        }
        return contactCount;
    }

    public bool GetIsAWheelSlipping()
    {
        foreach (Wheel wheel in DriveWheels)
        {
            if (!wheel.LimitSpin)
                return true;
        }
        return false;
    }

    public float GetDrivetrainSpin()
    {
        if (DriveWheels.Count == 0)
            return 0.0f;

        float driveSpin = 0.0f;
        foreach (Wheel wheel in DriveWheels)
            driveSpin += wheel.Spin;

        return driveSpin / DriveWheels.Count;
    }

    private float GetDriveWheelsReactionTorque()
    {
        float reactionTorque = 0.0f;
        foreach (Wheel wheel in DriveWheels)
            reactionTorque += wheel.ForceVector.Y * wheel.TireRadius;
        return reactionTorque;
    }

    public float GetGearRatio(int gear)
    {
        if (gear > 0)
            return GearRatioAt(gear - 1) * FinalDrive;
        if (gear == -1)
            return -ReverseRatio * FinalDrive;
        return 0.0f;
    }

    /// <summary>
    /// Indexes <see cref="GearRatios"/> the way GDScript arrays do, wrapping negative
    /// indices around to the end. The transmission code relies on that: in neutral or
    /// reverse it computes a gear RPM from index -1 or -2 before deciding whether to use it.
    /// </summary>
    private float GearRatioAt(int index)
    {
        int count = GearRatios.Length;
        if (count == 0)
            return 0.0f;

        int i = index < 0 ? count + index : index;
        if (i < 0 || i >= count)
            return 0.0f;

        return GearRatios[i];
    }

    public float GetTorqueAtRpm(float lookupRpm)
    {
        if (TorqueCurve == null)
            return 0.0f;

        float rpmFactor = Mathf.Clamp(lookupRpm / MaxRpm, 0.0f, 1.0f);
        return TorqueCurve.SampleBaked(rpmFactor) * MaxTorque;
    }

    private float GetMaxSteeringSlipAngle()
    {
        float steeringSlip = 0.0f;
        foreach (Wheel wheel in FrontAxle.Wheels)
        {
            if (Mathf.Abs(steeringSlip) < Mathf.Abs(wheel.SlipVector.X))
                steeringSlip = wheel.SlipVector.X;
        }
        return steeringSlip;
    }

    // ---------------------------------------------------------------- Setup maths

    private float CalculateAverageTireFriction(float weight, string surface)
    {
        float friction = 0.0f;
        foreach (Wheel wheel in WheelArray)
            friction += wheel.GetFriction(weight / WheelArray.Count, surface);
        return friction;
    }

    private void CalculateBrakeForce()
    {
        float friction = CalculateAverageTireFriction(VehicleMass * 9.8f, SurfaceGroups.Road);
        _maxBrakeForce = friction * BrakingGripMultiplier * _averageDriveWheelRadius
                         / WheelArray.Count * BrakeForceMultiplier;
        _maxHandbrakeForce = friction * BrakingGripMultiplier * 0.05f / _averageDriveWheelRadius;
    }

    private Vector3 CalculateCenterOfGravity(float frontDistribution,
                                             Wheel frontLeft, Wheel frontRight,
                                             Wheel rearLeft, Wheel rearRight)
    {
        FrontAxlePosition = frontLeft.Position.Lerp(frontRight.Position, 0.5f);
        RearAxlePosition = rearLeft.Position.Lerp(rearRight.Position, 0.5f);
        return RearAxlePosition.Lerp(FrontAxlePosition, frontDistribution);
    }

    /// <summary>Spring rate (N/mm) that puts the spring at <paramref name="restingRatio"/> under static load.</summary>
    private static float CalculateSpringRate(float weight, float springLength, float restingRatio)
    {
        float correctedRestingRatio = springLength * restingRatio / springLength;
        float targetCompression = springLength * correctedRestingRatio * 1000.0f;
        return weight / targetCompression;
    }

    private static float CalculateDamping(float weight, float springRate, float dampingRatio)
        => dampingRatio * 2.0f * Mathf.Sqrt(springRate * weight) * 0.01f;

    private static float CalculateAxleSpringForce(float compression, float springLength, float springRate)
        => springLength * compression * 1000.0f * springRate * 2.0f;

    private float CalculateAckermann(float wheelBase, float trackWidth)
        => Mathf.Atan(wheelBase * Mathf.Tan(MaxSteeringAngle)
                      / (wheelBase - trackWidth * 0.5f * Mathf.Tan(MaxSteeringAngle)))
           / MaxSteeringAngle - 1.0f;

    private static void ApplyWheelAlignment(Wheel wheel, float ackermann, float camber, float toe)
    {
        wheel.Ackermann = ackermann;
        Vector3 rotation = wheel.Rotation;
        rotation.Z = camber;
        wheel.Rotation = rotation;
        wheel.Toe = toe;
    }

    /// <summary>A flat 100%-torque curve, used only when none was assigned.</summary>
    private static Curve BuildFallbackTorqueCurve()
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0.0f, 1.0f));
        curve.AddPoint(new Vector2(1.0f, 1.0f));
        return curve;
    }
}
