# Taking the track off the grid

A plan for replacing `TrackGrid`'s cell model with a chain of transforms.

## Why

The grid quantises every dimension to `TileCatalog.TileSize`. That is the direct cause of the
last three tuning requests failing:

- tile lengths only come in 54 m steps, so "40% shorter" delivered 33%;
- corner radius is pinned to `(span - 0.5) x TileSize`, so the only legal radii are 27 m (past
  the car's grip limit), 81 m (current) and 135 m — there is nothing in between;
- the squiggle's ribbon had to fit inside one cell's width.

None of that is buying much. Here is everything `CanPlace` actually enforces:

```
cell != HeadCell                 -> placement is at the head.  A one-line invariant.
CellsFor(...) any occupied       -> does this overlap existing track?     <-- the only real one
exitCell occupied                -> same question, one step further on
HeadHeight + HeightChange < 0    -> a scalar comparison
```

So `Vector2I` cells, `CellsFor`, `ExitCellFor`, `_byCell`, the n x n turn blocks and the
hand-derived hairpin L all exist to answer one question: *does this piece overlap track that is
already there?*

And the spatial lookup the grid also provides — `TileAtWorld` — has exactly one caller
(`Game.cs:221`, a HUD readout). The car already collides with a specific `TrackTile` body, so
"which tile am I on" is available more accurately from the collision than from a cell.

**The track is a chain, not a layout.** It never branches, pieces only attach at the head, and the
head only moves forward. A chain does not need a coordinate system, it needs a running transform.
The grid is a 2-D solution to a 1-D problem and the quantisation is the rent.

## The model

### The anchor

```csharp
public readonly record struct TrackAnchor(Vector3 Position, float Yaw);
```

Position *and yaw only* — no pitch, no roll, no full `Transform3D`. This works because every tile
already enters and leaves level: `RampHeightAt` is explicitly "flat at both ends so the joints with
the neighbouring tiles are still tangential", and `BankScale` eases the bank to zero at both ends of
a corner. Nothing hands its neighbour a tilted frame. Keeping the anchor to four floats avoids
basis renormalisation and a whole class of drift.

### Exit transforms

Two shapes cover the entire catalog.

**A run** — straights, ramps, and every hazard tile. Length `L`, height change `h`:

```
exit.Position = entry.Position + Forward(entry.Yaw) * L + Vector3.Up * h
exit.Yaw      = entry.Yaw
```

**An arc** — curves, sweepers, hairpins. Radius `r`, swept angle `theta`, side `s` (+1 right):

```
exit.Position = entry.Position + Forward(entry.Yaw) * (r * sin(theta))
                               + Right(entry.Yaw)   * (s * r * (1 - cos(theta)))
exit.Yaw      = entry.Yaw + s * theta
```

Check it against what exists today. At `theta = 90°`: `sin = 1`, `cos = 0`, so the exit is
`forward * r + right * s * r` — the diagonal step the grid gets from `ExitCellFor`'s
`(ahead + out) * (span - 1)`. At `theta = 180°`: `sin = 0`, `cos = -1`, so it is
`right * s * 2r` — exactly `entryCell + Turn(TurnSide).Step() * 2`, the hairpin. One formula
replaces both special cases *and* generalises to any angle.

`r`, `L`, `h` and `theta` are floats. A 63 m corner is `63.0f`.

### Overlap

Each placed tile contributes a footprint in the XZ plane:

- a run: one oriented box, `L` by `RoadWidth`;
- an arc: `ceil(theta / 30°)` boxes along the chord — three for a quarter turn, six for a hairpin.

`CanPlace` tests the candidate's boxes against every live tile's boxes, skipping the last one
(which it legitimately butts against). Separating-axis test on two OBBs is a dozen lines.

Two deliberate differences from the grid:

1. **It is more permissive.** A hairpin currently reserves a 3 x 2 block of cells when the road only
   sweeps an annulus. Geometry lets the Track Master build tighter serpentines. This is a gameplay
   change, not just a refactor — worth playing before and after.
2. **It can be told about height.** The current check is purely 2-D, so the track can never pass
   over itself even when one part is three cubes up. Adding "overlapping is fine if the vertical
   gap exceeds a clearance" makes over-unders legal for the first time. Not required for the
   migration; noted because it becomes a two-line change rather than an impossibility.

## What happens to `TrackDirection`

It splits in two, and only half of it dies.

**The world-facing half** — `Step()`, `Forward()`, `Right()`, `Yaw()` — is replaced by the anchor's
yaw. These are the uses in `TrackGrid`, `TrackController`, `RacerArena` and `PhysicsTestArea`.

**The local-edge half survives untouched.** `TrackTile.BuildWalls` takes
`TrackDirection.North.Turn(Data.ExitTurn)` and asks which *local* edge the exit is on, so it can
wall off the other three. That is pure tile-local geometry and never touches the world. It keeps
working as long as turns stay at multiples of 90°, which they do until we choose otherwise.

This is why the migration can be staged: 35 of the 49 `TrackDirection` mentions are in
`TrackTile.cs` and `TrackDirection.cs` themselves, and most of those are the local half.

## Replication

Unchanged in kind. Every peer already replays the same ordered list of placements; composing
transforms is as deterministic as composing cells — identical operations in identical order give
bit-identical floats. Nothing about position goes over the wire, same as today.

`TileData`'s wire format gains real numbers in place of cell counts:

```
cell_length (int)  ->  run_length (float, metres)
                       turn_radius (float), turn_angle (float)
```

`TileCatalog.Match` still recovers a definition from replicated data; it compares one more field.

## Never leaving the builder without a move

This is a live bug today, not something the refactor introduces. There is no discard, no reroll and
no stuck-state handling anywhere in the codebase — `TileHand` has `Take` and `Tick` and nothing
else. If the head ends up somewhere nothing in hand fits, the track stops growing and the racers
drive off the end of it.

Two distinct failure modes, and only one of them is recoverable.

**Geometric dead end** — *no catalog tile at all* fits at the head. Unrecoverable: dealing,
discarding and waiting all fail equally, because the space is not there.

**Hand starvation** — some catalog tiles fit, but none of the six in hand do. Recoverable by dealing
or discarding, but the track stalls while it resolves.

### The rule, and what it is not

> Prefer placements that leave at least `MinimumEscapes` catalog tiles placeable at the head.

An earlier draft of this document claimed enforcing that makes a dead end unreachable by induction.
**That is wrong, and it is worth writing down why**, because the wrong version is the one that looks
obviously correct.

One step of lookahead can guarantee that N tiles *fit* at the next head. It cannot guarantee that
any of those N leaves a head with the same property. Safety here means "an infinite sequence of
placements exists" — a greatest fixpoint, not something decidable at any fixed depth. The induction
needs the property to be self-preserving and it is not.

Worse, enforced strictly the rule *causes* the failure it was written to prevent. Simulated, roughly
one head in four eventually has legal moves but none that clear the bar; refusing all of them is a
lock, and one whose cause the player cannot see. **So the threshold is advisory: it prunes a
placement only while some other tile would clear the bar, and stands down when nothing does.**

Measured over 120 random 50-tile tracks:

| | true dead ends |
|---|---|
| no rule (today) | 91 / 120 |
| rule + fallback, `MinimumEscapes = 3` | 23 / 120 |

A four-fold improvement, not a guarantee. Raising the threshold does not close the gap — 1, 3 and 6
all land within a few of each other.

### What zero would actually take

A tile that fits *everywhere*. On a 2-D grid there is no such thing: a fully enclosed head has room
for nothing, whatever you offer it. It needs the track to be allowed to climb over itself with
vertical clearance, at which point a ramp is always placeable and the guarantee is real.

The current `CanPlace` is purely 2-D and cannot express that. The transform model can — it is the
same "overlap is fine if the vertical gap exceeds a clearance" noted under Overlap above. **So the
one rule that would make this airtight is a rule the grid cannot state.** That is now the strongest
argument for the migration, and it should land as part of stage 3 rather than being deferred to
stage 5.

Two supporting notes: `TrackController.AllowUndo` is off in a match by design, so there is no
recovery path once a head is dead; and the fallback means the rule can never be the reason a
placement is refused when it was the only one available.

`CanPlace` splits into a non-recursive core and the check built on it:

```csharp
bool Fits(anchor, data)         // overlap + height only.  No lookahead.
int  Escapes(anchor)            // how many catalog entries Fit at this anchor
bool CanPlace(data, out reason) // Fits(Head, data) && Escapes(ExitOf(Head, data)) >= MinimumEscapes
```

The split matters: without it the lookahead recurses.

`MinimumEscapes` is the tuning knob the requirement really wants. At 1 it is the bare guarantee —
the builder always has *a* move, possibly only one, possibly a short straight into a corridor. At 3
or 4 the track is pushed away from pockets well before it reaches them, at the cost of rejecting
placements that would technically have been survivable. Start at 3.

This is also finishing a thought the code already started. The existing
`"That would run the track back into itself."` check is exactly this idea at a depth of one cell —
it looks at the single cell past the exit and rejects if it is occupied. The invariant generalises
that from one cell to "can anything actually follow this".

New rejection message: **"That would leave the track nowhere to go."**

### Cost, and why it is small

`Escapes` is ~26 footprint tests. It is not per frame — the placeable set is a property of the
**head**, so it changes only when a tile is placed or the tail retires. Compute it once per
placement and both consumers read it:

- the ghost preview and the palette's enabled/disabled state become a set membership test;
- the lookahead needs `Escapes` at the *prospective* head, which is at most six computations per
  head (one per hand slot), and each is cacheable against that slot.

Bound the inner test with a coarse spatial hash over the live tiles — 200 m buckets — so a long
track does not make each check O(n).

Retirement only ever helps: removing tiles from the tail frees space and can never remove a legal
move, so the invariant is monotonic in the right direction.

### Keeping it true of the *hand*, not just the catalog

The invariant above guarantees the catalog has an answer. It says nothing about whether the Track
Master is holding one. Two mechanisms close that, and together they are the guarantee:

1. **Filtered deal.** When `TileHand` deals and no tile currently in hand is placeable, draw from
   the placeable set instead of the full pool, with the weights renormalised inside that set so it
   is still a weighted draw rather than a fixed fallback. Invisible whenever the hand is healthy.
2. **Top up on placement.** If placing a tile would leave the hand with nothing placeable, deal a
   replacement from the placeable set immediately instead of waiting out `DealInterval`. Without
   this the guarantee still holds but the track can stall for 2.4 s, which at 55 m/s is 130 m of
   road the racers are already eating into.

**Discard** — letting the Track Master throw a tile away on a cooldown — is worth having, but as
agency rather than as safety. It handles "my hand is full of tiles I do not want", which is a
different complaint from "my hand is full of tiles I cannot play". Keep it a separate decision.

### Where it is enforced

Server-side, and for free. `TrackController` is authoritative and already routes through
`Grid.CanPlace`; `TrackMasterController` calls the same method for both the click and the ghost.
Putting the lookahead inside `CanPlace` covers all three call sites at once.

## Migration

Six stages. Stage 0 fixes the live bug on the existing grid, stages 1 and 2 are
behaviour-preserving and compile on their own, and that is what makes the risky one bisectable.

**0. The invariant, on the grid as it stands.** Split `CanPlace` into `Fits` / `Escapes` /
`CanPlace`, add the filtered deal and the top-up. Still cells, still `Vector2I`, no transforms
involved. This lands a real fix on its own, and by the time the overlap test changes underneath it
in stage 3 the mechanism is already trusted — only its inner predicate moves.

**1. Anchors alongside cells.** Add `TrackAnchor` to `PlacedTile`, derived from the existing
`(Cell, EntryDirection, EntryHeight)`. Add `HeadAnchor` to `TrackGrid`, maintained in parallel with
`HeadCell`. Change `TrackTile.Initialize` to take an anchor instead of the cell triple. The grid
still drives everything. *Nothing moves.* If a tile lands a millimetre off, the transform maths is
wrong and this is where it shows up, with the old system still there to compare against.

**2. Anchors become the source of truth.** `Place()` advances `HeadAnchor` using the tile's own exit
transform rather than deriving it from cells. Cells are still maintained, still used by `CanPlace`.
Lengths and radii are floats now, but must still land on the grid, so the numbers do not change yet.

**3. Occupancy becomes geometry.** `Fits` swaps cell lookup for OBB overlap — and because stage 0
already isolated it, that is the only function that changes. Delete `CellsFor`, `ExitCellFor`,
`_byCell` and `Vector2I` from `TrackGrid`. **This is the stage that can be subtly wrong** — a false
negative is track that visually intersects itself. Test with the proving ground's prebuilt course
first: it is 28 tiles including two hairpins and a serpentine, so it exercises the tight cases.

**Height clearance lands here too, and it is not optional.** Letting the track cross over itself when
the vertical gap is large enough is what turns the anti-lock rule from a four-fold improvement into a
guarantee — a ramp becomes a tile that always fits. See "What zero would actually take".

Note that the invariant gets *easier* to satisfy here, not harder: geometric footprints are smaller
than the cell blocks they replace, so more tiles fit and `Escapes` goes up. Stage 0 tuned against
the pessimistic version, which is the safe direction to have tuned in.

**4. Set the numbers.** Turn radius to 63 m. Tile lengths in metres rather than cell counts, so
"40% shorter" means 40%. `TileCatalog.TileSize` becomes `RoadWidth` and stops being a grid quantum —
at which point the constant that has been doing two jobs since the beginning is doing one.

**5. Later, if wanted.** Non-90° turns. Radius solved directly from
`TurnMaxBank`'s own equation, `r = v^2 / (g * tan(theta))`, instead of picked from the legal
multiples — which is what that comment has wanted since it was written.

## Risks

- **Stage 3 correctness.** A dictionary lookup is never subtly wrong; a swept-volume test can be.
  Needs a tolerance, and needs the prebuilt course driven before and after.
- **Tighter tracks become legal.** Not a bug, but the Track Master gains options they did not have,
  and `CanPlace`'s rejection messages ("There isn't room to swing the hairpin round") need to still
  be true and useful.
- **`MinimumEscapes` is a feel setting, not a safety one.** Any value >= 1 is safe. Too high and the
  board starts refusing placements that look obviously fine, which reads as the game being broken
  rather than careful. Tune it by trying to build a spiral on purpose.
- **The invariant guarantees a move, not an interesting one.** It cannot rule out the track being
  funnelled into a corridor where the only legal answer is a short straight, over and over. That is
  a degenerate state rather than a locked one, and raising `MinimumEscapes` is the lever if it shows
  up in play.
- **The test area builds through `TrackGrid` on purpose** — "so the course is held to exactly the
  rules a Track Master's track is". It follows the migration for free, but it is also the best
  regression test at every stage.
- **`docs/vehicle-physics.md` and the tile doc comments** quote metre figures derived from
  `TileSize`. Stage 4 moves several of them.

## What it unlocks

Beyond the 63 m corner: tile lengths that are whatever number reads best rather than a multiple of
54; corners whose radius is solved from the bank equation instead of rounded to it; turn angles
other than 90°; track that can cross over itself; and a hazard like the squiggle sized against the
car rather than against a cell.
