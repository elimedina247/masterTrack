using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles;

/// <summary>A tile that has been committed to the track.</summary>
public sealed class PlacedTile
{
    /// <summary>The cell the racer enters through — the first of possibly several.</summary>
    public required Vector2I Cell { get; init; }

    /// <summary>Direction a racer is travelling as they enter this tile.</summary>
    public required TrackDirection EntryDirection { get; init; }

    public required TileData Data { get; init; }

    /// <summary>Position along the track from the start line. Drives the "3 tiles ahead" warning.</summary>
    public required int Index { get; init; }

    /// <summary>
    /// Elevation the racer enters at, in cubes above the ground plane. The tile's geometry is
    /// built from here, so a ramp starts at this height and ends at <see cref="ExitHeight"/>.
    /// </summary>
    public required int EntryHeight { get; init; }

    /// <summary>Elevation the racer leaves at. Same as <see cref="EntryHeight"/> unless a ramp.</summary>
    public int ExitHeight => EntryHeight + Data.HeightChange;

    /// <summary>Direction a racer is travelling as they leave this tile.</summary>
    public TrackDirection ExitDirection => EntryDirection.Turn(Data.ExitTurn);

    /// <summary>
    /// The last cell the tile covers — the one the racer leaves through. Same as
    /// <see cref="Cell"/> for a single-cell tile.
    /// </summary>
    public Vector2I ExitCell => ExitCellFor(Cell, EntryDirection, Data);

    /// <summary>Every cell the tile covers, from the entry cell onward.</summary>
    public IEnumerable<Vector2I> Cells => CellsFor(Cell, EntryDirection, Data);

    /// <summary>
    /// The cells a tile would cover, entering <paramref name="entryCell"/> heading
    /// <paramref name="entryDirection"/>. Two shapes:
    ///
    /// A normal tile is a straight run of <see cref="TileData.CellLength"/> cells in the entry
    /// direction; only its exit is allowed to turn, which is why a curve has to be a single cell.
    ///
    /// A hairpin is an L of exactly two cells — the entry cell, plus one to the side it swings
    /// out to — and leaves through the second. That is the whole reason this is a footprint and
    /// not a length: the hairpin is the one tile that steps off the line it came in on.
    ///
    /// Static so the legality check can ask the question before there is a tile to ask it of,
    /// and so both answers come from the same place.
    /// </summary>
    public static IEnumerable<Vector2I> CellsFor(Vector2I entryCell, TrackDirection entryDirection,
                                                 TileData data)
    {
        yield return entryCell;

        if (data.IsHairpin)
        {
            // One cell per leg, whatever CellLength says: turning twice in the space of two
            // cells is what makes this a hairpin rather than a wide bay.
            yield return entryCell + entryDirection.Turn(data.TurnSide).Step();
            yield break;
        }

        Vector2I step = entryDirection.Step();
        for (int i = 1; i < data.CellLength; i++)
            yield return entryCell + step * i;
    }

    /// <summary>The cell such a tile would be left through. See <see cref="CellsFor"/>.</summary>
    public static Vector2I ExitCellFor(Vector2I entryCell, TrackDirection entryDirection, TileData data)
        => data.IsHairpin
            ? entryCell + entryDirection.Turn(data.TurnSide).Step()
            : entryCell + entryDirection.Step() * (data.CellLength - 1);
}

/// <summary>
/// The track as a model: which cells are occupied, in what order, and where the next tile is
/// allowed to go. Pure data with no scene nodes, so the server can own the authoritative copy
/// and every client can keep an identical one from replicated placements.
///
/// The track is a single connected path. Tiles are only ever added at the head — the open
/// cell the leading racer is driving toward — which is what makes "place tiles ahead of the
/// racers" the whole game rather than free-form building.
///
/// A tile can cover more than one cell — see <see cref="PlacedTile.CellsFor"/> for the shapes.
/// Every cell it covers maps back to the same <see cref="PlacedTile"/>, so asking which tile a
/// car is on works the same whether it's standing on the front of a long straight, the middle of
/// it, or the far side of a hairpin.
///
/// The track also has elevation: <see cref="HeadHeight"/> is a running count of cubes climbed,
/// which ramps move up and down. Cells stay two-dimensional even so, because two tiles are never
/// allowed to share a cell whatever height they are at — the track may climb over its own
/// neighbourhood but never over itself, which keeps "which tile is this car on" a flat lookup.
/// </summary>
public sealed class TrackGrid
{
    private readonly Dictionary<Vector2I, PlacedTile> _byCell = new();
    private readonly List<PlacedTile> _ordered = new();

    /// <summary>The open cell the next tile goes in.</summary>
    public Vector2I HeadCell { get; private set; }

    /// <summary>Direction a racer will be travelling when they enter <see cref="HeadCell"/>.</summary>
    public TrackDirection HeadDirection { get; private set; } = TrackDirection.North;

    /// <summary>
    /// Elevation the next tile starts at, in cubes. Runs up and down as ramps are placed, and is
    /// never allowed below 0 — the ground plane is the floor of the world, and a track that dug
    /// underneath it would leave the racers driving through the dark.
    /// </summary>
    public int HeadHeight { get; private set; }

    /// <summary>Tiles in track order, from the start line onward.</summary>
    public IReadOnlyList<PlacedTile> Tiles => _ordered;

    public int Count => _ordered.Count;

    /// <summary>Clear the track and start a new one at the given cell and heading.</summary>
    public void Reset(Vector2I startCell, TrackDirection startDirection)
    {
        _byCell.Clear();
        _ordered.Clear();
        HeadCell = startCell;
        HeadDirection = startDirection;
        HeadHeight = 0;
    }

    public PlacedTile? TileAt(Vector2I cell)
        => _byCell.TryGetValue(cell, out PlacedTile? tile) ? tile : null;

    /// <summary>Track index of the tile in a cell, or -1 if that cell is empty.</summary>
    public int IndexAt(Vector2I cell) => TileAt(cell)?.Index ?? -1;

    public PlacedTile? TileAtIndex(int index)
        => index >= 0 && index < _ordered.Count ? _ordered[index] : null;

    /// <summary>
    /// Whether a tile could legally go in a cell. The only legal cell is the head, every cell of
    /// the tile's footprint has to be clear — a long straight needs the room to lie down in, and
    /// a hairpin needs the room to swing out into — and the tile also has to lead somewhere, so
    /// a piece that would send the track straight back into itself is rejected rather than
    /// creating a dead end.
    /// </summary>
    public bool CanPlace(Vector2I cell, TileData data, out string reason)
    {
        if (cell != HeadCell)
        {
            reason = "Tiles can only be added to the end of the track.";
            return false;
        }

        foreach (Vector2I occupied in PlacedTile.CellsFor(cell, HeadDirection, data))
        {
            if (!_byCell.ContainsKey(occupied))
                continue;

            reason = data.IsHairpin ? "There isn't room to swing the hairpin round."
                   : data.CellLength > 1 ? "There isn't room for a tile that long."
                   : "That cell already has a tile.";
            return false;
        }

        Vector2I exitCell = PlacedTile.ExitCellFor(cell, HeadDirection, data)
                            + HeadDirection.Turn(data.ExitTurn).Step();
        if (_byCell.ContainsKey(exitCell))
        {
            reason = "That would run the track back into itself.";
            return false;
        }

        if (HeadHeight + data.HeightChange < 0)
        {
            reason = "The track is already on the ground — it can't go down from here.";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// Commit a tile at the head and advance the head to the cell it leads into — which for a
    /// long tile is several cells on, not one. Returns null if the placement wasn't legal.
    /// </summary>
    public PlacedTile? Place(TileData data)
    {
        if (!CanPlace(HeadCell, data, out string reason))
        {
            GD.PushWarning($"[TrackGrid] Rejected placement at {HeadCell}: {reason}");
            return null;
        }

        var tile = new PlacedTile
        {
            Cell = HeadCell,
            EntryDirection = HeadDirection,
            Data = data,
            Index = _ordered.Count,
            EntryHeight = HeadHeight,
        };

        foreach (Vector2I occupied in tile.Cells)
            _byCell[occupied] = tile;
        _ordered.Add(tile);

        HeadDirection = tile.ExitDirection;
        HeadCell = tile.ExitCell + HeadDirection.Step();
        HeadHeight = tile.ExitHeight;

        return tile;
    }

    /// <summary>
    /// Lay down a starting straight so racers have something to launch from, and leave the
    /// head at the far end of it. <paramref name="length"/> counts tiles, not cells — each one
    /// is a catalog straight, so it covers <see cref="TileCatalog.StraightCells"/> cells.
    /// </summary>
    public void BuildStartingStraight(Vector2I startCell, TrackDirection direction, int length)
    {
        Reset(startCell, direction);
        for (int i = 0; i < length; i++)
            Place(new TileData(TileHazard.Straight, cellLength: TileCatalog.StraightCells));
    }

    /// <summary>Which tile a world position sits on, or null if it's off the track.</summary>
    public PlacedTile? TileAtWorld(Vector3 world) => TileAt(TileCatalog.WorldToCell(world));
}
