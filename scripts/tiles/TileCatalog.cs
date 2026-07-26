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

	public TileData ToTileData() => new(Hazard, ExitTurn, CellLength);
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

	public static readonly IReadOnlyList<TileDefinition> All = new List<TileDefinition>
	{
		new()
		{
			Hazard = TileHazard.Straight,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Straight",
			Description = "A plain length of track. No hazard — good for building distance.",
			Accent = new Color(0.55f, 0.58f, 0.62f),
			Weight = 24.0f,
		},
		new()
		{
			Hazard = TileHazard.Curve,
			ExitTurn = -1,
			DisplayName = "Curve Left",
			Description = "A quarter turn to the left.",
			Accent = new Color(0.35f, 0.65f, 0.95f),
			Weight = 14.0f,
		},
		new()
		{
			Hazard = TileHazard.Curve,
			ExitTurn = 1,
			DisplayName = "Curve Right",
			Description = "A quarter turn to the right.",
			Accent = new Color(0.35f, 0.65f, 0.95f),
			Weight = 14.0f,
		},
		new()
		{
			// ExitTurn 2 rather than -2 is what puts the swing on the right; see
			// TileData.TurnSide for why the sign is doing that work.
			Hazard = TileHazard.HairpinTurn,
			ExitTurn = 2,
			DisplayName = "Hairpin Right",
			Description = "A 180-degree turn to the right. Two quarter turns in one tile — "
						  + "come in fast and you will not make the apex.",
			Accent = new Color(0.45f, 0.45f, 0.95f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.HairpinTurn,
			ExitTurn = -2,
			DisplayName = "Hairpin Left",
			Description = "A 180-degree turn to the left. Two quarter turns in one tile — "
						  + "come in fast and you will not make the apex.",
			Accent = new Color(0.45f, 0.45f, 0.95f),
			Weight = 5.0f,
		},
		new()
		{
			Hazard = TileHazard.JumpAhead,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Jump",
			Description = "A ramp that launches the racer into the air. Hit it too fast and you overshoot.",
			Accent = new Color(0.98f, 0.75f, 0.20f),
			Weight = 10.0f,
		},
		new()
		{
			Hazard = TileHazard.Gap,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Gap",
			Description = "A hole in the road. Carry enough speed to clear it or drop through.",
			Accent = new Color(0.90f, 0.30f, 0.30f),
			Weight = 8.0f,
		},
		new()
		{
			Hazard = TileHazard.Bottleneck,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Bottleneck",
			Description = "The track pinches in, squeezing the racers together.",
			Accent = new Color(0.85f, 0.45f, 0.85f),
			Weight = 10.0f,
		},
		new()
		{
			Hazard = TileHazard.IcePatch,
			ExitTurn = 0,
			CellLength = StraightCells,
			DisplayName = "Ice Patch",
			Description = "Sheet ice across the middle. Almost no grip — don't be turning.",
			Accent = new Color(0.60f, 0.90f, 0.98f),
			Weight = 10.0f,
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

	/// <summary>World position of the centre of a grid cell, on the road surface.</summary>
	public static Vector3 CellToWorld(Vector2I cell)
		=> new(cell.X * TileSize, 0.0f, cell.Y * TileSize);

	/// <summary>Grid cell containing a world position.</summary>
	public static Vector2I WorldToCell(Vector3 world)
		=> new(Mathf.RoundToInt(world.X / TileSize), Mathf.RoundToInt(world.Z / TileSize));

	/// <summary>
	/// Centre of a tile that enters at <paramref name="entryCell"/> and runs
	/// <paramref name="cellLength"/> cells on from there. An even-length tile centres between
	/// two cells, which is fine — the tile is a mesh, not a cell.
	/// </summary>
	public static Vector3 SpanCenterToWorld(Vector2I entryCell, TrackDirection direction, int cellLength)
	{
		Vector2I step = direction.Step();
		float offset = (cellLength - 1) * 0.5f * TileSize;
		return CellToWorld(entryCell) + new Vector3(step.X * offset, 0.0f, step.Y * offset);
	}

	/// <summary>Look a definition up by index, or null if the index is out of range.</summary>
	public static TileDefinition? At(int index)
		=> index >= 0 && index < All.Count ? All[index] : null;

	/// <summary>
	/// Find the definition matching a placed tile's data, so a replicated placement can be
	/// rendered with the right look. Falls back to the first entry.
	/// </summary>
	public static TileDefinition Match(TileData data)
	{
		foreach (TileDefinition definition in All)
		{
			if (definition.Hazard == data.Hazard && definition.ExitTurn == data.ExitTurn)
				return definition;
		}
		return All[0];
	}
}
