using Godot;
using MasterTrack.Tiles;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The Track Master's side of the game: the top-down board view and the act of dropping tiles
/// onto the end of the track.
///
/// This is the *view and input* half — it owns the camera, the ghost preview and the cell the
/// mouse is over, then hands an intent to <see cref="TrackController"/>. It never places
/// anything itself; the server decides what is real.
///
/// Interaction is deliberately one rule, which covers both ways people reach for it:
/// pick a tile in the palette, then release the mouse over the board. Drag from the palette
/// and let go over the track, or click the tile and then click the track — same code path.
/// </summary>
public partial class TrackMasterController : Node3D
{
	/// <summary>The track to build onto. Required.</summary>
	[Export] public TrackController? Track { get; set; }

	/// <summary>Starting height of the board camera, in metres.</summary>
	[Export] public float CameraHeight { get; set; } = 55.0f;

	[Export] public float MinCameraHeight { get; set; } = 18.0f;
	[Export] public float MaxCameraHeight { get; set; } = 140.0f;

	/// <summary>Keyboard pan speed, in metres per second at the default height.</summary>
	[Export] public float PanSpeed { get; set; } = 45.0f;

	/// <summary>Fraction of the current height added or removed per wheel notch.</summary>
	[Export] public float ZoomStep { get; set; } = 0.12f;

	[Signal] public delegate void HoverChangedEventHandler(bool valid, string reason);

	private Camera3D _camera = null!;
	private Node3D _headMarker = null!;
	private TrackTile? _ghost;

	private int _selectedIndex = -1;
	private int _ghostIndex = -1;
	private bool _ghostValid;
	private Vector2I _ghostCell;

	private Vector2I _hoverCell;
	private bool _hoverOnBoard;
	private bool _panning;

	private static readonly Color ValidTint = new(0.35f, 1.0f, 0.45f);
	private static readonly Color InvalidTint = new(1.0f, 0.35f, 0.35f);

	public override void _Ready()
	{
		if (Track == null)
		{
			GD.PushError("[TrackMaster] No TrackController assigned; the builder is inert.");
			SetProcess(false);
			SetProcessUnhandledInput(false);
			return;
		}

		_camera = new Camera3D
		{
			Name = "BoardCamera",
			// Straight down, so screen X/Y map cleanly onto world X/Z.
			RotationDegrees = new Vector3(-90, 0, 0),
			Position = Track.HeadWorldPosition + new Vector3(0, CameraHeight, 0),
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

	/// <summary>Called by the palette when the Track Master picks a tile up.</summary>
	public void SelectTile(int catalogIndex)
	{
		_selectedIndex = catalogIndex;
	}

	public void ClearSelection()
	{
		_selectedIndex = -1;
		ClearGhost();
	}

	public override void _Process(double delta)
	{
		if (Track == null)
			return;

		UpdatePan((float)delta);
		UpdateHover();
		UpdateGhost();
	}

	// ---- Camera ----

	private void UpdatePan(float delta)
	{
		var pan = new Vector2(
			Input.GetActionStrength("ui_right") - Input.GetActionStrength("ui_left"),
			Input.GetActionStrength("ui_down") - Input.GetActionStrength("ui_up"));

		if (pan == Vector2.Zero)
			return;

		// Pan faster when zoomed out, so the board takes the same time to cross either way.
		float scale = _camera.Position.Y / CameraHeight;
		Vector3 position = _camera.Position;
		position.X += pan.X * PanSpeed * scale * delta;
		position.Z += pan.Y * PanSpeed * scale * delta;
		_camera.Position = position;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Track == null)
			return;

		if (@event is InputEventMouseButton mouse)
		{
			HandleMouseButton(mouse);
			return;
		}

		// Middle-drag to pan the board.
		if (@event is InputEventMouseMotion motion && _panning)
		{
			float scale = _camera.Position.Y / CameraHeight * 0.05f;
			Vector3 position = _camera.Position;
			position.X -= motion.Relative.X * scale;
			position.Z -= motion.Relative.Y * scale;
			_camera.Position = position;
		}
	}

	private void HandleMouseButton(InputEventMouseButton mouse)
	{
		switch (mouse.ButtonIndex)
		{
			case MouseButton.WheelUp:
				Zoom(-ZoomStep);
				break;

			case MouseButton.WheelDown:
				Zoom(ZoomStep);
				break;

			case MouseButton.Middle:
				_panning = mouse.Pressed;
				break;

			case MouseButton.Right when mouse.Pressed:
				ClearSelection();
				break;

			// Releasing over the board is what commits a placement — whether the player
			// dragged here from the palette or clicked the palette and then clicked here.
			case MouseButton.Left when !mouse.Pressed:
				TryPlace();
				break;
		}
	}

	private void Zoom(float amount)
	{
		Vector3 position = _camera.Position;
		position.Y = Mathf.Clamp(position.Y * (1.0f + amount), MinCameraHeight, MaxCameraHeight);
		_camera.Position = position;
	}

	// ---- Hover ----

	/// <summary>Work out which grid cell the mouse is over by casting onto the ground plane.</summary>
	private void UpdateHover()
	{
		Vector2 mouse = GetViewport().GetMousePosition();
		Vector3 origin = _camera.ProjectRayOrigin(mouse);
		Vector3 direction = _camera.ProjectRayNormal(mouse);

		if (Mathf.IsZeroApprox(direction.Y))
		{
			_hoverOnBoard = false;
			return;
		}

		float distance = -origin.Y / direction.Y;
		if (distance < 0.0f)
		{
			_hoverOnBoard = false;
			return;
		}

		_hoverOnBoard = true;
		_hoverCell = TileCatalog.WorldToCell(origin + direction * distance);
	}

	// ---- Ghost preview ----

	private void UpdateGhost()
	{
		if (_selectedIndex < 0 || !_hoverOnBoard)
		{
			ClearGhost();
			return;
		}

		TileDefinition? definition = TileCatalog.At(_selectedIndex);
		if (definition == null)
		{
			ClearGhost();
			return;
		}

		bool valid = Track!.Grid.CanPlace(_hoverCell, definition.ToTileData(), out string reason);
		EmitSignal(SignalName.HoverChanged, valid, valid ? "Release to place" : reason);

		// Rebuilding is cheap but not free, so only do it when something actually changed.
		if (_ghost != null && _ghostIndex == _selectedIndex
			&& _ghostCell == _hoverCell && _ghostValid == valid)
			return;

		ClearGhost();

		_ghostIndex = _selectedIndex;
		_ghostCell = _hoverCell;
		_ghostValid = valid;

		_ghost = new TrackTile { Name = "TileGhost" };
		AddChild(_ghost);
		_ghost.Initialize(definition.ToTileData(), -1, _hoverCell, Track.Grid.HeadDirection,
						  isGhost: true, ghostTint: valid ? ValidTint : InvalidTint);
	}

	private void ClearGhost()
	{
		if (_ghost == null)
			return;

		_ghost.QueueFree();
		_ghost = null;
		_ghostIndex = -1;
	}

	// ---- Placement ----

	private void TryPlace()
	{
		if (_selectedIndex < 0 || !_hoverOnBoard || Track == null)
			return;

		TileDefinition? definition = TileCatalog.At(_selectedIndex);
		if (definition == null)
			return;

		if (!Track.Grid.CanPlace(_hoverCell, definition.ToTileData(), out string reason))
		{
			EmitSignal(SignalName.HoverChanged, false, reason);
			return;
		}

		Track.RequestPlaceTile(_selectedIndex);
		// The selection stays put so a run of tiles can be laid down without re-picking.
	}

	// ---- Head marker ----

	private void OnTrackHeadChanged()
	{
		if (Track == null)
			return;

		_headMarker.Position = Track.HeadWorldPosition;
		_headMarker.Rotation = new Vector3(0.0f, Track.Grid.HeadDirection.Yaw(), 0.0f);
		ClearGhost();
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
		// Shaft plus a chevron, pointing along local -Z (the direction of travel).
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.05f, 5.0f), Material = arrowMaterial },
			Position = new Vector3(0, 0.05f, 0.5f),
		});
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.05f, 2.6f), Material = arrowMaterial },
			Position = new Vector3(-0.85f, 0.05f, -2.6f),
			RotationDegrees = new Vector3(0, -40, 0),
		});
		root.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = new Vector3(0.7f, 0.05f, 2.6f), Material = arrowMaterial },
			Position = new Vector3(0.85f, 0.05f, -2.6f),
			RotationDegrees = new Vector3(0, 40, 0),
		});

		return root;
	}
}
