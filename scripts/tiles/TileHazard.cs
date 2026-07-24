namespace MasterTrack.Tiles;

/// <summary>
/// The hazard carried by a track tile. The Track Master sees these on the tiles in
/// their hand; a Racer is only told the hazard when the tile lands 3 tiles ahead of
/// them, after which they have to remember it.
/// </summary>
public enum TileHazard
{
    /// <summary>A plain length of track with no special hazard.</summary>
    Straight,

    /// <summary>A ramp that launches the racer into the air.</summary>
    JumpAhead,

    /// <summary>A vertical loop.</summary>
    LoopAhead,

    /// <summary>A very tight 180-degree turn.</summary>
    HairpinTurn,

    /// <summary>A gentle bend.</summary>
    Curve,

    /// <summary>A narrowing section that squeezes the racers together.</summary>
    Bottleneck,

    /// <summary>A slick surface that reduces grip.</summary>
    IcePatch,

    /// <summary>A gap the racer must clear or fall through.</summary>
    Gap,
}

public static class TileHazardExtensions
{
    /// <summary>Player-facing label used in the Track Master hand and Racer warnings.</summary>
    public static string DisplayName(this TileHazard hazard) => hazard switch
    {
        TileHazard.Straight => "Straight",
        TileHazard.JumpAhead => "Jump Ahead",
        TileHazard.LoopAhead => "Loop Ahead",
        TileHazard.HairpinTurn => "Hairpin Turn",
        TileHazard.Curve => "Curve",
        TileHazard.Bottleneck => "Bottleneck",
        TileHazard.IcePatch => "Ice Patch",
        TileHazard.Gap => "Gap",
        _ => hazard.ToString(),
    };
}
