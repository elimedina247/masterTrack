using System;
using System.Collections.Generic;
using Godot;
using MasterTrack.Tiles;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Game;

/// <summary>
/// The authored pieces, chained end to end off the west edge of the pad.
///
/// <b>Chained rather than set out as specimens, and that is the whole point of it.</b> The tile
/// layout on the pad answers "what does this look like"; a piece authored by hand has a second
/// question to answer, which is whether it still meets its neighbours squarely once a person has
/// been dragging its spine about. Joining them up is the only thing that shows that — a seam that
/// steps, gaps or kinks is invisible on a piece sitting by itself and unmissable at 200 km/h with
/// another piece bolted to it.
///
/// Everything in <c>res://scenes/tiles/pieces</c> is picked up, in name order, so authoring a new
/// one is dropping a scene in a folder. Nothing here has to be told about it.
/// </summary>
public partial class PhysicsTestArea
{
	/// <summary>Where the authored pieces are kept. Anything here is laid out; nothing else is.</summary>
	private const string PieceFolder = "res://scenes/tiles/pieces";

	/// <summary>
	/// Which way the chain runs off the pad: west, because north and south are already spoken for by
	/// the buildable track and the fixed course, and three tracks growing away from each other can
	/// never meet in the middle.
	/// </summary>
	private static float PieceChainYaw => Mathf.Pi * 0.5f;

	/// <summary>
	/// Lay every authored piece down in a row, each one starting where the last one finished.
	///
	/// The fold is the same one <c>TrackGrid</c> performs: a piece reports where it hands the track
	/// on, in its own local space, and that is carried onto the running anchor. So this is not a
	/// preview of how the pieces would chain — it is the chain, run by the same arithmetic the game
	/// uses, which is what makes a seam that looks right here evidence that it is right.
	/// </summary>
	private void BuildPieceChain()
	{
		string[] paths = PieceScenePaths();
		if (paths.Length == 0)
			return;

		var anchor = new TrackAnchor(new Vector3(-_padHalfX, 0.0f, 0.0f), PieceChainYaw);

		AddLabel("Authored pieces →", anchor.Position + new Vector3(0.0f, 10.0f, 0.0f),
				 new Color(1.0f, 0.82f, 0.05f));

		var vertices = 0;
		var shapes = 0;

		foreach (string path in paths)
		{
			var scene = GD.Load<PackedScene>(path);
			if (scene == null)
			{
				GD.PushError($"[TestArea] Could not load the authored piece at {path}.");
				continue;
			}

			if (scene.Instantiate() is not TrackPiece piece)
			{
				GD.PushError($"[TestArea] {path} is not a TrackPiece, so it cannot be chained.");
				continue;
			}

			piece.Name = $"Piece_{System.IO.Path.GetFileNameWithoutExtension(path)}";
			piece.Position = anchor.Position;
			piece.Rotation = new Vector3(0.0f, anchor.Yaw, 0.0f);
			_generated.AddChild(piece);

			// Read after the piece is in the tree, so its spine has been readied and the geometry it
			// reports on is the geometry that actually got built.
			TrackAnchor exit = piece.ExitAnchor;

			AddLabel(piece.Name.ToString().Replace("Piece_", ""),
					 anchor.Position + new Vector3(0.0f, 7.0f, 0.0f),
					 new Color(1.0f, 0.82f, 0.05f));

			anchor = Fold(anchor, exit);

			int pieceVertices = CountVertices(piece);
			int pieceShapes = CountShapes(piece);
			vertices += pieceVertices;
			shapes += pieceShapes;

			// The exit is the number an author has to be able to check, because it is the one the
			// chain acts on: a piece that reports the wrong seam still looks perfectly correct on
			// its own and puts a step in every joint it is ever used at.
			GD.Print($"[TestArea]   {piece.Name}: run {piece.RunLength:0.###} m, "
					 + $"exit {exit.Position} at {Mathf.RadToDeg(exit.Yaw):0.###} deg, "
					 + $"rise {piece.HeightChange:0.###} m, roll {piece.ExitRollDegrees:0.##} deg, "
					 + (piece.IsBaked
						 ? $"baked: {pieceVertices} vertices, {pieceShapes} shape(s)."
						 : "live CSG (not baked)."));
		}

		GD.Print($"[TestArea] Chained {paths.Length} authored piece(s): {vertices} vertices, "
				 + $"{shapes} collision shape(s), ending at {anchor.Position}.");

		ReportGeometry();
	}

	/// <summary>
	/// Say whether each piece actually produced any geometry.
	///
	/// Worth its own pass because an empty piece is completely silent otherwise: a
	/// <see cref="CsgPolygon3D"/> in path mode with no path builds nothing, reports no error, and
	/// leaves an author staring at a bake button that appears to do nothing. This is the check that
	/// names it — and it has to be a frame late, because CSG rebuilds are deferred.
	/// </summary>
	private async void ReportGeometry()
	{
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		foreach (Node child in _generated.GetChildren())
		{
			if (child is not TrackPiece piece)
				continue;

			if (piece.IsBaked)
			{
				GD.Print($"[TestArea]   {piece.Name}: baked geometry.");
				continue;
			}

			CsgShape3D? build = piece.Build;
			if (build == null)
			{
				GD.PushWarning($"[TestArea] {piece.Name} has no Build node and nothing baked.");
				continue;
			}

			int surfaces = build.GetMeshes().Count;
			Aabb bounds = build.GetAabb();

			if (surfaces == 0 || bounds.Size.LengthSquared() < 1.0f)
			{
				GD.PushWarning($"[TestArea] {piece.Name} builds no geometry. If its Road is a "
							   + "CSGPolygon3D in path mode, check path_node actually points at the "
							   + "Spine — an empty NodePath silently produces nothing.");
				continue;
			}

			GD.Print($"[TestArea]   {piece.Name}: live CSG, extent "
					 + $"{bounds.Size.X:0.##} x {bounds.Size.Y:0.##} x {bounds.Size.Z:0.##} m.");
		}
	}

	/// <summary>
	/// Carry a piece's local exit onto the anchor it was placed at — the one operation the whole
	/// chain is built from, and the same one <c>PlacedTile.ExitAnchorFor</c> performs for the
	/// catalog's tiles.
	/// </summary>
	private static TrackAnchor Fold(TrackAnchor at, TrackAnchor exit)
		=> new(at.Position + new Basis(Vector3.Up, at.Yaw) * exit.Position, at.Yaw + exit.Yaw);

	/// <summary>
	/// Every authored piece, in name order.
	///
	/// Sorted rather than left in whatever order the filesystem hands them over, so the chain is the
	/// same on every machine and a screenshot of it means something.
	/// </summary>
	private static string[] PieceScenePaths()
	{
		using DirAccess? dir = DirAccess.Open(PieceFolder);
		if (dir == null)
			return Array.Empty<string>();

		var paths = new List<string>();

		foreach (string file in dir.GetFiles())
		{
			// Exported projects hand back the imported name, so a scene arrives as .tscn.remap and
			// has to be asked for under its original name.
			string name = file.EndsWith(".remap", StringComparison.Ordinal)
				? file[..^".remap".Length]
				: file;

			if (name.EndsWith(".tscn", StringComparison.Ordinal))
				paths.Add($"{PieceFolder}/{name}");
		}

		paths.Sort(StringComparer.Ordinal);
		return paths.ToArray();
	}

	/// <summary>Vertices a piece's generated mesh came out at. Reported so that the cost of a change
	/// to a profile's resolution is visible rather than guessed at.</summary>
	private static int CountVertices(Node piece)
	{
		var total = 0;

		foreach (Node child in piece.GetChildren())
		{
			if (child is not MeshInstance3D { Mesh: { } mesh })
				continue;

			for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
				total += mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
		}

		return total;
	}

	/// <summary>
	/// The box a piece's geometry actually occupies, in its own local space.
	///
	/// Worth printing beside the exit anchor because the two answer different questions and an
	/// author needs both: the anchor says where the piece hands the track on, and this says how much
	/// room it swept getting there — which is what decides whether it can be placed at all.
	/// </summary>
	private static Aabb Bounds(Node piece)
	{
		var bounds = new Aabb();
		var started = false;

		foreach (Node child in piece.GetChildren())
		{
			if (child is not MeshInstance3D mesh)
				continue;

			Aabb box = mesh.GetAabb();
			bounds = started ? bounds.Merge(box) : box;
			started = true;
		}

		return bounds;
	}

	private static int CountShapes(Node piece)
	{
		var total = 0;
		foreach (Node child in piece.GetChildren())
		{
			if (child is CollisionShape3D)
				total++;
		}

		return total;
	}
}
