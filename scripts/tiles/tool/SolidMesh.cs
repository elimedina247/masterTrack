using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// One mesh surface's worth of triangle soup, on its way into <see cref="SolidMesh.Commit"/>.
///
/// <b>Smooth is the whole reason this exists.</b> CSG carries shading per face — a face whose
/// corner normals differ is shaded smoothly and welded into its neighbours' normals, a face
/// whose corners agree stays a facet and pollutes nobody — so which bucket a quad lands in
/// decides how the road reads under the color shader. Driveable surface wants smooth normals,
/// or every threshold in the shader flips per facet and the color boundaries come out sawtoothed;
/// slab sides, undersides and caps want flat, or their steepness bleeds into the rim of the road
/// above them and paints a green-to-black fringe along every edge.
///
/// <see cref="Uvs"/> rides along per corner when a generator has something to say in the UV
/// channel — the bank sweep writes its lateral lean into UV.x for the shader's turning color.
/// Empty means "all zero", which CSG happily carries.
/// </summary>
internal sealed class MeshBucket
{
	public List<Vector3> Triangles { get; } = new();

	public List<Vector2> Uvs { get; } = new();

	public bool Smooth { get; init; }

	public Material? Material { get; init; }
}

/// <summary>
/// Turns a triangle soup into a mesh CSG will trust: consistently wound, normal-bearing, one
/// surface per bucket. Shared by every generator that builds road out of code, because the
/// winding fix in particular is the kind of thing that must not exist twice.
/// </summary>
internal static class SolidMesh
{
	/// <summary>Two triangles for the quad a-b-c-d, wound as given.</summary>
	public static void Quad(List<Vector3> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
	{
		triangles.Add(a);
		triangles.Add(b);
		triangles.Add(c);

		triangles.Add(a);
		triangles.Add(c);
		triangles.Add(d);
	}

	/// <summary>The same quad with a UV per corner, kept in step with the vertices.</summary>
	public static void Quad(MeshBucket bucket,
							Vector3 a, Vector3 b, Vector3 c, Vector3 d,
							Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
	{
		Quad(bucket.Triangles, a, b, c, d);

		bucket.Uvs.Add(ua);
		bucket.Uvs.Add(ub);
		bucket.Uvs.Add(uc);

		bucket.Uvs.Add(ua);
		bucket.Uvs.Add(uc);
		bucket.Uvs.Add(ud);
	}

	/// <summary>
	/// Commit a closed triangle soup as an ArrayMesh, fixing the winding first. One surface,
	/// smooth-shaded — the shape every generator wanted before buckets existed.
	/// </summary>
	public static ArrayMesh Commit(List<Vector3> triangles, Material? material)
	{
		var bucket = new MeshBucket { Smooth = true, Material = material };
		bucket.Triangles.AddRange(triangles);

		return Commit(new List<MeshBucket> { bucket });
	}

	/// <summary>
	/// The same commit for a solid whose faces arrive in more than one bucket — one mesh surface
	/// per non-empty bucket, and CSG carries material and shading through booleans and the bake
	/// alike.
	///
	/// The buckets are one solid, so the winding coin is tossed once for all of them: Godot's
	/// front faces wind clockwise seen from outside, which comes out negative under the
	/// right-handed tetrahedron sum, so a positive total means every triangle is backwards and
	/// the whole soup is flipped once, here. Deciding per bucket would be deciding on an open
	/// shell — a bucket alone is not closed, and its own volume sum is noise.
	/// </summary>
	public static ArrayMesh Commit(List<MeshBucket> buckets)
	{
		var volume = 0.0f;
		foreach (MeshBucket bucket in buckets)
		{
			List<Vector3> triangles = bucket.Triangles;
			for (var i = 0; i < triangles.Count; i += 3)
				volume += triangles[i].Dot(triangles[i + 1].Cross(triangles[i + 2]));
		}

		if (volume > 0.0f)
		{
			foreach (MeshBucket bucket in buckets)
			{
				List<Vector3> triangles = bucket.Triangles;
				List<Vector2> uvs = bucket.Uvs;

				for (var i = 0; i < triangles.Count; i += 3)
				{
					(triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);

					if (uvs.Count == triangles.Count)
						(uvs[i + 1], uvs[i + 2]) = (uvs[i + 2], uvs[i + 1]);
				}
			}
		}

		var mesh = new ArrayMesh();

		foreach (MeshBucket bucket in buckets)
		{
			if (bucket.Triangles.Count == 0)
				continue;

			var surface = new SurfaceTool();
			surface.Begin(Mesh.PrimitiveType.Triangles);

			if (bucket.Material != null)
				surface.SetMaterial(bucket.Material);

			// Group 0 welds normals across every shared position in the bucket; the max value
			// is SurfaceTool's "no group", which leaves each facet its own plane normal.
			surface.SetSmoothGroup(bucket.Smooth ? 0u : uint.MaxValue);

			bool hasUvs = bucket.Uvs.Count == bucket.Triangles.Count;

			for (var i = 0; i < bucket.Triangles.Count; i++)
			{
				if (hasUvs)
					surface.SetUV(bucket.Uvs[i]);

				surface.AddVertex(bucket.Triangles[i]);
			}

			surface.GenerateNormals();
			surface.Commit(mesh);
		}

		return mesh;
	}
}
