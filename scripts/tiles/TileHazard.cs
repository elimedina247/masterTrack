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

    // Everything below is appended rather than slotted in alphabetically: the hazard goes over
    // the wire as its integer value, so inserting one in the middle would rename every tile a
    // client already knows about.

    /// <summary>A rolling log that sweeps across the road trying to shove racers off it.</summary>
    LogTrap,

    /// <summary>Sprung pads that fire whatever drives over them into the air.</summary>
    LaunchPad,

    /// <summary>The middle of the road falls away, leaving a ledge along each wall.</summary>
    SplitTrack,

    /// <summary>A climb. The track stays up there afterwards.</summary>
    RampUp,

    /// <summary>A descent, down to no lower than the ground.</summary>
    RampDown,

    /// <summary>Pads that slam the racer forward.</summary>
    BoostPad,

    /// <summary>Pistons that hammer down across the road on a timer.</summary>
    Crusher,

    /// <summary>A bladed arm sweeping round the middle of the road.</summary>
    Spinner,

    /// <summary>Staggered blocks that have to be weaved through.</summary>
    Slalom,

    /// <summary>A washboard of humps that throws the suspension around.</summary>
    Whoops,

    /// <summary>Loose gravel. Little grip and it drags the car down.</summary>
    Gravel,

    /// <summary>A ribbon of road that snakes across the tile, with dirt runoff either side of it.</summary>
    Squiggle,
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
        TileHazard.LogTrap => "Log Trap",
        TileHazard.LaunchPad => "Launch Pads",
        TileHazard.SplitTrack => "Split Track",
        TileHazard.RampUp => "Ramp Up",
        TileHazard.RampDown => "Ramp Down",
        TileHazard.BoostPad => "Boost Pads",
        TileHazard.Crusher => "Crushers",
        TileHazard.Spinner => "Spinner",
        TileHazard.Slalom => "Slalom",
        TileHazard.Whoops => "Whoops",
        TileHazard.Gravel => "Gravel Bed",
        TileHazard.Squiggle => "Squiggle",
        _ => hazard.ToString(),
    };
}
