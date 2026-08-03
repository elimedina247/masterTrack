# Sentry mode: implementation plan

Where Sentry mode is going, in the order it should be built. Written after a design pass over
the existing kit; the decisions here are settled unless marked **OPEN**.

**Status (2026-08-02, branch `hazards_update`): Phases 0–5 are implemented.** Open questions
were answered: hazards are dealt in their own separate hand (`HazardHand`, not points, not the
tile hand); debris decays at 45 s. Phase 6 (variant swap + porting the rest of the furniture)
and Phase 7 (RallyCopilot rework) remain. Placement shipped as click-card-then-click-lit-slot
rather than a literal drag — same gesture family as everything else on the board.

The through-line: Sentry mode currently has eight tools and seven of them are "pick a car, make
it worse." The plan adds tools that change *the road*, gives the builder hazard placement that
works in every mode, and fixes an economy that the regen change quietly broke.

---

## Phase 0 — done

Landed already, in `SentryActions`, `SentryBarrelBomb`, `SentryMissile`, `SentryManager`,
`GameManager`, `LobbyPanel`:

- **Barrel bomb** is a timed charge, not a proximity mine. 2 s fuse, then it blows on its own.
  Radius went `TrackTile.Size * 0.55` → `* 1.3`, bigger than the missile's.
- **Missile** descends at 75 m/s from 150 m (was 30 m/s from 220 m) — ~2 s of warning,
  matching `SentryActions.LeadSeconds`.
- **Point limit is a lobby setting**, `GameManager.SentryPointLimit`, default **500**, choices
  100/250/500/1000. Host-owned, replicated, re-sent to late joiners and at match start. Row is
  hidden outside Sentry mode.
- **Regen**: 5 pts/sec during the race phase, capped at the limit, through the same
  `NotifyPoints` broadcast every spend uses.

---

## Settled design decisions

**Economy.** Cooldowns *and* a meaningful pool. Cooldown stops repeat-spam of one tool; the pool
stops firing the whole kit at once. Different failure modes, both worth guarding. Because
cooldowns carry the anti-spam load, costs rescale ~4–5×, not the 10× a pool-only design needed.

**Hazards are components, placed into slots.** A piece scene declares `TrackHazardSlot` markers,
read out of the scene file by `PieceCatalog` exactly as seams already are. Dropping a hazard fills
a slot. Placement is legal by construction — no overlap maths, no lateral clamping — and slots are
*curation*: every hazard lands somewhere a human decided was a good spot.

**Composition is the base; authored variant is an upgrade.** Filling a slot instances the hazard's
scene into it. Optionally the catalog declares an override (`Straight + PopUpRamp →
PopUpStraight.tscn`) used at build time only. This is not optional polish — the sentry *needs*
composition, because swapping a tile out from under moving cars at race time means a frame with no
collision, a rebuild, and a possible load hitch. Composition also avoids owing the game
20 pieces × 8 hazards = 160 scenes; you author the ten combinations that deserve it.

**Variants must be seam-identical and route-identical to their base.** The track chains
`EntryFrame` through `ExitFrame`; if a variant's geometry contract differs by any amount, every
tile downstream shifts. Enforce in the catalog with a loud failure, don't trust it by hand.

**The old `TileHazard` enum splits three ways.** *Shape* (hairpin, loop, ramps, split, gap) stays
generated geometry — it *is* the road. *Surface* (ice, gravel) is an interval along the spine.
*Furniture* (launch/boost pads, crusher, spinner, slalom, log trap, whoops) becomes draggable
components, and so do the new ones.

**Hazard kicks must be velocity-level, not forces.** The grip solve eats outside forces — the
chain hit this and was rewritten as a velocity rope. Anything shoving a car goes through the
impact layer (`RegisterImpact` + direct velocity edits), the way car-to-car contact already does.

---

## Open questions

Work is sequenced so none of these block a start. Assumed defaults in brackets.

1. **How does the builder acquire hazards?** Dealt in hand alongside tiles / bought with points /
   unlimited but slot-limited. *[Assumed: bought with sentry points in Sentry mode, creating the
   build-vs-race tension; dealt alongside tiles in Live Build.]*
2. **Does cargo-spill junk decay or persist?** *[Assumed: 45 s lifetime with a fade, tunable to
   infinite. Permanent is funnier; unknown whether it ruins the last lap.]*
3. **May the sentry cost a racer real progress** (trapdoor → respawn, ~10–15 s)? *[Assumed: yes,
   but priced as a top-tier tool.]*
4. **Global tools — rare showpieces or cheap and constant?** *[Assumed: rare. Moon gravity is
   priced and cooled as an event.]*

---

## Phase 1 — Economy and UI

Self-contained, no new mechanics, and it makes everything after it feel better. This is also the
"general UI fixes" pass.

### Files

- `scripts/sentry/SentryActions.cs` — rescaled `CostOf`, new `CooldownOf`, new `CategoryOf`
- `scripts/sentry/SentryManager.cs` — cooldown ledger + gate + broadcast
- `scripts/ui/SentryBar.cs` — pool bar, cooldown display, grouping
- `scripts/trackmaster/TrackMasterController.Sentry.cs` — hotkeys

### Cost and cooldown table

Sized against a 500 pool at 5 pts/sec (300/min). A full pool is a real burst; sustained income
buys roughly one mid-tier play every ten seconds.

| Tool | Cost | Cooldown | Category |
|---|---:|---:|---|
| Crossed wires | 20 | 6 s | Target a car |
| Oil slick | 25 | 8 s | Place on track |
| Bouncy! | 25 | 8 s | Target a car |
| Barrel bomb | 35 | 6 s | Place on track |
| Runaway booster | 35 | 10 s | Target a car |
| Chained up | 45 | 12 s | Target a car |
| Magnet | 50 | 15 s | Place on track |
| Missile | 55 | 10 s | Place on track |
| Cargo spill | 70 | 20 s | Place on track |
| Moon gravity | 90 | 45 s | Everyone |

### Cooldowns

Server truth, mirroring `TrySpend` exactly. `SentryManager` holds
`Dictionary<SentryActionKind, ulong> _cooldownUntil`. The gate becomes affordable **and** off
cooldown; rejection reads `"Missile is reloading — 4s."`

Clients need it for the UI only. Broadcast `NotifyCooldown(kind, seconds)` on every successful
spend and let each peer run its own countdown from receipt — the same trick the build clock uses
in `GameManager.NotifyMatchPhase`. A few ms of disagreement is invisible; only the server's copy
gates anything.

### UI work

- **Pool readout** becomes `340 / 500` with a `ProgressBar` beneath it. Regen is currently
  invisible — the best new mechanic in the mode reads as nothing without this.
- **Cooldown display**: button disabled, text suffixed with the countdown, thin draining bar
  under it. Godot `Button` has no radial sweep and it isn't worth custom drawing.
- **Grouping**: three captioned rows — *Target a car* / *Place on track* / *Everyone* — instead
  of one `HFlowContainer`. Eight buttons is already at the limit and the kit is heading past twelve.
- **Unaffordable buttons** dim rather than vanish, showing the shortfall, so the sentry can see
  what they are saving toward.
- **Hotkeys** `1`–`9` arm, `Shift`+`1`–`9` for the overflow row, world-click still fires,
  right-click still cancels. Verified free: the board only binds `builder_undo` and `camera_look`.

---

## Phase 2 — Two new sentry tools

Independent of the hazard framework, cheap, and they exercise the Phase 1 cooldown system with
real tools before the big infrastructure lands. This is where "more fun" first becomes playable.

### Magnet — `scripts/sentry/SentryMagnet.cs` (new)

Placed at a point, lives ~5 s, drags every car within radius toward its centre.

- Reuses the chain's velocity-level maths almost directly — `RacerController.ApplyChainRope` is
  already "pull toward a point, capped, don't fight the grip solve."
- New `RacerController.ApplyMagnetPull(Vector3 centre, float radius, float strength, float delta)`
  in `RacerController.Debuffs.cs`, early-returning on `IsRemote` like every other force there.
- **Design rule: it must pull off-line, not backward.** A magnet that drags you back down a
  straight is a speed tax and feels awful. One placed on the outside of a corner drags you wide
  into the wall — same tool, and now placement is a skill.
- Visual: unshaded pulsing concentric ground rings, like the missile's target ring, plus a faint
  vertical column so it reads from the board camera.
- Combo: it pulls cargo-spill debris too. A magnet over a junk field is a vortex.

### Cargo spill — `scripts/sentry/SentryCargoSpill.cs` + `SentryDebris.cs` (new)

A rainfall of cubes and spheres that persist on the track as terrain.

- **Tuning insight: the redirect is yaw, not speed loss.** Momentum ratio against a 1200 kg car
  won't allow meaningful deceleration, so don't try. A centred hit is a thump you plough through;
  an off-centre clip puts yaw into the car. The hazard then punishes a bad *line* rather than
  punishing speed — which is the version that fits the game.
- Props want to be heavier than instinct says: **80–120 kg**. Confetti-light debris just scatters
  and feels like nothing.
- **Networking: seeded spawn, local simulation, no replication.** The broadcast carries
  `(Vector3 target, int seed)`; every peer builds the identical drop from an RNG seeded with it,
  and each machine only kicks its own cars — the same rule the blasts already follow. Syncing 200
  rigid bodies is not on the table.
- **Settle fast** — heavy linear and angular damping, near-zero bounce, asleep within a second or
  two. Short divergence window means every peer converges to nearly the same field without
  syncing, *and* sleeping bodies are nearly free, which is what makes persistence affordable.
- **Cubes settle** into static terrain; **spheres keep rolling** and stay live hazards. Both from
  one drop.
- Global cap with FIFO despawn (~150), via a `sentry_debris` group.
- Art is free: the house style is faceted CSG primitives with `SmoothFaces = false`, so cubes and
  spheres are literally on-model.
- Extend `SentryBlast.Explode` to push debris rigid bodies as well as cars — then the sentry can
  lay a field and missile it into the pack. Two-tool plays are what make a sentry feel like a
  player rather than a button.

---

## Phase 3 — Hazard component framework

The infrastructure. No new player-facing hazards beyond porting one to prove the pipeline.

### New files

- `scripts/tiles/TrackHazardSlot.cs` — `[Tool] [GlobalClass]`, a `Marker3D` carrying a `SlotKind`
  and a size budget. Authored into piece scenes.
- `scripts/tiles/HazardKind.cs` — enum for *furniture* hazards. Deliberately **not** `TileHazard`,
  which means "which geometry to generate." Append-only, same as `TileHazard`, because it goes
  over the wire as an int.
- `scripts/tiles/TrackHazard.cs` — base `Node3D` for a placed component, plus the per-kind factory.
- `scripts/tiles/PlacedHazard.cs` — the record `(int TileIndex, int SlotIndex, HazardKind Kind)`
  with `ToDict`/`FromDict`, mirroring `TileData`.

### Slot kinds

Hazards mount differently, so the slot carries a filter:

- `Surface` — flat on the road: pop-up ramp, trapdoor, boost/launch pads
- `Centre` — a pivot mid-road: spinner
- `Overhead` — mounted above: crusher
- `FullWidth` — spans the road: gate slam, log trap

A straight might carry two or three `Surface` and one `Overhead`; a hairpin one; a loop none.
Pieces that accept nothing are a design lever — the builder learns which pieces are
hazard-friendly, which makes piece choice itself more interesting.

### Modified

- `scripts/tiles/tool/PieceCatalog.cs` — read `TrackHazardSlot` nodes out of scene files. The
  mechanism already exists: `ReadRoute` walks `SceneState` looking for the `Spine` node, and seam
  reading does the same. Add `Slots` to `PieceEntry`.
- `scripts/tiles/TrackGrid.cs` — `PlacedTile` gains a hazard list. World placement of a slot is
  `EntryFrame * slot.Local`, the same transform `PieceFootprint` already uses for the route.
- `scripts/tiles/TrackController.cs` — request/validate/broadcast path for hazard placement and
  removal, mirroring tile placement.

### Proof of pipeline

Port **LaunchPad** first. It already exists as a trigger volume handing out an impulse
(`TrackTile.Hazards.cs`, `LaunchImpulse = 24.0f`), it is visually unmistakable, and it needs no
new physics. If a launch pad can be authored into a slot and fire correctly, the framework works.

---

## Phase 4 — Builder drag-and-drop placement

### Files

- `scripts/trackmaster/TrackMasterController.Hazards.cs` (new partial) — drag state, slot
  highlighting, ghost preview, commit
- Hazard buttons alongside the existing tile palette

### Interaction

1. Builder picks a hazard from the palette.
2. **Every compatible slot on nearby tiles lights up** — faint outlines on the road, brightening
   on the nearest one. This is the affordance that makes the whole feature self-explanatory, and
   it reuses the ghost-preview idea the editor already uses at open seams
   (`TrackAssembly.GhostMeta`).
3. Cursor raycast → hit tile → nearest compatible free slot → translucent ghost snapped there,
   oriented to travel. Slots carry the piece's pitch and bank for free, so a hazard on a banked
   corner sits correctly rolled with no special casing.
4. Release commits: request → server validates (tile exists, slot free, kind fits filter, budget,
   per-tile cap) → broadcast → every peer spawns from `(tileIndex, slotIndex, kind)`.
5. Right-click a placed hazard during build lifts it and refunds.

Address is ~12 bytes on the wire and rides the existing placement RPC path. Because a hazard is
addressed by tile index, it dies with its tile automatically — no orphans left floating where a
removed tile used to be.

---

## Phase 5 — Pop-up ramp, and sentry runtime placement

Where the build-time and race-time paths converge on one implementation.

- `scripts/tiles/hazards/PopUpRamp.cs` (new) — a wedge that rises out of the road and launches
  whoever meets it forward and up. Reuses the blast impulse path. Cruel-*funny* rather than cruel,
  because sometimes it helps you.
- New `SentryActionKind.PopUpRamp`: the sentry clicks a tile, it fills the nearest free
  compatible slot. **Composition only at race time — never a variant swap.**
- Deterministic from the broadcast, exactly like the barrel bomb, which already works.

One implementation, three spawn paths: authored into a piece at design time, dragged in by the
builder at build time, dropped by the sentry at race time.

---

## Phase 6 — Variant swap, and porting the old hazards

- `TileCatalog` gains an override table: `(basePieceName, HazardKind) → variantScenePath`.
- Catalog-time validation that the variant's seams and route match the base exactly. Loud failure.
- Author `PopUpStraight.tscn` — the first real variant, with the ramp housing modelled into the
  road, recessed, hazard-striped. This is what proves the upgrade path with actual art.
- **Build phase only.** Race-time placement stays composition.
- Then port the rest of the furniture: boost pads, crusher, spinner, slalom, log trap. Each one
  returns to the deck *and* becomes available to the sentry at the same time.

---

## Phase 7 — Warnings and tuning

- **`RallyCopilot` needs reworking.** It currently calls the next *tile* by its single
  `TileHazard`, position-swept within `LeadSeconds` of the entry seam. With N hazards per tile it
  needs to announce a list, and probably where on the tile they sit. This is not a small UI job
  and it is the piece most likely to be underestimated.
- `TrackFeed` (announces placements to everybody) needs the same treatment for hazard placement.
- Tuning passes: per-tile hazard cap, debris cap and lifetime, magnet radius, cost table against
  real play.

---

## Risks, in the order they are likely to bite

1. **Readability, not perf, is the constraint on hazards.** A track with sixty hazards is
   unreadable soup at 200 km/h. Start the per-tile cap low (2–3) and loosen it if it feels sparse.
   The cap is a design tool first.
2. **Procedurally-generated tiles have no spine.** `PieceFootprint` already has a
   `route.Count < 2` fallback, but hazard slots on generated tiles need either synthesized
   straight-line spines or an explicit "authored pieces only" restriction at first.
3. **Cargo-spill divergence is visible in a way the missile's is not.** The missile diverges for
   two seconds then stops existing; debris persists in slightly different places on every screen
   forever. Settling fast mitigates it. This is the one item that genuinely needs to be seen
   before it is committed to.
4. **`RallyCopilot` rework** (see Phase 7) is larger than it looks.
5. **The grip solve** will silently eat any hazard that pushes cars with a force instead of a
   velocity edit. Every new pusher goes through the impact layer.

---

## Suggested order

Phases 1 → 2 deliver playable new fun in the smallest number of changes and validate the economy
against real tools. Phases 3 → 5 are the structural investment, and 5 is the first point where
the hazard framework pays a visible dividend. Phase 6 is polish with real art; Phase 7 is the
debt Phase 3 creates.

Phase 2 is independent of 3–6 and can run in parallel if convenient.
