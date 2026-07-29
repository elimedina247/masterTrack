using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// A box of road, lying flat, turned to whatever angle the track was running at: the shape a piece
/// of track takes up, and what replaces "the set of cells it occupies".
///
/// Flat in the sense that it has no roll or pitch — only a yaw and a band of height — which is the
/// same simplification <see cref="TrackAnchor"/> makes, and true for the same reason: every tile in
/// the catalog is level where it meets its neighbours, and the ones that are not level in the middle
/// (the ramps) are described here by the height band they sweep rather than by a tilted box.
///
/// <b>The height band is not decoration.</b> Cells could only say "taken" or "free", so the track
/// could never pass over itself even when one part of it was three cubes up. Carrying a
/// <see cref="MinY"/> and <see cref="MaxY"/> is what makes an over-under legal, and that in turn is
/// what turns the anti-lock rule from a fourfold improvement into a guarantee — a ramp becomes a
/// tile that fits anywhere, because there is always room above. See
/// <c>docs/track-without-a-grid.md</c>.
/// </summary>
public readonly record struct TrackFootprint(
	Vector2 Center, float HalfWidth, float HalfLength, float Yaw, float MinY, float MaxY)
{
	/// <summary>
	/// How much clear air two pieces of track need between them to be allowed to cross.
	///
	/// Car-scale, and generously so: it has to clear the tallest thing a racer can be doing under a
	/// bridge, which is being thrown off a launch pad. The road slab itself is
	/// <c>TrackTile.FloorThickness</c> deep, so this is measured surface to surface and the slab is
	/// already accounted for by the bands not touching.
	/// </summary>
	public const float VerticalClearance = 18.0f;

	/// <summary>Across the road, in the ground plane.</summary>
	public Vector2 Across => new(Mathf.Cos(Yaw), -Mathf.Sin(Yaw));

	/// <summary>Along the road, in the ground plane.</summary>
	public Vector2 Along => new(-Mathf.Sin(Yaw), -Mathf.Cos(Yaw));

	/// <summary>
	/// Whether two pieces of track are in each other's way.
	///
	/// Height first, because it is one comparison and it is the one that most often says no on a
	/// track that has climbed. Then a separating-axis test on the two rectangles: two convex shapes
	/// miss each other exactly when some axis exists that their projections do not overlap on, and
	/// for rectangles the only axes worth trying are the four the two boxes are built from.
	/// </summary>
	public bool Overlaps(TrackFootprint other)
	{
		if (MinY - other.MaxY >= VerticalClearance || other.MinY - MaxY >= VerticalClearance)
			return false;

		return !(SeparatedAlong(Across, this, other)
				 || SeparatedAlong(Along, this, other)
				 || SeparatedAlong(other.Across, this, other)
				 || SeparatedAlong(other.Along, this, other));
	}

	private static bool SeparatedAlong(Vector2 axis, TrackFootprint a, TrackFootprint b)
		=> Mathf.Abs((b.Center - a.Center).Dot(axis)) > a.Reach(axis) + b.Reach(axis);

	/// <summary>How far this box sticks out along an axis, measured from its own centre.</summary>
	private float Reach(Vector2 axis)
		=> Mathf.Abs(axis.Dot(Across)) * HalfWidth + Mathf.Abs(axis.Dot(Along)) * HalfLength;

	/// <summary>Radius of a circle about <see cref="Center"/> that contains the whole box. Used to
	/// decide which buckets of the broadphase it can possibly reach into.</summary>
	public float BoundingRadius => Mathf.Sqrt(HalfWidth * HalfWidth + HalfLength * HalfLength);
}
