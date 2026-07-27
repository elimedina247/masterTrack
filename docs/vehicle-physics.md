# Vehicle physics

Master Track drives on a C# port of
[Godot-Easy-Vehicle-Physics](https://github.com/DAShoe1/Godot-Easy-Vehicle-Physics) (GEVP),
a ray-cast rigid-body vehicle. Godot's built-in `VehicleBody3D` is gone — nothing in the
project references it any more.

Licence and credits: [`assets/gevp/ATTRIBUTION.md`](../assets/gevp/ATTRIBUTION.md).

---

## Why a port and not the addon

The upstream project is GDScript. Master Track is a C# project, and `RacerController` has to
**be** the vehicle body: it carries the multiplayer authority, the `[Rpc]` hazard warnings and
the peer-ownership checks, all of which want to sit on the same node as the physics.

C# cannot inherit from a GDScript class. Keeping the addon as-is would have forced the racer
into a composition shape — a GDScript `Vehicle` root with a C# child poking at it through
`Call()` — and split the networking away from the body it is about. Porting keeps one
language, one node, and lets `RacerController : Vehicle` stay a two-line change.

The cost is that this no longer tracks upstream automatically. The port is from commit
`c392257` (2025-08-17).

---

## Layout

| File | Ported from | What it is |
|---|---|---|
| `scripts/vehicles/Vehicle.cs` | `vehicle.gd` | Body, motor, clutch, gearbox, differentials, assists. Every tuning knob lives here. |
| `scripts/vehicles/Wheel.cs` | `wheel.gd` | One ray-cast wheel: suspension, brush tire model, ABS. |
| `scripts/vehicles/Axle.cs` | `Axle` inner class | A pair of wheels, their brake bias and diff state. |
| `scripts/vehicles/VehicleInput.cs` | `vehicle_controllergd.gd` | Input as a value + the action-name map. |
| `scripts/vehicles/VehicleInputController.cs` | `vehicle_controllergd.gd` | Drop-in node that drives a vehicle from local input. |
| `scripts/vehicles/VehicleDebugOverlay.cs` | `debug.gd` + `debug_ui.gd` | The tuning overlay. |
| `scripts/vehicles/EngineSound.cs` | `engine_sound.gd` | RPM-pitched engine sample. |
| `scripts/vehicles/WheelSmoke.cs` | `wheel_smoke.gd` | Tire smoke on slip. |
| `scripts/vehicles/SurfaceGroups.cs` | — | Names of the surface node groups. |
| `scripts/ui/VehicleHud.cs` | `gui.gd` | Speed / RPM / gear readout. |

Not brought across: the demo cars, the demo track, the Kenney car-kit meshes, and
`camera.gd` — the project already has a better third-person rig in
`scripts/racer/CameraRig.cs`.

---

## Surfaces — the one thing that will bite you

A wheel identifies what it is driving on by reading the **first node group** on whatever its
ray cast hits, and looking that name up in the vehicle's tire dictionaries.

**Every drivable collider must be in a surface group** — `Road`, `Dirt` or `Grass` (see
`SurfaceGroups`) — **and it must be the first group on that node.** Put gameplay groups on
after the surface group, or on a different node.

A collider with no groups leaves the wheel on whatever surface it was already on. A collider
whose first group isn't a known surface logs one warning and is likewise ignored (upstream
would throw here).

---

## Controls

| Action | Key | Pad |
|---|---|---|
| Throttle | `W` / `↑` | Right trigger |
| Brake | `S` / `↓` | Left trigger |
| Steer | `A` `D` / `←` `→` | Left stick |
| Handbrake | `Space` | A |
| Nitro | `Shift` | B |
| Clutch | `C` | Left stick click |
| Auto/manual gearbox | `T` | LB |
| Shift up | `F` / `+` | X |
| Shift down | `R` / `-` | Y |
| Physics debug overlay | `` ` `` | Right stick click |
| Cycle debug pages | `,` `.` | D-pad ← → |
| Free-look camera | hold right mouse | — |

Holding the brake at a standstill swaps between first and reverse — that is the gearbox
working as designed, not a bug.

---

## Tuning

Everything is on the `Racer` root node in `scenes/Racer.tscn`, grouped in the inspector.
Turn the overlay on with `` ` `` and page through with `,` / `.` — the numbers only start
making sense once you can see what the tires are doing.

Start here:

- **`VehicleMass`, `FrontWeightDistribution`** — spring rates, damping and brake bias are all
  *derived* from these. Set them before touching anything else.
- **`CenterOfGravityHeightOffset`** — the single biggest lever on how much the car rolls and
  how easily it spins.
- **`MaxDriveForce` + `DriveCurve`** — power. The curve's X axis is `speed / TopSpeed`, and
  `MaxDriveForce / VehicleMass` is the launch acceleration in m/s². Move
  `LongitudinalGripRatio["Road"]` with it: what sets the car's character is the ratio between
  the two, not either on its own.
- **`CoefficientOfFriction["Road"]`** — overall grip.
- **`FrontTorqueSplit` / `VariableTorqueSplit`** — 0 is RWD, 1 is FWD. The racer ships as
  plain RWD (`VariableTorqueSplit` is off); turn it on to blend toward AWD under slip.
- **Assists** — `SteeringSlipAssist`, `CountersteerAssist`, `TractionControlMaxWheelSpin`,
  `EnableStability`. Turn these down for a car that bites, up for one that flatters.
- **`DownforceG`** — what stops the car understeering worse and worse as it speeds up. See
  **Aero and airborne** below; reach for it before you touch `MaxSteeringAngle`.

**Physics tick rate must stay at 120 Hz or higher** (`project.godot` sets it). The overlay
shouts at you in red if it drops. Handling changes when you change the tick rate.

### Aero and airborne

Two additions that aren't in GEVP. Both exist because the track is built on **40 m cubes**,
which makes this a much bigger world than a normal car sim is tuned for.

Cornering radius is `v² / (μg)`. Grip is flat with speed, the force a corner demands grows with
its square, so with no aero the car understeers worse the faster it goes — at 200 km/h on
1.8 μ the radius is about 186 m, against a 40 m tile. No amount of steering lock fixes that,
because above roughly 23 km/h the car is grip-limited rather than lock-limited.

- **`DownforceG`** (1.5) — downforce at `TopSpeed`, as a multiple of the car's own weight.
  Scales with v², so it's absent when you're crawling and largest exactly where the problem is.
  0 disables it.
- **`DownforceBalance`** (0.5) — front's share. Rearward for stability at speed, forward to
  keep the nose alive in a fast corner.

It arrives as **tire load, not a force on the chassis**. Pressing the body down would be the
physical route, but spring rates are derived so static weight already sits at `RestingRatio`
(half) of the travel — one g of downforce would park the car on its bump stops. Adding it to
the normal load in the brush model instead means grip without the ride height collapsing. It
also means an airborne wheel gets none of it, so **jump arcs are unchanged**.

Falling is the other half. A 40 m drop under real gravity is **2.9 seconds** of hang time,
which is correct and unplayable:

| Fall gravity | Hang time, one cube | Impact |
|---|---|---|
| 1.0× | 2.86 s | 28 m/s |
| 2.5× | 1.81 s | 44 m/s |
| **3.0×** (default) | **1.65 s** | 49 m/s |
| 4.0× | 1.43 s | 56 m/s |

- **`FallGravityMultiplier`** (3.0) — extra gravity while airborne **and descending only**.
  Leaving the ascent alone means a ramp still launches the car exactly as high; it just stops
  hanging at the apex. That asymmetry is what reads as weight rather than heaviness.
- **`MaxFallSpeed`** (65 m/s) — terminal velocity, clamped along gravity in `_IntegrateForces`.
  Not a feel knob: drag alone puts the real terminal velocity near 310 m/s, so this is the
  guard rail that keeps a fall off the edge of the board from outrunning the collision solver.
  Horizontal speed is untouched. 0 disables it.

Don't reach for global gravity or `gravity_scale` instead. `CalculateSpringRate` is fed a
hardcoded `4.9` (half of g), so a car that simply weighed more would sit bottomed out.

### Nitro

Five charges per run, spent one per press, never refilled — `ResetNitro()` on the vehicle puts
them back and is what a race start should call. `TryActivateNitro()` fires one without a button
press, for pickups or AI. Both `NitroFired(chargesRemaining)` and `NitroEnded()` are signals, so
audio and VFX can hang off them without polling.

The push goes in at the **body**, along the nose, not through the drivetrain. Routed through the
wheels a boost gets eaten by traction control, by wheelspin, by a rear axle that is sideways and
by the drive curve being flat at the top of the range — it would do least exactly when the player
expects most. Applied to the body it lands whatever the car is doing.

Knobs are under **Nitro**:

- **`NitroForce`** (9000 N) — the shove. Divide by `VehicleMass` for the acceleration it adds:
  7.5 m/s² on the 1200 kg racer, on top of whatever the wheels are already doing.
- **`NitroTopSpeedMultiplier`** (1.25) — hard speed cap while boosting, as a multiple of
  `TopSpeed`. 250 km/h on the racer's 200. Both the drive curve *and* the push itself fade out
  approaching it, so charges chained back to back hit a limit instead of stacking velocity.
- **`NitroCharges`** (5), **`NitroDuration`** (1.5 s), **`NitroCooldown`** (0.4 s dead time
  after a burst, so all five can't be dumped in one press-mash).

`SpeedFraction` deliberately stays scaled to the *unboosted* top speed — it is what the engine
note is pitched from, and rescaling it under nitro would drop the revs at the moment of the shove.

### Handbrake

Four knobs under **Braking → Handbrake**, in the order worth reaching for:

- **`HandbrakeLockedGrip`** (0.15) — lateral grip a locked rear tire keeps. This is the one
  that decides whether the car rotates. Lower slides more; 0 is a rear axle on ice.
- **`HandbrakeStabilitySuppression`** (1.0) — how much of the yaw stability assist the lever
  switches off. At 0 the assist stays on and will cancel the slide as fast as the tires can
  start it, whatever the other three say. Turn it down only if you want a car that resists
  being thrown around.
- **`HandbrakeForceMultiplier`** (1.5) — handbrake torque per rear wheel as a multiple of the
  total footbrake torque. Above ~1 the rears lock more or less on contact, so this mostly
  changes how fast they get there, not whether.
- **`HandbrakeLockSlip`** (0.7) — how much longitudinal slip counts as fully locked. Raise it
  for a slide that takes longer to build.

Grip is given up in proportion to the slip the tires actually produced, not to the button, so
a stationary car keeps its grip however hard the lever is pulled. A straight-line handbrake
pull still gives a straight skid: locking the rears makes the car unstable in yaw, it doesn't
create yaw. Carry a little steering into it and the back end goes.

### Wheel geometry

A wheel's `RayCast3D` node sits at the **top of the suspension travel**, not at the wheel
centre. At rest the wheel centre hangs `SpringLength × RestingRatio` below it. So for a wheel
centre at ride height `r` with tire radius `t`:

```
raycast Y = t + SpringLength × RestingRatio
```

That is why the racer's front rays sit at `0.475` and the rears at `0.5` — different spring
lengths, same 0.4 m wheel centre, level body. If you change a spring length, move the ray.

The mesh goes under the wheel's `WheelNode` (a plain `Node3D`), and must face **+Z**.

---

## Deviations from upstream

Behaviour is identical at default settings. These are the deliberate changes:

1. **Language.** C#, `MasterTrack.Vehicles` namespace, PascalCase members. Every export keeps
   its meaning and default.
2. **Unknown surface groups warn once and keep the previous surface** instead of throwing.
3. **A missing `TorqueCurve` falls back to a flat curve** with a warning instead of crashing.
4. **`BrakeForceMultiplier` is actually applied.** Upstream declares it and never reads it.
   The default of `1.0` reproduces the original exactly.
5. **A wheel adds its own vehicle as a ray-cast exception**, so a ray starting on the
   chassis' surface can never hit the car it belongs to.
6. **The debug overlay is one `Control`** rather than a `Node` plus a child `Control`, and it
   rebuilds its draw list every frame instead of keeping a dictionary of named shapes.
7. **Input is a value type** (`VehicleInputState`) separate from where it was sampled, so the
   same mapping can later be fed from the network for server reconciliation.
8. **Input actions use the project's `snake_case` names** (`racer_accelerate`,
   `racer_steer_left`, …) rather than upstream's `"Throttle"`, `"Steer Left"`, …
9. **`can_sleep = false` on the racer body**, so a car waiting on the start line can't be put
   to sleep by the physics server.
10. **The handbrake actually breaks the rear axle loose.** Upstream's handbrake adds its force
    into the shared brake force and then sends it through the front:rear bias split, so the
    axle it is meant to over-brake receives the smaller share of it — and because the shared
    field is never reset within a step, the force leaks onto whichever axle is processed
    afterwards. Here it is a separate torque applied straight to the handbrake axle, and three
    things that were cancelling the slide are stood down while the lever is up: the yaw
    stability assist (`HandbrakeStabilitySuppression`), the braking grip bonus, and the rear
    tires' lateral grip (`HandbrakeLockedGrip`). See **Handbrake** under Tuning.
11. **Rear ABS works.** Upstream disables ABS on the handbrake axle unconditionally, which
    left `RearAbsPulseTime` and `RearAbsSpinDifferenceThreshold` inert. It is now disabled only
    while the handbrake is actually pulled.
12. **Downforce**, which GEVP has no concept of. Added as tire normal load rather than a body
    force, for the ride-height reason in **Aero and airborne**. Set `DownforceG = 0` for
    upstream behaviour.
13. **Fall gravity and terminal velocity.** Also not upstream — both are about the 40 m tile
    scale rather than the car. `FallGravityMultiplier = 1` and `MaxFallSpeed = 0` restore plain
    Godot gravity.

Two GDScript behaviours are reproduced on purpose rather than "fixed":

- **`GearRatioAt` wraps negative indices** the way GDScript arrays do. The transmission
  genuinely relies on this: in neutral or reverse it computes a gear RPM from index `-1` or
  `-2` before deciding whether to use it.
- **`VehicleMath.SignF` returns a float and returns `0` for `0`**, matching GDScript's
  `signf`. C#'s `Math.Sign`/`Mathf.Sign` return `int`, and several force calculations depend
  on multiplying by an exact zero.

Three exports remain **inert**, exactly as upstream: `MotorBrake`, `ThrottleSteeringAdjust`
and `AutomaticTimeBetweenShifts`. They are declared, documented and never read. They're kept
so a tune copied from a GEVP car transfers cleanly.

---

## Known noise

One warning at exit is expected and harmless: Godot reports the looping engine sample as a
leaked `AudioStreamWAV` during shutdown. It's a teardown-order quirk with autoplay looping
audio, not a runtime problem.
