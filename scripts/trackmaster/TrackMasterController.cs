using System.Collections.Generic;
using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using MasterTrack.Tiles;

// Godot has a TileData of its own — TileMap's, nothing to do with ours. Inside MasterTrack.Tiles the
// local declaration wins on its own; out here the two using directives are peers and the name is
// ambiguous, so say which one this file means.
using TileData = MasterTrack.Tiles.TileData;

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
/// <see cref="FreeBuild"/> takes that away and offers the whole catalog instead, spending nothing.
/// That is the difference between the board as a *role* and the board as a *tool*: the lobby uses
/// it to lay a track out on purpose, where being dealt what you get is only in the way. Everything
/// else — the ghost, the head, the legality check, the camera — is the same either way, which is
/// the point of keeping the two apart in <see cref="CatalogIndexAt"/> and nowhere else.
///
/// It also does not necessarily own the screen. In a match it does, and
/// <see cref="ActiveOnReady"/> is left true; in the lobby the camera belongs to the car until
/// somebody asks for the board, so everything the board holds is handed over and taken back by
/// <see cref="SetActive"/>.
///
/// Because placement no longer aims at anything, the camera is free to be a camera. It has two
/// modes (see <see cref="BoardCameraMode"/>): by default it rides over the end of the track so
/// the Track Master's work is always in frame without them touching it, and on the toggle it
/// becomes a free-flying camera for going and looking at the race.
///
/// The board also carries a live marker per racer. From board altitude a car is a few pixels of
/// grey, which is no use to someone whose whole job is judging whether the track is beating
/// them — so each one gets its name floated over it, drawn over everything and held at a
/// constant on-screen size however far the camera is zoomed out. The arrowhead itself comes
/// from the car: every remote car already wears a <see cref="RacerChevron"/>, and the board
/// sees it the same way the road does.
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

		/// <summary>
		/// Rides over the middle of the racers — the average of where they all are, top down,
		/// re-centred every frame as they spread and bunch. The sentry's view: their targets
		/// are cars, not the end of the track, and this keeps the whole pack in frame without
		/// flying anything. Skipped by the toggle while there are no cars to centre on.
		/// </summary>
		Pack,

		/// <summary>
		/// Transient: rides over the spot a just-confirmed sentry action is about to pay off at,
		/// then hands back to whatever mode was on before. Never entered by the toggle — only by
		/// a confirmed placement — and <i>any</i> input from the builder ends it instantly. A
		/// camera that takes the wheel during a fight is worse than no camera help at all.
		/// </summary>
		Watch,
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

	/// <summary>
	/// Ignore the hand and offer the whole catalog, with nothing spent when a tile is placed.
	///
	/// This is the difference between the role and the tool. In a match the hand is the game —
	/// the Track Master is handed what they get and has to make it hurt. In the lobby there is no
	/// race to pressure, and waiting three seconds to find out whether you are allowed a hairpin
	/// is not a design constraint, it is just waiting.
	/// </summary>
	[Export] public bool FreeBuild { get; set; }

	/// <summary>
	/// Whether the board takes over the moment this loads. True in a match, where the Track Master
	/// has nothing else to be looking at; false in the lobby, where the camera belongs to the car
	/// until somebody asks for the board.
	/// </summary>
	[Export] public bool ActiveOnReady { get; set; } = true;

	/// <summary>How many tiles the Track Master can have waiting at once.</summary>
	[Export] public int HandSlots { get; set; } = 6;

	/// <summary>
	/// How many tiles are already in hand when the match starts. Without an opening hand the
	/// Track Master spends the first <see cref="DealInterval"/> seconds with nothing to place
	/// while the racers are already moving.
	/// </summary>
	[Export] public int StartingTiles { get; set; } = 3;

	/// <summary>
	/// Seconds between dealt tiles. This is the tap the whole role runs on, and it is bolted to
	/// <see cref="TileCatalog.TileSize"/>: the average tile over this interval is the rate the
	/// track grows at, and that rate has to beat the racer's <c>TopSpeed</c> or a clean driver
	/// runs off the end of the road the hand can lay.
	///
	/// Weighted across the catalog a deal is now worth about 135 m — most tiles are 108, the seven
	/// that need the room are 162, and corners are worth their arc rather than their footprint — so
	/// 2.4 s supplies about 56 m/s against a 55.6 m/s top speed.
	///
	/// <b>That margin is nearly gone, and this is the number that runs out first.</b> Every metre
	/// taken off a tile has to come back as a shorter interval, and the interval is bounded by a
	/// human being reading a hand and clicking — not by anything that can be tuned. If the tiles get
	/// shorter again, this is the wall, and the way past it is fewer tiles that carry nothing rather
	/// than more tiles per second.
	/// </summary>
	[Export] public float DealInterval { get; set; } = 2.4f;

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
	///
	/// The head's height goes in as a function rather than a number: the hand outlives every
	/// placement, and it is read at the moment a card is dealt. Without a track it reads as zero,
	/// which is the right answer for an opening hand — the race starts on the deck.
	/// </summary>
	public TileHand Hand => _hand ??= new TileHand(HandSlots, DealInterval, StartingTiles,
												   () => Track?.Grid.HeadHeight ?? 0.0f,
												   StaplesAvailable);

	private TileHand? _hand;

	/// <summary>
	/// Whether the always-available pieces — a straight and the two corners — are on offer.
	///
	/// <b>The one place this is decided</b>, because two things depend on the same answer and a
	/// disagreement between them is a bug either way round: the palette draws the staples bar off
	/// it, and <see cref="TileHand"/> bars those pieces from the deal off it. If the bar showed
	/// without the deck knowing, the hand would keep dealing cards the builder already had for
	/// free; if the deck knew without the bar showing, they could not be reached at all.
	///
	/// Sentry only, and free build never — see the note in <c>TilePalette.BuildStapleTray</c> for
	/// why Live Build's hand has to stay the whole supply.
	/// </summary>
	public bool StaplesAvailable
		=> !FreeBuild && GameManager.Instance.Mode == GameMode.Sentry;

	/// <summary>
	/// How many cards the tray has: the hand's slots, or one per catalog tile in free build.
	/// </summary>
	public int TrayLength => FreeBuild ? TileCatalog.All.Count : Hand.SlotCount;

	/// <summary>
	/// The catalog index a tray position is offering, or <see cref="TileHand.Empty"/> for none.
	///
	/// The one place the two modes differ, and the reason nothing else has to know which is in
	/// play: in free build the tray *is* the catalog, so a position and an index are the same
	/// number, and in a match the hand decides.
	/// </summary>
	public int CatalogIndexAt(int slot)
	{
		if (!FreeBuild)
			return Hand.At(slot);

		return slot >= 0 && slot < TileCatalog.All.Count ? slot : TileHand.Empty;
	}

	/// <summary>Whether the board currently owns the camera and the mouse.</summary>
	public bool IsActive { get; private set; }

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

	/// <summary>
	/// What the board says once the race's tiles have all been laid. Deliberately not phrased as a
	/// rejection: the Track Master has not done anything wrong, the race is simply over as far as
	/// building goes, and everything from here is down to whether the racers get there.
	/// </summary>
	private const string OutOfTilesMessage = "That's the whole race. The bar at the end is the finish now.";

	/// <summary>One board marker and the car it belongs to.</summary>
	private sealed class RacerMarker
	{
		public required RacerController Racer { get; init; }
		public required Node3D Root { get; init; }
	}

	private readonly List<RacerMarker> _markers = new();
	private float _rosterCountdown;

	// ---- Follow-through camera ----

	/// <summary>Where the Watch camera is looking: the confirmed action's target.</summary>
	private Vector3 _watchTarget;

	/// <summary>Seconds of watching left before the camera goes home on its own.</summary>
	private float _watchRemaining;

	/// <summary>The mode the Watch borrowed the camera from, restored when it ends.</summary>
	private BoardCameraMode _watchReturnMode = BoardCameraMode.Pack;

	/// <summary>
	/// The height the Watch rides at, low enough that the consequence fills the frame. The
	/// sentry's own zoom wins when it is already closer — a watch that zoomed <i>out</i> would
	/// be taking the shot away from someone already leaning in.
	/// </summary>
	private float WatchHeight => Mathf.Min(_boardHeight, TrackTile.Size * 3.2f);

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
			Current = false,
			Far = 4000.0f,
		};
		AddChild(_camera);

		_headMarker = BuildHeadMarker();
		AddChild(_headMarker);

		Track.TrackHeadChanged += OnTrackHeadChanged;
		OnTrackHeadChanged();

		SetActive(ActiveOnReady);
	}

	/// <summary>
	/// Hand the board the camera and the mouse, or give them back.
	///
	/// Everything the board owns goes at once — the camera, the per-frame work, the input, and the
	/// furniture it draws on the world. Leaving the head marker up over a track nobody is building
	/// would be a yellow arrow hanging in the air in the middle of someone's drive.
	/// </summary>
	public void SetActive(bool active)
	{
		IsActive = active;

		_camera.Current = active;
		SetProcess(active);
		SetProcessUnhandledInput(active);
		_headMarker.Visible = active;

		if (active)
		{
			// The board view is a mouse UI, not a driving view.
			Input.MouseMode = Input.MouseModeEnum.Visible;
			return;
		}

		// Going away: drop anything that was mid-gesture, so coming back starts clean rather than
		// with a stale ghost on a head that has since moved.
		StopLooking();
		ClearPreview();
		CancelHazardPlacement();
		CancelSentryAction();
		EndSentryWatch();
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
		if (!TryPlaceCatalogIndex(CatalogIndexAt(slot)))
			return;

		// Free build spends nothing, so the card under the cursor is still offering the same tile
		// afterwards. In a match the hand closes up behind the spent tile, and whatever slid into
		// this slot is what the cursor is now over — set before the placement, which walks the
		// head forward and rebuilds the ghost off it.
		if (!FreeBuild)
			Hand.Take(slot);

		_previewIndex = CatalogIndexAt(slot);

		EmitSignal(SignalName.HandChanged);
	}

	/// <summary>
	/// Place one of <see cref="TileCatalog.StapleIndexes"/> — the straight and the two corners
	/// that are always on offer. The same placement as a card, minus the spending: a staple is
	/// not in the hand, so there is no slot to take it out of and nothing closes up behind it.
	/// </summary>
	public void PlaceStaple(int catalogIndex)
	{
		if (TryPlaceCatalogIndex(catalogIndex))
			_previewIndex = catalogIndex;
	}

	/// <summary>
	/// The part of a placement both doors share: check the tile could actually land, then ask for
	/// it. Returns whether the request went out, so the caller knows whether to spend anything.
	///
	/// Both checks are run here as well as on the server, and in this order, so an illegal tile
	/// says why on the spot rather than being silently dropped by the authority a round trip
	/// later — and so a refusal never costs the Track Master a card they waited for.
	/// </summary>
	private bool TryPlaceCatalogIndex(int catalogIndex)
	{
		if (Track == null)
			return false;

		TileDefinition? definition = TileCatalog.At(catalogIndex);
		if (definition == null)
			return false;

		// The race is as long as the host said it was. The Track Master has run out of track,
		// not out of tiles.
		if (Track.AtTileLimit)
		{
			EmitSignal(SignalName.PreviewChanged, false, OutOfTilesMessage);
			return false;
		}

		if (!Track.Grid.CanPlace(definition.ToTileData(), out string reason))
		{
			EmitSignal(SignalName.PreviewChanged, false, reason);
			return false;
		}

		Track.RequestPlaceTile(catalogIndex);
		return true;
	}

	/// <summary>
	/// Take the last tile back off the end of the track. The track decides whether that is allowed
	/// at all — it is a lobby thing, not a match thing — and whether this peer may ask.
	/// </summary>
	public void UndoLastTile()
	{
		Track?.RequestRemoveTile();
	}

	/// <summary>Called by the palette on hover: show a slot's tile on the head before committing.</summary>
	public void PreviewSlot(int slot) => PreviewCatalogIndex(CatalogIndexAt(slot));

	/// <summary>Ghost a catalog tile onto the head. The hover half of both doors — a card in the
	/// hand and a staple are the same preview once the index is known.</summary>
	public void PreviewCatalogIndex(int catalogIndex)
	{
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
		// No deal clock in free build — there is no hand to fill, and ticking one would be a
		// countdown nobody is waiting on.
		if (!FreeBuild && Hand.Tick((float)delta, IsPlaceable))
			EmitSignal(SignalName.HandChanged);

		if (CameraMode == BoardCameraMode.FreeRoam)
			UpdateFreeRoam((float)delta);
		else
			UpdateFollow((float)delta);

		UpdateRacerMarkers((float)delta);
		UpdateSentryAim();
		UpdateHazardGhost();
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

	/// <summary>
	/// The middle of everybody: the average of every live car's position, which is what the
	/// Pack camera hovers over. The average includes height, so a pack riding an elevated
	/// stretch keeps the same apparent zoom as one at sea level. Falls back to the head of the
	/// track when nobody is racing — the one honest answer a board with no cars on it has.
	/// </summary>
	private Vector3 PackCenter()
	{
		Vector3 sum = Vector3.Zero;
		int count = 0;

		foreach (RacerMarker marker in _markers)
		{
			if (!IsInstanceValid(marker.Racer) || !marker.Racer.IsInsideTree())
				continue;

			sum += marker.Racer.GlobalPosition;
			count++;
		}

		return count > 0 ? sum / count : Track!.HeadWorldPosition;
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
		// next sweep, rather than a chevron that simply stops being drawn. An eliminated car is
		// hidden rather than freed, so its name goes with it the same way — a label floating
		// over an empty piece of void would be a ghost on the board.
		if (!IsInstanceValid(marker.Racer) || !marker.Racer.IsInsideTree()
			|| !marker.Racer.Visible)
		{
			marker.Root.Visible = false;
			return;
		}

		marker.Root.Visible = true;

		marker.Root.Position = marker.Racer.GlobalPosition + new Vector3(0.0f, MarkerHeight, 0.0f);

		// No aiming: the label is billboarded, and the direction of travel is the car's own
		// RacerChevron's job. Constant on-screen size still matters — at the closest zoom a
		// fixed-size name swamps the tile it's on, and at the furthest it disappears.
		float distance = _camera.GlobalPosition.DistanceTo(marker.Root.GlobalPosition);
		marker.Root.Scale = Vector3.One * Mathf.Clamp(distance / CameraHeight, 0.35f, 3.0f);
	}

	/// <summary>
	/// The racer's name, and nothing else. The car already carries its own arrowhead — the
	/// <see cref="RacerChevron"/> triangle every remote car wears, which the board sees the same
	/// way the road does — so a second arrow here was the same information drawn twice, on top
	/// of itself. What the chevron cannot say is <i>who</i>, and that is the half the board
	/// keeps: the name, in the player's colour, drawn with depth testing off so it never goes
	/// missing behind a wall exactly when the Track Master is aiming something at it.
	/// </summary>
	private static Node3D BuildRacerMarker(Color color, string label)
	{
		var root = new Node3D { Name = "RacerMarker" };

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

	/// <summary>
	/// Take the camera to a spot the sentry's shot is about to pay off at, for a few seconds or
	/// until the builder touches anything. Entered through <see cref="SetCameraMode"/> like every
	/// other mode; the return mode is only captured on the way <i>in</i>, so back-to-back shots
	/// extend the watch rather than making Watch its own return address.
	/// </summary>
	public void BeginSentryWatch(Vector3 target, float seconds)
	{
		_watchTarget = target;
		_watchRemaining = seconds;

		if (CameraMode != BoardCameraMode.Watch)
		{
			_watchReturnMode = CameraMode;
			SetCameraMode(BoardCameraMode.Watch);
		}
	}

	/// <summary>Hand the camera back to whatever the sentry was doing before the shot.</summary>
	public void EndSentryWatch()
	{
		if (CameraMode != BoardCameraMode.Watch)
			return;

		_watchRemaining = 0.0f;
		SetCameraMode(_watchReturnMode);
	}

	/// <summary>
	/// Step to the next camera mode: track, pack, free roam, round again. Pack only offers
	/// itself while somebody is racing — over an empty board it is just Follow with extra steps.
	/// Pressed during a Watch it simply ends the watch: the toggle is input, and input wins.
	/// </summary>
	public void ToggleCameraMode()
	{
		if (CameraMode == BoardCameraMode.Watch)
		{
			EndSentryWatch();
			return;
		}

		BoardCameraMode next = CameraMode switch
		{
			BoardCameraMode.Follow => BoardCameraMode.Pack,
			BoardCameraMode.Pack => BoardCameraMode.FreeRoam,
			_ => BoardCameraMode.Follow,
		};

		if (next == BoardCameraMode.Pack && _markers.Count == 0)
			next = BoardCameraMode.FreeRoam;

		SetCameraMode(next);
	}

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
	/// Ride over the point this mode cares about — the end of the track, the middle of the
	/// pack, or the spot a shot is landing on. Eased rather than pinned, so a placed tile (or a
	/// pack spreading out) pulls the board along instead of teleporting it out from under the
	/// Track Master's eyes.
	/// </summary>
	private void UpdateFollow(float delta)
	{
		if (Track == null)
			return;

		// The watch runs its own clock here, because this is the only per-frame work the mode
		// does: when the moment has been seen, the camera eases home by itself.
		if (CameraMode == BoardCameraMode.Watch)
		{
			_watchRemaining -= delta;
			if (_watchRemaining <= 0.0f)
				EndSentryWatch();
		}

		Vector3 focus = CameraMode switch
		{
			BoardCameraMode.Pack => PackCenter(),
			BoardCameraMode.Watch => _watchTarget,
			_ => Track.HeadWorldPosition,
		};

		float height = CameraMode == BoardCameraMode.Watch ? WatchHeight : _boardHeight;
		float t = 1.0f - Mathf.Exp(-FollowSpeed * delta);

		_camera.Position = _camera.Position.Lerp(
			focus + new Vector3(0, height, 0), t);

		// Per-angle rather than a plain lerp: coming back from free roam the yaw can be several
		// turns from zero, and a straight lerp would unwind all of it the long way round.
		_camera.Rotation = new Vector3(
			Mathf.LerpAngle(_camera.Rotation.X, -Mathf.Pi * 0.5f, t),
			Mathf.LerpAngle(_camera.Rotation.Y, 0.0f, t),
			0.0f);
	}

	/// <summary>Fly the camera: WASD along the way it's facing, Space and Shift straight up and
	/// down, look with the mouse held.</summary>
	private void UpdateFreeRoam(float delta)
	{
		var move = new Vector3(
			Input.GetActionStrength("builder_cam_right") - Input.GetActionStrength("builder_cam_left"),
			0.0f,
			Input.GetActionStrength("builder_cam_back") - Input.GetActionStrength("builder_cam_forward"));

		// Vertical is world up and down rather than camera-local: rising is about getting over
		// the track, and which way the camera happens to be aimed has nothing to say about it.
		float lift = Input.GetActionStrength("builder_cam_up")
					 - Input.GetActionStrength("builder_cam_down");

		// WASD stays relative to where the camera is pointing, so "forward" means into the screen
		// however the Track Master has it aimed.
		Vector3 direction = _camera.Basis * move + Vector3.Up * lift;
		if (direction.LengthSquared() < 1e-6f)
			return;

		_camera.Position += direction.Normalized()
							* (FreeMoveSpeed * _freeSpeedScale * delta);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Track == null)
			return;

		// Any deliberate input ends a follow-through watch on the spot — and then keeps going,
		// so the keystroke that ended the watch is also the keystroke that does its job. Mouse
		// motion deliberately doesn't count; nudging the mouse is not a decision.
		if (CameraMode == BoardCameraMode.Watch
			&& @event is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true })
			EndSentryWatch();

		// An armed sentry action owns the mouse buttons until it fires or is cancelled.
		if (HandleSentryInput(@event))
			return;

		// Then an armed hazard, the same way.
		if (HandleHazardInput(@event))
			return;

		if (@event.IsActionPressed("builder_undo"))
		{
			UndoLastTile();
			return;
		}

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

		bool valid = false;
		string message = OutOfTilesMessage;

		if (!Track.AtTileLimit)
		{
			valid = Track.Grid.CanPlace(definition.ToTileData(), out string reason);
			message = valid ? $"{definition.DisplayName} — click to place it." : reason;
		}

		EmitSignal(SignalName.PreviewChanged, valid, message);

		_ghost = new TrackTile { Name = "TileGhost" };
		AddChild(_ghost);
		_ghost.Initialize(definition.ToTileData(), -1, Track.Grid.HeadAnchor,
						  isGhost: true, ghostTint: valid ? ValidTint : InvalidTint,
						  entryFrame: Track.Grid.HeadFrame);
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
		_headMarker.Rotation = new Vector3(0.0f, Track.Grid.HeadAnchor.Yaw, 0.0f);

		// The head moving is the only thing that changes which tiles are playable, so it is the
		// only moment the hand can go dead. Rescue it here rather than on a timer: waiting out the
		// deal interval would be a hundred metres of road with nothing being built onto it.
		if (!FreeBuild && Hand.TopUp(IsPlaceable))
			EmitSignal(SignalName.HandChanged);

		// The head has moved out from under the ghost; put it back on the new one.
		RefreshGhost();
	}

	/// <summary>
	/// Whether a catalog tile could go on the track right now.
	///
	/// Handed to <see cref="TileHand"/> so it can keep at least one playable tile in front of the
	/// Track Master. The hand deals cards and does not know what a track is; this is the whole of
	/// what it needs to be told. Reads the shared <see cref="TileCatalog.DataAt"/> rather than
	/// building a <c>TileData</c>, because the hand asks this of every slot it holds.
	/// </summary>
	private bool IsPlaceable(int catalogIndex)
	{
		if (Track == null || Track.AtTileLimit)
			return false;

		TileData? data = TileCatalog.DataAt(catalogIndex);
		return data != null && Track.Grid.CanPlace(data, out _);
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
