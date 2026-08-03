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
/// <b>The ring is baked into the emission textures rather than fired by hand.</b> It used to walk
/// the perimeter calling <c>EmitParticle</c>, which is what <see cref="Vehicles.WheelSmoke"/> does
/// and works there — but the smoke emits from a node that has been in the tree for minutes, and this
/// one has to emit in the same frame it is created, on an emitter the renderer has never processed.
/// Nothing came out. A one-shot burst off a point-and-normal texture is the path every ordinary
/// Godot explosion takes, so it does not depend on that timing at all: the perimeter is a texture,
/// and <see cref="Burst"/> is one flag.
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

	/// <summary>
	/// How far the throw is tilted up out of the horizontal, as a fraction of the outward push. A
	/// touch, and only a touch: dust off an impact goes <i>out</i>, and dust that went up would be
	/// a chimney standing on the road.
	/// </summary>
	private const float UpwardLean = 0.3f;

	/// <summary>Degrees of scatter around each puff's outward direction, so the ring is a burst
	/// rather than a hundred and twenty parallel lines.</summary>
	private const float ThrowSpread = 12.0f;

	private float _age;
	private bool _fired;
	private ParticleProcessMaterial _process = null!;
	private QuadMesh _puff = null!;
	private StandardMaterial3D _puffPaint = null!;
	private GradientTexture2D _puffTexture = null!;
	private GradientTexture1D _ramp = null!;
	private CurveTexture _scale = null!;
	private Image _ringPointImage = null!;
	private Image _ringNormalImage = null!;
	private ImageTexture _ringPoints = null!;
	private ImageTexture _ringNormals = null!;

	public override void _Ready()
	{
		Lifetime = DustLifetime;

		// A landing is one event, so the burst is one shot at full explosiveness: every puff in the
		// ring leaves in the same frame the tile stops, and the emitter is done. Amount is exactly
		// the ring — with a one-shot burst nothing is recycling, so there is no headroom to keep.
		Amount = Mathf.Max(1, PuffCount);
		OneShot = true;
		Explosiveness = 1.0f;

		// World space: the dust is knocked off the road and stays where it was knocked off. In
		// local space it would ride the tile, which is the one thing it must not do. The emission
		// points below are still authored in tile space — Godot folds the emitter's transform onto
		// them as each puff spawns, which is what the old hand-rolled burst did with ToGlobal.
		LocalCoords = false;

		// Held until the impact. Left true, the ring would go off the moment the node existed —
		// which is a frame before the tile has finished falling onto it.
		Emitting = false;

		// A burst spans the whole tile, so the node's own bounds say nothing useful about where
		// its particles are. Without this the ring is culled the instant the emitter's little
		// default box leaves the frustum, which on the board camera is most of the time.
		float reach = Mathf.Max(RingSize.X, RingSize.Y) * 0.5f + OutwardSpeedMax * DustLifetime;
		VisibilityAabb = new Aabb(new Vector3(-reach, -6.0f, -reach),
								  new Vector3(reach * 2.0f, 30.0f, reach * 2.0f));

		BuildRing();
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
		Emitting = true;
	}

	public override void _Process(double delta)
	{
		_age += (float)delta;

		// Nothing to fade: every puff goes out together and dies a lifetime later, and then the
		// node has no reason to exist.
		if (_age >= DustLifetime + 0.3f)
			QueueFree();
	}

	/// <summary>
	/// Bake the perimeter into the pair of textures the emitter spawns from: where each puff starts,
	/// and which way it is thrown.
	///
	/// One texel per puff, <see cref="Image.Format.Rgbf"/> so a direction can hold its sign, and
	/// authored in tile space for the reason given at <see cref="GpuParticles3D.LocalCoords"/>. The
	/// jitter is rolled in here rather than per particle, which costs nothing that shows: every
	/// landing builds its own dust node, so no two rings are the same ring.
	/// </summary>
	private void BuildRing()
	{
		int count = Mathf.Max(1, PuffCount);
		float width = Mathf.Max(0.01f, RingSize.X);
		float length = Mathf.Max(0.01f, RingSize.Y);
		float perimeter = 2.0f * (width + length);

		_ringPointImage = Image.CreateEmpty(count, 1, false, Image.Format.Rgbf);
		_ringNormalImage = Image.CreateEmpty(count, 1, false, Image.Format.Rgbf);

		for (var i = 0; i < count; i++)
		{
			// Evenly spaced with a jitter under one step, so the ring is dense the whole way round
			// without the puffs landing in a visible row of dots.
			float along = Mathf.PosMod((i + GD.Randf() * 0.8f) / count * perimeter, perimeter);
			(Vector3 point, Vector3 outward) = WalkPerimeter(along, width, length);

			// A little spread either side of the edge, so the ring has thickness rather than being
			// a wire, and a little height so it doesn't read as a flat decal.
			Vector3 spawn = point
							+ outward * (GD.Randf() * 3.0f - 1.0f)
							+ Vector3.Up * (GD.Randf() * 1.5f);

			Vector3 thrown = (outward + Vector3.Up * UpwardLean).Normalized();

			_ringPointImage.SetPixel(i, 0, new Color(spawn.X, spawn.Y, spawn.Z));
			_ringNormalImage.SetPixel(i, 0, new Color(thrown.X, thrown.Y, thrown.Z));
		}

		_ringPoints = ImageTexture.CreateFromImage(_ringPointImage);
		_ringNormals = ImageTexture.CreateFromImage(_ringNormalImage);
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
			// Every puff starts on the perimeter and is thrown along the direction stored beside it.
			EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.DirectedPoints,
			EmissionPointCount = Mathf.Max(1, PuffCount),
			EmissionPointTexture = _ringPoints,
			EmissionNormalTexture = _ringNormals,

			// Directed points do not aim particles by themselves: the launch direction is built from
			// Direction and Spread as usual and *then* rotated into a frame whose forward is the
			// texel's normal. So +Z is what "straight out along the edge" is spelled as here, and
			// anything else quietly fires the whole ring off at an angle to its own perimeter.
			Direction = Vector3.Back,
			Spread = ThrowSpread,
			InitialVelocityMin = OutwardSpeedMin,
			InitialVelocityMax = OutwardSpeedMax,

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
		_ringPoints?.Dispose();
		_ringPoints = null!;
		_ringNormals?.Dispose();
		_ringNormals = null!;
		_ringPointImage?.Dispose();
		_ringPointImage = null!;
		_ringNormalImage?.Dispose();
		_ringNormalImage = null!;
	}
}
