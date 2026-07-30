using Godot;
using MasterTrack.Tiles;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Game;

/// <summary>
/// The chained course: every authored piece joined end to end off the west edge of the pad, built
/// through <see cref="PieceCatalog"/> and <see cref="TrackSnap"/> — the same catalog the palette
/// reads and the same arithmetic the editor's "+" handles use, exercised at runtime.
///
/// <b>This is the full-frame chain being proven, so it is allowed to leave the ground.</b> The
/// specimens' row exists to look at one piece on the tarmac; this exists to drive the joints, and
/// a joint is only trustworthy if it holds when the seam is climbing or banked. A piece that rises
/// carries the rest of the course up with it, exactly as a match's track would.
///
/// A plain Straight is threaded between the featured pieces so each one is met with a moment to
/// settle, the way hazards meet a racer in a match.
///
/// Every placement is planned from the catalog's cached seams and only then instanced — the
/// builder never opens a scene to decide where it goes, which is the discipline the real
/// procedural generator will inherit.
/// </summary>
public partial class PhysicsTestArea
{
	/// <summary>The breather piece's name in the catalog. Skipped silently if nobody authored
	/// one — the course is then just the pieces butted together.</summary>
	private const string BreatherPiece = "Straight";

	/// <summary>
	/// Chain the catalog off the west edge of the pad, heading west — the one edge nothing else
	/// anchors to: the two tracks run north and south, and the grass apron owns the east.
	/// </summary>
	private void BuildPieceRun()
	{
		if (PieceCatalog.Entries.Count == 0)
			return;

		// The cursor: a full frame, not a position and a yaw. West is +90 degrees of yaw in the
		// convention TrackDirection set (yaw 0 is -Z, turning right is negative).
		var cursor = new Transform3D(new Basis(Vector3.Up, Mathf.Pi * 0.5f),
									 new Vector3(-_padHalfX, 0.0f, 0.0f));

		AddLabel("Chained course →", cursor.Origin + new Vector3(20.0f, 8.0f, 0.0f),
				 new Color(0.2f, 0.9f, 1.0f));

		PieceEntry? breather = PieceCatalog.Named(BreatherPiece);
		var placed = 0;

		foreach (PieceEntry entry in PieceCatalog.Entries)
		{
			if (breather != null && entry != breather)
				cursor = PlaceFromCatalog(breather, cursor, ref placed);

			cursor = PlaceFromCatalog(entry, cursor, ref placed);
		}

		GD.Print($"[TestArea] Chained {placed} piece(s) west from (-{_padHalfX:0}, 0, 0); the run "
				 + $"ends at ({cursor.Origin.X:0}, {cursor.Origin.Y:0}, {cursor.Origin.Z:0}).");
	}

	/// <summary>
	/// Plan one piece from its catalog entry, instance it, and hand back the cursor its exit
	/// defines. On any defect the cursor comes back unchanged, so one bad piece is one hole in the
	/// course rather than the end of it.
	/// </summary>
	private Transform3D PlaceFromCatalog(PieceEntry entry, Transform3D cursor, ref int placed)
	{
		if (entry.Entry is not { } entrySeam)
		{
			GD.PushWarning($"[TestArea] {entry.Name} has no entry seam, so the chained course "
						   + "skipped it.");
			return cursor;
		}

		PieceSeam? exitSeam = null;
		foreach (PieceSeam seam in entry.Exits)
		{
			exitSeam = seam;
			break;
		}

		if (exitSeam == null)
		{
			GD.PushWarning($"[TestArea] {entry.Name} has no exit seam, so the chained course "
						   + "skipped it.");
			return cursor;
		}

		if (entry.Instantiate() is not { } piece)
		{
			GD.PushWarning($"[TestArea] {entry.ScenePath} would not instance, so the chained "
						   + "course skipped it.");
			return cursor;
		}

		Transform3D placement = TrackSnap.PlacementFor(cursor, entrySeam.Local);

		piece.Name = $"Run{placed:00}_{entry.Name}";
		_generated.AddChild(piece);
		piece.GlobalTransform = placement;
		placed++;

		return TrackSnap.CursorFor(placement, exitSeam.Local);
	}
}
