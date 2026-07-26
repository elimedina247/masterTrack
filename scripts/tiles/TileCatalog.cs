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
	/// How many grid cells the tile runs for along the direction of travel. Only meaningful for
	/// a tile that runs straight through — see <see cref="TileData.CellLength"/>.
	/// </summary>
	public int CellLength { get; init; } = 1;

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
	/// weight, not a probability — though the catalog's currently add up to 100, so each one
	/// happens to read as a percentage.
	/// </summary>
	public required float Weight { get; init; }

	public TileData ToTileData() => new(Hazard, ExitTurn, CellLength, HeightChange);
}

/// <summary>
/// Every tile type in the game. The Track Master's hand is dealt from here, and the builder
/// palette is built straight off this list — add an entry and it shows up in both.
///
/// A tile is one cell wide but can run for several along the direction of travel — see
/// <see cref="StraightCells"/>. Turning tiles are the exception and stay a single cell, because
/// the turn is what the cell is for; a hairpin is two, one per quarter turn.
/// <see cref="TileHazard.LoopAhead"/> still isn't here — a loop needs vertical geometry, which
/// the grid doesn't model.
/// </summary>
public static class TileCatalog
{
	/// <summary>
	/// Width and depth of one tile, in metres. This is the single knob for the scale of the
	/// whole board: everything that describes the track's footprint — tile geometry, the
	/// builder's camera, the start line — is derived from it.
	///
	/// Sized so that the three tiles a racer is warned about are far enough ahead to actually
	/// be driven for: at 40 m a car doing 150 km/h gets nearly three seconds of warning per
	/// tile, where a 10 m cell gave it well under one.
	/// </summary>
	public const float TileSize = 40.0f;

	/// <summary>
	/// How many cells a tile that runs straight through covers. Every non-turning tile is this
	/// long, so a straight is a real stretch of road rather than a single cell: a hazard gets a
	/// run-up and a run-out either side of it instead of arriving the moment the last one ended,
	/// and the racers get room to fight over the line between hazards.
	///
	/// Curves stay one cell — a corner is a quarter turn, and stretching it over three cells
	/// would make it something else.
	/// </summary>
	public const int StraightCells = 3;

	/// <summary>
	/// One step of track elevation, in metres — a "cube", so a cell on its side. Ramps are the
	/// only tiles that change height and they come in one- and two-cube versions, which over a
	/// three-cell run works out to climbs of about 18 and 34 degrees.
	///
	/// Deliberately the same as <see cref="TileSize"/>: a cube of track is the unit the board
	/// reads in, so height and length staying the same size is what lets the Track Master judge
	/// a climb by eye from above.
	/// </summary>
	public const float HeightStep = TileSize;

	public static readonly IReadOnlyList<TileDefinition> All = new List<TileDefinition>
	{
		// ---- Shape: what the track does, rather than what it does to you ----

		new()
		{
			Hazard = TileHazard.Straight,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Straight",
			Description = "A plain length of track. No hazard — good for building distance.",
			Accent = new Color(0.70f, 0.74f, 0.80f),
			Weight = 12.0f,
		},
		new()
		{
			Hazard = TileHazard.Curve,
			ExitTurn = -1,
			DisplayName = "Curve Left",
			Description = "A quarter turn to the left.",
			Accent = new Color(0.15f, 0.55f, 1.00f),
			Weight = 8.0f,
		},
		new()
		{
			Hazard = TileHazard.Curve,
			ExitTurn = 1,
			DisplayName = "Curve Right",
			Description = "A quarter turn to the right.",
			Accent = new Color(0.15f, 0.55f, 1.00f),
			Weight = 8.0f,
		},
		// ExitTurn 2 rather than -2 is what puts the swing on the right; see
		// TileData.TurnSide for why the sign is doing that work.
		new()
		{
			Hazard = TileHazard.HairpinTurn,
			ExitTurn = 2,
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
			CellLength = StraightCells,
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
			CellLength = StraightCells,
			HeightChange = 2,
			DisplayName = "Tall Ramp Up",
			Description = "Climbs two cubes over the same three cells, with a boost strip at the "
						  + "foot of it. Nothing gets up this on its own.",
			Accent = new Color(0.15f, 0.90f, 0.30f),
			Weight = 2.0f,
		},
		new()
		{
			Hazard = TileHazard.RampDown,
			ExitTurn = 0,
			CellLength = StraightCells,
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
			CellLength = StraightCells,
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
			CellLength = StraightCells,
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
			CellLength = StraightCells,
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
			CellLength = StraightCells,
			DisplayName = "Gap",
			Description = "A hole in the road. Carry enough speed to clear it or drop through.",
			Accent = new Color(0.95f, 0.20f, 0.40f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.SplitTrack,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Split Track",
			Description = "The middle of the road falls away. Pick a side and hold it for "
						  + "seventy metres.",
			Accent = new Color(0.32f, 0.38f, 0.52f),
			Weight = 3.0f,
		},
		new()
		{
			Hazard = TileHazard.Bottleneck,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Bottleneck",
			Description = "The track pinches in, squeezing the racers together.",
			Accent = new Color(1.00f, 0.20f, 0.85f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.Slalom,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Slalom",
			Description = "Four gates, alternating sides. Nothing moves and nothing pushes back "
						  + "— it just asks whether you can drive.",
			Accent = new Color(0.78f, 1.00f, 0.10f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.Whoops,
			ExitTurn = 0,
			CellLength = StraightCells,
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
			CellLength = StraightCells,
			DisplayName = "Ice Patch",
			Description = "Sheet ice across the middle. Almost no grip — do not be turning.",
			Accent = new Color(0.45f, 0.92f, 1.00f),
			Weight = 4.0f,
		},
		new()
		{
			Hazard = TileHazard.Gravel,
			ExitTurn = 0,
			CellLength = StraightCells,
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
			CellLength = StraightCells,
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
			CellLength = StraightCells,
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
			CellLength = StraightCells,
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
			CellLength = StraightCells,
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
			CellLength = StraightCells,
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

	/// <summary>
	/// Centre of a tile that enters at <paramref name="entryCell"/> and runs
	/// <paramref name="cellLength"/> cells on from there. An even-length tile centres between
	/// two cells, which is fine — the tile is a mesh, not a cell.
	/// </summary>
	public static Vector3 SpanCenterToWorld(Vector2I entryCell, TrackDirection direction, int cellLength,
											int heightCubes = 0)
	{
		Vector2I step = direction.Step();
		float offset = (cellLength - 1) * 0.5f * TileSize;
		return CellToWorld(entryCell, heightCubes) + new Vector3(step.X * offset, 0.0f, step.Y * offset);
	}

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
				&& definition.CellLength == data.CellLength
				&& definition.HeightChange == data.HeightChange)
				return definition;
		}
		return All[0];
	}
}
