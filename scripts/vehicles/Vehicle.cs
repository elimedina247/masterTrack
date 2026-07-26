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
/// A ray-cast rigid-body vehicle: suspension, brush-model tires, a speed-curve powertrain and
/// a pile of driver assists. The body is a plain <see cref="RigidBody3D"/> — all the forces
/// come from the four <see cref="Wheel"/> ray casts, which must be direct children of this node.
///
/// <b>There is no gearbox.</b> The source project's motor, clutch and transmission have been
/// replaced by a single <see cref="DriveCurve"/> of force against road speed, which is how
/// arcade racers usually do it — see docs/vehicle-physics.md. Drive torque still flows through
/// the wheels rather than being applied to the body, so wheelspin and power-oversteer survive.
///
/// Drive it by writing to <see cref="ThrottleInput"/>, <see cref="SteeringInput"/>,
/// <see cref="BrakeInput"/>, <see cref="HandbrakeInput"/> and <see cref="NitroInput"/> each
/// physics step — see <see cref="VehicleInput"/> for the keyboard/pad sampler.
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

    /// <summary>
    /// Handbrake torque per rear wheel, as a multiple of the *total* footbrake torque. The
    /// handbrake deliberately bypasses <see cref="FrontBrakeBias"/> — it is a cable onto the
    /// rear axle, not a hydraulic circuit — so this is the whole story for that axle. At full
    /// brakes a rear wheel only sees <c>0.5 * (1 - FrontBrakeBias)</c> of the total, so the
    /// default here is several times the footbrake and locks the rears more or less on contact.
    /// </summary>
    [ExportSubgroup("Handbrake")]
    [Export] public float HandbrakeForceMultiplier { get; set; } = 1.5f;

    /// <summary>
    /// Lateral grip a fully locked rear tire keeps, as a fraction of what it would have had
    /// rolling. This is the whole trick: a locked tire has spent its entire friction budget
    /// sliding forwards and has almost nothing left to resist sideways motion, which is what
    /// lets the back end come round. The brush model in <see cref="Wheel"/> has its past-peak
    /// fall-off removed on purpose, so that coupling has to be reintroduced here.
    ///
    /// Lower = the rear steps out sooner and further. 0 is a rear axle on ice.
    /// </summary>
    [Export] public float HandbrakeLockedGrip { get; set; } = 0.15f;

    /// <summary>
    /// Longitudinal slip at which the rear tire counts as fully locked for
    /// <see cref="HandbrakeLockedGrip"/>. Grip fades in across the range below this, so grabbing
    /// the lever loosens the car progressively instead of dropping it out from under the player.
    /// </summary>
    [Export] public float HandbrakeLockSlip { get; set; } = 0.7f;

    /// <summary>
    /// How much of the yaw stability assist the handbrake switches off, 0..1. At 1 a held
    /// handbrake disables it entirely.
    ///
    /// Without this nothing else on this list matters: <see cref="ProcessStability"/> applies a
    /// counter-yaw torque straight to the body as soon as the car rotates away from its
    /// direction of travel, which is exactly the rotation the handbrake exists to create.
    /// </summary>
    [Export] public float HandbrakeStabilitySuppression { get; set; } = 1.0f;

    /// <summary>
    /// How much faster than the road the driven wheels may spin before traction control
    /// starts cutting drive force, in m/s at the contact patch. Negative disables it.
    ///
    /// This is measured the same way as <see cref="FrontAbsSpinDifferenceThreshold"/> — a
    /// wheel-vs-road speed difference — rather than as a slip ratio. It used to be compared
    /// against <c>SlipVector.Y</c>, which is negative under wheelspin and never exceeds 1.0,
    /// so the old ceiling of 8.0 could not be reached and traction control never once ran.
    /// </summary>
    [Export] public float TractionControlMaxWheelSpin { get; set; } = 8.0f;

    /// <summary>
    /// Wheelspin past <see cref="TractionControlMaxWheelSpin"/> at which traction control
    /// reaches a full cut, in m/s. The cut ramps across this range instead of switching on,
    /// because slamming drive force to zero lurches the car exactly when it is least settled.
    /// </summary>
    [Export] public float TractionControlFadeRange { get; set; } = 6.0f;

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

    // ---------------------------------------------------------------- Powertrain

    /// <summary>
    /// Drive force at the contact patch when <see cref="DriveCurve"/> reads 1.0, in newtons.
    /// Divide by <see cref="VehicleMass"/> for the resulting acceleration in m/s².
    /// </summary>
    [ExportGroup("Powertrain")]
    [Export] public float MaxDriveForce { get; set; } = 12000.0f;

    /// <summary>Speed in m/s the drive curve is measured against. Multiply by 3.6 for km/h.</summary>
    [Export] public float TopSpeed { get; set; } = 200.0f;

    /// <summary>
    /// Fraction of <see cref="MaxDriveForce"/> available across the speed range, where X is
    /// <c>speed / TopSpeed</c>. This one curve replaces the motor, clutch and gearbox.
    ///
    /// A real drivetrain shapes force against speed via gearing; here that shape is authored
    /// directly. Neither a flat force nor a linear ramp works on its own — flat force plus aero
    /// drag can give punch or a sane top speed but not both, and a linear ramp bleeds away so
    /// early the car feels like it gives up. A curve that holds near full force through the
    /// midrange and then falls off is what reads as "fast" while still capping out.
    ///
    /// Ending at 0.0 makes <see cref="TopSpeed"/> a real ceiling rather than an asymptote.
    /// </summary>
    [Export] public Curve? DriveCurve { get; set; }

    /// <summary>Top speed in reverse, in m/s.</summary>
    [Export] public float ReverseTopSpeed { get; set; } = 25.0f;

    /// <summary>Drive force in reverse, as a fraction of <see cref="MaxDriveForce"/>.</summary>
    [Export] public float ReverseForceRatio { get; set; } = 0.45f;

    /// <summary>
    /// Rotational inertia the drive wheels are fighting, standing in for the motor and gearbox
    /// that used to supply it. Higher resists wheelspin; lower lets the tires light up sooner.
    ///
    /// The old gearbox made this vary with the gear — inertia scales with the square of the
    /// ratio, so it ran about 3.5 in first down to 0.6 in top. This sits mid-range: high enough
    /// that the tires don't light up instantly off every corner, low enough that the rear can
    /// still be provoked into stepping out. It also sets how *fast* a slide develops and
    /// recovers — higher feels mushy and slow to react, lower feels twitchy.
    /// </summary>
    [Export] public float DrivetrainInertia { get; set; } = 1.5f;

    /// <summary>Speed below which holding the brake swaps between forward and reverse, in m/s.</summary>
    [Export] public float DirectionSwapSpeed { get; set; } = 2.0f;

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

    /// <summary>
    /// Tire stiffness per surface group; higher = the tire builds force over a smaller slip
    /// angle, so the car answers the wheel more sharply.
    /// </summary>
    [Export] public Godot.Collections.Dictionary<string, float> TireStiffnesses { get; set; } = new()
    {
        { SurfaceGroups.Road, 10.0f }, { SurfaceGroups.Dirt, 0.5f },
        { SurfaceGroups.Grass, 0.5f }, { SurfaceGroups.Ice, 0.3f },
    };

    /// <summary>
    /// Grip multiplier per surface group. This is the single biggest handling number.
    ///
    /// Road is 1.8 rather than the source project's arcade value of 3.0, because Master Track
    /// wants cars that slide. Two reasons it matters:
    ///
    /// - <b>Drifting.</b> The rear has to be able to let go and stay gone. At 3.0 the tires
    ///   simply never saturate at any speed the track allows.
    /// - <b>Not rolling over.</b> Rollover threshold is geometry: half-track / CoG height.
    ///   Grip above that number means the car tips before it slides, every time. Mass makes no
    ///   difference to this — it cancels out of both sides.
    ///
    /// Keep an eye on the two together: if you raise this, raise the track width or lower the
    /// centre of gravity to match, or the car will start flipping again.
    /// </summary>
    [Export] public Godot.Collections.Dictionary<string, float> CoefficientOfFriction { get; set; } = new()
    {
        { SurfaceGroups.Road, 1.8f }, { SurfaceGroups.Dirt, 1.5f },
        { SurfaceGroups.Grass, 1.1f }, { SurfaceGroups.Ice, 0.45f },
    };

    /// <summary>Rolling resistance multiplier per surface group.</summary>
    [Export] public Godot.Collections.Dictionary<string, float> RollingResistance { get; set; } = new()
    {
        { SurfaceGroups.Road, 1.0f }, { SurfaceGroups.Dirt, 2.0f },
        { SurfaceGroups.Grass, 4.0f }, { SurfaceGroups.Ice, 0.8f },
    };

    /// <summary>
    /// Bonus grip proportional to lateral slip — the more the tire slides, the harder it grips
    /// back. Effectively an anti-slide assist: it keeps a slide from running away, at the cost
    /// of fighting the player when they're deliberately holding an angle.
    /// </summary>
    [Export] public Godot.Collections.Dictionary<string, float> LateralGripAssist { get; set; } = new()
    {
        { SurfaceGroups.Road, 0.05f }, { SurfaceGroups.Dirt, 0.0f },
        { SurfaceGroups.Grass, 0.0f }, { SurfaceGroups.Ice, 0.0f },
    };

    /// <summary>
    /// Longitudinal grip as a ratio of lateral grip. Splitting the two is what lets the car be
    /// loose sideways without being loose under power.
    ///
    /// Road is 0.85, which is the fishtail window for this car. Against the rear axle's
    /// capacity under load, <see cref="MaxDriveForce"/> lands at roughly:
    ///
    /// <code>
    ///   0.65 -> 130%   permanent wheelspin, the car is on ice and never straightens
    ///   0.85 -> 100%   breaks loose on power, hooks up as weight transfers  &lt;-- here
    ///   1.00 ->  85%   always hooks up, the back end will not step out at all
    /// </code>
    ///
    /// These are for the racer's 11000 N and 1200 kg. This number tracks
    /// <see cref="MaxDriveForce"/>: the ratio above is what matters, not the value here, so
    /// if you change the power, move this to match or the car changes character.
    ///
    /// Sitting right at the limit is what makes the slide throttle-controllable rather than
    /// either permanent or impossible. Note this changes cornering grip too, even though
    /// <see cref="CoefficientOfFriction"/> is untouched: the brush model shares one friction
    /// budget between the two axes, so a rear wheel that is spinning has less grip sideways.
    /// That coupling <i>is</i> the fishtail.
    /// </summary>
    [Export] public Godot.Collections.Dictionary<string, float> LongitudinalGripRatio { get; set; } = new()
    {
        { SurfaceGroups.Road, 0.85f }, { SurfaceGroups.Dirt, 0.5f },
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

    // ---------------------------------------------------------------- Nitro

    /// <summary>
    /// Boosts the player starts a run with. Spent one at a time and never refilled — call
    /// <see cref="ResetNitro"/> when a new run begins.
    /// </summary>
    [ExportGroup("Nitro")]
    [Export] public int NitroCharges { get; set; } = 5;

    /// <summary>How long one charge burns for, in seconds.</summary>
    [Export] public float NitroDuration { get; set; } = 1.5f;

    /// <summary>
    /// Push in newtons, applied to the body along its nose rather than through the tires.
    /// Divide by <see cref="VehicleMass"/> for the acceleration it adds: at the default 9000 N
    /// on a 1200 kg car that is 7.5 m/s² on top of whatever the wheels are already doing.
    ///
    /// Going in at the body rather than the contact patch is deliberate. Fed through the
    /// drivetrain a boost would be eaten by traction control, by wheelspin, by a rear axle
    /// that is sideways, and by the drive curve being flat at the top of the range — it would
    /// do least exactly when the player most expects a shove. This way it always lands.
    /// </summary>
    [Export] public float NitroForce { get; set; } = 9000.0f;

    /// <summary>
    /// Ceiling while boosting, as a multiple of <see cref="TopSpeed"/>. This is a hard cap:
    /// both the drive curve and the nitro push itself fade out as the car approaches it, so
    /// charges chained back to back can't walk the car up to an arbitrary speed. At the
    /// racer's 55.6 m/s top speed the default puts the boosted ceiling at 250 km/h.
    /// </summary>
    [Export] public float NitroTopSpeedMultiplier { get; set; } = 1.25f;

    /// <summary>Dead time after a burst ends before the next charge can be spent, in seconds.</summary>
    [Export] public float NitroCooldown { get; set; } = 0.4f;

    /// <summary>
    /// Fraction of the boosted ceiling the nitro push holds full force to, before fading out.
    /// Below this the shove is the full <see cref="NitroForce"/>; between here and the ceiling
    /// it tapers to nothing, which is what stops the cap arriving as a wall.
    /// </summary>
    private const float NitroFullForceFraction = 0.75f;

    // ---------------------------------------------------------------- Runtime state

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

    /// <summary>Whether the nitro button is held. The rising edge is what spends a charge.</summary>
    public bool NitroInput;

    // ---- Derived state, safe to read for HUD / effects / debug ----

    public bool IsVehicleReady { get; private set; }
    public Vector3 LocalVelocity { get; private set; } = Vector3.Zero;

    /// <summary>Speed in m/s. Multiply by 3.6 for km/h.</summary>
    public float Speed { get; private set; }

    public float SteeringAmount { get; private set; }

    /// <summary>Steering after the <see cref="SteeringExponent"/> curve, before assists.</summary>
    public float SteeringExponentAmount { get; private set; }

    /// <summary>Final steering angle in radians, after the exponent and the countersteer assist.</summary>
    public float TrueSteeringAmount { get; private set; }
    public float ThrottleAmount { get; private set; }
    public float BrakeAmount { get; private set; }

    /// <summary>
    /// Direction of travel: 1 forward, -1 reverse. There is no gearbox — this only picks which
    /// way drive force is applied. Kept as an int so input mapping and the HUD read naturally.
    /// </summary>
    public int CurrentGear { get; private set; } = 1;

    /// <summary>Drive force currently reaching the contact patch, in newtons.</summary>
    public float DriveForce { get; private set; }

    /// <summary>Drive torque handed to the axles this step, in Nm.</summary>
    public float TorqueOutput { get; private set; }

    /// <summary>How far into the speed range the car is, 0..1. Drives the fake-gear audio.</summary>
    public float SpeedFraction { get; private set; }

    public float TrueTorqueSplit { get; private set; }
    public bool IsBraking { get; private set; }
    public bool TcsActive { get; private set; }

    /// <summary>Fraction of drive force traction control is currently letting through, 0..1.</summary>
    public float TcsFactor { get; private set; } = 1.0f;
    /// <summary>Charges left in this run. Starts at <see cref="NitroCharges"/>.</summary>
    public int NitroChargesRemaining { get; private set; }

    /// <summary>Seconds left on the current burst, 0 when not boosting.</summary>
    public float NitroTimeRemaining { get; private set; }

    /// <summary>True while a charge is burning.</summary>
    public bool IsNitroActive => NitroTimeRemaining > 0.0f;

    /// <summary>How much of the current burst is left, 1 at ignition down to 0. For HUD and FX.</summary>
    public float NitroFraction => NitroDuration > 0.0f
        ? Mathf.Clamp(NitroTimeRemaining / NitroDuration, 0.0f, 1.0f)
        : 0.0f;

    /// <summary>Fires when a charge is spent, with the number left afterwards. For HUD, audio and FX.</summary>
    [Signal] public delegate void NitroFiredEventHandler(int chargesRemaining);

    /// <summary>Fires when a burst burns out.</summary>
    [Signal] public delegate void NitroEndedEventHandler();

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
    private float _averageDriveWheelRadius;
    private float _currentTorqueSplit;
    private float _brakeForce;
    private float _maxBrakeForce;
    private float _handbrakeForce;
    private float _maxHandbrakeForce;

    /// <summary>Stops the forward/reverse swap flickering while the brake is held at a stop.</summary>
    private float _directionSwapCooldown;

    /// <summary>Last frame's <see cref="NitroInput"/>, so a held button only spends one charge.</summary>
    private bool _nitroWasHeld;

    /// <summary>Time until another charge may be spent. Covers the burst plus the dead time after it.</summary>
    private float _nitroLockout;

    /// <summary>
    /// How much of the countersteer assist is currently being let through, 0..1. It ramps
    /// down toward <c>1 - |SteeringInput|</c> while the assist is pulling against the
    /// driver's input and back to 1 when it isn't, so committing to the wheel makes the
    /// assist release rather than fight.
    ///
    /// <b>This has to persist between steps.</b> It was previously a local re-initialised to
    /// 1.0 every frame, which meant a single <c>SteeringSpeed * delta</c> step was the most it
    /// could ever move — 0.965 at 120 Hz — and the release never happened at all.
    /// </summary>
    private float _steerCorrectionAmount = 1.0f;

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

        DriveCurve ??= BuildDefaultDriveCurve();

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
            wheel.HandbrakeLockedGrip = HandbrakeLockedGrip;
            wheel.HandbrakeLockSlip = HandbrakeLockSlip;
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
        ResetNitro();

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
        ProcessDirection(dt);
        // Before ProcessDrive, so the drive curve sees this step's boosted speed ceiling.
        ProcessNitro(dt);
        ProcessDrive(dt);
        ProcessForces(dt);
        ProcessStability();
    }

    /// <summary>
    /// Spend a nitro charge, if there is one to spend and the car isn't already boosting or
    /// still inside the dead time after the last burst. Returns whether one was actually lit,
    /// so a caller can play the "empty" click instead.
    ///
    /// Public so a pickup, a scripted sequence or an AI driver can fire one without having to
    /// fake a button press.
    /// </summary>
    public bool TryActivateNitro()
    {
        if (NitroChargesRemaining <= 0 || _nitroLockout > 0.0f)
            return false;

        NitroChargesRemaining--;
        NitroTimeRemaining = NitroDuration;
        _nitroLockout = NitroDuration + NitroCooldown;
        EmitSignal(SignalName.NitroFired, NitroChargesRemaining);
        return true;
    }

    /// <summary>Refill to <see cref="NitroCharges"/> and cancel any burst. Call when a run starts.</summary>
    public void ResetNitro()
    {
        NitroChargesRemaining = NitroCharges;
        NitroTimeRemaining = 0.0f;
        _nitroLockout = 0.0f;
        _nitroWasHeld = false;
    }

    private void ProcessNitro(float delta)
    {
        if (_nitroLockout > 0.0f)
            _nitroLockout = Mathf.Max(_nitroLockout - delta, 0.0f);

        if (IsNitroActive)
        {
            NitroTimeRemaining = Mathf.Max(NitroTimeRemaining - delta, 0.0f);
            if (!IsNitroActive)
                EmitSignal(SignalName.NitroEnded);
        }

        // Edge, not level: holding the button down burns one charge, not all five.
        if (NitroInput && !_nitroWasHeld)
            TryActivateNitro();
        _nitroWasHeld = NitroInput;

        if (!IsNitroActive)
            return;

        // Taper the push as the boosted ceiling comes up, so chaining charges runs into a
        // speed limit rather than adding velocity forever.
        float ceiling = GetNitroSpeedCeiling();
        float fade = 1.0f - Mathf.Clamp(
            Mathf.InverseLerp(ceiling * NitroFullForceFraction, ceiling, Speed), 0.0f, 1.0f);

        if (fade <= 0.0f)
            return;

        // Along the nose, signed by the direction of travel the same way drive torque is, so
        // boosting while reversing pushes the way the player is actually going.
        Vector3 forward = -GlobalTransform.Basis.Z * CurrentGear;
        ApplyCentralForce(forward * NitroForce * fade);
    }

    /// <summary>Speed ceiling in m/s that applies while boosting, for the current direction.</summary>
    private float GetNitroSpeedCeiling()
    {
        float baseCeiling = CurrentGear == -1 ? ReverseTopSpeed : TopSpeed;
        return baseCeiling * Mathf.Max(NitroTopSpeedMultiplier, 1.0f);
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

        // Keeps the correction from latching on when it fights the driver's input. The floor
        // is 1 - |SteeringInput|, so the harder the player commits the further it backs off.
        if (VehicleMath.SignF(steeringAdjust + steerCorrection) != VehicleMath.SignF(SteeringInput)
            && 1.0f - Mathf.Abs(SteeringInput) < _steerCorrectionAmount)
            _steerCorrectionAmount = Mathf.Clamp(_steerCorrectionAmount - SteeringSpeed * delta, 0.0f, 1.0f);
        else
            _steerCorrectionAmount = Mathf.Clamp(_steerCorrectionAmount + SteeringSpeed * delta, 0.0f, 1.0f);

        steerCorrection *= _steerCorrectionAmount;

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

    }

    /// <summary>
    /// Pick whether drive force pushes the car forward or backward. There is no gearbox, so
    /// this is just a direction flag: hold the brake at walking pace and the car swaps, which
    /// is how the geared version behaved and what players expect from an automatic.
    /// </summary>
    private void ProcessDirection(float delta)
    {
        _directionSwapCooldown = Mathf.Max(0.0f, _directionSwapCooldown - delta);

        if (_directionSwapCooldown > 0.0f || BrakeInput <= 0.75f || Speed >= DirectionSwapSpeed)
            return;

        // LocalVelocity.Z is negative moving forward, so only swap once the car has actually
        // stopped going the way it currently points.
        bool goingForward = LocalVelocity.Z < -0.1f;
        bool goingBackward = LocalVelocity.Z > 0.1f;

        if (CurrentGear == 1 && !goingForward)
        {
            CurrentGear = -1;
            BrakeAmount = 0.0f;
            _directionSwapCooldown = 0.4f;
        }
        else if (CurrentGear == -1 && !goingBackward)
        {
            CurrentGear = 1;
            BrakeAmount = 0.0f;
            _directionSwapCooldown = 0.4f;
        }
    }

    /// <summary>
    /// Drive force straight off the curve, then converted to axle torque.
    ///
    /// Force is expressed at the contact patch rather than at a crankshaft, so the curve says
    /// exactly what the player feels and the tuning stays honest: <c>MaxDriveForce / mass</c>
    /// is the launch acceleration, and the curve is how that fades toward
    /// <see cref="TopSpeed"/>. Turning it back into torque here means the differential, wheel
    /// spin and tire model downstream are completely unchanged — the car can still light up
    /// its rears and be driven on the throttle.
    /// </summary>
    private float ComputeDriveTorque()
    {
        bool reversing = CurrentGear == -1;
        float ceiling = reversing ? ReverseTopSpeed : TopSpeed;

        // Deliberately off the *unboosted* ceiling: this is what the engine note is pitched
        // from, and rescaling it under nitro would drop the revs at the moment of the shove.
        // It already clamps at 1, so the car just sits on the limiter past top speed.
        SpeedFraction = Mathf.Clamp(Speed / Mathf.Max(ceiling, 0.001f), 0.0f, 1.0f);

        // The curve, though, is sampled against the boosted ceiling, so the wheels keep pulling
        // past where they would normally have run out of drive.
        if (IsNitroActive)
            ceiling = GetNitroSpeedCeiling();

        float driveFraction = Mathf.Clamp(Speed / Mathf.Max(ceiling, 0.001f), 0.0f, 1.0f);
        float available = DriveCurve?.SampleBaked(driveFraction) ?? 1.0f;
        if (reversing)
            available *= ReverseForceRatio;

        DriveForce = MaxDriveForce * Mathf.Max(available, 0.0f) * ThrottleAmount;

        // Traction control: fade drive out as the driven wheels overrun the road speed.
        // SpinVelocityDiff is signed by the wheel's direction of rotation, so multiplying by
        // CurrentGear makes "spinning faster than the road" positive whichever way we're going.
        if (TractionControlMaxWheelSpin > 0.0f)
        {
            float wheelSpin = 0.0f;
            foreach (Wheel wheel in DriveWheels)
                wheelSpin = Mathf.Max(wheelSpin, wheel.SpinVelocityDiff * CurrentGear);

            float excess = wheelSpin - TractionControlMaxWheelSpin;
            TcsFactor = excess <= 0.0f
                ? 1.0f
                : Mathf.Clamp(1.0f - excess / Mathf.Max(TractionControlFadeRange, 0.001f), 0.0f, 1.0f);
            TcsActive = TcsFactor < 1.0f;
            DriveForce *= TcsFactor;
        }
        else
        {
            TcsFactor = 1.0f;
            TcsActive = false;
        }

        TorqueOutput = DriveForce * _averageDriveWheelRadius * CurrentGear;
        return TorqueOutput;
    }

    private void ProcessDrive(float delta)
    {
        float driveTorque = ComputeDriveTorque();
        float driveInertia = DrivetrainInertia;
        bool isSlipping = GetIsAWheelSlipping();

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
        float handbrakeTorque = 0.0f;

        // The handbrake is a cable straight onto this axle: it skips the bias split the
        // hydraulics go through, and there is no ABS on it. Note this is gated on the lever
        // actually being pulled — upstream killed the rear ABS unconditionally here, which is
        // why RearAbsPulseTime and RearAbsSpinDifferenceThreshold have never done anything.
        if (axle.Handbrake && _handbrakeForce > 0.0f)
        {
            handbrakeTorque = _handbrakeForce;
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
        float wheelBrakeTorque = _brakeForce * 0.5f * axle.BrakeBias + handbrakeTorque;
        axle.AppliedSplit = axle.RotationSplit;

        // How locked the tire model should treat this wheel as being. The wheel scales it by
        // its own slip, so this is only ever permission to let go, never a command to.
        float handbrakeAmount = handbrakeTorque > 0.0f ? HandbrakeInput : 0.0f;
        axle.Wheels[0].HandbrakeAmount = handbrakeAmount;
        axle.Wheels[1].HandbrakeAmount = handbrakeAmount;

        rotationSum += axle.Wheels[0].ProcessTorque(
            torque * split, driveInertia, wheelBrakeTorque, allowAbs, delta);
        rotationSum += axle.Wheels[1].ProcessTorque(
            torque * (1.0f - split), driveInertia, wheelBrakeTorque, allowAbs, delta);
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

                    // Fade the assist out under the handbrake. It exists to stop the car
                    // rotating away from its direction of travel, which is precisely what the
                    // player just asked for; left on, it cancels the slide as fast as the rear
                    // tires can start it.
                    StabilityYawTorque *= 1.0f - Mathf.Clamp(
                        HandbrakeInput * HandbrakeStabilitySuppression, 0.0f, 1.0f);
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

        // Per rear wheel, and off the same friction term as the footbrake so the two stay in
        // proportion when the tires or the mass change. Upstream divided by the wheel radius
        // where the footbrake multiplies by it, which left the handbrake on a different scale
        // to everything around it and too weak to reliably lock the axle.
        _maxHandbrakeForce = _maxBrakeForce * 0.5f * HandbrakeForceMultiplier;
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

    /// <summary>
    /// The drive curve used when none was authored: full force off the line, still near full
    /// through the midrange, then falling to nothing at <see cref="TopSpeed"/>.
    ///
    /// The long flat section is what makes a car read as fast — force that starts bleeding away
    /// immediately feels like it's giving up. Reaching exactly 0 at the end makes the top speed
    /// a real ceiling instead of something the car creeps toward forever.
    /// </summary>
    private static Curve BuildDefaultDriveCurve()
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0.0f, 1.0f));
        curve.AddPoint(new Vector2(0.55f, 0.9f));
        curve.AddPoint(new Vector2(0.85f, 0.45f));
        curve.AddPoint(new Vector2(1.0f, 0.0f));
        return curve;
    }
}
