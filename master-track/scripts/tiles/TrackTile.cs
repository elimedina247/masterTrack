using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// A tile that has been placed on the track. Spawned by the server (via a
/// MultiplayerSpawner) so every peer sees it fall into place. Holds its own
/// <see cref="TileData"/> so the racer-warning system can read its hazard.
/// </summary>
public partial class TrackTile : Node3D
{
    /// <summary>Grid coordinate of this tile along the track (index from the start line).</summary>
    [Export] public int TrackIndex { get; set; }

    public TileData Data { get; private set; } = new();

    [Signal] public delegate void TileLandedEventHandler(int trackIndex);

    /// <summary>Populate from replicated data. Called after spawn on every peer.</summary>
    public void Initialize(TileData data, int trackIndex)
    {
        Data = data;
        TrackIndex = trackIndex;
        // Visual "fall from the sky" animation would be triggered here.
        EmitSignal(SignalName.TileLanded, trackIndex);
    }

    public TileHazard Hazard => Data.Hazard;
}
