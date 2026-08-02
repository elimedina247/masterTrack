using Godot;
using MasterTrack.Tiles;

namespace MasterTrack.Game;

/// <summary>
/// What a racer watches while the Track Master builds: a high camera riding the head of the
/// track, so the road they are about to be raced on is assembled in front of them. A cut-down
/// cousin of the builder's own follow camera — no input, no modes, no ghost — because a
/// spectator has nothing to decide.
/// </summary>
public partial class BuildSpectatorCamera : Camera3D
{
    /// <summary>The track being built. Required.</summary>
    public TrackController? Track { get; set; }

    /// <summary>Ride height. A little above the builder's default, to take in more of the shape.</summary>
    private const float Height = TrackTile.Size * 6.5f;

    /// <summary>How quickly the view closes on the head, per second. Gentler than the builder's
    /// camera — this one is scenery, and nobody is aiming anything with it.</summary>
    private const float FollowSpeed = 2.5f;

    public override void _Ready()
    {
        RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f);
        Far = 4000.0f;

        if (Track != null)
            Position = Track.HeadWorldPosition + new Vector3(0.0f, Height, 0.0f);

        Current = true;
    }

    public override void _Process(double delta)
    {
        if (Track == null)
            return;

        float t = 1.0f - Mathf.Exp(-FollowSpeed * (float)delta);
        Position = Position.Lerp(Track.HeadWorldPosition + new Vector3(0.0f, Height, 0.0f), t);
    }
}
