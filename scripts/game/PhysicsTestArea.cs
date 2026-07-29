using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using MasterTrack.Tiles;
using MasterTrack.Vehicles;
using System.Collections.Generic;

namespace MasterTrack.Game;

/// <summary>
/// A playground for feeling out the vehicle physics, and the lobby everyone waits in.
///
/// The pad itself: a big open surface to slide around on, a grass apron so surface changes are
/// obvious, a bump strip for the dampers, and one of every tile in <see cref="TileCatalog"/> laid
/// out in a row with a run-up to each.
///
/// Two race tracks run off the ends of it. South is a fixed course — the same catalog again, but
/// joined end to end into one drivable track instead of set out as specimens, and always the same
/// so a physics change can be judged against it (<c>PhysicsTestArea.RaceTrack.cs</c>). North is a
/// bare start tile and whatever the host builds onto it this lobby
/// (<c>PhysicsTestArea.Builder.cs</c>).
///
/// In a session it is also where the group gathers. Cars are <see cref="RacerArena"/>'s business,
/// the same node the match uses, so a car that reaches everybody here reaches everybody there;
/// this class only decides *when* one is spawned — as each peer reports its lobby loaded, so a
/// late arrival gets a car without disturbing anyone already driving.
///
/// Everything is built in code so the tiles here are the real ones the Track Master places —
/// there's no second copy of the geometry to drift out of sync.
///
/// Runs as a <c>[Tool]</c> so the layout is visible in the editor viewport as well as at
/// play time. The generated nodes are deliberately left without an owner, so they render and
/// can be clicked through but are never written into the .tscn — the scene file stays as just
/// the environment, the car and the HUD.
/// </summary>
[Tool]
public partial class PhysicsTestArea : Node3D
{
	/// <summary>
	/// Minimum half-extent of the tarmac pad, in metres. A floor rather than the final size: the
	/// pad is grown to reach under every tile in the layout, because a proving ground where half
	/// the catalog hangs over the void is no use for driving onto anything.
	/// </summary>
	[Export] public float PadHalfSize { get; set; } = TileCatalog.TileSize * 7.5f;

	/// <summary>Width of the grass apron along the +X edge of the pad.</summary>
	[Export] public float GrassWidth { get; set; } = 120.0f;

	/// <summary>
	/// Tiles per row. The catalog is long enough now that one row would be a two kilometre line
	/// nobody is going to drive to the end of, so it wraps.
	/// </summary>
	[Export] public int TileColumns { get; set; } = 6;

	/// <summary>Cells between tile columns. Three, so there is a clear cell beside even a
	/// two-cell-wide hairpin.</summary>
	[Export] public int TileCellStride { get; set; } = 3;

	/// <summary>Cells between tile rows: a three-cell tile, plus three cells of run-up to it.</summary>
	[Export] public int TileRowStride { get; set; } = 6;

	/// <summary>Widest and longest any tile in the catalog is, in cells. Hairpins are the wide
	/// ones; the straight-through tiles are the long ones.</summary>
	private const int WidestTileCells = 2;
	private const int LongestTileCells = 3;

	/// <summary>Pad half-extents, worked out from the layout in <see cref="Rebuild"/>.</summary>
	private float _padHalfX;
	private float _padHalfZ;

	private const string MainMenuScenePath = "res://scenes/Main.tscn";
	private const string GameScenePath = "res://scenes/Game.tscn";
	private const string GeneratedRootName = "Generated";

	// The pad's surface sits a hair below the tiles so the two never z-fight. Small enough
	// that the suspension doesn't notice driving on or off a tile.
	private const float SurfaceY = -0.02f;
	private const float SlabThickness = 0.4f;

	private static readonly Color RoadColor = new(0.26f, 0.26f, 0.28f);
	private static readonly Color GrassColor = new(0.29f, 0.42f, 0.27f);
	private static readonly Color BumpColor = new(0.85f, 0.72f, 0.25f);

	private Node3D _generated = null!;

	private RacerArena? _arena;

	/// <summary>Server only. Which ring slot each peer's car was given.</summary>
	private readonly Dictionary<int, int> _slots = new();

	/// <summary>
	/// Slots the lobby ring is divided into. Fixed at the session's capacity rather than the
	/// current head count, so a car already parked on the pad never has to move because somebody
	/// else joined.
	/// </summary>
	private static int RingSlots => NetworkManager.MaxPlayers + 1;

	/// <summary>
	/// The buildable track has to be pointed at its start cell before it lays anything, and a node
	/// is readied after its children — so by <c>_Ready</c> it is already too late. Entering the
	/// tree runs parent-first, which makes this the only window.
	/// </summary>
	public override void _EnterTree()
	{
		if (Engine.IsEditorHint())
			return;

		ConfigureBuildableTrack();
	}

	public override void _Ready()
	{
		Rebuild();

		if (Engine.IsEditorHint())
			return;

		SetUpBuilder();

		_arena = GetNodeOrNull<RacerArena>("RacerArena");
		if (_arena == null)
		{
			GD.PushError("[TestArea] No RacerArena child, so there will be no cars.");
			return;
		}

		if (!NetworkManager.Instance.IsNetworked)
		{
			// Solo Test Drive: one car, ours, in the middle of the pad.
			_arena.Spawn(Multiplayer.GetUniqueId(), 0, 1);
			AdoptLocalCar();
			return;
		}

		GameManager.Instance.GameStateChanged += OnGameStateChanged;

		// Subscribed before checking in, so the host's own arrival is not missed.
		if (NetworkManager.Instance.IsHost)
		{
			GameManager.Instance.PeerSceneReady += OnPeerSceneReady;
			NetworkManager.Instance.PlayerDisconnected += OnPlayerDisconnected;
		}

		GameManager.Instance.ReportSceneReady();
	}

	/// <summary>
	/// Autoloads outlive this scene, and a C# <c>+=</c> handler is a managed delegate Godot
	/// cannot tie to a node's lifetime — so it has to be taken back by hand or the next signal
	/// lands on a disposed node. Harmless to remove a handler that was never added.
	/// </summary>
	public override void _ExitTree()
	{
		if (Engine.IsEditorHint())
			return;

		GameManager.Instance.GameStateChanged -= OnGameStateChanged;
		GameManager.Instance.PeerSceneReady -= OnPeerSceneReady;
		NetworkManager.Instance.PlayerDisconnected -= OnPlayerDisconnected;
	}

	/// <summary>Server only. A peer has the lobby loaded, so its car can safely be spawned.</summary>
	private void OnPeerSceneReady(int peerId)
	{
		if (_arena == null || _slots.ContainsKey(peerId))
			return;

		_slots[peerId] = NextFreeSlot();
		_arena.Spawn(peerId, _slots[peerId], RingSlots);
		AdoptLocalCar();

		// Placements are broadcast as they happen, so a peer that arrived after the track was
		// built has seen none of them. Without this they get a bare start tile to drive through
		// while everybody else is up on track that, for them, isn't there.
		_track?.SyncTo(peerId);
	}

	/// <summary>Server only. Free the slot and the car of a peer that has left.</summary>
	private void OnPlayerDisconnected(int peerId)
	{
		_slots.Remove(peerId);
		_arena?.Despawn(peerId);
	}

	/// <summary>Lowest unused ring slot, so a leaver's parking space is reused by the next joiner.</summary>
	private int NextFreeSlot()
	{
		for (int slot = 0; slot < RingSlots; slot++)
		{
			if (!_slots.ContainsValue(slot))
				return slot;
		}

		return 0;
	}

	/// <summary>The host has started the match; everyone follows them into it.</summary>
	private void OnGameStateChanged(int state)
	{
		if ((GameState)state == GameState.InRound)
			GetTree().ChangeSceneToFile(GameScenePath);
	}

	/// <summary>
	/// Tear down any previous build and lay the area out again. Called on load, and again
	/// whenever the editor reloads this script, so edits to the layout show up immediately.
	/// </summary>
	private void Rebuild()
	{
		Node? previous = GetNodeOrNull(GeneratedRootName);
		if (previous != null)
		{
			RemoveChild(previous);
			previous.QueueFree();
		}

		// No Owner assigned, so none of this is serialized when the scene is saved.
		_generated = new Node3D { Name = GeneratedRootName };
		AddChild(_generated);

		// Sized before anything is built, because the pad has to reach under the tile layout and
		// the layout is what decides how far that is.
		MeasurePad();

		BuildSurfaces();
		BuildTileGrid();
		BuildBumpStrip();
		BuildRaceTrack();
		BuildStartLine();
		BuildPieceChain();
	}

	/// <summary>Rows the tile layout needs to hold the whole catalog.</summary>
	private int TileRows => (TileCatalog.All.Count + TileColumns - 1) / Mathf.Max(1, TileColumns);

	/// <summary>Cell of the first column, so the grid straddles the origin.</summary>
	private int FirstColumnCell => -((TileColumns - 1) * TileCellStride) / 2;

	/// <summary>Cell of the first row.</summary>
	private int FirstRowCell => -((TileRows - 1) * TileRowStride) / 2;

	/// <summary>
	/// Work out how much tarmac the tile layout needs under it. Tiles run north from their own
	/// cell, and a hairpin swings a cell out to one side, so the extent is the grid plus room for
	/// the biggest tile in each direction plus the run-up a racer needs to arrive at speed.
	/// </summary>
	private void MeasurePad()
	{
		int lastColumnCell = FirstColumnCell + (TileColumns - 1) * TileCellStride;
		int lastRowCell = FirstRowCell + (TileRows - 1) * TileRowStride;

		int spreadX = Mathf.Max(Mathf.Abs(FirstColumnCell), Mathf.Abs(lastColumnCell)) + WidestTileCells;
		int spreadZ = Mathf.Max(Mathf.Abs(FirstRowCell - (LongestTileCells - 1)),
								Mathf.Abs(lastRowCell)) + LongestTileCells;

		_padHalfX = Mathf.Max(PadHalfSize, spreadX * TileCatalog.TileSize);
		_padHalfZ = Mathf.Max(PadHalfSize, spreadZ * TileCatalog.TileSize);

		// A track runs off each end of the pad, and the tarmac has to reach both start lines:
		// everything off the pad is open air, so a metre short and the only way onto a track is a
		// fall. One half-extent covers both because the pad is symmetric about the origin.
		_padHalfZ = Mathf.Max(_padHalfZ, Mathf.Max(PadEdgeForRaceTrack, PadEdgeForBuildableTrack));
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Engine.IsEditorHint())
			return;

		// racer_reset is the car's own business now — RacerController handles it, so it works
		// here and in a match alike rather than only on this pad.
		if (@event.IsActionPressed("ui_cancel"))
		{
			LeaveToMenu();
			return;
		}

		HandleBuilderInput(@event);
	}

	/// <summary>
	/// Back to the main menu. In a session that means dropping out of it first — otherwise the
	/// peer stays connected from behind the menu and the host keeps waiting on a ghost.
	/// </summary>
	private void LeaveToMenu()
	{
		if (NetworkManager.Instance.IsNetworked)
			NetworkManager.Instance.Disconnect();

		GetTree().ChangeSceneToFile(MainMenuScenePath);
	}

	private void BuildSurfaces()
	{
		AddSlab("RoadPad", SurfaceGroups.Road, RoadColor,
				new Vector3(_padHalfX * 2.0f, SlabThickness, _padHalfZ * 2.0f),
				new Vector3(0, SurfaceY - SlabThickness * 0.5f, 0));

		// Butts straight up against the pad's +X edge, so you can put two wheels on the grass
		// and feel the car go loose without leaving the ground.
		AddSlab("GrassApron", SurfaceGroups.Grass, GrassColor,
				new Vector3(GrassWidth, SlabThickness, _padHalfZ * 2.0f),
				new Vector3(_padHalfX + GrassWidth * 0.5f, SurfaceY - SlabThickness * 0.5f, 0));

		AddLabel("Grass →", new Vector3(_padHalfX - 12.0f, 5.0f, -40.0f), GrassColor);
	}

	/// <summary>
	/// One of every catalog tile, in a grid, each with clear tarmac in front of it to build speed
	/// on. Rows rather than one long line: the catalog outgrew the line.
	/// </summary>
	private void BuildTileGrid()
	{
		for (int i = 0; i < TileCatalog.All.Count; i++)
		{
			TileDefinition definition = TileCatalog.All[i];

			var cell = new Vector2I(
				FirstColumnCell + i % TileColumns * TileCellStride,
				FirstRowCell + i / TileColumns * TileRowStride);

			var tile = new TrackTile { Name = $"Tile_{definition.DisplayName.Replace(" ", "")}" };
			_generated.AddChild(tile);

			Vector3 world = TileCatalog.CellToWorld(cell);

			// Facing north, so the run-up is from +Z — the same way a racer meets it in a match.
			// Always from ground level: a ramp here climbs away from the pad rather than starting
			// in the air, which is the only way to drive onto one from the tarmac.
			tile.Initialize(definition.ToTileData(), i,
							PlacedTile.AnchorFor(world, TrackDirection.North,
												 TileCatalog.TileSize * 0.5f));

			// On the near side of the tile rather than over its middle, so the label is readable
			// from the run-up and is not buried inside a loop or a ramp.
			AddLabel(definition.DisplayName,
					 world + new Vector3(0.0f, 6.0f, TileCatalog.TileSize * 0.7f), definition.Accent);
		}
	}

	/// <summary>
	/// A washboard section. Drive it at different speeds to hear the dampers working — this is
	/// where bump/rebound settings stop being abstract numbers. Laid out behind the tile row so
	/// it never sits in the run-up to a tile; the ridges themselves are sized for the car's
	/// wheelbase, not for the tiles.
	/// </summary>
	private void BuildBumpStrip()
	{
		const float x = -95.0f;
		const float height = 0.1f;
		float start = -TileCatalog.TileSize * 1.5f;

		for (int i = 0; i < 8; i++)
		{
			float z = start - i * 3.5f;
			AddSlab($"Bump{i}", SurfaceGroups.Road, BumpColor,
					new Vector3(18.0f, height, 0.7f),
					new Vector3(x, SurfaceY + height * 0.5f, z));
		}

		AddLabel("Bump strip", new Vector3(x, 5.0f, start + 8.0f), BumpColor);
	}

	// ---- Primitives ----

	/// <summary>
	/// A box of driveable surface. The group is what the tire model reads for grip.
	///
	/// <paramref name="collision"/> is for the things that are only paint. A stripe lying 2 cm
	/// proud of the road is a 2 cm kerb as far as the suspension is concerned, which is a bump the
	/// car has no business feeling — the same reason <see cref="TrackTile"/> builds its racing
	/// lines as mesh only.
	/// </summary>
	private void AddSlab(string name, string surfaceGroup, Color color, Vector3 size, Vector3 position,
						 bool collision = true)
	{
		var body = new StaticBody3D { Name = name, Position = position };
		body.AddToGroup(surfaceGroup);
		_generated.AddChild(body);

		body.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh
			{
				Size = size,
				Material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.95f },
			},
		});

		if (collision)
			body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
	}

	private void AddLabel(string text, Vector3 position, Color color)
	{
		_generated.AddChild(new Label3D
		{
			Text = text,
			Position = position,
			PixelSize = 0.03f,
			FontSize = 96,
			OutlineSize = 30,
			Modulate = color,
			OutlineModulate = new Color(0, 0, 0, 0.85f),
			Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
			// Keeps labels readable through the walls of the tile they're naming.
			NoDepthTest = true,
		});
	}
}
