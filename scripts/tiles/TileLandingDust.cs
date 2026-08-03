using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// The dust a landing tile throws out of its own edges.
///
/// The tile does not sink into place, it <b>slams</b>: a cell and a half of road arriving at
/// 130 m/s. A soft poof would undersell that, so this is an impact — puffs fired outward and low
/// off the whole perimeter at once, the way anything heavy displaces the air under it.
///
/// <b>The ring is the tile's real footprint.</b> That is the house rule the blast radii already
/// follow, and here it does a second job: a racer reads "that is where road now is" straight off
/// the dust, without having to work out what shape landed.
///
/// Built in <see cref="_Ready"/> rather than authored into a scene, for the reason the track
/// builds its own geometry — one definition, and anything that wants the effect gets it by having
/// one as a child. It emits once, on demand, and takes itself away when the last puff dies.
///
/// Not replicated. Every peer builds an identical burst off its own copy of the landing, which is
/// the rule the whole sentry kit lives by; nothing here adds a packet.
/// </summary>
[GlobalClass]
public partial class TileLandingDust : GpuParticles3D
{
	/// <summary>Width and length of the footprint the ring is thrown off, in metres.</summary>
	[Export] public Vector2 RingSize { get; set; } = new(TrackTile.Size, TrackTile.Size);

	/// <summary>
	/// Puffs in the burst. Sized against a whole tile edge rather than against a wheel — this is
	/// a perimeter of well over a hundred metres, and a handful of puffs on it reads as a bug.
	/// </summary>
	[Export] public int PuffCount { get; set; } = 120;

	/// <summary>How long a puff lasts, in seconds. Short on purpose: see the risk note in
	/// <c>docs/game-feel-plan.md</c> — the builder is looking at the whole track at once, and dust
	/// that lingers on every landing is a wall of alpha from altitude.</summary>
	[Export] public float DustLifetime { get; set; } = 1.15f;

	/// <summary>Width of a puff at full size, in metres.</summary>
	[Export] public float PuffSize { get; set; } = 7.0f;

	/// <summary>Colour of the dust, and how opaque a fresh puff is.</summary>
	[Export] public Color DustColor { get; set; } = new(0.74f, 0.71f, 0.64f, 0.5f);

	/// <summary>How fast a puff is thrown away from the tile edge, in m/s.</summary>
	[Export] public float OutwardSpeedMin { get; set; } = 5.0f;

	[Export] public float OutwardSpeedMax { get; set; } = 16.0f;

	private float _age;
	private bool _fired;
	private ParticleProcessMaterial _process = null!;
	private QuadMesh _puff = null!;
	private StandardMaterial3D _puffPaint = null!;
	private GradientTexture2D _puffTexture = null!;
	private GradientTexture1D _ramp = null!;
	private CurveTexture _scale = null!;

	public override void _Ready()
	{
		Lifetime = DustLifetime;

		// The whole burst goes out in one frame, so the buffer only has to hold it once. A little
		// headroom so the last puffs aren't recycling the first ones.
		Amount = PuffCount + 8;

		// World space: the dust is knocked off the road and stays where it was knocked off. In
		// local space it would ride the tile, which is the one thing it must not do — see Burst,
		// where the same choice decides what space the emission is in.
		LocalCoords = false;

		// Manual emission only. Left true, this would pour dust out of the tile's centre from the
		// moment it existed.
		Emitting = false;

		// A burst spans the whole tile, so the node's own bounds say nothing useful about where
		// its particles are. Without this the ring is culled the instant the emitter's little
		// default box leaves the frustum, which on the board camera is most of the time.
		float reach = Mathf.Max(RingSize.X, RingSize.Y) * 0.5f + OutwardSpeedMax * DustLifetime;
		VisibilityAabb = new Aabb(new Vector3(-reach, -6.0f, -reach),
								  new Vector3(reach * 2.0f, 30.0f, reach * 2.0f));

		ProcessMaterial = BuildProcessMaterial();
		DrawPass1 = BuildPuffMesh();

		SetProcess(false);
	}

	/// <summary>
	/// Throw the ring. One shot: call it once, at the moment of impact, and the node cleans itself
	/// up when the dust has settled.
	/// </summary>
	public void Burst()
	{
		if (_fired)
			return;

		_fired = true;
		SetProcess(true);

		float width = RingSize.X;
		float length = RingSize.Y;
		float perimeter = 2.0f * (width + length);

		if (perimeter <= 0.0f || PuffCount <= 0)
			return;

		for (var i = 0; i < PuffCount; i++)
		{
			// Evenly spaced with a jitter under one step, so the ring is dense the whole way round
			// without the puffs landing in a visible row of dots.
			float along = Mathf.PosMod((i + GD.Randf() * 0.8f) / PuffCount * perimeter, perimeter);
			(Vector3 point, Vector3 outward) = WalkPerimeter(along, width, length);

			EmitPuff(point, outward);
		}
	}

	public override void _Process(double delta)
	{
		_age += (float)delta;

		// Nothing to fade: the last puff to go out dies a lifetime after the burst, and then the
		// node has no reason to exist.
		if (_age >= DustLifetime + 0.3f)
			QueueFree();
	}

	/// <summary>
	/// A point on the footprint's edge and the direction pointing out of it, given a distance
	/// walked around the perimeter starting at the entry seam.
	///
	/// Local space runs "north" like everything else on a tile: the racer comes in over +Z.
	/// </summary>
	private static (Vector3 Point, Vector3 Outward) WalkPerimeter(float along, float width, float length)
	{
		float halfWidth = width * 0.5f;
		float halfLength = length * 0.5f;

		if (along < width)
			return (new Vector3(-halfWidth + along, 0.0f, halfLength), Vector3.Back);

		along -= width;
		if (along < length)
			return (new Vector3(halfWidth, 0.0f, halfLength - along), Vector3.Right);

		along -= length;
		if (along < width)
			return (new Vector3(halfWidth - along, 0.0f, -halfLength), Vector3.Forward);

		along -= width;
		return (new Vector3(-halfWidth, 0.0f, -halfLength + along), Vector3.Left);
	}

	/// <summary>
	/// One puff off the edge, thrown outward and slightly up.
	///
	/// Both the transform and the velocity handed to <c>EmitParticle</c> are read in the emitter's
	/// coordinate space, and <see cref="GpuParticles3D.LocalCoords"/> is false — so both are taken
	/// through the node's global transform here rather than being left in tile space, which on any
	/// tile that is not pointing down the world axis would fire the whole ring off at an angle.
	/// </summary>
	private void EmitPuff(Vector3 localPoint, Vector3 localOutward)
	{
		// A little spread either side of the edge, so the ring has thickness rather than being a
		// wire, and a little height so it doesn't read as a flat decal.
		Vector3 jitter = localOutward * (GD.Randf() * 3.0f - 1.0f)
						 + Vector3.Up * (GD.Randf() * 1.5f);

		Transform3D spawn = GlobalTransform;
		spawn.Origin = ToGlobal(localPoint + jitter);

		Vector3 outward = (GlobalBasis * localOutward).Normalized();
		Vector3 velocity = outward * Mathf.Lerp(OutwardSpeedMin, OutwardSpeedMax, GD.Randf())
						   + Vector3.Up * Mathf.Lerp(1.5f, 5.0f, GD.Randf());

		EmitParticle(spawn, velocity, Colors.White, Colors.White,
					 (uint)(EmitFlags.Position | EmitFlags.Velocity));
	}

	/// <summary>
	/// How a puff behaves once it exists: shoved out, stalls almost at once, swells and fades.
	///
	/// The heavy damping is what makes it read as displaced air rather than as an explosion —
	/// dust thrown by an impact travels a short way fast and then simply hangs.
	/// </summary>
	private ParticleProcessMaterial BuildProcessMaterial()
	{
		var gradient = new Gradient();
		gradient.SetColor(0, DustColor);
		gradient.SetColor(1, DustColor with { A = 0.0f });
		_ramp = new GradientTexture1D { Gradient = gradient };

		var curve = new Curve();
		curve.AddPoint(new Vector2(0.0f, 0.3f));
		curve.AddPoint(new Vector2(1.0f, 1.0f));
		_scale = new CurveTexture { Curve = curve };

		_process = new ParticleProcessMaterial
		{
			// Every particle's direction comes from the velocity handed to EmitParticle, so the
			// material contributes none of its own.
			Direction = Vector3.Up,
			Spread = 0.0f,
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,

			// Rises, barely. This is the dust hanging over the joint, not smoke off a fire.
			Gravity = new Vector3(0.0f, 1.2f, 0.0f),

			DampingMin = 6.0f,
			DampingMax = 11.0f,

			ScaleMin = PuffSize * 0.65f,
			ScaleMax = PuffSize * 1.35f,
			ScaleCurve = _scale,

			AngularVelocityMin = -40.0f,
			AngularVelocityMax = 40.0f,

			ColorRamp = _ramp,
		};

		return _process;
	}

	/// <summary>
	/// The quad a puff is drawn on, softened by a radial falloff built here.
	///
	/// <see cref="Vehicles.WheelSmoke"/> gets away with a bare quad because a tire puff is under a
	/// metre across; at seven metres a hard-edged square reads as a sprite sheet that failed to
	/// load. The texture is four kilobytes of generated gradient rather than an asset, which keeps
	/// the effect self-contained the way the rest of the kit is.
	///
	/// Unshaded and billboarded — dust that took the scene's lighting would go black in a tunnel,
	/// and dust that did not face the camera would vanish edge-on.
	/// </summary>
	private QuadMesh BuildPuffMesh()
	{
		var falloff = new Gradient();
		falloff.SetColor(0, Colors.White);
		falloff.SetColor(1, new Color(1.0f, 1.0f, 1.0f, 0.0f));

		_puffTexture = new GradientTexture2D
		{
			Gradient = falloff,
			Width = 32,
			Height = 32,
			Fill = GradientTexture2D.FillEnum.Radial,
			FillFrom = new Vector2(0.5f, 0.5f),
			FillTo = new Vector2(1.0f, 0.5f),
		};

		_puffPaint = new StandardMaterial3D
		{
			AlbedoTexture = _puffTexture,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
			VertexColorUseAsAlbedo = true,

			// Particles are drawn in a heap on top of each other; depth-writing them would have
			// each puff cut a hole in the ones behind it.
			DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
			AlbedoColor = Colors.White,
		};

		_puff = new QuadMesh { Size = Vector2.One, Material = _puffPaint };
		return _puff;
	}

	/// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
	/// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
	public override void _ExitTree()
	{
		_process?.Dispose();
		_process = null!;
		_ramp?.Dispose();
		_ramp = null!;
		_scale?.Dispose();
		_scale = null!;
		_puff?.Dispose();
		_puff = null!;
		_puffPaint?.Dispose();
		_puffPaint = null!;
		_puffTexture?.Dispose();
		_puffTexture = null!;
	}
}
