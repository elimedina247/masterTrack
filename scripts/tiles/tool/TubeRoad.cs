using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// A waterslide tube: a circular pipe swept along a path, with a slice of the top left open —
/// the piece of vocabulary the chutes and tunnels were faking with boxes.
///
/// The section is a ring with a bite taken out: riders run on the inside of the pipe, the floor
/// is the pipe's lowest line and sits exactly on the path, and <see cref="OpennessDegrees"/> says
/// how much of the top is missing — 0 is a sealed pipe, 360 would be no pipe at all, and the
/// hundred-ish of a waterpark slide keeps the camera outside and the racer visibly inside.
///
/// <b>The tube follows the path's tilt.</b> The sweep reads the curve's up vector with tilt
/// applied, so a path that rolls — a chute banking over to match a bowl's rim — carries its
/// opening around with it. That is what a box channel could never do without shearing.
///
/// Follows the Path3D named by <see cref="PathNode"/>, or the owning piece's Spine when left
/// empty — so a whole piece can be a tube by dropping one of these under Build and nothing else.
/// A <see cref="CsgMesh3D"/> like its siblings: unions, combiner collision, ordinary Bake.
/// </summary>
[Tool]
[GlobalClass]
public partial class TubeRoad : CsgMesh3D
{
	/// <summary>The path swept along. Empty finds the owning piece's Spine.</summary>
	[Export]
	public NodePath PathNode { get; set; } = new();

	/// <summary>Inner radius of the pipe, in metres. The drivable floor is the bottom of the
	/// circle, usably flat for roughly the middle three-quarters of the radius each side.</summary>
	[Export(PropertyHint.Range, "4,30,0.5")]
	public float Radius { get; set; } = 13.0f;

	/// <summary>
	/// Width of a flat floor across the pipe's bottom, in metres. 0 keeps the pure circle.
	///
	/// What makes a tube drivable rather than merely traversable: a car in a round pipe is always
	/// on a camber unless it is dead centre, and the pipe fights every correction. The flat is a
	/// chord across the bottom — the walls still curve away from its edges exactly as the circle
	/// did, so the section reads as a waterslide with a floor, not a box with round corners.
	/// </summary>
	[Export(PropertyHint.Range, "0,40,0.5")]
	public float FloorWidth { get; set; } = 0.0f;

	/// <summary>Degrees of the pipe's top left open. 0 seals it.</summary>
	[Export(PropertyHint.Range, "0,270,5")]
	public float OpennessDegrees { get; set; } = 100.0f;

	/// <summary>Wall thickness, radially outward.</summary>
	[Export(PropertyHint.Range, "0.4,5,0.1")]
	public float Thickness { get; set; } = 1.2f;

	/// <summary>Metres of path per ring of facets.</summary>
	[Export(PropertyHint.Range, "1,12,0.5")]
	public float SampleInterval { get; set; } = 3.0f;

	/// <summary>Facets around the pipe's full circle. The enclosed arc gets its share.</summary>
	[Export(PropertyHint.Range, "12,64,1")]
	public int RingSegments { get; set; } = 28;

	[Export]
	public Material? SurfaceMaterial { get; set; }

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

		// The piece's spine regenerates in the piece's own _Ready, which runs after this one.
		CallDeferred(MethodName.Rebuild);
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

		// The node's own transform is part of the shape, because the mesh is generated to
		// compensate for it — the tube glues to its path, not to its node. Without this, dragging
		// the node shows a stale mesh moving where the geometry will not actually be, and the lie
		// holds right up until the next rebuild snaps it home.
		if (IsInsideTree())
			hash.Add(GlobalTransform);

		hash.Add(PathNode.ToString());
		hash.Add(Radius);
		hash.Add(FloorWidth);
		hash.Add(OpennessDegrees);
		hash.Add(Thickness);
		hash.Add(SampleInterval);
		hash.Add(RingSegments);

		if (Path() is { Curve: { } curve } path)
		{
			// The path's own transform too: the tube is glued to where the path IS, so dragging
			// the Path3D node has to re-sweep just as surely as editing its points does.
			if (path.IsInsideTree())
				hash.Add(path.GlobalTransform);

			for (var i = 0; i < curve.PointCount; i++)
			{
				hash.Add(curve.GetPointPosition(i));
				hash.Add(curve.GetPointIn(i));
				hash.Add(curve.GetPointOut(i));
				hash.Add(curve.GetPointTilt(i));
			}
		}

		return hash.ToHashCode();
	}

	private Path3D? Path()
	{
		if (!PathNode.IsEmpty && GetNodeOrNull<Path3D>(PathNode) is { } chosen)
			return chosen;

		for (Node? node = GetParent(); node != null; node = node.GetParent())
		{
			if (node is TrackPiece piece)
				return piece.Spine;
		}

		return null;
	}

	public void Rebuild()
	{
		if (Path() is not { Curve: { PointCount: >= 2 } curve } path
			|| curve.GetBakedLength() < 1.0f)
		{
			Mesh = null;
			return;
		}

		float length = curve.GetBakedLength();
		int rings = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(1.0f, SampleInterval)));

		// The section, in the sweep frame's (right, up) plane with the path at (0, 0): a flat
		// floor across the bottom, circular walls curving away from its edges, up to the lips of
		// the opening. Zero floor is the pure circle — the flat's edges meet at the bottom point.
		(Vector2[] innerSection, Vector2[] outerSection) = BuildSection();
		int stations = innerSection.Length - 1;

		// This node may sit anywhere under Build while the path sits elsewhere; everything is
		// generated in this node's own space, so the two transforms have to be reconciled.
		Transform3D intoLocal = GlobalTransform.AffineInverse() * path.GlobalTransform;

		var inner = new Vector3[rings + 1][];
		var outer = new Vector3[rings + 1][];

		for (var k = 0; k <= rings; k++)
		{
			float offset = length * k / rings;

			Vector3 origin = intoLocal * curve.SampleBaked(offset, cubic: true);
			Vector3 forward = intoLocal.Basis * TangentOf(curve, offset, length);
			Vector3 up = (intoLocal.Basis * curve.SampleBakedUpVector(offset, applyTilt: true))
				.Normalized();

			Vector3 right = forward.Cross(up);
			if (right.LengthSquared() < 1e-6f)
			{
				inner[k] = inner[Mathf.Max(0, k - 1)] ?? new Vector3[stations + 1];
				outer[k] = outer[Mathf.Max(0, k - 1)] ?? new Vector3[stations + 1];
				continue;
			}

			right = right.Normalized();
			up = right.Cross(forward).Normalized();

			inner[k] = new Vector3[stations + 1];
			outer[k] = new Vector3[stations + 1];

			for (var i = 0; i <= stations; i++)
			{
				inner[k][i] = origin + right * innerSection[i].X + up * innerSection[i].Y;
				outer[k][i] = origin + right * outerSection[i].X + up * outerSection[i].Y;
			}
		}

		var triangles = new List<Vector3>();

		for (var k = 0; k < rings; k++)
		{
			for (var i = 0; i < stations; i++)
			{
				SolidMesh.Quad(triangles, inner[k][i], inner[k][i + 1],
							   inner[k + 1][i + 1], inner[k + 1][i]);
				SolidMesh.Quad(triangles, outer[k][i], outer[k + 1][i],
							   outer[k + 1][i + 1], outer[k][i + 1]);
			}

			// The lips either side of the opening.
			SolidMesh.Quad(triangles, inner[k][0], inner[k + 1][0],
						   outer[k + 1][0], outer[k][0]);
			SolidMesh.Quad(triangles, inner[k][stations], outer[k][stations],
						   outer[k + 1][stations], inner[k + 1][stations]);
		}

		// Mouth rings at both ends close the solid.
		for (var i = 0; i < stations; i++)
		{
			SolidMesh.Quad(triangles, inner[0][i], outer[0][i],
						   outer[0][i + 1], inner[0][i + 1]);
			SolidMesh.Quad(triangles, inner[rings][i], inner[rings][i + 1],
						   outer[rings][i + 1], outer[rings][i]);
		}

		Mesh = SolidMesh.Commit(triangles, SurfaceMaterial);
	}

	/// <summary>
	/// The inner and outer profiles, lips-to-lips through the floor, with matching point counts so
	/// the sweep can pair them ring for ring.
	///
	/// Geometry of the flat: a chord of the same circle, so the walls leave the floor's edges at
	/// exactly the tangent the circle had there — no crease where floor meets wall. The circle's
	/// centre rises to <c>sqrt(R² - (w/2)²)</c> above the floor to make that true, and the outer
	/// shell repeats the construction one thickness further out, its own floor one thickness
	/// down, which keeps the shell watertight without mitred corners.
	/// </summary>
	private (Vector2[] Inner, Vector2[] Outer) BuildSection()
	{
		float half = Mathf.DegToRad(360.0f - Mathf.Clamp(OpennessDegrees, 0.0f, 270.0f)) * 0.5f;

		float w = Mathf.Min(FloorWidth * 0.5f, Radius * 0.95f);
		float rise = Mathf.Sqrt(Mathf.Max(0.01f, Radius * Radius - w * w));

		float floorAngle = Mathf.Asin(w / Radius);

		float outerRadius = Radius + Thickness;
		float outerFloorAngle = Mathf.Acos(Mathf.Min(1.0f, (rise + Thickness) / outerRadius));
		float outerW = outerRadius * Mathf.Sin(outerFloorAngle);

		int arcSegments = Mathf.Max(4,
			Mathf.CeilToInt(RingSegments * (half - floorAngle) / Mathf.Tau));
		int floorSegments = w > 0.05f ? 2 : 1;

		var innerPoints = new List<Vector2>();
		var outerPoints = new List<Vector2>();

		void Side(List<Vector2> points, float radius, float fromAngle, float toAngle, int steps,
				  bool skipFirst)
		{
			for (var i = skipFirst ? 1 : 0; i <= steps; i++)
			{
				float angle = Mathf.Lerp(fromAngle, toAngle, (float)i / steps);
				points.Add(new Vector2(radius * Mathf.Sin(angle),
									   rise - radius * Mathf.Cos(angle)));
			}
		}

		void Floor(List<Vector2> points, float halfWidth, float y, int steps)
		{
			for (var i = 1; i < steps; i++)
				points.Add(new Vector2(Mathf.Lerp(-halfWidth, halfWidth, (float)i / steps), y));
		}

		// With no floor the two arcs would both contribute the bottom point; the duplicate is a
		// zero-width strip of quads all the way down the pipe, and CSG has no sense of humour
		// about degenerate triangles.
		bool seamless = floorSegments == 1;

		Side(innerPoints, Radius, -half, -floorAngle, arcSegments, skipFirst: false);
		Floor(innerPoints, w, 0.0f, floorSegments);
		Side(innerPoints, Radius, floorAngle, half, arcSegments, skipFirst: seamless);

		Side(outerPoints, outerRadius, -half, -outerFloorAngle, arcSegments, skipFirst: false);
		Floor(outerPoints, outerW, -Thickness, floorSegments);
		Side(outerPoints, outerRadius, outerFloorAngle, half, arcSegments, skipFirst: seamless);

		return (innerPoints.ToArray(), outerPoints.ToArray());
	}

	private static Vector3 TangentOf(Curve3D curve, float offset, float length)
	{
		float epsilon = Mathf.Max(0.05f, length * 0.001f);

		Vector3 tangent = curve.SampleBaked(Mathf.Min(length, offset + epsilon), cubic: true)
						  - curve.SampleBaked(Mathf.Max(0.0f, offset - epsilon), cubic: true);

		return tangent.LengthSquared() < 1e-9f ? Vector3.Forward : tangent.Normalized();
	}
}
