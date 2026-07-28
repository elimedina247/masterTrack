using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// The four directions a stretch of track can run in, on the tile grid.
///
/// <see cref="North"/> is -Z, which is Godot's forward, so a tile sitting at its identity
/// rotation is one that a racer drives through heading north.
/// </summary>
public enum TrackDirection
{
    North = 0,
    East = 1,
    South = 2,
    West = 3,
}

public static class TrackDirectionExtensions
{
    /// <summary>Step of one cell in this direction, as (x, z) grid coordinates.</summary>
    public static Vector2I Step(this TrackDirection direction) => direction switch
    {
        TrackDirection.North => new Vector2I(0, -1),
        TrackDirection.East => new Vector2I(1, 0),
        TrackDirection.South => new Vector2I(0, 1),
        TrackDirection.West => new Vector2I(-1, 0),
        _ => Vector2I.Zero,
    };

    /// <summary>
    /// Unit vector this direction runs along in world space. The same step as
    /// <see cref="Step"/>, in the axes the world is measured in rather than the grid's.
    /// </summary>
    public static Vector3 Forward(this TrackDirection direction)
    {
        Vector2I step = direction.Step();
        return new Vector3(step.X, 0.0f, step.Y);
    }

    /// <summary>
    /// Unit vector across the track, to the right of <see cref="Forward"/>. What "how far off the
    /// racing line is this" is measured along.
    /// </summary>
    public static Vector3 Right(this TrackDirection direction) => direction.Turn(1).Forward();

    /// <summary>
    /// Turn by quarter-turns clockwise. Matches <see cref="TileData.ExitTurn"/>:
    /// 0 straight on, 1 right, -1 left, 2 a U-turn.
    /// </summary>
    public static TrackDirection Turn(this TrackDirection direction, int quarterTurns)
    {
        int index = ((int)direction + quarterTurns) % 4;
        if (index < 0)
            index += 4;
        return (TrackDirection)index;
    }

    /// <summary>
    /// Yaw in radians that points a tile's local forward (-Z) along this direction.
    /// Rotating about +Y by -90° per quarter-turn clockwise gets there.
    /// </summary>
    public static float Yaw(this TrackDirection direction)
        => Mathf.DegToRad(-90.0f * (int)direction);

    public static string DisplayName(this TrackDirection direction) => direction switch
    {
        TrackDirection.North => "North",
        TrackDirection.East => "East",
        TrackDirection.South => "South",
        TrackDirection.West => "West",
        _ => direction.ToString(),
    };
}
