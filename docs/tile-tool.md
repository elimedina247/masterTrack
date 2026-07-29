# Authoring tiles instead of generating them

A tool for shaping track in the Godot viewport, replacing the code that built every tile from a
hazard enum.

## Why

Three complaints, one cause.

**The tiles could not be seen.** `TrackTile.BuildGeometry` dispatched on `Data.Hazard` across some
2,200 lines in four partials. Nothing existed in the editor to look at, so "what does a bottleneck
actually look like" was a question you answered by launching the game and driving to it.

**The centre lines did not line up.** There were five different ideas of where the middle of the
road was. A straight struck a stripe at `x=0`. A banked corner painted no centre line at all — it
painted the flat/bank seam at 35% of the road width in from the inside instead. The loop's aprons
jumped theirs ten metres sideways with no taper, the split forked to ±22.5 m abruptly, and the
squiggle abandoned it and painted its edges. So the line pulsed on and off as you drove.

**The hazards were boring**, and measurably so — see the bottom of this file.

The cause underneath all three is that geometry was the *output* of a hazard enum rather than
something anyone could shape. You cannot look at an enum, an enum has no centre line, and making a
new one is a case in a switch plus a catalog entry plus a rebuild.

## The model

Taken from [godot-road-generator](https://github.com/TheDuckCow/godot-road-generator), stripped to
what a chain of tiles needs. Their `RoadPoint`/`RoadSegment`/`RoadContainer` becomes a spine, a
sweep, and a piece — and their "save a container as a reusable scene" is already what a tile is
here.

```
TrackPiece  (StaticBody3D, [Tool])
├── Spine   (Path3D)      <- you drag this
└── ...your props         <- blocks, pads, pistons
     [generated at load: Surface, Collision]
```

A `TrackProfile` resource is the cross-section — width, bank, thickness, walls, paint — and it is
swept along the spine. **One sweep replaced four hand-written builders**: the banked arc, the eased
ramp, the squiggle's ribbon and the plain floor were the same operation with different curves.

### The spine is the truth

Where a piece hands the track on, how far it runs, how much it climbs and where its centre line
goes are all read off the same curve. They cannot disagree, which is what the five-conventions
centre line problem was.

Exit anchors come from the **Bezier handles**, not from sampling the curve. A chord measured back
over the last half metre of a 63 m corner points 0.227° away from the true tangent — half the angle
that half metre sweeps — and the chain *accumulates* that, because every piece is laid relative to
the one before. Handles are exact.

### The one rule

`TrackAnchor` is a position and a yaw. Four floats, no pitch and no roll — which is deliberate, and
is what keeps the chain clear of basis renormalisation and the drift that comes with it. So **both
ends of a spine must be level and un-banked.**

`TrackPiece._GetConfigurationWarnings` enforces exactly that, and nothing about taste:

- the spine starts at the origin;
- it leaves heading down local −Z;
- neither end is tilted (a roll the anchor cannot carry);
- neither end is pitched (a kerb at the joint).

The bank is eased to zero over `BankBlend` at each end for the same reason. At full height from the
first ring, a default corner would meet the straight beside it as a **16 m step**.

## What it costs

Measured on the proving ground, per quarter turn:

| | old | new |
|---|---|---|
| `MeshInstance3D` | 144 | 1 |
| `BoxMesh` / `ArrayMesh` | 144 | 1 |
| `CollisionShape3D` | 132 | 1 |
| shape resources | 132 | 1 |
| **total objects** | **~552** | **4** |

Collision is one watertight `ConcavePolygonShape3D` built from the same triangles the mesh draws,
so it cannot drift from what you can see. What keeps a car out of it is the physics step being too
short to cross the slab: at the project's 120 Hz a car at `TopSpeed` moves 0.46 m against a 1.6 m
`Thickness`. **Thinning that is a physics decision, not a visual one.**

Geometry is never saved. A `.tscn` holds the curve, the profile reference and your props — a few
hundred bytes — and the road is rebuilt on load in the editor and the game alike. So there is no
baked mesh to go stale, and a change to a shared profile reshapes every piece using it.

## Authoring one

1. Duplicate a scene in `scenes/tiles/pieces/`.
2. Drag the `Spine`'s points in the viewport. Bank a corner with per-point **tilt**.
3. Warnings appear on the node the moment a seam stops being legal.
4. Launch `scenes/TestArea.tscn` — everything in that folder is chained end to end off the **west**
   edge of the pad, so you can drive the joints. North and south are the buildable track and the
   fixed course.

The console prints each piece's run length, exit anchor, rise, vertex count and extent.

## Where this is up to

Done: the sweep, the profile, the piece, seam validation, and four pieces covering the three
categories — `Straight`, `CurveLeft`/`CurveRight` (mirroring), `RampUp` (elevation). Verified
against the chain arithmetic: `CurveRight` reports `(63, 0, -63)` at `-90°`, which is exactly what
`TrackAnchor.Swept(63, π/2, 1)` folds.

**Not yet wired into `TileCatalog`.** The pieces are laid out on the proving ground on purpose, so
the seams can be driven before anything depends on them. Hooking them up means:

- `TileData.ScenePath` is declared and replicated but read by nothing — the hook is already stubbed;
- the catalog becomes a list resource so the wire format stays an index into a stable order;
- `TrackGrid`'s footprint comes from the swept spine rather than from `RunLength`/`TurnRadius`.

Every peer must have byte-identical piece files, since replication only sends a catalog id. Fine
for pieces that ship; "players author tiles" is a much harder feature and is not this.

## The boring hazards

The measurement, for whenever the design work starts. Feature length against tile length:

| Tile | Feature | Tile | Feature % |
|---|---|---|---|
| Gap | 14 m | 108 m | **13%** |
| Bottleneck | 19 m | 108 m | **18%** |
| Spinner / Crusher / Log | ~45 m | 108 m | ~42% |
| Ice / Gravel / Split | 78–121 m | 108–162 m | 72–75% |

Two different failures. The top group is **dead air** — a 0.2-second event wrapped in 94 m of held
throttle. The bottom group fills its tile but offers **no decision**: ice and gravel are a
full-width surface swap, so the tile asks you to hold the wheel straight for two seconds.

Both are now cheap to fix. Every point-hazard is still 108 m because that was two cells before the
grid came out; `RunLength` has been a free per-tile number in metres ever since, and on an authored
piece it is the length of a curve you drag.
