# Tower Defense mode

A third game mode, built 2026-08-04 on branch `hazards_update`. Written down because most of
the decisions here were made in conversation and none of them are recoverable from the diff.

**Status: the proof of concept is in and playable.** One turret, auto-firing. Everything below
marked OPEN is deliberately not built yet.

---

## The shape

Identical to Sentry — build → rig → race, same clocks, same cameras, same money, same placement
gesture — with one difference that changes the whole feel: **what the rig phase sells plays
itself**. The builder buys rocket turrets, puts them beside the road, and then watches.

That difference is worth having both modes for. Sentry keeps the builder's hands busy through
the race, timing traps. Tower Defense moves every decision into the rig phase and makes the race
the answer coming back. Which is more fun is an open question, and the point of trying it.

Because the two modes share the flow, the phase machine is no longer asked whether the mode *is*
Sentry — it asks `GameManager.IsPhasedMode`, which means "the track is finished before anybody
drives on it". Any later mode that furnishes a finished track inherits the whole flow by
answering true.

The mode's identity lives in exactly three places, and it should stay that small:
`TrackMasterController.ShopKinds` (what is on the shelves), `ShopTitle`, and `FireToolAvailable`
(Tower Defense has nothing to fire by hand, so the button is not drawn and the race does not arm
Fire mode).

## Pylon slots are derived, not authored

The hard-looking part — "a column beside nearly every tile" — turned out to be the cheap part.

A hazard mounts to a `TrackHazardSlot` hand-placed in a piece scene. Authoring one into twenty
scenes would have been twenty chances to get it wrong and twenty files to redo whenever the road
width moved. But `PieceCatalog` already reads each piece's **route** — the road's spine in the
entry frame — and a point either side of its midpoint is exactly what a column wants.

So `PieceCatalog.WithPylons` computes a pair of `HazardSlotKind.Pylon` slots for every piece:
right of the road, then left, at `PylonOffset` (half the road plus a nine-metre gap the bridge
spans). A piece opts out with `TrackPiece.AllowsPylons`.

Two properties make this safe to put on the wire:

- **Deterministic.** Same code, same byte-identical scene file, same answer on every peer — the
  same argument that lets a seam index cross the wire.
- **Appended after the authored slots**, so no existing slot index moves.

Everything downstream then works unchanged, which is the real payoff: highlights, the ghost
preview, lift-and-refund, concealment from racers during the rig, and death-with-the-tile all
came for free.

The pylon frame breaks the usual slot convention on purpose: local **+X points at the road**
rather than -Z pointing along travel, because the two sides of a tile disagree about which way
the road is and a turret does not care which way the traffic runs.

## The turret

`RocketTowerHazard` — code-built geometry, house style. Built in its **constructor**, not
`_Ready`, because `MakeGhost` paints the children it can see and the ghost is made before the
node enters the tree. (The other code-built hazards — launch pad, pop-up ramp — still have this
bug and ghost solid. Worth fixing the same way.)

It is the one hazard that argues with its slot: `StandUpright` levels it to the world, because a
column inheriting a climbing tile's pitch would lean over the track.

**It is a tower, not a gantry.** The deck stands 26 m above the road — about five times the
column's own thickness, read against a road 54 m wide — and the turret looks properly down on the
traffic. The bridge stays at road level and the column climbs from where it lands, so the walk
across is flat and the height is all in the shaft. Two collars up the column do the silhouette
work; an unbroken cylinder that tall reads as a pipe.

The height buys a piece of counterplay for free: the barrel only droops 55°, so **there is a dead
cone directly underneath a tower** — roughly the outer third of the road on that side, when level
with it. A driver who reads the road can take that line and buy the pass. The floor exists because
a steeper shot would fire down through the turret's own deck; that it is also good for the game is
the happy part.

**Aiming runs on every peer**, off the car poses they already receive, so the barrel tracks
correctly everywhere for nothing. **Only the server decides who and when**, and broadcasts
`TrackController.FireTower` — two peers resolving "closest car" differently for one frame would
be two peers firing different numbers of rockets, and nothing downstream could reconcile that.
That broadcast is the only unreliable RPC on the track: a placement must arrive or the peer's
track is wrong forever, but a shot that arrives late is worse than one that never arrives.

Target rule: **nearest racer in range**, ties broken by peer id. Nearest is the rule a driver can
read — leading the pack past a tower costs you something and everybody behind gets the gift,
which is the kit's standing "every device is a gift to somebody" rule arrived at for free.

## The numbers, and which ones matter

| | value | |
|---|---|---|
| Deck height | 26 m | makes it a tower; sets the dead cone underneath |
| Barrel droop | 55° | the dead cone's actual size |
| Range | 2.5 tiles | one tower owns its corner, not the track |
| Reload | 7 s | about one shot per car that comes past; staggered per tower off its own address, so a row does not volley in unison |
| Traverse | 90°/s | **counterplay.** A car at speed out-runs it |
| Rocket turn rate | 45°/s | **counterplay.** Committed to the lead it launched with |
| Rocket speed | 70 m/s | a quarter above a car's 55.6 top speed — see below; ~1.9 s of flight at max range |
| Blast | 24 m radius, 28 m/s | a quarter of the missile's reach, a third of its push |
| Price | $40 | about a dozen on a 20-tile track — see below |

The two traverse numbers are the whole design. A turret that always connected would be a toll
booth, and the rest of this game is built on hazards you can out-drive. **It is supposed to
miss** the driver who commits.

The rocket is deliberately weak, and deliberately slow. A turret fires every few seconds without
anybody deciding to, so its shot has to cost a corner rather than a race: getting hit should spin
you and hand the place behind you a gift, not delete you. That is why it is not just a
`SentryMissile` with a different spawn point.

**Rocket speed is tuned against one number: `Vehicle.TopSpeed`, 55.6 m/s.** That threshold turned
out to matter more than anything else about the weapon. Tried at 50 — below it — every shot at a
racer driving away trailed them forever and the turrets were toothless no matter what else was
true. At 70 the rocket closes, and a tower is a threat again.

It is still slow on purpose: nearly two seconds in the air at full reach, long enough to watch it
come and pick a line, and slow enough that the turret's linear lead goes badly wrong over a corner
— which is a miss, which is the intent. And a car *drifting* is well above its base top speed, so
driving beautifully outruns the shot, which is exactly who should get away with it.

The history is worth keeping: 110 (too strong, no time to react) → 50 (too weak, below car speed)
→ 70. If it needs moving again, move it against the 55.6, not against the old numbers.

## The reach circles

Every tower draws an amber circle on the road through the build and rig phases, and they all
vanish when the flag drops (`RocketTowerHazard.BuildReachRing`, toggled in `_Process`). **The gaps
between the circles are the builder's plan** — without them you are furnishing a track blind, and
placement stops being a decision.

Sized for road level, not for the range: the range is a 3D distance from a turret 29 m up, so the
circle drawn is the base of that cone. Promising road the turret cannot reach would be worse than
drawing nothing.

The ghost carries one too, and it is the one circle that is always up — `MakeGhost` switches the
preview's processing off, so the toggle never runs on it. The ring is built in `_Ready` rather
than the constructor for a related reason: `MakeGhost` repaints every child it can see, and
building the ring afterwards is what keeps it amber on the preview instead of ghost-green.

**The price is set by density, not by power**, and that is a different rule from the one the traps
use. A trap is priced as a *moment* — one decision the sentry makes at one instant, and six of them
is a full rig. A turret is not a moment; it is a stretch of road that is now dangerous. Tower
defense is a genre about a **line** of them, and you cannot cover a route with four of anything.

At $40 against the standing budget (`HazardFundsBase` 150 + 18/tile) a twenty-tile track carries
about a dozen — roughly one every other tile, sparse enough to leave gaps worth finding and dense
enough to read as a gauntlet. It was priced at $110 first, on the argument that a turret asks
nothing of the builder after purchase and should pay for that. True, and beside the point: it
priced the builder's attention when what the mode needed was a price that buys enough turrets to
be a plan.

The budget itself is shared with Sentry, so **raise the price rather than the funds** if towers
end up too dense — `HazardFundsBase` and `HazardFundsPerTile` would move the trap economy too.

## Still open

- **Only one turret kind.** The framework takes more for one class and one enum case each — a
  slow-firing flak piece that only shoots cars in the air, a machine gun that nudges rather than
  throws, a beam that paints a car for everyone else's towers.
- **Racers cannot fight back.** Towers are indestructible. Counterplay is entirely "drive well",
  which may be enough and may not. Shooting a tower down needs a damage model that nothing else
  in the game has.
- **No overlap check.** A pylon beside a hairpin can stand where the same tile's road doubles
  back. `TrackFootprint` could answer this at highlight time; for now, untick `AllowsPylons` on a
  piece that looks wrong.
- **Racers get no warning.** `RallyCopilot` and `RacerController.WarnHazard` do not know about
  towers. A dormant device is supposed to be visible, and a 60 m column is — but a callout would
  still help.
- **Balance is a guess.** Every number above was picked to be roughly right and none has been
  played against a real pack.
