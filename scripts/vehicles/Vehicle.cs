using System.Collections.Generic;
using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// An arcade drift car, built the way Walaber builds them in Parking Garage Rally Circuit:
/// a hovercraft that happens to be wearing a car.
///
/// <b>There is no tire model.</b> No wheels, no axles, no differentials, no gearbox, no brush
/// model. The car is a <see cref="RigidBody3D"/> held up by four <see cref="GroundRay"/> springs,
/// pushed along by one central force, held in line by a second central force, and pointed by one
/// torque. Four ideas do what fifty used to:
///
/// <list type="number">
///   <item><b>Suspension</b> — each ray applies <c>(compression × k) − (closing speed × c)</c>
///     along the chassis' up. The only per-corner force in the game, and the direction matters:
///     along <i>world</i> up it would cancel gravity on a slope exactly.</item>
///   <item><b>Drive</b> — solve for the force that would reach <see cref="TopSpeed"/> in a single
///     step, then clamp it to <see cref="MaxAccelForce"/>. Top speed is a <i>target</i>, not
///     something that emerges from power fighting drag — which is exactly why a boost can
///     overshoot it cleanly.</item>
///   <item><b>Grip</b> — solve for the force that would cancel all sideways velocity in a single
///     step, then keep <see cref="GripFactor"/> of it. That fraction <i>is</i> the grip.</item>
///   <item><b>Steering</b> — no steering angle exists. A PD controller torques the body toward
///     <see cref="HeadingDirection"/>.</item>
/// </list>
///
/// Drive it by writing <see cref="ThrottleInput"/>, <see cref="SteeringInput"/>,
/// <see cref="BrakeInput"/>, <see cref="DriftInput"/> and <see cref="NitroInput"/> every physics
/// step — see <see cref="VehicleInput"/>.
///
/// See docs/vehicle-physics.md for the reasoning, the tuning order, and what changed from the
/// GEVP port this replaced.
/// </summary>
[GlobalClass]
public partial class Vehicle : RigidBody3D
{
    // ---------------------------------------------------------------- Ground rays

    [ExportGroup("Ground Rays")]
    [Export] public GroundRay? FrontLeftWheel { get; set; }
    [Export] public GroundRay? FrontRightWheel { get; set; }
    [Export] public GroundRay? RearLeftWheel { get; set; }
    [Export] public GroundRay? RearRightWheel { get; set; }

    // ---------------------------------------------------------------- Suspension

    /// <summary>Vehicle mass in kg.</summary>
    [ExportGroup("Suspension")]
    [Export] public float VehicleMass { get; set; } = 1200.0f;

    /// <summary>
    /// How far below each ray origin the chassis floats, in metres. Rays sit at the top of the
    /// travel, so this is the whole suspension length.
    /// </summary>
    [Export] public float RideHeight { get; set; } = 0.55f;

    /// <summary>
    /// Spring rate in N/m. At rest each corner carries <c>VehicleMass × g / 4</c> newtons, so the
    /// car settles <c>mass × g / 4 / SpringStrength</c> into its travel.
    ///
    /// <b>Scale this with <c>gravity_scale</c>.</b> The racer runs at 2 g to keep jumps from
    /// floating (see docs/vehicle-physics.md), so the scene sets 84000 rather than the 42000 here
    /// — which is what keeps the static compression at 7 cm rather than letting the car sink to
    /// 14 and lose a third of its suspension travel.
    /// </summary>
    [Export] public float SpringStrength { get; set; } = 42000.0f;

    /// <summary>
    /// Damping in N per m/s. Too low and the car pogos off every kerb; too high and it refuses to
    /// lean and reads as welded down. Roughly 8–10% of <see cref="SpringStrength"/> is a sane
    /// starting point.
    /// </summary>
    [Export] public float SpringDamping { get; set; } = 3600.0f;

    /// <summary>
    /// Cap on the downward pull a ray applies when the ground falls away past
    /// <see cref="RideHeight"/>, in newtons. See <see cref="GroundRay.MaxPullForce"/>.
    ///
    /// This is what holds the car down over a crest, and it is worth a full g or so: ramps are
    /// built from chord facets and the convex creases between them will otherwise throw the car
    /// off the road several times on the way up.
    ///
    /// It does not flatten real jumps, because a ray that has left the road entirely finds
    /// nothing to pull against — the reach of the ray is what separates "following a crease"
    /// from "airborne", not the strength of this.
    /// </summary>
    [Export] public float MaxPullForce { get; set; } = 11000.0f;

    /// <summary>
    /// How far the ground may recede past <see cref="RideHeight"/> before the downward pull has
    /// faded to nothing, in metres. See <see cref="GroundRay.PullFadeDistance"/>.
    ///
    /// This is what lets <see cref="MaxPullForce"/> be strong enough to follow a ramp's creases
    /// without also sucking the car back down off every jump. Raise it and the car sticks to the
    /// road harder over crests; lower it and it flies off them sooner.
    /// </summary>
    [Export] public float PullFadeDistance { get; set; } = 0.12f;

    /// <summary>
    /// Fraction of <see cref="RideHeight"/> the springs may compress before the bump stop starts
    /// taking over, 0..1.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float BumpStopStart { get; set; } = 0.6f;

    /// <summary>
    /// How much stiffer the bump stop is than <see cref="SpringStrength"/> at the end of the
    /// travel. See <see cref="GroundRay.BumpStopStrength"/>.
    ///
    /// Lower this if the car pings off hard landings; raise it if the chassis is still reaching
    /// the road. It matters most at the foot of a ramp, where following the crease at speed needs
    /// more suspension travel than the car actually has.
    /// </summary>
    [Export] public float BumpStopStrength { get; set; } = 12.0f;

    /// <summary>
    /// Raises or lowers the centre of mass relative to the body origin, in metres. The single
    /// biggest lever on how much the car leans and how readily it flips; negative is the safe
    /// direction.
    /// </summary>
    [Export] public float CenterOfMassHeight { get; set; } = -0.25f;

    // ---------------------------------------------------------------- Drive

    /// <summary>Top speed in m/s. Multiply by 3.6 for km/h. A target, not an asymptote.</summary>
    [ExportGroup("Drive")]
    [Export] public float TopSpeed { get; set; } = 55.6f;

    /// <summary>
    /// Top speed in reverse, in m/s. Half of <see cref="TopSpeed"/> — 100 km/h against 200.
    ///
    /// Was 50 km/h, which is what a real car does and is far too slow to be worth having here: the
    /// thing reverse is actually for is getting out of a hairpin you overcooked or off a wall you
    /// are pinned against, while the rest of the field is not waiting for you. Still obviously the
    /// wrong way to travel — half speed and no boost reaches it — but fast enough to be a recovery
    /// rather than a penalty on top of the one you already took.
    /// </summary>
    [Export] public float ReverseTopSpeed { get; set; } = 28.0f;

    /// <summary>
    /// Largest drive force while the throttle is held, in newtons. Divide by
    /// <see cref="VehicleMass"/> for the acceleration — the default on 1200 kg is 15 m/s², which
    /// reaches <see cref="TopSpeed"/> in a bit under four seconds.
    /// </summary>
    [Export] public float MaxAccelForce { get; set; } = 18000.0f;

    /// <summary>
    /// Largest drive force with nothing held, in newtons. Engine braking and drag rolled into one
    /// number, because there is no aero model. Keep it small: coasting should bleed slowly.
    /// </summary>
    [Export] public float MaxCoastForce { get; set; } = 3000.0f;

    /// <summary>
    /// Largest force while the brake is held against the direction of travel, in newtons. Bigger
    /// than <see cref="MaxAccelForce"/>, the way real brakes out-muscle real engines.
    /// </summary>
    [Export] public float MaxBrakeForce { get; set; } = 30000.0f;

    /// <summary>Throttle/brake smoothing, per second. High enough to stay responsive.</summary>
    [Export] public float InputRampSpeed { get; set; } = 12.0f;

    /// <summary>Speed below which holding the brake flips into reverse, in m/s.</summary>
    [Export] public float ReverseEngageSpeed { get; set; } = 1.5f;

    // ---------------------------------------------------------------- Grip

    /// <summary>
    /// Fraction of sideways velocity cancelled per step, per surface. <b>This is the tire
    /// model.</b> 1.0 is on rails, 0.0 is a curling stone.
    ///
    /// It reads as a percentage rather than a force because the force needed is solved for first
    /// — see <see cref="ProcessGrip"/>. Grip is therefore independent of speed and of mass, which
    /// is not physical and is exactly what makes an arcade car predictable.
    /// </summary>
    [ExportGroup("Grip")]
    [Export] public Godot.Collections.Dictionary<string, float> GripFactor { get; set; } = new()
    {
        { SurfaceGroups.Road, 0.90f }, { SurfaceGroups.Dirt, 0.55f },
        { SurfaceGroups.Grass, 0.38f }, { SurfaceGroups.Ice, 0.07f },
    };

    /// <summary>
    /// What <see cref="GripFactor"/> is multiplied by while drifting. Lower is a wider, lazier
    /// drift; at 1.0 the car just turns sideways and keeps gripping.
    /// </summary>
    [Export] public float DriftGripMultiplier { get; set; } = 0.28f;

    /// <summary>
    /// Hardest the tires may pull sideways, in newtons. <b>This is what lets the car slide.</b>
    ///
    /// <see cref="GripFactor"/> removes a *percentage of the sideways velocity every physics
    /// step*, which is a fixed time constant rather than a fixed force — so the harder the car is
    /// thrown sideways, the harder it grips back, without limit. Land sideways at 55 m/s and the
    /// uncapped model asks for 6600 m/s² of lateral deceleration: about 670 g, seven million
    /// newtons, and every bit of the slide gone in twenty milliseconds. The car stops dead in its
    /// own length because a tire was allowed to be infinitely strong.
    ///
    /// Capping it means momentum survives a bad landing: the car keeps sliding and scrubs the
    /// speed off over time, which is what having been thrown sideways should feel like.
    ///
    /// 100000 N is about 8.5 g on the 1200 kg racer. Steady cornering only asks for around 60000,
    /// so ordinary driving never reaches this — it bites on landings, on impacts, and on trying
    /// to corner at boosted speeds, which is the one place it will change how the car drives.
    /// </summary>
    [Export] public float MaxGripForce { get; set; } = 100000.0f;

    // There is deliberately no airborne grip value. Grip is scaled by GroundFraction, so it is
    // exactly zero once the last ray leaves the road — see ProcessGrip. Any of it left running in
    // the air would point along the car's own X axis, which would make rotating the car change
    // the direction its velocity got scrubbed in: an orientation control acting as a thrust
    // control. Off the ground the car is a projectile.

    /// <summary>
    /// Top speed multiplier per surface. Cheaper and more legible than modelling rolling
    /// resistance, and it gives the off-track penalty somewhere obvious to live.
    /// </summary>
    [Export] public Godot.Collections.Dictionary<string, float> SurfaceSpeedMultiplier { get; set; } = new()
    {
        { SurfaceGroups.Road, 1.0f }, { SurfaceGroups.Dirt, 0.82f },
        { SurfaceGroups.Grass, 0.62f }, { SurfaceGroups.Ice, 1.0f },
    };

    // ---------------------------------------------------------------- Steering

    /// <summary>
    /// How fast the heading swings under full steering input, in degrees per second.
    ///
    /// The heading is the direction the car is being <i>asked</i> to point. In Walaber's build it
    /// is the camera rig's forward vector and the car chases the camera; here the vehicle owns it,
    /// because this project's camera has free-look and using it would mean looking around the car
    /// steered it. The maths is otherwise his.
    /// </summary>
    [ExportGroup("Steering")]
    [Export] public float SteeringRate { get; set; } = 190.0f;

    /// <summary>
    /// How fast the heading is dragged back onto the car's actual facing, per second.
    ///
    /// This is what a trailing chase camera does for free in the original, and it is
    /// load-bearing: without it a held stick would wind the heading round forever. With it the
    /// heading settles at a fixed offset ahead of the car and the car turns at a steady rate — so
    /// this and <see cref="SteeringRate"/> together set how tight the car corners.
    /// </summary>
    [Export] public float HeadingRecenterRate { get; set; } = 7.0f;

    /// <summary>Torque per radian of heading error. The P term of the alignment controller.</summary>
    [Export] public float AlignmentTorqueStrength { get; set; } = 90000.0f;

    /// <summary>Torque per rad/s of yaw rate. The D term — what stops it oscillating.</summary>
    [Export] public float AlignmentTorqueDamping { get; set; } = 22000.0f;

    /// <summary>Ceiling on the alignment torque, in Nm.</summary>
    [Export] public float AlignmentTorqueMax { get; set; } = 60000.0f;

    // ---------------------------------------------------------------- Drifting

    /// <summary>
    /// How far off the heading the car is commanded to sit while drifting, in degrees. Walaber's
    /// number is 35, and it is the drift angle you actually see.
    /// </summary>
    [ExportGroup("Drifting")]
    [Export] public float DriftAngle { get; set; } = 35.0f;

    /// <summary>
    /// How much of that angle the player can add or remove with the stick, in degrees. His is 15.
    ///
    /// This is the whole reason a drift here feels driven rather than survived: the car is already
    /// committed to an arc and the stick tightens or opens it. Set it to 0 and a drift becomes a
    /// cutscene.
    /// </summary>
    [Export] public float DriftSteerRange { get; set; } = 15.0f;

    /// <summary>
    /// How much steering input still swings the heading itself during a drift, 0..1. At 0 the
    /// stick works only through <see cref="DriftSteerRange"/>, which is closest to the original.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float DriftHeadingInfluence { get; set; }

    /// <summary>Minimum speed to start a drift, in m/s. Stops a standing car spinning on the spot.</summary>
    [Export] public float DriftMinSpeed { get; set; } = 8.0f;

    /// <summary>Speed at which an in-progress drift gives up, in m/s.</summary>
    [Export] public float DriftBreakSpeed { get; set; } = 5.0f;

    /// <summary>
    /// How far the steering has to be deflected, 0..1, for the drift button to do anything.
    ///
    /// This is what makes a drift a <i>turn</i> rather than a button. The car used to fall back on
    /// its own yaw rate when the stick was centred, so throttle-and-drift with no steering at all
    /// picked a side off whatever rotation happened to be there and slid — which meant the fastest
    /// line through most corners involved never actually steering into them.
    ///
    /// Read at the moment of the press only. Once a drift is running the steering is free to come
    /// back to centre, which is exactly what holding a long slide looks like.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float DriftSteerThreshold { get; set; } = 0.15f;

    /// <summary>
    /// How quickly the drift angle feeds in and out, per second. Instant looks like the car
    /// teleported sideways; this is the snap of it.
    /// </summary>
    [Export] public float DriftBlendSpeed { get; set; } = 6.0f;

    /// <summary>
    /// Seconds of drift needed for each boost tier. Release before the first entry and the drift
    /// pays nothing; the array's length is how many tiers there are.
    /// </summary>
    [Export] public float[] DriftTierTimes { get; set; } = { 0.55f, 1.3f, 2.2f };

    // ---------------------------------------------------------------- Boost

    /// <summary>
    /// Speed added on top of <see cref="TopSpeed"/> by each drift tier, in m/s, indexed to match
    /// <see cref="DriftTierTimes"/>.
    ///
    /// This is the mechanic in one number. Because <see cref="TopSpeed"/> is a target rather than
    /// a balance point, raising it is all it takes to send the car past its normal ceiling — no
    /// extra shove, nothing to fight, and it holds there for as long as the boost lasts.
    /// </summary>
    [ExportGroup("Boost")]
    [Export] public float[] BoostTierSpeed { get; set; } = { 8.0f, 15.0f, 24.0f };

    /// <summary>Seconds each tier's boost lasts, indexed to match <see cref="DriftTierTimes"/>.</summary>
    [Export] public float[] BoostTierDuration { get; set; } = { 1.0f, 1.7f, 2.6f };

    /// <summary>
    /// Extra drive force while boosting, in newtons, on top of <see cref="MaxAccelForce"/>. The
    /// raised ceiling alone would have the car drift up to its new top speed politely; this is
    /// what makes a boost land as a shove.
    /// </summary>
    [Export] public float BoostAccelForce { get; set; } = 14000.0f;

    /// <summary>
    /// Ceiling on stacked boost speed, in m/s. Reached by chaining, never by one drift.
    ///
    /// <b>Boosts are additive.</b> Land a drift while a boost is still burning and its speed adds
    /// to what is already there rather than replacing it, so a driver who keeps chaining ends up
    /// a very long way over <see cref="TopSpeed"/> — which is the point. The default of 45 on the
    /// racer's 55.6 puts the hard ceiling near 360 km/h. Raise it if chains should run further,
    /// lower it if the track can't take it.
    /// </summary>
    [Export] public float MaxBoostSpeed { get; set; } = 45.0f;

    /// <summary>
    /// How fast the boost bleeds off once every burst has expired, in m/s per second. Slow enough
    /// that running down from a big chain is a moment rather than a switch.
    ///
    /// <b>Paused for as long as a drift is held.</b> Speed is only additive if the next boost lands
    /// while there is still something to add to, and at 14 m/s a tier-1 boost's 8 m/s was gone
    /// 0.57 s after its burst ended — less than the 0.55 s of drifting needed to earn the next one
    /// plus any time at all to get into it. So a chain that the counter happily accepted paid
    /// nothing: <see cref="ChainCount"/> went up and <see cref="BoostSpeed"/> started again from
    /// zero. Freezing the bleed while the player is mid-drift is what makes the link they are
    /// working on worth anything, and it costs no tuning anywhere else.
    /// </summary>
    [Export] public float BoostDecayRate { get; set; } = 14.0f;

    /// <summary>
    /// Grace window after a boost ends during which a fresh drift still counts as a chain, in
    /// seconds. Without it a chain would demand frame-perfect re-entry.
    /// </summary>
    [Export] public float ChainGraceTime { get; set; } = 0.5f;

    // ---------------------------------------------------------------- Nitro

    /// <summary>
    /// Boost charges the player starts a run with, spent one per press and refilled only by
    /// <see cref="ResetNitro"/>.
    ///
    /// Nitro and drift boosts feed the same speed bonus, so they stack with each other and with a
    /// chain exactly as two drift boosts would.
    /// </summary>
    [ExportGroup("Nitro")]
    [Export] public int NitroCharges { get; set; } = 5;

    /// <summary>Speed a nitro charge adds, in m/s.</summary>
    [Export] public float NitroSpeed { get; set; } = 18.0f;

    /// <summary>How long one charge burns for, in seconds.</summary>
    [Export] public float NitroDuration { get; set; } = 1.5f;

    /// <summary>Dead time after a charge before another can be spent, in seconds.</summary>
    [Export] public float NitroCooldown { get; set; } = 0.4f;

    // ---------------------------------------------------------------- Hop

    /// <summary>
    /// Change in upward speed one hop is worth, in m/s. Applied as an impulse scaled by
    /// <see cref="VehicleMass"/>, so it is a velocity change and every car hops the same height
    /// whatever it weighs — the same bargain the track's launch pads make.
    ///
    /// Height is <c>impulse² / 2g</c>, and this project runs at 2 g, so the default 8 m/s is a hop
    /// of about 1.6 m. Enough to clear a log, unstick the car off a crease, or get the nose over a
    /// kerb; nowhere near enough to skip a hazard, which is the line it must not cross. 0 disables
    /// it outright.
    /// </summary>
    [ExportGroup("Hop")]
    [Export] public float HopImpulse { get; set; } = 8.0f;

    /// <summary>
    /// Dead time between hops, in seconds. Landing and immediately hopping again is the thing this
    /// stops: without it a player can bunny-hop a whole washboard, and the tiles that work by
    /// unsettling the car stop working.
    /// </summary>
    [Export] public float HopCooldown { get; set; } = 0.35f;

    // ---------------------------------------------------------------- Airborne

    /// <summary>
    /// Fastest the car flips in the air, in degrees per second. Brake pitches the nose up,
    /// throttle pitches it down — flight-stick sense, where pushing forward drops the nose.
    ///
    /// Those two pedals do nothing at all in the air — drive force is scaled by
    /// <see cref="GroundFraction"/>, which is zero — so they are free, and they are the pair the
    /// player's thumbs are already on.
    ///
    /// <b>A rate, not a torque.</b> A torque accelerates for as long as it is held, so the flip
    /// you get depends on how long you were in the air and the car can wind itself into a spin it
    /// cannot recover from. Driving toward a rate means the number below <i>is</i> the fastest it
    /// will ever go round, and releasing brings it back to still. 140°/s is a bit over two
    /// seconds per full rotation.
    /// </summary>
    [ExportGroup("Airborne")]
    [Export] public float AirPitchRate { get; set; } = 140.0f;

    /// <summary>
    /// Fastest the car turns left and right in the air, in degrees per second, from the steering
    /// input.
    ///
    /// Slower than the pitch rate on purpose: yaw in the air is for lining up a landing, not for
    /// tricks, and it is the axis a player is most likely to be holding by accident on the way
    /// off a ramp.
    /// </summary>
    [Export] public float AirYawRate { get; set; } = 100.0f;

    /// <summary>
    /// How hard the car is driven toward its target rotation rate, in Nm per rad/s of error.
    ///
    /// Sets how quickly a flip spins up and how quickly it stops, without changing how fast it
    /// ends up going. Higher is snappier and more arcade; lower feels like the car has mass.
    /// </summary>
    [Export] public float AirRotationGain { get; set; } = 6000.0f;

    /// <summary>
    /// Thrust along the nose while a boost is burning and the car is off the ground, in newtons.
    /// 0 restores the pure projectile.
    ///
    /// The one deliberate hole in "in the air the car is a projectile". Drive force is scaled by
    /// <see cref="GroundFraction"/> and so is exactly zero up there — which meant a nitro fired in
    /// mid-air spent a charge, lit the flames, raised the speed target and then did nothing at all,
    /// because there was no force left to deliver any of it. Paying for nothing is worse than not
    /// being allowed to pay.
    ///
    /// Matched to <see cref="BoostAccelForce"/>, so a charge spent in the air is worth about what
    /// one spent on the ground is. The rest of the projectile stands: no grip, no drive, and only
    /// the rotation rates from the pedals. Pitching the car <i>does</i> now steer this thrust, and
    /// that is the point — nose down to dive onto a landing you were going to miss, nose up to hold
    /// a jump out. It is aim, and it costs a charge.
    /// </summary>
    [Export] public float AirNitroForce { get; set; } = 14000.0f;

    /// <summary>
    /// How long the airborne thrust lasts after a nitro is fired, in seconds. Deliberately far
    /// shorter than <see cref="NitroDuration"/>: on the ground a boost is a run you hold, and in
    /// the air it is a punch that changes where you are going to land. A second and a half of free
    /// thrust up there is a flying car.
    ///
    /// Its own timer rather than a slice of the burst, and that has a second effect worth being
    /// explicit about: only <see cref="TryActivateNitro"/> sets it, so a <i>drift</i> boost still
    /// burning as the car leaves a ramp gives no thrust at all. That is the rule the exception was
    /// argued on — this force is allowed to break the projectile because it is opt-in and paid
    /// for, and a drift boost carried into a jump is neither.
    ///
    /// The clock starts when the charge is spent, wherever the car is. Fire one on the road and
    /// the air window has burned away long before the next ramp, which is correct: that thrust was
    /// already spent pushing you along the ground.
    /// </summary>
    [Export] public float AirNitroDuration { get; set; } = 0.35f;

    /// <summary>
    /// Hard ceiling on speed along gravity, in m/s. Not a feel knob — it is the guard rail that
    /// stops a fall off the edge of the board outrunning the collision solver. 0 disables it.
    /// </summary>
    [Export] public float MaxFallSpeed { get; set; } = 65.0f;

    // ---------------------------------------------------------------- Inputs

    /// <summary>0..1 throttle. Written by the controller every physics step.</summary>
    public float ThrottleInput;

    /// <summary>−1..1 steering; positive steers left, matching the project's existing convention.</summary>
    public float SteeringInput;

    /// <summary>0..1 brake / reverse.</summary>
    public float BrakeInput;

    /// <summary>Whether the drift button is held. Replaces the old handbrake.</summary>
    public bool DriftInput;

    /// <summary>Whether the nitro button is held. The rising edge spends a charge.</summary>
    public bool NitroInput;

    /// <summary>Whether the hop button is held. The rising edge springs one hop.</summary>
    public bool HopInput;

    /// <summary>
    /// Mirrors <see cref="DriftInput"/>, so anything still phrased in terms of a handbrake keeps
    /// working. The lever is gone; the button that replaced it does more.
    /// </summary>
    public float HandbrakeInput
    {
        get => DriftInput ? 1.0f : 0.0f;
        set => DriftInput = value > 0.5f;
    }

    // ---------------------------------------------------------------- Reported state

    public bool IsVehicleReady { get; private set; }

    /// <summary>Every ground ray, front axle first.</summary>
    public readonly List<GroundRay> WheelArray = new();

    /// <summary>Velocity in the body's own frame. −Z is forward.</summary>
    public Vector3 LocalVelocity { get; private set; } = Vector3.Zero;

    /// <summary>Speed in m/s. Multiply by 3.6 for km/h.</summary>
    public float Speed { get; private set; }

    /// <summary>Signed speed along the nose, in m/s. Negative means travelling backwards.</summary>
    public float ForwardSpeed { get; private set; }

    /// <summary>Smoothed throttle, 0..1.</summary>
    public float ThrottleAmount { get; private set; }

    /// <summary>Smoothed brake, 0..1.</summary>
    public float BrakeAmount { get; private set; }

    /// <summary>1 forward, −1 reverse. Only picks which way drive force points; there is no gearbox.</summary>
    public int CurrentGear { get; private set; } = 1;

    /// <summary>Speed as a fraction of the unboosted <see cref="TopSpeed"/>. Drives the engine note.</summary>
    public float SpeedFraction { get; private set; }

    /// <summary>Where the car is being asked to point, in world space, flat to the ground plane.</summary>
    public Vector3 HeadingDirection { get; private set; } = Vector3.Forward;

    /// <summary>Signed angle from the car's nose to <see cref="HeadingDirection"/>, in radians.</summary>
    public float HeadingError { get; private set; }

    /// <summary>Alignment torque applied this step, in Nm. For the debug overlay.</summary>
    public float SteerTorque { get; private set; }

    /// <summary>True when no ray reached anything.</summary>
    public bool IsAirborne { get; private set; }

    /// <summary>Seconds since the last ray left the road. Zero while any corner is down.</summary>
    public float AirTime { get; private set; }

    /// <summary>How many of the four rays are touching something.</summary>
    public int GroundedRayCount { get; private set; }

    /// <summary>
    /// Fraction of the car that is on the road, 0..1.
    ///
    /// Drive and grip scale with this rather than switching off the moment the last ray leaves.
    /// That matters far more than it sounds: ramps are built from chord facets, and the creases
    /// between them unstick a car at speed. Cutting drive outright there means a ramp stops
    /// driving the instant it starts working, which is exactly what an all-or-nothing check did.
    /// </summary>
    public float GroundFraction { get; private set; }

    /// <summary>Effective grip this step, after surface and drift. 1 is on rails, 0 is ice.</summary>
    public float CurrentGrip { get; private set; }

    /// <summary>Sideways speed in m/s. What the skid and squeal effects key off.</summary>
    public float LateralSpeed { get; private set; }

    /// <summary>Surface under the car, read off the rear rays. See <see cref="SurfaceGroups"/>.</summary>
    public string SurfaceType { get; private set; } = SurfaceGroups.Road;


    // ---- Drift ----

    /// <summary>0 when not drifting, otherwise +1 or −1 for the direction of the slide.</summary>
    public int DriftDirection { get; private set; }

    /// <summary>True while a drift is being held.</summary>
    public bool IsDrifting => DriftDirection != 0;

    /// <summary>How far the drift angle is blended in, 0..1. Drives the visual pose.</summary>
    public float DriftBlend { get; private set; }

    /// <summary>Seconds the current drift has been held.</summary>
    public float DriftTime { get; private set; }

    /// <summary>Tier the current drift has earned so far: 0 for none, up to <c>DriftTierTimes.Length</c>.</summary>
    public int DriftTier { get; private set; }

    // ---- Boost ----

    /// <summary>Speed currently added on top of <see cref="TopSpeed"/>, in m/s.</summary>
    public float BoostSpeed { get; private set; }

    /// <summary>Seconds left on the burst still running, 0 when none is.</summary>
    public float BoostTimeRemaining { get; private set; }

    /// <summary>
    /// True while a burst is running. Virtual because a networked car is not simulated on every
    /// machine — see <c>RacerController.IsBoosting</c>, which answers from the wire for a car
    /// somebody else is driving. The physics below deliberately reads <see cref="BurstActive"/>
    /// instead, so an override can never feed a replicated flag back into the simulation.
    /// </summary>
    public virtual bool IsBoosting => BurstActive;

    /// <summary>Raw simulation state: is a burst actually running on this machine.</summary>
    private bool BurstActive => BoostTimeRemaining > 0.0f;

    /// <summary>How many boosts deep the current chain is. Resets once the grace window lapses.</summary>
    public int ChainCount { get; private set; }

    /// <summary>
    /// Alias, so every existing effect — exhaust flames, camera FOV, HUD — lights up for drift
    /// boosts as well as for nitro, which is what an arcade racer wants.
    /// </summary>
    public bool IsNitroActive => IsBoosting;

    /// <summary>How much of the current burst is left, 1 at ignition down to 0.</summary>
    public float NitroFraction { get; private set; }

    /// <summary>Charges left in this run.</summary>
    public int NitroChargesRemaining { get; private set; }

    /// <summary>Fires when a boost starts, with the tier (0 for nitro) and the chain depth.</summary>
    [Signal] public delegate void BoostStartedEventHandler(int tier, int chainCount);

    /// <summary>Fires when every burst has expired.</summary>
    [Signal] public delegate void BoostEndedEventHandler();

    /// <summary>Fires when a nitro charge is spent, with the number left.</summary>
    [Signal] public delegate void NitroFiredEventHandler(int chargesRemaining);

    /// <summary>Fires when a drift ends, with the tier it earned. 0 means it earned nothing.</summary>
    [Signal] public delegate void DriftEndedEventHandler(int tier);

    // ---------------------------------------------------------------- Internals

    private float _headingYaw;
    private float _driftBlendTarget;
    private float _driftSteerAngle;
    private float _chainWindow;
    private float _nitroLockout;
    private bool _nitroWasHeld;

    /// <summary>Seconds of airborne thrust still owed by the last nitro. See
    /// <see cref="AirNitroDuration"/>.</summary>
    private float _airNitroRemaining;

    private bool _driftWasHeld;
    private bool _hopWasHeld;

    /// <summary>Seconds until another hop may be sprung. See <see cref="HopCooldown"/>.</summary>
    private float _hopCooldown;
    private float _burstLength = 1.0f;
    private Vector3 _gravity = Vector3.Down * 9.8f;

    private Vector3 GravityDirection => _gravity.LengthSquared() > 0.0f ? _gravity.Normalized() : Vector3.Down;

    public override void _Ready()
    {
        Initialize();
    }

    /// <summary>
    /// Collect the rays, push the suspension setup into them and seed the heading. Safe to call
    /// again after changing the tuning at runtime.
    /// </summary>
    public void Initialize()
    {
        if (FrontLeftWheel is not { } fl || FrontRightWheel is not { } fr
            || RearLeftWheel is not { } rl || RearRightWheel is not { } rr)
        {
            GD.PushError($"[Vehicle] {Name}: all four ground rays must be assigned. Not simulating.");
            return;
        }

        Mass = VehicleMass;
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        CenterOfMass = new Vector3(0.0f, CenterOfMassHeight, 0.0f);

        WheelArray.Clear();
        WheelArray.Add(fl);
        WheelArray.Add(fr);
        WheelArray.Add(rl);
        WheelArray.Add(rr);

        // Which corners look like they steer is a WheelVisual's business, not a ray's — the rays
        // never turn, because there is no steering angle in the physics to turn them by.
        foreach (GroundRay ray in WheelArray)
        {
            ray.RestLength = RideHeight;
            ray.SpringStrength = SpringStrength;
            ray.SpringDamping = SpringDamping;
            ray.MaxPullForce = MaxPullForce;
            ray.BumpStopStart = BumpStopStart;
            ray.BumpStopStrength = BumpStopStrength;
            ray.PullFadeDistance = PullFadeDistance;

            // Reaches past the ride height, so the ray still finds the road as the car tops a
            // crest and the pull term has something to work against.
            ray.TargetPosition = new Vector3(0.0f, -(RideHeight * 1.5f), 0.0f);
            ray.AddException(this);
        }

        _headingYaw = GlobalRotation.Y;
        HeadingDirection = -GlobalTransform.Basis.Z;

        ResetNitro();
        IsVehicleReady = true;
    }

    public override void _IntegrateForces(PhysicsDirectBodyState3D state)
    {
        _gravity = state.TotalGravity;

        if (MaxFallSpeed <= 0.0f)
            return;

        // A hard clamp rather than a drag force: a force that merely opposes the fall still lets
        // velocity creep past the number asked for. Only the component along gravity is touched,
        // so a car flung sideways off a ramp keeps every bit of its horizontal speed.
        Vector3 down = GravityDirection;
        float fallSpeed = state.LinearVelocity.Dot(down);
        if (fallSpeed > MaxFallSpeed)
            state.LinearVelocity -= down * (fallSpeed - MaxFallSpeed);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsVehicleReady)
            return;

        var dt = (float)delta;

        ReadState();
        ProcessSuspension();
        ProcessInputRamp(dt);
        ProcessDrift(dt);
        ProcessBoost(dt);
        ProcessHop(dt);
        ProcessDrive(dt);
        ProcessGrip(dt);
        ProcessSteering(dt);
        ProcessAirborne(dt);
    }

    /// <summary>
    /// Velocity of a point on this body in world space, including the part that comes from
    /// spinning. Each ground ray damps against its own corner rather than the centre of mass —
    /// that difference is what damps roll and pitch instead of only bounce.
    /// </summary>
    public Vector3 VelocityAtPoint(Vector3 worldPoint)
        => LinearVelocity + AngularVelocity.Cross(worldPoint - GlobalTransform * CenterOfMass);

    private void ReadState()
    {
        LocalVelocity = GlobalTransform.Basis.Transposed() * LinearVelocity;
        Speed = LinearVelocity.Length();
        ForwardSpeed = -LocalVelocity.Z;
        LateralSpeed = LocalVelocity.X;
        SpeedFraction = TopSpeed > 0.0f ? Mathf.Clamp(Speed / TopSpeed, 0.0f, 1.0f) : 0.0f;
    }

    private void ProcessSuspension()
    {
        GroundedRayCount = 0;

        foreach (GroundRay ray in WheelArray)
        {
            ray.ApplyGroundForce(this);
            if (ray.IsGrounded)
                GroundedRayCount++;
        }

        IsAirborne = GroundedRayCount == 0;
        GroundFraction = WheelArray.Count > 0 ? (float)GroundedRayCount / WheelArray.Count : 0.0f;

        // Off a rear ray rather than averaged: the rears are what the car drives on, and one
        // dictionary lookup beats blending four values that are nearly always the same number.
        if (RearLeftWheel is { IsGrounded: true } rear)
            SurfaceType = rear.SurfaceType;
        else if (RearRightWheel is { IsGrounded: true } other)
            SurfaceType = other.SurfaceType;
    }

    private void ProcessInputRamp(float delta)
    {
        float t = 1.0f - Mathf.Exp(-InputRampSpeed * delta);
        ThrottleAmount = Mathf.Lerp(ThrottleAmount, Mathf.Clamp(ThrottleInput, 0.0f, 1.0f), t);
        BrakeAmount = Mathf.Lerp(BrakeAmount, Mathf.Clamp(BrakeInput, 0.0f, 1.0f), t);
    }

    // ---------------------------------------------------------------- Drift

    /// <summary>
    /// Start, hold and end a drift, and pay a boost out when one ends having earned it.
    ///
    /// A drift is a <i>state</i>, not a consequence: pressing the button while turning commits the
    /// car to an angle, and the grip model plays along by handing most of its grip back. Nothing
    /// here waits for the car to break traction on its own, which is why it triggers when the
    /// player asks rather than when the physics happens to allow it.
    /// </summary>
    private void ProcessDrift(float delta)
    {
        bool wantsDrift = DriftInput;

        if (IsDrifting)
        {
            if (!wantsDrift || Speed < DriftBreakSpeed)
                EndDrift();
        }
        else if (wantsDrift && !_driftWasHeld && Speed >= DriftMinSpeed && !IsAirborne
                 && Mathf.Abs(SteeringInput) >= DriftSteerThreshold)
        {
            // Direction comes from the stick at the moment of the press, and there has to be one:
            // a drift is a corner taken sideways, so it is entered by turning into the corner and
            // committing, not by holding two buttons in a straight line. See DriftSteerThreshold.
            DriftDirection = SteeringInput > 0.0f ? 1 : -1;
            DriftTime = 0.0f;
            DriftTier = 0;

            // Entering a drift while a boost is alive — or just after one — is what makes a
            // chain. See MaxBoostSpeed.
            if (BurstActive || _chainWindow > 0.0f)
                ChainCount++;
            else
                ChainCount = 0;
        }

        _driftWasHeld = wantsDrift;

        if (IsDrifting)
        {
            DriftTime += delta;
            DriftTier = TierFor(DriftTime);
            _driftBlendTarget = 1.0f;
            _driftSteerAngle = Mathf.Clamp(SteeringInput, -1.0f, 1.0f) * Mathf.DegToRad(DriftSteerRange);
        }
        else
        {
            _driftBlendTarget = 0.0f;
            _driftSteerAngle = 0.0f;
        }

        DriftBlend = Mathf.Lerp(DriftBlend, _driftBlendTarget,
                                1.0f - Mathf.Exp(-DriftBlendSpeed * delta));
    }

    private void EndDrift()
    {
        int tier = DriftTier;
        DriftDirection = 0;
        DriftTime = 0.0f;
        DriftTier = 0;

        EmitSignal(SignalName.DriftEnded, tier);

        if (tier > 0)
            GrantBoost(BoostTierSpeed[tier - 1], BoostTierDuration[tier - 1], tier);
        else if (!BurstActive)
            ChainCount = 0;
    }

    /// <summary>Which boost tier a drift of this length has earned. 0 means none.</summary>
    private int TierFor(float heldFor)
    {
        var tier = 0;
        int count = Mathf.Min(DriftTierTimes.Length,
                              Mathf.Min(BoostTierSpeed.Length, BoostTierDuration.Length));

        for (var i = 0; i < count; i++)
        {
            if (heldFor >= DriftTierTimes[i])
                tier = i + 1;
        }

        return tier;
    }

    // ---------------------------------------------------------------- Boost

    /// <summary>
    /// Add a boost on top of whatever is already burning.
    ///
    /// <b>Additive, deliberately.</b> The speed goes on top of the current bonus rather than
    /// replacing it, and the timer takes the longer of the two rather than restarting — so a chain
    /// of three good drifts really does stack three lots of speed and leaves the car far past
    /// <see cref="TopSpeed"/>. <see cref="MaxBoostSpeed"/> is the only thing that stops it.
    /// </summary>
    public void GrantBoost(float speed, float duration, int tier)
    {
        BoostSpeed = Mathf.Min(BoostSpeed + speed, MaxBoostSpeed);
        BoostTimeRemaining = Mathf.Max(BoostTimeRemaining, duration);
        _burstLength = Mathf.Max(BoostTimeRemaining, 0.001f);
        _chainWindow = 0.0f;

        EmitSignal(SignalName.BoostStarted, tier, ChainCount);
    }

    private void ProcessBoost(float delta)
    {
        if (_chainWindow > 0.0f)
        {
            _chainWindow = Mathf.Max(_chainWindow - delta, 0.0f);
            if (_chainWindow <= 0.0f && !BurstActive && !IsDrifting)
                ChainCount = 0;
        }

        if (_nitroLockout > 0.0f)
            _nitroLockout = Mathf.Max(_nitroLockout - delta, 0.0f);

        if (_airNitroRemaining > 0.0f)
            _airNitroRemaining = Mathf.Max(_airNitroRemaining - delta, 0.0f);

        // Edge, not level: holding the button spends one charge, not all five.
        if (NitroInput && !_nitroWasHeld)
            TryActivateNitro();
        _nitroWasHeld = NitroInput;

        if (BurstActive)
        {
            BoostTimeRemaining = Mathf.Max(BoostTimeRemaining - delta, 0.0f);
            if (!BurstActive)
            {
                _chainWindow = ChainGraceTime;
                EmitSignal(SignalName.BoostEnded);
            }
        }
        else if (BoostSpeed > 0.0f && !IsDrifting)
        {
            // Bleed rather than drop: coming off a big chain should be the car running down, not
            // the speedometer being switched off.
            //
            // Held off entirely while a drift is being held. A drift is the player working on the
            // next link, and draining the last one while they earn it made chaining a fight
            // against the clock rather than against the corner — see BoostDecayRate.
            BoostSpeed = Mathf.Max(BoostSpeed - BoostDecayRate * delta, 0.0f);
        }

        NitroFraction = Mathf.Clamp(BoostTimeRemaining / _burstLength, 0.0f, 1.0f);
    }

    /// <summary>
    /// Spend a nitro charge if there is one and the dead time has passed. Returns whether one was
    /// lit, so a caller can play the empty click instead. Public so a pickup, a scripted sequence
    /// or an AI driver can fire one without faking a button press.
    /// </summary>
    public bool TryActivateNitro()
    {
        if (NitroChargesRemaining <= 0 || _nitroLockout > 0.0f)
            return false;

        NitroChargesRemaining--;
        _nitroLockout = NitroDuration + NitroCooldown;
        _airNitroRemaining = AirNitroDuration;
        GrantBoost(NitroSpeed, NitroDuration, 0);
        EmitSignal(SignalName.NitroFired, NitroChargesRemaining);
        return true;
    }

    // ---------------------------------------------------------------- Hop

    /// <summary>Fires when a hop is sprung, for whatever wants to make a noise about it.</summary>
    [Signal] public delegate void HoppedEventHandler();

    /// <summary>
    /// Spring the car straight up, if it has anything to push off. Returns whether one was sprung.
    ///
    /// Along <b>world</b> up rather than the chassis', so a hop is the same hop on a banked corner
    /// as on the flat. Chassis-up would fire the car off the side of a slope at whatever angle it
    /// happened to be sitting at, which is a launch pad, not a hop.
    ///
    /// Needs at least one wheel down. In the air it does nothing — repeatable upward impulses with
    /// nothing to push against is a helicopter, and an airborne car has exactly one sanctioned way
    /// to change its velocity (see <see cref="AirNitroForce"/>).
    ///
    /// Public alongside <see cref="TryActivateNitro"/> and for the same reason: a pickup or a
    /// scripted sequence can spring one without faking a button press.
    /// </summary>
    public bool TryHop()
    {
        if (HopImpulse <= 0.0f || IsAirborne || _hopCooldown > 0.0f)
            return false;

        _hopCooldown = HopCooldown;
        ApplyCentralImpulse(Vector3.Up * (HopImpulse * Mass));
        EmitSignal(SignalName.Hopped);
        return true;
    }

    private void ProcessHop(float delta)
    {
        if (_hopCooldown > 0.0f)
            _hopCooldown = Mathf.Max(_hopCooldown - delta, 0.0f);

        // Edge, not level: holding the button is one hop, not one per frame the wheels are down.
        if (HopInput && !_hopWasHeld)
            TryHop();
        _hopWasHeld = HopInput;
    }

    /// <summary>Refill the charges and cancel any boost. Call when a run starts.</summary>
    public void ResetNitro()
    {
        NitroChargesRemaining = NitroCharges;
        BoostSpeed = 0.0f;
        BoostTimeRemaining = 0.0f;
        _airNitroRemaining = 0.0f;
        ChainCount = 0;
        _chainWindow = 0.0f;
        _nitroLockout = 0.0f;
        _nitroWasHeld = false;
    }

    // ---------------------------------------------------------------- Drive

    /// <summary>
    /// Walaber's acceleration model: work out the force that would land the car exactly on its
    /// target speed in one physics step, then refuse to apply more than a fixed amount of it.
    ///
    /// The clamp is the entire drivetrain. Well below the target the car pushes at
    /// <see cref="MaxAccelForce"/> flat out; as it closes, the unclamped force falls under that on
    /// its own and the car eases in and holds. No curve, no gears, no drag to balance — and
    /// because the target is just a number, a boost that raises it works instantly and exactly.
    /// </summary>
    private void ProcessDrive(float delta)
    {
        // Reverse only once nearly stopped, so stamping the brake at speed brakes.
        if (BrakeAmount > 0.1f && ForwardSpeed < ReverseEngageSpeed)
            CurrentGear = -1;
        else if (ThrottleAmount > 0.1f || ForwardSpeed > ReverseEngageSpeed)
            CurrentGear = 1;

        float surface = SurfaceSpeedMultiplier.TryGetValue(SurfaceType, out float m) ? m : 1.0f;

        Vector3 forward = -GlobalTransform.Basis.Z;
        float currentForwardSpeed = forward.Dot(LinearVelocity);

        float desiredSpeed = CurrentGear == -1
            ? -BrakeAmount * ReverseTopSpeed * surface
            : ThrottleAmount * (TopSpeed + BoostSpeed) * surface;

        // The acceleration that would close the whole gap this step, and the force behind it.
        float accelForce = (desiredSpeed - currentForwardSpeed) / delta * Mass;

        // Asymmetric, and that asymmetry is load-bearing: how hard the car may be pushed *along*
        // its nose is a different question from how hard it may be pushed *against* it.
        //
        // A single limit means the throttle brakes you. Over the target speed the solve goes
        // negative, and with one limit that negative is as strong as the engine — so running
        // downhill under power the car fights gravity with 18000 N to pin itself at exactly top
        // speed. Retardation is capped at engine-braking instead unless the player is actually on
        // the brake, so a hill can run the car past its own top speed, and a boost bleeds off
        // smoothly rather than being slammed back down.
        float forwardLimit = MaxCoastForce;
        float backwardLimit = MaxCoastForce;

        if (CurrentGear == -1)
        {
            if (BrakeAmount > 0.05f)
                backwardLimit = MaxAccelForce;
        }
        else
        {
            if (ThrottleAmount > 0.05f)
                forwardLimit = MaxAccelForce + (BurstActive ? BoostAccelForce : 0.0f);

            if (BrakeAmount > 0.05f && currentForwardSpeed > 0.0f)
                backwardLimit = MaxBrakeForce;
        }

        // Scaled by how much of the car is actually on the road, not switched off the moment the
        // last ray leaves it: a car skimming a crease with two corners down keeps half its drive
        // instead of losing all of it. Ramps are chord facets, and the creases unstick the car
        // constantly.
        //
        // Fully airborne this reaches exactly zero and the force is skipped outright. There is
        // nothing to push against, and this force points along the nose — leaving any of it
        // running would mean pitching the car in mid-air changed its velocity.
        forwardLimit *= GroundFraction;
        backwardLimit *= GroundFraction;
        if (forwardLimit <= 0.0f && backwardLimit <= 0.0f)
            return;

        ApplyCentralForce(forward * Mathf.Clamp(accelForce, -backwardLimit, forwardLimit));
    }

    // ---------------------------------------------------------------- Grip

    /// <summary>
    /// The tire model, in full: find the force that would cancel every bit of sideways velocity
    /// this step, then apply a percentage of it.
    ///
    /// At 1.0 the car cannot slide at all. At 0.9 × <see cref="DriftGripMultiplier"/> on tarmac it
    /// keeps most of its sideways speed and slews. There is no slip curve, no load sensitivity and
    /// no friction budget shared with acceleration — which means grip does not sag at speed, and a
    /// drift holds for exactly as long as the player holds it.
    /// </summary>
    private void ProcessGrip(float delta)
    {
        float grip = GripFactor.TryGetValue(SurfaceType, out float g) ? g : 0.9f;

        // On the drift's own ramp, so grip returns at the rate the car straightens rather than
        // snapping back the instant the button comes up.
        grip *= Mathf.Lerp(1.0f, DriftGripMultiplier, DriftBlend);

        // Scaled by how much of the car is on the road, which also makes it exactly zero in the
        // air. A car crossing a crease on a ramp should lose some grip, not all of it; a car that
        // has left the road entirely should have none.
        grip *= GroundFraction;

        CurrentGrip = grip;

        // **Nothing acts sideways in the air.** This force points along the car's own X axis, so
        // any of it left running while airborne would mean rotating the car changed which
        // direction its velocity got scrubbed in — turning an orientation control into a
        // thrust control. Off the ground the car is a projectile: gravity, and nothing else.
        if (grip <= 0.0f)
            return;

        Vector3 side = GlobalTransform.Basis.X;
        float instantSideAccel = -side.Dot(LinearVelocity) / delta;

        // Clamped, because the solve above is a *time constant*, not a force: it asks for whatever
        // it takes to cancel the sideways velocity this step, so the faster the car is thrown
        // sideways the harder it grips back. Without the cap a tire is infinitely strong and a
        // car that lands sideways stops in its own length. See MaxGripForce.
        // Scaled by contact as well: two corners on the road can hold half of what four can, so a
        // car touching down on one wheel slides further before it hooks up.
        float cap = MaxGripForce * GroundFraction;
        float sideForce = Mathf.Clamp(instantSideAccel * Mass * grip, -cap, cap);
        ApplyCentralForce(side * sideForce);
    }

    // ---------------------------------------------------------------- Steering

    /// <summary>
    /// Point the car at its heading with a PD controller, and offset that heading while drifting.
    ///
    /// The car has no steering angle and no front axle. What it has is a direction it is being
    /// asked to face, a torque proportional to how far off it is, and damping proportional to how
    /// fast it is already turning. That is why it answers the stick the same at 30 km/h and at
    /// 300 — nothing here scales with speed or with grip.
    /// </summary>
    private void ProcessSteering(float delta)
    {
        float steer = Mathf.Clamp(SteeringInput, -1.0f, 1.0f);

        // While drifting the stick mostly works through the drift offset instead, so the player is
        // trimming the arc rather than fighting to leave it.
        float authority = IsDrifting ? DriftHeadingInfluence : 1.0f;
        _headingYaw += steer * Mathf.DegToRad(SteeringRate) * authority * delta;

        // The heading trails the car, which is what a chase camera does for free in Walaber's
        // build. Without it a held stick would wind the heading round forever instead of settling
        // at a fixed offset — see HeadingRecenterRate.
        _headingYaw = Mathf.LerpAngle(_headingYaw, GlobalRotation.Y,
                                      1.0f - Mathf.Exp(-HeadingRecenterRate * delta));

        var heading = new Vector3(-Mathf.Sin(_headingYaw), 0.0f, -Mathf.Cos(_headingYaw));

        // The drift angle plus the player's trim inside it — Walaber's
        // `deg_to_rad(35.0 * _drift_dir) + input_angle`, ramped by the drift blend.
        if (DriftBlend > 0.001f)
        {
            float driftAngle = Mathf.DegToRad(DriftAngle) * DriftDirection * DriftBlend;
            heading = heading.Rotated(Vector3.Up, driftAngle + _driftSteerAngle * DriftBlend);
        }

        HeadingDirection = heading;

        // Flattened, so a car on a banking is still asked to turn about world up rather than
        // about its own tilted roof.
        Vector3 nose = -GlobalTransform.Basis.Z;
        var noseFlat = new Vector3(nose.X, 0.0f, nose.Z);
        if (noseFlat.LengthSquared() < 0.0001f)
            return;

        HeadingError = noseFlat.Normalized().SignedAngleTo(heading, Vector3.Up);

        // Stood down completely in the air — ProcessAirborne owns every axis up there, and two
        // controllers arguing over yaw is what made air steering feel vague. The heading is still
        // being dragged onto the car's facing above, so there is nothing to unwind on landing.
        if (IsAirborne)
        {
            SteerTorque = 0.0f;
            return;
        }

        float yawRate = AngularVelocity.Dot(Vector3.Up);
        float turnForce = Mathf.Clamp(
            HeadingError * AlignmentTorqueStrength - yawRate * AlignmentTorqueDamping,
            -AlignmentTorqueMax, AlignmentTorqueMax);

        // Walaber's upright_factor: fade steering out as the car tips away from level, so a car on
        // its side or mid-roll stops trying to steer and lets the righting torque do its work.
        float uprightFactor = Mathf.Clamp(GlobalTransform.Basis.Y.Dot(Vector3.Up), 0.0f, 1.0f);

        SteerTorque = turnForce * uprightFactor;
        ApplyTorque(Vector3.Up * SteerTorque);
    }

    // ---------------------------------------------------------------- Airborne

    private void ProcessAirborne(float delta)
    {
        if (!IsAirborne)
        {
            AirTime = 0.0f;
            return;
        }

        AirTime += delta;

        // Throttle and brake are dead weight in the air, so they fly the car instead.
        //
        // Brake is nose up, throttle is nose down — flight-stick sense, where pushing forward on
        // the control puts the nose down. A positive torque about the car's +X pitches the nose
        // up, so the brake is the positive term.
        float pitchInput = Mathf.Clamp(BrakeInput, 0.0f, 1.0f)
                           - Mathf.Clamp(ThrottleInput, 0.0f, 1.0f);
        float yawInput = Mathf.Clamp(SteeringInput, -1.0f, 1.0f);

        // Rate control, not torque. Each axis is driven toward a target angular rate, so the
        // export is the fastest it will ever go round rather than an acceleration that keeps
        // adding for as long as the button is held.
        //
        // Releasing drives the rate back to zero, and with the upright assist gone that is now
        // the *only* thing settling the car in the air. Note what it does not cover: roll is
        // neither driven nor damped, so a car that takes off rolling keeps rolling until it
        // lands. That is honest, and it is also the one axis with no way to correct it.
        //
        // Pitch about the car's own X, so a flip stays in the plane it is travelling in. Yaw
        // about world up, so it is still a flat turn with the car upside down.
        ApplyAirRate(GlobalTransform.Basis.X, pitchInput * Mathf.DegToRad(AirPitchRate));
        ApplyAirRate(Vector3.Up, yawInput * Mathf.DegToRad(AirYawRate));

        ApplyAirNitro();
    }

    /// <summary>
    /// Push an airborne car along its nose while a boost is burning. See
    /// <see cref="AirNitroForce"/> for why this is allowed to exist at all.
    ///
    /// Raw thrust rather than the grounded solve, because there is nothing to solve against up
    /// here — but held to the same ceiling that solve drives at, so the air never becomes the
    /// better place to spend a charge. It is the same boost delivered a different way, not a
    /// bigger one.
    /// </summary>
    private void ApplyAirNitro()
    {
        if (AirNitroForce <= 0.0f || _airNitroRemaining <= 0.0f)
            return;

        Vector3 forward = -GlobalTransform.Basis.Z;
        if (forward.Dot(LinearVelocity) >= TopSpeed + BoostSpeed)
            return;

        ApplyCentralForce(forward * AirNitroForce);
    }

    /// <summary>
    /// Drive rotation about <paramref name="axis"/> toward <paramref name="targetRate"/> rad/s.
    /// A target of zero is therefore also the brake, which is why airborne rotation needs no
    /// separate damping term.
    /// </summary>
    private void ApplyAirRate(Vector3 axis, float targetRate)
    {
        float rateError = targetRate - AngularVelocity.Dot(axis);
        ApplyTorque(axis * (rateError * AirRotationGain));
    }
}
