using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// Turns a triangle soup into a mesh CSG will trust: consistently wound, normal-bearing, one
/// surface. Shared by every generator that builds road out of code, because the winding fix in
/// particular is the kind of thing that must not exist twice.
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

	/// <summary>
	/// Commit a closed triangle soup as an ArrayMesh, fixing the winding first.
	///
	/// Wound outward or wound inward is a coin the generators should never be trusted to have
	/// called: an inside-out solid renders invisible and turns every CSG boolean inside out. The
	/// signed volume settles it — Godot's front faces wind clockwise seen from outside, which
	/// comes out negative under the right-handed tetrahedron sum, so a positive total means every
	/// triangle is backwards and the whole soup is flipped once, here.
	/// </summary>
	public static ArrayMesh Commit(List<Vector3> triangles, Material? material)
	{
		var volume = 0.0f;
		for (var i = 0; i < triangles.Count; i += 3)
			volume += triangles[i].Dot(triangles[i + 1].Cross(triangles[i + 2]));

		if (volume > 0.0f)
		{
			for (var i = 0; i < triangles.Count; i += 3)
				(triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
		}

		var surface = new SurfaceTool();
		surface.Begin(Mesh.PrimitiveType.Triangles);

		if (material != null)
			surface.SetMaterial(material);

		foreach (Vector3 vertex in triangles)
			surface.AddVertex(vertex);

		surface.GenerateNormals();
		return surface.Commit();
	}
}
