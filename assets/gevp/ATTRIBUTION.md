# Godot Easy Vehicle Physics — attribution

**The physics port is gone.** `scripts/vehicles/` no longer contains any GEVP code — the
ray-cast tire model, suspension, drivetrain and assists were replaced wholesale by the
hovercraft model described in [`docs/vehicle-physics.md`](../../docs/vehicle-physics.md).

What remains from this project, and what this attribution now covers:

- **Sound samples:** `sounds/4000.wav` (engine) and `sounds/tires_squal_loop.wav` (tire
  squeal), taken unmodified from
  [Godot-Easy-Vehicle-Physics](https://github.com/DAShoe1/Godot-Easy-Vehicle-Physics) by
  David Shoemaker, used under the MIT licence in [`LICENSE`](LICENSE).

The physics port that used to live here was from commit
`c392257f54f6ca537dc10bc5badad0c060f18982` (2025-08-17); it is still on `main` if you need
to look at it.

The upstream project itself credits:

- [Dechode's Godot Advanced Vehicle](https://github.com/Dechode/Godot-Advanced-Vehicle) — Copyright (c) 2021 Dechode
- [Driving Simulator Workshop](https://lupine-vidya.itch.io/gdsim/devlog/677572/series-driving-simulator-workshop-mirror)
  by Baron Wittman — Copyright (c) 2024

Master Track does **not** vendor the demo cars, demo tracks or the Kenney car-kit meshes
from that repo.

See [`docs/vehicle-physics.md`](../../docs/vehicle-physics.md) for the model that replaced
the port, and how to tune a car.
