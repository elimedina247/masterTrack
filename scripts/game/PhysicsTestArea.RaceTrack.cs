using Godot;
using MasterTrack.Tiles;

namespace MasterTrack.Game;

/// <summary>
/// The proving ground's fixed race track: a full course laid off the south edge of the pad, built
/// from the same <see cref="TrackTile"/> the Track Master places so there is no second copy of
/// anything.
///
/// Fixed, as opposed to the buildable track off the north edge — that one starts as a bare start
/// tile and is whatever the host lays down this lobby. This is the one that is always the same,
/// which is what makes it useful for judging a change to the physics: drive it before, drive it
/// after.
///
/// The tile grid on the pad shows each tile as an isolated specimen with a run-up to it. That
/// answers "what does this tile do"; it says nothing about what the tiles are like one after
/// another at racing speed, which is the only way they are ever actually met. Hence this: every
/// tile in <see cref="TileCatalog"/> except the loop, joined end to end into one drivable course.
///
/// Point to point rather than a lap, because that is the shape the game builds — the track grows
/// ahead of the racers and never closes on itself, and <see cref="TrackGrid.CanPlace"/> rejects
/// anything that would run it back into itself.
///
/// It sits off the tarmac on purpose. The tiles that are holes — the gap, the split — are only
/// holes if there is air underneath them, and a pad under the course would quietly floor every one
/// of them. The cost is that falling off the side is a real fall, same as in a match.
/// </summary>
public partial class PhysicsTestArea
{
	/// <summary>
	/// Cell the course starts from, and the way it runs.
	///
	/// The south end of the pad, so it stays out of the way of the buildable track — that one
	/// anchors on the north edge, and having the two grow away from each other is what keeps them
	/// from ever meeting in the middle. The pad is grown to reach this start line; see
	/// <see cref="PadEdgeForRaceTrack"/>.
	/// </summary>
	private static readonly Vector2I RaceTrackStartCell = new(0, 15);

	private const TrackDirection RaceTrackStartDirection = TrackDirection.South;

	/// <summary>
	/// The course, in order, by catalog display name. Names rather than indices so that reordering
	/// the catalog cannot silently rebuild the track into something else.
	///
	/// Three things shape the order. The elevation has to climb before it drops, because the grid
	/// will not let the track dig below the ground plane — so the two climbs come first and are
	/// paid back largest-first, which is also the only order that keeps the head off the floor
	/// between them. The corners are chosen to walk the course north in a serpentine, so it never
	/// doubles back over the pad. And the hazards are dealt out so that each shelf of elevation
	/// carries a few: there is no point climbing eighty metres to drive a plain straight up there.
	/// </summary>
	private static readonly string[] RaceTrackTiles =
	{
		// Off the pad and up to speed.
		"Straight",
		"Boost Pads",
		"Curve Right",
		"Slalom",
		"Bottleneck",
		"Curve Left",

		// One cube up. Everything from here to the descent is a hole with real air under it.
		"Ramp Up",
		"Curve Left",
		"Gap",
		"Ice Patch",
		"Hairpin Right",
		"Gravel Bed",

		// Two more cubes: the high section, three cubes off the deck.
		"Tall Ramp Up",
		"Curve Left",
		"Jump",
		"Curve Left",
		"Crushers",

		// And back down. Two cubes then one — the other way round would need the track to be
		// standing on the ground and still have a cube left to give.
		"Tall Ramp Down",
		"Curve Right",
		"Ramp Down",
		"Curve Right",

		// Ground level again for the run home.
		"Log Trap",
		"Split Track",
		"Hairpin Left",
		"Spinner",
		"Launch Pads",
		"Whoops",
		"Straight",
	};

	/// <summary>
	/// How far the pad has to reach for the course's first tile to butt straight up against it:
	/// the start cell's near face, half a cell short of its centre.
	///
	/// Unsigned, because the pad is measured in half-extents and is symmetric about the origin —
	/// it reaches the same distance whichever end the course is anchored to.
	/// </summary>
	private static float PadEdgeForRaceTrack
		=> Mathf.Abs(RaceTrackStartCell.Y * TileCatalog.TileSize) - TileCatalog.TileSize * 0.5f;

	/// <summary>
	/// Lay the course out. Runs the placements through a real <see cref="TrackGrid"/> rather than
	/// hand-placed cells, so the course is held to exactly the rules a Track Master's track is: the
	/// grid works out every cell and heading, and refuses anything that would overlap or dig
	/// underground instead of leaving a tile floating somewhere wrong.
	/// </summary>
	private void BuildRaceTrack()
	{
		var grid = new TrackGrid();
		grid.Reset(RaceTrackStartCell, RaceTrackStartDirection);

		foreach (string name in RaceTrackTiles)
		{
			TileDefinition? definition = FindTile(name);
			if (definition == null)
			{
				GD.PushError($"[TestArea] The race track asks for \"{name}\", which is not a catalog tile.");
				continue;
			}

			// The grid has already logged why, and carrying on leaves the rest of the course
			// standing — a track with one tile missing is still worth driving, and the hole in it
			// is the clearest possible sign of what went wrong.
			PlacedTile? placed = grid.Place(definition.ToTileData());
			if (placed == null)
			{
				GD.PushError($"[TestArea] The race track's {name} would not fit at {grid.HeadCell}.");
				continue;
			}

			var tile = new TrackTile
			{
				Name = $"Race{placed.Index:00}_{name.Replace(" ", "")}",
			};
			_generated.AddChild(tile);

			// No drop: this track was always here, unlike one the Track Master is dealing out.
			tile.Initialize(placed.Data, placed.Index, placed.Cell, placed.EntryDirection,
							placed.EntryHeight);
		}

		WarnAboutTilesLeftOff();
		AddRaceTrackLabels(grid);
	}

	private static TileDefinition? FindTile(string displayName)
	{
		foreach (TileDefinition definition in TileCatalog.All)
		{
			if (definition.DisplayName == displayName)
				return definition;
		}

		return null;
	}

	/// <summary>
	/// The course is supposed to be one of everything, so say so when it stops being that. Adding a
	/// tile to the catalog is the easy way to break it — the palette and the Track Master's hand
	/// both pick a new entry up for free, and this is the one place that has to be told.
	///
	/// The loop is the standing exception: it is a tile with nothing underneath it that has to be
	/// taken at speed, and threading it into a course that also has to be driven the rest of the
	/// way would make the whole track about the loop.
	/// </summary>
	private static void WarnAboutTilesLeftOff()
	{
		foreach (TileDefinition definition in TileCatalog.All)
		{
			if (definition.Hazard == TileHazard.LoopAhead)
				continue;

			if (System.Array.IndexOf(RaceTrackTiles, definition.DisplayName) >= 0)
				continue;

			GD.PushWarning($"[TestArea] {definition.DisplayName} is in the catalog but not on the "
						   + "race track. Add it to RaceTrackTiles.");
		}
	}

	/// <summary>
	/// Signposts at each end, and one on the pad pointing the way. Positioned off the start
	/// direction rather than hard-coded, so moving the course to the other end of the pad is a
	/// change to <see cref="RaceTrackStartCell"/> and nothing else.
	/// </summary>
	private void AddRaceTrackLabels(TrackGrid grid)
	{
		Color accent = TileCatalog.All[0].Accent;
		Vector2I step = RaceTrackStartDirection.Step();
		var along = new Vector3(step.X, 0.0f, step.Y);

		// On the pad, a tile short of the edge the course runs off.
		AddLabel(RaceTrackStartDirection == TrackDirection.South ? "Fixed course ↓" : "Fixed course ↑",
				 along * (_padHalfZ - TileCatalog.TileSize) + new Vector3(0.0f, 8.0f, 0.0f), accent);

		// A short step back up the run-up, so the sign is read on the way in rather than driven past.
		AddLabel("Start", TileCatalog.CellToWorld(RaceTrackStartCell)
						  - along * (TileCatalog.TileSize * 0.4f) + new Vector3(0.0f, 8.0f, 0.0f), accent);

		// The head is the open cell past the last tile, at the height the track left off at, so
		// the sign lands just beyond the end of the road rather than on top of the final tile.
		AddLabel("Finish", TileCatalog.CellToWorld(grid.HeadCell, grid.HeadHeight)
						   + new Vector3(0.0f, 8.0f, 0.0f), accent);
	}
}
