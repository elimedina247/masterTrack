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

- [x] Add an appearance table to `GameManager` alongside `Roles`: peer id → (variant, colour).
- [x] Assign when a peer connects, on the server only, and replicate with the same RPC pattern
	  `NotifyRoleAssigned` already uses.
- [x] ~~`RacerController._Ready` looks up its own appearance by `OwnerPeerId`.~~ The appearance
	  rides in the spawn packet instead — see below.

Reusing the proven role-replication machinery avoids depending on `MultiplayerSpawner` spawn
property ordering, which is the fragile alternative.

**The car gets its appearance from the spawn packet, not from a lookup.** The concern above was
about spawn *properties* — the synchronizer-replicated kind, which is exactly what broke in 1.3.
A custom `SpawnFunction` payload is a different thing: explicit data in the spawn packet itself.
Since `RacerArena` already sends one, putting the variant and colour in it means the car is built
right the first time on every peer, with no assumption that an appearance RPC arrived before the
spawn did. The replicated table is still there — the lobby list and anything else asking "who is
the green one" needs it — it just isn't what dresses the car.

A newcomer is also sent the whole existing table on connect, or they would join a lobby full of
cars they have no colours for.

### 4.2 Draw colours without replacement

- [x] Use the seven rainbow colours (red, orange, yellow, green, blue, indigo, violet), drawn
	  without replacement so no two players share one.
- [x] Release a colour back to the pool when a peer disconnects.
- [x] Set `NetworkManager.MaxPlayers = 6` to cap the lobby at seven people.

Colours are dealt without replacement, models *with* — three models cannot cover seven people,
and the colour is the half that has to be unique. The palette is lifted off the pure hues so the
cars still read on tarmac; a pure blue car in shadow is a black car.

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

- [x] Add a small variant catalog mapping A_Wedge / B_Bubble / C_Cartoon to their body and rim
	  `PackedScene` paths under `assets/cars/`. → `scripts/racer/CarVariants.cs`, which also owns
	  the palette, so the car, the board and the lobby list cannot disagree about what a colour is.
- [x] Swap the five instanced models at runtime from the assigned variant.
- [x] **Clear `BodyModel`'s transform to identity.** Already gone from `Racer.tscn` by the time
	  this ran; the swap sets identity explicitly so it cannot come back.
- [x] Leave the rim node rotations alone — all four legitimately carry the same axle rotation,
	  and the L/R mirroring is baked into the assets. The swap reads the outgoing node's transform
	  and keeps it, so the convention survives without being restated in code.

The swap happens in `PrepareForSpawn`, before the car enters the tree — so every `_Ready` in the
car, `FlatShade`'s included, sees the model it will actually be wearing.

### 4.4 Tint only the paint surfaces

`tools/car_blockout.py` names materials `<Variant>_Paint`, `_Glass`, `_Trim`, `_Lights`,
`_Tire`, `_Rim`. `FlatShade` already rebuilds materials per surface specifically so windows and
lights do not get painted with the body colour.

- [x] In `FlatShade.RestyleSurfaces`, substitute the assigned colour for `AlbedoColor` when the
	  source material's name ends in `_Paint`; leave every other surface's albedo untouched.
- [x] Verify the material names survive FBX import — they do, intact: `C_Cartoon_Paint`,
	  `_Glass`, `_Trim`, `_Lights`, `_Rim`, `_Tire`. No index fallback needed.

**`FlatShade` had to be made idempotent to make this work.** It reads each surface's source
material — and after one pass, the "source" *is* the flat material it built, which carries no
name. So the second pass could never find `_Paint` and the paint silently never applied. It now
reads `mesh.Mesh.SurfaceGetMaterial`, the material as authored on the mesh, which survives any
number of passes.

### 4.5 Re-run FlatShade after the swap

`FlatShade` is a sibling node whose `_Ready` walks its parent (`Racer.tscn:50`). Godot runs
children's `_Ready` before the parent's, so a body swapped in from `RacerController._Ready`
arrives **after** FlatShade has already restyled the old model — the new car would render
smooth and shiny with the wrong paint.

- [x] Expose a public restyle method on `FlatShade` (`Restyle`/`RestyleSurfaces` are currently
	  private static) and call it explicitly after the variant swap.

### 4.6 Match the board chevrons to the cars

`TrackMasterController.cs:321` picks marker colours with
`RacerColors[Mathf.Abs(racer.OwnerPeerId) % RacerColors.Length]` — independent of the car's
paint, so a player's chevron on the board will not match the car they are driving.

- [x] Read the assigned colour instead, so the board and the car agree. This is what makes the
	  markers actually usable for the Track Master.

Read straight off the car (`racer.PaintColor`) rather than looked up, so the chevron cannot
disagree with the thing it is pointing at. The old `RacerColors` table is gone.

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

- [x] Pick one radius pair, apply to all three variants, adjust the rims to match.

**Kept the rig's existing 0.24 / 0.36 and scaled the rims to it**, rather than picking a new pair.
Normalising to, say, 0.28 / 0.28 would have satisfied the goal equally, but it changes ride height
and the weight transfer of a car you have already been tuning by feel — for no gain, since the
point is only that all three variants agree, not which number they agree on. This way the physics
is bit-for-bit what it was before Phase 4 and only the models changed.

`ApplyVariant` scales each rim by `FrontTireRadius / ModelledFrontRadius`, taking the modelled
radii from the catalog:

| Variant | Front scale | Rear scale |
| --- | --- | --- |
| A_Wedge | 0.86 | 1.29 |
| B_Bubble | 0.80 | 1.20 |
| C_Cartoon | 1.00 | 1.00 |

So the staggered rake is still there — it is just everybody's rake now, which is what stops it
confounding the playtest. If you later decide the stagger is wrong as a *look*, changing
`FrontTireRadius`/`RearTireRadius` on `Racer.tscn` now rescales all three variants to match on
its own; the numbers above are derived, not written down anywhere.

Note `assets/cars/README.md` still says "the rig currently has both at 0.24" — stale, it has been
0.24 / 0.36 for a while.

---

## Phase 5 — Playtest quality of life

- [x] **`ui_cancel` in `Game.cs:234` calls `GetTree().Quit()`.** Make it return to the lobby.
	  Someone will hit Escape by reflex and drop out of the session.
- [x] **Networked respawn in `RacerController`.** Today `racer_reset` only works in the solo
	  test area (`PhysicsTestArea.cs:146`). With no win condition to end a round, one bad
	  landing removes a player for the whole session.
- [x] Player names, so people are not bare peer ids. Optional.

**Escape ends the match for everyone, and only the host can press it.** Sending just the peer who
pressed it back to the lobby does not work: the server is the only thing that spawns cars and it
would still be in the match scene, so that peer would stand on the pad without one. So it goes
through `GameManager.EndMatch` → state `Lobby` → every peer follows. Appearances survive the round
trip; roles and the ready set are cleared, so the next Start re-draws a Track Master. A client
pressing Escape gets told it is the host's call, which is the behaviour that actually matters —
nobody quits the game by reflex any more. In solo there is no session, so Escape goes to the menu.

This is also, incidentally, the only way a round currently *ends*.

**Respawn needs no RPC.** A car is simulated on exactly one machine and that machine is the
authority for its pose, so moving it locally already is the authoritative answer — the new pose
goes out on the next sync like any other. `NetPosition` is written at the same time so remote
copies cut to it instead of gliding across the board.

It returns you to **the last place you were upright with three wheels down**, sampled every 0.25 s,
rather than to a fixed spawn point. On a track the Track Master is still building, the start line
can be a long way behind you, and being sent back there for one bad landing is its own punishment.
`PhysicsTestArea` no longer implements its own respawn — the car owns it, so it works on the pad
and in a match alike.

Names are dealt with like appearances — server holds the list, tells everyone, catches newcomers
up — with the difference that the string comes *from* a client, so it is trimmed and capped to 16
characters on the authority rather than trusted. The lobby list shows each name in that player's
car colour, and the board's chevrons are labelled with it.

---

## Phase 6 — Steam transport swap

Only after the game logic above works on ENet. Iterating with local instances is much faster
than iterating through Steam.

- [x] Add the `Facepunch.Steamworks` NuGet reference to `masterTrack.csproj`. Official package,
	  owners Facepunch/garry, version **2.3.3**.
- [x] Put `steam_appid.txt` containing `480` (Spacewar) and the native `steam_api64.dll` from
	  ~~the Steamworks SDK~~ **next to the exported binary** — not inside the `.pck`. Godot's export
	  filters pack files into the `.pck`, which is the wrong place for a native library that
	  needs to be loaded by the OS.
- [x] **Confirm `SteamClient.Init(480)` succeeds** before writing any transport code.
	  Passed: `[Steam] Ready as 'Husk' on app 480`.
- [x] Call `SteamNetworkingUtils.InitRelayNetworkAccess()` early and allow a few seconds before
	  the first connection attempt. The relay network needs to warm up; a connection tried
	  immediately at startup fails in a way that looks like a code bug. Written, unverified until
	  the line above passes. `SteamService.RelayReady` is the 3-second gate to check before
	  connecting.

**No Steamworks SDK download needed.** The NuGet package ships `steam_api64.dll` itself, at
`content/steam_api64.dll` in the package. It is committed to `native/` here rather than reached
for in the NuGet cache, so the path does not depend on the package's internal layout.

**Two things about getting that DLL loadable were not obvious, and both present as the same
useless error** — `Unable to load DLL 'steam_api64'`, with the file sitting visibly next to the
binary:

1. It ships under `content/`, which is the old `packages.config` convention. A `PackageReference`
   project does not copy it at all. The `None` item that copies it needs **`Link=`** as well, or
   MSBuild preserves the `native/` prefix and drops it in `Debug/native/`, one directory below
   where anything looks.
2. Even correctly placed it is still invisible, because Godot builds with `EnableDynamicLoading`
   and loads game assemblies into their own `AssemblyLoadContext`. Native lookups from there go
   through the assembly's `deps.json` rather than probing its folder, and a file copied in by the
   build is not described there. `SteamService` registers a `DllImportResolver` against
   *Facepunch's* assembly — that is where the `DllImport` lives — and resolves from
   `AppContext.BaseDirectory`. Not `Assembly.Location`: Godot loads assemblies from memory so it
   can hot-reload them, which leaves `Location` an empty string.

Steam lifetime lives in `SteamService` (autoload), separate from `NetworkManager`, because Steam
failing to start and a peer failing to connect are different problems and debugging them together
is miserable. **Init failing is not fatal** — the game carries on over ENet, which is what keeps
the Tailscale path working for anyone without Steam. `--no-steam` skips it entirely.
- [x] Implement a `MultiplayerPeerExtension` backed by Facepunch's `SocketManager` /
	  `ConnectionManager`. → `SteamMultiplayerPeer`.
- [x] Swap the two constructors in `NetworkManager.HostGame` / `JoinGame`. Thanks to 1.1,
	  nothing outside that file should need to change. **It held** — the transport is a two-branch
	  change inside `NetworkManager`, and no game code knows which one is in use. `IsNetworked` and
	  `Disconnect` needed nothing: both were already written against the base peer.

For discovery, start with the host pasting their SteamID rather than building a lobby browser.
App ID 480 is shared with every other Steamworks developer testing right now, so an unfiltered
lobby list will be full of strangers' Spacewar lobbies — and yours will be visible to them.
The menu has a **Use Steam** toggle; ticking it shows your own SteamID to send people and turns
the Join field from "Host IP" into "Host ID".

Everyone testing needs Steam installed, running and logged in.

### What the peer has to do, that ENet was doing for us

- **The host relays.** Clients hold one connection, to the host, and anything aimed at another
  client goes through them. Not an optimisation — a car's `MultiplayerSynchronizer` is owned by
  whoever drives it, so every client is constantly sending pose to every other client, and there
  are no client-to-client connections for that to travel down. Every packet carries an 11-byte
  header (kind, target, source, channel, mode); the host reads the target, delivers locally if it
  is one of the recipients, and forwards to the rest.
- **The host rewrites the source id on every relayed packet.** Otherwise a client could claim to
  be anyone by putting a different number in the header, and everything above this — roles, tile
  placement authority, car ownership — trusts the sender id.
- **Peer ids are ours.** Godot's are 32-bit, a SteamId is 64. The host hands out 2, 3, 4… and
  keeps the mapping. A client is not `Connected` as far as Godot is concerned until the `Welcome`
  arrives carrying its id and the current room, because until then there is nothing it could
  correctly do.
- Steam has no unreliable-ordered mode, so that collapses to unreliable. The only thing sent that
  way is the car pose, which is a whole transform replaced by the next one.

### Verified, and what is not

Host + one client, whole game over Steam sockets: connect, roles, appearances, names, lobby, the
scene-ready handshake, match load, spawns. Clean.

**Three peers could not be tested on one machine, and this is a Steam limit rather than a code
one.** A third process initialises Steam fine and then never connects, with
`ipcclient.cpp (98) : !"Invalid pipe handle specified"` out of Steam itself — three concurrent
Steam API processes against one Steam client is more than it will do. Two is fine.

Three peers over **ENet** works fine locally, so all the *game* logic is testable in three windows
— including a client Track Master placing tiles, which needs a mouse. Only the Steam relay itself
needs real friends on real machines.

So **the relay path is written and read through but not exercised**, because it only comes into
play with two clients. That is the thing to watch first with real friends. One anomaly appeared in
a three-instance attempt — a duplicate `peer_connected` on the last joiner — but only in a run
where Steam's IPC was already failing, and it does not follow from the protocol as written; it is
noted here rather than claimed fixed.

### A client must not be announced before it can hear you

The first real playtest of this found a joining client dropped into a grey void: connected, no
car, no camera, nothing to do. The host could see their car; they could not.

Godot pushes **every already-spawned node** at a peer the moment it hears that peer exists. This
transport learns of a client a full round trip before the client knows its own id — the Steam
connection establishes, and only then does the `Welcome` carrying the id go back — so the host was
announcing peers to Godot while they were still sitting on the main menu with no lobby in their
tree. The spawns arrived addressed to nodes that did not exist, the path cache entry for the
spawner failed to resolve, and *every later spawn through that spawner failed with it* — including
that client's own car:

```
Node not found: "TestArea/RacerArena/RacerSpawner"
ID 1 not found in cache of peer 1
Parameter "spawner" is null
```

Fixed with a `Hello`: the client answers the Welcome, and the host holds the peer back — no
`peer_connected`, no announcement to anyone else — until that answer arrives. ENet gets away with
announcing on connect because its handshake leaves both ends level; this one has to put the wait
back by hand.

**The same hazard is still visible on ENet**, at the lobby→match change rather than at join —
the identical `spawner is null` errors appear there and it recovers on its own. Worth remembering
if cars ever fail to appear after a transition: it is this, not the spawner being misconfigured.

### Joining: SteamID or address, one field

**The host opens both sockets at once** — Steam's relay for friends, and a plain UDP socket on the
same port ENet used. The Join field decides which is used: something with a dot or a colon in it
is an address, seventeen bare digits is a SteamID.

This exists because **you cannot reach yourself through the relay** — both ends would be the same
Steam account on the same machine, and there is nothing for it to route between. Trying is refused
by name rather than left to time out, since it is exactly what pasting your own ID to test two
windows looks like. Two windows locally means Steam ticked in both, and `127.0.0.1` in the second
one's Join field.

It also means a friend on your LAN can join by address while everyone else joins by ID, and the
local socket failing to open is not fatal — you just lose the address route.

---

## Phase 7 — Packaging

- [x] Ship a ZIP. Confirm the `.exe`, `.pck`, data folder, `steam_api64.dll` and
	  `steam_appid.txt` all survive the round trip.
- [ ] Confirm a friend's copy initialises Steam correctly. ← needs the friend. The nearest thing
	  short of that passes: the ZIP extracted to an unrelated directory boots, renders and
	  initialises Steam, and two copies of the *exported* binary host and join each other over
	  the Steam transport with both getting cars.

Build the ZIP from `builds/masterTrack-playtest/`, which is an export plus two files copied in:
`packaging/READ ME FIRST.txt` and `steam_appid.txt`.

**`steam_api64.dll` arrives on its own.** The csproj wiring survives `dotnet publish`, so it lands
in `data_masterTrack_windows_x86_64/` with the managed assemblies. Nothing to do by hand.

**`steam_appid.txt` turns out not to be needed.** Godot will not copy it — it is not a resource —
but the exported build initialises Steam fine without it, because Facepunch's `SteamClient.Init`
sets the `SteamAppId` environment variable itself before calling into the API. It is copied into
the ZIP anyway as insurance; it costs four bytes.

**No Agility SDK files are exported and that is correct.** `application/export_d3d12` on Auto
ships nothing, and the build still comes up as `D3D12 12_0 - Forward+` on the OS-provided D3D12.
Nothing to chase.

**Do not build the ZIP with PowerShell's `Compress-Archive`.** On PowerShell 5.1 it writes
backslash path separators into the archive, which is not what the format says and which some
extractors turn into files literally named `data_masterTrack.../foo.dll`. `ZipFile.CreateFromDirectory`
on .NET Framework does the same. Entries have to be added with forward-slash names explicitly.

The console wrapper is on (`debug/export_console_wrapper=2`), so `masterTrack.console.exe` ships
alongside. That is the one to ask a friend to run when something goes wrong — same game, with the
log in a window they can copy out of.

Inno Setup can wait until distribution goes beyond this group — at which point a real App ID
(Steamworks partner account, $100 per app, recoverable against revenue) and a Steam depot upload
are the same conversation. Godot ships no Windows installer generator; the only packaging format
it produces natively is `.dmg` on macOS.

---

## Milestones

**Phases 0–7 are done.** Two things have never been exercised and both need other people:

1. **The relay with more than one client.** Client-to-client traffic goes through the host, and
   that path only engages with two clients — which one machine cannot produce, because Steam will
   not run three of its API clients at once. It works on ENet; the Steam version of it is unproven.
2. **A client Track Master actually placing tiles.** Proved as far as loading the board. Needs a
   mouse, so three windows on ENet is enough to settle it without waiting for anybody.

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
