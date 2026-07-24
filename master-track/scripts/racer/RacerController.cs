using Godot;
using MasterTrack.Networking;
using MasterTrack.Tiles;

namespace MasterTrack.Racer;

/// <summary>
/// A Racer's car in third person. Movement is driven locally by the owning peer for
/// responsiveness and reconciled by the server; other peers see this car via a
/// MultiplayerSynchronizer on its transform.
///
/// This controller also receives the "3 tiles ahead" hazard warning: when a tile lands
/// three slots in front of this racer, the server calls <see cref="WarnHazard"/> on the
/// owning client only. After the warning fades, the player must *remember* it.
/// </summary>
public partial class RacerController : CharacterBody3D
{
    [Export] public float Speed = 12.0f;
    [Export] public float TurnSpeed = 2.5f;

    /// <summary>How many tiles ahead the racer is warned about a landing tile.</summary>
    public const int WarningLookahead = 3;

    /// <summary>Which peer owns/controls this car.</summary>
    [Export] public int OwnerPeerId { get; set; }

    /// <summary>Track index the racer is currently on, tracked by the server.</summary>
    public int CurrentTrackIndex { get; private set; }

    [Signal] public delegate void HazardWarnedEventHandler(int trackIndex, int hazard, string hazardName);

    private bool IsLocalPlayer => OwnerPeerId == Multiplayer.GetUniqueId();

    public override void _PhysicsProcess(double delta)
    {
        // Only the owning peer reads input for its own car.
        if (!IsLocalPlayer)
            return;

        float steer = Input.GetAxis("racer_steer_left", "racer_steer_right");
        float throttle = Input.GetAxis("racer_brake", "racer_accelerate");

        Rotate(Vector3.Up, -steer * TurnSpeed * (float)delta);
        Vector3 forward = -Transform.Basis.Z;
        Velocity = forward * throttle * Speed;
        MoveAndSlide();
    }

    /// <summary>
    /// Server only. Notify this racer's owner that a tile landed <see cref="WarningLookahead"/>
    /// tiles ahead. Sent to just the owning client so each racer only learns about the
    /// hazards in front of *them*.
    /// </summary>
    public void ServerSendHazardWarning(int trackIndex, TileHazard hazard)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        RpcId(OwnerPeerId, MethodName.WarnHazard, trackIndex, (int)hazard);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WarnHazard(int trackIndex, int hazard)
    {
        var h = (TileHazard)hazard;
        GD.Print($"[Racer {OwnerPeerId}] Warning: {h.DisplayName()} in {WarningLookahead} tiles!");
        EmitSignal(SignalName.HazardWarned, trackIndex, hazard, h.DisplayName());
    }
}
