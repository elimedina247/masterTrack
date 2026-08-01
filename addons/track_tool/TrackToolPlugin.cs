#if TOOLS
using Godot;
using MasterTrack.Tiles;
using MasterTrack.Tiles.Tool;

namespace MasterTrack.Editor;

/// <summary>
/// Editor plugin for authoring track pieces and assembling them into track.
///
/// It registers <see cref="TrackPieceGizmo"/> — the route drawing and the drag handles, which is
/// where most of the authoring happens — puts two buttons on the 3D toolbar (add a waypoint into
/// the route's longest segment, remove the last one), and docks <see cref="TrackPalette"/>, the
/// list of pieces you arm one from. <see cref="ExtendTrack"/> is the other half of that palette:
/// what a "+" handle on an open seam does with the armed piece. Everything is undoable.
///
/// Everything here is a shortcut for something you could do by hand in the scene tree. That is
/// deliberate: a piece is ordinary Godot nodes, and a tool that made them into something else
/// would be a tool you had to keep working.
/// </summary>
[Tool]
public partial class TrackToolPlugin : EditorPlugin
{
	private TrackPieceGizmo? _gizmo;
	private TrackPalette? _palette;
	private Button? _addWaypoint;
	private Button? _removeWaypoint;
	private Button? _smooth;
	private Button? _mirror;
	private Button? _extend;

	/// <summary>A ghost and the seam it is moored to. <see cref="MateByExit"/> says which of the
	/// ghost's own seams lands on it: entries mate ahead of an exit, exits mate behind an entry —
	/// the latter is how the authoring preview shows what arrives <i>into</i> a piece.</summary>
	private readonly record struct GhostMooring(TrackPiece Ghost, Marker3D? Seam, bool MateByExit);

	/// <summary>The translucent previews currently parked at seams. Rebuilt whenever the armed
	/// piece or the selection changes, re-moored every frame so they follow a seam being dragged;
	/// always freed eagerly.</summary>
	private readonly System.Collections.Generic.List<GhostMooring> _ghosts = new();

	/// <summary>Most seams a ghost is shown at. An assembly with a sprawling frontier gets its
	/// first few rather than a preview at every loose end — each ghost is a full piece instance,
	/// CSG and all, and selection changes should stay instant.</summary>
	private const int MaxGhosts = 4;

	public override void _EnterTree()
	{
		_gizmo = new TrackPieceGizmo { Plugin = this };
		AddNode3DGizmoPlugin(_gizmo);

		EditorInterface.Singleton.GetSelection().SelectionChanged += RefreshGhosts;

		_palette = new TrackPalette();
		_palette.ArmedChanged += RefreshGhosts;
		// The deprecated dock API on purpose. Subclassing 4.7's new EditorDock from C# crashed the
		// editor on exit — an access violation disposing a RefCounted after native teardown, flaky
		// but frequent, measured headless. The old call is a plain Control in a dock slot and has
		// years of miles on it; swap back when EditorDock has some.
#pragma warning disable CS0618
		AddControlToDock(DockSlot.RightBl, _palette);
#pragma warning restore CS0618

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

		_smooth = new Button
		{
			Text = "∿ Smooth",
			TooltipText = "Re-aim every waypoint of the selected piece along the line its "
						  + "neighbours make, so the road runs through them without wiggles. "
						  + "Positions and banking stay put; the seams are never touched. Undoable.",
			Visible = false,
		};
		_smooth.Pressed += OnSmoothPressed;
		AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _smooth);

		_mirror = new Button
		{
			Text = "⇄ Mirror",
			TooltipText = "Save a mirrored copy of the selected piece as a new scene — a right-hand "
						  + "corner authored once becomes both corners. Left/Right in the file name "
						  + "swaps automatically.",
			Visible = false,
		};
		_mirror.Pressed += OnMirrorPressed;
		AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _mirror);

		_extend = new Button
		{
			Text = "➕ Piece",
			TooltipText = "Build the piece armed in the Track Pieces dock onto the selected track's "
						  + "open seam — the same thing clicking a + in the viewport does. The new "
						  + "piece is selected afterwards, so clicking this repeatedly lays a run.",
			Visible = false,
		};
		_extend.Pressed += OnExtendPressed;
		AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _extend);
	}

	/// <summary>
	/// Everything created in <see cref="_EnterTree"/> is released here, <b>eagerly</b>: freed now
	/// rather than queue-freed, disposed now rather than left for the .NET runtime's shutdown
	/// sweep. Both halves matter, and both were learned the hard way. A QueueFree issued while the
	/// editor is exiting never runs — the frame it is queued for never comes — so the node leaks;
	/// and a C# wrapper for a refcounted object that survives to .NET shutdown is disposed after
	/// the engine has torn the native side down, which is an access violation on a good day and a
	/// heisenbug on the rest, because whether the freed memory is still mapped is heap luck.
	/// Measured headless: crash on exit, most runs, until this.
	/// </summary>
	public override void _ExitTree()
	{
		ClearGhosts();
		EditorInterface.Singleton.GetSelection().SelectionChanged -= RefreshGhosts;

		if (_gizmo != null)
		{
			RemoveNode3DGizmoPlugin(_gizmo);
			_gizmo.Dispose();
			_gizmo = null;
		}

		if (_palette != null)
		{
			_palette.ArmedChanged -= RefreshGhosts;
#pragma warning disable CS0618
			RemoveControlFromDocks(_palette);
#pragma warning restore CS0618
			_palette.Free();
			_palette = null;
		}

		RemoveButton(ref _addWaypoint);
		RemoveButton(ref _removeWaypoint);
		RemoveButton(ref _smooth);
		RemoveButton(ref _mirror);
		RemoveButton(ref _extend);
	}

	private void RemoveButton(ref Button? button)
	{
		if (button == null)
			return;

		RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, button);
		button.Free();
		button = null;
	}

	/// <summary>The buttons appear for a piece, any of its route markers, or an assembly — an
	/// author mid-drag on a marker should not have to re-select the root to add the next point,
	/// and an assembly selection is exactly when "build the armed piece" is the next move.</summary>
	public override bool _Handles(GodotObject @object)
		=> @object is TrackPiece
		   || @object is TrackAssembly
		   || (@object is Marker3D marker && marker.GetParent() is TrackPiece);

	public override void _MakeVisible(bool visible)
	{
		if (_addWaypoint != null)
			_addWaypoint.Visible = visible;

		if (_removeWaypoint != null)
			_removeWaypoint.Visible = visible;

		if (_smooth != null)
			_smooth.Visible = visible;

		if (_mirror != null)
			_mirror.Visible = visible;

		if (_extend != null)
			_extend.Visible = visible;
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

	private void OnSmoothPressed()
	{
		if (SelectedPiece() is { } piece)
			SmoothRoute(piece);
	}

	/// <summary>
	/// Aim every waypoint along the chord its neighbours make, as one undoable action — the
	/// viewport's answer to a road that wiggles.
	///
	/// A waypoint aimed even a few degrees off the line through its neighbours forces the curve to
	/// swing out and back to obey it, and the swing reads as a wiggle in the road and rides as a
	/// bump — the piece's <c>Smoothing</c> knob eases exactly this, but silently, at spine-build
	/// time. This writes the same easing <i>into the markers</i>, taken to its limit: afterwards
	/// what you see on every arrow is what the road does, and a piece authored with Smoothing at 0
	/// (obey my aims exactly) gets aims worth obeying.
	///
	/// Only headings move. Positions stay where they were dragged, each waypoint's bank survives
	/// (measured about the new heading), and the seams are never touched — their aims are the
	/// piece's contract with its neighbours.
	/// </summary>
	private void SmoothRoute(TrackPiece piece)
	{
		var route = new System.Collections.Generic.List<Marker3D>();
		foreach (Marker3D node in piece.Route())
			route.Add(node);

		if (route.Count < 3)
		{
			GD.PushWarning($"[TrackTool] {piece.Name} has no waypoints between its seams — "
						   + "nothing to smooth. Add one with the ＋ Waypoint button first.");
			return;
		}

		var changes = new System.Collections.Generic.List<(Marker3D Node, Transform3D After,
														   Transform3D Before)>();

		for (var i = 1; i < route.Count - 1; i++)
		{
			Marker3D node = route[i];
			Transform3D at = node.Transform;

			Vector3 chord = route[i + 1].Transform.Origin - route[i - 1].Transform.Origin;
			if (chord.LengthSquared() < 1e-4f)
				continue;

			chord = chord.Normalized();

			float pitch = Mathf.Asin(Mathf.Clamp(chord.Y, -1.0f, 1.0f));
			float yaw = Mathf.Atan2(-chord.X, -chord.Z);
			float roll = TrackPiece.RollOf(at.Basis);

			Basis smoothed = Basis.FromEuler(new Vector3(pitch, yaw, 0.0f));
			if (!Mathf.IsZeroApprox(roll))
				smoothed = smoothed.Rotated((smoothed * Vector3.Forward).Normalized(), roll);

			if (at.Basis.IsEqualApprox(smoothed))
				continue;

			changes.Add((node, at with { Basis = smoothed }, at));
		}

		if (changes.Count == 0)
		{
			GD.Print($"[TrackTool] {piece.Name}'s waypoints already run along their neighbours — "
					 + "nothing to smooth.");
			return;
		}

		EditorUndoRedoManager undo = GetUndoRedo();
		undo.CreateAction($"Smooth route of {piece.Name}");

		foreach ((Marker3D node, Transform3D after, Transform3D before) in changes)
		{
			undo.AddDoProperty(node, Node3D.PropertyName.Transform, after);
			undo.AddUndoProperty(node, Node3D.PropertyName.Transform, before);
		}

		undo.CommitAction();

		GD.Print($"[TrackTool] {piece.Name}: re-aimed {changes.Count} waypoint(s) along the route. "
				 + "Positions, banks and seams untouched.");
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

	// ---- Extending an assembly ----

	/// <summary>
	/// The toolbar's route to <see cref="ExtendTrack"/>: build the armed piece onto whatever the
	/// selection makes the obvious seam. A selected piece extends from its own first open exit; a
	/// selected assembly extends from its frontier's first — or bootstraps at its origin when it
	/// is empty. Chains exactly like the viewport handles do, because the new piece is selected
	/// afterwards and its own open exit becomes the next press's target.
	/// </summary>
	private void OnExtendPressed()
	{
		foreach (Node node in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
		{
			switch (node)
			{
				case TrackPiece piece when piece.GetParent() is TrackAssembly assembly:
				{
					foreach ((TrackPiece owner, Marker3D seam) in assembly.OpenSeams())
					{
						if (owner != piece)
							continue;

						ExtendTrack(assembly, seam);
						return;
					}

					GD.PushWarning($"[TrackTool] {piece.Name} has no open seam to build from — "
								   + "every exit already has a piece on it.");
					return;
				}

				case TrackAssembly assembly:
				{
					var open = assembly.OpenSeams();
					if (open.Count > 0)
					{
						ExtendTrack(assembly, open[0].Seam);
						return;
					}

					var any = false;
					foreach (TrackPiece _ in assembly.Pieces)
					{
						any = true;
						break;
					}

					if (!any)
					{
						ExtendTrack(assembly, null);
						return;
					}

					GD.PushWarning("[TrackTool] The assembly has no open seams — the track is "
								   + "closed. Select the piece to build from, or delete one to "
								   + "reopen its joint.");
					return;
				}
			}
		}

		GD.PushWarning("[TrackTool] Select a TrackAssembly, or a piece inside one, to build the "
					   + "armed piece onto it.");
	}

	/// <summary>
	/// Build the armed palette piece onto an open seam: instance it, land its entry exactly on the
	/// seam, and add it to the assembly as one undoable action. What a "+" handle click does. A
	/// null seam is the empty assembly's bootstrap: the piece starts the track at the assembly's
	/// own origin.
	///
	/// The new piece is selected afterwards, which is what makes extending rapid: its own open
	/// seams grow "+" handles the moment it lands, so a run of track is arm once, then click the
	/// handle at the end of each piece as it appears.
	/// </summary>
	public void ExtendTrack(TrackAssembly assembly, Marker3D? seam)
	{
		string path = _palette?.ArmedScenePath ?? "";
		if (path.Length == 0)
		{
			GD.PushWarning("[TrackTool] Nothing armed. Pick a piece in the Track Pieces dock, "
						   + "then click the + handle again.");
			return;
		}

		if (ResourceLoader.Load<PackedScene>(path) is not { } scene
			|| scene.Instantiate() is not TrackPiece piece)
		{
			GD.PushWarning($"[TrackTool] {path} did not instance as a TrackPiece, so it cannot "
						   + "be built onto the track.");
			return;
		}

		if (piece.EntrySeam is not { } entry)
		{
			GD.PushWarning($"[TrackTool] {path} has no entry seam to attach by.");
			piece.QueueFree();
			return;
		}

		// Warnings rather than refusals, both of them: a mismatched joint is a visible step at a
		// place you chose, and the tool's job is to name the cost, not to know better.
		if (seam is TrackConnector at && entry is TrackConnector arriving)
		{
			if (!at.Mates(arriving))
			{
				GD.PushWarning($"[TrackTool] {piece.Name}'s entry is profile "
							   + $"'{TrackConnector.ProfileOf(arriving)}' against the seam's "
							   + $"'{TrackConnector.ProfileOf(at)}'. They will join, but the "
							   + "cross-sections do not match.");
			}
			else if (Mathf.Abs(at.Width - arriving.Width) > 0.5f)
			{
				GD.PushWarning($"[TrackTool] {piece.Name} arrives {arriving.Width:0.#} m wide at a "
							   + $"{at.Width:0.#} m seam — a "
							   + $"{Mathf.Abs(at.Width - arriving.Width) * 0.5f:0.#} m step down "
							   + "each side.");
			}
		}

		piece.Name = UniquePieceName(assembly, path.GetFile().GetBaseName());

		// The transform is set on the instance itself rather than through the undo action, so a
		// redo after an undo puts it back exactly where it was without re-deriving anything.
		Transform3D cursor = seam != null ? TrackSnap.CursorOf(seam)
										  : assembly.GlobalTransform.Orthonormalized();
		Transform3D placement = TrackSnap.PlacementFor(cursor, entry.Transform);
		piece.Transform = (assembly.GlobalTransform.AffineInverse() * placement).Orthonormalized();

		EditorUndoRedoManager undo = GetUndoRedo();
		undo.CreateAction($"Extend track with {piece.Name}");
		undo.AddDoMethod(assembly, Node.MethodName.AddChild, piece);
		undo.AddDoProperty(piece, Node.PropertyName.Owner,
						   assembly.Owner ?? (GodotObject)assembly);
		undo.AddDoReference(piece);
		undo.AddUndoMethod(assembly, Node.MethodName.RemoveChild, piece);
		undo.CommitAction();

		EditorSelection selection = EditorInterface.Singleton.GetSelection();
		selection.Clear();
		selection.AddNode(piece);

		// The seam just built onto is no longer open, and the assembly's own gizmo would keep
		// offering it until the next reselect without this.
		assembly.UpdateGizmos();

		WarnAboutOverlaps(assembly, piece, seam?.GetParent() as TrackPiece);
	}

	/// <summary>
	/// Say so when a freshly placed piece runs through track that is already in the assembly —
	/// the same footprint arithmetic the match grid refuses placements with, demoted to a
	/// warning, because an assembly is freeform on purpose: an over-under with real clearance is
	/// legal, and a deliberate crossing is the author's call to make.
	/// </summary>
	private static void WarnAboutOverlaps(TrackAssembly assembly, TrackPiece placed,
										  TrackPiece? attachedTo)
	{
		var placedBoxes = FootprintOf(placed);
		if (placedBoxes == null)
			return;

		foreach (TrackPiece other in assembly.Pieces)
		{
			// The piece it attached to legitimately shares the joint; everything else it touches
			// is a crossing.
			if (other == placed || other == attachedTo)
				continue;

			if (FootprintOf(other) is not { } otherBoxes)
				continue;

			foreach (TrackFootprint mine in placedBoxes)
			{
				foreach (TrackFootprint theirs in otherBoxes)
				{
					if (!mine.Overlaps(theirs))
						continue;

					GD.PushWarning($"[TrackTool] {placed.Name} runs through {other.Name} with "
								   + $"less than {TrackFootprint.VerticalClearance:0} m of "
								   + "clearance. The assembly allows it; a match's grid would "
								   + "refuse the same placement.");
					return;
				}
			}
		}
	}

	/// <summary>
	/// A piece's room, read from its catalog entry at its live transform — or null for a piece
	/// with no saved scene, which has no cached route to measure.
	/// </summary>
	private static System.Collections.Generic.List<TrackFootprint>? FootprintOf(TrackPiece piece)
	{
		if (piece.SceneFilePath.Length == 0
			|| PieceCatalog.AtPath(piece.SceneFilePath) is not { } entry)
			return null;

		Transform3D frame = piece.EntrySeam is { } seam
			? TrackSnap.CursorOf(seam)
			: piece.GlobalTransform.Orthonormalized();

		var boxes = new System.Collections.Generic.List<TrackFootprint>();
		foreach (TrackFootprint box in PlacedTile.PieceFootprint(frame, entry, TrackTile.Size))
			boxes.Add(box);

		return boxes;
	}

	/// <summary>The next free "Straight2", "Straight3", ... so a run of the same piece reads in
	/// the tree without renaming.</summary>
	private static string UniquePieceName(TrackAssembly assembly, string baseName)
	{
		if (!assembly.HasNode(baseName))
			return baseName;

		for (var n = 2; ; n++)
		{
			string name = $"{baseName}{n}";
			if (!assembly.HasNode(name))
				return name;
		}
	}

	// ---- Mirroring ----

	private void OnMirrorPressed()
	{
		if (SelectedPiece() is { } piece)
			SaveMirroredVariant(piece);
	}

	/// <summary>
	/// Write a mirrored copy of a piece as a new scene, so both hands of a corner are one
	/// authoring job.
	///
	/// <b>Mirrored at the inputs, not the output.</b> The tempting version — instance the piece
	/// with scale -1 across X — inverts every triangle's winding and flips the collision normals,
	/// which is a piece that renders inside-out and drives worse. Instead every local transform in
	/// the copy is conjugated by the mirror (<c>M·T·M</c>, which stays a proper rotation), the
	/// road's cross-section is flipped and re-wound, and the spine curve's points and tilts are
	/// mirrored — then the CSG rebuilds the mirrored road from mirrored inputs exactly as if it had
	/// been authored that way.
	/// </summary>
	private void SaveMirroredVariant(TrackPiece piece)
	{
		string source = piece.SceneFilePath;
		if (source.Length == 0)
		{
			GD.PushWarning("[TrackTool] Save the piece as a scene first — the mirrored copy is "
						   + "written next to its file.");
			return;
		}

		string target = MirroredPath(source);
		if (FileAccess.FileExists(target))
		{
			GD.PushWarning($"[TrackTool] {target} already exists. Delete or rename it first; "
						   + "mirroring never overwrites.");
			return;
		}

		if (ResourceLoader.Load<PackedScene>(source) is not { } sourceScene
			|| sourceScene.Instantiate() is not TrackPiece copy)
		{
			GD.PushWarning($"[TrackTool] {source} would not instance as a TrackPiece.");
			return;
		}

		MirrorSubtree(copy);

		// A baked mesh in the copy is the un-mirrored shape and would win over the mirrored CSG,
		// so it goes. The CSG shows the right shape immediately; bake again when it is final.
		var hadBake = false;
		foreach (string stale in new[] { "BakedMesh", "BakedCollision" })
		{
			if (copy.GetNodeOrNull(stale) is { } node)
			{
				hadBake = true;
				copy.RemoveChild(node);
				node.Free();
			}
		}

		copy.Name = target.GetFile().GetBaseName();
		SetOwnerDeep(copy, copy);

		var packed = new PackedScene();
		if (packed.Pack(copy) != Error.Ok
			|| ResourceSaver.Save(packed, target) != Error.Ok)
		{
			GD.PushWarning($"[TrackTool] Could not write {target}.");
			copy.Free();
			return;
		}

		copy.Free();
		EditorInterface.Singleton.GetResourceFilesystem().Scan();

		GD.Print($"[TrackTool] Mirrored {source.GetFile()} into {target.GetFile()}."
				 + (hadBake ? " The copy's bake was dropped — open it and bake again." : ""));
	}

	/// <summary>Left becomes Right and vice versa, whichever appears; a piece named neither gets
	/// "Mirrored" appended.</summary>
	private static string MirroredPath(string source)
	{
		string dir = source.GetBaseDir();
		string name = source.GetFile().GetBaseName();

		string mirrored = name.Contains("Left") ? name.Replace("Left", "Right")
						: name.Contains("Right") ? name.Replace("Right", "Left")
						: name + "Mirrored";

		return $"{dir}/{mirrored}.tscn";
	}

	/// <summary>
	/// Mirror every local transform below (not including) a root, plus the data that lives beside
	/// transforms: swept cross-sections and spine curves.
	///
	/// Conjugating each <i>local</i> transform mirrors every global one — the mirror matrices
	/// between parent and child cancel pairwise — so one recursive pass handles arbitrarily deep
	/// Build subtrees, props and all.
	/// </summary>
	private static void MirrorSubtree(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is Node3D spatial)
				spatial.Transform = Conjugated(spatial.Transform);

			switch (child)
			{
				case CsgPolygon3D polygon:
					Vector2[] section = polygon.Polygon;
					for (var i = 0; i < section.Length; i++)
						section[i] = section[i] with { X = -section[i].X };

					// Flipping X reverses the polygon's winding; reversing the order restores it,
					// so the sweep still sees a front-facing section.
					System.Array.Reverse(section);
					polygon.Polygon = section;
					break;

				case Path3D { Curve: { } curve }:
					for (var i = 0; i < curve.PointCount; i++)
					{
						curve.SetPointPosition(i, Flip(curve.GetPointPosition(i)));
						curve.SetPointIn(i, Flip(curve.GetPointIn(i)));
						curve.SetPointOut(i, Flip(curve.GetPointOut(i)));
						curve.SetPointTilt(i, -curve.GetPointTilt(i));
					}
					break;
			}

			MirrorSubtree(child);
		}
	}

	private static Vector3 Flip(Vector3 v) => v with { X = -v.X };

	/// <summary>
	/// A transform conjugated by the X mirror: position flipped across the YZ plane, basis
	/// <c>M·B·M</c> — still right-handed, so nothing downstream meets a negative determinant.
	/// Forward and up mirror; right is re-derived from them, which is what negating roll means.
	/// </summary>
	private static Transform3D Conjugated(Transform3D t)
	{
		Basis b = t.Basis;

		var basis = new Basis(
			new Vector3(b.X.X, -b.X.Y, -b.X.Z),
			new Vector3(-b.Y.X, b.Y.Y, b.Y.Z),
			new Vector3(-b.Z.X, b.Z.Y, b.Z.Z));

		return new Transform3D(basis, Flip(t.Origin));
	}

	/// <summary>Every node below a root owned by it, which is what lets <see cref="PackedScene.Pack"/>
	/// write the whole tree.</summary>
	private static void SetOwnerDeep(Node node, Node owner)
	{
		foreach (Node child in node.GetChildren())
		{
			child.Owner = owner;
			SetOwnerDeep(child, owner);
		}
	}

	// ---- Ghost previews ----

	/// <summary>
	/// Park a translucent copy of the armed piece on every open seam the selection is showing, so
	/// "what would this piece do here" is answered before the click instead of by undoing after it.
	///
	/// Rebuilt from scratch on every change rather than moved, because correctness is easier to
	/// see than motion: the previous ghosts are freed, and whatever the palette and selection say
	/// <i>now</i> is what gets built. Ghosts are unowned (never saved), locked (never clicked),
	/// collisionless, and marked with <see cref="TrackAssembly.GhostMeta"/> so the assembly's own
	/// arithmetic never counts them.
	/// </summary>
	private void RefreshGhosts()
	{
		ClearGhosts();

		string armed = _palette?.ArmedScenePath ?? "";

		// What the selection is looking at. An assembly shows the armed piece on its whole
		// frontier; a chained piece on its own open seams; a piece being authored in its own scene
		// shows a neighbour on every seam — the armed piece if one is armed, a plain Straight
		// otherwise, because "does my seam join a straight cleanly" is the authoring question.
		TrackAssembly? assembly = null;
		TrackPiece? authored = null;
		var seams = new System.Collections.Generic.List<(Marker3D Seam, bool MateByExit)>();

		foreach (Node node in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
		{
			switch (node)
			{
				case TrackAssembly selected:
					assembly = selected;
					foreach ((TrackPiece _, Marker3D seam) in selected.OpenSeams())
						seams.Add((seam, false));
					break;

				case TrackPiece piece when piece.GetParent() is TrackAssembly parent:
					assembly = parent;
					foreach ((TrackPiece owner, Marker3D seam) in parent.OpenSeams())
					{
						if (owner == piece)
							seams.Add((seam, false));
					}
					break;

				case TrackPiece piece when !piece.HasMeta(TrackAssembly.GhostMeta):
					authored = piece;
					break;

				// Authoring is mostly done with a seam or waypoint selected, not the piece root —
				// the neighbours should not vanish the moment a handle is grabbed.
				case Marker3D marker when marker.GetParent() is TrackPiece piece
										  && piece.GetParent() is not TrackAssembly
										  && !piece.HasMeta(TrackAssembly.GhostMeta):
					authored = piece;
					break;
			}
		}

		string path = armed;

		if (authored != null)
		{
			if (authored.EntrySeam is { } entry)
				seams.Add((entry, true));

			foreach (Marker3D exit in authored.ExitSeams)
				seams.Add((exit, false));

			if (path.Length == 0)
				path = $"{TrackPalette.PiecesFolder}/Straight.tscn";

			// A piece neighbouring itself is legal and even useful — a straight chains into a
			// straight — but the ghost is instanced from the *saved* file, so while the piece has
			// unsaved edits the preview would be the stale shape wearing the current seams.
		}

		if (path.Length == 0 || (assembly == null && authored == null))
			return;

		Node? ghostParent = (Node?)assembly ?? authored;

		if (!ResourceLoader.Exists(path)
			|| ResourceLoader.Load<PackedScene>(path) is not { } scene)
			return;

		// The empty assembly gets its bootstrap ghost at the origin, matching the gizmo's
		// bootstrap handle. It has no seam to moor to; a null seam means "the assembly itself".
		var bootstrap = false;
		if (assembly != null && seams.Count == 0)
		{
			var any = false;
			foreach (TrackPiece _ in assembly.Pieces)
			{
				any = true;
				break;
			}

			bootstrap = !any;
		}

		if (bootstrap)
			MoorGhost(scene, ghostParent!, null, false);

		for (var i = 0; i < seams.Count && i < MaxGhosts; i++)
			MoorGhost(scene, ghostParent!, seams[i].Seam, seams[i].MateByExit);
	}

	/// <summary>Instance one ghost and moor it to a seam (or to the parent's origin when the seam
	/// is null). The transform is set now and again every frame — see <see cref="_Process"/>.</summary>
	private void MoorGhost(PackedScene scene, Node parent, Marker3D? seam, bool mateByExit)
	{
		if (scene.Instantiate() is not TrackPiece ghost)
			return;

		ghost.Name = "__GhostPiece";
		ghost.SetMeta(TrackAssembly.GhostMeta, true);
		// The editor's lock, so a click lands on the "+" handle behind the ghost rather than
		// selecting a node that is not even in the scene tree.
		ghost.SetMeta("_edit_lock_", true);
		ghost.CollisionLayer = 0;
		ghost.CollisionMask = 0;

		parent.AddChild(ghost);
		Ghostify(ghost);

		var mooring = new GhostMooring(ghost, seam, mateByExit);
		_ghosts.Add(mooring);
		Reposition(mooring);
	}

	/// <summary>
	/// Re-moored every frame rather than only on selection changes, so a ghost follows the seam
	/// it belongs to while that seam is being dragged — which is exactly when an author is looking
	/// at the joint. A handful of transform writes per frame; the instancing already happened.
	/// </summary>
	public override void _Process(double delta)
	{
		foreach (GhostMooring mooring in _ghosts)
			Reposition(mooring);
	}

	private static void Reposition(GhostMooring mooring)
	{
		if (!GodotObject.IsInstanceValid(mooring.Ghost))
			return;

		Marker3D? ownSeam = mooring.MateByExit
			? FirstExitOf(mooring.Ghost)
			: mooring.Ghost.EntrySeam;

		if (ownSeam == null)
			return;

		Transform3D cursor =
			mooring.Seam != null && GodotObject.IsInstanceValid(mooring.Seam)
				? TrackSnap.CursorOf(mooring.Seam)
				: mooring.Ghost.GetParent() is Node3D parent
					? parent.GlobalTransform.Orthonormalized()
					: Transform3D.Identity;

		mooring.Ghost.GlobalTransform = TrackSnap.PlacementFor(cursor, ownSeam.Transform);
	}

	private static Marker3D? FirstExitOf(TrackPiece piece)
	{
		foreach (Marker3D seam in piece.ExitSeams)
			return seam;

		return null;
	}

	/// <summary>See-through, all the way down: every mesh under the ghost — baked or live CSG —
	/// rendered at a fraction of its opacity.</summary>
	private static void Ghostify(Node node)
	{
		if (node is GeometryInstance3D geometry)
			geometry.Transparency = 0.65f;

		foreach (Node child in node.GetChildren())
			Ghostify(child);
	}

	/// <summary>Freed immediately, not queue-freed — the ghosts have to be gone before the frontier
	/// is next computed, and at editor exit a queued free never runs at all.</summary>
	private void ClearGhosts()
	{
		foreach (GhostMooring mooring in _ghosts)
		{
			if (GodotObject.IsInstanceValid(mooring.Ghost))
				mooring.Ghost.Free();
		}

		_ghosts.Clear();
	}
}
#endif
