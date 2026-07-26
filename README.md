# Master Track

**Master Track** is an asymmetric multiplayer game where one **Track Master** builds a
deadly racetrack in real time while the **Racers** try to survive it and beat each other
to the finish.

---

## The Concept

Two very different games happening at once, on the same track:

### The Track Master (1 player)
- Plays a top-down, **board-game-like overview** of the track.
- Their job: **build the racetrack in real time** as the Racers drive it.
- Each round they are dealt a **hand of tiles**. Every tile carries a specific, visible
  **hazard** — e.g. `Jump Ahead`, `Loop Ahead`, `Hairpin Turn`.
- They place tiles ahead of the racers to slow them down, trap them, and thin the herd.
- Goal: **defeat the Racers** — the track is their weapon.

### The Racers (2+ players)
- Race in **third person**, driving the track the Track Master is building.
- They **watch the tiles fall into place from the sky** just ahead of them.
- When a tile lands **3 tiles ahead** of a Racer, they get a **notification of that
  tile's hazard**.
- Core tension: they must **remember the upcoming hazards** they were warned about and
  react in time — memory is a survival skill.
- Goal: **survive the track and finish ahead of the other Racers.**

---

## Core Loop

1. **Round start** — the Track Master is dealt a fresh hand of hazard tiles.
2. **Build phase (live)** — as Racers drive, the Track Master places tiles ahead of them.
3. **Warning** — each Racer is notified when a tile lands 3 tiles ahead, with its hazard.
4. **React & remember** — Racers navigate the hazards, recalling what's coming.
5. **Repeat** — tiles keep falling; the track grows until a win condition is met.

---

## Roles at a Glance

| | Track Master | Racer |
|---|---|---|
| **Camera** | Top-down board overview | Third person, behind the car |
| **Input** | Place tiles from a hand | Drive / steer / react |
| **Knows** | The whole track & its hazards | Only hazards 3 tiles ahead (then must remember) |
| **Wins by** | Wiping out / stalling the Racers | Surviving and finishing first |
| **Count** | 1 | 2+ |

---

## Multiplayer Design (important — built in from day one)

The whole game is networked. Architecture notes so we keep this in mind while building:

- **Authority model:** the **host is the server / authority** for game state
  (the "real" track, tile placement validation, race positions, win conditions).
  Clients send intent (place-tile requests, driving input) via **RPCs**; the server
  validates and broadcasts results.
- **Track state is authoritative on the server.** Tile placements from the Track Master
  are *requests* the server confirms, then replicates to every peer so all clients see
  the same track fall into place.
- **Racer movement:** client-side input, server-reconciled. Racers' transforms are
  synchronized so everyone sees everyone.
- **Hazard notifications** (the "3 tiles ahead" warning) are computed against the
  authoritative track and pushed to the relevant Racer.
- **Roles are assigned by the server** at match start (one Track Master, rest Racers).

### Godot building blocks we're using
- **`ENetMultiplayerPeer`** — host / join transport (see `NetworkManager`).
- **`MultiplayerAPI` + `[Rpc]` methods** — client → server intent, server → client events.
- **`MultiplayerSpawner`** — replicate racer cars as they're created.
- **`MultiplayerSynchronizer`** — sync transforms / state on replicated nodes.

Tiles are deliberately **not** replicated as nodes. The server broadcasts a confirmed
placement (a catalog index), and every peer rebuilds the same tile from it — the track is
fully determined by the list of placements, so nothing about the geometry goes over the wire
and a client can't place a tile by lying about its shape.

---

## Driving

The car runs on a C# port of
[Godot-Easy-Vehicle-Physics](https://github.com/DAShoe1/Godot-Easy-Vehicle-Physics) — a
ray-cast rigid body with real suspension, a brush tire model, a clutch and gearbox, and a
stack of driver assists. Godot's built-in `VehicleBody3D` is no longer used anywhere.

Full write-up, tuning guide and the deviations from upstream:
**[`docs/vehicle-physics.md`](docs/vehicle-physics.md)**.

Surfaces are read from **node groups** — every drivable collider must be in `Road`, `Dirt`,
`Grass` or `Ice`, and it has to be that node's *first* group.

---

## Project Structure

```
master-track/
├── project.godot                 # autoloads, input map, 120 Hz physics
├── masterTrack.csproj            # .NET / C# project
├── docs/
│   └── vehicle-physics.md        # the ported physics: how to tune it, what changed
├── assets/
│   ├── CC96/                     # car body + rim meshes
│   └── gevp/                     # engine sample + upstream licence/attribution
├── scenes/
│   ├── Main.tscn                 # entry point: host / join / solo menu
│   ├── Game.tscn                 # the match: track, racers, board view, HUD
│   ├── Racer.tscn                # the car: rigid body + 4 raycast wheels
│   └── TestArea.tscn             # scratch area for driving
└── scripts/
    ├── networking/
    │   ├── NetworkManager.cs     # autoload: host/join, peer lifecycle
    │   └── GameManager.cs        # autoload: roles, rounds, game state
    ├── vehicles/                 # the ported vehicle physics
    │   ├── Vehicle.cs            # body, motor, clutch, gearbox, assists — all tuning
    │   ├── Wheel.cs              # one raycast wheel: suspension, tires, ABS
    │   ├── Axle.cs               # a pair of wheels + their differential
    │   ├── VehicleInput.cs       # input as a value + the action map
    │   ├── VehicleDebugOverlay.cs# the tuning overlay
    │   ├── SurfaceGroups.cs      # Road / Dirt / Grass / Ice group names
    │   └── EngineSound.cs, WheelSmoke.cs, VehicleInputController.cs
    ├── tiles/
    │   ├── TileHazard.cs         # enum of hazard types
    │   ├── TileData.cs           # data for a single tile (hazard + exit turn)
    │   ├── TileCatalog.cs        # every tile type + grid <-> world helpers
    │   ├── TrackDirection.cs     # N/E/S/W and the turn maths
    │   ├── TrackGrid.cs          # the track model: cells, order, placement rules
    │   ├── TrackController.cs    # authoritative track + placement replication
    │   └── TrackTile.cs          # a placed tile; builds its own geometry
    ├── trackmaster/
    │   └── TrackMasterController.cs # board camera, ghost preview, placement
    ├── racer/
    │   ├── RacerController.cs    # the car: ownership + hazard warnings
    │   └── CameraRig.cs          # third-person chase camera with free-look
    ├── game/
    │   └── Game.cs               # wires up whichever half this machine is playing
    └── ui/
        ├── MainMenu.cs           # host / join / solo buttons
        ├── TilePalette.cs        # the Track Master's tile tray
        └── VehicleHud.cs         # speed / rpm / gear
```

---

## Getting Started

Requires **Godot 4.7 (.NET / Mono build)** and the **.NET 8 SDK**.

1. Open `project.godot` in Godot 4.7 (.NET).
2. Build the C# solution (Godot will prompt / press **Build** top-right).
3. Press **Play**. The menu gives you four ways in:
   - **Test Drive (Solo)** — drive the car on the starting straight.
   - **Build Mode (Solo)** — the Track Master's board on its own, for working on the builder.
   - **Host** / **Join** — a real match (`127.0.0.1` for local tests).
4. For host + client locally, run a second instance
   (Godot: **Debug → Run Multiple Instances → 2+**).

You can also jump straight to either side from the command line:

```bash
godot --path . res://scenes/Game.tscn -- --role=trackmaster
```

---

## Building the track

The track is a single connected path on a 40 m grid (`TileCatalog.TileSize` — the one knob for
the scale of the whole board, sized so the three tiles a racer is warned about are far enough
ahead to react to). Tiles are only ever added at the
**head** — the next open cell the racers are driving toward — which is what makes "place tiles
ahead of the racers" the game rather than free-form building. The head cell is marked on the
board with a yellow pad and an arrow showing which way the track is running.

**To place a tile:** click it in the tray along the bottom. It goes straight onto the head —
that's the only cell it could have gone in, so there's nothing to aim at and no second click.
Hovering a tile in the tray ghosts it onto the head first: green if it can go there, red if it
can't, with the status line saying why. Keep the mouse on one tile and click repeatedly to lay
a run of them, the preview walking along the track as it grows.

**The camera** has two modes, on a toggle button in the top right:

- **Following track** (the default) — rides over the head, easing along as the track grows, so
  what you're building is always in frame without you touching the camera. The wheel zooms.
- **Free roam** — a flying camera for going and watching the race. WASD moves along the way
  it's pointing, holding right mouse aims it, and the wheel changes how fast it flies. Look is
  held rather than latched so the cursor stays free to reach the tray.

Toggling back to Following eases the camera home rather than cutting to it.

| | |
|---|---|
| Place a tile | Click it in the tray |
| Switch camera mode | The button, top right |
| Move (free roam) | WASD |
| Look (free roam) | Hold right mouse |
| Zoom / fly speed | Mouse wheel |

Tiles live in `TileCatalog`. Adding a new one is a single entry there plus a case in
`TrackTile.BuildHazard` — the tray, the ghost and the geometry all pick it up automatically.

`HairpinTurn` and `LoopAhead` aren't in the catalog yet: a hairpin exits back into the cell the
track arrived from, and a loop needs vertical geometry, so both need multi-cell tiles that the
grid doesn't model.

---

## Roadmap (rough)

- [x] Project scaffold + multiplayer connection layer
- [x] Role assignment (Track Master vs Racer)
- [x] Racer car controller (third person) on ray-cast vehicle physics
- [x] Track grid + tile catalog + procedural tile geometry
- [x] Track Master board view: tile tray, drag-and-drop placement, ghost preview
- [x] Live tile placement + server validation + replication
- [x] "3 tiles ahead" hazard notification system
- [ ] Server reconciliation / transform sync so racers see each other move
- [ ] Tile hand / dealing system (the Track Master currently has every tile available)
- [ ] Multi-cell tiles, to unlock Hairpin and Loop
- [ ] Win / lose conditions and round flow
- [ ] Lobby UI, player names, spectating
