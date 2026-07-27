using System.Collections.Generic;
using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using MasterTrack.Tiles;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The Track Master's side of the game: the top-down board view and the act of dropping tiles
/// onto the end of the track.
///
/// This is the *view and input* half — it owns the camera and the ghost preview, then hands an
/// intent to <see cref="TrackController"/>. It never places anything itself; the server decides
/// what is real.
///
/// Placement is a single click on the palette. A tile can only ever go on the head — the open
/// cell at the end of the track — so asking the Track Master to point at it was asking them to
/// tell the game something it already knew, in the middle of a live race. Hovering a tile in
/// the palette ghosts it onto the head; clicking places it there.
///
/// What they have to place comes from <see cref="TileHand"/> — a row of slots that fills itself
/// with weighted random tiles over time. The catalog is what exists in the game; the hand is
/// what the Track Master can actually reach for right now.
///
/// Because placement no longer aims at anything, the camera is free to be a camera. It has two
/// modes (see <see cref="BoardCameraMode"/>): by default it rides over the end of the track so
/// the Track Master's work is always in frame without them touching it, and on the toggle it
/// becomes a free-flying camera for going and looking at the race.
///
/// The board also carries a live marker per racer. From board altitude a car is a few pixels of
/// grey, which is no use to someone whose whole job is judging whether the track is beating
/// them — so each one gets a coloured chevron pointing the way it's travelling, drawn over
/// everything and held at a constant on-screen size however far the camera is zoomed out.
/// </summary>
public partial class TrackMasterController : Node3D
{
	/// <summary>How the board camera behaves.</summary>
	public enum BoardCameraMode
	{
		/// <summary>Rides over the end of the track, following it as it grows. The default:
		/// tiles land where the camera already is, so building needs no camera work at all.</summary>
		Follow,

		/// <summary>Flown by hand — WASD to move, hold the look button to aim.</summary>
		FreeRoam,
	}

	/// <summary>The track to build onto. Required.</summary>
	[Export] public TrackController? Track { get; set; }

	/// <summary>
	/// Starting height of the board camera, in metres. Expressed in tiles rather than as a
	/// flat number so the board keeps framing the same amount of *track* if the tile size
	/// changes — about five and a half cells across at the default field of view.
	/// </summary>
	[Export] public float CameraHeight { get; set; } = TrackTile.Size * 5.5f;

	/// <summary>Closest zoom: low enough to read the hazard on a single tile.</summary>
	[Export] public float MinCameraHeight { get; set; } = TrackTile.Size * 1.8f;

	/// <summary>Furthest zoom: high enough to see a long track's whole shape.</summary>
	[Export] public float MaxCameraHeight { get; set; } = TrackTile.Size * 14.0f;

	/// <summary>How many tiles the Track Master can have waiting at once.</summary>
	[Export] public int HandSlots { get; set; } = 6;

	/// <summary>
	/// How many tiles are already in hand when the match starts. Without an opening hand the
	/// Track Master spends the first <see cref="DealInterval"/> seconds with nothing to place
	/// while the racers are already moving.
	/// </summary>
	[Export] public int StartingTiles { get; set; } = 3;

	/// <summary>
	/// Seconds between dealt tiles. This is the tap the whole role runs on: a leading racer
	/// crosses a 40 m tile in about a second at speed, so anything much above that and the
	/// track cannot be kept ahead of them from the hand alone.
	/// </summary>
	[Export] public float DealInterval { get; set; } = 3.0f;

	/// <summary>Fraction of the current height added or removed per wheel notch.</summary>
	[Export] public float ZoomStep { get; set; } = 0.12f;

	/// <summary>
	/// How quickly the follow camera closes on the end of the track, per second. High enough to
	/// keep up with a run of tiles, slow enough that the board glides rather than snaps.
	/// </summary>
	[Export] public float FollowSpeed { get; set; } = 4.0f;

	/// <summary>Free-roam fly speed, in metres per second, before the wheel's multiplier.</summary>
	[Export] public float FreeMoveSpeed { get; set; } = TrackTile.Size * 2.5f;

	/// <summary>Radians of free-roam look per pixel of mouse movement.</summary>
	[Export] public float FreeLookSensitivity { get; set; } = 0.005f;

	/// <summary>How high above a car its board marker floats, in metres.</summary>
	[Export] public float MarkerHeight { get; set; } = 3.0f;

	/// <summary>
	/// How often the board re-checks who is racing, in seconds. Cars arrive once at the start of
	/// a match, so this doesn't need to be every frame — the markers themselves move every frame
	/// regardless, which is the part that has to look live.
	/// </summary>
	[Export] public float MarkerRosterInterval { get; set; } = 0.5f;

	/// <summary>Fired when the previewed tile changes, so the palette can say what will happen.</summary>
	[Signal] public delegate void PreviewChangedEventHandler(bool valid, string reason);

	/// <summary>Fired when the camera mode changes, as a <see cref="BoardCameraMode"/>. The
	/// toggle button reads its label off this rather than tracking the mode itself.</summary>
	[Signal] public delegate void CameraModeChangedEventHandler(int mode);

	/// <summary>
	/// Fired when the contents of the hand change — a tile dealt or spent. Deliberately not
	/// fired for the deal clock ticking: the tray reads that off <see cref="Hand"/> itself
	/// rather than being woken every frame to be told the same thing.
	/// </summary>
	[Signal] public delegate void HandChangedEventHandler();

	/// <summary>Which way the board camera is currently being driven.</summary>
	public BoardCameraMode CameraMode { get; private set; } = BoardCameraMode.Follow;

	/// <summary>
	/// The tiles waiting to be placed. Built on first use rather than in <c>_Ready</c>: the tray
	/// reads it while building itself, and which of the two runs first is a question about where
	/// the nodes sit in the scene — not something this should depend on.
	/// </summary>
	public TileHand Hand => _hand ??= new TileHand(HandSlots, DealInterval, StartingTiles);

	private TileHand? _hand;

	private Camera3D _camera = null!;
	private Node3D _headMarker = null!;
	private TrackTile? _ghost;

	/// <summary>Catalog index currently being previewed on the head, or -1 for none.</summary>
	private int _previewIndex = -1;

	/// <summary>Height the follow camera rides at. The wheel moves this, not the camera.</summary>
	private float _boardHeight;

	private float _freeYaw;
	private float _freePitch;
	private bool _looking;

	/// <summary>Wheel-driven multiplier on the free-roam fly speed.</summary>
	private float _freeSpeedScale = 1.0f;

	// Just short of straight up/down: at exactly vertical the yaw axis stops meaning anything
	// and the view rolls as it crosses over.
	private const float MaxFreePitch = Mathf.Pi * 0.5f - 0.01f;

	private const float MinFreeSpeedScale = 0.15f;
	private const float MaxFreeSpeedScale = 8.0f;

	private static readonly Color ValidTint = new(0.35f, 1.0f, 0.45f);
	private static readonly Color InvalidTint = new(1.0f, 0.35f, 0.35f);

	/// <summary>One board marker and the car it belongs to.</summary>
	private sealed class RacerMarker
	{
		public required RacerController Racer { get; init; }
		public required Node3D Root { get; init; }
	}

	private readonly List<RacerMarker> _markers = new();
	private float _rosterCountdown;

	public override void _Ready()
	{
		if (Track == null)
		{
			GD.PushError("[TrackMaster] No TrackController assigned; the builder is inert.");
			SetProcess(false);
			SetProcessUnhandledInput(false);
			return;
		}

		_boardHeight = CameraHeight;

		_camera = new Camera3D
		{
			Name = "BoardCamera",
			// Straight down: the board view reads the track's shape, not its scenery.
			RotationDegrees = new Vector3(-90, 0, 0),
			Position = Track.HeadWorldPosition + new Vector3(0, _boardHeight, 0),
			Current = true,
			Far = 4000.0f,
		};
		AddChild(_camera);

		_headMarker = BuildHeadMarker();
		AddChild(_headMarker);

		Track.TrackHeadChanged += OnTrackHeadChanged;
		OnTrackHeadChanged();

		// The board view is a mouse UI, not a driving view.
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	/// <summary>
	/// Called by the palette when the Track Master clicks a slot in their hand. The tile can
	/// only go in one place, so there is nothing to aim at — this is the whole gesture.
	///
	/// The tile is only spent if it would actually land: an illegal placement says why and
	/// leaves the hand alone rather than burning a slot the Track Master waited for.
	/// </summary>
	public void PlaceFromSlot(int slot)
	{
		if (Track == null)
			return;

		TileDefinition? definition = TileCatalog.At(Hand.At(slot));
		if (definition == null)
			return;

		// Checked here as well as on the server so an illegal tile says why on the spot,
		// rather than being silently dropped by the authority a round trip later.
		if (!Track.Grid.CanPlace(Track.Grid.HeadCell, definition.ToTileData(), out string reason))
		{
			EmitSignal(SignalName.PreviewChanged, false, reason);
			return;
		}

		int catalogIndex = Hand.Take(slot);

		// The hand has closed up behind the spent tile, so whatever slid into this slot is
		// what the cursor is now over. Set it before the placement, which walks the head
		// forward and rebuilds the ghost off it.
		_previewIndex = Hand.At(slot);

		Track.RequestPlaceTile(catalogIndex);
		EmitSignal(SignalName.HandChanged);
	}

	/// <summary>Called by the palette on hover: show a slot's tile on the head before committing.</summary>
	public void PreviewSlot(int slot)
	{
		int catalogIndex = Hand.At(slot);
		if (_previewIndex == catalogIndex)
			return;

		_previewIndex = catalogIndex;
		RefreshGhost();
	}

	public void ClearPreview()
	{
		_previewIndex = -1;
		ClearGhost();
	}

	public override void _Process(double delta)
	{
		if (Hand.Tick((float)delta))
			EmitSignal(SignalName.HandChanged);

		if (CameraMode == BoardCameraMode.Follow)
			UpdateFollow((float)delta);
		else
			UpdateFreeRoam((float)delta);

		UpdateRacerMarkers((float)delta);
	}

	// ---- Racer markers ----

	/// <summary>
	/// Keep a marker over every car. The roster is only re-read every
	/// <see cref="MarkerRosterInterval"/> seconds — cars arrive once per match, and scanning the
	/// group allocates — but the markers themselves are moved every frame, because "where are
	/// they right now" is the entire question the board is being asked.
	/// </summary>
	private void UpdateRacerMarkers(float delta)
	{
		_rosterCountdown -= delta;
		if (_rosterCountdown <= 0.0f)
		{
			_rosterCountdown = MarkerRosterInterval;
			RefreshRacerRoster();
		}

		foreach (RacerMarker marker in _markers)
			AimMarker(marker);
	}

	private void RefreshRacerRoster()
	{
		// Cars that have left, been freed, or are on their way out.
		for (int i = _markers.Count - 1; i >= 0; i--)
		{
			RacerController racer = _markers[i].Racer;
			if (IsInstanceValid(racer) && !racer.IsQueuedForDeletion())
				continue;

			_markers[i].Root.QueueFree();
			_markers.RemoveAt(i);
		}

		foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
		{
			if (node is not RacerController racer || HasMarkerFor(racer))
				continue;

			// The car's own paint, not a colour derived from the peer id. The chevron is only
			// useful if it matches the car the Track Master can see driving around underneath it.
			Node3D root = BuildRacerMarker(racer.PaintColor,
				racer.OwnerPeerId > 0 ? GameManager.Instance.NameOf(racer.OwnerPeerId) : "Racer");
			AddChild(root);

			_markers.Add(new RacerMarker { Racer = racer, Root = root });
		}
	}

	private bool HasMarkerFor(RacerController racer)
	{
		foreach (RacerMarker marker in _markers)
		{
			if (marker.Racer == racer)
				return true;
		}
		return false;
	}

	private void AimMarker(RacerMarker marker)
	{
		// The roster is only re-read every MarkerRosterInterval seconds, so a car that goes away
		// in between — a peer dropping out, or the match scene being torn down — leaves a marker
		// pointing at a freed object. Without this that is an exception every frame until the
		// next sweep, rather than a chevron that simply stops being drawn.
		if (!IsInstanceValid(marker.Racer) || !marker.Racer.IsInsideTree())
		{
			marker.Root.Visible = false;
			return;
		}

		marker.Root.Position = marker.Racer.GlobalPosition + new Vector3(0.0f, MarkerHeight, 0.0f);

		// Yaw off the car's forward axis flattened onto the board, rather than its euler Y —
		// a car that has rolled onto its roof reports euler angles that spin the marker around
		// while the car is still pointing the same way down the track.
		Vector3 forward = -marker.Racer.GlobalBasis.Z;
		var flat = new Vector2(forward.X, forward.Z);
		if (flat.LengthSquared() > 0.0001f)
			marker.Root.Rotation = new Vector3(0.0f, Mathf.Atan2(-flat.X, -flat.Y), 0.0f);

		// Constant on-screen size: at the closest zoom a fixed-size marker swamps the tile it's
		// on, and at the furthest it disappears into a pixel.
		float distance = _camera.GlobalPosition.DistanceTo(marker.Root.GlobalPosition);
		marker.Root.Scale = Vector3.One * Mathf.Clamp(distance / CameraHeight, 0.35f, 3.0f);
	}

	/// <summary>
	/// A chevron pointing along local -Z, the way the car is going, with the racer's name over
	/// it. Drawn with depth testing off on purpose — a marker that hides behind a tile wall or
	/// a descending tile goes missing exactly when the Track Master is aiming something at it.
	/// </summary>
	private static Node3D BuildRacerMarker(Color color, string label)
	{
		var root = new Node3D { Name = "RacerMarker" };

		var material = new StandardMaterial3D
		{
			AlbedoColor = color,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			NoDepthTest = true,
		};

		const float length = TrackTile.Size * 0.22f;
		const float width = TrackTile.Size * 0.055f;
		const float thickness = 0.4f;

		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(width, thickness, length), Material = material },
			Position = new Vector3(0.0f, 0.0f, length * 0.25f),
		});
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(width, thickness, length * 0.6f), Material = material },
			Position = new Vector3(-length * 0.19f, 0.0f, -length * 0.42f),
			RotationDegrees = new Vector3(0.0f, -38.0f, 0.0f),
		});
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(width, thickness, length * 0.6f), Material = material },
			Position = new Vector3(length * 0.19f, 0.0f, -length * 0.42f),
			RotationDegrees = new Vector3(0.0f, 38.0f, 0.0f),
		});

		root.AddChild(new Label3D
		{
			Text = label,
			Position = new Vector3(0.0f, 6.0f, 0.0f),
			PixelSize = 0.04f,
			FontSize = 96,
			OutlineSize = 28,
			Modulate = color,
			OutlineModulate = new Color(0.0f, 0.0f, 0.0f, 0.85f),
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			NoDepthTest = true,
		});

		return root;
	}

	// ---- Camera modes ----

	/// <summary>Flip between riding the end of the track and flying by hand.</summary>
	public void ToggleCameraMode()
		=> SetCameraMode(CameraMode == BoardCameraMode.Follow
						 ? BoardCameraMode.FreeRoam
						 : BoardCameraMode.Follow);

	public void SetCameraMode(BoardCameraMode mode)
	{
		if (mode == CameraMode)
			return;

		CameraMode = mode;

		if (mode == BoardCameraMode.FreeRoam)
		{
			// Pick up exactly where the follow camera left off, so the toggle doesn't jump.
			_freeYaw = _camera.Rotation.Y;
			_freePitch = Mathf.Clamp(_camera.Rotation.X, -MaxFreePitch, MaxFreePitch);
		}
		else
		{
			// Going back to Follow: drop the look, and let UpdateFollow ease the camera home
			// rather than cutting to it.
			StopLooking();
		}

		EmitSignal(SignalName.CameraModeChanged, (int)mode);
	}

	/// <summary>
	/// Ride over the end of the track. Eased rather than pinned, so a placed tile pulls the
	/// board along instead of teleporting it out from under the Track Master's eyes.
	/// </summary>
	private void UpdateFollow(float delta)
	{
		if (Track == null)
			return;

		float t = 1.0f - Mathf.Exp(-FollowSpeed * delta);

		_camera.Position = _camera.Position.Lerp(
			Track.HeadWorldPosition + new Vector3(0, _boardHeight, 0), t);

		// Per-angle rather than a plain lerp: coming back from free roam the yaw can be several
		// turns from zero, and a straight lerp would unwind all of it the long way round.
		_camera.Rotation = new Vector3(
			Mathf.LerpAngle(_camera.Rotation.X, -Mathf.Pi * 0.5f, t),
			Mathf.LerpAngle(_camera.Rotation.Y, 0.0f, t),
			0.0f);
	}

	/// <summary>Fly the camera: WASD along the way it's facing, look with the mouse held.</summary>
	private void UpdateFreeRoam(float delta)
	{
		var move = new Vector3(
			Input.GetActionStrength("builder_cam_right") - Input.GetActionStrength("builder_cam_left"),
			0.0f,
			Input.GetActionStrength("builder_cam_back") - Input.GetActionStrength("builder_cam_forward"));

		if (move == Vector3.Zero)
			return;

		// Relative to where the camera is pointing, so "forward" means into the screen however
		// the Track Master has it aimed.
		_camera.Position += _camera.Basis * move.Normalized()
							* (FreeMoveSpeed * _freeSpeedScale * delta);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Track == null)
			return;

		// Held rather than latched: the cursor has to stay free to reach the tray, which is
		// the only way tiles get placed.
		if (@event.IsActionPressed("camera_look") && CameraMode == BoardCameraMode.FreeRoam)
		{
			_looking = true;
			Input.MouseMode = Input.MouseModeEnum.Captured;
			return;
		}

		if (@event.IsActionReleased("camera_look"))
		{
			StopLooking();
			return;
		}

		if (@event is InputEventMouseButton mouse)
		{
			HandleWheel(mouse);
			return;
		}

		if (@event is InputEventMouseMotion motion && _looking)
			Look(motion.Relative);
	}

	private void Look(Vector2 relative)
	{
		_freeYaw -= relative.X * FreeLookSensitivity;
		_freePitch = Mathf.Clamp(_freePitch - relative.Y * FreeLookSensitivity,
								 -MaxFreePitch, MaxFreePitch);
		_camera.Rotation = new Vector3(_freePitch, _freeYaw, 0.0f);
	}

	private void StopLooking()
	{
		if (!_looking)
			return;

		_looking = false;
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	private void HandleWheel(InputEventMouseButton mouse)
	{
		switch (mouse.ButtonIndex)
		{
			case MouseButton.WheelUp:
				Zoom(-ZoomStep);
				break;

			case MouseButton.WheelDown:
				Zoom(ZoomStep);
				break;
		}
	}

	/// <summary>
	/// The wheel means "how far out am I" in Follow and "how fast am I flying" in free roam —
	/// there is no height to zoom when the camera is off the board and pointing sideways.
	/// </summary>
	private void Zoom(float amount)
	{
		if (CameraMode == BoardCameraMode.FreeRoam)
		{
			_freeSpeedScale = Mathf.Clamp(_freeSpeedScale * (1.0f - amount),
										  MinFreeSpeedScale, MaxFreeSpeedScale);
			return;
		}

		_boardHeight = Mathf.Clamp(_boardHeight * (1.0f + amount), MinCameraHeight, MaxCameraHeight);
	}

	// ---- Ghost preview ----

	/// <summary>
	/// Build the ghost on the head cell, or clear it if nothing is being previewed. Rebuilt
	/// when the previewed tile changes and again whenever the head moves — so the mouse can
	/// sit on one card and every click walks the preview along to the track's new end.
	/// </summary>
	private void RefreshGhost()
	{
		ClearGhost();

		if (Track == null || _previewIndex < 0)
			return;

		TileDefinition? definition = TileCatalog.At(_previewIndex);
		if (definition == null)
			return;

		bool valid = Track.Grid.CanPlace(Track.Grid.HeadCell, definition.ToTileData(), out string reason);
		EmitSignal(SignalName.PreviewChanged, valid,
				   valid ? $"{definition.DisplayName} — click to place it." : reason);

		_ghost = new TrackTile { Name = "TileGhost" };
		AddChild(_ghost);
		_ghost.Initialize(definition.ToTileData(), -1, Track.Grid.HeadCell, Track.Grid.HeadDirection,
						  Track.Grid.HeadHeight,
						  isGhost: true, ghostTint: valid ? ValidTint : InvalidTint);
	}

	private void ClearGhost()
	{
		if (_ghost == null)
			return;

		_ghost.QueueFree();
		_ghost = null;
	}

	// ---- Head marker ----

	private void OnTrackHeadChanged()
	{
		if (Track == null)
			return;

		_headMarker.Position = Track.HeadWorldPosition;
		_headMarker.Rotation = new Vector3(0.0f, Track.Grid.HeadDirection.Yaw(), 0.0f);

		// The head has moved out from under the ghost; put it back on the new one.
		RefreshGhost();
	}

	/// <summary>
	/// A translucent pad on the next open cell with an arrow through it. Without this the
	/// Track Master has no way to tell where the track is willing to grow.
	/// </summary>
	private static Node3D BuildHeadMarker()
	{
		var root = new Node3D { Name = "HeadMarker" };

		var padMaterial = new StandardMaterial3D
		{
			AlbedoColor = new Color(1.0f, 0.85f, 0.25f, 0.22f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(TrackTile.Size, 0.04f, TrackTile.Size), Material = padMaterial },
			Position = new Vector3(0, 0.02f, 0),
		});

		var arrowMaterial = new StandardMaterial3D
		{
			AlbedoColor = new Color(1.0f, 0.85f, 0.25f, 0.75f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		// Shaft plus a chevron, pointing along local -Z (the direction of travel). Sized off
		// the tile so the arrow keeps filling the cell it marks.
		const float bar = TrackTile.Size * 0.07f;
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh
			{
				Size = new Vector3(bar, 0.05f, TrackTile.Size * 0.5f),
				Material = arrowMaterial,
			},
			Position = new Vector3(0, 0.05f, TrackTile.Size * 0.05f),
		});
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh
			{
				Size = new Vector3(bar, 0.05f, TrackTile.Size * 0.26f),
				Material = arrowMaterial,
			},
			Position = new Vector3(TrackTile.Size * -0.085f, 0.05f, TrackTile.Size * -0.26f),
			RotationDegrees = new Vector3(0, -40, 0),
		});
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh
			{
				Size = new Vector3(bar, 0.05f, TrackTile.Size * 0.26f),
				Material = arrowMaterial,
			},
			Position = new Vector3(TrackTile.Size * 0.085f, 0.05f, TrackTile.Size * -0.26f),
			RotationDegrees = new Vector3(0, 40, 0),
		});

		return root;
	}
}
