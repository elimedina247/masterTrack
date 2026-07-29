#if TOOLS
using Godot;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Editor;

/// <summary>
/// Editor plugin for authoring track pieces.
///
/// It does two things. It registers <see cref="TrackPieceGizmo"/>, which draws a piece's route in
/// the viewport — the seams, the waypoints, and which way the road runs through each. And it puts a
/// button on the 3D toolbar for adding a waypoint, which is otherwise "add a Marker3D as a child,
/// then remember to name it something that is not Entry or Exit".
///
/// Everything it offers is a shortcut for something you can do by hand. That is deliberate: a piece
/// is ordinary Godot nodes, and a tool that made them into something else would be a tool you had
/// to keep working.
/// </summary>
[Tool]
public partial class TrackToolPlugin : EditorPlugin
{
	private TrackPieceGizmo? _gizmo;
	private Button? _addWaypoint;

	public override void _EnterTree()
	{
		_gizmo = new TrackPieceGizmo();
		AddNode3DGizmoPlugin(_gizmo);

		_addWaypoint = new Button
		{
			Text = "＋ Waypoint",
			TooltipText = "Add a waypoint to the selected track piece, halfway along its route. "
						  + "Drag it to shape the road; rotate it to bank.",
		};
		_addWaypoint.Pressed += AddWaypoint;
		AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _addWaypoint);

		// Hidden until a piece is actually selected — a button that does nothing most of the time is
		// worse than no button.
		_addWaypoint.Visible = false;
	}

	public override void _ExitTree()
	{
		if (_gizmo != null)
		{
			RemoveNode3DGizmoPlugin(_gizmo);
			_gizmo = null;
		}

		if (_addWaypoint == null)
			return;

		RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _addWaypoint);
		_addWaypoint.QueueFree();
		_addWaypoint = null;
	}

	public override bool _Handles(GodotObject @object) => @object is TrackPiece;

	public override void _MakeVisible(bool visible)
	{
		if (_addWaypoint != null)
			_addWaypoint.Visible = visible;
	}

	/// <summary>
	/// Drop a new waypoint into the selected piece, midway between the last two points on its route
	/// and facing the way the road already runs there — so it starts out changing nothing, and every
	/// drag from then on is a change you meant.
	/// </summary>
	private void AddWaypoint()
	{
		if (EditorInterface.Singleton.GetSelection().GetSelectedNodes() is not { Count: > 0 } selection
			|| selection[0] is not TrackPiece piece)
			return;

		if (piece.Entry is not { } entry || piece.Exit is not { } exit)
			return;

		// Between the last waypoint and the exit, so repeated presses walk the route forward rather
		// than stacking every new point in the same place.
		Transform3D from = entry.Transform;
		foreach (Marker3D existing in piece.Waypoints)
			from = existing.Transform;

		var placed = new Marker3D
		{
			Name = "Point",
			Transform = from.InterpolateWith(exit.Transform, 0.5f),
		};

		var undo = GetUndoRedo();
		undo.CreateAction("Add track waypoint");
		undo.AddDoMethod(piece, Node.MethodName.AddChild, placed);
		undo.AddDoProperty(placed, Node.PropertyName.Owner, piece.Owner ?? piece);
		undo.AddDoReference(placed);
		undo.AddUndoMethod(piece, Node.MethodName.RemoveChild, placed);
		undo.CommitAction();
	}
}
#endif
