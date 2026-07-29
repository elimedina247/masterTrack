#if TOOLS
using Godot;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Editor;

/// <summary>
/// Draws a <see cref="TrackPiece"/>'s contract in the viewport.
///
/// <b>The contract was invisible, and that was most of what made the tool unpleasant.</b> A piece's
/// entry and exit are two <see cref="Marker3D"/>s, which the editor draws as small crosses that say
/// nothing about which way the racer is travelling, how wide the road is meant to be there, or â€”
/// the one that actually bites â€” whether the road ends anywhere near where the marker claims it
/// does. You could drag a spine for ten minutes and have no way of seeing that the seam had stopped
/// matching it.
///
/// So this draws every point on the route as a bar across the road with an arrow through it: the
/// <b>entry</b> in green, the <b>exit</b> in cyan, and each <b>waypoint</b> in white. The arrow is
/// the part worth having — a bar alone is symmetric and says nothing about direction, and a piece
/// whose exit faces backwards looks entirely correct until it is chained onto something.
///
/// Nothing here reports a mismatch between the road and the seam, because there is no longer one to
/// report: the spine is generated through these nodes, so it cannot end anywhere other than where
/// the exit says it does.
/// </summary>
public partial class TrackPieceGizmo : EditorNode3DGizmoPlugin
{
	private const string EntryMaterial = "entry";
	private const string ExitMaterial = "exit";
	private const string SpineMaterial = "spine";

	/// <summary>Half the width of the bar drawn across a seam. The catalog's road, so a piece whose
	/// polygon is narrower than this reads as narrow at a glance.</summary>
	private const float SeamHalfWidth = 27.0f;

	/// <summary>How far the direction arrow reaches out of a seam.</summary>
	private const float ArrowLength = 14.0f;

	/// <summary>
	/// How far apart the spine's end and the exit marker may drift before it is called an error, in
	/// metres. Tight, because the chain butts pieces together exactly â€” anything visible here is a
	/// visible hole in the road.
	/// </summary>
	private const float SeamTolerance = 0.5f;

	public TrackPieceGizmo()
	{
		CreateMaterial(EntryMaterial, new Color(0.2f, 1.0f, 0.4f));
		CreateMaterial(ExitMaterial, new Color(0.2f, 0.9f, 1.0f));
		CreateMaterial(SpineMaterial, new Color(1.0f, 1.0f, 1.0f));
	}

	public override string _GetGizmoName() => "TrackPiece";

	public override bool _HasGizmo(Node3D forNode3D) => forNode3D is TrackPiece;

	public override void _Redraw(EditorNode3DGizmo gizmo)
	{
		gizmo.Clear();

		if (gizmo.GetNode3D() is not TrackPiece piece)
			return;

		if (piece.Entry is { } entry)
			DrawSeam(gizmo, entry.Transform, GetMaterial(EntryMaterial, gizmo));

		if (piece.Exit is { } exit)
			DrawSeam(gizmo, exit.Transform, GetMaterial(ExitMaterial, gizmo));

		foreach (Marker3D waypoint in piece.Waypoints)
			DrawSeam(gizmo, waypoint.Transform, GetMaterial(SpineMaterial, gizmo));
	}

	/// <summary>A point on the route: a bar across the road, an upright at each end so its bank is
	/// readable, and an arrow the way the racer travels through it.</summary>
	private static void DrawSeam(EditorNode3DGizmo gizmo, Transform3D at, Material material)
	{
		Vector3 origin = at.Origin;
		Vector3 across = at.Basis * Vector3.Right;
		Vector3 forward = at.Basis * Vector3.Forward;
		Vector3 up = at.Basis * Vector3.Up;

		Vector3 left = origin - across * SeamHalfWidth;
		Vector3 right = origin + across * SeamHalfWidth;
		Vector3 tip = origin + forward * ArrowLength;

		var lines = new Vector3[]
		{
			// The bar, with a short upright at each end so the seam's roll is readable.
			left, right,
			left, left + up * 4.0f,
			right, right + up * 4.0f,

			// The arrow.
			origin, tip,
			tip, tip - forward * 4.0f + across * 3.0f,
			tip, tip - forward * 4.0f - across * 3.0f,
		};

		gizmo.AddLines(lines, material);
	}
}
#endif
