using Godot;
using MasterTrack.Networking;
using MasterTrack.Tiles;

namespace MasterTrack.Racer;

/// <summary>
/// A Racer's car in third person, built on Godot's built-in <see cref="VehicleBody3D"/>
/// for realistic-enough driving out of the box (suspension, wheel traction, steering).
/// Movement is driven locally by the owning peer for responsiveness; other peers will see
/// this car via a MultiplayerSynchronizer on its transform (next step).
///
/// This controller also receives the "3 tiles ahead" hazard warning: when a tile lands
/// three slots in front of this racer, the server calls <see cref="WarnHazard"/> on the
/// owning client only. After the warning fades, the player must *remember* it.
/// </summary>
public partial class RacerController : VehicleBody3D
{
    /// <summary>Drive force applied to the traction wheels. Tune to taste.</summary>
    [Export] public float EnginePower = 1500.0f;

    /// <summary>Max steering angle in radians (~0.6 ≈ 34°).</summary>
    [Export] public float MaxSteer = 0.6f;

    /// <summary>How fast the steering angle eases toward its target (rad/sec).</summary>
    [Export] public float SteerSpeed = 4.0f;

    /// <summary>How many tiles ahead the racer is warned about a landing tile.</summary>
    public const int WarningLookahead = 3;

    /// <summary>Which peer owns/controls this car.</summary>
    [Export] public int OwnerPeerId { get; set; }

    /// <summary>Track index the racer is currently on, tracked by the server.</summary>
    public int CurrentTrackIndex { get; private set; }

    [Signal] public delegate void HazardWarnedEventHandler(int trackIndex, int hazard, string hazardName);

    /// <summary>True on the machine whose player owns/controls this car.</summary>
    public bool IsLocalPlayer => OwnerPeerId == Multiplayer.GetUniqueId();

    public override void _Ready()
    {
        // When spawned over the network (via MultiplayerSpawner) the owner isn't set by
        // hand, so we encode it in the node name and recover it here. Solo/host spawns
        // set OwnerPeerId directly before adding the node, so this leaves those alone.
        if (OwnerPeerId == 0 && int.TryParse(Name, out int idFromName))
            OwnerPeerId = idFromName;

        // The owning peer is the movement authority for its own car.
        if (Multiplayer.MultiplayerPeer != null)
            SetMultiplayerAuthority(OwnerPeerId);

        // Hand the camera to the local player only.
        GetNodeOrNull<CameraRig>("CameraRig")?.SetActive(IsLocalPlayer);
    }

    public override void _PhysicsProcess(double delta)
    {
        // Only the owning peer reads input for its own car.
        if (!IsLocalPlayer)
            return;

        float steerInput = Input.GetAxis("racer_steer_left", "racer_steer_right");
        float throttle = Input.GetAxis("racer_brake", "racer_accelerate");

        // TEMP debug: prove input is reaching the car (rate-limited so it doesn't spam).
        if ((throttle != 0 || steerInput != 0) && Engine.GetPhysicsFrames() % 20 == 0)
            GD.Print($"[Racer] input throttle={throttle:F2} steer={steerInput:F2} speed={LinearVelocity.Length():F1}");

        // GetAxis returns +1 for "steer right"; VehicleBody3D positive steering turns left,
        // so negate. Ease toward the target angle for a smoother feel.
        Steering = Mathf.MoveToward(Steering, -steerInput * MaxSteer, SteerSpeed * (float)delta);

        // W drives forward, S applies negative force (reverse). The wheels do the rest.
        EngineForce = throttle * EnginePower;
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
