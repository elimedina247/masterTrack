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
/// <b>Every route node gets the full set of handles, seams included.</b> The chain folds full
/// transforms now (<c>TrackSnap</c>), so a banked or climbing seam is a legal frame for the next
/// piece to be built against — the entry and exit used to be denied bank handles because
/// <c>TrackAnchor</c> had nowhere to record a roll, and that restriction went out with it. A seam
/// drawn as a <c>TrackConnector</c> also shows its declared width: the bar across the road is that
/// wide, so a narrow lane reads as narrow at a glance.
///
/// Handle kinds, per route node:
/// <list type="bullet">
/// <item><b>Move</b> — the node's centre; drags across the ground plane at its height.</item>
/// <item><b>Height</b> — a dot above the node; drags vertically.</item>
/// <item><b>Aim</b> — the arrow tip; point it where the road should go, in yaw and pitch.</item>
/// <item><b>Bank</b> — the two bar ends; lift an edge to roll the road.</item>
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

		// Handles draw through geometry, or they may as well not draw: a handle is a dot a few
		// pixels wide sitting on — and half inside — a road slab, and the extend handles in
		// particular sit exactly where the ghost preview parks its mesh. Depth-tested, "there is
		// no + anywhere" was the accurate user report.
		foreach (string name in new[] { HandleMaterial, InsertHandleMaterial })
		{
			if (GetMaterial(name) is StandardMaterial3D material)
			{
				material.NoDepthTest = true;
				material.RenderPriority = 100;
			}
		}
	}

	public override string _GetGizmoName() => "TrackPiece";

	public override bool _HasGizmo(Node3D forNode3D)
		=> forNode3D is TrackPiece
		   || forNode3D is TrackAssembly
		   || (forNode3D is Marker3D marker && marker.GetParent() is TrackPiece);

	public override void _Redraw(EditorNode3DGizmo gizmo)
	{
		gizmo.Clear();

		switch (gizmo.GetNode3D())
		{
			// An assembly shows its whole frontier: every open seam gets its bar and a "+" handle.
			case TrackAssembly assembly:
				DrawAssembly(gizmo, assembly);
				break;

			// A piece sitting in an assembly is an instance being chained, not a shape being
			// authored: its route draws so the joint can be read, but the editing handles belong
			// to the piece's own scene — what it gets here are the "+" handles on its open seams.
			case TrackPiece piece when piece.GetParent() is TrackAssembly assembly:
				DrawRoute(gizmo, piece, Transform3D.Identity);
				DrawExtendHandles(gizmo, OpenSeamsOf(assembly, piece),
								  piece.GlobalTransform.AffineInverse());
				break;

			case TrackPiece piece:
				DrawPiece(gizmo, piece);
				break;

			// A selected marker gets its own handles as well as the route drawn around it. Clicking
			// the thing you mean to move is the instinct worth serving: putting the handles only on
			// the piece meant selecting Exit gave you its bare Marker3D gizmo and nothing else,
			// which reads as the tool not being there at all.
			case Marker3D marker when marker.GetParent() is TrackPiece piece:
				DrawRoute(gizmo, piece, marker.Transform.AffineInverse());
				DrawMarker(gizmo, piece, marker);
				break;
		}
	}

	/// <summary>
	/// The frontier, drawn on the assembly itself: a bar at every open seam and a "+" handle off
	/// each one's arrow tip. Selecting the assembly is how you see everywhere the track can grow
	/// at once.
	/// </summary>
	private void DrawAssembly(EditorNode3DGizmo gizmo, TrackAssembly assembly)
	{
		Transform3D into = assembly.GlobalTransform.AffineInverse();

		var seams = new List<Marker3D>();
		foreach ((TrackPiece _, Marker3D seam) in assembly.OpenSeams())
		{
			seams.Add(seam);
			DrawSeam(gizmo, into * seam.GlobalTransform, GetMaterial(ExitMaterial, gizmo),
					 HalfWidthOf(seam));
		}

		// An empty assembly has no seams and therefore nowhere to click, which would make the
		// first piece the one piece the tool cannot place. One handle at the origin instead: click
		// it and the armed piece starts the track there.
		if (seams.Count == 0 && !HasPieces(assembly))
		{
			DrawSeam(gizmo, Transform3D.Identity, GetMaterial(EntryMaterial, gizmo));

			Vector3 tip = Vector3.Forward * ArrowLength;
			Vector3 handle = tip + Vector3.Up * ExtendLift;

			var lines = new List<Vector3>();
			AddExtendCross(lines, handle, Basis.Identity, tip);

			gizmo.AddLines(lines.ToArray(), GetMaterial(RouteMaterial, gizmo));
			gizmo.AddHandles(new[] { handle },
							 GetMaterial(InsertHandleMaterial, gizmo), new[] { 0 }, secondary: true);
			return;
		}

		DrawExtendHandles(gizmo, seams, into);
	}

	private static bool HasPieces(TrackAssembly assembly)
	{
		foreach (TrackPiece _ in assembly.Pieces)
			return true;

		return false;
	}

	/// <summary>How far an extend handle floats above the seam's arrow tip. Clear of the deck —
	/// and of the ghost preview parked on the same spot — with a stalk drawn down to the road so
	/// the dot reads as belonging to the seam.</summary>
	private const float ExtendLift = 7.0f;

	/// <summary>
	/// A clickable "+" per open seam, floating over the seam's arrow tip — click one and the
	/// armed palette piece is built there. Secondary handles, like the waypoint inserts, because
	/// a click that creates something is a commit, not a drag.
	/// </summary>
	private void DrawExtendHandles(EditorNode3DGizmo gizmo, List<Marker3D> seams, Transform3D into)
	{
		if (seams.Count == 0)
			return;

		var handles = new Vector3[seams.Count];
		var ids = new int[seams.Count];
		var lines = new List<Vector3>();

		for (var i = 0; i < seams.Count; i++)
		{
			Transform3D at = into * seams[i].GlobalTransform;
			Vector3 tip = at.Origin + at.Basis * Vector3.Forward * ArrowLength;

			handles[i] = tip + at.Basis * Vector3.Up * ExtendLift;
			ids[i] = i;

			AddExtendCross(lines, handles[i], at.Basis, tip);
		}

		gizmo.AddLines(lines.ToArray(), GetMaterial(RouteMaterial, gizmo));
		gizmo.AddHandles(handles, GetMaterial(InsertHandleMaterial, gizmo), ids, secondary: true);
	}

	/// <summary>Half-length of each arm of the "+" drawn at an extend point.</summary>
	private const float CrossArm = 3.5f;

	/// <summary>
	/// The visible "+": a stalk up from the seam's arrow tip and a cross of lines around the
	/// clickable handle. Drawn out of lines rather than trusting the handle's own dot, because a
	/// handle renders as a textured point and a point that fails to draw fails silently — the
	/// lines are the signpost, the handle underneath them is the button.
	/// </summary>
	private static void AddExtendCross(List<Vector3> lines, Vector3 at, Basis basis, Vector3 stalkFoot)
	{
		lines.Add(stalkFoot);
		lines.Add(at);

		Vector3 across = basis * Vector3.Right * CrossArm;
		Vector3 along = basis * Vector3.Forward * CrossArm;
		Vector3 up = basis * Vector3.Up * CrossArm;

		lines.Add(at - across);
		lines.Add(at + across);
		lines.Add(at - along);
		lines.Add(at + along);
		lines.Add(at - up);
		lines.Add(at + up);
	}

	/// <summary>The open seams belonging to one piece, in the assembly's frontier order.</summary>
	private static List<Marker3D> OpenSeamsOf(TrackAssembly assembly, TrackPiece piece)
	{
		var mine = new List<Marker3D>();

		foreach ((TrackPiece owner, Marker3D seam) in assembly.OpenSeams())
		{
			if (owner == piece)
				mine.Add(seam);
		}

		return mine;
	}

	/// <summary>
	/// The handles for a single selected marker, drawn in its own space.
	///
	/// The ids carry no node index, because the gizmo is already attached to the node they belong
	/// to — which is what <see cref="Resolve"/> reads them back through.
	/// </summary>
	private void DrawMarker(EditorNode3DGizmo gizmo, TrackPiece piece, Marker3D marker)
	{
		Basis toLocal = marker.Transform.Basis.Inverse();

		var handles = new List<Vector3>
		{
			Vector3.Zero,
			// Straight up in the piece's space, not the marker's: the height handle has to stay
			// vertical however the marker has been aimed or banked.
			toLocal * (Vector3.Up * HeightLift),
			Vector3.Forward * ArrowLength,
		};

		var ids = new List<int> { KindMove, KindHeight, KindAim };

		float half = HalfWidthOf(marker);
		handles.Add(Vector3.Left * half + Vector3.Up * BarUpright);
		ids.Add(KindBankLeft);

		handles.Add(Vector3.Right * half + Vector3.Up * BarUpright);
		ids.Add(KindBankRight);

		gizmo.AddHandles(handles.ToArray(), GetMaterial(HandleMaterial, gizmo), ids.ToArray());
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

			// Seams included: a banked seam is a frame the chain now carries whole, so the handle
			// that banks one is a legal move like any other.
			float half = HalfWidthOf(node);
			handles.Add(at.Origin + at.Basis * (Vector3.Left * half + Vector3.Up * BarUpright));
			ids.Add(i * KindStride + KindBankLeft);

			handles.Add(at.Origin + at.Basis * (Vector3.Right * half + Vector3.Up * BarUpright));
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

			DrawSeam(gizmo, into * node.Transform, GetMaterial(material, gizmo), HalfWidthOf(node));
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

	/// <summary>A route node: a bar across the road at its declared width, an upright at each end
	/// so its bank is readable, and an arrow the way the racer travels through it.</summary>
	private static void DrawSeam(EditorNode3DGizmo gizmo, Transform3D at, Material material,
								 float halfWidth = SeamHalfWidth)
	{
		Vector3 origin = at.Origin;
		Vector3 across = at.Basis * Vector3.Right;
		Vector3 forward = at.Basis * Vector3.Forward;
		Vector3 up = at.Basis * Vector3.Up;

		Vector3 left = origin - across * halfWidth;
		Vector3 right = origin + across * halfWidth;
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
		{
			// The same slot means two things depending on what the gizmo is attached to: on a
			// piece being authored it inserts a waypoint, on an assembly (or a piece chained into
			// one) it builds the armed piece at an open seam.
			return gizmo.GetNode3D() is TrackAssembly
				   || (gizmo.GetNode3D() is TrackPiece piece && piece.GetParent() is TrackAssembly)
				? "Extend track"
				: "Insert waypoint";
		}

		return (handleId % KindStride) switch
		{
			KindMove => "Move",
			KindHeight => "Height",
			KindAim => "Aim",
			_ => "Bank",
		};
	}

	/// <summary>
	/// Which route node a handle belongs to, and which kind it is — the one place that knows a
	/// gizmo may be sitting on the piece (ids carry a node index) or on a single marker (they do
	/// not, because the node is the gizmo's own).
	/// </summary>
	private static bool Resolve(EditorNode3DGizmo gizmo, int handleId,
							   out TrackPiece? piece, out Marker3D? node, out int kind)
	{
		piece = null;
		node = null;
		kind = handleId % KindStride;

		switch (gizmo.GetNode3D())
		{
			case TrackPiece owner:
			{
				piece = owner;
				List<Marker3D> route = RouteOf(owner);
				int index = handleId / KindStride;

				if (index >= route.Count)
					return false;

				node = route[index];
				return true;
			}

			case Marker3D marker when marker.GetParent() is TrackPiece parent:
				piece = parent;
				node = marker;
				kind = handleId;
				return true;

			default:
				return false;
		}
	}

	public override Variant _GetHandleValue(EditorNode3DGizmo gizmo, int handleId, bool secondary)
	{
		if (secondary)
			return handleId;

		return Resolve(gizmo, handleId, out _, out Marker3D? node, out _) && node != null
			? node.Transform
			: default;
	}

	public override void _SetHandle(EditorNode3DGizmo gizmo, int handleId, bool secondary,
									Camera3D camera, Vector2 screenPos)
	{
		// Insert handles do nothing during a drag: the waypoint is created on the click's commit,
		// where it can be one clean undoable action instead of a node conjured mid-gesture.
		if (secondary)
			return;

		if (!Resolve(gizmo, handleId, out TrackPiece? piece, out Marker3D? node, out int kind)
			|| piece == null || node == null)
			return;

		Transform3D at = node.Transform;

		// The pick ray in the piece's own space, which is the space every route transform lives in.
		Transform3D inverse = piece.GlobalTransform.AffineInverse();
		Vector3 from = inverse * camera.ProjectRayOrigin(screenPos);
		Vector3 direction = (inverse.Basis * camera.ProjectRayNormal(screenPos)).Normalized();

		switch (kind)
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
				// the pick ray. Seams aim in pitch as well as yaw now — the chain carries the whole
				// frame, so a seam mid-climb is as legal as a waypoint mid-climb.
				Vector3 nearest = from + direction * Mathf.Max(0.0f, (at.Origin - from).Dot(direction));
				Vector3 aim = nearest - at.Origin;

				if (aim.LengthSquared() < 0.25f)
					return;

				aim = aim.Normalized();

				float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(aim.Y, -1.0f, 1.0f)),
										  Mathf.DegToRad(-MaxPitchDegrees),
										  Mathf.DegToRad(MaxPitchDegrees));
				float yaw = Mathf.Atan2(-aim.X, -aim.Z);
				float roll = TrackPiece.RollOf(at.Basis);

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
				if (kind == KindBankLeft)
					bank = Mathf.Wrap(bank + Mathf.Pi, -Mathf.Pi, Mathf.Pi);

				bank = Mathf.Clamp(bank, Mathf.DegToRad(-MaxBankDegrees),
								   Mathf.DegToRad(MaxBankDegrees));

				float pitch = Mathf.Asin(Mathf.Clamp(forward.Y, -1.0f, 1.0f));
				float yaw = Mathf.Atan2(-forward.X, -forward.Z);

				// The bank hinges on the edge NOT being dragged: lift the left bar-end and the
				// right one holds still, like tilting a plank resting on its far edge. A pure
				// roll pivots the section about the spine, which drops the inside of the road
				// exactly as far as it raises the outside — nobody banking a corner means that.
				// Keeping the hinge edge fixed through each incremental update means the whole
				// drag leaves it exactly where it started.
				float half = HalfWidthOf(node);
				Vector3 hingeLocal = kind == KindBankLeft
					? Vector3.Right * half
					: Vector3.Left * half;

				Vector3 hinge = at.Origin + at.Basis * hingeLocal;
				Basis banked = AimedBasis(yaw, pitch, bank);

				node.Transform = new Transform3D(banked, hinge - banked * hingeLocal);
				return;
			}
		}
	}

	public override void _CommitHandle(EditorNode3DGizmo gizmo, int handleId, bool secondary,
									   Variant restore, bool cancel)
	{
		if (secondary)
		{
			// Creation happens here rather than in _SetHandle so it lands as one undoable action —
			// whether it is a waypoint into a route or a whole piece onto the frontier.
			if (cancel)
				return;

			switch (gizmo.GetNode3D())
			{
				case TrackAssembly assembly:
				{
					List<(TrackPiece Piece, Marker3D Seam)> open = assembly.OpenSeams();

					// No open seams and no pieces is the bootstrap handle: the armed piece
					// starts the track at the assembly's own origin.
					if (open.Count == 0 && !HasPieces(assembly))
						Plugin?.ExtendTrack(assembly, null);
					else if (handleId >= 0 && handleId < open.Count)
						Plugin?.ExtendTrack(assembly, open[handleId].Seam);
					return;
				}

				case TrackPiece chained when chained.GetParent() is TrackAssembly assembly:
				{
					List<Marker3D> mine = OpenSeamsOf(assembly, chained);
					if (handleId >= 0 && handleId < mine.Count)
						Plugin?.ExtendTrack(assembly, mine[handleId]);
					return;
				}

				case TrackPiece owner:
					Plugin?.InsertWaypointAfter(owner, handleId);
					return;
			}

			return;
		}

		if (!Resolve(gizmo, handleId, out TrackPiece? piece, out Marker3D? node, out _)
			|| piece == null || node == null)
			return;

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

	/// <summary>Half the width a node's bar draws at: a connector's declared width, or the
	/// catalog road for a plain marker — waypoints and old-scene seams alike.</summary>
	private static float HalfWidthOf(Marker3D node)
		=> node is TrackConnector connector ? connector.Width * 0.5f : SeamHalfWidth;

	private static List<Marker3D> RouteOf(TrackPiece piece)
	{
		var route = new List<Marker3D>();
		foreach (Marker3D node in piece.Route())
			route.Add(node);
		return route;
	}
}
#endif
