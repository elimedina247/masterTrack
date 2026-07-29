using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// Serializable description of a single track tile: which hazard it carries and how it
/// connects to neighbouring tiles. Small and network-friendly so the server can hand
/// tiles to the Track Master and replicate placements to every peer.
/// </summary>
[GlobalClass]
public partial class TileData : Resource
{
    [Export] public TileHazard Hazard { get; set; } = TileHazard.Straight;

    /// <summary>
    /// Direction the track turns as it leaves this tile, in 90-degree steps (grid based).
    /// 0 = straight through, 1 = quarter turn right, -1 = quarter turn left, 2 = U-turn.
    /// </summary>
    [Export] public int ExitTurn { get; set; }

    /// <summary>
    /// How far the tile runs, in metres, along the direction of travel. Meaningless on a tile that
    /// turns — a corner's length is its arc, and that comes from <see cref="TurnRadius"/>.
    ///
    /// <b>Metres, not cells.</b> This used to be a count of grid squares, which meant a tile's
    /// length only came in 54 m jumps and "make it 40% shorter" could only ever deliver 33%. In
    /// <see cref="TileCatalog"/> it is now <see cref="TileCatalog.ShortRun"/> or
    /// <see cref="TileCatalog.LongRun"/>, and either can be any number that reads well.
    ///
    /// Clamped positive: a tile of no length would occupy nothing and leave the head where it
    /// already was, so the track would quietly stop growing.
    /// </summary>
    [Export]
    public float RunLength
    {
        get => _runLength;
        set => _runLength = Mathf.Max(1.0f, value);
    }

    private float _runLength = TileCatalog.ShortRun;

    /// <summary>
    /// How much higher the track is when the racer leaves this tile than when they entered it, in
    /// cubes — <see cref="TileCatalog.HeightStep"/> metres each. Positive climbs, negative drops,
    /// 0 is flat, which is everything but the ramps.
    ///
    /// The change is cumulative: the grid carries a running height, so a tile that climbs leaves
    /// every tile after it up there until something brings the track back down. That is what makes
    /// a ramp a ramp rather than a bump.
    /// </summary>
    [Export] public int HeightChange { get; set; }

    /// <summary>
    /// Whether this tile doubles back on itself — a 180-degree turn, leaving the racer heading
    /// the way they came in a lane one cell to the side. The only tile whose footprint steps off
    /// the line it entered on.
    /// </summary>
    public bool IsHairpin => Mathf.Abs(ExitTurn) == 2;

    /// <summary>
    /// Whether this tile turns at all. A quarter turn either way, or a hairpin.
    ///
    /// It used to also have to ask whether the turn spanned more than one cell, because a one-cell
    /// turn pinned its radius to half the road's width — 27 m against the 37 m the car needs to hold
    /// <c>TopSpeed</c> at all, which made every corner a wall you slid into rather than a corner you
    /// drove. That was the single biggest reason the track was not fun, and it is not a question any
    /// more: a corner's radius is <see cref="TurnRadius"/> and owes nothing to a cell.
    /// </summary>
    public bool IsTurn => ExitTurn != 0;

    /// <summary>
    /// Radius of the arc this tile sweeps, in metres. Zero for anything that runs straight through.
    ///
    /// <b>Free, at last.</b> While the track was on a grid this was pinned to
    /// <c>(span - 0.5) x TileSize</c>: the road is a tile wide and centred on the arc, so its edges
    /// came out at <c>radius +/- half a tile</c> and both had to land on a cell face or the corner
    /// met its neighbours with a step in the road. That left exactly three legal radii — 27 m, past
    /// what the car can hold; 81 m; and 135 m — with nothing in between. It is why "tighten the
    /// corners a little" was impossible for as long as the cells existed. See
    /// <c>docs/track-without-a-grid.md</c>.
    ///
    /// Now it is a number. What it should be is a question about the car and the bank rather than
    /// about arithmetic — see <see cref="TileCatalog.CornerRadius"/>.
    ///
    /// Lives here because two things read it and they must not drift: the geometry builds the arc
    /// from it, and <see cref="PlacedTile.ExitAnchorFor"/> works out where the tile hands the track
    /// on from it.
    /// </summary>
    [Export] public float TurnRadius { get; set; }

    /// <summary>How far round that arc the tile goes, in radians. Zero if it runs straight.</summary>
    public float TurnSweep => Mathf.Pi * 0.5f * Mathf.Abs(ExitTurn);

    /// <summary>
    /// Which way the tile swings out: 1 to the right of the entry direction, -1 to the left.
    ///
    /// A U-turn reverses the racer whichever way round it goes — <c>Turn(2)</c> and
    /// <c>Turn(-2)</c> land on the same direction — so for a hairpin the exit direction says
    /// nothing about which side of the track it occupies. The sign of <see cref="ExitTurn"/> is
    /// what carries that, which is why a right hairpin is 2 and a left one is -2.
    /// </summary>
    public int TurnSide => ExitTurn >= 0 ? 1 : -1;

    /// <summary>Path to the visual scene instanced when this tile is placed on the track.</summary>
    [Export] public string ScenePath { get; set; } = "";

    public TileData() { }

    public TileData(TileHazard hazard, int exitTurn = 0, float runLength = 0.0f,
                    float turnRadius = 0.0f, int heightChange = 0, string scenePath = "")
    {
        Hazard = hazard;
        ExitTurn = exitTurn;
        RunLength = runLength > 0.0f ? runLength : TileCatalog.ShortRun;
        TurnRadius = turnRadius;
        HeightChange = heightChange;
        ScenePath = scenePath;
    }

    /// <summary>
    /// Pack into a Godot dictionary for sending across the wire in an RPC. Keeping this
    /// explicit (rather than relying on Resource serialization) keeps replication cheap
    /// and predictable.
    /// </summary>
    public Godot.Collections.Dictionary ToDict() => new()
    {
        ["hazard"] = (int)Hazard,
        ["exit_turn"] = ExitTurn,
        ["run_length"] = RunLength,
        ["turn_radius"] = TurnRadius,
        ["height_change"] = HeightChange,
        ["scene_path"] = ScenePath,
    };

    public static TileData FromDict(Godot.Collections.Dictionary dict) => new(
        (TileHazard)(int)dict["hazard"],
        (int)dict["exit_turn"],
        (float)dict["run_length"],
        (float)dict["turn_radius"],
        (int)dict["height_change"],
        (string)dict["scene_path"]);
}
