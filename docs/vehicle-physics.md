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
- **`MaxTorque` + `TorqueCurve`** — power. The curve's X axis is `rpm / MaxRpm`.
- **`CoefficientOfFriction["Road"]`** — overall grip.
- **`FrontTorqueSplit` / `VariableTorqueSplit`** — 0 is RWD, 1 is FWD. The racer ships as RWD
  that blends toward AWD when it detects slip, which is the arcade-friendly setup.
- **Assists** — `SteeringSlipAssist`, `CountersteerAssist`, `TractionControlMaxSlip`,
  `EnableStability`. Turn these down for a car that bites, up for one that flatters.

**Physics tick rate must stay at 120 Hz or higher** (`project.godot` sets it). The overlay
shouts at you in red if it drops. Handling changes when you change the tick rate.

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
