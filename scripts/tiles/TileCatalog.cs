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

    /// <summary>Label shown on the palette button.</summary>
    public required string DisplayName { get; init; }

    /// <summary>One-line explanation shown as the button's tooltip.</summary>
    public required string Description { get; init; }

    /// <summary>Accent colour used for the palette swatch and the tile's hazard markings.</summary>
    public required Color Accent { get; init; }

    public TileData ToTileData() => new(Hazard, ExitTurn);
}

/// <summary>
/// Every tile type in the game. The Track Master's hand is dealt from here, and the builder
/// palette is built straight off this list — add an entry and it shows up in both.
///
/// Tiles are all a single grid cell. <see cref="TileHazard.HairpinTurn"/> and
/// <see cref="TileHazard.LoopAhead"/> aren't here yet: a hairpin exits back into the cell the
/// track arrived from, and a loop needs vertical geometry, so both want multi-cell tiles that
/// the grid doesn't model yet.
/// </summary>
public static class TileCatalog
{
    /// <summary>Width and depth of one tile, in metres.</summary>
    public const float TileSize = 10.0f;

    public static readonly IReadOnlyList<TileDefinition> All = new List<TileDefinition>
    {
        new()
        {
            Hazard = TileHazard.Straight,
            ExitTurn = 0,
            DisplayName = "Straight",
            Description = "A plain length of track. No hazard — good for building distance.",
            Accent = new Color(0.55f, 0.58f, 0.62f),
        },
        new()
        {
            Hazard = TileHazard.Curve,
            ExitTurn = -1,
            DisplayName = "Curve Left",
            Description = "A quarter turn to the left.",
            Accent = new Color(0.35f, 0.65f, 0.95f),
        },
        new()
        {
            Hazard = TileHazard.Curve,
            ExitTurn = 1,
            DisplayName = "Curve Right",
            Description = "A quarter turn to the right.",
            Accent = new Color(0.35f, 0.65f, 0.95f),
        },
        new()
        {
            Hazard = TileHazard.JumpAhead,
            ExitTurn = 0,
            DisplayName = "Jump",
            Description = "A ramp that launches the racer into the air. Hit it too fast and you overshoot.",
            Accent = new Color(0.98f, 0.75f, 0.20f),
        },
        new()
        {
            Hazard = TileHazard.Gap,
            ExitTurn = 0,
            DisplayName = "Gap",
            Description = "A hole in the road. Carry enough speed to clear it or drop through.",
            Accent = new Color(0.90f, 0.30f, 0.30f),
        },
        new()
        {
            Hazard = TileHazard.Bottleneck,
            ExitTurn = 0,
            DisplayName = "Bottleneck",
            Description = "The track pinches in, squeezing the racers together.",
            Accent = new Color(0.85f, 0.45f, 0.85f),
        },
        new()
        {
            Hazard = TileHazard.IcePatch,
            ExitTurn = 0,
            DisplayName = "Ice Patch",
            Description = "Sheet ice across the middle. Almost no grip — don't be turning.",
            Accent = new Color(0.60f, 0.90f, 0.98f),
        },
    };

    /// <summary>World position of the centre of a grid cell, on the road surface.</summary>
    public static Vector3 CellToWorld(Vector2I cell)
        => new(cell.X * TileSize, 0.0f, cell.Y * TileSize);

    /// <summary>Grid cell containing a world position.</summary>
    public static Vector2I WorldToCell(Vector3 world)
        => new(Mathf.RoundToInt(world.X / TileSize), Mathf.RoundToInt(world.Z / TileSize));

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
