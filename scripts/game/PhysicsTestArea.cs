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
/// obvious, a bump strip for the dampers, and every authored piece from the catalog set out as a
/// specimen with a run-up to it (<c>PhysicsTestArea.Pieces.cs</c>).
///
/// Two tracks run off it. West is the chained course — the same pieces joined end to end into one
/// drivable line, always the same so a physics change can be judged against it
/// (<c>PhysicsTestArea.PieceRun.cs</c>). North is a bare start tile and whatever the host builds
/// onto it this lobby (<c>PhysicsTestArea.Builder.cs</c>).
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

	/// <summary>Pad half-extents, worked out from the layout in <see cref="Rebuild"/>.</summary>
	private float _padHalfX;
	private float _padHalfZ;

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

		// Every peer sets its own car up for the pad, whichever machine spawned it. The host is
		// the only one that ever calls Spawn, so hooking the spawn itself would leave a client's
		// car — which arrives by replication — never adopted, and its reset key never handing
		// nitro back.
		_arena.LocalCarSpawned += AdoptLocalCar;

		if (!NetworkManager.Instance.IsNetworked)
		{
			// Solo Test Drive: one car, ours, in the middle of the pad.
			_arena.Spawn(Multiplayer.GetUniqueId(), 0, 1);
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
			UI.SceneFader.Instance.TransitionTo(GameScenePath);
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
		BuildBumpStrip();
		BuildStartLine();
		BuildPieceSpecimens();
		BuildPieceRun();
	}

	/// <summary>
	/// Work out how much tarmac the layout needs under it: the buildable track's start line, and
	/// the row of authored pieces.
	/// </summary>
	private void MeasurePad()
	{
		_padHalfX = PadHalfSize;
		_padHalfZ = PadHalfSize;

		// The buildable track anchors off the pad's edge, and the tarmac has to reach its start
		// line: everything off the pad is open air, so a metre short and the only way onto the
		// track is a fall.
		_padHalfZ = Mathf.Max(_padHalfZ, PadEdgeForBuildableTrack);

		// And west, under the row of authored pieces. They have no size limit the way a catalog
		// tile did, so this is worked out from how many there are rather than from a constant.
		_padHalfX = Mathf.Max(_padHalfX, PadEdgeForPieces);
		_padHalfZ = Mathf.Max(_padHalfZ, PadEdgeAlongForPieces);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Engine.IsEditorHint())
			return;

		// racer_reset is the car's own business now — RacerController handles it, so it works
		// here and in a match alike rather than only on this pad. Escape belongs to the
		// PauseMenu overlay, which sees it before this node does.
		HandleBuilderInput(@event);
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
