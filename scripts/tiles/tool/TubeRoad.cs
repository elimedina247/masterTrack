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

	/// <summary>
	/// Where the floor should be facing when the path ends, in the path's space. Zero is off.
	///
	/// This is the pour: a chute that discharges onto a banked surface wants its floor arriving
	/// at that surface's angle, so the riders are already on the angle of attack when they are
	/// shot out. Tilt cannot say this — tilt is relative to the curve's transported frame, which
	/// a path that spirals has quietly rolled — so the contract here is absolute: by the last
	/// ring the floor's normal <i>is</i> this direction, whatever the transport did on the way.
	/// Set it to the receiving surface's own normal at the mouth.
	/// </summary>
	[Export]
	public Vector3 EndUp { get; set; } = Vector3.Zero;

	/// <summary>Metres before the end over which the section rolls to meet
	/// <see cref="EndUp"/>, eased at both ends of the run.</summary>
	[Export(PropertyHint.Range, "5,300,5")]
	public float EndTwistMeters { get; set; } = 60.0f;

	/// <summary>
	/// Read <see cref="EndUp"/> as a point rather than a direction: the floor's normal aims at
	/// it, freshly per sample. For pouring onto curved geometry — aimed at a sphere's centre,
	/// the floor is exactly the wall wherever the path rides the sphere, which one fixed
	/// direction can only manage at a single point.
	/// </summary>
	[Export]
	public bool EndUpIsPoint { get; set; }

	/// <summary>
	/// Sweep the bore instead of the pipe: the volume inside the walls, closed straight across
	/// the opening and capped at both mouths. For subtracting — a tube that dives through other
	/// geometry gets its inside carved clear by a Solid twin set to subtraction, ordered after
	/// the geometry it clears and before the tube itself, sharing the tube's path and section.
	/// </summary>
	[Export]
	public bool Solid { get; set; }

	/// <summary>Metres the solid stands proud of the inner wall, so the cut boundary hides
	/// inside the pipe's shell instead of fighting its riding surface face-to-face.</summary>
	[Export(PropertyHint.Range, "0,2,0.05")]
	public float SolidInflate { get; set; } = 0.3f;

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
		hash.Add(EndUp);
		hash.Add(EndTwistMeters);
		hash.Add(EndUpIsPoint);
		hash.Add(Solid);
		hash.Add(SolidInflate);

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
		(Vector2[] innerSection, Vector2[] outerSection, float[] lean) = BuildSection();

		if (Solid)
		{
			innerSection = Inflate(innerSection, SolidInflate);
			outerSection = innerSection;
		}

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

			if (EndUp != Vector3.Zero)
			{
				float twist = 1.0f - (length - offset) / Mathf.Max(1.0f, EndTwistMeters);
				if (twist > 0.0f)
				{
					twist = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Min(1.0f, twist));
					Vector3 target = EndUpIsPoint
						? (intoLocal * EndUp - origin).Normalized()
						: (intoLocal.Basis * EndUp).Normalized();
					up = up.Slerp(target, twist).Normalized();
				}
			}

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

		if (Solid)
		{
			Mesh = BuildSolid(inner, rings, stations + 1);
			return;
		}

		var tube = new MeshBucket { Smooth = true, Material = SurfaceMaterial };

		// Metres along the path in UV.x, stretched onto a whole number of the shader's color
		// panels so the rhythm closes exactly at both mouths — the same contract every road
		// generator keeps, and the thing that makes a chute readable at speed instead of one
		// long uniform surface.
		float panelSpan = Mathf.Max(1, Mathf.RoundToInt(length / 27.0f)) * 27.0f;

		// The riding surface also writes each station's lateral lean into UV.y — the same
		// contract BankedRoad keeps — so a material with use_uv_turn paints the floor and the
		// walls apart. The distinction lives in the section, not the world: when the section
		// rolls (EndUp), the painted floor lane rolls with it, which is what makes the pattern
		// a line the racers can actually follow through a twist.
		for (var k = 0; k < rings; k++)
		{
			float near = panelSpan * k / rings;
			float far = panelSpan * (k + 1) / rings;

			for (var i = 0; i < stations; i++)
			{
				SolidMesh.Quad(tube, inner[k][i], inner[k][i + 1],
							   inner[k + 1][i + 1], inner[k + 1][i],
							   new Vector2(near, lean[i]), new Vector2(near, lean[i + 1]),
							   new Vector2(far, lean[i + 1]), new Vector2(far, lean[i]));
				SolidMesh.Quad(tube, outer[k][i], outer[k + 1][i],
							   outer[k + 1][i + 1], outer[k][i + 1],
							   new Vector2(near, 0.0f), new Vector2(far, 0.0f),
							   new Vector2(far, 0.0f), new Vector2(near, 0.0f));
			}

			// The lips either side of the opening, leaning as hard as their own station.
			SolidMesh.Quad(tube, inner[k][0], inner[k + 1][0],
						   outer[k + 1][0], outer[k][0],
						   new Vector2(near, lean[0]), new Vector2(far, lean[0]),
						   new Vector2(far, lean[0]), new Vector2(near, lean[0]));
			SolidMesh.Quad(tube, inner[k][stations], outer[k][stations],
						   outer[k + 1][stations], inner[k + 1][stations],
						   new Vector2(near, lean[stations]), new Vector2(near, lean[stations]),
						   new Vector2(far, lean[stations]), new Vector2(far, lean[stations]));
		}

		// Mouth rings at both ends close the solid.
		var nearMouth = new Vector2(0.0f, 0.0f);
		var farMouth = new Vector2(panelSpan, 0.0f);

		for (var i = 0; i < stations; i++)
		{
			SolidMesh.Quad(tube, inner[0][i], outer[0][i],
						   outer[0][i + 1], inner[0][i + 1],
						   nearMouth, nearMouth, nearMouth, nearMouth);
			SolidMesh.Quad(tube, inner[rings][i], inner[rings][i + 1],
						   outer[rings][i + 1], outer[rings][i],
						   farMouth, farMouth, farMouth, farMouth);
		}

		Mesh = SolidMesh.Commit(new List<MeshBucket> { tube });
	}

	/// <summary>
	/// The bore as a closed rod: the section loop swept ring to ring, the lips joined straight
	/// across the opening by the wrap, and a fan cap over each mouth. The section is convex, so
	/// the centroid fan is safe; the caps traverse the loop in opposite directions, which is what
	/// closes the surface consistently for the CSG backend's manifold check.
	/// </summary>
	private ArrayMesh BuildSolid(Vector3[][] ring, int rings, int loopPoints)
	{
		var rod = new MeshBucket { Smooth = true, Material = SurfaceMaterial };

		for (var k = 0; k < rings; k++)
		{
			for (var i = 0; i < loopPoints; i++)
			{
				int next = (i + 1) % loopPoints;
				SolidMesh.Quad(rod.Triangles, ring[k][i], ring[k][next],
							   ring[k + 1][next], ring[k + 1][i]);
			}
		}

		Vector3 Centroid(Vector3[] mouth)
		{
			var sum = Vector3.Zero;
			for (var i = 0; i < loopPoints; i++)
				sum += mouth[i];
			return sum / loopPoints;
		}

		Vector3 nearCap = Centroid(ring[0]);
		Vector3 farCap = Centroid(ring[rings]);

		for (var i = 0; i < loopPoints; i++)
		{
			int next = (i + 1) % loopPoints;

			rod.Triangles.Add(nearCap);
			rod.Triangles.Add(ring[0][next]);
			rod.Triangles.Add(ring[0][i]);

			rod.Triangles.Add(farCap);
			rod.Triangles.Add(ring[rings][i]);
			rod.Triangles.Add(ring[rings][next]);
		}

		return SolidMesh.Commit(new List<MeshBucket> { rod });
	}

	/// <summary>Every section point pushed away from the section's centroid — the floor down,
	/// the walls out, the lips up — so the rod stands proud of the surface it mirrors.</summary>
	private static Vector2[] Inflate(Vector2[] section, float by)
	{
		var centroid = Vector2.Zero;
		foreach (Vector2 point in section)
			centroid += point;
		centroid /= section.Length;

		var inflated = new Vector2[section.Length];
		for (var i = 0; i < section.Length; i++)
			inflated[i] = section[i] + (section[i] - centroid).Normalized() * by;

		return inflated;
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
	private (Vector2[] Inner, Vector2[] Outer, float[] Lean) BuildSection()
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

		// The lean channel, kept in lockstep with the inner stations: a station's angle around
		// the circle is exactly how far the surface leans there — 0 on the flat, growing up the
		// walls — as a fraction of 90° for UV.y, the shader's turn contract.
		var leans = new List<float>();

		void Side(List<Vector2> points, List<float>? sideLeans, float radius, float fromAngle,
				  float toAngle, int steps, bool skipFirst)
		{
			for (var i = skipFirst ? 1 : 0; i <= steps; i++)
			{
				float angle = Mathf.Lerp(fromAngle, toAngle, (float)i / steps);
				points.Add(new Vector2(radius * Mathf.Sin(angle),
									   rise - radius * Mathf.Cos(angle)));
				sideLeans?.Add(Mathf.Min(1.0f, Mathf.Abs(angle) / (Mathf.Pi * 0.5f)));
			}
		}

		void Floor(List<Vector2> points, List<float>? floorLeans, float halfWidth, float y,
				   int steps)
		{
			for (var i = 1; i < steps; i++)
			{
				points.Add(new Vector2(Mathf.Lerp(-halfWidth, halfWidth, (float)i / steps), y));
				floorLeans?.Add(0.0f);
			}
		}

		// With no floor the two arcs would both contribute the bottom point; the duplicate is a
		// zero-width strip of quads all the way down the pipe, and CSG has no sense of humour
		// about degenerate triangles.
		bool seamless = floorSegments == 1;

		Side(innerPoints, leans, Radius, -half, -floorAngle, arcSegments, skipFirst: false);
		Floor(innerPoints, leans, w, 0.0f, floorSegments);
		Side(innerPoints, leans, Radius, floorAngle, half, arcSegments, skipFirst: seamless);

		Side(outerPoints, null, outerRadius, -half, -outerFloorAngle, arcSegments,
			 skipFirst: false);
		Floor(outerPoints, null, outerW, -Thickness, floorSegments);
		Side(outerPoints, null, outerRadius, outerFloorAngle, half, arcSegments,
			 skipFirst: seamless);

		return (innerPoints.ToArray(), outerPoints.ToArray(), leans.ToArray());
	}

	private static Vector3 TangentOf(Curve3D curve, float offset, float length)
	{
		float epsilon = Mathf.Max(0.05f, length * 0.001f);

		Vector3 tangent = curve.SampleBaked(Mathf.Min(length, offset + epsilon), cubic: true)
						  - curve.SampleBaked(Mathf.Max(0.0f, offset - epsilon), cubic: true);

		return tangent.LengthSquared() < 1e-9f ? Vector3.Forward : tangent.Normalized();
	}
}
