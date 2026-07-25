using Godot;
using MasterTrack.Networking;
using MasterTrack.Tiles;
using MasterTrack.Vehicles;

namespace MasterTrack.Racer;

/// <summary>
/// A Racer's car in third person. The driving comes from <see cref="Vehicle"/> — a ray-cast
/// rigid body with real suspension, a brush tire model, a gearbox and a stack of assists —
/// so this class is only about *who* is driving and what they're told.
///
/// Input is read locally by the owning peer for responsiveness; other peers see this car
/// through a MultiplayerSynchronizer on its transform (next step).
///
/// This controller also receives the "3 tiles ahead" hazard warning: when a tile lands three
/// slots in front of this racer, the server calls <see cref="WarnHazard"/> on the owning
/// client only. After the warning fades, the player has to *remember* it.
/// </summary>
public partial class RacerController : Vehicle
{
	/// <summary>How many tiles ahead the racer is warned about a landing tile.</summary>
	public const int WarningLookahead = 3;

	/// <summary>Which peer owns/controls this car.</summary>
	[Export] public int OwnerPeerId { get; set; }

	/// <summary>Which input actions drive this car.</summary>
	[Export] public VehicleInputActions Actions { get; set; } = new();

	/// <summary>Track index the racer is currently on, tracked by the server.</summary>
	public int CurrentTrackIndex { get; private set; }

	[Signal] public delegate void HazardWarnedEventHandler(int trackIndex, int hazard, string hazardName);

	/// <summary>True on the machine whose player owns/controls this car.</summary>
	public bool IsLocalPlayer => OwnerPeerId == Multiplayer.GetUniqueId();

	public override void _Ready()
	{
		// Builds the axles and derives the suspension/brake setup. Must run before the first
		// physics step, and before anything reads the vehicle's state.
		base._Ready();

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
		// Only the owning peer reads input for its own car. Everyone else still runs the
		// physics so the car doesn't freeze — it just coasts on its last inputs until
		// transform replication lands.
		if (IsLocalPlayer)
		{
			VehicleInputState.Sample(Actions).ApplyTo(this, Actions);
		}

		base._PhysicsProcess(delta);
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
