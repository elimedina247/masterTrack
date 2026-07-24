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
- **`MultiplayerSpawner`** — replicate tiles and racer cars as they're created.
- **`MultiplayerSynchronizer`** — sync transforms / state on replicated nodes.

---

## Project Structure

```
master-track/
├── project.godot            # autoloads + main scene registered here
├── masterTrack.csproj        # .NET / C# project
├── scenes/
│   └── Main.tscn            # entry point: host / join menu
├── scripts/
│   ├── networking/
│   │   ├── NetworkManager.cs # autoload: host/join, peer lifecycle
│   │   └── GameManager.cs    # autoload: roles, rounds, game state
│   ├── tiles/
│   │   ├── TileHazard.cs     # enum of hazard types
│   │   ├── TileData.cs       # data for a single tile (hazard + shape)
│   │   └── TrackTile.cs      # a placed tile node
│   ├── trackmaster/
│   │   └── TrackMasterController.cs
│   ├── racer/
│   │   └── RacerController.cs
│   └── ui/
│       └── MainMenu.cs       # host / join buttons
```

---

## Getting Started

Requires **Godot 4.7 (.NET / Mono build)** and the **.NET 8 SDK**.

1. Open `master-track/project.godot` in Godot 4.7 (.NET).
2. Build the C# solution (Godot will prompt / press **Build** top-right).
3. Press **Play**. The Main scene lets you **Host** or **Join** (`127.0.0.1` for local tests).
4. Run a second instance (Godot: **Debug → Run Multiple Instances → 2+**) to test
   host + client locally.

---

## Roadmap (rough)

- [x] Project scaffold + multiplayer connection layer
- [x] Role assignment (Track Master vs Racer)
- [ ] Tile hand / dealing system for the Track Master
- [ ] Live tile placement + server validation + replication
- [ ] Racer car controller (third person) with server reconciliation
- [ ] "3 tiles ahead" hazard notification system
- [ ] Hazard behaviors (Jump, Loop, Hairpin, ...)
- [ ] Win / lose conditions and round flow
- [ ] Lobby UI, player names, spectating
