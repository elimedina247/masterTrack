using Godot;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Tools;

/// <summary>
/// Bake every authored track piece in one headless run:
///
/// <code>
/// $GODOT --headless --path . res://tools/BakePieces.tscn
/// </code>
///
/// An unbaked piece rebuilds its CSG on every placement, on every peer, mid-race — which is the
/// hitch the Track Master feels every time they play a big card. The fix has always been the Bake
/// tick box on <see cref="TrackPiece"/>; this scene just presses it for the whole catalog, so the
/// answer to "which pieces are baked" can be "all of them" without anyone auditing seventeen
/// scenes by hand. Re-run it after editing any piece's shape.
///
/// Each piece is instantiated, given a few frames for its CSG to build (the combiner folds its
/// children together one deferred update after their scripts generate meshes — baking earlier
/// hands back an empty shape), baked, and saved <b>over its own scene file</b>. Saving through the
/// loaded <see cref="PackedScene"/> keeps the file's uid, so nothing that references the piece
/// notices the rewrite. Already-baked pieces are skipped: their <c>Build</c> subtree is freed at
/// runtime by <see cref="TrackPiece"/> itself, so there is nothing here to bake again.
///
/// <c>-- --force</c> re-bakes the already-baked instead of skipping them — what a change to any
/// of the road <i>generators</i> needs, because every saved bake is that generator's old output:
///
/// <code>
/// $GODOT --headless --path . res://tools/BakePieces.tscn -- --force
/// </code>
/// </summary>
public partial class BakePieces : Node
{
	/// <summary>Frames to wait for the CSG to settle before baking, and again for the bake.</summary>
	private const int CsgSettleFrames = 3;
	private const int BakeTimeoutFrames = 300;

	public override void _Ready() => _ = BakeAllAsync();

	private static bool ForceRequested()
	{
		foreach (string arg in OS.GetCmdlineUserArgs())
		{
			if (arg == "--force")
				return true;
		}

		return false;
	}

	private async System.Threading.Tasks.Task BakeAllAsync()
	{
		var baked = 0;
		var skipped = 0;
		var failed = 0;

		bool force = ForceRequested();

		// Before the first instance, because each piece decides whether to keep its CSG inside its
		// own _Ready — which runs during AddChild, well before this loop could reach it.
		TrackPiece.KeepBuildForRebake = force;

		foreach (PieceEntry entry in PieceCatalog.Entries)
		{
			if (GD.Load<PackedScene>(entry.ScenePath) is not { } scene
				|| scene.Instantiate() is not TrackPiece piece)
			{
				GD.PushError($"[BakePieces] {entry.ScenePath} would not instance as a TrackPiece.");
				failed++;
				continue;
			}

			AddChild(piece);

			if (piece.IsBaked && !force)
			{
				GD.Print($"[BakePieces] {entry.Name}: already baked, skipping.");
				skipped++;
				piece.QueueFree();
				continue;
			}

			// A forced re-bake drops the stale bake first, and not only for tidiness: completion
			// is observed through IsBaked, so a piece that still wears its old bake would read as
			// done the instant RunBake was called — and be saved with the old mesh still on.
			if (piece.IsBaked)
			{
				foreach (string stale in new[] { "BakedMesh", "BakedCollision" })
				{
					if (piece.GetNodeOrNull(stale) is { } node)
					{
						piece.RemoveChild(node);
						node.Free();
					}
				}
			}

			for (var i = 0; i < CsgSettleFrames; i++)
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

			piece.RunBake();

			var waited = 0;
			while (!piece.IsBaked && waited++ < BakeTimeoutFrames)
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

			if (!piece.IsBaked)
			{
				// RunBake said why on its own warning — no Build node, or an empty result.
				GD.PushError($"[BakePieces] {entry.Name} did not bake.");
				failed++;
			}
			else
			{
				scene.Pack(piece);
				Error error = ResourceSaver.Save(scene, entry.ScenePath);
				if (error == Error.Ok)
				{
					baked++;
				}
				else
				{
					GD.PushError($"[BakePieces] {entry.Name} baked but would not save: {error}.");
					failed++;
				}
			}

			piece.QueueFree();
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}

		GD.Print($"[BakePieces] Done: {baked} baked, {skipped} already baked, {failed} failed.");
		GetTree().Quit(failed > 0 ? 1 : 0);
	}
}
