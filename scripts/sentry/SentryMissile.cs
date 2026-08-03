using Godot;
using MasterTrack.Audio;
using MasterTrack.Racer;
using MasterTrack.Tiles;
using MasterTrack.Vehicles;

namespace MasterTrack.Sentry;

/// <summary>
/// The sentry's missile: a slow, extremely visible drop onto a marked spot, then a blast that
/// throws every car near it.
///
/// The warning is short but honest. It spawns high enough to be seen coming, dives hard, and a
/// pulsing ring sits on the impact point the entire way down — about two seconds of "not here",
/// the same lead beat every delayed sentry action gives. A racer who eats it was told, briefly.
///
/// Not replicated. Every peer builds one from the same launch broadcast and flies it with the
/// same constants, so they all detonate at the same spot within a frame of each other. The
/// blast follows the same rule as car-to-car impacts: each machine applies the impulse to the
/// cars <i>it</i> simulates and nobody else's — see <see cref="RacerController"/>.
///
/// The node itself sits at the impact point; only the <see cref="_body"/> child descends. That
/// keeps the ring and the blast maths on the target with no bookkeeping.
/// </summary>
public partial class SentryMissile : Node3D
{
	/// <summary>How high above the target the missile starts, in metres.</summary>
	private const float SpawnHeight = 150.0f;

	/// <summary>Descent speed, in metres per second. With the height, ~2 s of warning — the
	/// same window as <see cref="SentryActions.LeadSeconds"/>.</summary>
	private const float DescendSpeed = 75.0f;

	/// <summary>How far from the impact point the blast still throws cars, in metres.</summary>
	public const float ExplosionRadius = TrackTile.Size * 1.1f;

	/// <summary>Speed the blast adds to a car at the centre of it, in m/s. Well past a car's top
	/// speed — a direct hit is a launch, not a shove. Falls off with the square of distance.</summary>
	private const float ExplosionStrength = 80.0f;

	/// <summary>
	/// The authored missile, straight from Blender the way the cars are. Nose along -Z at about
	/// 1.7 m — see the AABBs on import — so it is scaled and pitched here rather than asking the
	/// asset to be authored pre-rotated for one use.
	/// </summary>
	public const string ModelPath = "res://assets/weapons/long_range_missile.blend";

	/// <summary>Model units to metres. ~1.7 m authored × 5 ≈ an 8.4 m rocket in the world.</summary>
	private const float ModelScale = 5.0f;

	/// <summary>The cartoon whistle of the drop — the audible half of the warning ring. It
	/// rides the falling body, so it descends on the racers it is aimed at, and it dies with
	/// the missile: the cut into the bang <i>is</i> the impact.</summary>
	private const string WhistleSfxPath = "res://assets/audio/hazards/missile_whistle.mp3";

	private Node3D _body = null!;
	private StandardMaterial3D _ringMaterial = null!;
	private float _age;

	/// <summary>How far the nose tip hangs below the body's origin, set by whichever body got
	/// built. Contact is when the nose meets the road, not when the middle of the rocket does.</summary>
	private float _noseLength;

	public override void _Ready()
	{
		// The warning: a ring the size of the blast, on the ground, pulsing the whole way down.
		_ringMaterial = new StandardMaterial3D
		{
			AlbedoColor = new Color(1.0f, 0.2f, 0.15f, 0.5f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		AddChild(new MeshInstance3D
		{
			Name = "TargetRing",
			Mesh = new TorusMesh
			{
				InnerRadius = ExplosionRadius * 0.92f,
				OuterRadius = ExplosionRadius,
				Material = _ringMaterial,
			},
			Position = new Vector3(0.0f, 0.6f, 0.0f),
		});

		// The missile itself. Cosmetic only — the "collision" is the altitude check in _Process.
		_body = BuildBody();
		_body.Position = new Vector3(0.0f, SpawnHeight, 0.0f);
		AddChild(_body);

		Sfx.Attach(_body, WhistleSfxPath, unitSize: 30.0f);
	}

	/// <summary>
	/// The rocket: the authored model when it loads, the code-built blockout when it doesn't.
	/// Nose down either way, because it is only ever falling, and the flame is shared — the
	/// model ends at its nozzle, and the glow is the game's VFX language, not bodywork.
	/// </summary>
	private Node3D BuildBody()
	{
		var body = new Node3D { Name = "Body" };

		if (GD.Load<PackedScene>(ModelPath)?.Instantiate() is Node3D model)
		{
			// Same treatment a car body gets on arrival: pitched from its authoring convention
			// (nose along -Z, like the cars) to the way it actually travels, and restyled by
			// FlatShade so the Blender materials light like the rest of the game.
			model.RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f);
			model.Scale = Vector3.One * ModelScale;
			model.AddChild(new FlatShade { Target = model });
			body.AddChild(model);

			// Nose tip at authored -0.98 on the long axis; nozzle mouth at +0.7.
			_noseLength = 0.98f * ModelScale;
			AddFlame(body, 0.7f * ModelScale + 1.3f);
			return body;
		}

		GD.PushWarning($"[Sentry] Could not load {ModelPath}; using the blockout missile.");
		BuildBlockoutBody(body);
		return body;
	}

	/// <summary>
	/// The stand-in rocket, in the game's own art style: faceted low-poly solids under the same
	/// material recipe the cars are restyled to and the track builds with (per-vertex light, no
	/// specular — see <see cref="FlatShade"/>). CSG primitives rather than the Mesh ones because
	/// <c>SmoothFaces = false</c> is the one switch that gives real flat facets from code; the
	/// Mesh primitives only ever shade smooth, which is exactly the look this game is not.
	/// Standalone CSG nodes never run a boolean, so none of the tile-placement CSG cost applies.
	/// </summary>
	private void BuildBlockoutBody(Node3D body)
	{

		StandardMaterial3D red = HouseMaterial(new Color(0.78f, 0.13f, 0.11f));
		StandardMaterial3D cream = HouseMaterial(new Color(0.92f, 0.88f, 0.78f));
		StandardMaterial3D dark = HouseMaterial(new Color(0.16f, 0.16f, 0.19f));

		// Nose cone, pointing at the ground it is about to make worse.
		body.AddChild(new CsgCylinder3D
		{
			Cone = true, Sides = 8, SmoothFaces = false,
			Radius = 1.35f, Height = 2.4f, Material = red,
			Position = new Vector3(0.0f, -3.4f, 0.0f),
			RotationDegrees = new Vector3(180.0f, 0.0f, 0.0f),
		});

		// The shell, with a band where the cone meets it.
		body.AddChild(new CsgCylinder3D
		{
			Sides = 8, SmoothFaces = false,
			Radius = 1.35f, Height = 4.4f, Material = cream,
		});
		body.AddChild(new CsgCylinder3D
		{
			Sides = 8, SmoothFaces = false,
			Radius = 1.45f, Height = 0.55f, Material = red,
			Position = new Vector3(0.0f, -1.9f, 0.0f),
		});

		// Four fins around the tail, thin boxes swept outward.
		for (int i = 0; i < 4; i++)
		{
			var fin = new Node3D { RotationDegrees = new Vector3(0.0f, 90.0f * i, 0.0f) };
			fin.AddChild(new CsgBox3D
			{
				Size = new Vector3(0.22f, 2.0f, 1.5f), Material = red,
				Position = new Vector3(0.0f, 1.6f, 1.75f),
				RotationDegrees = new Vector3(-18.0f, 0.0f, 0.0f),
			});
			body.AddChild(fin);
		}

		// The nozzle.
		body.AddChild(new CsgCylinder3D
		{
			Sides = 8, SmoothFaces = false,
			Radius = 0.9f, Height = 0.9f, Material = dark,
			Position = new Vector3(0.0f, 2.6f, 0.0f),
		});

		_noseLength = 4.6f;
		AddFlame(body, 4.1f);
	}

	/// <summary>The flame out of the tail — unshaded on purpose, like every glow on the board.</summary>
	private static void AddFlame(Node3D body, float height)
	{
		var flame = new StandardMaterial3D
		{
			AlbedoColor = new Color(1.0f, 0.55f, 0.1f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		body.AddChild(new CsgCylinder3D
		{
			Cone = true, Sides = 8, SmoothFaces = false,
			Radius = 0.75f, Height = 2.6f, Material = flame,
			Position = new Vector3(0.0f, height, 0.0f),
		});
	}

	/// <summary>The game's material: per-vertex light, no specular — the same three decisions
	/// <see cref="Vehicles.FlatShade"/> makes for the cars and the track makes for itself.</summary>
	private static StandardMaterial3D HouseMaterial(Color colour) => new()
	{
		AlbedoColor = colour,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
		SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
		Metallic = 0.0f,
		Roughness = 1.0f,
	};

	public override void _Process(double delta)
	{
		_age += (float)delta;

		_body.Position += Vector3.Down * (DescendSpeed * (float)delta);

		// A lazy roll on the way down. Pure theatre, but a falling thing that doesn't move in
		// any way but down reads as a screenshot of a missile rather than a missile.
		_body.RotateY((float)delta * 0.9f);

		// The pulse quickens as it falls — the ring is a countdown for anyone standing in it.
		float urgency = 4.0f + _age * 0.8f;
		Color ring = _ringMaterial.AlbedoColor;
		ring.A = 0.35f + 0.3f * Mathf.Sin(_age * urgency);
		_ringMaterial.AlbedoColor = ring;

		if (_body.Position.Y <= _noseLength)
			Detonate();
	}

	/// <summary>The blast, shared with the barrel bomb — see <see cref="SentryBlast"/> for the
	/// rules on who throws whom.</summary>
	private void Detonate()
	{
		SentryBlast.Explode(this, GlobalPosition, ExplosionRadius, ExplosionStrength);
		QueueFree();
	}
}
