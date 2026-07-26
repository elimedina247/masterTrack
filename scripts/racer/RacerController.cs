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
/// Input is read locally by the owning peer for responsiveness; every other peer sees this car
/// through a MultiplayerSynchronizer carrying its pose. That's what puts the racers on the Track
/// Master's board in real time, which the whole role depends on — they're trying to build a
/// track hard enough to stop these cars, and they can't judge that from a static board.
///
/// A car is simulated on exactly one machine: its owner's. Everywhere else it's a puppet, frozen
/// kinematic and slid toward the pose coming off the wire. Running the vehicle physics on a
/// remote car would only produce a second, disagreeing version of it to fight the network.
///
/// This controller also receives the "3 tiles ahead" hazard warning: when a tile lands three
/// slots in front of this racer, the server calls <see cref="WarnHazard"/> on the owning
/// client only. After the warning fades, the player has to *remember* it.
/// </summary>
public partial class RacerController : Vehicle
{
	/// <summary>How many tiles ahead the racer is warned about a landing tile.</summary>
	public const int WarningLookahead = 3;

	/// <summary>
	/// Group every racer joins. The Track Master's board finds the cars through this rather than
	/// a node path, so racers arriving by replication show up on the board without anything
	/// having to be told about them.
	/// </summary>
	public const string GroupName = "racers";

	/// <summary>Which peer owns/controls this car.</summary>
	[Export] public int OwnerPeerId { get; set; }

	/// <summary>Which input actions drive this car.</summary>
	[Export] public VehicleInputActions Actions { get; set; } = new();

	/// <summary>Replicated position, written by the owner and followed by everyone else.</summary>
	[Export] public Vector3 NetPosition { get; set; }

	/// <summary>
	/// Replicated orientation. A quaternion rather than euler angles so a remote car can be
	/// slerped through a barrel roll off a jump without the angles unwrapping the long way.
	/// </summary>
	[Export] public Quaternion NetRotation { get; set; } = Quaternion.Identity;

	/// <summary>How quickly a remote car closes on its replicated pose, per second.</summary>
	[Export] public float RemoteSmoothing { get; set; } = 18.0f;

	/// <summary>
	/// Past this far from the replicated pose a remote car cuts instead of sliding. Covers the
	/// first update after spawning and any respawn, either of which would otherwise be a long
	/// glide across the board.
	/// </summary>
	[Export] public float RemoteSnapDistance { get; set; } = 25.0f;

	/// <summary>How often the owner pushes its pose, in seconds.</summary>
	[Export] public float SyncInterval { get; set; } = 1.0f / 30.0f;

	/// <summary>
	/// Whether the nitro is burning, as told to everyone else. Replicated alongside the pose.
	///
	/// A remote car is frozen and never simulated, so its own <see cref="Vehicle.IsNitroActive"/>
	/// is permanently false — without this, every exhaust flame in a race would be invisible to
	/// everyone but the driver making it.
	/// </summary>
	[Export] public bool NetNitro { get; set; }

	/// <summary>
	/// Whether this car is burning nitro, from whichever source is authoritative for it: its own
	/// simulation if we are driving it, the replicated flag if someone else is. What the cosmetic
	/// effects should read.
	/// </summary>
	public bool IsBoosting => IsRemote ? NetNitro : IsNitroActive;

	/// <summary>Track index the racer is currently on, tracked by the server.</summary>
	public int CurrentTrackIndex { get; private set; }

	[Signal] public delegate void HazardWarnedEventHandler(int trackIndex, int hazard, string hazardName);

	/// <summary>True on the machine whose player owns/controls this car.</summary>
	public bool IsLocalPlayer => OwnerPeerId == Multiplayer.GetUniqueId();

	/// <summary>
	/// Real networked play. Solo runs on Godot's implicit offline peer, where there is nobody to
	/// replicate to and every car is simulated locally exactly as it always was.
	/// </summary>
	private static bool IsNetworked => NetworkManager.Instance.IsNetworked;

	/// <summary>Somebody else's car on this machine: a puppet driven by the wire, not by physics.</summary>
	private bool IsRemote => IsNetworked && !IsLocalPlayer;

	/// <summary>
	/// Assemble a freshly instantiated car: who owns it, where it starts, and the pose channel
	/// it will talk over. Called by <see cref="MasterTrack.Game.RacerArena"/>'s spawn function on
	/// every peer, the server included.
	///
	/// Deliberately before the car enters the tree. Godot needs a
	/// <see cref="MultiplayerSynchronizer"/> to already exist, with its authority already
	/// settled, by the time the spawn it belongs to is processed — leaving either until
	/// <c>_Ready</c> is an error it reports at runtime, and quietly costs the car its pose.
	/// </summary>
	public void PrepareForSpawn(int peerId, Vector3 position)
	{
		// Name = peer id as well, so a copy can still recover its owner from the node name.
		Name = peerId.ToString();
		OwnerPeerId = peerId;
		Position = position;

		if (!IsNetworked)
			return;

		// Seed the pose before anyone can read it, so a remote copy has somewhere real to
		// start from instead of the world origin.
		NetPosition = position;
		NetRotation = Basis.GetRotationQuaternion();

		AddChild(BuildSynchronizer());

		// The owning peer is the movement authority for its own car. Recursive by default, so
		// this covers the synchronizer added just above.
		SetMultiplayerAuthority(peerId);
	}

	public override void _Ready()
	{
		// Builds the axles and derives the suspension/brake setup. Must run before the first
		// physics step, and before anything reads the vehicle's state.
		base._Ready();

		// How the board finds this car. Everything else about the marker is the board's business.
		AddToGroup(GroupName);

		if (IsRemote)
		{
			// Kinematic rather than static: the pose is assigned rather than simulated, but
			// the car should still shove anything it lands on.
			FreezeMode = FreezeModeEnum.Kinematic;
			Freeze = true;
		}

		// Hand the camera to the local player only.
		GetNodeOrNull<CameraRig>("CameraRig")?.SetActive(IsLocalPlayer);
	}

	/// <summary>
	/// The pose channel. Built in code rather than authored into the scene so the property list
	/// can't drift away from the fields it names — a typo'd path in a .tscn replicates nothing
	/// and says nothing about it.
	/// </summary>
	private MultiplayerSynchronizer BuildSynchronizer()
	{
		var config = new SceneReplicationConfig();

		// Relative to the synchronizer's root path, which defaults to its parent — this car.
		foreach (string property in new[] { ":NetPosition", ":NetRotation", ":NetNitro" })
		{
			config.AddProperty(property);
			config.PropertySetSpawn(property, true);
			// Always rather than OnChange: a moving car changes every frame anyway, and this
			// keeps a dropped packet from leaving a remote copy parked.
			config.PropertySetReplicationMode(property, SceneReplicationConfig.ReplicationMode.Always);
		}

		return new MultiplayerSynchronizer
		{
			Name = "PoseSync",
			ReplicationConfig = config,
			ReplicationInterval = SyncInterval,
		};
	}

	public override void _PhysicsProcess(double delta)
	{
		// A remote car isn't driven, it's told. Running the vehicle simulation as well would
		// just be a second opinion for the network to keep overruling.
		if (IsRemote)
		{
			FollowNetworkPose((float)delta);
			return;
		}

		// Only the owning peer reads input for its own car.
		if (IsLocalPlayer)
			VehicleInputState.Sample(Actions).ApplyTo(this, Actions);

		base._PhysicsProcess(delta);

		if (IsNetworked)
		{
			NetPosition = GlobalPosition;
			NetRotation = GlobalBasis.GetRotationQuaternion();
			NetNitro = IsNitroActive;
		}
	}

	/// <summary>
	/// Slide this puppet toward the pose its owner last sent. Smoothed rather than assigned
	/// outright because the pose arrives at <see cref="SyncInterval"/>, which is well under the
	/// frame rate — snapping to each update is what makes networked cars look like they're
	/// stuttering rather than driving.
	/// </summary>
	private void FollowNetworkPose(float delta)
	{
		if (GlobalPosition.DistanceSquaredTo(NetPosition) > RemoteSnapDistance * RemoteSnapDistance)
		{
			GlobalPosition = NetPosition;
			GlobalBasis = new Basis(NetRotation);
			return;
		}

		float t = 1.0f - Mathf.Exp(-RemoteSmoothing * delta);
		GlobalPosition = GlobalPosition.Lerp(NetPosition, t);
		GlobalBasis = new Basis(GlobalBasis.GetRotationQuaternion().Slerp(NetRotation, t));
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
