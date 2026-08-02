using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// The ramp box: a CSG box whose running surface can be concaved, so a ramp eases into its climb
/// instead of hinging into it.
///
/// A rotated <c>CSGBox3D</c> meets the road at its full angle in zero metres — the springs take
/// the entire pitch change in one physics step, which is the crash at the foot of every ramp
/// built that way. Here the top face leaves the toe and steepens along a curve; how much is
/// <see cref="Concave"/>'s one slider: 0 is the flat wedge (the old hinge, moved to the toe),
/// 1 leaves the toe at exactly 0° and spends the whole run arriving at the angle.
///
/// <b>The origin is the toe, at surface height</b> — not a box centre. Put the node where the
/// ramp should start and it runs to <c>-Size.Z</c>, rising to <c>Size.Y</c>, with a metre of
/// skirt below to bury in the slab so the union never fights a coplanar face. Turned 180° it is
/// a landing: the same curve ridden crest-to-toe eases a falling car back onto the flat.
///
/// The curve is faceted to <see cref="FacetDegrees"/> rather than a fixed count, because the
/// smoothness overlay's numbers are per facet joint: the budget here is the kick the springs
/// take at each one. A <see cref="CsgMesh3D"/> like its siblings — unions, combiner collision,
/// ordinary Bake — with the driveable top in the smooth bucket and metres along the surface in
/// UV.x, so the color shader reads it like any road.
/// </summary>
[Tool]
[GlobalClass]
public partial class RampBox : CsgMesh3D
{
	/// <summary>Width across the road, total rise, and length of run — X, Y, Z, box-fashion.</summary>
	[Export]
	public Vector3 Size { get; set; } = new(54.0f, 8.0f, 30.0f);

	/// <summary>
	/// How much of the wedge's angle is moved off the toe and onto the curve. 0 is the flat
	/// wedge; 1 starts dead flat and steepens all the way, exiting at roughly twice the wedge's
	/// angle — the rise is the same, so the angle has to come from somewhere.
	/// </summary>
	[Export(PropertyHint.Range, "0,1,0.05")]
	public float Concave { get; set; } = 1.0f;

	/// <summary>
	/// Degrees of surface bend allowed per facet joint — the size of the kick the springs take
	/// crossing each one. ½° is where the overlay starts reading texture at race speed.
	/// </summary>
	[Export(PropertyHint.Range, "0.1,5,0.1")]
	public float FacetDegrees { get; set; } = 0.5f;

	[Export]
	public Material? SurfaceMaterial { get; set; }

	/// <summary>Metres of solid below the toe, for burying in the slab.</summary>
	private const float Skirt = 1.0f;

	private const int MaxSegments = 256;

	private int _shape;

	/// <summary>Derived data never saves — same rule as every generator.</summary>
	public override void _ValidateProperty(Godot.Collections.Dictionary property)
	{
		if (property["name"].AsStringName() == CsgMesh3D.PropertyName.Mesh)
		{
			property["usage"] = (int)(property["usage"].As<PropertyUsageFlags>()
									  & ~PropertyUsageFlags.Storage);
		}
	}

	public override void _Ready()
	{
		if (Engine.IsEditorHint())
		{
			SetProcess(true);
			return;
		}

		Rebuild();
	}

	public override void _Process(double delta)
	{
		int shape = Fingerprint();
		if (shape == _shape)
			return;

		_shape = shape;
		Rebuild();
	}

	private int Fingerprint()
	{
		var hash = new System.HashCode();

		hash.Add(Size);
		hash.Add(Concave);
		hash.Add(FacetDegrees);
		hash.Add(SurfaceMaterial?.GetInstanceId() ?? 0);

		return hash.ToHashCode();
	}

	public void Rebuild()
	{
		float halfWidth = Mathf.Max(0.1f, Size.X) * 0.5f;
		float rise = Mathf.Max(0.0f, Size.Y);
		float run = Mathf.Max(0.1f, Size.Z);
		float concave = Mathf.Clamp(Concave, 0.0f, 1.0f);

		// The top profile, toe to crest: a blend from the straight wedge to the parabola that
		// leaves the toe at 0°. Slope runs from (1-c)·H/L to (1+c)·H/L — same rise, so what the
		// toe gives up the crest collects.
		int segments = SegmentsFor(rise, run, concave);

		var profile = new Vector2[segments + 1];
		for (var i = 0; i <= segments; i++)
		{
			float t = (float)i / segments;
			profile[i] = new Vector2(-run * t, rise * Mathf.Lerp(t, t * t, concave));
		}

		// Metres along the running surface in UV.x — arc length, not plan length, so the color
		// panels neither stretch on the steep end nor let the shader misread travel direction.
		var along = new float[segments + 1];
		for (var i = 1; i <= segments; i++)
			along[i] = along[i - 1] + (profile[i] - profile[i - 1]).Length();

		var top = new MeshBucket { Smooth = true, Material = SurfaceMaterial };
		var shell = new MeshBucket { Smooth = false, Material = SurfaceMaterial };

		for (var i = 0; i < segments; i++)
		{
			(float zNear, float yNear) = (profile[i].X, profile[i].Y);
			(float zFar, float yFar) = (profile[i + 1].X, profile[i + 1].Y);

			Vector2 uvNear = new(along[i], 0.0f);
			Vector2 uvFar = new(along[i + 1], 0.0f);

			SolidMesh.Quad(top,
						   new Vector3(-halfWidth, yNear, zNear),
						   new Vector3(halfWidth, yNear, zNear),
						   new Vector3(halfWidth, yFar, zFar),
						   new Vector3(-halfWidth, yFar, zFar),
						   uvNear, uvNear, uvFar, uvFar);

			// The side walls under the curve, one strip per segment down to the skirt — and the
			// bottom in the same per-segment steps, because the CSG backend wants a manifold: a
			// single long bottom edge against segmented side strips is a row of T-junctions, and
			// the whole brush is silently dropped for them.
			SolidMesh.Quad(shell.Triangles,
						   new Vector3(-halfWidth, yNear, zNear),
						   new Vector3(-halfWidth, yFar, zFar),
						   new Vector3(-halfWidth, -Skirt, zFar),
						   new Vector3(-halfWidth, -Skirt, zNear));
			SolidMesh.Quad(shell.Triangles,
						   new Vector3(halfWidth, -Skirt, zNear),
						   new Vector3(halfWidth, -Skirt, zFar),
						   new Vector3(halfWidth, yFar, zFar),
						   new Vector3(halfWidth, yNear, zNear));
			SolidMesh.Quad(shell.Triangles,
						   new Vector3(-halfWidth, -Skirt, zNear),
						   new Vector3(-halfWidth, -Skirt, zFar),
						   new Vector3(halfWidth, -Skirt, zFar),
						   new Vector3(halfWidth, -Skirt, zNear));
		}

		// The toe's skirt face and the crest's back face close the solid.
		SolidMesh.Quad(shell.Triangles,
					   new Vector3(-halfWidth, -Skirt, 0.0f),
					   new Vector3(halfWidth, -Skirt, 0.0f),
					   new Vector3(halfWidth, 0.0f, 0.0f),
					   new Vector3(-halfWidth, 0.0f, 0.0f));
		SolidMesh.Quad(shell.Triangles,
					   new Vector3(-halfWidth, -Skirt, -run),
					   new Vector3(-halfWidth, rise, -run),
					   new Vector3(halfWidth, rise, -run),
					   new Vector3(halfWidth, -Skirt, -run));

		Mesh = SolidMesh.Commit(new List<MeshBucket> { top, shell });
	}

	/// <summary>
	/// Facets needed to keep every joint under <see cref="FacetDegrees"/>. The slope change per
	/// uniform step is constant — 2cH/L across the whole run — and the angle change per step is
	/// never more than the slope change, so dividing slope by the budget is exact at the flat end
	/// (the landing zone, where the kicks matter) and conservative on the steep end.
	/// </summary>
	private int SegmentsFor(float rise, float run, float concave)
	{
		float slopeRange = 2.0f * concave * rise / run;
		float budget = Mathf.Tan(Mathf.DegToRad(Mathf.Max(0.1f, FacetDegrees)));

		return Mathf.Clamp(Mathf.CeilToInt(slopeRange / budget), 2, MaxSegments);
	}
}
