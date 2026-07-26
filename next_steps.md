# Next Steps — Remote Playtest

Goal: a build my friends can download and play over the internet, to test the custom vehicle
physics and the overall theme. Not a feature-complete game — no win conditions, no scoring, no
polish beyond what keeps a session from falling apart.

Target experience:

1. Everyone launches the build and joins the host.
2. Everyone lands in the **lobby** — the existing test area — with a car, and drives around
   freely while waiting for the rest of the group.
3. Each player gets one of the three car models in a random colour when they join.
4. The host presses **Start Match**. One player is picked at random to be the Track Master.
5. Everyone loads into the match with their role.

Two things shape the plan. First, the lobby needs the same networked car spawning that
`Game.tscn` already has, so that machinery gets extracted once and shared. Second, a random
Track Master is nearly free — `TrackController` already routes placements through
`RpcId(1, ...)`, so a non-host builder is already supported by the tile code.

---

## Phase 0 — Prove the Windows toolchain

Do this before writing any code. Export problems surface late and are miserable to debug
alongside new features.

- [ ] Clone the repo onto the Windows PC. `.gitignore` already excludes `.godot/`, so git is a
	  fine sync mechanism between the Mac (dev) and Windows (build).
- [ ] Install Godot 4.7 **.NET** build and the .NET 8 SDK.
- [x] Install export templates: **Editor → Manage Export Templates**. Installed on the Windows
	  PC at `%APPDATA%\Godot\export_templates\4.7.stable.mono\` — the .NET flavour, which is the
	  one that matters here.
- [x] Export the project exactly as it stands. Confirm you get a runnable `.exe`, a `.pck`, and
	  a `data_masterTrack_windows_x86_64` folder, and that Test Drive works from the export.
	  Export path is a **file** ending in `.exe`, not a folder, and belongs outside the project
	  tree — `export_filter="all_resources"` would otherwise sweep a previous build into the
	  next one. See the blocker below on Test Drive.
- [ ] Zip it, send to one friend, confirm it launches.

### Found and fixed while testing: cars launched themselves on spawn

A car placed at ride height was **flung towards the world origin on its first physics tick**,
before anyone touched a control — around 130 km/h within 50 ms, an impulse rather than
acceleration. Not new, and not caused by the work below: it reproduced on a clean worktree at
`dbf3857`, headless and windowed alike, with the old hardcoded `TestCar`.

`Wheel` derives its own velocity by differencing its world position frame to frame, and
`PreviousGlobalPosition` started at `Vector3.Zero` with nothing seeding it. So on tick one every
wheel measured its velocity as *its distance from the world origin over one 1/120 s step* — about
7 km/s for a car sitting still at `z = 60`. The tire model answered that slip with a force to
match and fired the car back down its own position vector. `PreviousCompression` had the same
hole, which is where the smaller vertical kick came from: the dampers were handed the resting
compression as if the spring had arrived there in a single step.

The tell was that it scaled with distance from the origin and vanished at it — velocity after
0.25 s, spawning at three different `z`:

| spawn z | before | after |
| --- | --- | --- |
| 0 | `(0, -0.30, 0.003)` | `(0, -0.50, 0.002)` |
| 60 | `(0, +10.6, -25.0)` | `(0, -0.50, 0.002)` |
| 300 | `(0, +11.0, -462.6)` | `(0, -0.50, 0.002)` |

Fixed by seeding both in `Wheel.Initialize` / on the first suspension tick. Behaviour is now
position-independent, which is what it always should have been. Worth knowing that this made
every car *anywhere but the origin* wrong in proportion to how far out it was, so the match start
line at `z = 128` was hit harder than the test pad ever was.

Note: a C# export is **not** a single executable. The whole folder structure has to stay intact,
which is why we ship a ZIP rather than an installer. Unsigned builds trigger SmartScreen — warn
people that they need *More info → Run anyway* so they don't assume it's broken.

---

## Phase 1 — Foundations

Correctness fixes everything else depends on. All testable locally with two instances.

### 1.1 Replace the ENet type checks

Four places use `Multiplayer.MultiplayerPeer is ENetMultiplayerPeer` as a proxy for "are we in a
real session":

- `scripts/game/Game.cs:65` — solo vs. networked spawning
- `scripts/game/Game.cs:203` — server-only hazard warning guard
- `scripts/tiles/TrackController.cs:51` — the `Networked` property
- `scripts/racer/RacerController.cs:94` — the `IsNetworked` property

Every one of these evaluates **false** under a Steam-backed peer. The failure is not a crash:
the game would decide it is in solo mode while genuinely networked, so `Game` spawns a single
solo car and `RacerController` simulates *every* car locally instead of freezing remote ones —
each machine running its own diverging copy of the race.

- [x] Add `NetworkManager.IsNetworked` — non-null peer whose connection status is not the
	  offline peer, rather than a concrete type check.
- [x] Route all four call sites through it.

`NetworkManager.Disconnect` went the same way — it was closing the peer only if it was an
`ENetMultiplayerPeer`, so a Steam session would never have been shut down. `Close()` is on the
base peer.

Worth doing regardless of Steam. Do it before Steam so the transport swap stays confined to
`NetworkManager`.

### 1.2 Scene-ready handshake

The current match start has a race condition that will break remote play. `Game.cs:99` spawns
cars one deferred frame after the host's own `_Ready`, but clients only *begin* loading
`Game.tscn` when the `NotifyGameStateChanged` RPC arrives (`MainMenu.cs:151`). The spawner's
`spawn_path` is `../Racers` — a node that does not exist on the client yet. Locally this can
pass by luck; over real latency the client ends up with no car.

- [x] Add to `GameManager`: a set of peers that have reported their scene loaded, an
	  `[Rpc(AnyPeer)] ServerNotifySceneReady()` clients call from `_Ready`, and an
	  `AllPeersReady` signal the server emits when every connected peer has checked in.
- [x] Spawn cars on that signal instead of `CallDeferred(nameof(SpawnNetworkedRacers))`.

Also emits `PeerSceneReady(peerId)` per peer, which is what the lobby spawns on — a lobby has
no single moment when everyone is in, so it cannot use the all-or-nothing gate. And
`SceneReadyProgress(ready, total)` is broadcast to everyone so the count can be shown on clients
too, not just the host that knows it.

**Wait for all peers rather than timing out.** A `MultiplayerSpawner` only sends spawn packets
to peers connected at spawn time, and will not retroactively spawn for a peer whose scene
appeared late — a straggler that misses the window is permanently carless, a silent failure that
looks like a physics bug. Show `Waiting for players… (2/4)` instead. If someone genuinely hangs,
it is visible and the host can restart.

### 1.3 Extract a shared racer arena

The lobby and the match both need: container + `MultiplayerSpawner`, spawn one car per racer
peer, wire the local car to the HUD.

- [x] Pull that into one node script — `RacerArena` — parameterised by a spawn-point strategy
	  (the match uses a start-line row, the lobby wants cars scattered on the pad).
- [x] Move the deferred HUD-wiring pattern from `Game.OnRacerEnteredTree` into it.
- [x] Make `Game.cs` a consumer of it.

The HUD wiring binds by interface (`IVehicleObserver`) rather than by naming each overlay, so a
new overlay dropped into the HUD is wired up by code that already exists.

**This turned up the bug that would have sunk the playtest.** `MultiplayerSpawner` sends no
transform with a replicated scene instance, and the pose only reached clients through the
synchronizer — which `RacerController` was building in `_Ready`, too late for the spawn packet
(Godot says so at runtime: *"unable to process the pending spawn since it has no network ID"*).
The consequence was invisible in a one-machine test and fatal in a real one: **every client's own
car started at the world origin**, and since a peer is the authority for its own car, nothing ever
corrected it. Everyone would have piled up on the same square metre.

Fixed by spawning through a custom `SpawnFunction` carrying peer id and position explicitly, and
by assembling the car — name, owner, position, synchronizer, authority — in
`RacerController.PrepareForSpawn` *before* it enters the tree, which is what Godot actually
requires. Verified with three headless instances: each client's car now arrives on its assigned
ring slot.

---

## Phase 2 — TestArea becomes the lobby

### 2.1 Remove the hardcoded car

`TestArea.tscn` instances `Racer.tscn` as a `TestCar` node, and the HUD overlays
(`SpeedBlur`, `SpeedLines`, `VehicleHud`, `VehicleDebug`) bind to it through exported
`node_paths`. Those bindings break the moment cars are spawned dynamically.

- [x] Delete the `TestCar` node.
- [x] Let `RacerArena` spawn in both the solo and networked cases, the way `Game.SpawnSolo`
	  already branches.

### 2.2 Add the arena

- [x] Add a `Racers` container and a `MultiplayerSpawner` with `spawn_path` pointing at it,
	  mirroring `scenes/Game.tscn:46`.

Both are built in `RacerArena._Ready` rather than authored into two scenes, so the lobby and the
match cannot drift apart on a spawn path — the same reasoning `RacerController` already applies
to its synchronizer. `Game.tscn` and `TestArea.tscn` each hold one `RacerArena` node instead.

### 2.3 Guard the `[Tool]` path

`PhysicsTestArea` runs in the editor and calls `Rebuild()` from `_Ready` to generate the pad and
the tile grid.

- [x] Keep all networking behind the existing `Engine.IsEditorHint()` early return.

The generated geometry needs no replication — every peer builds it identically from
`TileCatalog`, exactly like track tiles.

### 2.4 Fix respawn

`RespawnCar` (`PhysicsTestArea.cs:157`) targets a fixed `Car` export.

- [x] Retarget it to the **local player's** car and store that car's spawn transform.
- [x] Keep the `PhysicsServer3D.BodySetState` approach — assigning `GlobalTransform` on a rigid
	  body is not reliable.

The car and its transform are captured from `RacerArena.LocalRacerSpawned`, since in a session
which car is ours is not known until one is replicated to us.

### 2.5 Lobby UI

- [x] Connected-player list.
- [x] Host-only **Start Match** button, moved out of `Main.tscn` (`MainMenu.cs:92`) — the host
	  will be out driving, so it cannot live on the menu any more.
- [x] Disable Start below two players: you need at least one builder and one racer.

`LobbyPanel` builds its own controls, the way `VehicleHud` does, and hides itself entirely when
there is no session — so the same scene is still the solo Test Drive. Players are listed by peer
id for now; names are Phase 5 and colours are Phase 4.

Host and client now both leave the menu for the lobby the moment the session exists, so
`MainMenu` no longer knows anything about roles or match flow.

---

## Phase 3 — Random Track Master

### 3.1 Randomise the role

`GameManager.StartMatch` (`GameManager.cs:99`) hardcodes the host as Track Master.

- [x] Pick uniformly from all connected peers, including the host.
- [x] Roll **only on the server** and broadcast through the existing `NotifyRoleAssigned` RPC.
	  Clients must never roll their own.

### 3.2 Verify the non-host builder path

This should already work. A client Track Master's placements go through
`RpcId(1, MethodName.ServerPlaceTile, ...)` (`TrackController.cs:98`), and the host-as-builder
case is handled by the `IsServer()` branch just above it.

- [x] Test with three local instances where a **client** draws the builder role. That
	  combination has probably never run.

Ran three headless instances repeatedly; a client drew the builder in two runs of three, and all
three peers agreed on every assignment. Loading the match with a client Track Master and the host
as a racer works. Tile placement over that path is **not** yet exercised — it needs a mouse on the
board, so it wants a real three-window run.

Two crashes fell out of the roster churn this exposed, both now fixed: `RacerArena`'s deferred
HUD binding asked a car that had left the tree who owned it, and `TrackMasterController.AimMarker`
kept aiming at freed cars between roster sweeps — an exception per frame whenever a peer dropped.

### 3.3 Lobby → match transition

- [x] Move the scene-change logic out of `MainMenu.OnGameStateChanged` and into the lobby.
- [x] Final sequence: host presses Start → server assigns roles → all peers load `Game.tscn` →
	  each reports ready (1.2) → server spawns once everyone has.

---

## Phase 4 — Random car and paint on join

**Each player gets one of the three car models in a random rainbow colour when they join.** The
appearance is chosen once, at join time, and persists from the lobby into the match so your car
looks the same in both.

### 4.1 Assign server-side, on join

- [ ] Add an appearance table to `GameManager` alongside `Roles`: peer id → (variant, colour).
- [ ] Assign when a peer connects, on the server only, and replicate with the same RPC pattern
	  `NotifyRoleAssigned` already uses.
- [ ] `RacerController._Ready` looks up its own appearance by `OwnerPeerId`.

Reusing the proven role-replication machinery avoids depending on `MultiplayerSpawner` spawn
property ordering, which is the fragile alternative.

### 4.2 Draw colours without replacement

- [ ] Use the seven rainbow colours (red, orange, yellow, green, blue, indigo, violet), drawn
	  without replacement so no two players share one.
- [ ] Release a colour back to the pool when a peer disconnects.
- [ ] Set `NetworkManager.MaxPlayers = 6` to cap the lobby at seven people.

**The palette has to cover the whole lobby, not just the racers.** The builder is not chosen
until the host presses Start, so everyone waiting in the lobby is driving a car and needs a
colour. The builder's colour only frees up at match start, by which point they have already held
it for the entire wait.

**`MaxPlayers` is off by one from what it looks like.** It is passed to `CreateServer`
(`NetworkManager.cs:42`), where ENet's max-clients parameter counts *connections* — the host is
peer 1 and is not one of them. So `MaxPlayers = 8` currently allows 8 clients **plus** the host:
nine people, two over the palette. `MaxPlayers = 6` gives 6 clients plus the host = 7, matching
the seven rainbow colours exactly.

### 4.3 Make the rig variant-swappable

`Racer.tscn` currently hardcodes the `C_Cartoon` set: `BodyRig/BodyModel` (`5_body`), and
`RimFL`/`RimRL` (`6_rimL`) and `RimFR`/`RimRR` (`7_rimR`).

- [ ] Add a small variant catalog mapping A_Wedge / B_Bubble / C_Cartoon to their body and rim
	  `PackedScene` paths under `assets/cars/`.
- [ ] Swap the five instanced models at runtime from the assigned variant.
- [ ] **Clear `BodyModel`'s transform to identity.** It currently carries
	  `Transform3D(-4.37e-08, 0, 1, …, 0, 0.2, -0.1)`, which is the old CC96 rotation fudge and
	  is wrong for every model in `assets/cars/` (see `assets/cars/README.md`).
- [ ] Leave the rim node rotations alone — all four legitimately carry the same axle rotation,
	  and the L/R mirroring is baked into the assets.

### 4.4 Tint only the paint surfaces

`tools/car_blockout.py` names materials `<Variant>_Paint`, `_Glass`, `_Trim`, `_Lights`,
`_Tire`, `_Rim`. `FlatShade` already rebuilds materials per surface specifically so windows and
lights do not get painted with the body colour.

- [ ] In `FlatShade.RestyleSurfaces`, substitute the assigned colour for `AlbedoColor` when the
	  source material's name ends in `_Paint`; leave every other surface's albedo untouched.
- [ ] Verify the material names survive FBX import — if Godot renames them, fall back to
	  identifying the paint surface by index per variant.

### 4.5 Re-run FlatShade after the swap

`FlatShade` is a sibling node whose `_Ready` walks its parent (`Racer.tscn:50`). Godot runs
children's `_Ready` before the parent's, so a body swapped in from `RacerController._Ready`
arrives **after** FlatShade has already restyled the old model — the new car would render
smooth and shiny with the wrong paint.

- [ ] Expose a public restyle method on `FlatShade` (`Restyle`/`RestyleSurfaces` are currently
	  private static) and call it explicitly after the variant swap.

### 4.6 Match the board chevrons to the cars

`TrackMasterController.cs:321` picks marker colours with
`RacerColors[Mathf.Abs(racer.OwnerPeerId) % RacerColors.Length]` — independent of the car's
paint, so a player's chevron on the board will not match the car they are driving.

- [ ] Read the assigned colour instead, so the board and the car agree. This is what makes the
	  markers actually usable for the Track Master.

### 4.7 Decide the tire radius question

`assets/cars/README.md` records that the variants were modelled at different tire radii:

| Variant | Front | Rear |
| --- | --- | --- |
| A_Wedge | 0.28 | 0.28 |
| B_Bubble | 0.30 | 0.30 |
| C_Cartoon | 0.24 | **0.36** |

The rig currently has both at **0.24**, so C_Cartoon's rear wheels are already visually wrong.
`Wheel.cs` positions the hub but never scales the rim, so a mismatch renders the wheel sunk into
the road or floating above it — and `FrontTireRadius`/`RearTireRadius` are **physics**
parameters, not cosmetics. C's staggered rake is a real handling difference.

**Recommendation for this playtest: normalise all three variants to a single radius pair so the
cars are purely cosmetic.** Random assignment of different handling would confound exactly what
this playtest is meant to measure — you would not be able to tell whether feedback is about the
physics or about which car someone drew. Revisit per-variant handling later as a deliberate
design choice, once the baseline feels right.

- [ ] Pick one radius pair, apply to all three variants, adjust the rims to match.

---

## Phase 5 — Playtest quality of life

- [ ] **`ui_cancel` in `Game.cs:234` calls `GetTree().Quit()`.** Make it return to the lobby.
	  Someone will hit Escape by reflex and drop out of the session.
- [ ] **Networked respawn in `RacerController`.** Today `racer_reset` only works in the solo
	  test area (`PhysicsTestArea.cs:146`). With no win condition to end a round, one bad
	  landing removes a player for the whole session.
- [ ] Player names, so people are not bare peer ids. Optional.

---

## Phase 6 — Steam transport swap

Only after the game logic above works on ENet. Iterating with local instances is much faster
than iterating through Steam.

- [ ] Add the `Facepunch.Steamworks` NuGet reference to `masterTrack.csproj`.
- [ ] Put `steam_appid.txt` containing `480` (Spacewar) and the native `steam_api64.dll` from
	  the Steamworks SDK **next to the exported binary** — not inside the `.pck`. Godot's export
	  filters pack files into the `.pck`, which is the wrong place for a native library that
	  needs to be loaded by the OS.
- [ ] Confirm `SteamClient.Init(480)` succeeds **before** writing any transport code.
- [ ] Call `SteamNetworkingUtils.InitRelayNetworkAccess()` early and allow a few seconds before
	  the first connection attempt. The relay network needs to warm up; a connection tried
	  immediately at startup fails in a way that looks like a code bug.
- [ ] Implement a `MultiplayerPeerExtension` backed by Facepunch's `SocketManager` /
	  `ConnectionManager`.
- [ ] Swap the two constructors in `NetworkManager.HostGame` / `JoinGame`. Thanks to 1.1,
	  nothing outside that file should need to change.

For discovery, start with the host pasting their SteamID rather than building a lobby browser.
App ID 480 is shared with every other Steamworks developer testing right now, so an unfiltered
lobby list will be full of strangers' Spacewar lobbies — and yours will be visible to them.

Everyone testing needs Steam installed, running and logged in.

---

## Phase 7 — Packaging

- [ ] Ship a ZIP. Confirm the `.exe`, `.pck`, data folder, `steam_api64.dll` and
	  `steam_appid.txt` all survive the round trip.
- [ ] Confirm a friend's copy initialises Steam correctly.

Inno Setup can wait until distribution goes beyond this group — at which point a real App ID
(Steamworks partner account, $100 per app, recoverable against revenue) and a Steam depot upload
are the same conversation. Godot ships no Windows installer generator; the only packaging format
it produces natively is `.dmg` on macOS.

---

## Milestones

**First milestone — end of Phase 4.** Everyone drives around the test pad together in their own
randomly assigned car, the host starts a match, and a random player finds themselves looking at
the board. Still on ENet over Tailscale (everyone installs it, joins the tailnet, and types the
host's Tailscale IP into the existing Join field — no code changes, no port forwarding). This is
testable before any Steam work exists.

**Second milestone — end of Phase 7.** The same thing, distributed as a ZIP, connecting through
Steam with no VPN.

## Interim connectivity

Until Phase 6 lands, `NetworkManager` does a raw ENet connect to an IP on UDP 8910. Use
**Tailscale** rather than port forwarding: no router config, works through NAT, and the existing
Join field already accepts an arbitrary IP.
