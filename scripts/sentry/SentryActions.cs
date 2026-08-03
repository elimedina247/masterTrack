namespace MasterTrack.Sentry;

/// <summary>The things a sentry can spend points on. See <see cref="SentryActions"/> for costs.</summary>
public enum SentryActionKind
{
	/// <summary>A car becomes a bumper: anyone who touches it is launched away hard.</summary>
	Bouncy,

	/// <summary>Two cars are roped together and can no longer drive apart.</summary>
	ChainedUp,

	/// <summary>A slow missile at a spot on the track; a big blast when it arrives.</summary>
	Missile,

	/// <summary>A bomb planted on the road that blows on its own after a short fuse.</summary>
	BarrelBomb,

	/// <summary>A car's throttle is jammed open: no brakes, extra top speed, and no way off.</summary>
	RunawayBooster,

	/// <summary>A car's steering is wired backwards for a while.</summary>
	CrossedWires,

	/// <summary>A dark puddle on the road that turns the tires to soap for anyone crossing it.</summary>
	OilSlick,

	/// <summary>Gravity drops for everyone at once. Every crest becomes a flight.</summary>
	MoonGravity,

	// Everything below is appended rather than slotted in by theme: the kind goes over the wire
	// as its integer value, the same rule TileHazard lives by.

	/// <summary>A point on the road that drags every car near it toward itself for a while.</summary>
	Magnet,

	/// <summary>A rainfall of heavy junk — cubes and spheres — that lands and stays on the road.</summary>
	CargoSpill,

	/// <summary>A wedge that rises out of a tile's hazard slot and launches whoever meets it.</summary>
	PopUpRamp,
}

/// <summary>
/// How an action is aimed, which is also how the sentry bar groups its buttons: eight-plus
/// buttons in one undifferentiated row stopped being readable, and the aiming gesture is the
/// difference a sentry actually thinks in.
/// </summary>
public enum SentryActionCategory
{
	/// <summary>Click a racer's marker.</summary>
	TargetCar,

	/// <summary>Click a spot on the road.</summary>
	PlaceOnTrack,

	/// <summary>No target — it lands on everybody at once.</summary>
	Everyone,
}

/// <summary>
/// The sentry's shop window: what each action costs and how long it runs. One static table
/// rather than data on nodes, because the server and every client price-check the same actions
/// and a disagreement about a cost is a desync about who can afford what.
///
/// The economy is a regenerating pool rather than a fixed ration: the sentry starts full and
/// earns points back every second, so the role is a rhythm of spending rather than a single
/// budget to husband. The pool's size is the host's call — a lobby setting on
/// <see cref="Networking.GameManager"/> — and the regen never overfills it.
/// </summary>
public static class SentryActions
{
	/// <summary>Points the sentry earns back each second while the race runs.</summary>
	public const int RegenPerSecond = 5;

	/// <summary>How long a Bouncy! car stays a bumper, in seconds.</summary>
	public const float BouncyDuration = 10.0f;

	/// <summary>How long a chained pair stays chained, in seconds.</summary>
	public const float ChainDuration = 10.0f;

	/// <summary>Slack in the chain, in metres. Inside this the pair drive normally.</summary>
	public const float ChainLength = 10.0f;

	/// <summary>
	/// Seconds between a debuff being announced and it taking hold. Every delayed action shares
	/// the one fuse: the sentry leads their target by the same beat everywhere, and a racer only
	/// has to learn one "how long do I have" — the same window the barrel's fuse gives.
	/// </summary>
	public const float LeadSeconds = 2.0f;

	/// <summary>How long a runaway booster runs, in seconds. Short on purpose — the burn is
	/// violent now, and three seconds of it is a corner arriving much too early.</summary>
	public const float BoosterDuration = 3.0f;

	/// <summary>Speed the booster adds to the victim's top speed, in m/s — about a third over
	/// stock, so every corner arrives faster than they have ever taken it.</summary>
	public const float BoosterSpeedBonus = 18.0f;

	/// <summary>What the booster multiplies the car's drive force by while it burns. Stock
	/// reaches top speed in a little under four seconds; at three times the force the gap to the
	/// raised ceiling closes in about one — a rocket strapped on, not a tailwind.</summary>
	public const float BoosterAccelMultiplier = 3.0f;

	/// <summary>How long crossed wires stay crossed, in seconds.</summary>
	public const float WiresDuration = 6.0f;

	/// <summary>How long an oil slick stays slick once spread, in seconds.</summary>
	public const float OilSlickDuration = 20.0f;

	/// <summary>Radius of the puddle, in metres. Deliberately dodgeable: a racer who reads the
	/// road steers around it, and painting a line takes several.</summary>
	public const float OilSlickRadius = 7.5f;

	/// <summary>What the puddle multiplies tire grip by. On tarmac this lands next to ice.</summary>
	public const float OilGripMultiplier = 0.12f;

	/// <summary>How long the moon takes the wheel, in seconds. Short on purpose — it hits the
	/// whole pack at once, and it should end before it stops being funny.</summary>
	public const float MoonGravityDuration = 5.0f;

	/// <summary>What gravity drops to while the moon is on, as a fraction of normal.</summary>
	public const float MoonGravityFactor = 0.25f;

	/// <summary>
	/// Costs sized against the 500-point default pool and its 5/sec regen: a full pool is a real
	/// burst of everything, and sustained income buys roughly one mid-tier play every ten
	/// seconds. Cooldowns carry the anti-spam load, so the price only has to make burst-dumping
	/// a decision rather than a reflex.
	/// </summary>
	public static int CostOf(SentryActionKind kind) => kind switch
	{
		SentryActionKind.Bouncy => 25,
		SentryActionKind.ChainedUp => 45,
		SentryActionKind.Missile => 55,
		SentryActionKind.BarrelBomb => 35,
		SentryActionKind.RunawayBooster => 35,
		SentryActionKind.CrossedWires => 20,
		SentryActionKind.OilSlick => 25,
		SentryActionKind.MoonGravity => 90,
		SentryActionKind.Magnet => 50,
		SentryActionKind.CargoSpill => 70,
		SentryActionKind.PopUpRamp => 40,
		_ => 0,
	};

	/// <summary>
	/// Seconds before the same action may fire again. Sized to how <i>disruptive</i> a tool is
	/// rather than to what it costs — moon gravity is an event and waits like one, while crossed
	/// wires is a jab the sentry can throw often.
	/// </summary>
	public static float CooldownOf(SentryActionKind kind) => kind switch
	{
		SentryActionKind.Bouncy => 8.0f,
		SentryActionKind.ChainedUp => 12.0f,
		SentryActionKind.Missile => 10.0f,
		SentryActionKind.BarrelBomb => 6.0f,
		SentryActionKind.RunawayBooster => 10.0f,
		SentryActionKind.CrossedWires => 6.0f,
		SentryActionKind.OilSlick => 8.0f,
		SentryActionKind.MoonGravity => 45.0f,
		SentryActionKind.Magnet => 15.0f,
		SentryActionKind.CargoSpill => 20.0f,
		SentryActionKind.PopUpRamp => 12.0f,
		_ => 0.0f,
	};

	/// <summary>How the action is aimed — the sentry bar's grouping. See
	/// <see cref="SentryActionCategory"/>.</summary>
	public static SentryActionCategory CategoryOf(SentryActionKind kind) => kind switch
	{
		SentryActionKind.Bouncy => SentryActionCategory.TargetCar,
		SentryActionKind.ChainedUp => SentryActionCategory.TargetCar,
		SentryActionKind.RunawayBooster => SentryActionCategory.TargetCar,
		SentryActionKind.CrossedWires => SentryActionCategory.TargetCar,
		SentryActionKind.MoonGravity => SentryActionCategory.Everyone,
		_ => SentryActionCategory.PlaceOnTrack,
	};

	public static string NameOf(SentryActionKind kind) => kind switch
	{
		SentryActionKind.Bouncy => "Bouncy!",
		SentryActionKind.ChainedUp => "Chained up!",
		SentryActionKind.Missile => "Missile",
		SentryActionKind.BarrelBomb => "Barrel bomb",
		SentryActionKind.RunawayBooster => "Runaway booster",
		SentryActionKind.CrossedWires => "Crossed wires!",
		SentryActionKind.OilSlick => "Oil slick",
		SentryActionKind.MoonGravity => "Moon gravity",
		SentryActionKind.Magnet => "Magnet",
		SentryActionKind.CargoSpill => "Cargo spill",
		SentryActionKind.PopUpRamp => "Pop-up ramp",
		_ => "?",
	};
}
