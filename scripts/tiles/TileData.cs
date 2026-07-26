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
    /// How many grid cells the tile runs for along the direction of travel. A turning tile is
    /// always 1 — the turn happens inside a single cell — but a tile that runs straight through
    /// can be as long as it likes, and every straight in <see cref="TileCatalog"/> is three.
    ///
    /// A hairpin ignores this: it is always one cell per leg, whatever is set here. See
    /// <see cref="IsHairpin"/>.
    ///
    /// Clamped to at least 1: a tile of no length would occupy nothing and leave the head where
    /// it already was, so the track would quietly stop growing.
    /// </summary>
    [Export]
    public int CellLength
    {
        get => _cellLength;
        set => _cellLength = Mathf.Max(1, value);
    }

    private int _cellLength = 1;

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

    public TileData(TileHazard hazard, int exitTurn = 0, int cellLength = 1, int heightChange = 0,
                    string scenePath = "")
    {
        Hazard = hazard;
        ExitTurn = exitTurn;
        CellLength = cellLength;
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
        ["cell_length"] = CellLength,
        ["height_change"] = HeightChange,
        ["scene_path"] = ScenePath,
    };

    public static TileData FromDict(Godot.Collections.Dictionary dict) => new(
        (TileHazard)(int)dict["hazard"],
        (int)dict["exit_turn"],
        (int)dict["cell_length"],
        (int)dict["height_change"],
        (string)dict["scene_path"]);
}
