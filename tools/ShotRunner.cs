using System.Collections.Generic;
using System.Linq;
using Godot;
using MasterTrack.Tiles;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Tools;

/// <summary>
/// Throwaway harness for photographing the track under controlled conditions.
///
/// Two modes:
///
/// - <b>style</b> (<c>--style=NAME</c>) sets one straight and one curve down on their own, under
///   the named style's own environment and materials, and photographs each from a fixed camera.
///   This is the mode for comparing looks: nothing else is in the frame, every style gets the
///   same two pieces from the same two angles, and the only variables are the ones the style
///   owns.
/// - <b>area</b> (the default) instances the whole proving ground, drops hazards into every slot
///   and shoots the specimens in place. This is the mode for checking a lighting change against
///   the real scene.
///
/// Camera transforms are authored in each piece's own entry frame and lifted into the world, so
/// two runs either side of a change are identically framed.
///
/// Not part of the game. Run it with:
///   Godot_v4.7-stable_mono_win64_console.exe --path . res://tools/ShotRunner.tscn \
///       -- --out=DIR --style=cel
/// </summary>
public partial class ShotRunner : Node3D
{
	private const string AreaScene = "res://scenes/TestArea.tscn";
	private const string PieceFolder = "res://scenes/tiles/pieces";
	private const string StyleFolder = "res://resources/tiles/styles";

	/// <summary>Seconds of game time to let the area settle before shooting: the pieces bake, the
	/// car drops onto the pad, and the pop-up ramps finish rising (a 2 s fuse).</summary>
	private const float SettleSeconds = 4.0f;

	/// <summary>The sun every style shot is lit by — the game's own, so a style is judged under
	/// the light it would actually ship with rather than a studio rig.</summary>
	private static readonly Transform3D SunTransform = new(
		new Basis(new Vector3(0.866025f, 0.0f, -0.5f),
				  new Vector3(-0.353553f, 0.707107f, -0.612372f),
				  new Vector3(0.353553f, 0.707107f, 0.612372f)),
		new Vector3(0.0f, 40.0f, 0.0f));

	/// <summary>The two pieces every style is judged on, and where the camera stands in each
	/// one's own frame. A piece runs along its local -Z from its entry, so +Z is behind the
	/// start line.</summary>
	private static readonly (string Piece, string Shot, Vector3 Eye, Vector3 Look)[] StyleShots =
	{
		("Straight", "straight", new Vector3(38.0f, 20.0f, 40.0f), new Vector3(0.0f, 0.0f, -62.0f)),
		("CurveLeft", "curve", new Vector3(62.0f, 52.0f, 52.0f), new Vector3(-24.0f, 0.0f, -44.0f)),
	};

	/// <summary>Which specimens the area mode photographs, same convention.</summary>
	private static readonly (string Piece, string Shot, Vector3 Eye, Vector3 Look)[] AreaShots =
	{
		("Straight", "straight-hazards", new Vector3(0.0f, 7.0f, 26.0f), new Vector3(0.0f, 1.0f, -70.0f)),
		("Straight", "straight-above", new Vector3(38.0f, 62.0f, 62.0f), new Vector3(0.0f, 0.0f, -54.0f)),
		("RampLarge", "ramp", new Vector3(0.0f, 9.0f, 34.0f), new Vector3(0.0f, 8.0f, -80.0f)),
		("RampLarge", "ramp-oblique", new Vector3(72.0f, 7.0f, 10.0f), new Vector3(-20.0f, 30.0f, -70.0f)),
		("HairpinLeft", "hairpin", new Vector3(30.0f, 46.0f, 50.0f), new Vector3(-20.0f, 0.0f, -60.0f)),
	};

	private string _outDir = "user://shots";
	private string _style = "";
	private string _spike = "";

	/// <summary>Where the proving ground's car ended up, for the car close-up.</summary>
	private Transform3D? _carFrame;

	public override void _Ready()
	{
		foreach (string argument in OS.GetCmdlineUserArgs())
		{
			if (argument.StartsWith("--out="))
				_outDir = argument["--out=".Length..];
			else if (argument.StartsWith("--style="))
				_style = argument["--style=".Length..];
			else if (argument.StartsWith("--spike="))
				_spike = argument["--spike=".Length..];
		}

		DisplayServer.WindowSetSize(new Vector2I(1600, 900));

		if (_spike.Length > 0)
			RunCarSpike();
		else if (_style.Length > 0)
			RunStyle();
		else
			RunArea();
	}

	// ---- Style mode ----

	private async void RunStyle()
	{
		// The style owns the light as much as the surface: a cel look under a washed sky is not
		// the cel look. "current" means the game as it stands, for a like-for-like reference.
		string envPath = _style == "current"
			? "res://resources/environment.tres"
			: $"{StyleFolder}/{_style}_env.tres";

		AddChild(new WorldEnvironment { Environment = GD.Load<Godot.Environment>(envPath) });

		var sun = new DirectionalLight3D
		{
			Name = "Sun",
			Transform = SunTransform,
			LightEnergy = 1.15f,
			LightAngularDistance = 1.0f,
			ShadowEnabled = true,
			ShadowBias = 0.06f,
			ShadowNormalBias = 1.5f,
			DirectionalShadowMaxDistance = 900.0f,
			DirectionalShadowBlendSplits = true,
		};
		AddChild(sun);

		// A curve is laid out clear of the straight so the two never share a frame — each shot is
		// meant to be one piece against the style's own background and nothing else.
		var pieces = new Dictionary<string, TrackPiece>();
		foreach ((string name, Vector3 at) in new[]
				 {
					 ("Straight", Vector3.Zero),
					 ("CurveLeft", new Vector3(2400.0f, 0.0f, 0.0f)),
				 })
		{
			if (GD.Load<PackedScene>($"{PieceFolder}/{name}.tscn")?.Instantiate() is not TrackPiece piece)
			{
				GD.PushError($"[ShotRunner] Could not instance {name}.");
				continue;
			}

			piece.Name = $"Piece_{name}";
			AddChild(piece);
			piece.Position = at;
			pieces[name] = piece;
		}

		// A couple of frames for the pieces to free their CSG and settle their baked meshes.
		for (var i = 0; i < 8; i++)
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		if (_style != "current")
			Repaint(pieces);

		await Shoot(StyleShots, pieces, suffix: $"-{_style}");
		GetTree().Quit();
	}

	/// <summary>
	/// Dress every piece in the style's materials.
	///
	/// Two materials per style, not one, and the split is the UV contract rather than taste: the
	/// swept pieces leave UV.y as polygon perimeter, while <see cref="BankedRoad"/> writes each
	/// point's lateral lean into it. A material that reads lean has to be given only to meshes
	/// that wrote it, which is exactly what <c>banked_road.tres</c> exists for on the base look.
	/// </summary>
	private void Repaint(Dictionary<string, TrackPiece> pieces)
	{
		var road = GD.Load<Material>($"{StyleFolder}/{_style}_road.tres");
		var banked = GD.Load<Material>($"{StyleFolder}/{_style}_banked.tres");

		if (road == null || banked == null)
		{
			GD.PushError($"[ShotRunner] Style '{_style}' is missing a road or banked material.");
			return;
		}

		foreach (TrackPiece piece in pieces.Values)
		{
			foreach (Node node in piece.FindChildren("*", recursive: true, owned: false))
			{
				if (node is not MeshInstance3D mesh || mesh.Mesh == null)
					continue;

				// Whether this mesh wrote the lean channel, asked of the mesh itself rather than
				// of the node that built it: a baked piece frees its CSG combiner on load, so by
				// now the BankedRoad node is gone and only its output survives. What survives
				// with it is the material the bake was authored against, and only the banked
				// sweep is ever given banked_road.tres — so that reference is the record of which
				// UV.y convention this mesh carries.
				bool wroteLean = Enumerable
					.Range(0, mesh.Mesh.GetSurfaceCount())
					.Select(surface => mesh.Mesh.SurfaceGetMaterial(surface)?.ResourcePath ?? "")
					.Any(path => path.Contains("banked"));

				mesh.MaterialOverride = wroteLean ? banked : road;
			}
		}
	}

	// ---- Area mode ----

	private async void RunArea()
	{
		var area = GD.Load<PackedScene>(AreaScene).Instantiate<Node3D>();
		AddChild(area);

		int frames = Mathf.RoundToInt(SettleSeconds * Engine.PhysicsTicksPerSecond);
		for (var i = 0; i < frames; i++)
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

		// The HUD would be in every shot, and this is about the world.
		if (area.GetNodeOrNull<CanvasLayer>("HUD") is { } hud)
			hud.Visible = false;

		Dictionary<string, TrackPiece> pieces = area
			.FindChildren("Piece_*", recursive: true, owned: false)
			.OfType<TrackPiece>()
			.ToDictionary(piece => piece.Name.ToString()["Piece_".Length..]);

		DressWithHazards(pieces);

		// The car itself, framed from behind and close, because the small round furniture on it
		// (the antenna ball) is where the outline's normal detector is most easily overwhelmed.
		// By group rather than by type name: the car adds itself to it on ready, which is also
		// how the board finds it, and FindChildren's type matching does not see C# class names.
		if (GetTree().GetNodesInGroup(MasterTrack.Racer.RacerController.GroupName)
				.OfType<Node3D>().FirstOrDefault() is { } racer)
		{
			_carFrame = racer.GlobalTransform;
		}

		for (var i = 0; i < Mathf.RoundToInt(3.0f * Engine.PhysicsTicksPerSecond); i++)
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

		await Shoot(AreaShots, pieces, suffix: "");
		GetTree().Quit();
	}

	/// <summary>
	/// Fill every surface slot on the specimens with a hazard, alternating the two kinds — the
	/// point of the shot is what the furniture looks like sitting on the road.
	/// </summary>
	private static void DressWithHazards(Dictionary<string, TrackPiece> pieces)
	{
		foreach (TrackPiece piece in pieces.Values)
		{
			var index = 0;

			foreach (TrackHazardSlot slot in piece.GetChildren().OfType<TrackHazardSlot>())
			{
				if (slot.Kind != HazardSlotKind.Surface)
					continue;

				HazardKind kind = index % 2 == 0 ? HazardKind.PopUpRamp : HazardKind.LaunchPad;
				slot.AddChild(TrackHazard.Create(kind));
				index++;
			}
		}
	}


	// ---- Car cel spike ----

	/// <summary>Bodies sampled for the spike, chosen to span the fleet's shapes: a hard-creased
	/// wedge, a round cartoon, and a boxy hatch. Split normals bite hardest on creases, so the
	/// wedge is the one that decides whether the inverted hull is usable.</summary>
	private static readonly string[] SpikeBodies =
	{
		"res://assets/cars/Body/A_Wedge_Body.fbx",
		"res://assets/cars/Body/C_Cartoon_Body.fbx",
		"res://assets/cars/Body/H_Hatch_Body.fbx",
	};

	/// <summary>
	/// Put the cel car shader and the pixel-width outline on real exported bodies and photograph
	/// them near and far.
	///
	/// Two questions, and only the models can answer them: does a constant-pixel outline actually
	/// hold its weight across distance, and do the FBXs' split normals tear the hull open at their
	/// hard edges. Both are invisible on road geometry, which is smooth-shaded and huge.
	/// </summary>
	private async void RunCarSpike()
	{
		AddChild(new WorldEnvironment
		{
			Environment = GD.Load<Godot.Environment>("res://resources/tiles/styles/cel_env.tres"),
		});
		AddChild(new DirectionalLight3D
		{
			Name = "Sun",
			Transform = SunTransform,
			LightEnergy = 1.15f,
			LightAngularDistance = 1.0f,
			ShadowEnabled = true,
			ShadowBias = 0.06f,
			ShadowNormalBias = 1.5f,
			DirectionalShadowMaxDistance = 900.0f,
		});

		var car = GD.Load<Shader>("res://resources/cars/cel_car.gdshader");

		for (var i = 0; i < SpikeBodies.Length; i++)
		{
			if (GD.Load<PackedScene>(SpikeBodies[i])?.Instantiate() is not Node3D body)
			{
				GD.PushError($"[ShotRunner] Could not instance {SpikeBodies[i]}.");
				continue;
			}

			body.Position = new Vector3((i - 1) * 4.0f, 0.0f, 0.0f);
			AddChild(body);
			Celshade(body, car, MasterTrack.Racer.CarVariants.ColourAt(i));
		}

		for (var i = 0; i < 6; i++)
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		var camera = new Camera3D { Name = "ShotCamera", Fov = 70.0f, Far = 6000.0f, Current = true };

		// The outline is a compositor pass rather than anything on the meshes, so a camera is
		// all it needs to attach to.
		var compositor = new Compositor();
		compositor.CompositorEffects = new Godot.Collections.Array<CompositorEffect>
		{
			new MasterTrack.Rendering.OutlineEffect(),
		};
		camera.Compositor = compositor;
		AddChild(camera);

		DirAccess.MakeDirRecursiveAbsolute(_outDir);

		// Near, mid and far on the same cars. If the outline is holding a constant pixel width,
		// the line looks identical in all three; if it is still metre-based it fattens as we close.
		foreach ((string name, Vector3 eye, Vector3 look) in new[]
				 {
					 ("cars-near", new Vector3(2.6f, 1.5f, 4.2f), new Vector3(0.0f, 0.5f, 0.0f)),
					 ("cars-mid", new Vector3(7.0f, 3.4f, 11.0f), new Vector3(0.0f, 0.4f, 0.0f)),
					 ("cars-far", new Vector3(24.0f, 11.0f, 38.0f), new Vector3(0.0f, 0.3f, 0.0f)),
					 ("cars-crease", new Vector3(-3.6f, 1.2f, 2.0f), new Vector3(-4.0f, 0.5f, 0.0f)),
				 })
		{
			camera.GlobalPosition = eye;
			camera.LookAt(look, Vector3.Up);

			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			Error error = GetViewport().GetTexture().GetImage().SavePng($"{_outDir}/{name}.png");
			GD.Print(error == Error.Ok
						 ? $"[ShotRunner] Wrote {_outDir}/{name}.png"
						 : $"[ShotRunner] Could not write {name}: {error}");
		}

		GetTree().Quit();
	}

	/// <summary>
	/// The cel equivalent of what <see cref="Vehicles.FlatShade"/> does today: rebuild each
	/// surface's material keeping everything that says what the surface IS, replacing only how
	/// light lands on it, and hang the outline off it as a second pass.
	///
	/// Per surface rather than a material override for FlatShade's reason — an override would
	/// paint the windows and headlights in bodywork colour — and reading the mesh's own authored
	/// material rather than the active one so this stays idempotent.
	/// </summary>
	private static void Celshade(Node node, Shader shader, Color paint)
	{
		if (node is MeshInstance3D mesh && mesh.Mesh != null)
		{
			for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
			{
				var source = mesh.Mesh.SurfaceGetMaterial(surface) as BaseMaterial3D;
				bool isPaint = source?.ResourceName.EndsWith("_Paint", System.StringComparison.Ordinal) == true;

				var material = new ShaderMaterial { Shader = shader };
				material.SetShaderParameter("albedo", isPaint ? paint : source?.AlbedoColor ?? Colors.White);

				if (source?.AlbedoTexture is { } texture)
				{
					material.SetShaderParameter("albedo_texture", texture);
					material.SetShaderParameter("use_texture", true);
				}

				mesh.SetSurfaceOverrideMaterial(surface, material);
			}
		}

		foreach (Node child in node.GetChildren())
			Celshade(child, shader, paint);
	}

	// ---- Shared ----

	private async System.Threading.Tasks.Task Shoot(
		(string Piece, string Shot, Vector3 Eye, Vector3 Look)[] shots,
		Dictionary<string, TrackPiece> pieces, string suffix)
	{
		var camera = new Camera3D { Name = "ShotCamera", Fov = 70.0f, Far = 6000.0f, Current = true };

		// The outline is a compositor pass rather than anything on the meshes, so a camera is
		// all it needs to attach to.
		var compositor = new Compositor();
		compositor.CompositorEffects = new Godot.Collections.Array<CompositorEffect>
		{
			new MasterTrack.Rendering.OutlineEffect(),
		};
		camera.Compositor = compositor;
		AddChild(camera);

		DirAccess.MakeDirRecursiveAbsolute(_outDir);

		// The car close-up, if this run found one. World-framed rather than piece-framed.
		if (_carFrame is { } car)
		{
			camera.GlobalPosition = car * new Vector3(2.2f, 1.9f, 6.5f);
			camera.LookAt(car * new Vector3(0.0f, 1.0f, 0.0f), Vector3.Up);

			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			GetViewport().GetTexture().GetImage().SavePng($"{_outDir}/car-closeup{suffix}.png");
			GD.Print($"[ShotRunner] Wrote {_outDir}/car-closeup{suffix}.png");
		}

		foreach ((string pieceName, string shot, Vector3 eye, Vector3 look) in shots)
		{
			if (!pieces.TryGetValue(pieceName, out TrackPiece? piece))
			{
				GD.PushWarning($"[ShotRunner] No specimen named {pieceName}; skipping {shot}.");
				continue;
			}

			// Authored in the piece's frame and lifted into the world, so every shot is framed on
			// the piece rather than on wherever it happens to have been put.
			Transform3D frame = piece.GlobalTransform * (piece.Entry?.Transform ?? Transform3D.Identity);
			camera.GlobalPosition = frame * eye;
			camera.LookAt(frame * look, Vector3.Up);

			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
			Image image = GetViewport().GetTexture().GetImage();

			string path = $"{_outDir}/{shot}{suffix}.png";
			Error error = image.SavePng(path);
			GD.Print(error == Error.Ok
						 ? $"[ShotRunner] Wrote {path}"
						 : $"[ShotRunner] Could not write {path}: {error}");
		}
	}
}
