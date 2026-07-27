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

There are no wheels. `WheelFL` and friends are ray casts that hold up a corner and carry a
decorative mesh; they have no spin, no brakes, no drive and no tire forces.

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

The front wheels turning is cosmetic (`GroundRay.VisualSteerAngle`). What actually steers the
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

`TireRadius` is now **only** where the visible wheel mesh sits relative to the contact point —
it has no effect on physics. `TireWidth` is metres (was millimetres) and is read only by
`SkidMarks` for the strip width. The mesh goes under the ray's `WheelNode` and must face **+Z**.

---

## Layout

| File | What it is |
|---|---|
| `scripts/vehicles/Vehicle.cs` | The whole model. Every tuning knob lives here. |
| `scripts/vehicles/GroundRay.cs` | One corner: ray, spring, surface read, wheel mesh. |
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

### The wheels have to come with it

The ground rays are children of the **chassis**, not of the posed shell — they have to be, or the
pose would swing the ray casts around and the suspension would read the road from the wrong
places. Left alone that means the shell yaws 35° into a drift while the wheels stay square to the
chassis, and the car looks like its shell has come loose.

`BodyLean` therefore publishes `Vehicle.WheelPose`, a rotation each `GroundRay` composes into its
wheel. Rotation only, never translation, so the wheels turn with the body but stay planted on
their contact patches.

- **`WheelYawFollow`** (1.0) — wants to be 1. This is the fix.
- **`WheelRollFollow`** (0.0) — deliberately off. A real wheel stays flat on the road while the
  body leans over it, and at up to 16° of roll, wheels that leaned with the body would visibly
  lift off their contact patches. Turn it up only if you want the whole car to read as one rigid
  lump.

Pitch is never passed on: on a wheel it is a rotation about the axle axis, indistinguishable from
rolling a little further.

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

Unchanged in spirit from the old model — both of these are about the 40 m tile scale rather than
about the car.

- **`FallGravityMultiplier`** (3.0) — extra gravity while airborne **and descending only**.
  Leaving the ascent alone means a ramp still launches the car exactly as high; it just stops
  hanging at the apex.
- **`MaxFallSpeed`** (65 m/s) — terminal velocity, clamped along gravity in `_IntegrateForces`.
  Not a feel knob: it is the guard rail that stops a fall off the edge of the board outrunning
  the collision solver.

New, and needed because there is no suspension geometry to land on:

- **`AirborneUprightTorque` / `AirborneUprightDamping`** — levels the car so it lands on its
  wheels.
- **`AirborneSteerMultiplier`** (0.35) — air steering at full strength pirouettes the car off
  every jump.
- **`MaxPullForce`** (11000 N, near a full g) — the spring term goes negative when the ground
  drops away past the ride height, which glues the car over a crest. It does **not** flatten real
  jumps: a ray that has left the road entirely finds nothing to pull against, so the reach of the
  ray is what separates "following a crease" from "airborne", not the strength of this.

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
