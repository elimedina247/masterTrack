using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// The smudge left across the joint where a tile has just landed.
///
/// The dust ring is gone in a little over a second, which only serves the racers who happened to
/// be looking at that stretch when it arrived. Everyone else meets the road cold. The scuff is the
/// part of the impact that waits: a band of settled dust across the entry seam that says <i>this
/// joint is fresh</i> to whoever gets here next, and then fades out before it becomes scenery.
///
/// Deliberately the only lingering piece of the landing. Dust on every tile at once is a wall of
/// alpha from the board camera; a thin band on the road is nearly free and reads from both views.
///
/// Fades along the direction of travel rather than stopping at a hard line — a rectangle painted
/// on the road for four seconds reads as a texturing bug, not as dirt.
/// </summary>
[GlobalClass]
public partial class TileSeamScuff : MeshInstance3D
{
	/// <summary>Width across the road and depth down it, in metres. Set before adding.</summary>
	[Export] public Vector2 Patch { get; set; } = new(TrackTile.Size, 9.0f);

	/// <summary>How long the smudge lasts, in seconds, including the fade.</summary>
	[Export] public float Duration { get; set; } = 4.5f;

	/// <summary>
	/// Fraction of <see cref="Duration"/> the smudge holds at full strength before it starts to
	/// go. A scuff that begins fading the instant it exists never reads as having settled.
	/// </summary>
	[Export] public float HoldFraction { get; set; } = 0.45f;

	[Export] public float StartAlpha { get; set; } = 0.28f;

	[Export] public Color ScuffColor { get; set; } = new(0.68f, 0.64f, 0.56f);

	/// <summary>
	/// How far above the road the band sits. Clear of the racing-line stripes, which top out at
	/// about two centimetres, and far enough off the surface not to z-fight with it.
	/// </summary>
	public const float Lift = 0.06f;

	private float _age;
	private StandardMaterial3D _paint = null!;
	private QuadMesh _quad = null!;
	private GradientTexture2D _fade = null!;

	public override void _Ready()
	{
		var gradient = new Gradient();
		gradient.SetColor(0, Colors.White);
		gradient.SetColor(1, new Color(1.0f, 1.0f, 1.0f, 0.0f));

		// Laid flat (see the rotation below) the quad's +Y runs back toward the seam, and V = 1 is
		// that edge — so the fill starts opaque at the joint and thins out down the road.
		_fade = new GradientTexture2D
		{
			Gradient = gradient,
			Width = 4,
			Height = 64,
			Fill = GradientTexture2D.FillEnum.Linear,
			FillFrom = new Vector2(0.5f, 1.0f),
			FillTo = new Vector2(0.5f, 0.0f),
		};

		_paint = new StandardMaterial3D
		{
			AlbedoTexture = _fade,
			AlbedoColor = ScuffColor with { A = StartAlpha },
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		};

		_quad = new QuadMesh { Size = Patch, Material = _paint };
		Mesh = _quad;

		// A QuadMesh stands up in the XY plane; laid back onto the ground its normal points at the
		// sky and its local +Y runs back up the road toward the seam.
		RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f);
	}

	public override void _Process(double delta)
	{
		_age += (float)delta;

		if (_age >= Duration)
		{
			QueueFree();
			return;
		}

		float gone = _age / Mathf.Max(Duration, 0.001f);
		float fade = Mathf.Clamp((1.0f - gone) / Mathf.Max(1.0f - HoldFraction, 0.001f), 0.0f, 1.0f);
		_paint.AlbedoColor = ScuffColor with { A = StartAlpha * fade };
	}

	/// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
	/// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
	public override void _ExitTree()
	{
		_quad?.Dispose();
		_quad = null!;
		_paint?.Dispose();
		_paint = null!;
		_fade?.Dispose();
		_fade = null!;
	}
}
