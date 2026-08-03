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
  **hazard** — e.g. `Jump Ahead`, `Loop Ahead`, `Log Trap`, `Crushers`.
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
- **Racer movement:** each car is simulated only by the peer that owns it, which pushes its
  pose 30 times a second through a `MultiplayerSynchronizer`. Everyone else holds that car
  frozen kinematic and slides it toward the pose it was sent. Not yet server-reconciled: an
  owner's word on where its own car is, is currently final.
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

The car is an **arcade drift model** — a rigid body on four ray-cast springs with no tire model
at all, following the approach Walaber describes for Parking Garage Rally Circuit. Drive force
and grip are each a one-step solve that gets clamped; steering is a torque pointing the body at
a heading vector. Godot's built-in `VehicleBody3D` is not used anywhere.

Holding the drift button commits the car to a 35° angle you can still steer ±15° inside, and
releasing a long enough drift pays out a boost that **stacks** with any boost still burning — so
chained drifts run the car a long way over its normal top speed.

Full write-up and tuning guide: **[`docs/vehicle-physics.md`](docs/vehicle-physics.md)**.

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
	├── vehicles/                 # the arcade drift physics
	│   ├── Vehicle.cs            # the whole model — all tuning lives here
	│   ├── GroundRay.cs          # one corner: ray, spring, surface, wheel mesh
	│   ├── BodyLean.cs           # the visual pose: roll, drift yaw, squat/dive
	│   ├── VehicleInput.cs       # input as a value + the action map
	│   ├── VehicleDebugOverlay.cs# the tuning overlay
	│   ├── SurfaceGroups.cs      # Road / Dirt / Grass / Ice group names
	│   └── EngineSound.cs, WheelSmoke.cs, VehicleInputController.cs
	├── tiles/
	│   ├── TileHazard.cs         # enum of hazard types
	│   ├── TileData.cs           # one tile's data: hazard, exit turn, length, height change
    │   ├── TileCatalog.cs        # every tile type + weights, grid <-> world helpers
    │   ├── TrackDirection.cs     # N/E/S/W and the turn maths
    │   ├── TrackGrid.cs          # the track model: cells, order, height, placement rules
    │   ├── TrackController.cs    # authoritative track + placement replication
    │   ├── TrackTile.cs          # a placed tile; builds its own geometry
    │   ├── TrackTile.Hazards.cs  # the still hazards, and the impulse pads
    │   ├── TrackTile.Moving.cs   # moving parts + the clock that drives them
    │   └── TrackTile.Shapes.cs   # hairpin, ramps, loop: tiles that are their own shape
    ├── trackmaster/
    │   ├── TrackMasterController.cs # board camera, ghost preview, placement
    │   └── TileHand.cs           # the dealt slots: weighted draw on a timer
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

Every tile is one cell wide, but a tile that runs straight through covers
`TileCatalog.StraightCells` of them along the direction of travel — three, so a straight is 120 m
of road and a hazard gets a real run-up and run-out either side of it. Curves stay a single cell,
because a corner *is* the cell.

The **hairpin** is the exception to all of that: two cells side by side, entered up one and left
down the other, so the racer comes out heading the way they came in with a barrier — the apex —
between the two lanes. It goes down as one tile in one click rather than as two curves the Track
Master has to line up, and it comes in a left and a right. Being the one tile whose footprint
steps off the line it entered on, it's why `PlacedTile.CellsFor` describes a shape rather than a
length; a right hairpin is `ExitTurn = 2` and a left one `-2`, which reverse the racer identically
and differ only in which side the tile swings out to.

A tile has to have room for its whole footprint: if the cells a long straight or a hairpin needs
aren't all clear, the placement is refused the same way running the track back into itself is, and
the ghost turns red with the reason in the status line.

**Elevation.** The track climbs. `TrackGrid.HeadHeight` is a running count of cubes above the
ground — a cube being `TileCatalog.HeightStep`, which is one cell on its side, 40 m — and the ramp
tiles are the only things that change it. The change is cumulative, so everything placed after a
climb is placed up there until something brings the track back down, and the board camera and head
marker rise with it. Cells stay flat two-dimensional coordinates regardless: two tiles may never
share a cell whatever height they're at, so the track can climb over its own neighbourhood but not
over itself, and "which tile is this car on" stays a flat lookup. A ramp down is refused when the
track is already on the ground, which means a ramp down in the hand is a tile you hold until
you've climbed — the one card in the catalog whose legality depends on the shape of the track so
far.

Ramps are built as eight facets along a smoothstep profile rather than as one flat wedge, so the
road is level where it meets its neighbours instead of meeting them at an 18° or 34° kerb. The
price is that the middle is steeper than the average: 27° for one cube and 45° for two.

**Moving hazards** (`TrackTile.Moving.cs`) are `AnimatableBody3D` parts driven in the physics step,
which is what lets them shove a car rather than teleport through it. Their phase is deliberately
per-peer: every part runs off its own tile's elapsed time, so two machines have the same log a
fraction of a swing apart. That's the same bargain the game already takes on the cars — a racer is
simulated by whoever owns it and only its pose is replicated — so each car is knocked about by its
own owner's view of the hazard and everyone sees the consequence.

**Nothing recovers a car that leaves the track.** There's no respawn, so the log trap (which has no
barriers, by design) and the hole under the loop are both able to end someone's race outright. The
hazards that could do that are weighted low for exactly that reason.

**The hand.** The Track Master doesn't get the whole catalog — they get a row of slots along
the bottom that fills itself with random tiles, one every `DealInterval` seconds. They open with
`StartingTiles` of them already in hand, so the race doesn't begin with an empty tray. Which tile
comes up is weighted (`TileDefinition.Weight`, currently summing to 100 so each reads as a
percentage). The countdown to the next one shows in the slot at the end of the row; when every
slot is full the clock stops and nothing more arrives until something is spent. So the choice
isn't *which tile is best here* — it's what to spend now and what to hold, while the racers eat
track ahead of you.

**To place a tile:** click it in the tray. It goes straight onto the head — that's the only cell
it could have gone in, so there's nothing to aim at and no second click. Hovering a slot ghosts
its tile onto the head first: green if it can go there, red if it can't, with the status line
saying why. An illegal placement is refused without spending the tile. Spending one closes the
hand up behind it, the way a hand of cards does.

**The drop.** A placed tile appears `TileFallHeight` above the track and sinks into place at
`TileFallSpeed` metres per second (both on `TrackController`, since every peer builds the tile
and the racers are the ones meant to see it coming). The whole tile descends, collision and all,
so it isn't track you can drive on until it lands — which puts a real clock on building far
enough ahead. At the defaults that's 135 m at 130 m/s, a descent of about a second: read against
how fast the cars are rather than against how long it looks nice for, so a placement arrives
roughly as the car that was one tile back reaches it. The tile doesn't sink, it slams — a shadow
tightens on the spot it's headed for on the way down, and it throws a ring of dust off its own
footprint and shakes the cameras of anyone nearby when it hits. The starting straight doesn't
drop; the racers are already parked on it, and road that never moved gets none of the impact.

**Watching the racers.** Every car carries a coloured chevron on the board, pointing the way
it's travelling, labelled with whose it is. They're drawn over everything — a marker that hid
behind a tile wall would go missing exactly when you were aiming something at it — and held at
a constant on-screen size, so they stay findable from the closest zoom to the furthest. This is
the feedback the whole role runs on: you're building a track hard enough to stop these cars, and
you can't judge that from a board that doesn't show them.

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

Tiles live in `TileCatalog` — 23 of them. Adding a new one is a single entry there — hazard, look,
weight — plus a case in `TrackTile.BuildHazard`. The deal, the ghost and the geometry all pick it
up automatically; the tray shows whatever the hand happens to hold. `TrackTile` is split into
partials by kind: `.Hazards.cs` for the still ones, `.Moving.cs` for the ones with moving parts,
`.Shapes.cs` for tiles that build their own geometry outright because the standard
floor-walls-line-hazard assembly can't describe them.

Two hazards act on the car with a force rather than by being in the way — the launch pads and the
boost pads — and both do it from the tile's own side, through a trigger volume that hands the car
an impulse. The loop does the same to hold a car on the inside of it. Nothing in `Vehicle.cs`
needed changing to make any of them work; the only vehicle-side thing tiles use is the surface
group on whatever the wheel ray hits, which is how the ice patch and the gravel bed get their grip.

`LoopAhead` is now a real drivable loop. Still not in the catalog: nothing. The one shape the grid
still can't express is a tile whose exit turns *and* whose footprint runs more than a cell, since
`PlacedTile.CellsFor` assumes a long tile runs in the direction it was entered.

---

## Roadmap (rough)

- [x] Project scaffold + multiplayer connection layer
- [x] Role assignment (Track Master vs Racer)
- [x] Racer car controller (third person) on ray-cast vehicle physics
- [x] Track grid + tile catalog + procedural tile geometry
- [x] Track Master board view: tile tray, drag-and-drop placement, ghost preview
- [x] Live tile placement + server validation + replication
- [x] "3 tiles ahead" hazard notification system
- [x] Transform sync so racers see each other move, and the Track Master sees them all
- [ ] Server reconciliation (today each owner's word on where its own car is, is final)
- [ ] Tile hand / dealing system (the Track Master currently has every tile available)
- [ ] Multi-cell tiles, to unlock Hairpin and Loop
- [ ] Win / lose conditions and round flow
- [ ] Lobby UI, player names, spectating
