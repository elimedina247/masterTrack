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

    /// <summary>
    /// How hard the car presses itself into the road while the hop button is held, as a multiple
    /// of its own weight. This is the crouch before the jump: the springs take it, the body sinks
    /// onto them, and the wheels stay where they were — which is the whole look.
    ///
    /// A multiple of weight rather than a force in newtons, so the squat is the same depth on
    /// every car in the game and stays the same depth if the gravity scale is ever touched. At the
    /// default the springs carry three and a half times what they hold the car up at normally,
    /// which on the racer's tune takes about seventeen centimetres out of a forty-eight centimetre
    /// stance — a crouch that reads from the chase camera, and still less than half the travel the
    /// suspension has, so the car never bottoms out just for holding the button.
    ///
    /// It is worth noticing that nothing here adds anything to the hop, and it does not need to.
    /// The suspension is a real spring solved every frame, so squatting genuinely loads it, and
    /// letting go genuinely gives some of that back on the way up. The pop you get for holding it
    /// is the springs', not a bonus.
    /// </summary>
    [Export] public float HopSquatForce { get; set; } = 2.5f;

    /// <summary>
    /// How long the squat takes to reach full depth, in seconds, and how long it takes to let go
    /// again. Short, but not instant: slamming the full force on in one frame reads as the car
    /// being hit from above rather than as it crouching.
    /// </summary>
    [Export] public float HopSquatRamp { get; set; } = 0.10f;

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

    /// <summary>
    /// Extra gravity while airborne and on the way down. 1 is off.
    ///
    /// The honest knob for floaty jumps, and the reason it is honest is that it only ever touches
    /// the <i>descent</i>: a car leaves a ramp at the same speed and reaches the same height, it
    /// just does not hang there. Raising <c>gravity_scale</c> instead would tighten the whole arc
    /// and cut how high the ramp threw you, and raising the mass would do nothing at all — gravity
    /// is an acceleration, so mass cancels.
    ///
    /// <b>Gated on being fully airborne, not merely on falling.</b> A car sitting on the road has a
    /// small negative vertical velocity almost every step as the springs breathe, so a check on the
    /// velocity alone would quietly multiply gravity on a parked car — which sinks it into its own
    /// travel and means the springs have to be rescaled to get the ride height back. Off the ground
    /// there is no spring to fight, so nothing else has to move.
    /// </summary>
    [Export] public float FallGravityMultiplier { get; set; } = 1.6f;

    // ---------------------------------------------------------------- Car impact

    /// <summary>
    /// Slowest closing speed that counts as a hit, in m/s. Below this two cars leaning on each
    /// other are *racing*, and the solver's ordinary contact response is the right answer — the
    /// impulse layer only wakes up for the moment one of them arrives with intent.
    /// </summary>
    [ExportGroup("Car Impact")]
    [Export] public float ImpactMinSpeed { get; set; } = 3.0f;

    /// <summary>
    /// Closing speed that counts as a maximum-severity hit, in m/s. Everything about a hit — the
    /// launch, the spin, how long the victim is wrecked for — scales with closing speed up to
    /// this, then stops growing. 20 m/s is well inside what one boost buys, so a takeout is
    /// something a player can *choose* to line up rather than a jackpot.
    /// </summary>
    [Export] public float ImpactFullSpeed { get; set; } = 20.0f;

    /// <summary>
    /// How much of the closing speed comes back as launch, along the contact normal. 1.0 is a
    /// perfectly elastic wall; this ships above it on purpose. The solver has already resolved
    /// the "real" collision by the time this fires — this is the movie version layered on top,
    /// and under-selling it reads as two shopping trolleys touching.
    /// </summary>
    [Export] public float ImpactBounce { get; set; } = 1.2f;

    /// <summary>
    /// How much of the closing speed becomes upward pop. Pure theatre: a car that gets hit hard
    /// should come *off the road* a little, because the airborne rules then do the rest — no
    /// grip, no drive, a projectile with a spin on it. This is what makes a takeout look like a
    /// takeout from the chase camera instead of like a lateral teleport.
    /// </summary>
    [Export] public float ImpactPop { get; set; } = 0.35f;

    /// <summary>
    /// Yaw rate a full-severity hit injects, in rad/s. 10 is a bit over a full rotation and a
    /// half per second off the line — Burnout numbers, deliberately. The sign comes from where
    /// the hit landed relative to the centre of mass, so clipping someone's rear quarter
    /// pirouettes them, which is the classic takeout and should be the one that pays best.
    /// </summary>
    [Export] public float ImpactSpinRate { get; set; } = 10.0f;

    /// <summary>
    /// How long a full-severity hit wrecks the car for, in seconds. Scaled down by severity, and
    /// it is the load-bearing number of the whole feature: the launch and the spin are *velocity*,
    /// and this car's grip and steering solvers exist to delete unwanted velocity within a few
    /// frames. The stun holds the door open so the spin actually runs. See <see cref="StunAmount"/>.
    /// </summary>
    [Export] public float ImpactStunTime { get; set; } = 1.2f;

    /// <summary>
    /// Dead time per opposing car before another impulse can fire, in seconds. Two boxes
    /// grinding along each other report contact every step; without this, the "hit" would
    /// machine-gun. One big hit, then the solver's push takes over — that is the shape of it.
    /// </summary>
    [Export] public float ImpactCooldown { get; set; } = 0.3f;

    /// <summary>
    /// Fraction of the reaction the *aggressor* eats, 0..1. The car that got driven into takes
    /// the full launch and spin; the car that did the driving takes this much of it.
    ///
    /// This asymmetry is the Burnout grammar and it is not optional. Split the reaction evenly
    /// and rear-ending someone stops <i>you</i> dead — which punishes exactly the play the
    /// mechanic exists to reward. The rammer still eats enough to feel the hit land; they just
    /// keep their race.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ImpactRammerScale { get; set; } = 0.35f;

    /// <summary>Fraction of grip the stun takes away at full strength. See <see cref="StunAmount"/>.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float StunGripLoss { get; set; } = 0.9f;

    /// <summary>Fraction of steering torque the stun takes away at full strength.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float StunSteerLoss { get; set; } = 0.85f;

    /// <summary>Fraction of drive force the stun takes away at full strength.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float StunDriveLoss { get; set; } = 0.6f;

    // ---------------------------------------------------------------- Wall riding

    /// <summary>
    /// How walls answer to load and gravity, in two rules that both fade in past
    /// <see cref="WallTiltDeadZone"/> so flat road and gentle ramps never feel them: the grip
    /// cap falls toward <see cref="WallFriction"/> × the springs' actual pressing load — speed
    /// through a wall's curve is what buys the budget to stay on it, and a slow car's budget
    /// falls under gravity's pull and slides down the slope — and a
    /// <see cref="WallSagSpeed"/> of downhill drift is exempt from the grip solve entirely,
    /// the part of gravity the tires are not allowed to answer, which is what the driver is
    /// steering up against. Both live <i>inside</i> ProcessGrip: a force merely added on top
    /// would be read as sideways velocity next step and cancelled like any other.
    /// </summary>
    [ExportGroup("Wall Riding")]
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float WallTiltDeadZone { get; set; } = 0.15f;

    /// <summary>
    /// The friction coefficient on walls: the grip cap is this × the springs' pressing load
    /// once the car is tilted past the dead zone. 1.0 puts the stick-to-a-vertical-wall
    /// threshold at <c>v² / r = g</c> — about 25 m/s on the globe's 65 m orbit. The flat-road
    /// cap is untouched: this only ever *lowers* the budget.
    /// </summary>
    [Export(PropertyHint.Range, "0.25,3,0.05")]
    public float WallFriction { get; set; } = 1.0f;

    /// <summary>
    /// Downhill drift the grip solve is forbidden to cancel at full tilt, in m/s. The wall's
    /// constant tax: hold the wheel straight and the car walks down the slope this fast; the
    /// lap line is held by steering up against it. 0 makes walls sticky again once fast.
    /// </summary>
    [Export(PropertyHint.Range, "0,15,0.5")]
    public float WallSagSpeed { get; set; } = 4.0f;

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

    // ---- Impact ----

    /// <summary>
    /// How wrecked the car currently is, 0..1, fading linearly back to 0 over the stun window.
    ///
    /// While this is up, the car's three correcting authorities — grip, steering torque, drive
    /// force — are scaled down by their respective <c>Stun*Loss</c> fractions. That is the whole
    /// trick: an impact is just velocity, and this simulation is built to delete velocity it
    /// didn't ask for. Stun is the window during which it isn't allowed to, which is what turns
    /// "a nudge the solver corrects in three frames" into "a car sliding sideways across the
    /// track with the driver sawing at a dead wheel".
    ///
    /// Public so the cosmetics can key off it — wobbling steering, dazed camera, smoke.
    /// </summary>
    public float StunAmount { get; private set; }

    /// <summary>
    /// Fires when this car takes a hit from another racer, with the severity (0..1) and the
    /// world-space contact point. Camera shake, crunch sounds and sparks hang off this; physics
    /// must not.
    /// </summary>
    [Signal] public delegate void CarImpactEventHandler(float severity, Vector3 worldPoint);

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

    /// <summary>Stun clock: seconds left, and the window it started from. See <see cref="StunAmount"/>.</summary>
    private float _stunRemaining;
    private float _stunDuration;

    /// <summary>Seconds until another hop may be sprung. See <see cref="HopCooldown"/>.</summary>
    private float _hopCooldown;

    /// <summary>
    /// How far into the crouch the car is, 0 to 1. Ramped rather than switched — see
    /// <see cref="HopSquatRamp"/> — and readable so the cosmetics can follow it.
    /// </summary>
    public float SquatAmount { get; private set; }
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

        // The collision box carries no friction, ever. Grip lives entirely in ProcessGrip; the
        // engine's contact friction was a second tire model that only woke up when the box
        // touched the road — which is exactly the hard landings, where its bite scales with the
        // landing's normal impulse and it deleted most of a jump's forward speed. The normal
        // impulse itself is untouched, so walls still stop the car and landings still stop the
        // fall; the contact just no longer grabs sideways. A material set on the scene wins, so
        // a special car can still opt back into engine friction.
        if (PhysicsMaterialOverride == null)
        {
            _frictionlessMaterial ??= new PhysicsMaterial { Friction = 0.0f };
            PhysicsMaterialOverride = _frictionlessMaterial;
        }

        // Swept rather than teleported through each step. At 120 Hz a car at MaxFallSpeed moves
        // 0.54 m per step and the chassis box is 0.46 m tall, so a hard landing can put the whole
        // box past the road's collision skin in one step — the car ends up *inside* the tile,
        // where the ground rays start under the surface, see nothing (trimesh backfaces don't
        // report), and the suspension goes silent with the drive scaled to zero behind it. Jolt
        // runs this as its LinearCast motion quality; the cost is one shape cast on a handful of
        // bodies and it closes the tunnel outright.
        ContinuousCd = true;

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

        Vector3 down = GravityDirection;
        float fallSpeed = state.LinearVelocity.Dot(down);

        // Extra gravity on the way down only, and only with nothing under the wheels. Applied to
        // the velocity here rather than as a force in the physics step because this is where the
        // integrator's own gravity has just landed, so the two add cleanly and there is no
        // half-step where one has been applied and the other has not.
        if (FallGravityMultiplier > 1.0f && GroundFraction <= 0.0f && fallSpeed > 0.0f)
        {
            state.LinearVelocity += down * (_gravity.Length() * (FallGravityMultiplier - 1.0f)
                                            * state.Step);
            fallSpeed = state.LinearVelocity.Dot(down);
        }

        if (MaxFallSpeed <= 0.0f)
            return;

        // A hard clamp rather than a drag force: a force that merely opposes the fall still lets
        // velocity creep past the number asked for. Only the component along gravity is touched,
        // so a car flung sideways off a ramp keeps every bit of its horizontal speed.
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
        ProcessStun(dt);
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

    // ---------------------------------------------------------------- Impact stun

    /// <summary>
    /// Take a hit: start (or extend) the stun window and tell the cosmetics. The velocity side
    /// of an impact — the launch and the spin — is the caller's business, because only the
    /// caller knows what was hit and how; this is the car's own reaction to being hit.
    ///
    /// A weaker hit never shortens the window a stronger one already opened, so getting clipped
    /// twice mid-spin can't accidentally hand control back early.
    /// </summary>
    public void RegisterImpact(float severity, Vector3 worldPoint)
    {
        severity = Mathf.Clamp(severity, 0.0f, 1.0f);

        float duration = ImpactStunTime * severity;
        if (duration > _stunRemaining)
        {
            _stunRemaining = duration;
            _stunDuration = Mathf.Max(duration, 0.001f);
        }

        EmitSignal(SignalName.CarImpact, severity, worldPoint);
    }

    /// <summary>Hand control straight back. A respawned car should not still be seeing stars.</summary>
    public void ClearImpactStun()
    {
        _stunRemaining = 0.0f;
        _stunDuration = 0.0f;
        StunAmount = 0.0f;
    }

    /// <summary>
    /// Run the stun clock down. <see cref="StunAmount"/> fades linearly, so grip and steering
    /// come back *gradually* over the window — the car hooks up again rather than snapping from
    /// carousel to rails on one frame, which is also what keeps <see cref="MaxGripForce"/> from
    /// having to absorb the whole recovery at once.
    /// </summary>
    private void ProcessStun(float delta)
    {
        if (_stunRemaining <= 0.0f)
        {
            StunAmount = 0.0f;
            return;
        }

        _stunRemaining = Mathf.Max(_stunRemaining - delta, 0.0f);
        StunAmount = _stunDuration > 0.0f ? _stunRemaining / _stunDuration : 0.0f;
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

        // The crouch is over the instant the car goes, however far through its release ramp it
        // was. Left to decay on its own it would spend the first few frames of the hop still
        // pushing down on a car that is trying to climb.
        SquatAmount = 0.0f;

        ApplyCentralImpulse(Vector3.Up * (HopImpulse * Mass));
        EmitSignal(SignalName.Hopped);
        return true;
    }

    /// <summary>
    /// The whole hop gesture: crouch while the button is down, spring when it comes back up.
    ///
    /// The hop fires on the <b>release</b> rather than the press, which is what makes the crouch
    /// mean anything — a hop that had already happened could hardly be about to. A tap is still a
    /// hop, because the release is one physics step behind the press and nobody can feel a
    /// sixtieth of a second; what changes is that leaning on the button now holds the car down
    /// instead of firing it immediately, which is the point.
    /// </summary>
    private void ProcessHop(float delta)
    {
        if (_hopCooldown > 0.0f)
            _hopCooldown = Mathf.Max(_hopCooldown - delta, 0.0f);

        // Edge, not level, and the falling one: holding the button is one hop, not one per frame,
        // and it is the letting go that springs it.
        //
        // Settled before the squat is stepped, and not after, so that the frame the car is trying
        // to leave the ground is not also a frame it is being pressed into it. TryHop drops the
        // crouch on the way past, which is what leaves ProcessSquat with nothing to apply.
        if (!HopInput && _hopWasHeld)
            TryHop();
        _hopWasHeld = HopInput;

        ProcessSquat(delta);
    }

    /// <summary>
    /// Press the car into the road while the button is held, so it visibly gathers itself before
    /// it goes. The springs are what actually do it: this only adds weight, and the suspension
    /// solve turns that into a body that sinks while the wheels stay put.
    ///
    /// Along the chassis' own down rather than the world's, which is the opposite of the way
    /// <see cref="TryHop"/> goes. A hop wants to be the same hop whatever the car is sitting on,
    /// so it fires along world up; a squat wants to load the springs squarely, and the springs
    /// push along the chassis — pressing straight down on a banked corner would be trying to
    /// compress them at an angle and would shove the car down the camber instead.
    ///
    /// Only on the ground, and only while there is a hop to be had. Pushing down on nothing is a
    /// way to fall faster, which is not what the button is for; and holding it through the
    /// cooldown after a hop should not pin the car to the floor while it waits.
    /// </summary>
    private void ProcessSquat(float delta)
    {
        bool crouching = HopInput && !IsAirborne && HopImpulse > 0.0f && _hopCooldown <= 0.0f;

        // Ramped both ways, so letting go releases the springs over a couple of frames rather
        // than dropping the load in one and punching the car off its own suspension.
        float step = HopSquatRamp > 0.0f ? delta / HopSquatRamp : 1.0f;
        SquatAmount = Mathf.Clamp(SquatAmount + (crouching ? step : -step), 0.0f, 1.0f);

        if (SquatAmount <= 0.0f || HopSquatForce <= 0.0f)
            return;

        // Scaled by how much of the car is actually on the road: a car balanced on two wheels
        // over a crest has half the suspension to push into, and pressing at full strength there
        // would pivot it rather than crouch it.
        float weight = Mass * _gravity.Length();
        ApplyCentralForce(-GlobalTransform.Basis.Y
                          * (weight * HopSquatForce * SquatAmount * GroundFraction));
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

        // A stunned car can't just power out of its own spin — the drive solve chasing the target
        // speed is a stabiliser too, and it would straighten the car's path even with the grip and
        // steering already stood down. Brakes are left alone: stamping the brake mid-spin is a
        // *choice*, and it should remain one.
        forwardLimit *= 1.0f - StunDriveLoss * StunAmount;

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
    /// keeps most of its sideways speed and slews. There is no slip curve and no friction budget
    /// shared with acceleration — grip does not sag at speed on level road, and a drift holds for
    /// exactly as long as the player holds it. The one load-sensitive part is the wall rules:
    /// past <see cref="WallTiltDeadZone"/> the cap answers to the springs' pressing load and a
    /// slice of downhill drift goes uncancelled — see the Wall Riding exports.
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

        // A car that has just been hit is not allowed to save itself. This grip solve would
        // otherwise cancel most of a takeout's sideways launch within a few frames — the stun is
        // what lets the hit *carry*. See StunAmount.
        grip *= 1.0f - StunGripLoss * StunAmount;

        CurrentGrip = grip;

        // The load that gates the wall cap, low-passed. Raw spring force is spiky — the
        // damper term jumps at every bump and facet joint, and the chassis bobs — and a cap
        // that flutters with it is a stick-slip machine: budget dips, the rear steps out, the
        // catch spike snatches the slide back, and the drive force poured in while the nose was
        // off the velocity arrives as a forward lurch. A ~0.2 s average keeps the meaning
        // (sustained lightness still starves the tire) and loses the flicker. Updated before
        // the airborne early-out so it decays toward zero off the ground and a landing builds
        // its budget back over the same fifth of a second instead of snatching.
        float load = 0.0f;
        foreach (GroundRay ray in WheelArray)
            load += Mathf.Max(0.0f, ray.SpringForce);

        _smoothedLoad = Mathf.Lerp(_smoothedLoad, load,
                                   1.0f - Mathf.Exp(-delta / LoadSmoothingSeconds));

        // **Nothing acts sideways in the air.** This force points along the car's own X axis, so
        // any of it left running while airborne would mean rotating the car changed which
        // direction its velocity got scrubbed in — turning an orientation control into a
        // thrust control. Off the ground the car is a projectile: gravity, and nothing else.
        if (grip <= 0.0f)
            return;

        Vector3 side = GlobalTransform.Basis.X;
        float sideVelocity = side.Dot(LinearVelocity);

        // Clamped, because the solve above is a *time constant*, not a force: it asks for whatever
        // it takes to cancel the sideways velocity this step, so the faster the car is thrown
        // sideways the harder it grips back. Without the cap a tire is infinitely strong and a
        // car that lands sideways stops in its own length. See MaxGripForce.
        // Scaled by contact as well: two corners on the road can hold half of what four can, so a
        // car touching down on one wheel slides further before it hooks up.
        float cap = MaxGripForce * GroundFraction;

        // Walls answer to load and gravity. Past the dead zone the cap falls toward
        // WallFriction × what the springs actually press with — so speed through a wall's curve
        // is what pays for staying on it — and a slice of downhill velocity is subtracted from
        // what the solve may cancel, so gravity keeps pulling and the driver keeps steering up.
        // Both scale with tilt: flat road never feels either.
        float tilt = 1.0f - Mathf.Clamp(GlobalTransform.Basis.Y.Dot(-GravityDirection),
                                        0.0f, 1.0f);
        float range = Mathf.Max(1.0f - WallTiltDeadZone, 0.001f);
        float tiltStrength = Mathf.Clamp((tilt - WallTiltDeadZone) / range, 0.0f, 1.0f);

        if (tiltStrength > 0.0f)
        {
            cap = Mathf.Min(cap, Mathf.Lerp(cap, _smoothedLoad * WallFriction, tiltStrength));

            sideVelocity -= side.Dot(GravityDirection) * WallSagSpeed * tiltStrength;
        }

        float instantSideAccel = -sideVelocity / delta;
        float sideForce = Mathf.Clamp(instantSideAccel * Mass * grip, -cap, cap);
        ApplyCentralForce(side * sideForce);
    }

    /// <summary>Time constant of the load average that gates LoadedGrip's cap, in seconds.</summary>
    private const float LoadSmoothingSeconds = 0.2f;

    /// <summary>The springs' pressing load, low-passed over <see cref="LoadSmoothingSeconds"/>.</summary>
    private float _smoothedLoad;

    /// <summary>The body's friction-0 material, made in <see cref="Initialize"/> and freed in
    /// <see cref="_ExitTree"/>.</summary>
    private PhysicsMaterial? _frictionlessMaterial;

    public override void _ExitTree()
    {
        // Freed eagerly rather than left to the .NET shutdown sweep — a C# resource wrapper
        // still alive at exit is what intermittently crashes the process on quit.
        if (PhysicsMaterialOverride == _frictionlessMaterial)
            PhysicsMaterialOverride = null;
        _frictionlessMaterial?.Dispose();
        _frictionlessMaterial = null;
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

        // Faded while stunned, or the PD controller would kill an injected takeout spin almost
        // immediately — 22000 Nm of damping against 10 rad/s is exactly the fight it was built to
        // win. The wheel goes light, the spin runs, and authority bleeds back with the stun.
        float stunFactor = 1.0f - StunSteerLoss * StunAmount;

        SteerTorque = turnForce * uprightFactor * stunFactor;
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
