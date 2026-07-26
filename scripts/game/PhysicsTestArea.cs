using Godot;
using MasterTrack.Tiles;
using MasterTrack.Vehicles;

namespace MasterTrack.Game;

/// <summary>
/// A playground for feeling out the vehicle physics: a big open pad to slide around on, a
/// grass apron so surface changes are obvious, a bump strip for the dampers, and one of every
/// tile in <see cref="TileCatalog"/> laid out in a row with a run-up to each.
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
	/// Half-extent of the tarmac pad, in metres. Wide enough to hold the whole tile row with
	/// room to line each one up, so it grows with the tiles.
	/// </summary>
	[Export] public float PadHalfSize { get; set; } = TileCatalog.TileSize * 7.5f;

	/// <summary>Width of the grass apron along the +X edge of the pad.</summary>
	[Export] public float GrassWidth { get; set; } = 120.0f;

	/// <summary>Grid cell the tile row is centred on, in cells.</summary>
	[Export] public int TileRowZCell { get; set; }

	/// <summary>Cells between each tile in the row, so 2 leaves a whole cell of clear tarmac
	/// between neighbours.</summary>
	[Export] public int TileCellStride { get; set; } = 2;

	/// <summary>The car to respawn. Defaults to a sibling named <c>TestCar</c>.</summary>
	[Export] public RigidBody3D? Car { get; set; }

	private const string MainMenuScenePath = "res://scenes/Main.tscn";
	private const string GeneratedRootName = "Generated";

	// The pad's surface sits a hair below the tiles so the two never z-fight. Small enough
	// that the suspension doesn't notice driving on or off a tile.
	private const float SurfaceY = -0.02f;
	private const float SlabThickness = 0.4f;

	private static readonly Color RoadColor = new(0.26f, 0.26f, 0.28f);
	private static readonly Color GrassColor = new(0.29f, 0.42f, 0.27f);
	private static readonly Color BumpColor = new(0.85f, 0.72f, 0.25f);

	private Node3D _generated = null!;
	private Transform3D _carStart;

	public override void _Ready()
	{
		Rebuild();

		if (Engine.IsEditorHint())
			return;

		Car ??= GetNodeOrNull<RigidBody3D>("TestCar");
		if (Car != null)
			_carStart = Car.GlobalTransform;
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

		BuildSurfaces();
		BuildTileRow();
		BuildBumpStrip();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (Engine.IsEditorHint())
			return;

		// You will end up on the roof in here, so make getting back trivial. Deliberately its
		// own action rather than a ui_* one: Godot's ui_accept includes Space, which would
		// fight the handbrake every time you tried to slide the car.
		if (@event.IsActionPressed("racer_reset"))
			RespawnCar();

		if (@event.IsActionPressed("ui_cancel"))
			GetTree().ChangeSceneToFile(MainMenuScenePath);
	}

	/// <summary>
	/// Put the car back on the start line, upright and stopped. Goes through the physics
	/// server rather than assigning GlobalTransform, which a rigid body is entitled to ignore.
	/// </summary>
	private void RespawnCar()
	{
		if (Car == null || !IsInstanceValid(Car))
			return;

		Rid rid = Car.GetRid();
		PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Transform, _carStart);
		PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.LinearVelocity, Vector3.Zero);
		PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.AngularVelocity, Vector3.Zero);
	}

	private void BuildSurfaces()
	{
		AddSlab("RoadPad", SurfaceGroups.Road, RoadColor,
				new Vector3(PadHalfSize * 2.0f, SlabThickness, PadHalfSize * 2.0f),
				new Vector3(0, SurfaceY - SlabThickness * 0.5f, 0));

		// Butts straight up against the pad's +X edge, so you can put two wheels on the grass
		// and feel the car go loose without leaving the ground.
		AddSlab("GrassApron", SurfaceGroups.Grass, GrassColor,
				new Vector3(GrassWidth, SlabThickness, PadHalfSize * 2.0f),
				new Vector3(PadHalfSize + GrassWidth * 0.5f, SurfaceY - SlabThickness * 0.5f, 0));

		AddLabel("Grass →", new Vector3(PadHalfSize - 12.0f, 5.0f, -40.0f), GrassColor);
	}

	/// <summary>One of every catalog tile, spaced out so each can be approached on its own.</summary>
	private void BuildTileRow()
	{
		int count = TileCatalog.All.Count;
		int firstCell = -(count - 1) * TileCellStride / 2;

		for (int i = 0; i < count; i++)
		{
			TileDefinition definition = TileCatalog.All[i];
			var cell = new Vector2I(firstCell + i * TileCellStride, TileRowZCell);

			var tile = new TrackTile { Name = $"Tile_{definition.DisplayName.Replace(" ", "")}" };
			_generated.AddChild(tile);
			// Facing north, so the run-up is from +Z — the same way a racer meets it in a match.
			tile.Initialize(definition.ToTileData(), i, cell, TrackDirection.North);

			Vector3 world = TileCatalog.CellToWorld(cell);
			AddLabel(definition.DisplayName, world + new Vector3(0, 6.0f, 0), definition.Accent);
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

	/// <summary>A box of driveable surface. The group is what the tire model reads for grip.</summary>
	private void AddSlab(string name, string surfaceGroup, Color color, Vector3 size, Vector3 position)
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
