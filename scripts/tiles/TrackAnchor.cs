using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// Where a piece of track starts and which way it runs: the seam between one tile and the next.
///
/// This is what replaces a grid cell as the track comes off the grid — see
/// <c>docs/track-without-a-grid.md</c>. A tile's exit anchor is the next tile's entry anchor, so a
/// track becomes a fold down an ordered list of pieces rather than a set of occupied squares, and
/// nothing about it has to land on a lattice. Lengths and radii stop being multiples of a cell and
/// go back to being distances.
///
/// <b>Position is the middle of the entry face, not the middle of the tile</b> — the point the
/// racer actually crosses. That is what makes the fold work: two tiles butt together exactly when
/// one's exit anchor equals the other's entry anchor, with no half-lengths to reconcile at the
/// joint.
///
/// Yaw and no more. There is no pitch or roll here and there does not need to be, because every
/// tile in the catalog hands its neighbour a level frame: <c>RampHeightAt</c> is flat at both ends
/// so the joints stay tangential, and <c>BankScale</c> eases a corner's bank away to nothing before
/// it meets the straight either side. Four floats rather than a <see cref="Transform3D"/> keeps the
/// fold clear of basis renormalisation and the drift that comes with it.
/// </summary>
public readonly record struct TrackAnchor(Vector3 Position, float Yaw)
{
	/// <summary>
	/// The way the racer is travelling. A tile is built running along its own local -Z — Godot's
	/// forward, and what <see cref="TrackDirection.North"/> means — so this is that axis turned by
	/// <see cref="Yaw"/>.
	/// </summary>
	public Vector3 Forward => new(-Mathf.Sin(Yaw), 0.0f, -Mathf.Cos(Yaw));

	/// <summary>Across the track, to the racer's right: local +X turned by <see cref="Yaw"/>.</summary>
	public Vector3 Right => new(Mathf.Cos(Yaw), 0.0f, -Mathf.Sin(Yaw));

	/// <summary>
	/// This anchor moved straight ahead, optionally climbing. What every tile that runs through
	/// hands on — a straight, a ramp, and every hazard.
	/// </summary>
	public TrackAnchor Advanced(float distance, float rise = 0.0f)
		=> this with { Position = Position + Forward * distance + new Vector3(0.0f, rise, 0.0f) };

	/// <summary>
	/// This anchor swept round an arc of <paramref name="radius"/> through
	/// <paramref name="radians"/>, turning <paramref name="side"/> — 1 to the right, -1 to the left.
	///
	/// <b>The one formula every turning tile needs.</b> At a quarter turn <c>sin</c> is 1 and
	/// <c>cos</c> is 0, so it comes out as <c>forward * r + right * side * r</c> — precisely the
	/// diagonal step the old grid arrived at by counting cells. At a
	/// half turn the forward term vanishes and it is <c>right * side * 2r</c>, which is the
	/// hairpin's "two cells across". Both drop out of these two lines instead of being the two
	/// hand-written special cases the grid needs, and any angle in between works for free.
	///
	/// Yaw <i>decreases</i> turning right, because that is the convention
	/// <see cref="TrackDirectionExtensions.Yaw"/> already set: a quarter turn clockwise is -90
	/// degrees about +Y.
	/// </summary>
	public TrackAnchor Swept(float radius, float radians, int side)
		=> new(Position + Forward * (radius * Mathf.Sin(radians))
						+ Right * (side * radius * (1.0f - Mathf.Cos(radians))),
			   Yaw - side * radians);
}
