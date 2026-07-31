using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// A road swept along the piece's spine whose <b>cross-section changes as it goes</b>: a flat
/// inner lane at grade, and a bank that curves up like the wall of a half-pipe to a lip in the
/// air, rising out of nothing where the road bends and gone again where it runs straight.
///
/// This is the code sweep the tile doc promised. <see cref="CsgPolygon3D"/> in path mode sweeps
/// one polygon unchanged from end to end, so a profile drawn into the polygon is at full height at
/// the entry and exit faces — a cliff at every seam — and a bank made by rolling waypoints tips
/// the whole road, so the inside edge digs as far below grade as the outside rises. The generated
/// corners never had either problem because they built their surface point by point, flat lane
/// plus curved bank, eased away to nothing at the ends. This node is that construction, freed from
/// the arc: it reads the spine wherever it goes.
///
/// <b>How much bank appears where is read off the road itself.</b> Each sample measures the
/// spine's horizontal curvature; the bank scales with how hard the road bends there and rises on
/// the outside of that bend. A straight stretch stays flat, an S-curve banks one way then the
/// other, and the seams are forced flat regardless over <see cref="BlendMeters"/> — which is what
/// lets the piece butt onto a plain straight.
///
/// The profile is the one the hand-built corners shipped with, kept on purpose: the bank's angle
/// grows with the <i>square</i> of the distance across it, so the surface leaves the flat lane
/// almost imperceptibly and keeps all of its steepness for the lip, and the strips are spaced by
/// the square root of that so every facet joint turns through the same angle instead of covering
/// the same width.
///
/// A <see cref="CsgMesh3D"/> rather than a mesh instance so it slots under a piece's Build like
/// any other CSG: booleans work against it, the combiner's collision covers it while authoring,
/// and the piece's Bake button freezes it exactly as it freezes everything else.
/// </summary>
[Tool]
[GlobalClass]
public partial class BankedRoad : CsgMesh3D
{
	/// <summary>Width of the road surface, in metres — measured along the surface, so the lateral
	/// footprint narrows slightly where the bank curls up, exactly as a half-pipe's does.</summary>
	[Export(PropertyHint.Range, "10,120,0.5")]
	public float Width { get; set; } = TileCatalog.TileSize;

	/// <summary>
	/// The fraction of the road, from the inside edge, that stays dead flat — the racing line's
	/// floor, and the part of the old corners this whole node exists to keep. The rest is bank.
	/// </summary>
	[Export(PropertyHint.Range, "0.1,0.9,0.05")]
	public float FlatFraction { get; set; } = 0.35f;

	/// <summary>
	/// The surface's slope at the very lip of the bank, in degrees, where the road bends at
	/// <see cref="ReferenceRadius"/> or harder. 60 is the number the corners were always written
	/// for: a bank holds a car unaided at <c>v = sqrt(g·r·tan θ)</c>, and 60 degrees at the
	/// catalog's radius is neutral right at top speed.
	/// </summary>
	[Export(PropertyHint.Range, "10,75,1")]
	public float MaxBankDegrees { get; set; } = 60.0f;

	/// <summary>The bend that earns the full bank, in metres of turn radius. Bends gentler than
	/// this get proportionally less; the catalog's corner radius by default, so a standard corner
	/// develops its whole wall.</summary>
	[Export(PropertyHint.Range, "20,300,1")]
	public float ReferenceRadius { get; set; } = TileCatalog.CornerRadius;

	/// <summary>Metres of road at each end over which the bank is eased away to nothing, whatever
	/// the curvature says. The seams are the contract with the neighbouring pieces, and the
	/// contract is flat.</summary>
	[Export(PropertyHint.Range, "5,60,1")]
	public float BlendMeters { get; set; } = 22.0f;

	/// <summary>Thickness of the road slab, straight down from the surface.</summary>
	[Export(PropertyHint.Range, "0.5,20,0.1")]
	public float Thickness { get; set; } = 1.6f;

	/// <summary>Metres of road per ring of facets along the spine.</summary>
	[Export(PropertyHint.Range, "1,12,0.5")]
	public float SampleInterval { get; set; } = 3.0f;

	/// <summary>What the surface is made of. On the node rather than baked into the mesh build so
	/// swapping it never costs a rebuild.</summary>
	[Export]
	public Material? SurfaceMaterial { get; set; }

	/// <summary>Strip boundaries across the flat lane, and across the bank. The bank gets the
	/// detail because it is the part that curves; eight joints of 7.5 degrees was the old
	/// corners' budget and it read smoothly at speed.</summary>
	private const int FlatStrips = 2;

	private const int BankStrips = 8;

	private int _shape;

	/// <summary>
	/// Keep the generated mesh out of the scene file. It is derived data — the spine and the
	/// knobs are the whole source of truth — and letting the base class's mesh property save
	/// meant four hundred kilobytes of triangles written into the piece every time the scene was
	/// saved, all of it thrown away and rebuilt on the next load anyway.
	/// </summary>
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

		// At runtime the spine is regenerated by the piece's own _Ready, which runs after this
		// node's — parents ready last — so the rebuild has to wait a beat or it sweeps a curve
		// that is about to be replaced.
		CallDeferred(MethodName.Rebuild);
	}

	/// <summary>The same polling the piece itself uses: rebuild when the spine or the knobs
	/// change, because a signal wired to the curve is a signal silently lost on script reloads.</summary>
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

		hash.Add(Width);
		hash.Add(FlatFraction);
		hash.Add(MaxBankDegrees);
		hash.Add(ReferenceRadius);
		hash.Add(BlendMeters);
		hash.Add(Thickness);
		hash.Add(SampleInterval);

		if (Spine()?.Curve is { } curve)
		{
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

	/// <summary>The spine of the piece this road belongs to, however deep under Build it sits.</summary>
	private Path3D? Spine()
	{
		for (Node? node = GetParent(); node != null; node = node.GetParent())
		{
			if (node is TrackPiece piece)
				return piece.Spine;
		}

		return null;
	}

	public void Rebuild()
	{
		if (Spine()?.Curve is not { PointCount: >= 2 } curve
			|| curve.GetBakedLength() < 1.0f)
		{
			Mesh = null;
			return;
		}

		float length = curve.GetBakedLength();
		int rings = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(1.0f, SampleInterval)));

		// The cross-section, from the inside edge outward, at full development: where each strip
		// boundary falls across the road, and how far the bank has climbed by then. Scaled per
		// ring rather than recomputed — the profile keeps its shape while its height comes and
		// goes, which is exactly what the generated corners did.
		int strips = FlatStrips + BankStrips;
		var across = new float[strips + 1];
		var lift = new float[strips + 1];

		float flat = Width * FlatFraction;
		float banked = Width - flat;
		float maxBank = Mathf.DegToRad(MaxBankDegrees);

		for (var m = 0; m <= strips; m++)
		{
			if (m <= FlatStrips)
			{
				across[m] = flat * m / FlatStrips;
				continue;
			}

			int b = m - FlatStrips;

			// Angle squared across the road, strips spaced by its inverse — see the class doc.
			float v = Mathf.Sqrt((float)b / BankStrips);
			float previous = Mathf.Sqrt((b - 1.0f) / BankStrips);

			across[m] = flat + banked * v;

			float mid = (v + previous) * 0.5f;
			lift[m] = lift[m - 1] + (across[m] - across[m - 1]) * Mathf.Tan(maxBank * mid * mid);
		}

		// One frame and one bank strength per ring. The strength is signed: positive banks to the
		// local right, negative to the left, zero is a flat road — and it crosses zero smoothly
		// wherever the road changes which way it bends.
		var rows = new Vector3[rings + 1][];

		for (var k = 0; k <= rings; k++)
		{
			float offset = length * k / rings;

			Vector3 origin = curve.SampleBaked(offset, cubic: true);
			Vector3 forward = TangentAt(curve, offset, length);
			Vector3 right = forward.Cross(Vector3.Up);

			// A road running dead vertical has no across; nothing sensible to build there.
			if (right.LengthSquared() < 1e-6f)
			{
				rows[k] = rows[Mathf.Max(0, k - 1)] ?? new Vector3[strips + 1];
				continue;
			}

			right = right.Normalized();

			float strength = BankStrength(curve, offset, length);

			// Which side is inner: the bank rises on the outside of the bend, so a right-hand
			// bend (negative strength) keeps its flat lane on the right and lifts the left.
			float side = strength >= 0.0f ? 1.0f : -1.0f;
			float amount = Mathf.Abs(strength);

			var row = new Vector3[strips + 1];
			for (var m = 0; m <= strips; m++)
			{
				float x = side * (across[m] - Width * 0.5f);
				row[m] = origin + right * x + Vector3.Up * (lift[m] * amount);
			}

			// Rows always run inner to outer, which flips side to side through an S-bend. The
			// mesh needs a consistent lateral order, so flip the row back when the bend is
			// right-handed — at the crossover the bank is zero and both orders are the same
			// points, so nothing jumps.
			if (side < 0.0f)
				System.Array.Reverse(row);

			rows[k] = row;
		}

		Mesh = Solidify(rows, strips);
	}

	/// <summary>
	/// How much of the full bank this point of the road earns, signed by which way it bends.
	/// Curvature over the reference, clamped — then multiplied by the hard ease to zero at the
	/// seams, which wins whatever the bend says.
	/// </summary>
	private float BankStrength(Curve3D curve, float offset, float length)
	{
		const float probe = 6.0f;

		float back = Mathf.Max(0.0f, offset - probe);
		float ahead = Mathf.Min(length, offset + probe);

		if (ahead - back < 0.5f)
			return 0.0f;

		Vector3 before = TangentAt(curve, back, length);
		Vector3 after = TangentAt(curve, ahead, length);

		var beforeFlat = new Vector2(before.X, before.Z);
		var afterFlat = new Vector2(after.X, after.Z);

		if (beforeFlat.LengthSquared() < 1e-6f || afterFlat.LengthSquared() < 1e-6f)
			return 0.0f;

		// AngleTo is signed, and in the flattened (X, Z) plane a left turn comes out negative —
		// the plane is the XZ view of a right-handed space, so its winding reads mirrored.
		// Magnitude and side are therefore taken separately: curvature over the reference for how
		// much, the sign for which way, and a left turn banks to the right — lip on the outside.
		float angle = beforeFlat.AngleTo(afterFlat);

		float strength = Mathf.Clamp(Mathf.Abs(angle) / (ahead - back) * ReferenceRadius, 0.0f, 1.0f);
		float side = angle <= 0.0f ? 1.0f : -1.0f;

		float blend = Mathf.Max(1.0f, BlendMeters);
		float ease = Mathf.SmoothStep(0.0f, 1.0f,
									  Mathf.Clamp(Mathf.Min(offset, length - offset) / blend, 0.0f, 1.0f));

		return side * strength * ease;
	}

	private static Vector3 TangentAt(Curve3D curve, float offset, float length)
	{
		float epsilon = Mathf.Max(0.05f, length * 0.001f);

		Vector3 tangent = curve.SampleBaked(Mathf.Min(length, offset + epsilon), cubic: true)
						  - curve.SampleBaked(Mathf.Max(0.0f, offset - epsilon), cubic: true);

		return tangent.LengthSquared() < 1e-9f ? Vector3.Forward : tangent.Normalized();
	}

	/// <summary>
	/// Close the swept surface into a watertight solid: the top, the same rows dropped by
	/// <see cref="Thickness"/> for the underside, walls down both edges and a cap over each end.
	/// CSG only trusts a closed mesh, and a bake is only as good as what CSG was given.
	/// </summary>
	private ArrayMesh Solidify(Vector3[][] rows, int strips)
	{
		var triangles = new List<Vector3>();
		Vector3 drop = Vector3.Down * Thickness;

		void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
			=> SolidMesh.Quad(triangles, a, b, c, d);

		for (var k = 0; k < rows.Length - 1; k++)
		{
			Vector3[] near = rows[k];
			Vector3[] far = rows[k + 1];

			for (var m = 0; m < strips; m++)
			{
				// Top, then underside wound the other way, then nothing else per facet — the
				// edges and caps close the ring.
				Quad(near[m], far[m], far[m + 1], near[m + 1]);
				Quad(near[m] + drop, near[m + 1] + drop, far[m + 1] + drop, far[m] + drop);
			}

			Quad(near[0], near[0] + drop, far[0] + drop, far[0]);
			Quad(near[strips], far[strips], far[strips] + drop, near[strips] + drop);
		}

		Vector3[] first = rows[0];
		Vector3[] last = rows[^1];

		for (var m = 0; m < strips; m++)
		{
			Quad(first[m], first[m + 1], first[m + 1] + drop, first[m] + drop);
			Quad(last[m], last[m] + drop, last[m + 1] + drop, last[m + 1]);
		}

		return SolidMesh.Commit(triangles, SurfaceMaterial);
	}
}
