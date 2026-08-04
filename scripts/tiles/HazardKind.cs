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

    /// <summary>
    /// A tape-ringed red plate on a spring, planted dormant and fired by hand: it punches
    /// whatever is standing on it into the air, then hangs long enough for the cars behind to
    /// drive through the gap. The first of the rigged devices — see `docs/sentry-mode-plan.md`.
    /// </summary>
    SpringTrap,

    /// <summary>
    /// A gantry over the road: four posts, a top frame, and a log slung from ropes at one side.
    /// Fired, the ropes let go and the log falls into a pendulum that swings across the road —
    /// the first hazard that owns a whole tile rather than a spot on one.
    /// </summary>
    LogTrap,

    /// <summary>A target painted across the road. Fired, it calls down the sentry's missile —
    /// the same ordnance, arriving the same way, at a spot bought once during the rig.</summary>
    Bullseye,

    /// <summary>A charge on the road that the sentry lights by hand: a short fuse, then a blast
    /// that owns the whole stretch it sits on.</summary>
    BombTrap,
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
        HazardKind.SpringTrap => "Spring Trap",
        HazardKind.LogTrap => "Log Trap",
        HazardKind.Bullseye => "Bullseye",
        HazardKind.BombTrap => "Bomb Trap",
        _ => kind.ToString(),
    };

    /// <summary>
    /// What a hazard costs the builder, in dollars. The shop's whole price list.
    ///
    /// Priced by how much of a moment the thing makes rather than by how hard it was to build:
    /// a launch pad fires at whoever happens to drive over it, while a spring trap is aimed by a
    /// human being at a moment of their choosing, and the second is worth more than twice the
    /// first however similar the two look sitting on the road.
    ///
    /// The budget these are sized against is <see cref="TrackMaster.TrackMasterController.HazardFunds"/>
    /// — a few hundred dollars on a twenty-tile track, so a rig is six to twelve devices rather
    /// than a slot filled on every piece. The cap on how many hazards a track can carry before it
    /// stops being readable is the real constraint, and the price list is how it is enforced.
    /// </summary>
    public static int PriceOf(this HazardKind kind) => kind switch
    {
        HazardKind.LaunchPad => 40,
        HazardKind.PopUpRamp => 60,
        HazardKind.SpringTrap => 85,
        // The dearest thing on the shelf, and it should be: it owns a whole tile, it is visible
        // from the far end of the track, and a swing sweeps the full width of the road rather
        // than a spot on it. One log trap is a corner of the course the racers have to solve.
        HazardKind.LogTrap => 120,
        // A missile on a spot you keep. Dear because the shot is the sentry's biggest, and
        // because the bullseye buys it repeatedly rather than once.
        HazardKind.Bullseye => 100,
        // The cheap decisive one: no throw, no machinery, just a circle of road nobody may be
        // standing in two seconds from now.
        HazardKind.BombTrap => 65,
        _ => 50,
    };

    /// <summary>One line on the shop shelf: what the builder is buying.</summary>
    public static string Blurb(this HazardKind kind) => kind switch
    {
        HazardKind.LaunchPad => "Fires anyone who drives over it.",
        HazardKind.PopUpRamp => "Rises out of the road and throws them.",
        HazardKind.SpringTrap => "You fire it. Punches a car skyward.",
        HazardKind.LogTrap => "You cut it loose. Sweeps the middle of the road.",
        HazardKind.Bullseye => "You call it in. A missile lands on the mark.",
        HazardKind.BombTrap => "You light it. Two seconds, then it takes the road.",
        _ => "",
    };

    /// <summary>The mounting a hazard needs. A hazard fits a slot only when this matches the
    /// slot's declared kind — placement legality is decided here and nowhere else.</summary>
    public static HazardSlotKind SlotKind(this HazardKind kind) => kind switch
    {
        HazardKind.LaunchPad => HazardSlotKind.Surface,
        HazardKind.PopUpRamp => HazardSlotKind.Surface,
        HazardKind.SpringTrap => HazardSlotKind.Surface,
        // The first FullWidth hazard. It does not sit on the road, it stands over it, so it
        // cannot share a piece with anything and only pieces that declare the mounting take it.
        HazardKind.LogTrap => HazardSlotKind.FullWidth,
        HazardKind.Bullseye => HazardSlotKind.Surface,
        HazardKind.BombTrap => HazardSlotKind.Surface,
        _ => HazardSlotKind.Surface,
    };
}
