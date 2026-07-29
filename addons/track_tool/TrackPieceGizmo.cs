#if TOOLS
using System.Collections.Generic;
using Godot;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Editor;

/// <summary>
/// Draws a <see cref="TrackPiece"/>'s route in the viewport and puts draggable handles on it, so a
/// piece is shaped where it can be seen instead of through the scene tree.
///
/// Every point on the route gets a bar across the road with an arrow through it — the entry in
/// green, the exit in cyan, waypoints in white — and the generated spine is drawn as a polyline,
/// which is what makes the threading order readable: the road connects the points in the order they
/// will be driven.
///
/// <b>The handles are the contract made visible.</b> A seam may move, rise and turn but never pitch
/// or roll — <c>TrackAnchor</c> is a position and a yaw, so a tilted seam is a frame the next piece
/// cannot be built against. Rather than letting the rotation happen and snapping it back, the entry
/// and exit simply do not have bank handles: the handles that exist are exactly the moves that are
/// legal, and nothing the tool offers ever silently un-does itself. Waypoints get the full set —
/// aim in any direction, and a bank handle on each road edge that is dragged up and down.
///
/// Handle kinds, per route node:
/// <list type="bullet">
/// <item><b>Move</b> — the node's centre; drags across the ground plane at its height.</item>
/// <item><b>Height</b> — a dot above the node; drags vertically.</item>
/// <item><b>Aim</b> — the arrow tip; point it where the road should go. Yaw only on seams,
/// yaw and pitch on waypoints.</item>
/// <item><b>Bank</b> — the two bar ends, waypoints only; lift an edge to roll the road.</item>
/// </list>
///
/// Between consecutive nodes a secondary handle sits on the route: clicking it inserts a waypoint
/// there, which with the toolbar's remove button covers add and delete without the Add Node dialog.
///
/// Selecting a route <i>marker</i> rather than the piece draws the same picture around it, so the
/// ordinary select-and-transform workflow gets the same feedback the handles do.
/// </summary>
public partial class TrackPieceGizmo : EditorNode3DGizmoPlugin
{
	private const string EntryMaterial = "entry";
	private const string ExitMaterial = "exit";
	private const string WaypointMaterial = "waypoint";
	private const string RouteMaterial = "route";
	private const string HandleMaterial = "handles";
	private const string InsertHandleMaterial = "insert_handles";

	/// <summary>Half the width of the bar drawn across a seam — the catalog's road, so a piece
	/// whose section is narrower reads as narrow at a glance.</summary>
	private const float SeamHalfWidth = 27.0f;

	/// <summary>How far the direction arrow reaches out of a node. The aim handle sits on its tip.</summary>
	private const float ArrowLength = 14.0f;

	/// <summary>How far above a node its height handle floats.</summary>
	private const float HeightLift = 10.0f;

	/// <summary>Height of the uprights at the bar ends. The bank handles sit on their tips.</summary>
	private const float BarUpright = 4.0f;

	private const int KindMove = 0;
	private const int KindHeight = 1;
	private const int KindAim = 2;
	private const int KindBankLeft = 3;
	private const int KindBankRight = 4;
	private const int KindStride = 5;

	/// <summary>Steepest a waypoint can be aimed, in degrees. Short of vertical, where yaw and
	/// roll stop being distinguishable and the frame degenerates.</summary>
	private const float MaxPitchDegrees = 85.0f;

	/// <summary>Hardest a road edge can be banked, in degrees.</summary>
	private const float MaxBankDegrees = 80.0f;

	/// <summary>The plugin, for undo/redo and waypoint insertion. Set right after construction;
	/// null only leaves drags un-undoable rather than broken.</summary>
	public TrackToolPlugin? Plugin { get; set; }

	public TrackPieceGizmo()
	{
		CreateMaterial(EntryMaterial, new Color(0.2f, 1.0f, 0.4f));
		CreateMaterial(ExitMaterial, new Color(0.2f, 0.9f, 1.0f));
		CreateMaterial(WaypointMaterial, new Color(1.0f, 1.0f, 1.0f));
		CreateMaterial(RouteMaterial, new Color(1.0f, 0.82f, 0.05f));
		CreateHandleMaterial(HandleMaterial);
		CreateHandleMaterial(InsertHandleMaterial);
	}

	public override string _GetGizmoName() => "TrackPiece";

	public override bool _HasGizmo(Node3D forNode3D)
		=> forNode3D is TrackPiece
		   || (forNode3D is Marker3D marker && marker.GetParent() is TrackPiece);

	public override void _Redraw(EditorNode3DGizmo gizmo)
	{
		gizmo.Clear();

		switch (gizmo.GetNode3D())
		{
			case TrackPiece piece:
				DrawPiece(gizmo, piece);
				break;

			// A selected marker draws the whole route around itself, converted into its own space —
			// so dragging one with the standard gizmo still shows the road it is reshaping.
			case Marker3D marker when marker.GetParent() is TrackPiece piece:
				DrawRoute(gizmo, piece, marker.Transform.AffineInverse());
				break;
		}
	}

	private void DrawPiece(EditorNode3DGizmo gizmo, TrackPiece piece)
	{
		DrawRoute(gizmo, piece, Transform3D.Identity);

		List<Marker3D> route = RouteOf(piece);
		if (route.Count < 2)
			return;

		var handles = new List<Vector3>();
		var ids = new List<int>();

		for (var i = 0; i < route.Count; i++)
		{
			Marker3D node = route[i];
			Transform3D at = node.Transform;

			handles.Add(at.Origin);
			ids.Add(i * KindStride + KindMove);

			handles.Add(at.Origin + Vector3.Up * HeightLift);
			ids.Add(i * KindStride + KindHeight);

			handles.Add(at.Origin + at.Basis * Vector3.Forward * ArrowLength);
			ids.Add(i * KindStride + KindAim);

			// The one omission that is the point: a seam has no bank handles, because a banked seam
			// is the one thing the chain cannot represent. Nothing to grab, nothing to snap back.
			if (IsSeam(piece, node))
				continue;

			handles.Add(at.Origin + at.Basis * (Vector3.Left * SeamHalfWidth + Vector3.Up * BarUpright));
			ids.Add(i * KindStride + KindBankLeft);

			handles.Add(at.Origin + at.Basis * (Vector3.Right * SeamHalfWidth + Vector3.Up * BarUpright));
			ids.Add(i * KindStride + KindBankRight);
		}

		gizmo.AddHandles(handles.ToArray(), GetMaterial(HandleMaterial, gizmo), ids.ToArray());

		// Insert points, one per segment, as secondary handles: click one and a waypoint appears
		// there. Secondary keeps them out of the way of the transform handles.
		var inserts = new List<Vector3>();
		var insertIds = new List<int>();

		for (var i = 0; i < route.Count - 1; i++)
		{
			inserts.Add(route[i].Transform.Origin.Lerp(route[i + 1].Transform.Origin, 0.5f));
			insertIds.Add(i);
		}

		gizmo.AddHandles(inserts.ToArray(), GetMaterial(InsertHandleMaterial, gizmo),
						 insertIds.ToArray(), secondary: true);
	}

	/// <summary>
	/// The route as everything but the handles: a bar per node and the generated spine threading
	/// them, all pushed through <paramref name="into"/> so a marker can draw the picture in its own
	/// space.
	/// </summary>
	private void DrawRoute(EditorNode3DGizmo gizmo, TrackPiece piece, Transform3D into)
	{
		foreach (Marker3D node in RouteOf(piece))
		{
			string material = node == piece.Entry ? EntryMaterial
							: node == piece.Exit ? ExitMaterial
							: WaypointMaterial;

			DrawSeam(gizmo, into * node.Transform, GetMaterial(material, gizmo));
		}

		if (piece.Spine is not { Curve: { } curve } spine || curve.PointCount < 2)
			return;

		Vector3[] baked = curve.GetBakedPoints();
		if (baked.Length < 2)
			return;

		var lines = new Vector3[(baked.Length - 1) * 2];
		for (var i = 0; i < baked.Length - 1; i++)
		{
			lines[i * 2] = into * (spine.Transform * baked[i]);
			lines[i * 2 + 1] = into * (spine.Transform * baked[i + 1]);
		}

		gizmo.AddLines(lines, GetMaterial(RouteMaterial, gizmo));
	}

	/// <summary>A route node: a bar across the road, an upright at each end so its bank is
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

		gizmo.AddLines(new[]
		{
			left, right,
			left, left + up * BarUpright,
			right, right + up * BarUpright,

			origin, tip,
			tip, tip - forward * 4.0f + across * 3.0f,
			tip, tip - forward * 4.0f - across * 3.0f,
		}, material);
	}

	// ---- Handles ----

	public override string _GetHandleName(EditorNode3DGizmo gizmo, int handleId, bool secondary)
	{
		if (secondary)
			return "Insert waypoint";

		return (handleId % KindStride) switch
		{
			KindMove => "Move",
			KindHeight => "Height",
			KindAim => "Aim",
			_ => "Bank",
		};
	}

	public override Variant _GetHandleValue(EditorNode3DGizmo gizmo, int handleId, bool secondary)
	{
		if (secondary || gizmo.GetNode3D() is not TrackPiece piece)
			return handleId;

		List<Marker3D> route = RouteOf(piece);
		int index = handleId / KindStride;

		return index < route.Count ? route[index].Transform : default;
	}

	public override void _SetHandle(EditorNode3DGizmo gizmo, int handleId, bool secondary,
									Camera3D camera, Vector2 screenPos)
	{
		// Insert handles do nothing during a drag: the waypoint is created on the click's commit,
		// where it can be one clean undoable action instead of a node conjured mid-gesture.
		if (secondary)
			return;

		if (gizmo.GetNode3D() is not TrackPiece piece)
			return;

		List<Marker3D> route = RouteOf(piece);
		int index = handleId / KindStride;
		if (index >= route.Count)
			return;

		Marker3D node = route[index];
		bool seam = IsSeam(piece, node);
		Transform3D at = node.Transform;

		// The pick ray in the piece's own space, which is the space every route transform lives in.
		Transform3D inverse = piece.GlobalTransform.AffineInverse();
		Vector3 from = inverse * camera.ProjectRayOrigin(screenPos);
		Vector3 direction = (inverse.Basis * camera.ProjectRayNormal(screenPos)).Normalized();

		switch (handleId % KindStride)
		{
			case KindMove:
			{
				// Across the ground plane at the node's own height: placement in plan, with height
				// deliberately on its own handle so neither drag corrupts the other.
				var plane = new Plane(Vector3.Up, at.Origin.Y);
				if (plane.IntersectsRay(from, direction) is not { } hit)
					return;

				node.Transform = at with { Origin = new Vector3(hit.X, at.Origin.Y, hit.Z) };
				return;
			}

			case KindHeight:
			{
				// Against a vertical plane facing the camera, keeping only the vertical part.
				var normal = new Vector3(direction.X, 0.0f, direction.Z);
				if (normal.LengthSquared() < 1e-6f)
					return;

				normal = -normal.Normalized();
				var plane = new Plane(normal, normal.Dot(at.Origin));
				if (plane.IntersectsRay(from, direction) is not { } hit)
					return;

				node.Transform = at with
				{
					Origin = new Vector3(at.Origin.X, hit.Y - HeightLift, at.Origin.Z),
				};
				return;
			}

			case KindAim:
			{
				// Point the node at the cursor: the direction from the node to the nearest point on
				// the pick ray. On a seam the vertical part is dropped, which is the yaw-only
				// constraint doing its work in the one place it applies.
				Vector3 nearest = from + direction * Mathf.Max(0.0f, (at.Origin - from).Dot(direction));
				Vector3 aim = nearest - at.Origin;

				if (seam)
					aim.Y = 0.0f;

				if (aim.LengthSquared() < 0.25f)
					return;

				aim = aim.Normalized();

				float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(aim.Y, -1.0f, 1.0f)),
										  Mathf.DegToRad(-MaxPitchDegrees),
										  Mathf.DegToRad(MaxPitchDegrees));
				float yaw = Mathf.Atan2(-aim.X, -aim.Z);
				float roll = seam ? 0.0f : TrackPiece.RollOf(at.Basis);

				node.Transform = at with { Basis = AimedBasis(yaw, pitch, roll) };
				return;
			}

			case KindBankLeft:
			case KindBankRight:
			{
				// Lift a road edge: the angle from the node to the cursor, measured around the
				// direction of travel in the un-rolled frame, becomes the roll.
				Vector3 forward = (at.Basis * Vector3.Forward).Normalized();
				Vector3 flatRight = forward.Cross(Vector3.Up);
				if (flatRight.LengthSquared() < 1e-6f)
					return;

				flatRight = flatRight.Normalized();
				Vector3 flatUp = flatRight.Cross(forward).Normalized();

				Vector3 nearest = from + direction * Mathf.Max(0.0f, (at.Origin - from).Dot(direction));
				Vector3 reach = nearest - at.Origin;

				float a = reach.Dot(flatRight);
				float b = reach.Dot(flatUp);
				if (a * a + b * b < 0.25f)
					return;

				float bank = Mathf.Atan2(b, a);
				if (handleId % KindStride == KindBankLeft)
					bank = Mathf.Wrap(bank + Mathf.Pi, -Mathf.Pi, Mathf.Pi);

				bank = Mathf.Clamp(bank, Mathf.DegToRad(-MaxBankDegrees),
								   Mathf.DegToRad(MaxBankDegrees));

				float pitch = Mathf.Asin(Mathf.Clamp(forward.Y, -1.0f, 1.0f));
				float yaw = Mathf.Atan2(-forward.X, -forward.Z);

				node.Transform = at with { Basis = AimedBasis(yaw, pitch, bank) };
				return;
			}
		}
	}

	public override void _CommitHandle(EditorNode3DGizmo gizmo, int handleId, bool secondary,
									   Variant restore, bool cancel)
	{
		if (gizmo.GetNode3D() is not TrackPiece piece)
			return;

		if (secondary)
		{
			// The insert happens here rather than in _SetHandle so it lands as one undoable action.
			if (!cancel)
				Plugin?.InsertWaypointAfter(piece, handleId);
			return;
		}

		List<Marker3D> route = RouteOf(piece);
		int index = handleId / KindStride;
		if (index >= route.Count)
			return;

		Marker3D node = route[index];
		var previous = restore.AsTransform3D();

		if (cancel)
		{
			node.Transform = previous;
			return;
		}

		EditorUndoRedoManager? undo = Plugin?.GetUndoRedo();
		if (undo == null)
			return;

		undo.CreateAction($"Edit {node.Name} on {piece.Name}");
		undo.AddDoProperty(node, Node3D.PropertyName.Transform, node.Transform);
		undo.AddUndoProperty(node, Node3D.PropertyName.Transform, previous);
		undo.CommitAction(false);
	}

	// ---- Shared ----

	/// <summary>A basis facing (<paramref name="yaw"/>, <paramref name="pitch"/>) and rolled
	/// <paramref name="roll"/> about the direction it faces.</summary>
	private static Basis AimedBasis(float yaw, float pitch, float roll)
	{
		Basis aimed = Basis.FromEuler(new Vector3(pitch, yaw, 0.0f));

		if (Mathf.IsZeroApprox(roll))
			return aimed;

		Vector3 forward = aimed * Vector3.Forward;
		return aimed.Rotated(forward.Normalized(), roll);
	}

	private static bool IsSeam(TrackPiece piece, Marker3D node)
		=> node == piece.Entry || node == piece.Exit;

	private static List<Marker3D> RouteOf(TrackPiece piece)
	{
		var route = new List<Marker3D>();
		foreach (Marker3D node in piece.Route())
			route.Add(node);
		return route;
	}
}
#endif
