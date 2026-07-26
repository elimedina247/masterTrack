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
- [ ] Install export templates: **Editor → Manage Export Templates**. Currently
      `~/Library/Application Support/Godot/export_templates/` is empty on the Mac and there is
      no `export_presets.cfg` anywhere, so this has never been done.
- [ ] Export the project exactly as it stands. Confirm you get a runnable `.exe`, a `.pck`, and
      a `data_masterTrack_windows_x86_64` folder, and that Test Drive works from the export.
- [ ] Zip it, send to one friend, confirm it launches.

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

- [ ] Add `NetworkManager.IsNetworked` — non-null peer whose connection status is not the
      offline peer, rather than a concrete type check.
- [ ] Route all four call sites through it.

Worth doing regardless of Steam. Do it before Steam so the transport swap stays confined to
`NetworkManager`.

### 1.2 Scene-ready handshake

The current match start has a race condition that will break remote play. `Game.cs:99` spawns
cars one deferred frame after the host's own `_Ready`, but clients only *begin* loading
`Game.tscn` when the `NotifyGameStateChanged` RPC arrives (`MainMenu.cs:151`). The spawner's
`spawn_path` is `../Racers` — a node that does not exist on the client yet. Locally this can
pass by luck; over real latency the client ends up with no car.

- [ ] Add to `GameManager`: a set of peers that have reported their scene loaded, an
      `[Rpc(AnyPeer)] ServerNotifySceneReady()` clients call from `_Ready`, and an
      `AllPeersReady` signal the server emits when every connected peer has checked in.
- [ ] Spawn cars on that signal instead of `CallDeferred(nameof(SpawnNetworkedRacers))`.

**Wait for all peers rather than timing out.** A `MultiplayerSpawner` only sends spawn packets
to peers connected at spawn time, and will not retroactively spawn for a peer whose scene
appeared late — a straggler that misses the window is permanently carless, a silent failure that
looks like a physics bug. Show `Waiting for players… (2/4)` instead. If someone genuinely hangs,
it is visible and the host can restart.

### 1.3 Extract a shared racer arena

The lobby and the match both need: container + `MultiplayerSpawner`, spawn one car per racer
peer, wire the local car to the HUD.

- [ ] Pull that into one node script — `RacerArena` — parameterised by a spawn-point strategy
      (the match uses a start-line row, the lobby wants cars scattered on the pad).
- [ ] Move the deferred HUD-wiring pattern from `Game.OnRacerEnteredTree` into it.
- [ ] Make `Game.cs` a consumer of it.

---

## Phase 2 — TestArea becomes the lobby

### 2.1 Remove the hardcoded car

`TestArea.tscn` instances `Racer.tscn` as a `TestCar` node, and the HUD overlays
(`SpeedBlur`, `SpeedLines`, `VehicleHud`, `VehicleDebug`) bind to it through exported
`node_paths`. Those bindings break the moment cars are spawned dynamically.

- [ ] Delete the `TestCar` node.
- [ ] Let `RacerArena` spawn in both the solo and networked cases, the way `Game.SpawnSolo`
      already branches.

### 2.2 Add the arena

- [ ] Add a `Racers` container and a `MultiplayerSpawner` with `spawn_path` pointing at it,
      mirroring `scenes/Game.tscn:46`.

### 2.3 Guard the `[Tool]` path

`PhysicsTestArea` runs in the editor and calls `Rebuild()` from `_Ready` to generate the pad and
the tile grid.

- [ ] Keep all networking behind the existing `Engine.IsEditorHint()` early return.

The generated geometry needs no replication — every peer builds it identically from
`TileCatalog`, exactly like track tiles.

### 2.4 Fix respawn

`RespawnCar` (`PhysicsTestArea.cs:157`) targets a fixed `Car` export.

- [ ] Retarget it to the **local player's** car and store that car's spawn transform.
- [ ] Keep the `PhysicsServer3D.BodySetState` approach — assigning `GlobalTransform` on a rigid
      body is not reliable.

### 2.5 Lobby UI

- [ ] Connected-player list.
- [ ] Host-only **Start Match** button, moved out of `Main.tscn` (`MainMenu.cs:92`) — the host
      will be out driving, so it cannot live on the menu any more.
- [ ] Disable Start below two players: you need at least one builder and one racer.

---

## Phase 3 — Random Track Master

### 3.1 Randomise the role

`GameManager.StartMatch` (`GameManager.cs:99`) hardcodes the host as Track Master.

- [ ] Pick uniformly from all connected peers, including the host.
- [ ] Roll **only on the server** and broadcast through the existing `NotifyRoleAssigned` RPC.
      Clients must never roll their own.

### 3.2 Verify the non-host builder path

This should already work. A client Track Master's placements go through
`RpcId(1, MethodName.ServerPlaceTile, ...)` (`TrackController.cs:98`), and the host-as-builder
case is handled by the `IsServer()` branch just above it.

- [ ] Test with three local instances where a **client** draws the builder role. That
      combination has probably never run.

### 3.3 Lobby → match transition

- [ ] Move the scene-change logic out of `MainMenu.OnGameStateChanged` and into the lobby.
- [ ] Final sequence: host presses Start → server assigns roles → all peers load `Game.tscn` →
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
