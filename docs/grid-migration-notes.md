# Grid migration — running notes

Progress log for the plan in `docs/track-without-a-grid.md`. Newest stage at the bottom.

**Nothing here has been compiled.** Every check below is either algebraic or a simulation of the
same arithmetic in Python. The first build is still the first build.

---

## Stage 0 — the anti-lock rule, on the existing grid ✅

`CanPlace` split into three:

```csharp
bool Fits(cell, direction, height, data, out reason)   // overlap + height, no lookahead
int  Escapes(cell, direction, height)                  // how many catalog entries Fit here
bool CanPlace(cell, data, out reason)                  // Fits + the lookahead
```

`Fits` takes the head state explicitly because `Escapes` asks about a head that does not exist yet.
A reused `_pending` set books the candidate's cells in for the length of the lookahead, so a
follow-up that doubles back through the tile being judged is not counted as an escape.

Hand side: `TileCatalog.DrawIndex(rng, allow)` renormalises weights inside the placeable subset,
`TileHand.Deal` reaches for it only when the hand is already stuck, `TopUp` deals immediately on
`TrackHeadChanged` rather than waiting out 2.4 s. `AllAsData` caches the catalog as `TileData` so
`Escapes` is not allocating 26 Resources per check.

**Correction made during the work.** The plan claimed the rule makes dead ends unreachable by
induction. It does not — one ply guarantees N tiles *fit* at the next head, not that any of them
leaves a head with the same property. Enforced strictly the rule *causes* locks. It is now advisory:
it prunes only while some other tile clears the bar, and stands down when nothing does.

Measured, 120 random 50-tile tracks:

| | true dead ends |
|---|---|
| no rule | 91 / 120 |
| rule + fallback, `MinimumEscapes = 3` | 23 / 120 |

Prebuilt course still builds: 28 tiles, tightest point 21 escapes.

---

## Stage 1 — anchors alongside cells ✅

Behaviour-preserving. Nothing about the track moves; the anchors are derived from cells and the
chain is computed in parallel and checked against them.

**New** `scripts/tiles/TrackAnchor.cs` — `readonly record struct TrackAnchor(Vector3 Position,
float Yaw)`, position being the *middle of the entry face*. Two operations: `Advanced(distance,
rise)` and `Swept(radius, radians, side)`.

**`TileData.TurnRadius` / `TurnSweep`** are now the single definition of a corner's shape. Both the
geometry (`BuildHairpin`, `BuildWideTurn`) and the anchor chain read them, so they cannot drift.
`TrackTile.Shapes.cs`'s private `HairpinRadius` is gone.

**`PlacedTile`** gained `EntryAnchor`, `ExitAnchor`, and the statics `AnchorFor(cell, direction,
height)` and `ExitAnchorFor(entry, data)`.

**`TrackTile.Initialize`** takes a `TrackAnchor` in place of `(cell, entryDirection, entryHeight)`.
Positioning collapsed to one line for every tile in the catalog:

```csharp
Position = anchor.Position + anchor.Forward * (Length * 0.5f);
Rotation = new Vector3(0.0f, anchor.Yaw, 0.0f);
```

`TileCatalog.SpanCenterToWorld` is deleted — nothing calls it any more. Callers updated:
`TrackController`, `TrackMasterController` (the ghost), and both `PhysicsTestArea` files.

**`TrackGrid.VerifyChain`** runs on every `Place` and pushes an error if the chained anchor and the
cell-derived one disagree by more than 1 cm / 0.01 rad. Scaffolding; it goes when the cells do.

### Verification

The two models share no arithmetic — one folds sines and cosines, the other steps integer cells and
multiplies by a tile size — so agreement is evidence rather than a tautology.

| check | worst disagreement |
|---|---|
| prebuilt proving-ground course (28 tiles) | **0.000000000 m**, 0.000000000 rad |
| 200 random tracks × 40 tiles | **0.000000000 m**, 0.000000000 rad |
| tile centre: anchor vs old `SpanCenterToWorld`, every catalog tile × 4 headings × 3 cells | **0.000000000 m** |

Exact, not merely inside tolerance. In C# these are `float` rather than `double`, so expect
rounding on the order of 1e-4 at a thousand metres out — three orders under `ChainTolerance`.

The quarter-turn and half-turn cases were also checked algebraically before the code was written:
`Swept` at 90° reduces to `forward*r + right*s*r`, which is `ExitCellFor`'s diagonal step, and at
180° to `right*s*2r`, which is the hairpin's two cells across.

---

## Stage 2 — the anchor becomes the source of truth ✅

`TrackGrid.HeadAnchor` is now a stored property, advanced in `Place` by folding the placed tile's
own exit transform onto the seam it was placed at:

```csharp
HeadAnchor = tile.ExitAnchor;
```

That one line is what the migration is for — it reads the tile's length and radius in metres and
knows nothing about cells. `PlacedTile.EntryAnchor` became a stored `required` member rather than
something derived from `Cell`. `Reset` seeds the anchor, `RemoveLast` restores it from the removed
tile's entry anchor (which is exactly what that method's doc already said it does with the cell).

**`VerifyChain` inverted.** It used to ask whether the new chain agreed with the cells. It now asks
whether the cells still agree with the chain — which is the question worth asking once the anchor is
the one being believed, because `CanPlace` was at that point still reserving space from cells. If the
two parted company the grid would be holding room somewhere the track no longer goes.

No behaviour change. Both models still produced identical numbers, as stage 1 established.

---

## Stage 3 — occupancy becomes geometry ✅

**New** `scripts/tiles/TrackFootprint.cs` — a flat oriented box with a height band, and a
separating-axis test. Height first, because it is one comparison and the one that most often says no
on a track that has climbed.

**Height clearance landed here, not in stage 5.** `VerticalClearance = 18 m`. Cells could only say
taken or free, so the track could never pass over itself even three cubes up; a footprint carries
`MinY`/`MaxY`, so an over-under is legal for the first time.

**`PlacedTile.FootprintFor`** replaces `CellsFor`: one box for anything that runs through, a fan of
`ArcFacets = 3` per quarter turn for anything that turns, each widened by its sagitta so the road
never pokes out of its own footprint.

**`TrackGrid`** gained `_boxes`, `_pendingBoxes`, and a 128 m bucket broadphase. The broadphase is
not premature — the anti-lock check is quadratic in the catalog (`Escapes` tries 26, and
`CanPlace`'s fallback can try 26 more), so a linear scan would be hundreds of thousands of tests
every time the mouse moved.

`Fits`, `Escapes` and `CanPlace` all take anchors now; `CanPlace` dropped its cell parameter since
every caller passed the head. Callers updated in `TrackController` and `TrackMasterController`.

### The bug this stage was going to have

First implementation exempted **one** box at the joint, on the reasoning that a tile only butts
against its immediate predecessor. Wrong: a corner's first facet is *yawed* relative to the seam, so
its rear corners swing back past the joint, and against a hairpin's short 30-degree facets that
reaches **two** boxes deep.

Caught by comparing every ordered pair in the catalog against the cell model, which should never be
stricter than geometry:

| joint exemption | geometry stricter than cells | hairpin → hairpin |
|---|---|---|
| 1 box | **4 pairs** — hairpin into a same-side corner | caught |
| **2 boxes** | **0** | caught |
| 3 boxes | 0 | caught |

Two is both the measured floor and the ceiling, and it is safely short of hiding a real collision —
two hairpins in a row genuinely do loop through each other and are still caught, because the
intrusion lands on facets nowhere near the seam. A joint-radius exemption was also tried and is much
worse (it exempts 182–569 pairs).

### Verification

| check | result |
|---|---|
| every ordered pair in the catalog, geometry vs cells | 673 agree, 3 geometry-looser, **0 geometry-stricter** |
| prebuilt proving-ground course | all 28 tiles place |
| 2782 heads over 80 random tracks, mean tiles that fit | cells 13.9 → geometry **25.1 (+81%)** |

The +81% is the point: a hairpin was reserving a three-by-two block of board for a road that only
sweeps an annulus through it.

**One known divergence, and it is expected.** Over random tracks, geometry refuses something cells
allowed on about 1.7% of heads. These are all downstream of a crossing: once geometry lets the track
pass over itself, the cell model's `_byCell` has had an entry overwritten and is no longer a
truthful record of anything. The cells are wrong there, not the geometry — and it is a concrete
reason they cannot survive stage 4.

---

## Stage 4 — the numbers come off the grid ✅

The payoff stage. Cells are gone and the corner is 63 m.

### Data model

`TileData` and `TileDefinition` now carry real dimensions:

| was | is |
|---|---|
| `CellLength` (int cells) | `RunLength` (float metres) |
| radius derived as `(TurnSpan - 0.5) x TileSize` | `TurnRadius` (float metres), stored |
| `IsWideTurn` (`\|ExitTurn\| == 1 && CellLength > 1`) | `IsTurn` (`ExitTurn != 0`) |
| `TurnSpan` | gone |

Wire format changed with it: `cell_length` → `run_length` + `turn_radius`, both floats. **Old
clients will not match.**

### The catalog constants

```csharp
LongRun       = 162.0f   // seven tiles whose feature needs the distance
ShortRun      = 108.0f   // everything else
CornerRadius  =  63.0f   // was 81 — the only legal value the cells offered
SweeperRadius =  99.0f   // was 135
HairpinRadius =  54.0f   // unchanged; the one radius the grid was not distorting
```

**63 m is not a compromise, it is the number the bank was written for.** `TurnMaxBank` banks the
outer lip at 60° on the argument that a bank holds a car unaided at `v = sqrt(g·r·tan θ)` and that
the top of it should be neutral at `TopSpeed`. At the grid's 81 m that landed at 218 km/h — the
corner was over-radiused for its own bank. At 63 m it lands at **199 km/h against a 200 km/h top
speed**. Tightening the corner delivered the design rather than compromising it.

| | radius | arc | at 50 m/s | grip needed | bank neutral |
|---|---|---|---|---|---|
| Curve | 63 m | 99 m | **2.0 s** (was 2.5) | 5.0 g of 8.5 | **199 km/h** |
| Sweeper | 99 m | 155 m | 3.1 s (was 4.2) | 3.2 g | 235 km/h |
| Hairpin | 54 m | 170 m | 3.4 s | 5.8 g | 189 km/h |

### What was deleted

`_byCell`, `HeadCell`, `HeadDirection`, `CellsFor`, `ExitCellFor`, `TileAt`, `IndexAt`,
`TileAtWorld`, `VerifyChain`, `PlacedTile.Cell` / `EntryDirection` / `EntryHeight` / `ExitCell` /
`ExitDirection` / `ExitHeight`. `TrackGrid.Reset` and `BuildStartingStraight` take a `TrackAnchor`;
`HeadHeight` is now `HeadAnchor.Position.Y` in metres.

`TileCatalog.TileSize` survives as **just the road width**, and its doc says so. The proving ground
still lays specimens out on a lattice of that size — the one place a grid is the right tool, because
it is a display case rather than a track.

### Two consumers that got better, not worse

**`RacerController.CurrentTrackIndex`** was a declared-but-never-written property; the hazard warning
used `TileAtWorld`, a cell lookup. It now reads the wheels: the car already collides with exactly one
`TrackTile`, so the grounded wheel knows the answer exactly — including on a hairpin doubling back or
a bridge over another part of the track, which a flat cell lookup was always going to be vague about.

**`FinishLine.PlaceAt`** took a `TrackDirection`; it takes a yaw. `TrackDirection` now survives only
where a person still thinks in compass headings — the start line and the proving ground's layout.

### Verification

| check | result |
|---|---|
| prebuilt course with the new radii | **all 28 tiles place** |
| course length | 3722 m → **3467 m**, 74 s → **69 s** at 50 m/s |
| escapes at the finished head | 24 / 26 |

### Left for you

- Nothing is compiled. `TrackAnchor.cs` and `TrackFootprint.cs` are new files and Godot will want to
  generate `.cs.uid` for them on first import.
- `TrackController.StartPosition` replaces `StartCell` and is a `Vector3` in the inspector — the
  default reproduces the old start line's world position, but any `.tscn` that set `StartCell`
  needs re-pointing.
- `MinimumEscapes` and `JointBoxes` were tuned against the cell model. Both should hold, but the
  first thing worth watching is whether the board ever refuses a placement that looks obviously fine.
