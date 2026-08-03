# Game feel: the feedback the game is missing

A running list of the moments this game has built and not yet *sold*. Written after a pass over
the shipped kit looking for places where something important happens and nothing tells you.

**Status (2026-08-02, branch `hazards_update`): nothing here is started.** This is a wish list
with the groundwork checked — every phase below names the hook it hangs off, and in several
cases the hook already exists and has no listener.

The through-line: the mechanics are in. Tiles fall, missiles land, cars get chained, hazards
fire. What is missing is the layer that says *that just happened, and a person did it to you*.
An asymmetric game lives on that layer — a racer who cannot tell a debuff from a bug, and a
sentry who cannot tell a hit from a miss, are both playing alone.

---

## House rules this inherits

These are already how the codebase behaves. Every phase below is written to keep them.

**Effects are honest.** `SentryBlast.SpawnFireball` grows the fireball to the true blast radius,
and `SentryMagnet`'s outermost ring is the field's real reach. What a racer learns to dodge is
the distance that actually throws them. No effect here may be bigger or smaller than the thing
it represents.

**Effects build themselves.** `WheelSmoke` is a `GpuParticles3D` subclass that assembles its own
`ParticleProcessMaterial` in `_Ready` rather than being authored into `Racer.tscn` — the same
reasoning the track uses for its geometry. One definition, and anything that wants the effect
gets it by having one as a child. New effects follow suit.

**VFX are never replicated.** Every peer builds the same effect off the same broadcast, the rule
the whole sentry kit already lives by. Nothing in this document adds a packet.

**Wrappers are freed in `_ExitTree`.** A refcounted resource left to .NET shutdown is disposed
after native teardown and can crash the process on exit. Every material and mesh made in code
gets an explicit `Dispose`.

**There is one shared fuse.** `SentryActions.LeadSeconds` is 2.0 s and everything honours it —
the missile's descent, the barrel bomb, the oil slick's spread, the magnet's spin-up, the pop-up
ramp's rise, and the delayed debuffs in `SentryManager`. It is a contract with the racer that the
game has never spent: two seconds of warning is worth nothing if nothing on screen counts it down.

---

## Phase 1 — The landing

The best ratio of feel-per-hour on the list, and the signal is already sitting there unused.

### The hook

`TrackTile` declares `[Signal] TileLanded(int trackIndex)` at
`scripts/tiles/TrackTile.cs:75` and emits it in two places — the instant-place path and the end
of the descent. **Nothing in the codebase connects to it.** It was built for this.

### The numbers change the design

`TileFallSpeed` is **130 m/s** over `TrackTile.Size * 2.5` (`scripts/tiles/TrackController.cs:57`
and `:70`) — about three quarters of a second. The tile does not sink, it **slams**: a hundred
metres of road arrives at highway speed and the world does not react at all.

So this is an impact, not a gentle arrival. A soft poof would undersell what is actually
happening. (Note: `README.md` still describes the old 24 m/s five-second descent. Stale — worth
fixing while in here.)

### The work

- **Dust ring.** An expanding annulus of particles from the tile's footprint edge, thrown outward
  and low. Copy `WheelSmoke`'s shape — a `GpuParticles3D` subclass that builds its look in
  `_Ready` and emits manually — which also means the dust shares a material language with the
  tire smoke for free.
- **Honest footprint.** The ring is the tile's real footprint, per the house rule, so a racer
  reads "that is where road now is" off the dust itself.
- **Shadow telegraph.** A dark quad on the ground under the descending tile, tightening as it
  falls. Three quarters of a second is very little warning; the shadow converts the whole descent
  into readable information instead of only the last frame.
- **Camera kick.** Cars within a tile or two get a shake. `CameraRig` already runs a
  noise-driven speed shake (`ShakeAngle`, `ShakeFrequency`, `FastNoiseLite _shakeNoise`,
  `scripts/racer/CameraRig.cs:83`–`115`) — an impulse channel rides on that machinery rather than
  needing its own.
- **Seam scuff.** A dust smudge left at the joint for a few seconds, so a racer arriving late
  still sees that the road is fresh.

Every one of these is driven off `TileLanded` on each peer independently. No new traffic.

---

## Phase 2 — The sentry fires into a void

The biggest gap for "does this mode feel finished." The builder clicks a button and something
happens somewhere on a board they may not even be pointed at.

- **Hit confirmation.** A missile lands, cars are thrown, and the sentry's UI says nothing. Feed
  the result back: a flash on the caught racers' chevrons plus a status line — `Caught 2` — is
  enough to turn a button press into a play. `SentryBlast.Explode` already walks exactly the set
  of cars it threw, so the count is free at the point of impact.
- **Follow-through camera.** Firing should take the sentry to the consequence. `SentryBar` has
  three camera modes already (`Follow` / `Pack` / `Free`, `scripts/ui/SentryBar.cs:233`); a
  fourth transient "watch what I just did" mode would sell every tool in the kit at once, and
  return to the previous mode when it ends.
- **The pool needs drama.** `SentryBar._Process` redraws a `ProgressBar` every frame, so a
  20-point spend and a 90-point spend look identical. Spending should drain visibly, flash, and
  recoil.
- **Arming has no world presence.** Armed to place a bomb, the cursor is still a cursor. A ghost
  of the thing under the crosshair — designed for hazards in the sentry plan's Phase 4 and never
  built — applies to the whole placement half of the kit.

---

## Phase 3 — The racers are attacked by nobody

The other half of the same conversation. An asymmetric game runs on knowing there is a person on
the other end.

- **Attribution.** When a missile picks you, nothing says a human chose *you*. The sentry's name
  on the warning — a brief vignette, `Husk is targeting you` — converts a physics event into a
  rivalry, which is the whole reason the role exists.
- **Spend the shared fuse.** `LeadSeconds` gives every tool a consistent two-second tell and the
  racer HUD ignores it completely. A directional incoming-warning arrow, counting down that exact
  window, cashes in a contract the sentry code already honours everywhere.
- **Debuffs are unlabelled.** `RacerController.Debuffs.cs` applies crossed wires, runaway booster,
  bouncy and chained, and no HUD element says what is on you or how long is left. A first-time
  player reads reversed steering as a bug in the game, not as a move by an opponent. This is the
  single most likely thing to be mistaken for broken.
- **Hazard warnings.** Carried over from the launch review and repeated here because it is the
  same layer: `RallyCopilot` and `TrackFeed` have no `HazardKind` awareness at all, so
  builder-placed furniture arrives with no callout. Note also that `RallyCopilot` keeps its own
  `LeadSeconds = 2.5f` (`scripts/ui/RallyCopilot.cs:48`), separate from the sentry's 2.0 s —
  worth deciding whether those are deliberately different or accidentally so.

---

## Phase 4 — Lights out

Cheap, and it makes the whole match feel authored rather than assembled.

- **Phase transitions are cuts.** Build → race swaps the UI and starts. `BuildPhasePanel` is a
  countdown label and a Done button; there is no lights-out, no `3… 2… 1`, no moment where
  everyone knows it has begun.
- **The build has no reveal.** The builder spends a minute or more making a thing nobody has
  looked at. A flythrough of the finished track as the phase closes would pay that off and give
  the racers their one legitimate look at what is coming.
- **Winning is one line of text.** `Game.cs:219` sets a label and a linger timer runs out. No
  slow-motion, no camera on the winner, no finishing order. See the launch review — the results
  screen is its own piece of work, but even the existing single-winner path deserves a moment.

---

## Phase 5 — The board as a place

The builder spends the whole match looking at this and it is currently a diagram.

- **The racers are chevrons.** `RacerChevron` and the board's `RacerMarker` list give position and
  heading, which is information, not presence. Speed, a trail, or a tell for "this one is about to
  crash" would make the board something you watch rather than something you read.
- **Nothing is celebrated.** A racer eating a hazard the builder placed is the entire fantasy of
  the role and the board does not react. `TrackFeed` exists and could be carrying
  `Astra hit your pop-up ramp` — the same plumbing the hazard warnings need.
- **The hazard gestures are inert.** From the launch review: the slot highlights are static
  unshaded tori at one alpha (`scripts/trackmaster/TrackMasterController.Hazards.cs:325`) with no
  pulse and no nearest-slot brightening, there is no ghost preview, and lift mode is invisible —
  placed hazards do not light up, so the builder clicks from memory.

---

## The cross-cutting one: none of this makes a sound

There is no `AudioStreamPlayer` anywhere in `scripts/sentry/` or `scripts/tiles/`. Missiles,
explosions, the magnet, the cargo spill, oil, pop-up ramps, and the tile slam are all silent, and
the `Music` bus in `default_bus_layout.tres` has never had anything sent to it.

This is carried over from the launch review rather than being a new idea, but it belongs in this
document because it is the same layer and because **half of every effect below is the sound**. A
dust ring with no thud is a screensaver. Whatever order the phases run in, the audio for a moment
should land with that moment rather than in a pass at the end.

---

## Open questions

Assumed defaults in brackets, so none of these block a start.

1. **Does the landing shake apply to the board camera too?** *[Assumed: no. The builder is looking
   down from altitude and a shaking board is nausea, not impact.]*
2. **Is attribution always on, or only for targeted tools?** *[Assumed: only for tools that pick a
   car. A missile aimed at road is aimed at the road; naming the sentry for it makes everything
   feel personal and so nothing does.]*
3. **Does the follow-through camera steal control, or offer it?** *[Assumed: it eases, and any
   input from the builder cancels it instantly. A camera that takes the wheel during a fight is
   worse than no camera help at all.]*
4. **Should the shared fuse be one number?** `SentryActions.LeadSeconds` is 2.0 s and
   `RallyCopilot.LeadSeconds` is 2.5 s. *[Assumed: they stay separate — one is "how long until
   this hits you", the other is "how far ahead do I call a corner" — but the difference should be
   deliberate and written down.]*

---

## Risks

1. **Particle budget on the board camera.** The builder sees the whole track at once. Dust on
   every landing is fine; dust that persists on every tile is a wall of alpha from altitude. Keep
   lifetimes short and let the seam scuff be the only lingering part.
2. **Warning fatigue.** Attribution, incoming arrows, debuff labels and hazard callouts all
   compete for the same corner of the racer's screen at the same two-second mark. These want to be
   designed as one HUD, not four features that each grew an element.
3. **The telegraph could out-read the tile.** A shadow that is clearer than the falling slab
   teaches racers to watch the ground instead of the sky, which loses the drop entirely. Keep it
   subordinate.
4. **Honest VFX cuts both ways.** Once dust marks the true footprint, it becomes a promise. Any
   later change to how footprints work has to change the dust with it.

---

## Suggested order

1. **Phase 1**, whole. Self-contained, hangs off an existing unused signal, and it is the moment
   every player in both roles is already looking at.
2. **Phase 2's hit confirmation**, then **Phase 3's debuff HUD**. They are two halves of one
   conversation and shipping either alone leaves the other side still talking to itself.
3. **Phase 4's lights-out.** A day's work that reframes the whole match.
4. Phase 5 and the rest of Phases 2–3 as the mode fills out.

Audio rides along with whichever phase it belongs to, rather than waiting for a pass of its own.
