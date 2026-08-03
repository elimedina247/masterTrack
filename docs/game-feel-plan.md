# Game feel: the feedback the game is missing

A running list of the moments this game has built and not yet *sold*. Written after a pass over
the shipped kit looking for places where something important happens and nothing tells you.

**Status (2026-08-02, branch `hazards_update`): Phases 1, 2 and 4 are implemented.** Phase 1
shipped visuals-only with audio deferred; sounds have since started landing alongside (the blast,
the barrel's countdown). Phase 4 grew: it shipped with scene-fade transitions, the race countdown,
elimination + spectating, and the results board — see its section. Phases 3 and 5 are unstarted.

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

## Phase 1 — The landing — **done, bar the sound**

The best ratio of feel-per-hour on the list, and the signal was already sitting there unused.

### The hook

`TrackTile` declares `[Signal] TileLanded(int trackIndex)` at
`scripts/tiles/TrackTile.cs:75` and emits it in two places — the instant-place path and the end
of the descent. It was built for this, and `TrackTile.Landing.cs` is now the listener: the tile
connects to its own signal in `_Ready` rather than the effects being called out of `StepFall`, so
the whole layer lifts off in one line and anything else that wants the moment — a sound, a feed
line, a board flash — hooks the same place.

### The numbers change the design

`TileFallSpeed` is **130 m/s** over `TrackTile.Size * 2.5` (`scripts/tiles/TrackController.cs:57`
and `:70`) — about three quarters of a second. The tile does not sink, it **slams**: a hundred
metres of road arrives at highway speed and the world does not react at all.

So this is an impact, not a gentle arrival. A soft poof would undersell what is actually
happening. (`README.md` described the old 24 m/s five-second descent; fixed while in here.)

### The work

- **Dust ring.** `TileLandingDust`, a `GpuParticles3D` subclass built along `WheelSmoke`'s lines —
  assembles its own look in `_Ready`, emits manually, one shot, frees itself when the last puff
  dies. Fired off the whole perimeter at once, outward and low. It departs from `WheelSmoke` in
  one place: a generated radial falloff texture on the puff quad, because a bare quad is fine at
  the sub-metre scale of tire smoke and reads as a failed sprite at seven metres.
- **Honest footprint.** The ring is the tile's real footprint. Measured rather than assumed —
  `MeasureFootprint` merges the bounds of every drawable hanging off the tile, because the origin
  sits in a different place for a straight, a turn and an authored piece, and only reading the
  geometry is right for all three. A box, which is what the game already means by a footprint.
- **Shadow telegraph.** `TileDropShadow` — a quad held on the resting plane while the tile falls
  over it, tightening from 1.5× onto the true footprint and darkening as it comes. Kept
  subordinate on purpose (risk 3): it starts nearly invisible and only earns its weight in the
  last few metres, so the tile stays the thing you watch.
- **Camera kick.** `CameraRig.AddImpact` — an impulse channel riding the existing noise-driven
  speed shake rather than owning any machinery of its own. Cars inside two and a half cells get a
  squared falloff. Racers only, per open question 1: the builder's board does not lurch.
- **Seam scuff.** `TileSeamScuff` — a band across the entry seam, fading down the road and out
  over 4.5 s. The only lingering part, per risk 1.

Every one of these is driven off `TileLanded` on each peer independently. No new traffic.

**Still owed:** the sound. Everything above is silent, which by this document's own argument is
half an effect — see the cross-cutting section.

---

## Phase 2 — The sentry fires into a void — **done**

The biggest gap for "does this mode feel finished." The builder clicked a button and something
happened somewhere on a board they may not even have been pointed at. Now:

- **Hit confirmation.** `SentryBlast.Explode` counts every car inside the radius — geometry
  against replicated poses, identical on every peer, so no packet carries the answer — and
  reports through `SentryManager.BlastLanded`. The bar's status line says `Caught Husk!` /
  `Caught 2!` / `Missed — nobody in the blast.`, and the caught racers' name markers flash white
  on the board. A miss is announced as loudly as a hit, on purpose: the silence after a whiffed
  missile was the exact thing that made the role feel unfinished.
- **Follow-through camera.** A fourth transient `BoardCameraMode.Watch`: every confirmed placed
  tool (`SentryManager.ActionPlaced`, fired on confirmation rather than request, so a rejection
  never flies the camera) eases the board to the target for a per-tool few seconds, then hands
  back to whatever mode it borrowed the camera from. Any key or click ends it instantly and
  still does its own job — the input that cancels the watch is not eaten. Settles open
  question 3 the assumed way.
- **The pool has drama.** The drawn bar chases the ledger through an eased gap — a 90-point
  spend visibly drains where a 20-point spend blinks — and a spend (never a regen tick) flashes
  the bar and punches the readout's scale.
- **Arming has world presence.** While a placed tool is armed, a pulsing ring rides the cursor's
  raycast point at the tool's <i>true</i> radius — the missile's and barrel's blast circles in
  warning red, the magnet's field in its blue, the oil's puddle, the spill's scatter — honest
  VFX applied before the effect exists. The pop-up ramp's ghost snaps to the slot the click
  would actually take, in the slot highlights' green.

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

## Phase 4 — Lights out — **done, plus the death layer it turned out to need**

Grew past its original writeup: making the ending a real screen forced the question of what
happens to racers who *don't* reach it, and the game had no answer — falling always respawned,
even off a track that no longer existed. So Phase 4 shipped with elimination.

- **Scene transitions fade.** `SceneFader` (autoload, `scripts/ui/SceneFader.cs`): every
  `ChangeSceneToFile` in the game now goes through a fade to black, a small loading card over a
  threaded load, and a fade back in. One cut in a game full of fades reads as a crash, so there
  are no cuts left.
- **The race starts on a count.** `RaceCountdown`: 3… 2… 1… RACE! on every screen as the cars
  spawn, driver input locked until RACE!. `OnBeat` is the audio hook — one call per number, one
  for the horn.
- **Death exists, in gamemodes only.** The lobby and proving ground never set
  `RacerController.EliminationEnabled`, so nothing there can kill you — the match scene sets it
  on the local car at spawn. In Sentry mode's race phase every kill-plane fall is fatal (being
  knocked off the road is the kit working); in Live Build only falling from road that has
  already crumbled is fatal, judged off the last tile the wheels touched. Owner detects,
  server validates and broadcasts (`GameManager.RacerEliminated`), every peer switches the car
  off — out of the racers group, so the board markers, finish sweep and blasts all forget it in
  one move.
- **The dead spectate.** `SpectateCamera`: chase-cam on a living racer, click to cycle,
  retargets itself when the watched car dies too.
- **Winning is a results board.** `MatchResults`: finishers by place, survivors by progress,
  the dead greyed at the bottom (latest death first). Three endings, one board: a racer's
  `{name} wins!`; every racer dead in Sentry mode is the sentry's `{NAME} WON !!!`; every racer
  dead in Live Build is `UNFINISHED`. The finish sweep now records every crossing during the
  victory linger, not only the first, so late finishers make the list.
- **Still unbuilt from the original writeup:** the build-phase flythrough reveal.

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

1. ~~**Does the landing shake apply to the board camera too?**~~ **Settled: no.** The builder is
   looking down from altitude and a shaking board is nausea, not impact — and it would shake
   hardest exactly while they are aiming the next tile. `KickNearbyCameras` walks the racer group
   and nothing else.
2. **Is attribution always on, or only for targeted tools?** *[Assumed: only for tools that pick a
   car. A missile aimed at road is aimed at the road; naming the sentry for it makes everything
   feel personal and so nothing does.]*
3. ~~**Does the follow-through camera steal control, or offer it?**~~ **Settled: it eases, and any
   key or click ends it instantly** — and the ending input still performs its own job, so the
   keystroke that dismisses the watch is also the keystroke that arms the next tool. Mouse motion
   deliberately does not count as input here; nudging the mouse is not a decision.
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

1. ~~**Phase 1**, whole.~~ Done except the sound, which is now the first thing owed to it.
2. ~~**Phase 2's hit confirmation**~~ done — the whole of Phase 2 is — which leaves **Phase 3's
   debuff HUD** as the open half of the conversation: the sentry now hears their own shots land,
   and the racers still can't tell a debuff from a bug.
3. **Phase 4's lights-out.** A day's work that reframes the whole match.
4. Phase 5 and the rest of Phases 2–3 as the mode fills out.

Audio rides along with whichever phase it belongs to, rather than waiting for a pass of its own.
