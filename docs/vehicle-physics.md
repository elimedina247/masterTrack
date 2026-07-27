# Vehicle physics

Master Track drives on an **arcade drift model**: a rigid body held up by four ray-cast
springs, with no tire model at all. It is a port of the approach Walaber describes for
Parking Garage Rally Circuit in
[Arcade Drift Car Physics Explained](https://www.youtube.com/watch?v=wOAAitKoV9M).

The previous physics — a C# port of
[Godot-Easy-Vehicle-Physics](https://github.com/DAShoe1/Godot-Easy-Vehicle-Physics), with a
brush tire model, real spring rates, differentials, ABS and traction control — is gone. It is
still on `main` if you want to compare.

Licence and credits: [`assets/gevp/ATTRIBUTION.md`](../../assets/gevp/ATTRIBUTION.md).

---

## The whole model

The car is a hovercraft wearing a car. Four ideas, in the order they run each physics step:

| Step | What it does | Where |
|---|---|---|
| **Suspension** | Each ray applies `(compression × k) − (closing speed × c)` along **world up** | `GroundRay.ApplyGroundForce` |
| **Drive** | Solve for the force that reaches target speed in one step, clamp it | `Vehicle.ProcessDrive` |
| **Grip** | Solve for the force that cancels all sideways velocity, keep a % of it | `Vehicle.ProcessGrip` |
| **Steering** | PD torque pointing the body at a heading vector | `Vehicle.ProcessSteering` |

There are no wheels. `WheelFL` and friends are ray casts that hold up a corner and report what
it is standing on; they have no spin, no brakes, no drive and no tire forces. The wheels you can
see are `WheelVisual` nodes hanging off the posed shell — see **Body pose** below.

### Every force on the car

The complete list. If the car does something, it is one of these — there is nothing else, and
nothing from the old physics survives.

Order matters: they run in this sequence inside one `_PhysicsProcess`, and each one reads the
velocity as it stood at the top of the step.

| # | Force / torque | Where it acts | Grounded | Airborne |
|---|---|---|---|---|
| 0 | **Gravity** (Godot's own, 9.8) | centre of mass | always | always |
| 1 | **Spring + damper**, ×4 | each ray, along **world up** | yes | — |
| 1b | **Bump stop** | folded into #1 past 60% travel | yes | — |
| 1c | **Downward pull** | each ray, capped + faded | over crests | — |
| 2 | **Drive / brake** | centre of mass, along nose | × `GroundFraction` | **none** |
| 3 | **Grip** | centre of mass, along `Basis.X` | × `GroundFraction` | **none** |
| 4 | **Steering torque** (PD → heading) | about **world up** | yes | **stood down** |
| 5 | **Flip rate torque** | about car's `Basis.X` | — | yes |
| 5b | **Air yaw rate torque** | about **world up** | — | yes |
| 6 | **Terminal velocity clamp** | `_IntegrateForces`, velocity | always | always |

Every force in that list is new. **Nothing from the previous physics survives** — the airborne
upright assist and the extra fall gravity were the last two, and both were removed on purpose.
See **Airborne** below for what that costs.

Things that are **not** in the list, and that people expect to be:

- **No aerodynamic drag.** Coasting slows via `MaxCoastForce` (#2), which is a flat force, not a
  v² term. There is no downforce either.
- **No rolling resistance.** Off-road is `SurfaceSpeedMultiplier` scaling the *target speed*
  in #2, not a force.
- **No engine, gearbox, clutch, differential, or per-wheel drive.** `CurrentGear` is a direction
  flag.
- **No tire model.** #3 is the whole of it, and it is one number per surface.
- **No anti-roll bar, camber, toe, Ackermann, ABS or traction control.**
- **No roll control in the air.** Pitch and yaw only.

### In the air the car is a projectile

**Nothing but gravity touches its velocity.** Both #2 and #3 are scaled by `GroundFraction` and
skipped outright when it reaches zero, so the only things acting on an airborne car are gravity,
the `MaxFallSpeed` clamp, and whatever torques the player is applying.

This is a rule, not a tuning choice, and it is worth defending. Drive points along the nose and
grip points along `Basis.X` — both are **body axes**. Any amount of either left running in the
air would mean rotating the car changed the direction its velocity was pushed or scrubbed in,
which quietly turns an orientation control into a thrust control. A flip would accelerate you.

The failure mode is easy to reintroduce and hard to spot, because it looks tiny in the source.
The old airborne grip was 5%, which sounds negligible — but grip removes that fraction of the
velocity along its axis *every physics step*, and at 120 Hz even 5% is 99.8% of that component
gone inside one second. With the car rolled, that axis had a vertical component, so it ate the
car's fall speed and gravity appeared to switch off whenever you spun.

**Any per-step percentage applied along an axis that is free to rotate is a bug waiting to
happen.** Scale it by `GroundFraction`.

### Why "solve then clamp"

Both the drive and the grip force are worked out the same way: *what force would get this
exactly where I want it in a single step?* — then most of that force is thrown away.

```csharp
float instantAccel = (desiredSpeed - currentForwardSpeed) / delta;
float force = instantAccel * Mass;
ApplyCentralForce(forward * Mathf.Clamp(force, -maxForce, maxForce));
```

The clamp **is** the drivetrain. Well below the target the car pushes at `MaxAccelForce` flat
out; as it closes on the target the unclamped force drops under the clamp on its own and the
car eases in and holds. No curve, no gears, no drag to balance against.

The consequence that matters: **top speed is a number, not an equilibrium.** In the old model
top speed was where drive force and drag happened to cancel, so pushing past it meant fighting
the whole drivetrain. Here, raising the target raises the speed, instantly and exactly. That is
what makes the boost system below possible.

Grip works identically, except the target sideways speed is always zero and the fraction kept
is `GripFactor`. That fraction is the entire tire model. It does not vary with load, speed or
how much of the friction budget acceleration is using — which is unphysical, and is exactly why
the car is predictable.

---

## Steering has no steering angle

The front wheels turning is cosmetic (`WheelVisual.VisualSteerAngle`). What actually steers the
car is a PD controller that torques the body toward `HeadingDirection`:

```csharp
turnForce = HeadingError * AlignmentTorqueStrength - yawRate * AlignmentTorqueDamping;
ApplyTorque(Vector3.Up * Mathf.Clamp(turnForce, -Max, Max) * uprightFactor);
```

**This is the one deliberate deviation from the video.** Walaber points the car at
`camera_rig_01.global_basis.z` — the camera *is* the steering reference, and the car chases it.
That can't port literally: [`CameraRig.cs`](../scripts/racer/CameraRig.cs) is a child of the car
with free-look on right-mouse, so coupling steering to it would mean looking around the car
steered it into a wall. The vehicle owns the heading instead. The maths is otherwise his.

The heading is pushed by the stick and dragged back toward the car's own facing:

```csharp
_headingYaw += steer * SteeringRate * delta;
_headingYaw = LerpAngle(_headingYaw, GlobalRotation.Y, 1 - exp(-HeadingRecenterRate * delta));
```

That recentre is what a trailing chase camera gives Walaber for free, and it is load-bearing —
without it a held stick winds the heading round forever. With it, the heading settles at a fixed
offset ahead of the car and the car turns at a steady rate. **`SteeringRate` and
`HeadingRecenterRate` together set how tight the car corners**; neither means much alone.

Nothing in the steering scales with speed or grip, so the car answers the stick the same at
30 km/h and at 300.

---

## Controls

| Action | Key | Pad |
|---|---|---|
| Throttle | `W` / `↑` | Right trigger |
| Brake / reverse | `S` / `↓` | Left trigger |
| Steer | `A` `D` / `←` `→` | Left stick |
| **Drift** | `Space` | A |
| **Flip** (airborne) | `W` nose up / `S` nose down | Triggers |
| **Turn** (airborne) | `A` `D` / `←` `→` | Left stick |
| Nitro | `Shift` | B |
| Reset | see `racer_reset` | — |
| Physics debug overlay | `` ` `` | Right stick click |
| Cycle debug pages | `,` `.` | D-pad ← → |
| Free-look camera | hold right mouse | — |

The drift button is still bound to the `racer_handbrake` action, so existing keyboard and pad
bindings carry over — the button is in the same place, it just does something else now. The
clutch and shift bindings are gone: there is no gearbox, and reverse engages by holding the
brake below `ReverseEngageSpeed`.

---

## Drifting

A drift is a **state you ask for**, not something the physics decides has happened. Press the
drift button above `DriftMinSpeed` and:

- the heading is offset by `DriftAngle` (35°) in the drift direction,
- `GripFactor` is multiplied by `DriftGripMultiplier` (0.28), so the car actually slews,
- both ramp in over `DriftBlendSpeed` rather than snapping.

Direction comes from the stick at the moment of the press; held straight, the car takes
whichever way it is already rotating.

### Steering inside a drift

This is the part worth protecting. While drifting, the stick adds **±`DriftSteerRange`** (15°)
on top of the drift angle:

```gdscript
camera_fwd.rotated(Vector3.UP, deg_to_rad(35.0 * _drift_dir) + steer_input * deg_to_rad(15.0))
```

The car is already committed to an arc and the stick tightens or opens it. That is why a drift
here reads as *driven* rather than *survived*. Set `DriftSteerRange` to 0 and a drift becomes a
cutscene.

By default the stick does **not** also swing the heading during a drift
(`DriftHeadingInfluence = 0`), which is closest to the original. Raise it if drifts feel like
they can't be aimed.

---

## Boost, and chaining

Holding a drift earns tiers by time (`DriftTierTimes`, default 0.55 / 1.3 / 2.2 s). Releasing
pays out the tier reached:

- `BoostTierSpeed` (8 / 15 / 24 m/s) is **added to `TopSpeed`**,
- `BoostTierDuration` (1.0 / 1.7 / 2.6 s) is how long it holds,
- `BoostAccelForce` (14000 N) is added to `MaxAccelForce` so it lands as a shove rather than a
  polite climb.

**Boosts are additive.** Start a new drift while a boost is still burning — or inside
`ChainGraceTime` after it — and the next payout stacks on top of what is already there instead
of replacing it:

```csharp
BoostSpeed = Mathf.Min(BoostSpeed + speed, MaxBoostSpeed);
BoostTimeRemaining = Mathf.Max(BoostTimeRemaining, duration);
```

So a driver who keeps chaining ends up a very long way over `TopSpeed`. `MaxBoostSpeed` (45 m/s)
is the only thing that stops it — on the racer's 55.6 that is a hard ceiling near 360 km/h.
When every burst expires the bonus **bleeds** off at `BoostDecayRate` rather than dropping, so
coming down off a big chain is a moment of the car running out rather than a switch.

Nitro feeds the same bonus (`GrantBoost`), so charges stack with drift boosts and with each
other exactly the same way. `IsNitroActive` is now an alias for `IsBoosting`, which means the
exhaust flames, camera FOV kick and HUD all fire on drift boosts too.

`ChainCount` and the `BoostStarted(tier, chainCount)` signal are there for UI — nothing reads
them yet.

---

## Tuning order

Everything is on the `Racer` root in [`scenes/Racer.tscn`](../scenes/Racer.tscn), grouped in the
inspector. Turn the overlay on with `` ` `` and page with `,` / `.` — the **Drift and Boost**
page prints tier, chain depth and the live speed ceiling.

Work in this order; later steps assume earlier ones are settled.

1. **`VehicleMass`, `RideHeight`, `SpringStrength`, `SpringDamping`** — how the car sits. The
   car settles `mass × 9.8 / 4 / SpringStrength` into its travel; the default is about 7 cm of
   a 55 cm ride height. Damping around 8–10% of the spring rate. See **Bottoming out** below
   before changing any of these.
2. **`CenterOfMassHeight`** — still the single biggest lever on lean and on flipping. Negative
   is the safe direction.
3. **`TopSpeed`, `MaxAccelForce`** — `MaxAccelForce / VehicleMass` is the acceleration in m/s².
4. **`GripFactor["Road"]`** — how much the car slides at all.
5. **`SteeringRate` + `HeadingRecenterRate`** — cornering. Together, not separately.
6. **`DriftAngle`, `DriftGripMultiplier`, `DriftSteerRange`** — how a drift feels.
7. **`DriftTierTimes`, `BoostTierSpeed`, `MaxBoostSpeed`** — how the boost economy pays out.

**Physics tick rate must stay at 120 Hz or higher** (`project.godot` sets it). The overlay
shouts in red if it drops. The solve-then-clamp forces divide by `delta`, so the clamp is what
keeps them stable — but the drift and boost timings are still tick-sensitive.

### Bottoming out

Three numbers have to stay in this order, or ramps stop working:

```
collision box clearance  >  spring travel  >  what a crease actually needs
        0.50 m           >      0.48 m
```

The car settles 0.07 m into a 0.55 m ride height, so there is **0.48 m** of travel left below
static. The collision box was originally 2.86 m long with its bottom 0.225 m above the body
origin — **0.365 m** of ground clearance, which is *less* than the travel. The chassis therefore
reached the road before the suspension ran out, and a rigid-body collision between the car and
the track deletes momentum outright. That is what made climbs feel like driving into a wall.

Two things fix it, and both are load-bearing:

- **The collision box is lifted and shortened** — 1.2 × 0.42 × 2.25 at `y = 0.59`, giving 0.50 m
  of clearance against 0.48 m of travel. It is also no longer overhanging both axles by 0.4 m,
  which is what dug into a slope the wheels were already following. The trade is that the
  collision proxy is now smaller than the visible car, so bodywork can clip slightly into walls.
- **The springs have a bump stop** — `BumpStopStart` (0.6) and `BumpStopStrength` (12). Past 60%
  of the travel the rate climbs with the square of how far in it is, so it is soft where it
  engages and very hard at the end.

Worth knowing what the bump stop is sized against. The worst case is the foot of a two-cube climb
taken at chained-boost speed: a 7.9° crease at 50 m/s asks the car to absorb 6.9 m/s of vertical
velocity, or 28.6 kJ.

| `BumpStopStrength` | Energy absorbed in the travel | Largest impact held |
|---|---|---|
| 0 (no stop) | 19.4 kJ | 5.7 m/s — **under the worst case** |
| **12** (default) | 43.7 kJ | 8.5 m/s |
| 20 | 60.0 kJ | 10.0 m/s |

Raise it if the chassis still reaches the road; lower it if hard landings ping the car back into
the air. It contributes nothing at all in normal driving — static compression is 0.07 m and the
stop does not engage until 0.33 m.

### Ray geometry

A ray sits at the **top of the travel**. The body floats `RideHeight` below it (less the static
compression), so the ground ends up at `ray Y − RideHeight + compression` in body space. All
four rays sit at `Y = 0.34` on the racer with `RideHeight = 0.55`.

`TireWidth` lives on the ray, is in metres (was millimetres), and is read only by `SkidMarks` for
the strip width. `TireRadius` lives on the `WheelVisual` instead, because it is purely about where
the mesh sits. The mesh goes under the `WheelVisual` and must face **+Z**.

---

## Layout

| File | What it is |
|---|---|
| `scripts/vehicles/Vehicle.cs` | The whole model. Every tuning knob lives here. |
| `scripts/vehicles/GroundRay.cs` | One corner: ray, spring, surface read. Physics only. |
| `scripts/vehicles/WheelVisual.cs` | One visible wheel, parented to the posed shell. |
| `scripts/vehicles/VehicleInput.cs` | Input as a value + the action-name map. |
| `scripts/vehicles/BodyLean.cs` | The visual pose — roll, drift yaw, squat and dive. |
| `scripts/vehicles/TireSlip.cs` | One shared "how hard are we sliding" number. |
| `scripts/vehicles/VehicleDebugOverlay.cs` | The tuning overlay. |
| `scripts/vehicles/SkidMarks.cs`, `TireSqueal.cs`, `WheelSmoke.cs` | Slide effects. |
| `scripts/vehicles/EngineSound.cs`, `FakeGearbox.cs` | Engine note, pitched off `SpeedFraction`. |

Deleted with the old model: `Wheel.cs`, `Axle.cs`, `VehicleMath.cs`, and `scenes/racer_old.tscn`
(an unreferenced CC96-era snapshot that pointed at `Wheel.cs`).

---

## Body pose

[`BodyLean.cs`](../scripts/vehicles/BodyLean.cs) poses the visible shell and touches nothing
else — no collision shape, no rays. Walaber calls this "posing the car", and it is a separate
system on purpose: real weight transfer on a body with a low centre of mass is far too subtle to
read from a chase camera directly behind it.

The old version derived everything from *measured* lateral g, which is why it was so restrained —
measured g is honest, and honest is not the goal. This one is driven mostly by what the player is
**asking for**, so the shell answers the stick before the physics catches up:

- **Roll** — `LeanRoll` 9°, plus `DriftRoll` 7° more at full drift.
- **Drift yaw** — `HeadingYawFollow`, see below.
- **Pitch** — `SquatPitch` under power, `DivePitch` under braking, `BoostPitch` on top while a
  boost burns.

### Drift yaw is the heading error

The vehicle is a PD controller chasing `HeadingDirection` — the **white arrow** in the debug
overlay's Steering page — and the chassis always lags it. During a drift that lag *is* the drift
angle, because the heading has been swung 35° off.

`HeadingYawFollow` (1.0) yaws the shell by `HeadingError` to take up exactly that lag, which puts
the **model on the white arrow** while the chassis underneath is still catching up. That is what
makes the nose point into the slide.

- `0` — shell follows the chassis; the car looks like it is cornering, not drifting.
- `1` — model sits exactly on the commanded heading.
- `>1` — model *leads* the heading, overstating the drift. A legitimate arcade cheat.

`MaxHeadingYaw` (40°) stops a spin or a hard collision twisting the shell off its chassis.

### The wheels are children of the shell

**The wheels are decoration and nothing else.** No force in the game is applied at a wheel, so
there is nothing tying a wheel mesh to the ray cast that holds that corner up — which means the
mesh is free to live wherever it looks best.

It looks best under the **posed shell**. `WheelVisual` nodes are children of `BodyRig`, so they
inherit the body's roll, yaw and pitch for free and the car reads as one object. The alternative
— parenting them to the chassis and trying to bolt the rotation back on afterwards — is what made
the wheels stay square while the body swung 35° into a drift, and what drove them into ramps: the
placement ran on the rendered frame while the contact point came off the last physics tick, and on
a ramp the two disagree.

The ray casts stay on the **chassis**. They have to: hanging them off the shell would swing the
probes around with the pose and the suspension would read the road from the wrong places.

So a `WheelVisual` computes exactly one thing per frame — how far below its rest position to hang
so the tire lands on the road — from its ray's contact point **in world space**, then measured
against its own parent. That last part is what makes it immune to the pose: however far the shell
has leaned over this corner, the answer is still "put the hub `TireRadius` above the tarmac".

| Export | Default | |
|---|---|---|
| `MaxDroop` | 0.30 | How far it hangs in the air |
| `MaxLift` | 0.50 | Bump travel. Must cover the springs bottoming out — see below |
| `TravelResponse` | 30 | How fast it closes on the road |
| `ReboundRatio` | 0.55 | Extends slower than it compresses, which is what reads as damping |

Place the node at the wheel's **static** centre height so travel reads zero at rest.

`MaxLift` is the one to be careful with. Clamping it short does not keep the wheel out of the
bodywork — it drives the wheel *through the road*, because the hub stops rising while the tarmac
keeps coming up to meet it. A wheel clipping an arch is much the lesser evil.

### Suspension you can see

There is nothing faked here. The wheels are pinned to the road and the **body springs above
them**, which is exactly what the four ray casts are already doing to the rigid body — so hitting
a kerb compresses the visible suspension because the car really did compress.

The only cosmetic liberty is `ReboundRatio`: the wheel snaps up over a bump and eases back down
rather than moving at the same rate both ways, because symmetric travel reads as a mechanism and
asymmetric travel reads as a damped spring.

Raise `LeanRoll` and `DriftRoll` if the car still looks too flat, and `HeadingYawFollow` if it
doesn't look sideways enough.

---

## Surfaces

Unchanged: a `GroundRay` identifies what it is on by the **first node group** on whatever it
hits, looked up in `GripFactor` and `SurfaceSpeedMultiplier`.

**Every drivable collider must be in a surface group** — `Road`, `Dirt`, `Grass` or `Ice` (see
`SurfaceGroups`) — **and it must be the first group on that node.** Put gameplay groups on after
the surface group, or on a different node. An unknown or missing group leaves the ray on
whatever surface it was already on and warns once.

`SurfaceSpeedMultiplier` is the off-track penalty: it scales the target speed rather than adding
rolling resistance, which is cheaper and much easier to reason about.

---

## Airborne

Gravity in the air is now **just gravity**. There is no extra fall multiplier and no auto-level:
both were the last surviving pieces of the previous physics, and both were removed on purpose.

The aerial controls are **orientation only**. They apply torque and nothing else — no drive, no
grip, no thrust. See **In the air the car is a projectile** above for why that has to hold.

- **`MaxFallSpeed`** (65 m/s) — the one remaining airborne clamp, in `_IntegrateForces`. Not a
  feel knob: it is the guard rail that stops a fall off the edge of the board outrunning the
  collision solver.
- **`MaxPullForce`** (11000 N) and **`PullFadeDistance`** (0.12 m) — the spring term goes negative
  when the ground drops away past the ride height, which glues the car over a crest. The fade is
  what separates a crease from a cliff: a crease drops the road away by centimetres and wants
  holding onto, an edge drops it away by everything and wants letting go of. Without it, a pull
  strong enough to follow a crease also sucks the car back down off every jump.

### What removing the auto-level and fall gravity costs

Both were doing real work. Know what has been given up:

| Gone | What it did | What happens now |
|---|---|---|
| `FallGravityMultiplier` | 2×–3× gravity on descent | A 40 m drop hangs for **2.86 s**, up from 2.02 s. Jumps are long and floaty — but honest |
| `AirborneUprightTorque` | Levelled the car toward flat | A car that takes off crooked **lands crooked**. On its roof, it stays there until `racer_reset` |

And one thing neither of them covered, now exposed: **roll is neither driven nor damped.** Pitch
and yaw are rate controlled, so releasing the stick settles them — nothing touches roll at all. A
car that leaves the ground rolling keeps rolling until it lands, and there is no input bound to
correct it. The overlay's **Airborne** page shows the roll rate.

If floaty jumps become the problem, `FallGravityMultiplier` is the honest thing to bring back — it
only ever touched the descent, so it never changed how high a ramp threw you. If landing on the
roof becomes the problem, binding roll is a better answer than reinstating an assist.

### Flying the car

Air rotation is **rate controlled, not torque controlled**. Each axis is driven toward a target
angular rate, so the export *is* the fastest the car will ever go round — rather than an
acceleration that keeps adding for as long as the button is held, which lets the car wind itself
into a spin it cannot recover from. Releasing drives the rate back to zero, which is also what
bleeds off a tumble from a bad take-off; there is no separate damping term.

- **`AirPitchRate`** (140 °/s) — throttle pitches the nose **up**, brake pitches it **down**.
  Those two pedals do nothing at all in the air (drive is scaled by `GroundFraction`, which is
  zero), so they were free, and they are the pair the player's thumbs are already on. 140 °/s is
  a bit over two seconds per full rotation.
- **`AirYawRate`** (100 °/s) — steering turns the car left and right, about **world up**, so it
  is still a flat turn with the car upside down. Slower than pitch on purpose: yaw in the air is
  for lining up a landing, not for tricks, and it is the axis a player is most likely to be
  holding by accident on the way off a ramp.
- **`AirRotationGain`** (6000 Nm per rad/s) — how quickly a flip spins up and stops, without
  changing how fast it ends up going.

The heading controller is **stood down completely** while airborne — `ProcessAirborne` owns every
axis up there, and two controllers arguing over yaw is what made air steering feel vague. The
heading is still being dragged onto the car's facing throughout, so there is nothing to unwind on
landing.

Roll is not bound. It needs an input you don't currently have spare.

### The camera lets go in the air

On the ground the rig is a child of the car, so "no rotation" already means "looking down the
track" — right, because the car and the road turn together. In the air the car can be doing
anything, and a camera welded to it turns the whole world upside down around a car that appears
to sit still. That reads as the *world* spinning rather than the car, and it is genuinely
nauseating.

`CameraRig.DetachWhenAirborne` holds a fixed world orientation instead: level, on the yaw the car
was travelling on as it took off, so the horizon stays put and the car tumbles in front of it.

- **`DetachDelay`** (0.35 s) — long enough that kerbs, crests and the constant going-light of a
  ramp don't twitch the camera in and out.
- **`DetachTime`** (0.25 s) / **`ReattachTime`** (0.55 s) — slower to take hold again than to let
  go, because the car can land facing anywhere and snapping onto its nose is its own lurch.

Both poses are built as world bases and slerped rather than switching between a local and a
global mode. A switch would move the camera on the frame it happened; a slerp cannot, because at
the moment of the swap the two poses are identical.

### Drive and grip scale with `GroundFraction`, not with `IsAirborne`

Both fade with **how much of the car is on the road** — `GroundedRayCount / 4` — rather than
switching off the moment the last ray leaves it.

This is not a nicety. Ramps are built from chord facets (see below), and the convex creases
between them unstick a car at speed. An all-or-nothing check meant a ramp stopped driving the
instant it started working: the car would skim a crease, lose *all* drive and drop to airborne
grip, and stall halfway up. Fully airborne the drive is still zero — there is nothing to push
against, and without that a car accelerates off a ramp.

### Ramp geometry

Ramps used to be pure smoothstep from end to end, which sounds smooth and is not: a smoothstep's
slope peaks at **1.5× its own average**, so a two-cube climb reached 44° in the middle while
being 34° on paper, and the angle changed everywhere. Eight evenly spaced facets left a 10–18°
crease every 15 metres.

They are now **trapezoidal**: smoothstep into a constant slope, hold it, smoothstep out
(`RampBlend` 0.22). Over three cells that is a flat **23°** for a one-cube climb and **40°** for
two. Facets are clustered into the two eased ends, where all the curvature is —
`RampEaseSegments` 8 each, `RampMidSegments` 4 down the collinear middle — which brings the worst
crease to 4.3° and 7.9°.

If ramps still feel wrong, `RampBlend` is the knob: lower is a longer constant slope with sharper
ends, higher is gentler ends and a steeper middle.

---

## Known noise

Two warnings at exit are expected and predate all of this: `2 ObjectDB instances were leaked`
and `1 resources still in use`. Ignore them.
