using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// The arithmetic that joins two pieces: given where the track hands on, where does the next piece
/// go, and where does it hand on in turn.
///
/// <b>One implementation, used by the editor tool and the game alike.</b> The click-to-extend
/// gizmo, a hand-assembled layout and the procedural builder all have to agree about what
/// "attached" means, and two copies of this maths is how an editor preview ends up a few
/// millimetres from where the game builds the same joint.
///
/// The unit of currency is a <b>cursor</b>: a full <see cref="Transform3D"/> whose origin is the
/// point the racer crosses between pieces and whose basis is their frame there — local <c>-Z</c>
/// the direction of travel, <c>+Y</c> up out of the road, roll and pitch carried in the basis like
/// any other rotation. This is what replaces <see cref="Tiles.TrackAnchor"/>'s position-and-yaw:
/// the anchor had nowhere to put a banked or climbing seam, so every piece was forced to flatten
/// out at both ends and a corner could never hand its bank to the next corner.
///
/// <b>Every result is orthonormalised.</b> The reason the anchor was kept to four floats was drift
/// — chained basis multiplications accumulate error until nothing downstream lines up. That is
/// only true of a chain that never renormalises: squaring the basis up after each composition caps
/// the error at machine epsilon per joint instead of compounding it, and identical operations in
/// identical order still give every peer bit-identical placements, which is all replication needs.
/// </summary>
public static class TrackSnap
{
	/// <summary>
	/// Where a piece goes so that its entry lands exactly on a cursor: position, heading, pitch and
	/// roll all agree at the seam by construction.
	///
	/// The entry's own transform is orthonormalised before inverting, so a connector somebody has
	/// accidentally scaled still describes a rigid frame — a scaled inverse would smear the whole
	/// piece.
	/// </summary>
	/// <param name="cursor">The open seam being built onto, in world space.</param>
	/// <param name="entryLocal">The entry connector's transform in the piece's own space.</param>
	public static Transform3D PlacementFor(Transform3D cursor, Transform3D entryLocal)
		=> (cursor * entryLocal.Orthonormalized().AffineInverse()).Orthonormalized();

	/// <summary>
	/// Where a placed piece hands the track on: the cursor its exit connector defines, ready for
	/// <see cref="PlacementFor"/> to build the next piece against.
	/// </summary>
	/// <param name="placement">The piece's world transform.</param>
	/// <param name="exitLocal">The exit connector's transform in the piece's own space.</param>
	public static Transform3D CursorFor(Transform3D placement, Transform3D exitLocal)
		=> (placement * exitLocal.Orthonormalized()).Orthonormalized();

	/// <summary>
	/// A connector's cursor read straight off the live tree — for the editor, where the pieces are
	/// real nodes rather than planned transforms.
	/// </summary>
	public static Transform3D CursorOf(Node3D connector)
		=> connector.GlobalTransform.Orthonormalized();
}
