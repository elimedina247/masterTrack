using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// The dark patch a descending tile puts on the place it is about to occupy.
///
/// A tile falls at 130 m/s over two and a half cells — about a second — which is very little
/// warning for something the size of a road. The shadow converts the whole descent into
/// information a racer can act on: it appears the instant the placement lands, it sits exactly
/// where the road will be, and it tightens onto that footprint as the tile comes down. A racer
/// who never looks up still knows that stretch is spoken for.
///
/// <b>It is deliberately subordinate to the tile.</b> It starts wide and nearly invisible and
/// only reaches its real size and its full weight as the slab arrives — so the thing you watch
/// is the tile, and the shadow is the thing you notice. A telegraph clearer than the object it
/// telegraphs would teach racers to read the ground instead of the sky, which loses the drop.
///
/// Built in code and freed at landing, like every other effect in the kit; the material and mesh
/// are C# wrappers, so they are disposed in <see cref="_ExitTree"/> while the engine is still up.
/// </summary>
[GlobalClass]
public partial class TileDropShadow : MeshInstance3D
{
	/// <summary>Width and length of the tile's real footprint, in metres. Set before adding.</summary>
	[Export] public Vector2 Footprint { get; set; } = new(TrackTile.Size, TrackTile.Size);

	/// <summary>
	/// How much larger than the footprint the patch starts out, as a multiplier. This is the only
	/// dishonest number in the effect and it is honest about being one: it is a shadow cast from
	/// height, so it is wider than the object at the top of the fall and exact at the bottom.
	/// </summary>
	[Export] public float Spread { get; set; } = 1.5f;

	/// <summary>Opacity at the top of the fall, and at the bottom of it.</summary>
	[Export] public float FaintAlpha { get; set; } = 0.05f;

	[Export] public float DarkAlpha { get; set; } = 0.30f;

	[Export] public Color ShadowColor { get; set; } = new(0.03f, 0.04f, 0.07f);

	private StandardMaterial3D _paint = null!;
	private QuadMesh _quad = null!;

	public override void _Ready()
	{
		_paint = new StandardMaterial3D
		{
			AlbedoColor = ShadowColor with { A = FaintAlpha },
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,

			// Nothing is drawn behind it and it is co-planar with road that does not exist yet,
			// so it has no depth of its own worth writing.
			DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,

			// A single-sided quad would vanish for anyone under it — which, on a track that
			// crosses over itself, is a racer on the lower road.
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		};

		_quad = new QuadMesh { Size = Footprint, Material = _paint };
		Mesh = _quad;

		// A QuadMesh stands up in the XY plane; laid back onto the ground its local Y runs down
		// the track and its normal points at the sky.
		RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f);

		SetProgress(0.0f);
	}

	/// <summary>
	/// Where the tile is in its descent, 0 at the top and 1 the moment it lands.
	///
	/// Size closes linearly so the patch tracks the slab, and weight comes in squared so most of
	/// the darkening happens in the last few metres — which is the part there is still time to
	/// react to.
	/// </summary>
	public void SetProgress(float fallen)
	{
		if (_paint == null)
			return;

		fallen = Mathf.Clamp(fallen, 0.0f, 1.0f);

		// Z is the quad's normal and has nothing to scale.
		float size = Mathf.Lerp(Spread, 1.0f, fallen);
		Scale = new Vector3(size, size, 1.0f);

		_paint.AlbedoColor = ShadowColor with { A = Mathf.Lerp(FaintAlpha, DarkAlpha, fallen * fallen) };
	}

	/// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
	/// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
	public override void _ExitTree()
	{
		_quad?.Dispose();
		_quad = null!;
		_paint?.Dispose();
		_paint = null!;
	}
}
