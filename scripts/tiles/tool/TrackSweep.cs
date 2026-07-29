using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// Sweeping a cross-section along a spine, which is the one operation the whole tile tool is built
/// out of.
///
/// <b>This replaces four hand-written builders with one.</b> The banked arc, the eased ramp, the
/// squiggle's snaking ribbon and the plain floor were each a bespoke loop emitting boxes, and they
/// were four copies of the same idea: walk a path, carry a shape along it, join consecutive
/// positions up. Written once, a corner stops being special — it is a curved spine — and a ramp
/// stops being special, because it is a spine that climbs.
///
/// Nothing in here knows what a tile is. It takes a curve and a polygon and gives back triangles,
/// which is what makes it usable for the road, the barriers and the paint alike.
///
/// <b>Triangle soup, not indexed.</b> Vertices are emitted three at a time with a face normal each,
/// so the surface shades as flat facets — the arcade look the hand-built tiles got for free from
/// being made of boxes. It also means the same array serves as both the mesh and the
/// <see cref="ConcavePolygonShape3D"/> data, so the collision cannot drift from the thing you can
/// see.
/// </summary>
public static class TrackSweep
{
	/// <summary>
	/// A position along the spine and the axes of the road there: where the section is planted, and
	/// which way is across it, up out of it, and along it.
	/// </summary>
	public readonly record struct Frame(Vector3 Position, Vector3 Right, Vector3 Up, Vector3 Forward)
	{
		/// <summary>A section point — a lateral offset and a height — placed into world space.</summary>
		public Vector3 At(Vector2 section)
			=> Position + Right * section.X + Up * section.Y;
	}

	/// <summary>
	/// Frames along a curve, spaced evenly by arc length.
	///
	/// Evenly rather than by curvature on purpose. The hand-built corners had to ration facets — each
	/// one cost four objects and the budget was a wall you hit with a native crash — so they bought
	/// resolution cleverly, packing samples where the surface bent. A swept section pays vertices,
	/// which are close to free, so the clever spacing is no longer worth the complexity it costs.
	///
	/// <paramref name="segmentLength"/> is therefore a plain quality knob: at the default a 63 m
	/// corner comes out at about the twelve segments the old build fought for.
	/// </summary>
	/// <param name="transform">
	/// Where the curve sits relative to whoever is sweeping it. A <see cref="Path3D"/> is a node
	/// with a transform of its own, and leaving it out means the road ignores the spine being moved
	/// or turned — the curve visibly shifts in the viewport and the geometry stays where it was.
	/// </param>
	public static Frame[] Frames(Curve3D curve, float segmentLength, Transform3D transform)
	{
		float length = curve.GetBakedLength();
		if (length <= 0.0f)
			return System.Array.Empty<Frame>();

		int steps = Mathf.Max(1, Mathf.RoundToInt(length / Mathf.Max(0.05f, segmentLength)));
		var frames = new Frame[steps + 1];

		// Small enough to read the tangent as local, large enough not to be lost in float noise at
		// the scale a track is built on.
		float epsilon = Mathf.Max(0.01f, length * 0.0005f);

		for (int i = 0; i <= steps; i++)
		{
			float at = length * i / steps;

			Vector3 position = transform * curve.SampleBaked(at, cubic: true);

			// One-sided at the ends so the tangent is never taken across the end of the curve, where
			// SampleBaked clamps and would hand back a direction of zero length.
			Vector3 ahead = curve.SampleBaked(Mathf.Min(length, at + epsilon), cubic: true);
			Vector3 behind = curve.SampleBaked(Mathf.Max(0.0f, at - epsilon), cubic: true);

			Vector3 forward = transform.Basis * (ahead - behind);
			forward = forward.LengthSquared() > 1e-9f ? forward.Normalized() : Vector3.Forward;

			Vector3 up = transform.Basis * (curve.UpVectorEnabled
				? curve.SampleBakedUpVector(at, applyTilt: true)
				: Vector3.Up);

			frames[i] = Orthonormal(position, forward, up);
		}

		return frames;
	}

	/// <summary>
	/// A frame with its axes squared up against each other.
	///
	/// The tilt-carried up vector is only approximately perpendicular to the tangent — it is
	/// interpolated between control points, and the tangent is a finite difference — so it is used
	/// for the <i>roll</i> it carries and then rebuilt from the two axes that are trustworthy. Left
	/// unsquared the section shears as it sweeps, which shows up as a road whose width breathes
	/// through a corner.
	/// </summary>
	private static Frame Orthonormal(Vector3 position, Vector3 forward, Vector3 up)
	{
		Vector3 right = forward.Cross(up);

		// The up vector has collapsed onto the tangent — a spine going straight up, or a tilt that
		// has rolled the section into its own direction of travel. Pick any perpendicular rather
		// than emitting a degenerate frame that would collapse the whole section to a line.
		if (right.LengthSquared() < 1e-8f)
		{
			right = forward.Cross(Vector3.Up);
			if (right.LengthSquared() < 1e-8f)
				right = forward.Cross(Vector3.Right);
		}

		right = right.Normalized();
		return new Frame(position, right, right.Cross(forward).Normalized(), forward);
	}

	/// <summary>
	/// Sweep a closed section along the frames, emitting the walls of the tube it traces and a cap
	/// at each end.
	///
	/// The section must run <b>left to right along its top, then right to left along its bottom</b>.
	/// That ordering is what puts the face normals on the outside: swept forward, a section edge
	/// running across the road faces up, one running down the far side faces outward, and so on
	/// round. Reversed, every face in the piece points into itself and the road is invisible from
	/// above while still being solid.
	///
	/// Closed and capped, so the triangles describe a watertight solid. That is what lets the same
	/// soup be handed to a <see cref="ConcavePolygonShape3D"/> and still behave like something with
	/// an inside — a car is kept out of it by the surface rather than by a stack of boxes.
	/// </summary>
	/// <param name="sections">
	/// One section per frame, so the shape may change as it travels. That is not a luxury: a banked
	/// corner has to ease its bank away to nothing before the seam or it meets the flat straight
	/// beside it as a sixteen metre step, and a road that narrows is the same idea spent on width
	/// instead of height.
	/// </param>
	public static void SweepClosed(Frame[] frames, Vector2[][] sections,
								   List<Vector3> vertices, List<Vector3> normals)
	{
		if (frames.Length < 2 || sections.Length != frames.Length || sections[0].Length < 3)
			return;

		for (int i = 0; i < frames.Length - 1; i++)
		{
			Frame near = frames[i];
			Frame far = frames[i + 1];
			Vector2[] nearSection = sections[i];
			Vector2[] farSection = sections[i + 1];

			// A section that changed length between frames has no correspondence between its points,
			// so there is no quad to draw. Skipped rather than guessed at.
			if (nearSection.Length != farSection.Length)
				continue;

			for (int j = 0; j < nearSection.Length; j++)
			{
				int next = (j + 1) % nearSection.Length;

				Quad(vertices, normals,
					 near.At(nearSection[j]), near.At(nearSection[next]),
					 far.At(farSection[next]), far.At(farSection[j]));
			}
		}

		CapEnd(frames[0], sections[0], vertices, normals, front: false);
		CapEnd(frames[^1], sections[^1], vertices, normals, front: true);
	}

	/// <summary>The same sweep with one section held constant the whole way along.</summary>
	public static void SweepClosed(Frame[] frames, Vector2[] section,
								   List<Vector3> vertices, List<Vector3> normals)
		=> SweepClosed(frames, Repeat(section, frames.Length), vertices, normals);

	/// <summary>One section per frame, all of them the same one.</summary>
	public static Vector2[][] Repeat(Vector2[] section, int count)
	{
		var sections = new Vector2[count][];
		for (int i = 0; i < count; i++)
			sections[i] = section;

		return sections;
	}

	/// <summary>
	/// Sweep an open run of section points, emitting a one-sided ribbon.
	///
	/// What the painted markings are: a stripe has a top and nothing else, and giving it a solid to
	/// be the top of would put a lip in the road for the suspension to trip over.
	/// </summary>
	public static void SweepRibbon(Frame[] frames, Vector2[][] sections,
								   List<Vector3> vertices, List<Vector3> normals)
	{
		if (frames.Length < 2 || sections.Length != frames.Length || sections[0].Length < 2)
			return;

		for (int i = 0; i < frames.Length - 1; i++)
		{
			Frame near = frames[i];
			Frame far = frames[i + 1];
			Vector2[] nearSection = sections[i];
			Vector2[] farSection = sections[i + 1];

			if (nearSection.Length != farSection.Length)
				continue;

			for (int j = 0; j < nearSection.Length - 1; j++)
			{
				Quad(vertices, normals,
					 near.At(nearSection[j]), near.At(nearSection[j + 1]),
					 far.At(farSection[j + 1]), far.At(farSection[j]));
			}
		}
	}

	/// <summary>
	/// Close one end of a swept tube.
	///
	/// The section is a top run and a bottom run of the same length rather than an arbitrary
	/// polygon, so it triangulates as a strip between the two — no ear clipping, and no chance of a
	/// cap that disagrees with the surface it is closing.
	/// </summary>
	private static void CapEnd(Frame frame, Vector2[] section,
							   List<Vector3> vertices, List<Vector3> normals, bool front)
	{
		int half = section.Length / 2;

		for (int j = 0; j < half - 1; j++)
		{
			Vector2 topNear = section[j];
			Vector2 topFar = section[j + 1];

			// The bottom run is the top run backwards, so the point under top[j] is the one that
			// many places from the end.
			Vector2 bottomNear = section[section.Length - 1 - j];
			Vector2 bottomFar = section[section.Length - 2 - j];

			if (front)
			{
				Quad(vertices, normals, frame.At(topNear), frame.At(topFar),
					 frame.At(bottomFar), frame.At(bottomNear));
			}
			else
			{
				Quad(vertices, normals, frame.At(topNear), frame.At(bottomNear),
					 frame.At(bottomFar), frame.At(topFar));
			}
		}
	}

	/// <summary>
	/// Two triangles for a quad, with a face normal each. Degenerate triangles are dropped rather
	/// than emitted with a zero normal, which would leave a black facet in the mesh and a
	/// zero-area triangle in the collision.
	/// </summary>
	private static void Quad(List<Vector3> vertices, List<Vector3> normals,
							 Vector3 a, Vector3 b, Vector3 c, Vector3 d)
	{
		Triangle(vertices, normals, a, b, c);
		Triangle(vertices, normals, a, c, d);
	}

	private static void Triangle(List<Vector3> vertices, List<Vector3> normals,
								 Vector3 a, Vector3 b, Vector3 c)
	{
		Vector3 normal = (b - a).Cross(c - b);
		if (normal.LengthSquared() < 1e-12f)
			return;

		normal = normal.Normalized();

		vertices.Add(a);
		vertices.Add(b);
		vertices.Add(c);

		normals.Add(normal);
		normals.Add(normal);
		normals.Add(normal);
	}

	/// <summary>
	/// A closed section built from a run of surface points and a thickness: the surface itself, then
	/// the same run backwards and dropped, which is the ordering <see cref="SweepClosed"/> requires.
	/// </summary>
	public static Vector2[] Solidify(Vector2[] surface, float thickness)
	{
		var closed = new Vector2[surface.Length * 2];

		for (int i = 0; i < surface.Length; i++)
		{
			closed[i] = surface[i];
			closed[^(i + 1)] = surface[i] with { Y = surface[i].Y - thickness };
		}

		return closed;
	}
}
