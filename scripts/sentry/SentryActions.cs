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

	/// <summary>A bomb planted on the road that arms after a moment and blows on contact.</summary>
	BarrelBomb,

	/// <summary>A car's throttle is jammed open: no brakes, extra top speed, and no way off.</summary>
	RunawayBooster,

	/// <summary>A car's steering is wired backwards for a while.</summary>
	CrossedWires,

	/// <summary>A dark puddle on the road that turns the tires to soap for anyone crossing it.</summary>
	OilSlick,

	/// <summary>Gravity drops for everyone at once. Every crest becomes a flight.</summary>
	MoonGravity,
}

/// <summary>
/// The sentry's shop window: what each action costs and how long it runs. One static table
/// rather than data on nodes, because the server and every client price-check the same actions
/// and a disagreement about a cost is a desync about who can afford what.
///
/// The budget is fixed for the whole race — that is the design, not a placeholder: a sentry
/// who blows everything on the first lap spectates the rest, and rationing the pool <i>is</i>
/// the role. Costs are sized against the 50-point budget: eight-ish debuffs, or four missiles,
/// or a spread of everything, per race.
/// </summary>
public static class SentryActions
{
	/// <summary>Points the sentry has for the whole race. Spent, never regenerated.</summary>
	public const int PointsBudget = 50;

	/// <summary>How long a Bouncy! car stays a bumper, in seconds.</summary>
	public const float BouncyDuration = 10.0f;

	/// <summary>How long a chained pair stays chained, in seconds.</summary>
	public const float ChainDuration = 10.0f;

	/// <summary>Slack in the chain, in metres. Inside this the pair drive normally.</summary>
	public const float ChainLength = 10.0f;

	/// <summary>
	/// Seconds between a debuff being announced and it taking hold. Every delayed action shares
	/// the one fuse: the sentry leads their target by the same beat everywhere, and a racer only
	/// has to learn one "how long do I have" — the same window the barrel's arm delay gives.
	/// </summary>
	public const float LeadSeconds = 2.0f;

	/// <summary>How long a runaway booster runs, in seconds.</summary>
	public const float BoosterDuration = 5.0f;

	/// <summary>Speed the booster adds to the victim's top speed, in m/s — about a third over
	/// stock, so every corner arrives faster than they have ever taken it.</summary>
	public const float BoosterSpeedBonus = 18.0f;

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

	public static int CostOf(SentryActionKind kind) => kind switch
	{
		SentryActionKind.Bouncy => 6,
		SentryActionKind.ChainedUp => 10,
		SentryActionKind.Missile => 12,
		SentryActionKind.BarrelBomb => 8,
		SentryActionKind.RunawayBooster => 8,
		SentryActionKind.CrossedWires => 5,
		SentryActionKind.OilSlick => 5,
		SentryActionKind.MoonGravity => 12,
		_ => 0,
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
		_ => "?",
	};
}
