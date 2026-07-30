using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// A run of track built out of <see cref="TrackPiece"/> instances: the container you assemble a
/// course in, and the thing that knows which seams are still open to build from.
///
/// <b>It holds ordinary children and does nothing to them.</b> A piece in an assembly is a scene
/// instance with a transform, the same as a piece dropped anywhere else — the assembly is where
/// the joining knowledge lives, not a format. Delete the assembly's script and the track still
/// stands; save the assembly as a scene and it is a course.
///
/// Three things live here:
///
/// <list type="bullet">
/// <item><see cref="Attach"/> — place one piece so its entry lands on a given seam, which is the
/// whole of what "snapped together" means. The editor's click-to-extend calls this; so can
/// anything else.</item>
/// <item><see cref="OpenSeams"/> — the frontier: every exit no piece has been built onto yet.
/// The click-to-extend handles draw on these.</item>
/// <item><see cref="SnapChain"/> — re-thread every piece in tree order, each one's entry onto the
/// piece before it. The by-hand workflow: drag piece scenes in from the FileSystem dock in any
/// order and at any position, tick once, and they are a track.</item>
/// </list>
/// </summary>
[Tool]
[GlobalClass]
public partial class TrackAssembly : Node3D
{
	/// <summary>
	/// How close two seams have to sit to count as the same joint, in metres.
	///
	/// Snapped joints are exact to floating point, so this could be tiny; it is half a metre so
	/// that a joint somebody nudged — or assembled by eye before the tool existed — still reads as
	/// a joint rather than as two open seams occupying the same air.
	/// </summary>
	private const float MatedDistance = 0.5f;

	/// <summary>How closely two mated seams' directions of travel have to agree. Cosine of about
	/// eleven degrees: forgiving of a nudge, unforgiving of a seam facing back the way it came.</summary>
	private const float MatedAlignment = 0.98f;

	/// <summary>
	/// Metadata key that marks a piece as editor scaffolding — the translucent previews the plugin
	/// parks at open seams. Everything the assembly counts, snaps or reports skips them: a ghost
	/// that occupied a seam would close the very frontier it exists to advertise.
	/// </summary>
	public const string GhostMeta = "track_ghost";

	/// <summary>The pieces in the assembly, in tree order — which is also the order
	/// <see cref="SnapChain"/> threads them in. Ghost previews are not pieces.</summary>
	public IEnumerable<TrackPiece> Pieces
	{
		get
		{
			foreach (Node child in GetChildren())
			{
				if (child is TrackPiece piece && !piece.HasMeta(GhostMeta))
					yield return piece;
			}
		}
	}

	/// <summary>
	/// Place a piece so that its entry seam lands exactly on <paramref name="ontoSeam"/> —
	/// position, heading, pitch and roll all carried through the joint.
	///
	/// This is the one write the assembly ever makes to a piece, and it is the piece's whole
	/// transform: where a piece sat before it was attached is scaffolding, same as the root scale
	/// warning on <see cref="TrackPiece"/> says.
	/// </summary>
	public static void Attach(TrackPiece piece, Marker3D ontoSeam)
	{
		if (piece.EntrySeam is not { } entry)
		{
			GD.PushWarning($"[TrackAssembly] {piece.Name} has no entry seam to attach by. "
						   + "Give it an Entry connector.");
			return;
		}

		piece.GlobalTransform = TrackSnap.PlacementFor(TrackSnap.CursorOf(ontoSeam), entry.Transform);
	}

	/// <summary>
	/// The frontier: every exit seam in the assembly that no piece's entry currently sits on.
	/// These are where the track can grow — the click-to-extend handles are drawn on exactly this
	/// list.
	///
	/// Computed by looking, not tracked by bookkeeping: a joint is two seams occupying the same
	/// frame, so deleting a piece from the middle of a run reopens its neighbours' seams with no
	/// state to have forgotten to update.
	/// </summary>
	public List<(TrackPiece Piece, Marker3D Seam)> OpenSeams()
	{
		var exits = new List<(TrackPiece, Marker3D)>();
		var entries = new List<Marker3D>();

		foreach (TrackPiece piece in Pieces)
		{
			foreach (Marker3D seam in piece.ExitSeams)
				exits.Add((piece, seam));

			if (piece.EntrySeam is { } entry)
				entries.Add(entry);
		}

		var open = new List<(TrackPiece, Marker3D)>();

		foreach ((TrackPiece piece, Marker3D exit) in exits)
		{
			var mated = false;
			foreach (Marker3D entry in entries)
			{
				if (Mated(exit, entry))
				{
					mated = true;
					break;
				}
			}

			if (!mated)
				open.Add((piece, exit));
		}

		return open;
	}

	/// <summary>Whether two seams occupy the same joint: same place, travelling the same way.</summary>
	private static bool Mated(Marker3D exit, Marker3D entry)
	{
		Transform3D a = exit.GlobalTransform;
		Transform3D b = entry.GlobalTransform;

		if (a.Origin.DistanceTo(b.Origin) > MatedDistance)
			return false;

		Vector3 forwardA = -a.Basis.Z;
		Vector3 forwardB = -b.Basis.Z;

		return forwardA.Normalized().Dot(forwardB.Normalized()) >= MatedAlignment;
	}

	/// <summary>
	/// Tick to thread every piece in tree order: the first stays where it is, and each of the rest
	/// is attached to the first open exit of the piece before it. Unticks itself.
	///
	/// The by-hand assembly workflow, no plugin required: instance piece scenes under the assembly
	/// in whatever order and position, arrange the tree, tick. Not undoable — like Bake, it is a
	/// button rather than a gesture, and re-ticking after a rearrange is the way back.
	/// </summary>
	[Export]
	public bool SnapChain
	{
		get => false;
		set
		{
			if (value && Engine.IsEditorHint())
				RunSnapChain();
		}
	}

	private void RunSnapChain()
	{
		TrackPiece? previous = null;

		foreach (TrackPiece piece in Pieces)
		{
			if (previous != null)
			{
				Marker3D? onto = null;
				foreach (Marker3D seam in previous.ExitSeams)
				{
					onto = seam;
					break;
				}

				if (onto == null)
				{
					GD.PushWarning($"[TrackAssembly] {previous.Name} has no exit seam, so "
								   + $"{piece.Name} and everything after it were left alone.");
					return;
				}

				Attach(piece, onto);
			}

			previous = piece;
		}
	}
}
