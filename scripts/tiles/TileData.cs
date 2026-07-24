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

    /// <summary>Path to the visual scene instanced when this tile is placed on the track.</summary>
    [Export] public string ScenePath { get; set; } = "";

    public TileData() { }

    public TileData(TileHazard hazard, int exitTurn = 0, string scenePath = "")
    {
        Hazard = hazard;
        ExitTurn = exitTurn;
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
        ["scene_path"] = ScenePath,
    };

    public static TileData FromDict(Godot.Collections.Dictionary dict) => new(
        (TileHazard)(int)dict["hazard"],
        (int)dict["exit_turn"],
        (string)dict["scene_path"]);
}
