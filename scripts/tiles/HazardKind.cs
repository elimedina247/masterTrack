namespace MasterTrack.Tiles;

/// <summary>
/// The furniture hazards: things that sit <i>on</i> road, as opposed to the shapes that
/// <i>are</i> road (<see cref="TileHazard"/>'s hairpins and loops, which stay tile geometry).
/// A furniture hazard is one implementation with three spawn paths — authored into a piece
/// scene, dropped into a slot by the builder, or planted live by the sentry — which is the
/// whole reason it is its own enum rather than more values on <see cref="TileHazard"/>: that
/// enum means "which geometry to generate", and furniture generates road for nobody.
///
/// Append-only, same as <see cref="TileHazard"/> and for the same reason: the kind goes over
/// the wire as its integer value.
/// </summary>
public enum HazardKind
{
    /// <summary>A sprung pad that fires whatever drives over it straight up.</summary>
    LaunchPad,

    /// <summary>A wedge that rises out of the road and launches whoever meets it forward and up.</summary>
    PopUpRamp,
}

/// <summary>
/// How a hazard mounts to the road, which is what a <see cref="TrackHazardSlot"/> filters on:
/// a spinner needs a pivot in the middle, a crusher hangs overhead, and neither fits a slot
/// authored for a flat pad.
/// </summary>
public enum HazardSlotKind
{
    /// <summary>Flat on the road surface: pads, ramps, trapdoors.</summary>
    Surface,

    /// <summary>A pivot in the middle of the road: spinners.</summary>
    Centre,

    /// <summary>Mounted above the road: crushers.</summary>
    Overhead,

    /// <summary>Spans the full width: gates, log traps.</summary>
    FullWidth,
}

public static class HazardKindExtensions
{
    /// <summary>Player-facing label, used in the builder's hazard hand and racer warnings.</summary>
    public static string DisplayName(this HazardKind kind) => kind switch
    {
        HazardKind.LaunchPad => "Launch Pad",
        HazardKind.PopUpRamp => "Pop-up Ramp",
        _ => kind.ToString(),
    };

    /// <summary>The mounting a hazard needs. A hazard fits a slot only when this matches the
    /// slot's declared kind — placement legality is decided here and nowhere else.</summary>
    public static HazardSlotKind SlotKind(this HazardKind kind) => kind switch
    {
        HazardKind.LaunchPad => HazardSlotKind.Surface,
        HazardKind.PopUpRamp => HazardSlotKind.Surface,
        _ => HazardSlotKind.Surface,
    };
}
