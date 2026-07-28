using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using MasterTrack.Tiles;
using MasterTrack.TrackMaster;
using MasterTrack.Vehicles;

namespace MasterTrack.Game;

/// <summary>
/// The lobby's buildable track, and the switch between driving and building it.
///
/// The start tile is a fixed anchor off the north edge of the pad — one straight, always in the
/// same place, laid by the <see cref="TrackController"/> in the scene. Everything past it is
/// whatever gets built this lobby, and it lives exactly as long as the lobby does: the tiles are
/// nodes in this scene, so leaving for the menu or into a match frees them along with everything
/// else. Nothing is saved and nothing needs clearing — the next lobby is a new scene with a bare
/// start tile.
///
/// The track built here is a sandbox, not a race. A match starts from its own starting straight
/// with the Track Master dealing tiles under pressure; that is a different mode and it inherits
/// none of this.
///
/// Building is the host's alone. There is exactly one head — the single open cell the track grows
/// from — so two people building is two people fighting over one cell, and the lobby has no Track
/// Master assigned to arbitrate (<see cref="GameManager.TrackMasterPeerId"/> is 0 until a match
/// hands the role out). Everyone else sees the track appear as it is placed.
/// </summary>
public partial class PhysicsTestArea
{
	/// <summary>
	/// Cell the buildable track's start tile sits on, and the way it runs: off the north edge of
	/// the pad, mirroring the fixed course off the south edge, so the two grow away from each
	/// other and can never meet in the middle.
	/// </summary>
	private static readonly Vector2I BuildStartCell = new(0, -15);

	private const TrackDirection BuildStartDirection = TrackDirection.North;

	/// <summary>Colour of the start line and its gate. Racing white, against the grey of the pad.</summary>
	private static readonly Color StartLineColor = new(0.94f, 0.94f, 0.90f);

	/// <summary>
	/// How far the pad has to reach to meet the start tile. Same shape as
	/// <see cref="PadEdgeForRaceTrack"/> and for the same reason: off the pad is open air, and a
	/// start line you can only arrive at by falling is no start line.
	/// </summary>
	private static float PadEdgeForBuildableTrack
		=> Mathf.Abs(BuildStartCell.Y * TileCatalog.TileSize) - TileCatalog.TileSize * 0.5f;

	private TrackController? _track;
	private TrackMasterController? _builder;
	private Control? _palette;

	/// <summary>True while the board has the screen. See <see cref="SetBuildMode"/>.</summary>
	private bool _building;

	/// <summary>
	/// Whether this peer may build. Solo is always yes; in a session it is the host — the same rule
	/// the server enforces when a placement arrives, checked here as well so a client is never
	/// handed a board it would only be refused from.
	/// </summary>
	private static bool MayBuild
		=> !NetworkManager.Instance.IsNetworked || NetworkManager.Instance.IsHost;

	/// <summary>
	/// Point the scene's track at the start cell and put it in lobby mode: the host builds it,
	/// tiles can be taken back, and one straight is the whole starting track.
	///
	/// Has to happen in <c>_EnterTree</c>, not <c>_Ready</c>. A node is readied after its children,
	/// so by the time this class is ready the track has already laid its starting straight — at
	/// whatever cell it was still carrying. Entering the tree runs the other way round, parent
	/// first, which is the only window where these settings still count.
	///
	/// Set from here rather than in the .tscn because <see cref="BuildStartCell"/> also decides how
	/// far the pad has to reach. Two places to change it is one place to forget.
	/// </summary>
	private void ConfigureBuildableTrack()
	{
		_track = GetNodeOrNull<TrackController>("Track");
		if (_track == null)
			return;

		_track.StartCell = BuildStartCell;
		_track.StartDirection = BuildStartDirection;
		_track.StartingStraightLength = 1;
		_track.Authority = TrackController.BuildAuthority.Host;
		_track.AllowUndo = true;
	}

	/// <summary>Find the board and the tray, and start the player in the car.</summary>
	private void SetUpBuilder()
	{
		_builder = GetNodeOrNull<TrackMasterController>("TrackMaster");
		_palette = GetNodeOrNull<Control>("HUD/TilePalette");

		if (_track == null || _builder == null)
		{
			GD.PushError("[TestArea] No Track or TrackMaster child, so there is nothing to build with.");
			return;
		}

		// The board is somewhere you go, not what you arrive in.
		SetBuildMode(false);
	}

	/// <summary>
	/// Swap between driving and building. Everything that owns the screen changes hands at once:
	/// the camera, the tray, and whether the car is listening to the keyboard — which it must not
	/// be while building, because the board camera flies on the same WASD the car drives on.
	/// </summary>
	private void SetBuildMode(bool building)
	{
		if (_builder == null)
			return;

		_building = building;

		_builder.SetActive(building);
		if (_palette != null)
			_palette.Visible = building;

		RacerController? car = LocalCar();
		if (car == null)
			return;

		car.AcceptsInput = !building;

		// Taking the camera back is the board's business; handing it over is the car's, because the
		// rig is what knows the pose it films from.
		if (!building)
			car.GetNodeOrNull<CameraRig>("CameraRig")?.SetActive(true);
	}

	/// <summary>
	/// Put a freshly spawned car into whichever mode is already running.
	///
	/// Cars arrive late — in a session, whenever their peer reports in — so the mode was decided
	/// before there was a car to decide it for. Without this a host who went to the board while
	/// waiting for their car gets one that spawns still listening to the keyboard, and flying the
	/// board on WASD drives it around the pad off-screen.
	/// </summary>
	private void AdoptLocalCar() => SetBuildMode(_building);

	/// <summary>This machine's own car, or null before it has spawned.</summary>
	private RacerController? LocalCar()
	{
		foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
		{
			if (node is RacerController racer && racer.IsLocalPlayer)
				return racer;
		}

		return null;
	}

	/// <summary>
	/// The build toggle. Only offered to whoever may actually build — a client that flipped into
	/// the board would be looking at a track the server will not take tiles for, which is a worse
	/// answer than the key doing nothing.
	/// </summary>
	private void HandleBuilderInput(InputEvent @event)
	{
		if (!@event.IsActionPressed("builder_toggle") || !MayBuild)
			return;

		SetBuildMode(!_building);
		GetViewport().SetInputAsHandled();
	}

	/// <summary>
	/// A chequered line across the mouth of the start tile, with a post either side.
	///
	/// Furniture rather than a catalog entry, deliberately. <see cref="TileCatalog.All"/> is what
	/// the Track Master's hand is dealt from and what the free-build tray shows — a "Start" in that
	/// list would be a tile somebody gets handed in the middle of a race.
	/// </summary>
	private void BuildStartLine()
	{
		Vector2I step = BuildStartDirection.Step();
		var along = new Vector3(step.X, 0.0f, step.Y);
		var across = new Vector3(step.Y, 0.0f, -step.X);

		// The near face of the start cell: where the pad stops and the track begins.
		Vector3 line = TileCatalog.CellToWorld(BuildStartCell) - along * (TileCatalog.TileSize * 0.5f);

		// Every other square painted, so the gaps between them do the other half of the chequer
		// without anything having to be drawn in the pad's own colour.
		const int squares = 10;
		const float lift = 0.02f;
		float square = TileCatalog.TileSize / squares;

		for (int i = 0; i < squares; i += 2)
		{
			float offset = (i - (squares - 1) * 0.5f) * square;
			AddSlab($"StartLine{i}", SurfaceGroups.Road, StartLineColor,
					new Vector3(square, lift * 2.0f, square),
					line + across * offset + new Vector3(0.0f, SurfaceY + lift, 0.0f),
					collision: false);
		}

		const float postHeight = 14.0f;
		foreach (int side in new[] { -1, 1 })
		{
			AddSlab($"StartPost{(side < 0 ? "L" : "R")}", SurfaceGroups.Road, StartLineColor,
					new Vector3(2.0f, postHeight, 2.0f),
					line + across * (side * TileCatalog.TileSize * 0.5f)
						 + new Vector3(0.0f, postHeight * 0.5f, 0.0f));
		}

		AddLabel("Build here ↑", line - along * (TileCatalog.TileSize * 0.6f)
								 + new Vector3(0.0f, 10.0f, 0.0f), StartLineColor);
	}
}
