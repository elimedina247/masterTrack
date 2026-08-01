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
| **Suspension** | Each ray applies `(compression × k) − (closing speed × c)` along **the chassis' up** | `GroundRay.ApplyGroundForce` |
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
| 0b | **Fall gravity** | `_IntegrateForces`, velocity | — | descending only |
| 1 | **Spring + damper**, ×4 | each ray, along **world up** | yes | — |
| 1b | **Bump stop** | folded into #1 past 60% travel | yes | — |
| 1c | **Downward pull** | each ray, capped + faded | over crests | — |
| 2 | **Drive / brake** | centre of mass, along nose | × `GroundFraction` | **none** |
| 3 | **Grip** | centre of mass, along `Basis.X` | × `GroundFraction` | **none** |
| 4 | **Steering torque** (PD → heading) | about **world up** | yes | **stood down** |
| 5 | **Flip rate torque** | about car's `Basis.X` | — | yes |
| 5b | **Air yaw rate torque** | about **world up** | — | yes |
| 5c | **Air nitro thrust** | centre of mass, along nose | — | `AirNitroDuration` after a nitro |
| 5d | **Hop impulse** | centre of mass, along **world up** | on press | — |
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

### In the air the car is a projectile, except when the nitro is lit

**Nothing but gravity touches its velocity**, unless a boost is burning. Both #2 and #3 are scaled
by `GroundFraction` and skipped outright when it reaches zero, so the only things acting on an
airborne car are gravity (times `FallGravityMultiplier` on the way down), the `MaxFallSpeed` clamp,
whatever torques the player is applying, and #5c.

This is a rule, not a tuning choice, and it is worth defending. Drive points along the nose and
grip points along `Basis.X` — both are **body axes**. Any amount of either left running in the
air would mean rotating the car changed the direction its velocity was pushed or scrubbed in,
which quietly turns an orientation control into a thrust control. A flip would accelerate you.

**`AirNitroForce` is the one deliberate exception**, and it breaks exactly that rule on purpose.
Before it, a nitro fired in mid-air spent a charge, lit the flames, raised the speed target — and
then delivered none of it, because there was no force left to deliver it with. Paying for nothing
is worse than not being allowed to pay.

So for `AirNitroDuration` after a nitro is fired, an airborne car gets thrust along its nose at
`AirNitroForce` (14000 N, matched to `BoostAccelForce`), held to the same `TopSpeed + BoostSpeed`
ceiling the grounded solve drives at so the air is never the better place to spend a charge.
Pitching the car now steers that thrust, which is the feature: nose down to dive onto a landing
you were going to miss, nose up to hold a jump out. The distinction that keeps the rule honest is
that this is **opt-in and finite** where a flip accelerating you would have been free and
permanent. Set `AirNitroForce` to 0 to get the pure projectile back.

Two details do the load-bearing work there:

- **`AirNitroDuration` (0.35 s) is its own timer, not a slice of the burst.** On the ground a boost
  is a 1.5 s run you hold; in the air it is a punch that changes where you land. A second and a
  half of free thrust up there is a flying car.
- **Only `TryActivateNitro` sets that timer.** A *drift* boost still burning as the car leaves a
  ramp gives no thrust at all — a drift boost is finite but not opt-in, so it does not clear the
  bar the exception was argued on. Boost pads never came into it: they are a direct
  `ApplyCentralImpulse` from the tile and never touch `BoostTimeRemaining`.

The clock starts when the charge is spent, wherever the car is. Fire one on the road and the air
window is long gone before the next ramp — correct, because that thrust was already spent pushing
you along the ground.

### The hop is the other thing that is not gravity

`HopImpulse` (8 m/s) springs the car straight up along **world** up, as an impulse scaled by mass
so every car hops the same height. At this project's 2 g that is about 1.6 m — enough to clear a
log, unstick off a crease or get the nose over a kerb, and nowhere near enough to skip a hazard,
which is the line it must not cross.

It is **grounded only**, and that is what keeps it from touching the projectile rule at all: with
at least one wheel down there is something to push against, and in the air the button does
nothing. Repeatable upward impulses with nothing to push against is a helicopter. `HopCooldown`
(0.35 s) stops a landing being immediately re-hopped, which would let a player bunny-hop a whole
washboard and quietly defeat every tile that works by unsettling the car.

World up rather than chassis up on purpose: chassis-up would fire the car off the side of a banked
corner at whatever angle it was sitting at, which is a launch pad, not a hop.

The failure mode is easy to reintroduce and hard to spot, because it looks tiny in the source.
The old airborne grip was 5%, which sounds negligible — but grip removes that fraction of the
velocity along its axis *every physics step*, and at 120 Hz even 5% is 99.8% of that component
gone inside one second. With the car rolled, that axis had a vertical component, so it ate the
car's fall speed and gravity appeared to switch off whenever you spun.

**Any per-step percentage applied along an axis that is free to rotate is a bug waiting to
happen.** Scale it by `GroundFraction`.

### Gravity has to survive a slope

The springs push along **the chassis' own up**, not world up. Walaber uses world up, and this did
too until it met a 40° ramp.

A world-up spring has to push with `mg` to hold the car off a surface *at any angle*. And `mg` up
against `mg` down is zero net force in **every** direction — including along the slope. Gravity
stops existing on a gradient entirely: no speed gained downhill, none lost climbing, and a car
left on a 40° ramp sits there motionless.

Along the chassis' up it pushes `mg·cosθ` instead, leaving gravity's `mg·sinθ` along the slope
unopposed. That term is the whole of what makes a hill a hill:

| Slope | Gravity along it | Accel downhill | Accel uphill | Coasting |
|---|---|---|---|---|
| flat | 0 N | 15.0 m/s² | 15.0 m/s² | −2.5 m/s² |
| 23° (one-cube ramp) | 4595 N | **18.8 m/s²** | 11.2 m/s² | +1.3 m/s² |
| 40° (two-cube ramp) | 7559 N | **21.3 m/s²** | 8.7 m/s² | +3.8 m/s² |

The ray direction rather than the surface normal, because the normal jumps several degrees at
every facet joint on a ramp and a spring chasing it would kick the car sideways at each one. The
chassis' own up turns smoothly and settles parallel to the road anyway.

Two consequences worth knowing:

- **A car will not hold station on a ramp.** Coasting retardation is 3000 N against 4595 N of
  gravity at 23°, so it rolls back. That is correct, and it is new.
- **Ride height rises slightly on a slope**, since the spring only carries `mg·cosθ` — 5.4 cm of
  compression at 40° against 7 cm on the flat. Not enough to notice.

### The throttle must not brake you

The drive force limit is **asymmetric**: how hard the car may be pushed *along* its nose is a
different question from how hard it may be pushed *against* it.

```csharp
ApplyCentralForce(forward * Mathf.Clamp(accelForce, -backwardLimit, forwardLimit));
```

With a single limit the throttle brakes you. Past the target speed the solve goes negative, and
one limit makes that negative as strong as the engine — so running downhill under power the car
fights gravity with 18000 N to pin itself at exactly `TopSpeed`, which is the *opposite* of what a
hill should do.

Retardation is capped at `MaxCoastForce` unless the player is actually on the brake. So a hill can
run the car past its own top speed — bounded, because the ramps are short: 211 km/h off a 23°
descent and 236 km/h off a 40° one, against a 200 km/h `TopSpeed`. It also means a boost bleeds
off smoothly instead of being slammed back down when it expires.

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

### The tires need a limit, or momentum dies on landing

`GripFactor` is a **time constant, not a force**. It removes a percentage of the sideways
velocity every physics step, so the harder the car is thrown sideways, the harder it grips back —
without limit:

| Landed sideways at | Lateral deceleration asked for | Force |
|---|---|---|
| 5 m/s | 600 m/s² (61 g) | 0.65 MN |
| 20 m/s | 2400 m/s² (245 g) | 2.6 MN |
| 55 m/s | 6600 m/s² (**674 g**) | **7.1 MN** |

A tire allowed to be infinitely strong stops the car dead in its own length. Twist the car in
mid-air, land sideways at speed, and every bit of that momentum vanished in about twenty
milliseconds.

**`MaxGripForce`** (100000 N, ≈8.5 g on the racer) caps it, scaled by `GroundFraction` so a car
touching down on one wheel slides further before it hooks up. Momentum now survives a bad landing
and scrubs off over time:

| Landed sideways at | Slides | Over |
|---|---|---|
| 20 m/s | 2.4 m | 0.24 s |
| 40 m/s | 9.6 m | 0.48 s |
| 55 m/s | 18.2 m | 0.66 s |

Ordinary driving never reaches the cap — steady cornering asks for about 60000 N. What it does
change is **cornering at boosted speed**, which is now genuinely grip-limited:

| Speed | Tightest radius held without sliding |
|---|---|
| 40 m/s | 19 m |
| 55 m/s (`TopSpeed`) | 36 m |
| 75 m/s (chained boost) | 68 m |

**This table is why corners are built the way they are.** A quarter turn pivoted inside a single
cell has a radius of half a tile whatever else is done to it, and no tile size makes half of one
reach 68 m. So turns sweep a square block of cells instead — 90 m of radius at the default span and
150 m for a sweeper — which the numbers above say is holdable well past `TopSpeed`. See
`TileData.IsWideTurn`.

The corners are also **banked**, and that leans on the slope behaviour above rather than on grip. A
bank holds a car with no help from the tires at all at `v = sqrt(g·r·tanθ)`; at 2 g, a turn's outer
radius and the 60° at the top of the bank, that is about 210 km/h — so riding the wall flat out is
the car being carried by its own cornering, and a driver who takes the high line too slowly slides
back down it because `mg·sinθ` along the surface is left unopposed. Nothing was added to the
vehicle to make that work.

Raise `MaxGripForce` if boosted cornering feels too loose; lower it for longer slides everywhere.

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
| **Hop** | `E` | X |
| **Flip** (airborne) | `W` nose **down** / `S` nose **up** | Triggers |
| **Turn** (airborne) | `A` `D` / `←` `→` | Left stick |
| Nitro | `Shift` | B |
| Reset | see `racer_reset` | — |
| Physics debug overlay | `` ` `` | Right stick click |
| Cycle debug pages | `,` `.` | D-pad ← → |
| Free-look camera | hold right mouse | — |

**Reset also refills the nitro, but only on the proving ground.** `RacerController.Respawn` calls
`ResetNitro` when `RefillNitroOnReset` is set, and `PhysicsTestArea` is the only thing that sets
it. In a match the charges are meant to last a whole run, so a reset that handed them back would
make deliberately wrecking the car the cheapest way to get five more — the reset is there so a bad
landing doesn't end someone's race, not as a pit stop. On the pad there is no run to spend them
over, so the alternative is leaving and re-entering the lobby to try a jump on a fresh set.

The drift button is still bound to the `racer_handbrake` action, so existing keyboard and pad
bindings carry over — the button is in the same place, it just does something else now. The
clutch and shift bindings are gone: there is no gearbox, and reverse engages by holding the
brake below `ReverseEngageSpeed`.

**The pedals do not swap in reverse.** Brake is reverse, throttle is forward, in both gears.
Upstream swapped them over so "forward on the stick" always meant "away from the nose", and that
was carried across and then quietly broke reverse for a long time: `ProcessDrive` takes the
reverse target off `BrakeAmount` and latches back into forward off `ThrottleAmount`, so the swap
fed each pedal to the wrong half of that pair. Holding the brake to reverse put its strength on
the throttle, which flipped the gear straight back, while the brake amount the speed is actually
taken from decayed away. The gear sawtoothed every frame or two and reverse crawled at a fraction
of `ReverseTopSpeed`. Nitro in the air is worth reading against this one: both were cases where
the input arrived and the force never did.

---

## Drifting

A drift is a **state you ask for**, not something the physics decides has happened. Press the
drift button above `DriftMinSpeed` **while steering** and:

- the heading is offset by `DriftAngle` (35°) in the drift direction,
- `GripFactor` is multiplied by `DriftGripMultiplier` (0.28), so the car actually slews,
- both ramp in over `DriftBlendSpeed` rather than snapping.

Direction comes from the stick at the moment of the press, and there has to be one:
`DriftSteerThreshold` (0.15) is how far the steering must be deflected for the button to do
anything at all.

**That threshold is a gate, not a tiebreak.** The car used to fall back on its own yaw rate when
the stick was centred, which meant throttle-and-drift with no steering picked a side off whatever
rotation happened to be there and slid anyway — so the quickest way through most corners never
involved steering into them. A drift is a corner taken sideways; it is entered by turning in and
committing.

Read at the press only. Once a drift is running the stick is free to come back to centre, which is
what holding a long slide looks like. The press has to *coincide* with the steering, though —
pressing the button on the straight and turning afterwards starts nothing, because the rising edge
has already been spent.

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

### The bleed stops while a drift is held

Additive only pays if the next boost lands while there is still something to add to, and that is
where chaining used to quietly fall apart. A tier-1 boost is 8 m/s over a 1.0 s burst, and at
14 m/s it is gone **0.57 s** after the burst ends — less than the 0.55 s of drifting needed to
earn the next link, before counting any time at all spent getting into it.

`ChainGraceTime` did not agree with that. It accepts a new drift up to 0.5 s after the burst ends,
but for tier 1 the last moment a stack could actually land was 1.02 s in, against a counter that
kept saying yes until 1.50 s. Enter in that gap and `ChainCount` went up while `BoostSpeed`
restarted from zero — the overlay said you had chained and the car disagreed.

So the bleed is **paused for as long as a drift is being held**. A drift is the player working on
the next link; draining the last one while they earn it made chaining a fight against a clock
rather than against the corner. Nothing else needed retuning — with the freeze, any entry the
grace window accepts now produces a real stack, which is what the counter was claiming all along.

Note this pauses `BoostSpeed` only. The burst's own countdown (`BoostTimeRemaining`, and with it
`BoostAccelForce` and the exhaust flames) keeps running during a drift — you hold the *speed* you
earned, not a permanently lit boost.

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

There is no auto-level: a car that takes off crooked lands crooked. `FallGravityMultiplier` **is**
back, after both it and the upright assist were removed together — see below for why only one of
them came back.

But gravity itself is **2 g**. The racer sets `gravity_scale = 2.0` on the rigid body, because at
1 g the car floats: a 40 m drop hangs for 2.86 seconds, which is correct and unplayable.

**Mass is not the knob here, and reaching for it is the natural mistake.** A 1200 kg car and a
4800 kg car fall at exactly the same rate — gravity is an acceleration and mass cancels out.
Making the car heavier only makes it accelerate and corner worse. Airtime comes from gravity and
from launch speed, nothing else.

| `gravity_scale` | 40 m drop | Small ramp hop | Impact from 40 m |
|---|---|---|---|
| 1.0 | 2.86 s | 1.63 s | 28.0 m/s |
| **2.0** | **2.02 s** | **0.82 s** | **39.6 m/s** |
| 3.0 | 1.65 s | 0.54 s | 48.5 m/s |

Symmetric, unlike the old descent-only multiplier: it tightens the whole arc rather than just the
hang, which is what reads as weight rather than as the car being yanked down at the apex.

**If you change it, scale the springs with it.** They carry `mass × g / 4` at rest, so at 2 g the
car sinks to 14 cm of static compression instead of 7 and loses a third of its travel. The scene
runs `SpringStrength = 84000` and `SpringDamping = 7200` — both exactly double the code defaults —
which puts the ride height back where it was.

The aerial controls are **orientation only**. They apply torque and nothing else — no drive, no
grip. The single thing in the air that is not a torque is `AirNitroForce`, and it is not one of
these controls: it costs a charge and runs for `AirNitroDuration` only. The hop is grounded-only
and never applies up here at all. See **In the air the car is a projectile, except when the nitro
is lit** above for why the line is drawn there.

- **`MaxFallSpeed`** (65 m/s) — the clamp, in `_IntegrateForces` just after the fall multiplier has
  been applied. Not a feel knob: it is the guard rail that stops a fall off the edge of the board
  outrunning the collision solver. It bites sooner now that tiles are 60 m — a two-cube climb is
  120 m up, and a fall from there would otherwise reach 68.6 m/s.
- **`MaxPullForce`** (11000 N) and **`PullFadeDistance`** (0.12 m) — the spring term goes negative
  when the ground drops away past the ride height, which glues the car over a crest. The fade is
  what separates a crease from a cliff: a crease drops the road away by centimetres and wants
  holding onto, an edge drops it away by everything and wants letting go of. Without it, a pull
  strong enough to follow a crease also sucks the car back down off every jump.

### One of the two assists came back, and the difference matters

Both were removed together. Only `FallGravityMultiplier` returned, because only one of them was
ever an assist:

| | What it does | Verdict |
|---|---|---|
| `FallGravityMultiplier` (1.6) | Extra gravity on the **descent only** | **Back.** It does not drive the car, it does not decide anything for the player, and it cannot change where a jump goes — it only stops the arc hanging at the top |
| `AirborneUprightTorque` | Levelled the car toward flat | **Still gone.** It flew the car for you. A car that takes off crooked **lands crooked**, and on its roof it stays there until `racer_reset` |

The reason the multiplier is honest is that it is symmetric in everything the player controls: the
car leaves a ramp at the same speed, on the same angle, and reaches the same height. Raising
`gravity_scale` instead would tighten the whole arc and cut how high the ramp threw you; raising the
mass would do nothing at all, because gravity is an acceleration and mass cancels.

**It is gated on `GroundFraction == 0`, not on falling**, and that gate is the whole of what keeps
the springs out of it. A car sitting still has a small negative vertical velocity nearly every step
as the suspension breathes, so a check on the velocity alone would multiply gravity on a parked car
— which sinks it into its own travel and means every spring rate has to be rescaled to get the ride
height back. Off the ground there is no spring to fight, so nothing else had to move.

And one thing neither of them covered, now exposed: **roll is neither driven nor damped.** Pitch
and yaw are rate controlled, so releasing the stick settles them — nothing touches roll at all. A
car that leaves the ground rolling keeps rolling until it lands, and there is no input bound to
correct it. The overlay's **Airborne** page shows the roll rate.

If landing on the roof becomes the problem, binding roll is a better answer than reinstating an
assist.

### Flying the car

Air rotation is **rate controlled, not torque controlled**. Each axis is driven toward a target
angular rate, so the export *is* the fastest the car will ever go round — rather than an
acceleration that keeps adding for as long as the button is held, which lets the car wind itself
into a spin it cannot recover from. Releasing drives the rate back to zero, which is also what
bleeds off a tumble from a bad take-off; there is no separate damping term.

- **`AirPitchRate`** (140 °/s) — brake pitches the nose **up**, throttle pitches it **down**;
  flight-stick sense, where pushing forward drops the nose.
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

## Car-to-car impacts

Cars are simulated on exactly one machine each; everyone else's car here is a frozen kinematic
puppet slid along its replicated pose. The solver therefore sees a remote car as an **unlimited
mass with zero velocity**: leaning on one works (its advancing pose genuinely shoves you), but a
real collision comes out as bouncing off scenery — no momentum exchange, and no consequence for
the car that was hit. The impact layer in `RacerController.ProcessCarContacts` adds the movie on
top, Burnout-style and deliberately over the top.

**Each machine only ever touches its own car.** Both sides detect the same contact — you against
their puppet, them against yours — and each applies its own share of a reaction computed from the
same replicated data (`NetVelocity` rides alongside the pose for exactly this). Nothing is
negotiated and no authority moves; the two responses agree because the rule is symmetric even
though the shares are not.

A hit is four things, all scaled by closing speed up to `ImpactFullSpeed`:

1. **Launch** — `ImpactBounce` × closing speed along the contact normal. Above 1.0 on purpose:
   the solver already did the physically honest part, this is theatre.
2. **Pop** — `ImpactPop` × closing speed straight up. Lifting the victim off the road hands them
   to the airborne rules (no grip, no drive, a projectile), which is what sells the takeout.
3. **Spin** — up to `ImpactSpinRate` of yaw, signed by which side of the centre of mass the hit
   landed. Clipping a rear quarter pirouettes them.
4. **Stun** — up to `ImpactStunTime` during which grip, steering torque and drive force are
   scaled down by the `Stun*Loss` fractions, fading back linearly. This is the load-bearing one:
   the launch and spin are just velocity, and this car's grip and steering solvers exist to
   delete unwanted velocity within a few frames. The stun is the window in which they aren't
   allowed to, so the spin actually *runs*.

**Who was the victim matters.** The car that got driven into receives the full reaction; the one
that did the driving receives `ImpactRammerScale` of it. Split it evenly and rear-ending someone
stops *you* dead — punishing exactly the play the mechanic rewards. Head-ons land in the middle
and wreck everybody.

The launch and pop are deliberately **unclamped** above `ImpactFullSpeed` right now (severity —
spin and stun — is clamped). Two boosted cars meeting head-on will produce something absurd.
That is the current tuning philosophy: cranked to 10 first, then walked back by feel. The knobs
all live in the `Car Impact` export group on `Vehicle`.

---

## Known noise

Two warnings at exit are expected and predate all of this: `2 ObjectDB instances were leaked`
and `1 resources still in use`. Ignore them.
