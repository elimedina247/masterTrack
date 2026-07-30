using System;
using System.Collections.Generic;
using Godot;
using MasterTrack.Tiles;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Game;

/// <summary>
/// The authored pieces, each set out as its own specimen on the pad with tarmac under it and a
/// run-up to it.
///
/// <b>Specimens rather than a chain, and that was a correction.</b> They were laid end to end,
/// which is the right way to check that seams meet — and completely the wrong way to look at a
/// piece you are in the middle of building. Two things went wrong with it. The chain started off
/// the western edge of the pad, so there was no ground to drive out to it on; and because each
/// piece was laid at the one before's exit, a single climbing piece put <i>everything after it</i>
/// in the air. A Twister that rises seventy-two metres left the rest of the row hanging over the
/// void.
///
/// Set out separately, every piece starts on the ground, sits on tarmac, and can be driven onto
/// from the pad — which is what an author actually needs while shaping one. The fold arithmetic is
/// still reported per piece, so a seam that has gone wrong is still visible in the log.
///
/// Everything in <c>res://scenes/tiles/pieces</c> is picked up, in name order, so authoring a new
/// one is dropping a scene in a folder.
/// </summary>
public partial class PhysicsTestArea
{
	/// <summary>Where the authored pieces are kept. Anything here is laid out; nothing else is.</summary>
	private const string PieceFolder = "res://scenes/tiles/pieces";

	/// <summary>
	/// Cell the row of specimens runs along.
	///
	/// <b>Straddling the origin rather than trailing off to one side.</b> The row used to start west
	/// of the catalog grid and run outward, which put the eighth piece two kilometres from where the
	/// car spawns — most of the time spent looking at a piece was spent driving to it. Centred, the
	/// furthest is about a third of that and the nearest is straight ahead.
	///
	/// Just south of the catalog's rows, which is the one band of the pad nothing else claims: the
	/// tile grid reaches Z cell 4 at nine columns, and the two tracks anchor off the far edges at
	/// -15 and 15.
	/// </summary>
	private const int PieceRowCell = 7;

	/// <summary>
	/// Cells between one specimen and the next.
	///
	/// Cut from six, which put 324 m between pieces and pushed the pad out past two kilometres in
	/// each direction — more time spent driving between them than looking at them. Four is 216 m,
	/// which still clears the widest thing built so far: a three-loop corkscrew measures 162 m
	/// across, so there is a clear cell between it and its neighbour.
	///
	/// An authored piece has no size limit the way a catalog tile does, so this is the one number to
	/// raise if pieces start overlapping. The proving ground reports every piece's extents, which is
	/// what to read it against.
	/// </summary>
	private const int PieceCellStride = 4;

	/// <summary>How many authored pieces there are, counted without loading any of them.</summary>
	private static int PieceCount => PieceScenePaths().Length;

	/// <summary>Cell of the first specimen, so the row straddles the origin.</summary>
	private static int FirstPieceCell => -((PieceCount - 1) * PieceCellStride) / 2;

	/// <summary>
	/// How far across the pad has to reach for the whole row to have ground under it.
	///
	/// Grown to this in <see cref="MeasurePad"/>, for the same reason the pad is grown to reach the
	/// two tracks: everything off it is open air, so a specimen a metre past the edge is one you can
	/// only arrive at by falling.
	/// </summary>
	private static float PadEdgeForPieces
	{
		get
		{
			int count = PieceCount;
			if (count == 0)
				return 0.0f;

			int first = FirstPieceCell;
			int last = first + (count - 1) * PieceCellStride;
			int furthest = Mathf.Max(Mathf.Abs(first), Mathf.Abs(last));

			// Plus a piece's own reach, since a specimen is placed by its entry and sweeps outward
			// from there rather than being centred on it.
			return (furthest + PieceCellStride) * TileCatalog.TileSize;
		}
	}

	/// <summary>And how far along, for the same reason. A piece runs north from its row.</summary>
	private static float PadEdgeAlongForPieces
		=> PieceCount == 0 ? 0.0f : (PieceRowCell + PieceCellStride) * TileCatalog.TileSize;

	/// <summary>
	/// Set every authored piece down on the pad, at ground level, facing north — so the run-up is
	/// from +Z, which is the way a racer meets a tile in a match.
	/// </summary>
	private void BuildPieceSpecimens()
	{
		string[] paths = PieceScenePaths();
		if (paths.Length == 0)
			return;

		for (var i = 0; i < paths.Length; i++)
		{
			var scene = GD.Load<PackedScene>(paths[i]);
			if (scene == null)
			{
				GD.PushError($"[TestArea] Could not load the authored piece at {paths[i]}.");
				continue;
			}

			if (scene.Instantiate() is not TrackPiece piece)
			{
				GD.PushError($"[TestArea] {paths[i]} is not a TrackPiece.");
				continue;
			}

			string label = System.IO.Path.GetFileNameWithoutExtension(paths[i]);
			piece.Name = $"Piece_{label}";

			var cell = new Vector2I(FirstPieceCell + i * PieceCellStride, PieceRowCell);
			Vector3 world = TileCatalog.CellToWorld(cell);

			_generated.AddChild(piece);
			PlaceByEntry(piece, world, 0.0f);

			// On the near side, so it is readable from the run-up rather than buried in the piece.
			AddLabel(label, world + new Vector3(0.0f, 8.0f, TileCatalog.TileSize * 0.7f),
					 new Color(1.0f, 0.82f, 0.05f));

			float bend = ReportBend(piece);
			ReportSeamRoll(piece);

			TrackAnchor exit = piece.ExitAnchor;
			GD.Print($"[TestArea]   {piece.Name}: run {piece.RunLength:0.###} m, "
					 + $"exit {exit.Position} at {Mathf.RadToDeg(exit.Yaw):0.###} deg, "
					 + $"rise {piece.HeightChange:0.###} m, roll {piece.ExitRollDegrees:0.##} deg, "
					 + $"road {piece.RoadWidth:0.##} m (catalog {TileCatalog.TileSize:0.##}), "
					 + $"bend {bend:0.##} deg/m (63 m corner is 0.91), "
					 + (piece.IsBaked ? "baked." : "live CSG (not baked)."));
		}

		AddLabel("Authored pieces ↓",
				 TileCatalog.CellToWorld(new Vector2I(0, PieceRowCell))
				 + new Vector3(0.0f, 16.0f, TileCatalog.TileSize * 1.4f),
				 new Color(1.0f, 0.82f, 0.05f));

		GD.Print($"[TestArea] Laid out {paths.Length} authored piece(s) along row {PieceRowCell} "
				 + $"from cell {FirstPieceCell}, every {PieceCellStride} cells, at ground level. "
				 + $"Pad reaches "
				 + $"X +/-{_padHalfX:0} m, Z +/-{_padHalfZ:0} m.");

		ReportGeometry();
	}

	/// <summary>
	/// How hard the spine bends, measured as degrees of heading change per metre travelled, and
	/// where the worst of it is.
	///
	/// <b>Per metre rather than per sample, which is the whole point.</b> A curve sampled finely
	/// turns a little at every step and one sampled coarsely turns a lot, so the raw angle between
	/// consecutive segments says more about the sampling than about the road. Divided through by
	/// the distance it is spread over, it becomes curvature — a number that means the same thing at
	/// any resolution, and one a car can be judged against.
	///
	/// A 63 m corner, which is <c>TileCatalog.CornerRadius</c> and the tightest the catalog turns at
	/// speed, works out at 0.91 deg/m. Anything much past that is tighter than the game's own
	/// corners; several times it is a kink rather than a curve.
	/// </summary>
	private static float ReportBend(TrackPiece piece)
	{
		if (piece.Spine?.Curve is not { PointCount: >= 2 } curve)
			return 0.0f;

		Vector3[] baked = curve.GetBakedPoints();
		if (baked.Length < 3)
			return 0.0f;

		var worst = 0.0f;
		Vector3 worstAt = Vector3.Zero;

		for (var i = 1; i < baked.Length - 1; i++)
		{
			Vector3 into = baked[i] - baked[i - 1];
			Vector3 outOf = baked[i + 1] - baked[i];

			float span = (into.Length() + outOf.Length()) * 0.5f;
			if (span < 0.01f || into.LengthSquared() < 1e-6f || outOf.LengthSquared() < 1e-6f)
				continue;

			float turn = Mathf.RadToDeg(into.Normalized().AngleTo(outOf.Normalized())) / span;
			if (turn <= worst)
				continue;

			worst = turn;
			worstAt = baked[i];
		}

		// 0.91 deg/m is the catalog's own corner radius. Twice that is tighter than anything the
		// game ships and is where a curve starts reading as a crease.
		if (worst >= 1.8f)
		{
			GD.PushWarning($"[TestArea] {piece.Name} bends {worst:0.##} deg/m at "
						   + $"({worstAt.X:0.#}, {worstAt.Y:0.#}, {worstAt.Z:0.#}) — a 63 m corner is "
						   + "0.91. Raise Smoothing, move the waypoints apart, or aim them closer to "
						   + "the way the road actually runs between them.");
		}

		return worst;
	}

	/// <summary>
	/// How far the road itself is banked where it meets its neighbours — as opposed to how far the
	/// seam marker says it is.
	///
	/// Those are not the same thing, which is the whole reason this exists. A curve that both climbs
	/// and turns carries its own frame along itself, and that frame accumulates roll as it goes: the
	/// marker can sit dead level, report zero rotation, and still have the road arrive at it visibly
	/// banked. It is the road the next tile butts against, so the road is what has to be measured.
	/// </summary>
	private static void ReportSeamRoll(TrackPiece piece)
	{
		if (piece.Spine?.Curve is not { PointCount: >= 2 } curve)
			return;

		float length = curve.GetBakedLength();
		if (length < 0.01f)
			return;

		float entry = RoadRollAt(curve, 0.0f);
		float exit = RoadRollAt(curve, length);

		if (Mathf.Abs(entry) < 1.0f && Mathf.Abs(exit) < 1.0f)
			return;

		// A baked piece is measured from the curve saved in its file, which is also the curve its
		// mesh was built from - so this is not a stale reading, it is the shape that actually
		// shipped, and re-baking is what fixes it.
		GD.PushWarning($"[TestArea] {piece.Name}'s road is banked {entry:0.#} deg at its entry and "
					   + $"{exit:0.#} deg at its exit, even though both seams are level. "
					   + (piece.IsBaked
						   ? "This piece is baked, so its mesh carries that roll: open it and bake "
							 + "again to pick up the corrected spine."
						   : "The spine's frame has picked up roll along the way."));
	}

	/// <summary>How far the swept road is rolled from vertical at a point along the spine, in
	/// degrees — read from the same up vector the sweep uses.</summary>
	private static float RoadRollAt(Curve3D curve, float offset)
	{
		float length = curve.GetBakedLength();
		float epsilon = Mathf.Max(0.05f, length * 0.001f);

		Vector3 ahead = curve.SampleBaked(Mathf.Min(length, offset + epsilon), cubic: true);
		Vector3 behind = curve.SampleBaked(Mathf.Max(0.0f, offset - epsilon), cubic: true);

		Vector3 forward = ahead - behind;
		if (forward.LengthSquared() < 1e-9f)
			return 0.0f;

		forward = forward.Normalized();

		Vector3 up = curve.SampleBakedUpVector(offset, applyTilt: true).Normalized();

		Vector3 right = forward.Cross(Vector3.Up);
		if (right.LengthSquared() < 1e-6f)
			return 0.0f;

		right = right.Normalized();
		return Mathf.RadToDeg(Mathf.Atan2(up.Dot(right), up.Dot(right.Cross(forward))));
	}

	/// <summary>
	/// Position a piece so that its <c>Entry</c> marker lands exactly on a spot, facing a heading.
	///
	/// By the entry rather than by the piece's own origin, because those are not the same thing and
	/// only one of them is the contract: a piece may be built anywhere in its own scene, and it is
	/// the entry that says where the racer arrives. This is the same placement the chain performs.
	/// </summary>
	private static void PlaceByEntry(TrackPiece piece, Vector3 position, float yaw)
	{
		var wanted = new Transform3D(new Basis(Vector3.Up, yaw), position);

		piece.Transform = piece.Entry is { } entry
			? wanted * entry.Transform.AffineInverse()
			: wanted;
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

			// In world space, because "is it on the pad" is a question about where it actually
			// ended up, not about how big it is.
			Aabb world = piece.Transform * bounds;
			bool onPad = Mathf.Abs(world.Position.X) <= _padHalfX
						 && Mathf.Abs(world.End.X) <= _padHalfX
						 && Mathf.Abs(world.Position.Z) <= _padHalfZ
						 && Mathf.Abs(world.End.Z) <= _padHalfZ;

			GD.Print($"[TestArea]   {piece.Name}: live CSG, extent "
					 + $"{bounds.Size.X:0.##} x {bounds.Size.Y:0.##} x {bounds.Size.Z:0.##} m, "
					 + $"base Y {bounds.Position.Y:0.##} m, world X {world.Position.X:0} to "
					 + $"{world.End.X:0}, Z {world.Position.Z:0} to {world.End.Z:0}, "
					 + (onPad ? "on the pad." : "OFF THE PAD."));

			// A specimen is set down by its Entry marker, so a piece whose geometry does not reach
			// down to the entry is a piece hanging in the air above the spot it was placed on. It
			// happens when the spine and the Entry have come apart — which the generated route makes
			// impossible, but a hand-authored curve kept from before it can still be carrying.
			if (bounds.Position.Y > 2.0f)
			{
				GD.PushWarning($"[TestArea] {piece.Name}'s road starts {bounds.Position.Y:0.#} m above "
							   + "its Entry, so it floats above the pad. Its spine is authored rather "
							   + "than generated — add a waypoint to the piece and the route is rebuilt "
							   + "from Entry through to Exit, which puts it back on the ground.");
			}
		}
	}

	/// <summary>
	/// Every authored piece, in name order.
	///
	/// Sorted rather than left in whatever order the filesystem hands them over, so the row is the
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
}
