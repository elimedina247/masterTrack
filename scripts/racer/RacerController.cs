using Godot;
using MasterTrack.Audio;
using MasterTrack.Networking;
using MasterTrack.Tiles;
using MasterTrack.Vehicles;

namespace MasterTrack.Racer;

/// <summary>
/// A Racer's car in third person. The driving comes from <see cref="Vehicle"/> — a hovercraft on
/// four ray-cast springs, with drift and chained boosts — so this class is only about *who* is
/// driving and what they're told.
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

	/// <summary>Which of the three models this car wears. See <see cref="CarVariants"/>.</summary>
	[Export] public int VariantIndex { get; set; } = CarVariants.DefaultVariantIndex;

	/// <summary>Where the whip antenna is mounted. See <see cref="CarVariants.AntennaSpots"/>.</summary>
	[Export] public int AntennaSpot { get; set; } = CarVariants.DefaultAntennaSpot;

	/// <summary>
	/// The colour on its bodywork — this player's identity for the whole session. The Track
	/// Master's chevron reads this off the car, so the board and the road always agree.
	/// </summary>
	[Export] public Color PaintColor { get; set; } = CarVariants.DefaultPaint;

	/// <summary>Which input actions drive this car.</summary>
	[Export] public VehicleInputActions Actions { get; set; } = new();

	/// <summary>
	/// Whether <see cref="Respawn"/> also hands the nitro charges back.
	///
	/// Off everywhere but the proving ground. Charges are meant to last a whole run, so a reset
	/// that refilled them would make deliberately wrecking the car the cheapest way to get five
	/// more — the reset exists so a bad landing doesn't end someone's race, not as a pit stop.
	///
	/// On the pad none of that applies. There is no run to spend them over, and having to leave
	/// and re-enter the lobby to try a jump on a fresh set of charges is nothing but a walk.
	/// </summary>
	[Export] public bool RefillNitroOnReset { get; set; }

	/// <summary>Replicated position, written by the owner and followed by everyone else.</summary>
	[Export] public Vector3 NetPosition { get; set; }

	/// <summary>
	/// Replicated orientation. A quaternion rather than euler angles so a remote car can be
	/// slerped through a barrel roll off a jump without the angles unwrapping the long way.
	/// </summary>
	[Export] public Quaternion NetRotation { get; set; } = Quaternion.Identity;

	/// <summary>
	/// Replicated velocity, written by the owner alongside the pose.
	///
	/// A puppet is frozen kinematic and slid by assignment, so as far as the local physics is
	/// concerned it has no velocity at all — and car-to-car impact response is built entirely on
	/// <i>relative</i> velocity. Without this, a 200 km/h T-bone and a parking-lot nudge are the
	/// same event. See <see cref="ProcessCarContacts"/>.
	/// </summary>
	[Export] public Vector3 NetVelocity { get; set; }

	/// <summary>
	/// How fast this car is really going, from whichever source knows: the simulation if it runs
	/// here, the wire if it runs somewhere else. What impact response reads off the *other* car.
	/// </summary>
	public Vector3 EffectiveVelocity => IsRemote ? NetVelocity : LinearVelocity;

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
	/// Whether this car is boosting, from whichever source is authoritative for it: its own
	/// simulation if we are driving it, the replicated flag if someone else is. What the cosmetic
	/// effects should read.
	/// </summary>
	public override bool IsBoosting => IsRemote ? NetNitro : base.IsBoosting;

	/// <summary>
	/// How often the recovery pose is refreshed, in seconds. Coarse on purpose: it wants to be
	/// where you *were driving*, not the instant before you got it wrong.
	/// </summary>
	private const float RecoveryPoseInterval = 0.25f;

	/// <summary>How square to the ground the car must be for a pose to count as recoverable.</summary>
	private const float RecoveryUprightDot = 0.7f;

	/// <summary>Ground rays that must be touching something for a pose to count as recoverable.</summary>
	private const int RecoveryGroundedWheels = 3;

	/// <summary>
	/// How many recorded poses deep the recovery memory goes. A reset uses the <i>oldest</i>, not
	/// the newest: the last pose before a fall is routinely the lip of the road itself — wheels
	/// down, upright, and half a metre from the drop — and putting the car back there just feeds
	/// it to the same edge again, with no new pose ever recorded on the way down to break the
	/// loop. Four samples at <see cref="RecoveryPoseInterval"/> reaches about a second back up
	/// the road, which is far enough from the edge to actually drive away from.
	/// </summary>
	private const int RecoveryPoseDepth = 4;

	/// <summary>
	/// The last few places this car was upright on solid ground, oldest first. A reset puts it
	/// back at the front of the queue — see <see cref="RecoveryPoseDepth"/> for why not the back.
	/// </summary>
	private readonly System.Collections.Generic.Queue<Transform3D> _recoveryPoses = new();

	private float _recoveryCountdown;

	/// <summary>
	/// Slower than this with no wheel on anything counts as stuck, in m/s. Free fall at 2 g blows
	/// past it within a tenth of a second, and the moment of zero speed at the top of a hop is a
	/// moment, so nothing a car does on purpose stays under it for long.
	/// </summary>
	private const float StuckSpeedThreshold = 1.0f;

	// ---- Kill plane ----
	//
	// The track is the only terrain there is, so anything this far below its lowest tile is in
	// the void with nothing left to land on. Checked against the track's own floor rather than a
	// fixed height because the track descends — a fixed plane would either kill cars on a diving
	// section or let a fall off the start line run for half a minute.

	/// <summary>How far below the lowest standing tile a car may fall before it is respawned,
	/// in metres. A couple of seconds of falling: long enough to sell the drop, short enough
	/// that nobody watches clouds go by.</summary>
	private const float KillPlaneMargin = 40.0f;

	/// <summary>The track whose floor is checked. Found once through the group.</summary>
	private TrackController? _killTrack;
	private bool _killTrackSearched;

	// ---- Elimination ----
	//
	// A fall is not always survivable. During a match — and only there — some falls take the car
	// out of the race instead of putting it back: in Sentry mode every trip past the kill plane
	// is fatal (being knocked off the road is the sentry's whole kit working), and in Live Build
	// the fatal fall is the one where the road you were on has already crumbled away, because
	// there is nothing left to respawn onto. The lobby and the proving ground never eliminate;
	// the flag below is only ever set by the match scene.

	/// <summary>
	/// Whether a fall can end this car's race. Off by default and switched on per-car by the
	/// match scene, which is what keeps death a gamemode rule rather than a physics rule — the
	/// lobby pad and the playtest area never set it, so nothing there can kill you.
	/// </summary>
	public bool EliminationEnabled { get; set; }

	/// <summary>Set the moment this machine reports its own death, so the frames spent falling
	/// while the broadcast makes its round trip don't report it again.</summary>
	private bool _eliminationReported;

	/// <summary>
	/// The last tile this car actually stood on, or -1 before it has stood on any. Cached because
	/// <see cref="CurrentTrackIndex"/> reads the wheels and answers -1 the moment the car is
	/// airborne — and the fall is precisely when the fatality rule needs to know what road the
	/// car fell <i>from</i>.
	/// </summary>
	private int _lastGroundedTileIndex = -1;

	/// <summary>Whether this copy of the car has been switched off by an elimination.</summary>
	public bool IsEliminated { get; private set; }

	/// <summary>
	/// How long a car must be stuck before it is rescued, in seconds. Long enough that no live
	/// state trips it; short enough that being wedged never reads as the game breaking.
	/// </summary>
	private const float StuckRescueTime = 1.25f;

	private float _stuckTime;

	/// <summary>
	/// Which tile the car is standing on, or -1 in the air.
	///
	/// Read off the wheels rather than looked up in a grid. It used to be
	/// <c>TrackGrid.TileAtWorld</c> — a cell lookup — and when the track came off the grid there was
	/// no lattice left to look anything up in. That turned out to be an improvement: the car already
	/// collides with exactly one <see cref="TrackTile"/> body, so the wheel that is touching it knows
	/// the answer exactly, including on the tiles a cell was always going to be vague about — a
	/// hairpin doubling back under itself, or a bridge over another part of the track.
	///
	/// Takes the first wheel with an answer. Straddling a seam, that is whichever corner is on the
	/// older tile, which is the conservative way round for a hazard warning: it is better to be told
	/// about the tile ahead a moment early than a moment late.
	/// </summary>
	public int CurrentTrackIndex
	{
		get
		{
			foreach (GroundRay wheel in WheelArray)
			{
				if (wheel.IsGrounded && wheel.LastCollider is TrackTile tile)
					return tile.TrackIndex;
			}

			return -1;
		}
	}

	[Signal] public delegate void HazardWarnedEventHandler(int trackIndex, int hazard, string hazardName);

	/// <summary>
	/// True on the machine whose player owns/controls this car.
	///
	/// The tree check is not paranoia. <see cref="Node.Multiplayer"/> comes from the scene tree, so
	/// it is null on a node that has been taken out of one — and a car that has been despawned or
	/// is going down with its scene stays a live object for a while yet. Anything still holding a
	/// reference to it and asking a question per frame (the HUD does exactly that, guarded only by
	/// <c>IsInstanceValid</c>, which is true of a detached node) would otherwise throw for every
	/// frame of the teardown.
	/// </summary>
	public bool IsLocalPlayer => IsInsideTree() && OwnerPeerId == Multiplayer.GetUniqueId();

	/// <summary>
	/// Whether this car reads the keyboard. Almost always yes — the exception is the lobby, where
	/// the player can put the car down and go and build instead.
	///
	/// It has to be a switch rather than something the car works out for itself, because the board
	/// camera flies on the same WASD the car drives on: without this, lining up a tile would have
	/// the car doing donuts across the pad behind you.
	/// </summary>
	public bool AcceptsInput
	{
		get => _acceptsInput;
		set
		{
			if (_acceptsInput == value)
				return;

			_acceptsInput = value;

			// Let go of the controls on the way out. Input is pushed onto the vehicle each step
			// rather than polled by it, so simply stopping would leave whatever was last pressed
			// held down forever — and a car left at full throttle is not a car left alone.
			if (!value)
				VehicleInputState.Idle.ApplyTo(this);
		}
	}

	private bool _acceptsInput = true;

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
	public void PrepareForSpawn(int peerId, Vector3 position, RacerAppearance appearance)
	{
		// Name = peer id as well, so a copy can still recover its owner from the node name.
		Name = peerId.ToString();
		OwnerPeerId = peerId;
		Position = position;

		VariantIndex = appearance.VariantIndex;
		AntennaSpot = appearance.AntennaSpot;
		PaintColor = appearance.Paint;
		ApplyVariant();

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

		// Deliberately after that hand-over, so the gate keeps the server's authority while the
		// pose channel goes to the driver. See BuildSpawnGate for why the server needs one.
		AddChild(BuildSpawnGate());
	}

	/// <summary>
	/// Put this car's model on the rig: the body, and the four rims.
	///
	/// The rims are scaled rather than taken as they come. Each variant's wheels were modelled at
	/// a different radius (see <c>assets/cars/README.md</c>) while <c>GroundRay.cs</c> positions
	/// the hub without ever scaling it, so an unscaled rim renders sunk into the road or floating
	/// above it. Scaling to each ray's own <see cref="GroundRay.TireRadius"/> means all three
	/// variants sit right *and* handle identically — which is the point for this playtest. Random
	/// assignment of different handling would confound exactly what the playtest is measuring:
	/// you could not tell feedback about the physics from feedback about which car someone drew.
	///
	/// Note the wheels are pure decoration now: they carry no tire model, no spin and no drive.
	/// Their radius only decides where the mesh sits relative to the contact point.
	/// </summary>
	private void ApplyVariant()
	{
		CarVariants.Variant variant = CarVariants.At(VariantIndex);

		// Identity, deliberately: the old CC96 rotation fudge is wrong for every model in
		// assets/cars/, which are authored nose along Blender +Y and arrive facing -Z already.
		if (GetNodeOrNull<Node3D>("BodyRig") is { } bodyRig)
			SwapModel(bodyRig, "BodyModel", variant.BodyPath, Transform3D.Identity);

		// The wheels hang off the posed shell, not off the ground rays — see WheelVisual.
		SwapRim("BodyRig/WheelFLHub", "RimFL", variant.RimLeftPath, variant.ModelledFrontRadius);
		SwapRim("BodyRig/WheelFRHub", "RimFR", variant.RimRightPath, variant.ModelledFrontRadius);
		SwapRim("BodyRig/WheelRLHub", "RimRL", variant.RimLeftPath, variant.ModelledRearRadius);
		SwapRim("BodyRig/WheelRRHub", "RimRR", variant.RimRightPath, variant.ModelledRearRadius);

		// The antenna is authored at the back-centre spot; players may have picked another.
		if (GetNodeOrNull<Node3D>("BodyRig/Antenna") is { } antenna)
			antenna.Position = CarVariants.AntennaSpotAt(AntennaSpot);
	}

	/// <summary>
	/// Swap one rim, keeping the node's own orientation. All four rim nodes carry the same axle
	/// rotation and the left/right mirroring is baked into the assets, so that transform is the
	/// asset convention rather than anything per-variant — it must survive the swap.
	///
	/// The rim is scaled to the hub's own <see cref="WheelVisual.TireRadius"/>, which is also what
	/// decides where the hub sits above the road, so every variant's wheels end up the same size
	/// and sitting right whatever radius they were modelled at.
	/// </summary>
	private void SwapRim(string hubPath, string rimName, string scenePath, float modelledRadius)
	{
		if (GetNodeOrNull<Node3D>(hubPath) is not { } hub)
			return;

		float radius = hub is WheelVisual wheel ? wheel.TireRadius : 0.3f;
		float scale = modelledRadius > 0.0f ? radius / modelledRadius : 1.0f;

		Transform3D axle = hub.GetNodeOrNull<Node3D>(rimName)?.Transform ?? Transform3D.Identity;
		SwapModel(hub, rimName, scenePath, axle.Scaled(new Vector3(scale, scale, scale)));
	}

	private static void SwapModel(Node parent, string childName, string scenePath, Transform3D transform)
	{
		if (parent.GetNodeOrNull(childName) is { } previous)
		{
			// Renamed before freeing: QueueFree defers, and the new child cannot take a name
			// the outgoing one is still holding.
			previous.Name = $"{childName}_old";
			parent.RemoveChild(previous);
			previous.QueueFree();
		}

		var scene = GD.Load<PackedScene>(scenePath);
		if (scene == null)
		{
			GD.PushError($"[Racer] Could not load car model {scenePath}.");
			return;
		}

		var model = scene.Instantiate<Node3D>();
		model.Name = childName;
		model.Transform = transform;
		parent.AddChild(model);
	}

	public override void _Ready()
	{
		// Builds the axles and derives the suspension/brake setup. Must run before the first
		// physics step, and before anything reads the vehicle's state.
		base._Ready();

		// Explicitly, and after the variant swap. FlatShade is a child, so Godot has already run
		// its _Ready against whatever model the scene shipped with — by now that model is gone.
		GetNodeOrNull<FlatShade>("FlatShade")?.Restyle(PaintColor);

		// How the board finds this car. Everything else about the marker is the board's business.
		AddToGroup(GroupName);

		if (IsRemote)
		{
			// Kinematic rather than static: the pose is assigned rather than simulated, but
			// the car should still shove anything it lands on.
			FreezeMode = FreezeModeEnum.Kinematic;
			Freeze = true;
		}
		else
		{
			// The simulated car watches for other racers arriving in its paintwork. Only the
			// simulated one: a puppet never reacts to anything locally, so paying for contact
			// reporting on it would buy nothing.
			ContactMonitor = true;
			MaxContactsReported = 8;
		}

		// Hand the camera to the local player only.
		GetNodeOrNull<CameraRig>("CameraRig")?.SetActive(IsLocalPlayer);

		// Somewhere to go back to before we have ever been anywhere.
		_recoveryPoses.Enqueue(GlobalTransform);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// Only ever your own car, and only the machine that simulates it. A remote copy is a
		// puppet; shoving it about here would just be overruled by the next pose off the wire.
		// And only while somebody is actually driving it — a reset fired from the board would
		// teleport a car the player cannot currently see.
		if (!IsLocalPlayer || IsRemote || !AcceptsInput || !@event.IsActionPressed(Actions.Reset))
			return;

		Respawn();
		GetViewport().SetInputAsHandled();
	}

	/// <summary>
	/// Put this car back where it was last driving, upright and stopped.
	///
	/// No RPC and no server involvement: a car is simulated on exactly one machine, and that
	/// machine is the authority for its pose, so moving it locally is already the authoritative
	/// answer — the new pose goes out on the next sync like any other. <see cref="NetPosition"/>
	/// is written straight away as well so remote copies cut to it rather than gliding across
	/// the board (see <see cref="RemoteSnapDistance"/>).
	///
	/// This exists because there is no win condition to end a round on. Without it a single bad
	/// landing takes a player out for the rest of the session.
	/// </summary>
	public void Respawn()
	{
		Transform3D pose = _recoveryPoses.Count > 0 ? _recoveryPoses.Peek() : GlobalTransform;

		Rid rid = GetRid();
		PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Transform, pose);
		PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.LinearVelocity, Vector3.Zero);
		PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.AngularVelocity, Vector3.Zero);

		// The history starts over from here. The newer entries lead back toward whatever went
		// wrong, so a second reset before any new ground is covered should land *here* again,
		// not walk forward through the queue toward the edge.
		_recoveryPoses.Clear();
		_recoveryPoses.Enqueue(pose);

		NetPosition = pose.Origin;
		NetRotation = pose.Basis.GetRotationQuaternion();
		NetVelocity = Vector3.Zero;

		// Set back down stopped and *clear-headed* — a reset that kept the stun would put the
		// car on the road with no grip and someone else's spin still fading out of it.
		ClearImpactStun();

		// Charges back, on the pad only. ResetNitro also cancels whatever boost was burning, which
		// is what a car being set back down stopped should have anyway.
		if (RefillNitroOnReset)
			ResetNitro();
	}

	/// <summary>
	/// Remember where the car was, while it is somewhere worth going back to.
	///
	/// Deliberately the last place it was <i>driving</i> rather than a fixed spawn point: on a
	/// track the Track Master is still building, the start line can be a long way behind you, and
	/// being sent back there for one bad landing is its own punishment. Requires most of the
	/// wheels down and the car roughly the right way up, so the pose recorded is never mid-roll.
	/// </summary>
	private void UpdateRecoveryPose(float delta)
	{
		_recoveryCountdown -= delta;
		if (_recoveryCountdown > 0.0f)
			return;

		_recoveryCountdown = RecoveryPoseInterval;

		if (!IsVehicleReady || GlobalBasis.Y.Dot(Vector3.Up) < RecoveryUprightDot)
			return;

		if (GroundedRayCount < RecoveryGroundedWheels)
			return;

		_recoveryPoses.Enqueue(GlobalTransform);
		while (_recoveryPoses.Count > RecoveryPoseDepth)
			_recoveryPoses.Dequeue();
	}

	/// <summary>
	/// Rescue a car that physics has orphaned. The tell is unmistakable: no wheel touching
	/// anything <i>and</i> not actually moving, held for over a second. A driving car is
	/// grounded; a flying car is fast; a car that is neither is wedged in something — historically
	/// the road itself, when a hard landing tunnelled the chassis through the collision skin and
	/// left the ground rays staring at backfaces. <see cref="Vehicle"/> now sweeps its collision
	/// (ContinuousCd) so that particular grave should be sealed, but this is the guarantee the
	/// player actually feels: nothing the physics does can take the car away for good.
	///
	/// Beached counts too — belly on a barrier, all four corners in the air — and that is a
	/// feature, not a coincidence: every state this catches is one the player cannot drive out of.
	/// </summary>
	private void UpdateStuckWatchdog(float delta)
	{
		bool stuck = IsVehicleReady && IsAirborne && Speed < StuckSpeedThreshold;
		_stuckTime = stuck ? _stuckTime + delta : 0.0f;

		if (_stuckTime < StuckRescueTime)
			return;

		_stuckTime = 0.0f;
		GD.Print($"[Racer {OwnerPeerId}] Stuck (airborne, stationary {StuckRescueTime:0.##}s) — auto-respawn.");
		Respawn();
	}

	/// <summary>
	/// Respawn a car that has fallen past the bottom of the world. Runs only on the machine
	/// simulating this car, like the stuck watchdog above it: <see cref="Respawn"/> is already
	/// the authoritative local answer, and everyone else sees the result over the wire.
	/// </summary>
	private void UpdateKillPlane()
	{
		if (!_killTrackSearched)
		{
			_killTrackSearched = true;
			_killTrack = GetTree().GetFirstNodeInGroup(TrackController.GroupName) as TrackController;
		}

		if (_killTrack == null || !IsInstanceValid(_killTrack))
			return;

		if (GlobalPosition.Y >= _killTrack.LowestTileY - KillPlaneMargin)
			return;

		if (FallIsFatal())
		{
			if (_eliminationReported)
				return;

			_eliminationReported = true;
			GD.Print($"[Racer {OwnerPeerId}] Fell with nothing to go back to — out of the race.");

			// The report goes up; the car is only actually switched off when the confirmation
			// comes back down to every peer at once — see MarkEliminated. Until then it simply
			// keeps falling, which is also what everyone watching is seeing.
			GameManager.Instance.ReportSelfEliminated();
			return;
		}

		GD.Print($"[Racer {OwnerPeerId}] Fell {KillPlaneMargin:0}m below the lowest tile — respawn.");
		Respawn();
	}

	/// <summary>
	/// Whether this fall ends the race for this car. Only ever true inside a match (see
	/// <see cref="EliminationEnabled"/>), never once the match has concluded, and never for a
	/// car that already crossed the line — a finisher who then sails off the world has finished.
	/// </summary>
	private bool FallIsFatal()
	{
		if (!EliminationEnabled || IsEliminated)
			return false;

		GameManager manager = GameManager.Instance;

		if (manager.WinnerPeerId != 0 || manager.MatchUnfinished || manager.HasFinished(OwnerPeerId))
			return false;

		// The phased modes: while the race runs, the void is the void. Everything the builder
		// owns that throws, drags or launches a car is lethal exactly when it puts you off the
		// road — which is the whole reason placement is a skill. A turret's rocket earns the
		// same right as a sentry's missile.
		if (manager.IsPhasedMode)
			return manager.Phase == MatchPhase.Racing;

		// Live Build: fatal only when the road this car was on has already crumbled away. An
		// ordinary tumble off live road keeps the respawn it has always had; falling behind the
		// track's moving tail is the one fall with nothing left under it. Judged off the last
		// tile the wheels touched, because by the time the kill plane fires the car has been
		// airborne for seconds and the wheels have long since stopped answering.
		return _killTrack != null && _lastGroundedTileIndex >= 0
			   && _lastGroundedTileIndex < _killTrack.Grid.OldestLiveIndex;
	}

	/// <summary>
	/// Switch this copy of the car off: the elimination broadcast landed, on every peer at once.
	/// The car is not freed — freeing is the server's spawner's business and nothing here needs
	/// it — it simply stops being part of the race: invisible, solid to nothing, simulated by
	/// nobody, and out of the racers group so the board's markers, the finish sweep and the
	/// sentry's blasts all forget it in one move.
	/// </summary>
	public void MarkEliminated()
	{
		if (IsEliminated)
			return;

		IsEliminated = true;

		RemoveFromGroup(GroupName);
		Visible = false;
		AcceptsInput = false;
		CollisionLayer = 0;
		CollisionMask = 0;
		SetPhysicsProcess(false);
		SetProcess(false);

		if (!IsRemote)
		{
			FreezeMode = FreezeModeEnum.Kinematic;
			Freeze = true;
		}

		// The dead player's own camera is handed over to the spectator by the match scene; the
		// rig only needs to let go.
		GetNodeOrNull<CameraRig>("CameraRig")?.SetActive(false);
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
		foreach (string property in new[] { ":NetPosition", ":NetRotation", ":NetVelocity", ":NetNitro" })
		{
			config.AddProperty(property);
			config.PropertySetSpawn(property, true);
			// Always rather than OnChange: a moving car changes every frame anyway, and this
			// keeps a dropped packet from leaving a remote copy parked.
			config.PropertySetReplicationMode(property, SceneReplicationConfig.ReplicationMode.Always);
		}

		var sync = new MultiplayerSynchronizer
		{
			Name = "PoseSync",
			ReplicationConfig = config,
			ReplicationInterval = SyncInterval,
		};

		// The same gate the car carries below, on this channel too. On the server's *own* car
		// this synchronizer is the one the replication layer asks first, and a "yes" here settles
		// the question before the gate is ever consulted.
		sync.AddVisibilityFilter(Callable.From<int, bool>(HasSceneFor));
		return sync;
	}

	/// <summary>
	/// A synchronizer that replicates nothing at all. It exists only for its <i>visibility</i>,
	/// which is the single lever Godot gives the server over <b>when</b> a car's spawn packet is
	/// sent to a given peer.
	///
	/// Without it the engine pushes every car that already exists at a peer the instant that peer
	/// connects — while it is still on the main menu, with no lobby scene loaded. A spawn packet
	/// names its spawner by a cached id, so the client is first handed the path to cache; it
	/// cannot resolve <c>TestArea/RacerArena/RacerSpawner</c> in a scene it hasn't loaded, so it
	/// never confirms the id — and the server, having offered that path once, never offers it
	/// again. Every later spawn to that peer is then dropped, <i>including its own car</i>. The
	/// result is a player standing in the lobby with no car, and since the camera rides on the
	/// car, no camera either: a grey screen. That is the bug this exists to prevent, and it is
	/// the same one <c>GameManager</c>'s scene-ready handshake was written for — the handshake
	/// governs what the game spawns, and this governs what the engine sends unasked.
	///
	/// Its authority is left as the server's, because a synchronizer is only consulted on the
	/// machine that owns it, and the machine deciding what to send is the server. The pose
	/// channel belongs to the driver instead, which is why it cannot do this job alone: for a
	/// client's car the server owns none of it, and the answer defaults to "send it".
	/// </summary>
	private MultiplayerSynchronizer BuildSpawnGate()
	{
		var gate = new MultiplayerSynchronizer
		{
			Name = "SpawnGate",
			// An empty config rather than none: a synchronizer without one is an error the
			// replication layer reports on every sync tick for as long as the car exists.
			ReplicationConfig = new SceneReplicationConfig(),
			// It has nothing to say, so it never needs a turn in which to say it.
			ReplicationInterval = GateSyncInterval,
		};

		gate.AddVisibilityFilter(Callable.From<int, bool>(HasSceneFor));
		return gate;
	}

	/// <summary>How often the gate is given a sync slot, in seconds. Long, because it carries
	/// no properties — see <see cref="BuildSpawnGate"/>.</summary>
	private const float GateSyncInterval = 60.0f;

	/// <summary>
	/// Whether <paramref name="peerId"/> has the scene this car lives in loaded, and so can be
	/// sent a car at all. Only the server tracks that; everywhere else the question is not this
	/// machine's to answer and the honest reply is yes — a client evaluating this is deciding
	/// where to send its own car's pose, which has nothing to do with anybody's loading.
	/// </summary>
	private static bool HasSceneFor(int peerId) =>
		!NetworkManager.Instance.IsHost || GameManager.Instance.IsSceneReady(peerId);

	/// <summary>
	/// Server only. Re-ask whether this car may be shown to a peer. Called when that peer's scene
	/// finally turns up, because the gate's answer has just changed and the car is already
	/// standing on the pad — nothing else would go back and look.
	/// </summary>
	public void RefreshSpawnVisibility(int peerId)
	{
		foreach (Node child in GetChildren())
			(child as MultiplayerSynchronizer)?.UpdateVisibility(peerId);
	}

	public override void _PhysicsProcess(double delta)
	{
		// Before the remote/local split: the sentry's debuffs mark every copy of a car (the
		// aura has to glow on every screen), and the method sorts out which copy does physics.
		UpdateDebuffs((float)delta);

		// A remote car isn't driven, it's told. Running the vehicle simulation as well would
		// just be a second opinion for the network to keep overruling.
		if (IsRemote)
		{
			FollowNetworkPose((float)delta);
			return;
		}

		// Only the owning peer reads input for its own car, and only while it is being driven.
		// The debuffs get the last word on what the driver "said" — see ApplyInputDebuffs.
		if (IsLocalPlayer && AcceptsInput)
		{
			VehicleInputState.Sample(Actions).ApplyTo(this);
			ApplyInputDebuffs();
		}

		base._PhysicsProcess(delta);

		// Remembered while the wheels still know it; read back when they no longer do.
		int standingOn = CurrentTrackIndex;
		if (standingOn >= 0)
			_lastGroundedTileIndex = standingOn;

		UpdateRecoveryPose((float)delta);
		UpdateStuckWatchdog((float)delta);
		UpdateKillPlane();

		if (IsNetworked)
		{
			NetPosition = GlobalPosition;
			NetRotation = GlobalBasis.GetRotationQuaternion();
			NetVelocity = LinearVelocity;
			NetNitro = IsNitroActive;
		}
	}

	/// <summary>
	/// When each opposing car may next trigger an impulse, in ticks. See <see cref="Vehicle.ImpactCooldown"/>.
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<ulong, ulong> _impactCooldownUntil = new();

	public override void _IntegrateForces(PhysicsDirectBodyState3D state)
	{
		base._IntegrateForces(state);

		if (!IsRemote && IsVehicleReady)
			ProcessCarContacts(state);
	}

	/// <summary>
	/// Car-to-car impact response — the takeout layer. See docs/vehicle-physics.md.
	///
	/// The solver's own contact response still runs and is what makes *leaning* on another car
	/// work: a puppet advancing along its wire pose genuinely shoves this body. But the solver
	/// sees a puppet as an unlimited mass with no velocity, so an actual collision comes out as
	/// bouncing off scenery — no momentum exchange, no drama, and above all no consequence for
	/// the car that got hit. This layer adds the movie on top: launch, pop, spin, stun.
	///
	/// <b>Each machine only ever touches its own car.</b> Both sides of a hit detect the same
	/// contact — here against the other car's puppet, on the other machine against ours — and
	/// each applies its own share of the reaction, computed from the same replicated data. No
	/// authority changes hands, nothing is negotiated, and the two responses agree because the
	/// rule is symmetric even though the shares are not: <see cref="Vehicle.ImpactRammerScale"/>
	/// decides who was the victim, and the victim is the one who gets launched.
	/// </summary>
	private void ProcessCarContacts(PhysicsDirectBodyState3D state)
	{
		int contacts = state.GetContactCount();
		if (contacts == 0)
			return;

		ulong now = Time.GetTicksMsec();

		for (var i = 0; i < contacts; i++)
		{
			if (state.GetContactColliderObject(i) is not RacerController other)
				continue;

			// One impulse per opposing car per cooldown, or a grind along someone's door would
			// re-fire the takeout every physics step.
			ulong otherId = other.GetInstanceId();
			if (_impactCooldownUntil.TryGetValue(otherId, out ulong until) && now < until)
				continue;

			// The direction this car gets pushed. The reported normal's sign convention is not
			// worth trusting across physics backends; centre-to-centre settles which way is out.
			Vector3 normal = state.GetContactLocalNormal(i);
			if (normal.LengthSquared() < 0.5f)
				continue;
			if (normal.Dot(GlobalPosition - other.GlobalPosition) < 0.0f)
				normal = -normal;

			// Closing speed along the contact, from what each car is *really* doing — the wire
			// velocity for a puppet, the simulation for anything simulated here.
			Vector3 theirVelocity = other.EffectiveVelocity;
			float closing = (theirVelocity - state.LinearVelocity).Dot(normal);

			// A bouncy car launches on any touch — the minimum-speed gate is exactly the thing
			// the debuff exists to ignore.
			bool bouncy = other.IsBouncyActive;
			if (closing < ImpactMinSpeed && !bouncy)
				continue;

			// Who ran into whom. Their motion toward us versus our motion toward them decides
			// how much of the reaction this car deserves: all of it if we were minding our own
			// business, ImpactRammerScale of it if we did the ramming. Head-on lands in the
			// middle and wrecks everybody, which is correct.
			float theirShare = Mathf.Max(theirVelocity.Dot(normal), 0.0f);
			float ourShare = Mathf.Max(-state.LinearVelocity.Dot(normal), 0.0f);
			float victimFactor = theirShare + ourShare > 0.001f
				? theirShare / (theirShare + ourShare)
				: 0.5f;
			float receive = Mathf.Lerp(ImpactRammerScale, 1.0f, victimFactor);

			// Touching a bumper: you are always the victim, the hit is never soft, and it is
			// scaled up on top. The bouncy car itself feels nothing extra — it is the wall.
			float bounceScale = 1.0f;
			if (bouncy)
			{
				closing = Mathf.Max(closing, BouncyLaunchSpeed);
				receive = 1.0f;
				bounceScale = BouncyBounceScale;
			}

			float severity = Mathf.Clamp(closing / ImpactFullSpeed, 0.0f, 1.0f) * receive;
			_impactCooldownUntil[otherId] = now + (ulong)(ImpactCooldown * 1000.0f);

			// The launch, and the pop that lifts it off the road — where the airborne rules
			// (no grip, no drive, a projectile) take over selling it.
			state.LinearVelocity += normal * (closing * ImpactBounce * receive * bounceScale)
									+ Vector3.Up * (closing * ImpactPop * receive * bounceScale);

			// The spin, signed by where the hit landed relative to the centre of mass — the
			// same sign an off-centre impulse would earn from r × J, just paid at a rate that
			// was chosen instead of inherited. Clip a rear quarter, watch a pirouette.
			Vector3 contactPoint = state.GetContactLocalPosition(i);
			Vector3 lever = contactPoint - GlobalTransform * CenterOfMass;
			float spinSign = Mathf.Sign(lever.Cross(normal).Dot(Vector3.Up));
			if (spinSign == 0.0f)
				spinSign = 1.0f;

			state.AngularVelocity += Vector3.Up * (spinSign * ImpactSpinRate * severity);

			RegisterImpact(severity, contactPoint);

			if (bouncy)
				Sfx.PlayAt(this, BouncySfxPath, contactPoint,
						   volumeDb: 2.0f, unitSize: 15.0f, pitchJitter: 0.08f);
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
