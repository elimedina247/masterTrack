#if TOOLS
using Godot;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Editor;

/// <summary>
/// Editor plugin for authoring track pieces.
///
/// It registers <see cref="TrackPieceGizmo"/> — the route drawing and the drag handles, which is
/// where most of the authoring now happens — and puts two buttons on the 3D toolbar: add a
/// waypoint into the route's longest segment, and remove the last one. Both are undoable, and both
/// work whether the piece itself or one of its markers is selected.
///
/// Everything here is a shortcut for something you could do by hand in the scene tree. That is
/// deliberate: a piece is ordinary Godot nodes, and a tool that made them into something else
/// would be a tool you had to keep working.
/// </summary>
[Tool]
public partial class TrackToolPlugin : EditorPlugin
{
	private TrackPieceGizmo? _gizmo;
	private Button? _addWaypoint;
	private Button? _removeWaypoint;

	public override void _EnterTree()
	{
		_gizmo = new TrackPieceGizmo { Plugin = this };
		AddNode3DGizmoPlugin(_gizmo);

		_addWaypoint = new Button
		{
			Text = "＋ Waypoint",
			TooltipText = "Add a waypoint into the longest stretch of the selected track piece. "
						  + "Drag its handles to shape the road; the bar-end handles bank it.",
			Visible = false,
		};
		_addWaypoint.Pressed += OnAddPressed;
		AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _addWaypoint);

		_removeWaypoint = new Button
		{
			Text = "－ Waypoint",
			TooltipText = "Remove the selected track piece's last waypoint.",
			Visible = false,
		};
		_removeWaypoint.Pressed += OnRemovePressed;
		AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _removeWaypoint);
	}

	public override void _ExitTree()
	{
		if (_gizmo != null)
		{
			RemoveNode3DGizmoPlugin(_gizmo);
			_gizmo = null;
		}

		RemoveButton(ref _addWaypoint);
		RemoveButton(ref _removeWaypoint);
	}

	private void RemoveButton(ref Button? button)
	{
		if (button == null)
			return;

		RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, button);
		button.QueueFree();
		button = null;
	}

	/// <summary>The buttons appear for a piece or any of its route markers — an author mid-drag on
	/// a marker should not have to re-select the root to add the next point.</summary>
	public override bool _Handles(GodotObject @object)
		=> @object is TrackPiece
		   || (@object is Marker3D marker && marker.GetParent() is TrackPiece);

	public override void _MakeVisible(bool visible)
	{
		if (_addWaypoint != null)
			_addWaypoint.Visible = visible;

		if (_removeWaypoint != null)
			_removeWaypoint.Visible = visible;
	}

	/// <summary>The piece the current selection belongs to, whichever of its nodes is selected.</summary>
	private static TrackPiece? SelectedPiece()
	{
		foreach (Node node in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
		{
			switch (node)
			{
				case TrackPiece piece:
					return piece;
				case Marker3D marker when marker.GetParent() is TrackPiece parent:
					return parent;
			}
		}

		return null;
	}

	/// <summary>
	/// Add a waypoint into the longest segment of the route — the stretch with the most room, and
	/// so the one a new point most plausibly belongs in.
	/// </summary>
	private void OnAddPressed()
	{
		if (SelectedPiece() is not { } piece)
			return;

		var best = -1;
		float bestLength = -1.0f;
		var route = new System.Collections.Generic.List<Marker3D>();

		foreach (Marker3D node in piece.Route())
			route.Add(node);

		for (var i = 0; i < route.Count - 1; i++)
		{
			float length = route[i].Transform.Origin.DistanceTo(route[i + 1].Transform.Origin);
			if (length <= bestLength)
				continue;

			bestLength = length;
			best = i;
		}

		if (best >= 0)
			InsertWaypointAfter(piece, best);
	}

	private void OnRemovePressed()
	{
		if (SelectedPiece() is { } piece)
			RemoveLastWaypoint(piece);
	}

	/// <summary>
	/// Insert a waypoint between two consecutive route nodes, halfway along and halfway turned, as
	/// one undoable action. Also what the gizmo's insert handles call.
	///
	/// Starting as the midpoint means the new point changes nothing about the road until it is
	/// dragged — every edit from there is one the author meant.
	/// </summary>
	public void InsertWaypointAfter(TrackPiece piece, int afterRouteIndex)
	{
		var route = new System.Collections.Generic.List<Marker3D>();
		foreach (Marker3D node in piece.Route())
			route.Add(node);

		if (afterRouteIndex < 0 || afterRouteIndex >= route.Count - 1)
			return;

		Marker3D before = route[afterRouteIndex];
		Marker3D after = route[afterRouteIndex + 1];

		var waypoint = new Marker3D
		{
			Name = UniqueWaypointName(piece),
			Transform = before.Transform.InterpolateWith(after.Transform, 0.5f),
		};

		// Tree order is threading order, so the node has to land right after the segment's start —
		// not at the end of the child list, where it would reroute the road through itself last.
		int childIndex = before.GetIndex() + 1;

		EditorUndoRedoManager undo = GetUndoRedo();
		undo.CreateAction($"Add waypoint to {piece.Name}");
		undo.AddDoMethod(piece, Node.MethodName.AddChild, waypoint);
		undo.AddDoMethod(piece, Node.MethodName.MoveChild, waypoint, childIndex);
		undo.AddDoProperty(waypoint, Node.PropertyName.Owner, piece.Owner ?? piece);
		undo.AddDoReference(waypoint);
		undo.AddUndoMethod(piece, Node.MethodName.RemoveChild, waypoint);
		undo.CommitAction();
	}

	/// <summary>Take the route's last waypoint back out, restorably — its child index is recorded
	/// so undo threads it back into the same place.</summary>
	private void RemoveLastWaypoint(TrackPiece piece)
	{
		Marker3D? last = null;
		foreach (Marker3D waypoint in piece.Waypoints)
			last = waypoint;

		if (last == null)
			return;

		int childIndex = last.GetIndex();

		EditorUndoRedoManager undo = GetUndoRedo();
		undo.CreateAction($"Remove waypoint from {piece.Name}");
		undo.AddDoMethod(piece, Node.MethodName.RemoveChild, last);
		undo.AddUndoMethod(piece, Node.MethodName.AddChild, last);
		undo.AddUndoMethod(piece, Node.MethodName.MoveChild, last, childIndex);
		undo.AddUndoProperty(last, Node.PropertyName.Owner, last.Owner ?? piece);
		undo.AddUndoReference(last);
		undo.CommitAction();
	}

	/// <summary>The next free "P1", "P2", ... so waypoints read in the tree without renaming.</summary>
	private static string UniqueWaypointName(TrackPiece piece)
	{
		for (var n = 1; ; n++)
		{
			string name = $"P{n}";
			if (!piece.HasNode(name))
				return name;
		}
	}
}
#endif
