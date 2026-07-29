using System;
using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// One kind of tile the Track Master can be dealt and place. This is the *definition* —
/// the shape and hazard of a tile type. <see cref="TileData"/> is an instance of one.
/// </summary>
public sealed class TileDefinition
{
	public required TileHazard Hazard { get; init; }

	/// <summary>Where the track leaves this tile: 0 straight on, 1 right, -1 left.</summary>
	public int ExitTurn { get; init; }

	/// <summary>
	/// How far the tile runs, in metres. Only meaningful for a tile that runs straight through —
	/// see <see cref="TileData.RunLength"/>.
	/// </summary>
	public float RunLength { get; init; } = TileCatalog.ShortRun;

	/// <summary>Radius of the arc a turning tile sweeps, in metres. See
	/// <see cref="TileData.TurnRadius"/>.</summary>
	public float TurnRadius { get; init; }

	/// <summary>
	/// Cubes of elevation the tile gains, or loses if negative. See
	/// <see cref="TileData.HeightChange"/>.
	/// </summary>
	public int HeightChange { get; init; }

	/// <summary>Label shown on the palette button.</summary>
	public required string DisplayName { get; init; }

	/// <summary>One-line explanation shown as the button's tooltip.</summary>
	public required string Description { get; init; }

	/// <summary>Accent colour used for the palette swatch and the tile's hazard markings.</summary>
	public required Color Accent { get; init; }

	/// <summary>
	/// How often this tile comes up when the Track Master is dealt one. Relative to every other
	/// weight, not a probability — though the catalog's currently add up to about a hundred, so
	/// each one reads near enough as a percentage.
	/// </summary>
	public required float Weight { get; init; }

	public TileData ToTileData() => new(Hazard, ExitTurn, RunLength, TurnRadius, HeightChange);
}

/// <summary>
/// Every tile type in the game. The Track Master's hand is dealt from here, and the builder
/// palette is built straight off this list — add an entry and it shows up in both.
///
/// A tile is one cell wide but can run for several along the direction of travel, and how many
/// depends on whether its feature needs the distance: <see cref="LongRun"/> for the seven that do,
/// <see cref="ShortRun"/> for everything else. Turns answer to neither and take their length from
/// their radius — see <see cref="CornerRadius"/>.
/// <see cref="TileHazard.LoopAhead"/> still isn't here — a loop needs vertical geometry, which
/// the grid doesn't model.
/// </summary>
public static class TileCatalog
{
	/// <summary>
	/// Width of the road, in metres.
	///
	/// <b>This used to be two things and is now one.</b> It was the road's width <i>and</i> the
	/// grid's step, which is why every length and radius on the track quantised to it — tile lengths
	/// only came in 54 m jumps, and a corner's radius could only ever be 27, 81 or 135 m. Splitting
	/// the two apart is what <c>docs/track-without-a-grid.md</c> was about; what is left here is just
	/// how wide the road is.
	///
	/// Sized against what the car can actually do, which is the only thing a road has to be measured
	/// against. Every hazard's dimensions are car-scale metres (see <c>TrackTile</c>), so a road
	/// narrow relative to the car's speed makes each one either invisible or unavoidable — there is
	/// no room across it to place the car.
	///
	/// It is still the number every hazard's lateral budget is spent out of: a slalom asks 6.9 g of
	/// the tires at this width against an 8.5 g <c>MaxGripForce</c> cap, and narrowing the road
	/// raises that as one over the width. 54 m is close enough to the wall that it should move down
	/// only with the slalom and the squiggle re-checked.
	///
	/// The proving ground still lays its specimen tiles out on a lattice of this size, which is the
	/// one place a grid is still the right tool — it is a display case, not a track.
	/// </summary>
	public const float TileSize = 54.0f;

	/// <summary>
	/// The long run: 162 m, for the seven tiles whose feature genuinely needs the distance. Each one
	/// earns it for a reason that is checkable rather than a matter of taste:
	///
	/// - <b>Launch Pads</b> throw the car 135 m. A shorter tile lands it on whatever comes next.
	/// - <b>Jump</b> needs a run-up to pick a speed on and a run-out to land on. Its ramp is only
	///   10 m, so this is the one tile where the empty road really is the mechanic.
	/// - <b>Slalom</b> and <b>Squiggle</b> are lateral: cornering load goes as one over the
	///   wavelength squared, so cutting a third off the tile is 2.25x the g. The slalom would ask
	///   15.5 g of a car that has 8.5.
	/// - <b>Whoops</b> spaces its ridges on the suspension's resonance, 21 m apart. Six of them
	///   compound; four do not.
	/// - <b>Split Track</b> is asking you to hold a line, and the length <i>is</i> the ask.
	/// - <b>Loop</b> needs the run-up its boost pads sit on.
	///
	/// A number of metres now rather than a count of cells, so it can be any number at all. It
	/// happens still to be 162 because that is what those seven tiles measured out at.
	/// </summary>
	public const float LongRun = 162.0f;

	/// <summary>
	/// The short run: 108 m, and the default. Everything not on <see cref="LongRun"/> — the
	/// straights and ramps that only move the track along, and every hazard that is a single feature
	/// rather than a stretch of road.
	///
	/// That second group is where the dead air was. A gap is a 14 m hole, a bottleneck a 19 m pinch,
	/// a spinner a 46 m arm; on the long run each of those was under a third of its own tile and the
	/// rest was transit.
	///
	/// <b>This is now genuinely adjustable.</b> While it was a cell count the only values available
	/// were 108 and 162 with nothing between, which is why "40% shorter" came out at 33%. It can be
	/// 97 if 97 is what plays best.
	/// </summary>
	public const float ShortRun = 108.0f;

	/// <summary>
	/// Radius of a quarter turn, in metres.
	///
	/// <b>This number is a speed, and it is the one the bank was always written for.</b>
	/// <c>TrackTile.TurnMaxBank</c> banks the outer lip at 60 degrees on the argument that a bank
	/// holds a car with no help from the tires at exactly <c>v = sqrt(g x r x tan(theta))</c>, and
	/// that the top of it should therefore be neutral right at <c>TopSpeed</c>. At the grid's 81 m
	/// that landed at 218 km/h — the corner was over-radiused for its own bank, because 81 m was
	/// simply the only legal value the cells offered. At 63 m it lands at <b>199 km/h against a
	/// 200 km/h top speed</b>.
	///
	/// So tightening the corner did not compromise the bank, it delivered it. The corner also drops
	/// from 127 m of arc to 99 — 2.5 seconds to 2.0 at racing speed — which is what was actually
	/// asked for.
	///
	/// The floor is the car, not the geometry: holding <c>TopSpeed</c> at all takes 37 m, and there
	/// wants to be enough over that to drift with. 63 m asks 5.0 g of the available 8.5.
	/// </summary>
	public const float CornerRadius = 63.0f;

	/// <summary>
	/// And a sweeper's: a longer, faster corner meant to be taken flat out on the bank.
	///
	/// Cut from 135 m with the same change. At 135 the sweeper was 212 m of arc — 4.2 seconds, the
	/// longest single tile in the game by a distance, and most of it spent holding one steady angle.
	/// 99 m is 155 m of arc, still half as long again as a normal corner, which is enough for it to
	/// read as a different kind of turn rather than just a slow one.
	/// </summary>
	public const float SweeperRadius = 99.0f;

	/// <summary>
	/// A hairpin's, unchanged at 54 m — the one corner radius the grid was not distorting.
	///
	/// It sets what a hairpin asks of a driver, and the answer is a question rather than a toll. The
	/// inside of the U is 27 m and has to be crawled; the outside is 81 m and banked, and a bank of
	/// that radius carries a car round unaided at about 190 km/h. So the quick way round is the long
	/// way round, held up on the wall — and getting it wrong up there drops you back down the bank
	/// having lost everything.
	/// </summary>
	public const float HairpinRadius = 54.0f;

	/// <summary>
	/// One step of track elevation, in metres. Ramps are the only tiles that change height and they
	/// come in one- and two-cube versions, which over their <see cref="ShortRun"/> works out to
	/// climbs of about 18 and 34 degrees.
	///
	/// <b>This has to move whenever a ramp's length does, because a slope is rise over run.</b> It
	/// used to be <see cref="TileSize"/> exactly — a cube of track was a cell on its side, which is
	/// a tidy thing for the Track Master to read a climb by from above — and that is what it cost
	/// to shorten the ramps: two thirds of 54 is 36, which holds <c>36/108</c> equal to the old
	/// <c>54/162</c> and lands both ramps on the same 18.4 and 33.7 degrees they already had. Left
	/// at 54 the tall ramp would have been 45 degrees average and about 70 through the middle,
	/// which is not a climb, it is a wall with a texture on it.
	///
	/// A cube being smaller than a cell also means the track climbs less far for the same tile, so
	/// an elevated section is 36 m up rather than 54 — less far to fall, and less of a view.
	/// </summary>
	public const float HeightStep = 36.0f;

	public static readonly IReadOnlyList<TileDefinition> All = new List<TileDefinition>
	{
		// ---- Shape: what the track does, rather than what it does to you ----

		new()
		{
			Hazard = TileHazard.Straight,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Straight",
			Description = "A plain length of track. No hazard — good for building distance.",
			Accent = new Color(0.70f, 0.74f, 0.80f),
			// Halved from 12. At 12 this was nearly an eighth of every draw, and a straight is the
			// one tile with nothing in it: 108 m of held throttle, two seconds of the race where
			// neither player has a decision to make. It still has to be the commonest tile — it is
			// what the Track Master reaches for when they want distance rather than a trap — but it
			// should not be the reason the track goes quiet.
			Weight = 6.0f,
		},
		new()
		{
			Hazard = TileHazard.Curve,
			ExitTurn = -1,
			TurnRadius = CornerRadius,
			DisplayName = "Curve Left",
			Description = "A banked quarter turn to the left. Carry speed and the bank holds you "
						  + "up it; arrive slow and high and you slide back down.",
			Accent = new Color(0.15f, 0.55f, 1.00f),
			Weight = 7.0f,
		},
		new()
		{
			Hazard = TileHazard.Curve,
			ExitTurn = 1,
			TurnRadius = CornerRadius,
			DisplayName = "Curve Right",
			Description = "A banked quarter turn to the right. Carry speed and the bank holds you "
						  + "up it; arrive slow and high and you slide back down.",
			Accent = new Color(0.15f, 0.55f, 1.00f),
			Weight = 7.0f,
		},
		new()
		{
			Hazard = TileHazard.Curve,
			ExitTurn = -1,
			TurnRadius = SweeperRadius,
			DisplayName = "Sweeper Left",
			Description = "A long banked left, 135 metres of radius. Flat out on the wall if you "
						  + "dare, and it eats a lot of board.",
			Accent = new Color(0.10f, 0.72f, 0.95f),
			Weight = 3.0f,
		},
		new()
		{
			Hazard = TileHazard.Curve,
			ExitTurn = 1,
			TurnRadius = SweeperRadius,
			DisplayName = "Sweeper Right",
			Description = "A long banked right, 135 metres of radius. Flat out on the wall if you "
						  + "dare, and it eats a lot of board.",
			Accent = new Color(0.10f, 0.72f, 0.95f),
			Weight = 3.0f,
		},
		// ExitTurn 2 rather than -2 is what puts the swing on the right; see
		// TileData.TurnSide for why the sign is doing that work.
		new()
		{
			Hazard = TileHazard.HairpinTurn,
			ExitTurn = 2,
			TurnRadius = HairpinRadius,
			DisplayName = "Hairpin Right",
			Description = "A 180-degree turn to the right. Two quarter turns in one tile — come "
						  + "in fast and you will not make the apex.",
			Accent = new Color(0.45f, 0.25f, 1.00f),
			Weight = 3.0f,
		},
		new()
		{
			Hazard = TileHazard.HairpinTurn,
			ExitTurn = -2,
			TurnRadius = HairpinRadius,
			DisplayName = "Hairpin Left",
			Description = "A 180-degree turn to the left. Two quarter turns in one tile — come "
						  + "in fast and you will not make the apex.",
			Accent = new Color(0.45f, 0.25f, 1.00f),
			Weight = 3.0f,
		},

		// ---- Elevation. Everything placed after a climb is placed up there ----

		new()
		{
			Hazard = TileHazard.RampUp,
			ExitTurn = 0,
			RunLength = ShortRun,
			HeightChange = 1,
			DisplayName = "Ramp Up",
			Description = "Climbs a cube. The track carries on at the new height until something "
						  + "brings it back down.",
			Accent = new Color(0.15f, 0.90f, 0.30f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.RampUp,
			ExitTurn = 0,
			RunLength = ShortRun,
			HeightChange = 2,
			DisplayName = "Tall Ramp Up",
			Description = "Climbs two cubes over the same two cells, with a boost strip at the "
						  + "foot of it. Nothing gets up this on its own.",
			Accent = new Color(0.15f, 0.90f, 0.30f),
			Weight = 2.0f,
		},
		new()
		{
			Hazard = TileHazard.RampDown,
			ExitTurn = 0,
			RunLength = ShortRun,
			HeightChange = -1,
			DisplayName = "Ramp Down",
			Description = "Drops a cube. Only playable while the track is up in the air.",
			Accent = new Color(0.00f, 0.78f, 0.72f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.RampDown,
			ExitTurn = 0,
			RunLength = ShortRun,
			HeightChange = -2,
			DisplayName = "Tall Ramp Down",
			Description = "Drops two cubes. Needs two cubes of height to drop from.",
			Accent = new Color(0.00f, 0.78f, 0.72f),
			Weight = 2.0f,
		},
		new()
		{
			Hazard = TileHazard.LoopAhead,
			ExitTurn = 0,
			RunLength = LongRun,
			DisplayName = "Loop",
			Description = "A full vertical loop with nothing underneath it. Boost pads feed the "
						  + "one lane that leads up into it; miss them and you drop through.",
			Accent = new Color(1.00f, 0.45f, 0.00f),
			Weight = 2.0f,
		},

		// ---- Hazards ----

		new()
		{
			Hazard = TileHazard.JumpAhead,
			ExitTurn = 0,
			RunLength = LongRun,
			DisplayName = "Jump",
			Description = "A ramp that launches the racer into the air. Hit it too fast and you "
						  + "overshoot.",
			Accent = new Color(1.00f, 0.82f, 0.05f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.Gap,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Gap",
			Description = "A hole in the road. Carry enough speed to clear it or drop through.",
			Accent = new Color(0.95f, 0.20f, 0.40f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.SplitTrack,
			ExitTurn = 0,
			RunLength = LongRun,
			DisplayName = "Split Track",
			Description = "The middle of the road falls away. Pick a side and hold it for a "
						  + "hundred and twenty metres.",
			Accent = new Color(0.32f, 0.38f, 0.52f),
			Weight = 3.0f,
		},
		new()
		{
			Hazard = TileHazard.Bottleneck,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Bottleneck",
			Description = "The track pinches in, squeezing the racers together.",
			Accent = new Color(1.00f, 0.20f, 0.85f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.Slalom,
			ExitTurn = 0,
			RunLength = LongRun,
			DisplayName = "Slalom",
			Description = "Four gates, alternating sides. Nothing moves and nothing pushes back "
						  + "— it just asks whether you can drive.",
			Accent = new Color(0.78f, 1.00f, 0.10f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.Squiggle,
			ExitTurn = 0,
			RunLength = LongRun,
			DisplayName = "Squiggle",
			Description = "The road itself snakes, and it is dirt either side of it. Three bends "
						  + "with no walls to lean on — lift, or run wide and lose everything.",
			Accent = new Color(0.85f, 0.60f, 1.00f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.Whoops,
			ExitTurn = 0,
			RunLength = LongRun,
			DisplayName = "Whoops",
			Description = "A washboard of ridges. Take them at the wrong speed and the car never "
						  + "settles between them.",
			Accent = new Color(0.92f, 0.40f, 0.12f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.IcePatch,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Ice Patch",
			Description = "Sheet ice across the middle. Almost no grip — do not be turning.",
			Accent = new Color(0.45f, 0.92f, 1.00f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.Gravel,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Gravel Bed",
			Description = "Loose stuff across the middle. Little grip, and it drags the speed "
						  + "out of you.",
			Accent = new Color(0.74f, 0.58f, 0.32f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.BoostPad,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Boost Pads",
			Description = "Arrows that slam you down the track. The one tile in the hand that "
						  + "helps.",
			Accent = new Color(0.10f, 1.00f, 0.72f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.LaunchPad,
			ExitTurn = 0,
			RunLength = LongRun,
			DisplayName = "Launch Pads",
			Description = "Three sprung pads, staggered. Whatever runs one over goes a very long "
						  + "way up.",
			Accent = new Color(1.00f, 0.10f, 0.12f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.LogTrap,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Log Trap",
			Description = "A log sweeping across the road, and no barriers to be shoved into. "
						  + "Time it or be somewhere else.",
			Accent = new Color(0.62f, 0.38f, 0.18f),
			Weight = 3.0f,
		},
		new()
		{
			Hazard = TileHazard.Crusher,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Crushers",
			Description = "Three pistons hammering the road out of step with each other. The way "
						  + "through is a moving gap.",
			Accent = new Color(0.55f, 0.58f, 0.66f),
			Weight = 3.0f,
		},
		new()
		{
			Hazard = TileHazard.Spinner,
			ExitTurn = 0,
			RunLength = ShortRun,
			DisplayName = "Spinner",
			Description = "An arm sweeping the middle of the road like a clock hand. Follow it "
						  + "through or wear it.",
			Accent = new Color(0.72f, 0.30f, 1.00f),
			Weight = 3.0f,
		},
	};

	/// <summary>
	/// Declared after <see cref="All"/> on purpose: static field initialisers run in source
	/// order, so the list has to exist before anything can add its weights up.
	/// </summary>
	private static readonly float TotalWeight = SumWeights();

	private static float SumWeights()
	{
		float total = 0.0f;
		foreach (TileDefinition definition in All)
			total += definition.Weight;
		return total;
	}

	/// <summary>
	/// Every entry as placeable data, built once. Declared after <see cref="All"/> for the same
	/// reason <see cref="TotalWeight"/> is.
	///
	/// <c>TrackGrid.Escapes</c> asks "what would still fit here" of the whole catalog every time a
	/// placement is judged, and <see cref="TileDefinition.ToTileData"/> allocates a
	/// <see cref="Resource"/> on each call. Twenty-six of those per keystroke is a lot of garbage
	/// for a question whose answer never changes.
	///
	/// <b>Read-only by contract.</b> <see cref="TileData"/> has setters because it is also what
	/// comes off the wire; nothing may write to one of these.
	/// </summary>
	public static readonly IReadOnlyList<TileData> AllAsData = BuildAllAsData();

	private static IReadOnlyList<TileData> BuildAllAsData()
	{
		var data = new List<TileData>(All.Count);
		foreach (TileDefinition definition in All)
			data.Add(definition.ToTileData());
		return data;
	}

	/// <summary>
	/// The shared data for a catalog index, or null if the index is out of range. The read-only
	/// counterpart to <see cref="At"/> — see <see cref="AllAsData"/> before holding on to one.
	/// </summary>
	public static TileData? DataAt(int index)
		=> index >= 0 && index < AllAsData.Count ? AllAsData[index] : null;

	/// <summary>
	/// Pick a tile at random from a subset, respecting the weights <i>inside</i> that subset.
	///
	/// This is the anti-lock backstop. When nothing in the Track Master's hand can legally be
	/// placed, the next deal is drawn from what can be rather than from the whole catalog. The
	/// weights are renormalised across the survivors, so it stays a weighted draw rather than
	/// collapsing to one fixed rescue tile — the hand is bailed out without becoming predictable.
	///
	/// If nothing at all passes <paramref name="allow"/> it falls back to an unfiltered draw.
	/// <c>TrackGrid.MinimumEscapes</c> is supposed to make that unreachable, and if it ever is
	/// reached, a hand holding a tile that explains why it cannot be placed is a better failure
	/// than a hand with an empty slot and no explanation.
	/// </summary>
	public static int DrawIndex(RandomNumberGenerator rng, Func<int, bool> allow)
	{
		float total = 0.0f;
		for (int i = 0; i < All.Count; i++)
		{
			if (allow(i))
				total += All[i].Weight;
		}

		if (total <= 0.0f)
			return DrawIndex(rng);

		float subsetRoll = rng.Randf() * total;

		for (int i = 0; i < All.Count; i++)
		{
			if (!allow(i))
				continue;

			subsetRoll -= All[i].Weight;
			if (subsetRoll <= 0.0f)
				return i;
		}

		// Only reachable if the roll lands past the end on floating point slop. Hand back the last
		// allowed entry rather than the last entry, which might not be one.
		for (int i = All.Count - 1; i >= 0; i--)
		{
			if (allow(i))
				return i;
		}

		return All.Count - 1;
	}

	/// <summary>
	/// Pick a tile at random, respecting <see cref="TileDefinition.Weight"/>. Walks the list
	/// subtracting weights from a roll across the total — the wider a tile's slice, the more
	/// often the roll lands in it.
	/// </summary>
	public static int DrawIndex(RandomNumberGenerator rng)
	{
		float roll = rng.Randf() * TotalWeight;

		for (int i = 0; i < All.Count; i++)
		{
			roll -= All[i].Weight;
			if (roll <= 0.0f)
				return i;
		}

		// Only reachable if the roll lands past the end on floating point slop.
		return All.Count - 1;
	}

	/// <summary>
	/// World position of the centre of a grid cell, on the road surface.
	/// <paramref name="heightCubes"/> lifts it by that many <see cref="HeightStep"/>s, which is
	/// how the track keeps climbing after a ramp.
	/// </summary>
	public static Vector3 CellToWorld(Vector2I cell, int heightCubes = 0)
		=> new(cell.X * TileSize, heightCubes * HeightStep, cell.Y * TileSize);

	/// <summary>Grid cell containing a world position.</summary>
	public static Vector2I WorldToCell(Vector3 world)
		=> new(Mathf.RoundToInt(world.X / TileSize), Mathf.RoundToInt(world.Z / TileSize));

	/// <summary>Look a definition up by index, or null if the index is out of range.</summary>
	public static TileDefinition? At(int index)
		=> index >= 0 && index < All.Count ? All[index] : null;

	/// <summary>
	/// Find the definition matching a placed tile's data, so a replicated placement can be
	/// rendered with the right look. Falls back to the first entry.
	///
	/// Every field that distinguishes two entries has to be compared, not just the hazard: the
	/// catalog holds pairs that differ only in one of them — the two ramps up share a hazard and
	/// an exit turn and differ only in how far they climb — and matching on too little would
	/// quietly hand a tile its sibling's colour and description.
	/// </summary>
	public static TileDefinition Match(TileData data)
	{
		foreach (TileDefinition definition in All)
		{
			if (definition.Hazard == data.Hazard
				&& definition.ExitTurn == data.ExitTurn
				&& Mathf.IsEqualApprox(definition.RunLength, data.RunLength)
				&& Mathf.IsEqualApprox(definition.TurnRadius, data.TurnRadius)
				&& definition.HeightChange == data.HeightChange)
				return definition;
		}
		return All[0];
	}
}
