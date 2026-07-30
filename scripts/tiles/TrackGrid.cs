using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles;

/// <summary>A tile that has been committed to the track.</summary>
public sealed class PlacedTile
{
    public required TileData Data { get; init; }

    /// <summary>Position along the track from the start line. Drives the "3 tiles ahead" warning.</summary>
    public required int Index { get; init; }

    /// <summary>The space this tile takes up. See <see cref="FootprintFor"/>.</summary>
    public IEnumerable<TrackFootprint> Footprint => FootprintFor(EntryFrame, Data);

    /// <summary>
    /// The seam the track had reached when this tile was placed onto it, as a <b>full frame</b>:
    /// position, heading, pitch and roll. This is the value the track is actually built on — a
    /// banked piece hands the next tile its bank through here, which the position-and-yaw
    /// <see cref="EntryAnchor"/> cannot carry.
    /// </summary>
    public required Transform3D EntryFrame { get; init; }

    /// <summary>
    /// Where the racer crosses into this tile, flattened to a position and a yaw — the level
    /// approximation of <see cref="EntryFrame"/>, for the things that lay tiles out by hand and
    /// the readouts that think in headings.
    /// </summary>
    public required TrackAnchor EntryAnchor { get; init; }

    /// <summary>Where they hand the track on — by definition the next tile's <see cref="EntryFrame"/>.</summary>
    public Transform3D ExitFrame => ExitFrameFor(EntryFrame, Data);

    /// <summary>The exit, flattened the same way <see cref="EntryAnchor"/> is.</summary>
    public TrackAnchor ExitAnchor => AnchorOf(ExitFrame);

    /// <summary>A full frame from a flat anchor: the anchor's position and yaw, level by
    /// construction.</summary>
    public static Transform3D FrameOf(TrackAnchor anchor)
        => new(new Basis(Vector3.Up, anchor.Yaw), anchor.Position);

    /// <summary>A flat anchor from a frame: its position, and the heading its forward flattens
    /// to. Pitch and roll do not survive — that is the definition of an anchor.</summary>
    public static TrackAnchor AnchorOf(Transform3D frame)
    {
        Vector3 forward = -frame.Basis.Z;
        return new TrackAnchor(frame.Origin, Mathf.Atan2(-forward.X, -forward.Z));
    }

    /// <summary>Whether a frame is level enough for a tile built flat to sit on it: up is
    /// vertical to within about a degree and a half.</summary>
    public static bool IsLevel(Transform3D frame) => frame.Basis.Y.Y > 0.9997f;

    /// <summary>
    /// An anchor built from a world position and a compass heading, positioned so the <i>middle</i>
    /// of the first tile lands on <paramref name="at"/>.
    ///
    /// The track itself no longer thinks in headings — this is for the things that lay tiles out by
    /// hand rather than by building a track: the proving ground's specimen grid, and the start of a
    /// course. See <c>docs/track-without-a-grid.md</c>.
    /// </summary>
    public static TrackAnchor AnchorFor(Vector3 at, TrackDirection facing, float backOff)
        => new(at - facing.Forward() * backOff, facing.Yaw());

    /// <summary>
    /// Facets an arc's footprint is chopped into per quarter turn. A quarter turn becomes three
    /// boxes and a hairpin six, which is enough that the boxes hug the arc without the overlap test
    /// having much to do.
    ///
    /// Boxes are rectangles and an arc is not, so each one is widened by the sagitta of the piece of
    /// arc it stands in for — it over-reserves by about three metres at the default corner rather
    /// than leaving a sliver of the road unclaimed. Over-reserving costs a placement the Track
    /// Master might have got away with; under-reserving is track intersecting track.
    /// </summary>
    private const int ArcFacets = 3;

    /// <summary>
    /// The space a tile takes up: one box for anything that runs through, a fan of them round an
    /// arc for anything that turns.
    ///
    /// This is what replaced the cell footprints, and it is a strictly tighter answer. A hairpin
    /// reserved a three-by-two block of cells — six whole tiles of board — for a road that only ever
    /// sweeps an annulus through it. Measuring the road itself lets the Track Master build the tight
    /// serpentines the cells were refusing on a technicality.
    /// </summary>
    public static IEnumerable<TrackFootprint> FootprintFor(Transform3D frame, TileData data)
    {
        float width = TrackTile.Size;

        // An authored piece's room is whatever its spine sweeps — read from the piece itself, so
        // a corkscrew reserves its coil and not just the chord between its seams. The full frame
        // matters here: a piece placed on a banked seam sweeps a rolled coil, and its boxes
        // follow the route through the actual frame.
        if (data.ScenePath.Length > 0 && Tool.PieceCatalog.AtPath(data.ScenePath) is { } piece)
        {
            foreach (TrackFootprint box in PieceFootprint(frame, piece, width))
                yield return box;

            yield break;
        }

        // Generated tiles are flat by construction and only ever placed on level frames, so the
        // anchor is the whole of what their arithmetic needs.
        TrackAnchor entry = AnchorOf(frame);

        if (!data.IsTurn)
        {
            float length = data.RunLength;
            float rise = data.HeightChange * TileCatalog.HeightStep;
            Vector3 middle = entry.Position + entry.Forward * (length * 0.5f);

            yield return new TrackFootprint(
                new Vector2(middle.X, middle.Z), width * 0.5f, length * 0.5f, entry.Yaw,
                Mathf.Min(entry.Position.Y, entry.Position.Y + rise),
                Mathf.Max(entry.Position.Y, entry.Position.Y + rise));

            yield break;
        }

        int facets = Mathf.Max(1, Mathf.RoundToInt(ArcFacets * data.TurnSweep / (Mathf.Pi * 0.5f)));
        float step = data.TurnSweep / facets;

        // Half the chord a facet spans, and how far the arc bulges past that chord. The box is
        // grown by the bulge so the road never pokes out of its own footprint.
        float halfChord = data.TurnRadius * Mathf.Sin(step * 0.5f);
        float bulge = data.TurnRadius * (1.0f - Mathf.Cos(step * 0.5f));

        TrackAnchor at = entry;
        for (int i = 0; i < facets; i++)
        {
            TrackAnchor next = at.Swept(data.TurnRadius, step, data.TurnSide);
            Vector3 middle = (at.Position + next.Position) * 0.5f;

            yield return new TrackFootprint(
                new Vector2(middle.X, middle.Z), width * 0.5f + bulge, halfChord,
                at.Yaw - data.TurnSide * step * 0.5f,
                middle.Y, middle.Y);

            at = next;
        }
    }

    /// <summary>
    /// Where a tile hands the track on, worked out from its entry anchor and its own shape rather
    /// than by counting cells.
    ///
    /// This is the whole of what replaced the cell arithmetic. Two lines against its two
    /// hand-written special cases, and nothing in here cares how big a cell is — the numbers it
    /// reads are metres and radians. An authored piece answers from its own seams: the exit
    /// expressed in the entry's frame, folded onto the anchor. The catalog only deals pieces whose
    /// exits are level (<see cref="Tool.PieceEntry.IsAnchorChainable"/>), so flattening the result
    /// back to a position and a yaw loses nothing.
    /// </summary>
    public static TrackAnchor ExitAnchorFor(TrackAnchor entry, TileData data)
        => AnchorOf(ExitFrameFor(FrameOf(entry), data));

    /// <summary>
    /// Where a tile hands the track on, as a full frame. An authored piece folds its whole
    /// exit-in-entry transform onto the frame it was placed at — bank, pitch, everything — which
    /// is what lets a banked corner hand its bank to the corner after it. A generated tile is
    /// flat, so it advances the flat anchor and lifts the result back into a level frame.
    /// </summary>
    public static Transform3D ExitFrameFor(Transform3D frame, TileData data)
    {
        if (data.ScenePath.Length > 0 && Tool.PieceCatalog.AtPath(data.ScenePath) is { } piece)
            return (frame * piece.ExitInEntry).Orthonormalized();

        TrackAnchor entry = AnchorOf(frame);

        return FrameOf(data.IsTurn
            ? entry.Swept(data.TurnRadius, data.TurnSweep, data.TurnSide)
            : entry.Advanced(data.RunLength, data.HeightChange * TileCatalog.HeightStep));
    }

    /// <summary>
    /// Boxes along an authored piece's route, from a world frame — the same fan-of-boxes
    /// treatment an arc gets, but following whatever course the spine takes, elevation included.
    /// Public because the editor's assembly tool asks the same "how much room does this piece
    /// take" question the grid does, and two answers would drift.
    /// </summary>
    public static IEnumerable<TrackFootprint> PieceFootprint(
        Transform3D frame, Tool.PieceEntry piece, float width)
    {
        // The margin an arc gets from its sagitta, a piece gets as a constant: the route is
        // sampled finely enough that each box's chord hugs the road, and three metres covers the
        // bulge of anything the catalog turns at.
        const float bulge = 3.0f;

        // How much road one box stands in for. Short enough to hug a 63 m corner, long enough
        // that a corkscrew is dozens of boxes rather than thousands.
        const float stride = 30.0f;

        TrackAnchor entry = AnchorOf(frame);

        IReadOnlyList<Vector3> route = piece.Route;
        if (route.Count < 2)
        {
            // A degenerate route still occupies its own seam.
            yield return new TrackFootprint(
                new Vector2(entry.Position.X, entry.Position.Z), width * 0.5f, width * 0.5f,
                entry.Yaw, entry.Position.Y, entry.Position.Y);
            yield break;
        }

        Vector3 from = frame * route[0];
        float covered = 0.0f;
        float lastYaw = entry.Yaw;

        for (var i = 1; i < route.Count; i++)
        {
            Vector3 to = frame * route[i];
            covered += (to - from).Length();

            if (covered < stride && i < route.Count - 1)
                continue;

            Vector3 run = to - from;
            var flat = new Vector2(run.X, run.Z);

            // A stretch that climbs near vertically has no heading of its own; the previous box's
            // serves, and the widened box still covers the road.
            float yaw = flat.LengthSquared() > 1.0f ? Mathf.Atan2(-run.X, -run.Z) : lastYaw;
            lastYaw = yaw;

            Vector3 middle = (from + to) * 0.5f;

            yield return new TrackFootprint(
                new Vector2(middle.X, middle.Z), width * 0.5f + bulge,
                Mathf.Max(width * 0.25f, run.Length() * 0.5f), yaw,
                Mathf.Min(from.Y, to.Y), Mathf.Max(from.Y, to.Y));

            from = to;
            covered = 0.0f;
        }
    }
}

/// <summary>
/// The track as a model: which cells are occupied, in what order, and where the next tile is
/// allowed to go. Pure data with no scene nodes, so the server can own the authoritative copy
/// and every client can keep an identical one from replicated placements.
///
/// The track is a single connected path. Tiles are only ever added at the head — the open
/// cell the leading racer is driving toward — which is what makes "place tiles ahead of the
/// racers" the whole game rather than free-form building.
///
/// Where a tile is, and how much room it takes up, are both answered in metres — see
/// <see cref="PlacedTile.FootprintFor"/>. Nothing here quantises to anything, which is what lets a
/// corner be 63 m of radius rather than the nearest legal multiple of a cell.
///
/// Elevation is part of that answer rather than beside it: a footprint carries the band of height it
/// sweeps, so the track is allowed to cross over itself given enough clearance. Under the old cell
/// model it could climb over its own neighbourhood but never over itself, because a cell could only
/// be taken or free.
///
/// The back of the track crumbles away as the front grows — see <see cref="RetireThrough"/>. A
/// retired tile stops being road, but it is never taken out of <see cref="_ordered"/>: a tile's
/// index is its position in that list, and shuffling it up would renumber every tile still
/// standing and every node in the scene named after one.
/// </summary>
public sealed class TrackGrid
{
    private readonly List<PlacedTile> _ordered = new();

    /// <summary>
    /// Every box of road on the track, in the order it was laid. What "is there room" is now asked
    /// of, in place of the old dictionary of occupied cells.
    ///
    /// A list rather than a set, because the order carries meaning: the last entry is the box the
    /// next tile butts against, and a joint is not a collision. See <see cref="LiveBoxes"/>.
    /// </summary>
    private readonly List<TrackFootprint> _boxes = new();

    /// <summary>Index into <see cref="_boxes"/> of the oldest box still standing.</summary>
    private int _retiredBoxes;

    /// <summary>
    /// Boxes belonging to the placement currently being judged. The tile is not committed, but a
    /// follow-up that doubled back through it is not really an escape, so the lookahead has to see
    /// it. Cleared by whoever fills it.
    /// </summary>
    private readonly List<TrackFootprint> _pendingBoxes = new();

    /// <summary>
    /// Side of a broadphase bucket, in metres. Comfortably larger than the longest tile so a box
    /// never spans more than a couple of buckets in each axis.
    ///
    /// This exists because the anti-lock check is quadratic in the catalog — <see cref="Escapes"/>
    /// tries 26 tiles, and the fallback in <see cref="CanPlace"/> can try 26 more — so a linear scan
    /// of a long track's boxes would be hundreds of thousands of tests every time the Track Master
    /// moved the mouse. Bucketing turns the inner loop into "the handful of boxes actually near
    /// here".
    /// </summary>
    private const float BucketSize = 128.0f;

    /// <summary>Slack on the "not below ground" test, so a track sitting exactly on the ground plane
    /// is not refused by a float that landed a millionth under it.</summary>
    private const float GroundEpsilon = 0.01f;

    private readonly Dictionary<Vector2I, List<int>> _buckets = new();

    /// <summary>
    /// How many tiles have crumbled off the back. They stay in <see cref="_ordered"/> to keep the
    /// indices stable, so this is also the index of the oldest tile still standing.
    /// </summary>
    private int _retired;

    /// <summary>
    /// The seam the next tile attaches to, and <b>the authoritative answer to where the track ends</b>.
    ///
    /// Carried rather than derived: it is advanced by folding each placed tile's own exit transform
    /// onto it, so nothing about where the track goes is a multiple of a cell any more. The cells
    /// are still kept, and still answer "is there room" — but they no longer decide where anything
    /// is. See <c>docs/track-without-a-grid.md</c>.
    /// </summary>
    public TrackAnchor HeadAnchor { get; private set; }

    /// <summary>
    /// The head as a full frame — what placements actually fold onto. When the last piece exited
    /// level this is <see cref="HeadAnchor"/> lifted back into a transform and nothing more; when
    /// it exited banked, this carries the bank and the anchor is the flat approximation for
    /// readouts. Generated tiles refuse to sit on a non-level frame (<see cref="Fits"/>), so the
    /// two never quietly disagree about where a flat tile goes.
    /// </summary>
    public Transform3D HeadFrame { get; private set; } = Transform3D.Identity;

    /// <summary>
    /// Height the next tile starts at, in metres. Runs up and down as ramps are placed, and is never
    /// allowed below zero — the ground plane is the floor of the world, and a track that dug
    /// underneath it would leave the racers driving through the dark.
    /// </summary>
    public float HeadHeight => HeadAnchor.Position.Y;

    /// <summary>
    /// Tiles in track order, from the start line onward — including the ones that have already
    /// crumbled away, so an index into this list means the same thing for the whole race.
    /// </summary>
    public IReadOnlyList<PlacedTile> Tiles => _ordered;

    /// <summary>How many tiles have ever been laid, crumbled ones included.</summary>
    public int Count => _ordered.Count;

    /// <summary>
    /// Index of the oldest tile still standing. Everything before this has crumbled away and is
    /// no longer road, however solid it looks in <see cref="Tiles"/>.
    /// </summary>
    public int OldestLiveIndex => _retired;

    /// <summary>Clear the track and start a new one at the given cell and heading.</summary>
    public void Reset(TrackAnchor start)
    {
        _ordered.Clear();
        _boxes.Clear();
        _buckets.Clear();
        _pendingBoxes.Clear();
        _retired = 0;
        _retiredBoxes = 0;
        HeadAnchor = start;
        HeadFrame = PlacedTile.FrameOf(start);
    }

    /// <summary>
    /// Crumble the back of the track away up to and including <paramref name="index"/>. The tiles
    /// stop occupying their cells, so a car standing on one is standing on nothing.
    ///
    /// Takes an index rather than simply retiring the oldest, so it does not matter what order the
    /// callers arrive in: retiring through 4 twice is retiring through 4, and a tile that somehow
    /// expires out of turn takes the ones in front of it with it rather than leaving the count
    /// pointing at the wrong tile.
    ///
    /// The head is never touched. The track only ever grows from the front, and a tail that could
    /// push the head backwards would be rewriting road the racers are already driving on.
    /// </summary>
    public void RetireThrough(int index)
    {
        while (_retired <= index && _retired < _ordered.Count)
        {
            // Boxes are laid in the same order as tiles, so retiring a tile is advancing the
            // watermark past however many boxes it contributed. They stay in the list to keep the
            // bucket indices stable, exactly as retired tiles stay in _ordered.
            foreach (TrackFootprint _ in _ordered[_retired].Footprint)
                _retiredBoxes++;

            _retired++;
        }
    }

    /// <summary>
    /// How many catalog tiles should still be placeable at the head a placement would leave behind.
    ///
    /// This steers the Track Master away from building into a corner they cannot get out of, which
    /// matters because a dead end is the one failure nothing recovers from. A hand full of tiles
    /// that happen not to fit is fixed by the next deal (see <c>TileHand.TopUp</c>); a head with
    /// nowhere to go is fixed by nothing — <c>TrackController.AllowUndo</c> is off in a match on
    /// purpose — and the racers drive off the end of the track.
    ///
    /// <b>It is a heuristic, not a proof, and the difference is worth being precise about.</b> One
    /// step of lookahead can guarantee that this many tiles <i>fit</i> at the next head. It cannot
    /// guarantee that any of them leaves a head with the same property — safety in this game is
    /// "an infinite sequence of placements exists", which is a greatest fixpoint and not decidable
    /// at any fixed depth. Simulated against 120 random 50-tile tracks, the check takes true dead
    /// ends from 91 to 24. It does not take them to zero, and no amount of raising it will.
    ///
    /// Zero needs a tile that fits <i>everywhere</i>, and on a 2-D grid there is no such thing — a
    /// fully enclosed head has room for nothing. It needs the track to be allowed to climb over
    /// itself with vertical clearance, which this grid cannot express and the transform model can.
    /// See <c>docs/track-without-a-grid.md</c>.
    ///
    /// Higher steers away from pockets earlier at the cost of pruning more; the measured difference
    /// between 1, 3 and 6 is small, and 3 prunes about 3% of otherwise-legal placements. It is a
    /// feel setting — tune it by trying to build a spiral on purpose.
    /// </summary>
    public const int MinimumEscapes = 3;

    /// <summary>
    /// Whether a tile would physically go here: nothing already on the track is in the way, and it
    /// does not dig underground.
    ///
    /// Deliberately does <b>not</b> ask whether anything could follow it. That is
    /// <see cref="CanPlace"/>'s job and it calls this to answer it, so keeping the two apart is
    /// what stops the lookahead recursing.
    ///
    /// Takes the frame rather than reading the head off the grid, because <see cref="Escapes"/>
    /// asks this question about a head that does not exist yet.
    /// </summary>
    public bool Fits(Transform3D frame, TileData data, out string reason)
    {
        // A generated tile is built flat, so a banked or climbing seam is a frame it cannot sit
        // on — the joint would be a step at exactly the place a car crosses it. Authored pieces
        // are exempt: they are placed by the whole frame and inherit its bank.
        if (data.ScenePath.Length == 0 && !PlacedTile.IsLevel(frame))
        {
            reason = "The track is banked here — only an authored piece can follow until it "
                     + "levels out.";
            return false;
        }

        if (PlacedTile.ExitFrameFor(frame, data).Origin.Y < -GroundEpsilon)
        {
            reason = "The track is already on the ground — it can't go down from here.";
            return false;
        }

        foreach (TrackFootprint box in PlacedTile.FootprintFor(frame, data))
        {
            if (!Blocked(box))
                continue;

            reason = data.IsHairpin ? "There isn't room to swing the hairpin round."
                   : data.IsTurn ? "There isn't room to sweep the corner round."
                   : "There isn't room for a tile that long.";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// How many boxes at the end of the track a candidate is allowed to sit on top of.
    ///
    /// A joint is not a collision, and it costs more than one box to say so. A corner's first facet
    /// is yawed relative to the seam it leaves, so its rear corners genuinely swing back past the
    /// joint — measured against every pair in the catalog, the deepest reach is a hairpin's, whose
    /// 30-degree facets are short enough that the next tile's first box overlaps <i>two</i> of them.
    ///
    /// <b>Two is the measured floor, and it is also the ceiling.</b> At one, geometry refuses four
    /// pairs the cells allowed — hairpin into a same-side corner, which is legal and looks it. At
    /// three it catches nothing extra. And it is safely short of hiding a real collision: two
    /// hairpins in a row genuinely do loop back through each other, and are still caught here,
    /// because the intrusion reaches facets nowhere near the seam.
    /// </summary>
    private const int JointBoxes = 2;

    /// <summary>
    /// Whether a box of road runs into track that is already there.
    ///
    /// The last <see cref="JointBoxes"/> boxes of the track are exempt — counting the pending
    /// placement as part of the track, so the exemption lands in the same place whether this is a
    /// real placement being judged or a hypothetical one inside <see cref="Escapes"/>.
    /// </summary>
    private bool Blocked(TrackFootprint box)
    {
        int exemptLive = Mathf.Max(0, JointBoxes - _pendingBoxes.Count);
        int liveLimit = _boxes.Count - exemptLive;

        foreach (int i in Nearby(box))
        {
            if (i < liveLimit && box.Overlaps(_boxes[i]))
                return true;
        }

        for (int i = 0; i < _pendingBoxes.Count - JointBoxes; i++)
        {
            if (box.Overlaps(_pendingBoxes[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Indices of the live boxes whose bucket a box reaches into. A superset of what it can touch,
    /// which is all a broadphase owes the test that follows it.
    /// </summary>
    private IEnumerable<int> Nearby(TrackFootprint box)
    {
        float reach = box.BoundingRadius;
        var low = new Vector2I(Mathf.FloorToInt((box.Center.X - reach) / BucketSize),
                               Mathf.FloorToInt((box.Center.Y - reach) / BucketSize));
        var high = new Vector2I(Mathf.FloorToInt((box.Center.X + reach) / BucketSize),
                                Mathf.FloorToInt((box.Center.Y + reach) / BucketSize));

        for (int x = low.X; x <= high.X; x++)
        {
            for (int y = low.Y; y <= high.Y; y++)
            {
                if (!_buckets.TryGetValue(new Vector2I(x, y), out List<int>? bucket))
                    continue;

                foreach (int i in bucket)
                {
                    if (i >= _retiredBoxes)
                        yield return i;
                }
            }
        }
    }

    /// <summary>Throw the broadphase away and file every box again. See <see cref="RemoveLast"/>.</summary>
    private void RebuildBuckets()
    {
        _buckets.Clear();
        for (int i = 0; i < _boxes.Count; i++)
            Bucket(_boxes[i], i);
    }

    /// <summary>File a box into every bucket it reaches into, so <see cref="Nearby"/> can find it.</summary>
    private void Bucket(TrackFootprint box, int index)
    {
        float reach = box.BoundingRadius;
        int lowX = Mathf.FloorToInt((box.Center.X - reach) / BucketSize);
        int highX = Mathf.FloorToInt((box.Center.X + reach) / BucketSize);
        int lowY = Mathf.FloorToInt((box.Center.Y - reach) / BucketSize);
        int highY = Mathf.FloorToInt((box.Center.Y + reach) / BucketSize);

        for (int x = lowX; x <= highX; x++)
        {
            for (int y = lowY; y <= highY; y++)
            {
                var key = new Vector2I(x, y);
                if (!_buckets.TryGetValue(key, out List<int>? bucket))
                    _buckets[key] = bucket = new List<int>();

                bucket.Add(index);
            }
        }
    }

    /// <summary>
    /// How many catalog tiles would fit at a head. What <see cref="MinimumEscapes"/> is read
    /// against, and the measurement that makes a dead end rare rather than routine.
    /// </summary>
    public int Escapes(Transform3D frame)
    {
        int count = 0;
        foreach (TileData data in TileCatalog.AllAsData)
        {
            if (Fits(frame, data, out _))
                count++;
        }

        return count;
    }

    /// <summary>Catalog tiles that would fit at the head as it stands right now.</summary>
    public int EscapesAtHead => Escapes(HeadFrame);

    /// <summary>
    /// Whether a tile could legally go on the end of the track: it has to fit there, and — the part
    /// that is not obvious — the head it would leave behind should still have somewhere to go. See
    /// <see cref="MinimumEscapes"/>.
    ///
    /// That last check generalises one the grid used to have. "Would run the track back into itself"
    /// looked at the single cell past the exit and rejected the placement if it was taken, which is
    /// this same idea at a depth of one cell. It was never enough, because tiles are longer than one
    /// cell: a free cell ahead says nothing about whether any tile fits in front of it.
    ///
    /// <b>The threshold is advisory, and that is deliberate.</b> It prunes a placement only while
    /// some other tile in the catalog would clear the bar. Enforced strictly it becomes the thing it
    /// was written to prevent: refusing the last option left is a lock, and one the player cannot
    /// even see the cause of. So when nothing clears it, the rule stands down and the move goes
    /// through.
    /// </summary>
    public bool CanPlace(TileData data, out string reason)
    {
        if (!Fits(HeadFrame, data, out reason))
            return false;

        if (EscapesAfter(data) >= MinimumEscapes)
        {
            reason = "";
            return true;
        }

        foreach (TileData alternative in TileCatalog.AllAsData)
        {
            if (ReferenceEquals(alternative, data)
                || !Fits(HeadFrame, alternative, out _)
                || EscapesAfter(alternative) < MinimumEscapes)
                continue;

            // There is a better move on the board, so this one can be turned down.
            reason = "That would leave the track nowhere to go.";
            return false;
        }

        // Nothing clears the bar. Let it through rather than blocking the builder outright.
        reason = "";
        return true;
    }

    /// <summary>
    /// <see cref="Escapes"/> at the head a tile would leave behind.
    ///
    /// The tile being judged is not on the track yet, so its boxes are staged in
    /// <see cref="_pendingBoxes"/> for the length of the question — otherwise a follow-up that
    /// doubled back through it would be counted as an escape that does not exist.
    /// </summary>
    private int EscapesAfter(TileData data)
    {
        _pendingBoxes.Clear();
        _pendingBoxes.AddRange(PlacedTile.FootprintFor(HeadFrame, data));

        int escapes = Escapes(PlacedTile.ExitFrameFor(HeadFrame, data));

        _pendingBoxes.Clear();
        return escapes;
    }

    /// <summary>
    /// Commit a tile at the head and advance the head to the cell it leads into — which for a
    /// long tile is several cells on, not one. Returns null if the placement wasn't legal.
    /// </summary>
    public PlacedTile? Place(TileData data)
    {
        if (!CanPlace(data, out string reason))
        {
            GD.PushWarning($"[TrackGrid] Rejected placement at {HeadAnchor.Position}: {reason}");
            return null;
        }

        var tile = new PlacedTile
        {
            Data = data,
            Index = _ordered.Count,
            EntryFrame = HeadFrame,
            EntryAnchor = HeadAnchor,
        };

        foreach (TrackFootprint box in tile.Footprint)
        {
            Bucket(box, _boxes.Count);
            _boxes.Add(box);
        }

        _ordered.Add(tile);

        // The head moves by folding the tile's own exit transform onto the seam it was placed at.
        // This is the line the whole migration was for: the frame carries everything — including
        // a bank, when an authored piece leaves one — and the anchor is its flat shadow.
        HeadFrame = tile.ExitFrame;
        HeadAnchor = PlacedTile.AnchorOf(HeadFrame);
        return tile;
    }

    /// <summary>
    /// Take the last tile back off the end of the track and put the head where that tile started.
    /// Returns the tile that was removed, or null if the track was empty.
    ///
    /// Only ever the last one. Placement is append-only — the head is the single point the track
    /// grows from — so undo is the same rule read backwards, and lifting a tile out of the middle
    /// would leave a gap no later tile could be reached across.
    ///
    /// The head is restored from the removed tile's <i>entry</i> state rather than recomputed from
    /// the new last tile. Those are the same thing, and the removed tile is the one that already
    /// knows: it recorded the cell, heading and height it was placed at, which is by definition
    /// where the next tile now goes.
    /// </summary>
    public PlacedTile? RemoveLast()
    {
        // Nothing left that is still road. Undo and crumbling never meet in practice — undo is a
        // lobby thing and crumbling is a match thing — but a tile that has already fallen away is
        // not there to be taken back.
        if (_ordered.Count <= _retired)
            return null;

        PlacedTile last = _ordered[^1];
        _ordered.RemoveAt(_ordered.Count - 1);

        // Undo is the only thing that shortens the box list, and it can only ever take from the
        // end, so the bucket indices below the watermark stay valid. The buckets are rebuilt rather
        // than picked through: undo is a lobby action on a track of a few dozen tiles, and a correct
        // rebuild is worth more than a clever removal.
        foreach (TrackFootprint _ in last.Footprint)
            _boxes.RemoveAt(_boxes.Count - 1);

        RebuildBuckets();

        HeadFrame = last.EntryFrame;
        HeadAnchor = last.EntryAnchor;

        return last;
    }

    /// <summary>
    /// Lay down a starting straight so racers have something to launch from, and leave the
    /// head at the far end of it. <paramref name="length"/> counts tiles, not cells — each one
    /// is a catalog straight, so it runs <see cref="TileCatalog.ShortRun"/> metres.
    /// </summary>
    public void BuildStartingStraight(TrackAnchor start, int length)
    {
        Reset(start);
        for (int i = 0; i < length; i++)
            Place(new TileData(TileHazard.Straight, runLength: TileCatalog.ShortRun));
    }

}
