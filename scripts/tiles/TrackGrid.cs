using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles;

/// <summary>A tile that has been committed to the track.</summary>
public sealed class PlacedTile
{
    public required Vector2I Cell { get; init; }

    /// <summary>Direction a racer is travelling as they enter this tile.</summary>
    public required TrackDirection EntryDirection { get; init; }

    public required TileData Data { get; init; }

    /// <summary>Position along the track from the start line. Drives the "3 tiles ahead" warning.</summary>
    public required int Index { get; init; }

    /// <summary>Direction a racer is travelling as they leave this tile.</summary>
    public TrackDirection ExitDirection => EntryDirection.Turn(Data.ExitTurn);
}

/// <summary>
/// The track as a model: which cells are occupied, in what order, and where the next tile is
/// allowed to go. Pure data with no scene nodes, so the server can own the authoritative copy
/// and every client can keep an identical one from replicated placements.
///
/// The track is a single connected path. Tiles are only ever added at the head — the open
/// cell the leading racer is driving toward — which is what makes "place tiles ahead of the
/// racers" the whole game rather than free-form building.
/// </summary>
public sealed class TrackGrid
{
    private readonly Dictionary<Vector2I, PlacedTile> _byCell = new();
    private readonly List<PlacedTile> _ordered = new();

    /// <summary>The open cell the next tile goes in.</summary>
    public Vector2I HeadCell { get; private set; }

    /// <summary>Direction a racer will be travelling when they enter <see cref="HeadCell"/>.</summary>
    public TrackDirection HeadDirection { get; private set; } = TrackDirection.North;

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
    }

    public PlacedTile? TileAt(Vector2I cell)
        => _byCell.TryGetValue(cell, out PlacedTile? tile) ? tile : null;

    /// <summary>Track index of the tile in a cell, or -1 if that cell is empty.</summary>
    public int IndexAt(Vector2I cell) => TileAt(cell)?.Index ?? -1;

    public PlacedTile? TileAtIndex(int index)
        => index >= 0 && index < _ordered.Count ? _ordered[index] : null;

    /// <summary>
    /// Whether a tile could legally go in a cell. The only legal cell is the head, and the
    /// tile also has to lead somewhere — a piece that would send the track straight back into
    /// itself is rejected rather than creating a dead end.
    /// </summary>
    public bool CanPlace(Vector2I cell, TileData data, out string reason)
    {
        if (cell != HeadCell)
        {
            reason = "Tiles can only be added to the end of the track.";
            return false;
        }

        if (_byCell.ContainsKey(cell))
        {
            reason = "That cell already has a tile.";
            return false;
        }

        Vector2I exitCell = cell + HeadDirection.Turn(data.ExitTurn).Step();
        if (_byCell.ContainsKey(exitCell))
        {
            reason = "That would run the track back into itself.";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// Commit a tile at the head and advance the head to the cell it leads into.
    /// Returns null if the placement wasn't legal.
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
        };

        _byCell[tile.Cell] = tile;
        _ordered.Add(tile);

        HeadDirection = tile.ExitDirection;
        HeadCell = tile.Cell + HeadDirection.Step();

        return tile;
    }

    /// <summary>
    /// Lay down a starting straight so racers have something to launch from, and leave the
    /// head at the far end of it.
    /// </summary>
    public void BuildStartingStraight(Vector2I startCell, TrackDirection direction, int length)
    {
        Reset(startCell, direction);
        for (int i = 0; i < length; i++)
            Place(new TileData(TileHazard.Straight));
    }

    /// <summary>Which tile a world position sits on, or null if it's off the track.</summary>
    public PlacedTile? TileAtWorld(Vector3 world) => TileAt(TileCatalog.WorldToCell(world));
}
