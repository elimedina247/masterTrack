using System.Collections.Generic;
using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// A piece of track authored in the editor. You build the shape out of CSG nodes in the viewport,
/// press bake, and the piece carries a plain mesh and a collision shape from then on.
///
/// <b>This deliberately does not generate geometry.</b> It used to — a cross-section swept along the
/// spine, parameterised by width and bank angle — and that was the wrong tool: a parameterised
/// profile can only describe the shapes somebody thought of in advance, which ruled out a half-pipe,
/// an overhang, a tube, or a road with a launch ramp welded onto it. <see cref="CsgPolygon3D"/> in
/// path mode does the same sweep with an arbitrary polygon you draw, and CSG's booleans do the rest.
/// Godot already has both, and neither needed writing.
///
/// So what is left here is the part CSG has no idea about: <b>the contract</b>. A tile is not just a
/// shape, it is a shape that has to join the ones either side of it, report where it hands the track
/// on, and tell the tires what it is made of.
///
/// <code>
/// TrackPiece                 this — contract, surface, baking
/// ├── Entry (Marker3D)       where the racer arrives  \ the contract, and the whole of it
/// ├── Exit  (Marker3D)       where they leave         /
/// ├── Spine (Path3D)         optional — only if the shape is swept along a curve
/// ├── Build (CSGCombiner3D)  the shape. Authoring only; freed at runtime once baked
/// │   ├── Road   (CSGPolygon3D, path mode, path_node = Spine)
/// │   └── ...    unions and subtractions: ramps, holes, whatever
/// ├── BakedMesh               \ written by Bake, saved with the scene
/// └── BakedCollision          /
/// </code>
///
/// <b>The contract is two markers, and nothing about the shape.</b> Everything the chain needs is
/// the transform from <c>Entry</c> to <c>Exit</c>, so a piece may be built anywhere in its own
/// scene, out of anything, facing any way. There is no spine to keep at the origin and no axis the
/// geometry has to run down.
///
/// Almost every mismatch between neighbours is free, because the next piece is placed <i>at</i>
/// this one's exit: position, heading and height always agree by construction. The two that survive
/// that are roll and pitch at the seam, because <see cref="TrackAnchor"/> is a position and a yaw
/// with nowhere to record either — see <see cref="ExitRollDegrees"/>.
/// <see cref="_GetConfigurationWarnings"/> names them rather than forbidding the shape.
/// </summary>
[Tool]
[GlobalClass]
public partial class TrackPiece : StaticBody3D
{
	private const string SpineName = "Spine";
	private const string BuildName = "Build";
	private const string EntryName = "Entry";
	private const string ExitName = "Exit";
	private const string BakedMeshName = "BakedMesh";
	private const string BakedCollisionName = "BakedCollision";

	private string _surface = SurfaceGroups.Road;

	/// <summary>
	/// What the piece is made of, as far as the tires are concerned.
	///
	/// A <c>GroundRay</c> reads the <b>first</b> group on whatever body it hit and looks the name up
	/// in the vehicle's grip tables, so this is the whole of what makes a piece slippery. A tile of
	/// sheet ice is this set to <see cref="SurfaceGroups.Ice"/> and nothing else.
	/// </summary>
	[Export(PropertyHint.Enum, "Road,Dirt,Grass,Ice")]
	public string Surface
	{
		get => _surface;
		set { _surface = value; ApplySurfaceGroup(); }
	}

	/// <summary>
	/// Tick to bake the CSG under <c>Build</c> into a mesh and a collision shape. Unticks itself.
	///
	/// A button rather than something automatic, because baking is the moment the shape stops being
	/// cheap to change: CSG rebuilds itself as you drag things, and a bake writes a few thousand
	/// vertices into the scene file. Author freely, bake when you are happy.
	/// </summary>
	[Export]
	public bool Bake
	{
		get => false;
		set
		{
			if (value && Engine.IsEditorHint())
				RunBake();
		}
	}

	/// <summary>
	/// The curve the shape is swept along. <b>Generated — do not hand-edit it.</b>
	///
	/// It is rebuilt from <see cref="Entry"/>, the waypoints, and <see cref="Exit"/> every time one
	/// of them moves. That is what makes the seam trustworthy: while this was authored directly it
	/// could drift away from the markers that declare where the piece joins, and a road ending
	/// twelve metres from where the chain thought it did is a hole in the track that neither the
	/// mesh nor the marker looks wrong on its own.
	/// </summary>
	public Path3D? Spine => GetNodeOrNull<Path3D>(SpineName);

	/// <summary>
	/// The nodes the road is threaded through, between the two seams, in tree order.
	///
	/// <b>Add a Marker3D as a child and drag it.</b> Its position is where the road goes, the way it
	/// faces is the direction the road runs through it, and rolling it banks the road there. Reorder
	/// them in the scene tree to reorder the path.
	///
	/// This is the whole authoring model, and it replaces editing a Curve3D by its handles. A curve
	/// point has a position and two tangent handles you drag with a modifier held down, plus a tilt
	/// on a separate small handle; a node has a transform gizmo everybody already knows.
	/// </summary>
	public IEnumerable<Marker3D> Waypoints
	{
		get
		{
			foreach (Node child in GetChildren())
			{
				if (child is Marker3D marker
					&& marker.Name != EntryName
					&& marker.Name != ExitName)
					yield return marker;
			}
		}
	}

	/// <summary>
	/// Rebuild the spine from the seams and the waypoints.
	///
	/// Each node contributes its position, the direction it faces, and its roll. The handles are a
	/// third of the way to each neighbour along that facing, which is the standard construction for
	/// a smooth curve through a set of points with chosen tangents — so rotating a waypoint swings
	/// the road through it rather than kinking it.
	/// </summary>
	private void RebuildSpine()
	{
		Path3D? spine = Spine;
		Marker3D? entry = Entry;
		Marker3D? exit = Exit;

		if (spine == null || entry == null || exit == null)
			return;

		var nodes = new List<Marker3D> { entry };
		nodes.AddRange(Waypoints);
		nodes.Add(exit);

		// A piece with no waypoints but a curve that clearly did not come from two points was
		// authored by hand, before the route was made of nodes. Regenerating it would silently
		// straighten somebody's spiral the moment they opened the scene, which is the worst thing a
		// tool can do. Add a waypoint to it and it converts.
		if (nodes.Count == 2 && spine.Curve is { PointCount: > 2 })
			return;

		var curve = new Curve3D { UpVectorEnabled = true };

		for (var i = 0; i < nodes.Count; i++)
		{
			Transform3D at = nodes[i].Transform;
			Vector3 position = at.Origin;
			Vector3 forward = (at.Basis * Vector3.Forward).Normalized();

			// A third of the distance to the neighbour, which keeps the curve close to the straight
			// line between two points and stops it bulging when they are far apart.
			float back = i > 0 ? position.DistanceTo(nodes[i - 1].Transform.Origin) / 3.0f : 0.0f;
			float on = i < nodes.Count - 1
				? position.DistanceTo(nodes[i + 1].Transform.Origin) / 3.0f
				: 0.0f;

			curve.AddPoint(position, -forward * back, forward * on);
			curve.SetPointTilt(i, RollOf(at.Basis));
		}

		spine.Curve = curve;
	}

	/// <summary>
	/// How far a basis is rolled about its own forward axis, in radians — which is what a curve's
	/// tilt means, so banking a piece is rotating a waypoint.
	///
	/// Measured against the un-rolled frame: the up vector you would have if the same heading and
	/// pitch carried no roll at all.
	/// </summary>
	private static float RollOf(Basis basis)
	{
		Vector3 forward = (basis * Vector3.Forward).Normalized();
		Vector3 up = (basis * Vector3.Up).Normalized();

		Vector3 right = forward.Cross(Vector3.Up);

		// Pointing straight up or down: every roll looks the same and there is no reference to
		// measure against, so call it none rather than pick one arbitrarily.
		if (right.LengthSquared() < 1e-6f)
			return 0.0f;

		right = right.Normalized();
		return Mathf.Atan2(up.Dot(right), up.Dot(right.Cross(forward)));
	}

	/// <summary>
	/// Hold the two seams level.
	///
	/// <b>Flat by construction rather than by warning.</b> The chain carries a position and a yaw,
	/// so a seam with pitch or roll in it is a frame the next piece cannot be built against — and
	/// being told off about it after the fact is worse than it simply not being possible. Position
	/// and heading stay free; only the pitch and the roll are taken back out.
	/// </summary>
	private void LevelSeams()
	{
		foreach (Marker3D? seam in new[] { Entry, Exit })
		{
			if (seam == null)
				continue;

			Vector3 forward = seam.Transform.Basis * Vector3.Forward;
			var flat = new Vector3(forward.X, 0.0f, forward.Z);

			// Facing straight up or down: there is no heading left to keep, so leave it be rather
			// than snapping it to an arbitrary one.
			if (flat.LengthSquared() < 1e-6f)
				continue;

			var level = new Basis(Vector3.Up, Mathf.Atan2(-flat.X, -flat.Z));
			if (!seam.Transform.Basis.IsEqualApprox(level))
				seam.Transform = new Transform3D(level, seam.Transform.Origin);
		}
	}

	/// <summary>The CSG the shape is built out of, or null once it has been baked away.</summary>
	public CsgShape3D? Build => GetNodeOrNull<CsgShape3D>(BuildName);

	/// <summary>
	/// The first swept polygon under <c>Build</c> — the road's cross-section, as opposed to any
	/// wedges or holes unioned and subtracted around it.
	/// </summary>
	public CsgPolygon3D? RoadPolygon => FindPolygon(Build);

	private static CsgPolygon3D? FindPolygon(Node? from)
	{
		if (from == null)
			return null;

		if (from is CsgPolygon3D polygon)
			return polygon;

		foreach (Node child in from.GetChildren())
		{
			if (FindPolygon(child) is { } found)
				return found;
		}

		return null;
	}

	/// <summary>
	/// How wide the road is, in metres, measured across the cross-section. Zero if there is no
	/// swept polygon to measure.
	///
	/// <b>This wants to match <see cref="TileCatalog.TileSize"/>, and the reason is joints.</b> Two
	/// pieces butt together at a seam that carries a position and a heading but says nothing about
	/// width, so a 48 m road meeting a 54 m one leaves a three metre step down each side — at the
	/// exact place a car is crossing between them. Nothing stops a piece being any width; this is
	/// what makes the cost visible.
	/// </summary>
	public float RoadWidth
	{
		get
		{
			Vector2[]? polygon = RoadPolygon?.Polygon;
			if (polygon is not { Length: >= 2 })
				return 0.0f;

			float low = polygon[0].X;
			float high = polygon[0].X;

			foreach (Vector2 point in polygon)
			{
				low = Mathf.Min(low, point.X);
				high = Mathf.Max(high, point.X);
			}

			return high - low;
		}
	}

	/// <summary>
	/// Tick to scale the cross-section across its width until it measures exactly
	/// <see cref="TileCatalog.TileSize"/>, keeping it centred on the spine. Unticks itself.
	///
	/// Scaled rather than clamped, so the shape you drew survives — a half-pipe stays a half-pipe,
	/// it is just the right width afterwards. Only the X axis is touched: the heights are what make
	/// it the shape it is, and stretching those to fix a width would be a different tile.
	/// </summary>
	[Export]
	public bool SnapToRoadWidth
	{
		get => false;
		set
		{
			if (value && Engine.IsEditorHint())
				ScaleToCatalogWidth();
		}
	}

	private void ScaleToCatalogWidth()
	{
		CsgPolygon3D? road = RoadPolygon;
		float width = RoadWidth;

		if (road == null || width <= 0.01f)
		{
			GD.PushWarning($"[TrackPiece] {Name} has no CSGPolygon3D under {BuildName} to measure.");
			return;
		}

		float scale = TileCatalog.TileSize / width;
		if (Mathf.IsEqualApprox(scale, 1.0f))
			return;

		Vector2[] polygon = road.Polygon;

		// About the section's own middle rather than about zero, so a cross-section drawn off to one
		// side keeps its offset instead of being dragged onto the spine.
		var centre = 0.0f;
		foreach (Vector2 point in polygon)
			centre += point.X;
		centre /= polygon.Length;

		for (var i = 0; i < polygon.Length; i++)
			polygon[i] = polygon[i] with { X = centre + (polygon[i].X - centre) * scale };

		road.Polygon = polygon;

		GD.Print($"[TrackPiece] {Name}: road width {width:0.##} m -> {TileCatalog.TileSize:0.##} m.");
	}

	/// <summary>Whether this piece has geometry that does not need CSG to exist.</summary>
	public bool IsBaked => GetNodeOrNull<MeshInstance3D>(BakedMeshName) != null;

	private Curve3D? Curve
	{
		get
		{
			Curve3D? curve = Spine?.Curve;
			return curve is { PointCount: >= 2 } ? curve : null;
		}
	}

	/// <summary>How far the piece runs along its own spine, in metres. Zero when it has no spine —
	/// a piece built entirely out of CSG boxes is perfectly legal.</summary>
	public float RunLength => Curve?.GetBakedLength() ?? 0.0f;

	/// <summary>
	/// Where the racer comes in, and where they leave. Two markers you place.
	///
	/// <b>The contract is the transform between these two and nothing else.</b> That is what frees
	/// the shape: there is no requirement that the geometry start at the origin, run down any
	/// particular axis, or have a spine at all. Build the piece however it wants to be built, then
	/// say where it is entered and where it is left.
	/// </summary>
	public Marker3D? Entry => GetNodeOrNull<Marker3D>(EntryName);

	public Marker3D? Exit => GetNodeOrNull<Marker3D>(ExitName);

	/// <summary>
	/// Where the racer leaves the piece, <i>relative to where they entered it</i> — what the chain
	/// folds onto the head.
	///
	/// Relative, which is the whole point. The old version read the spine's last point in the
	/// piece's own space, so the entry had to be pinned at the origin heading down -Z for the
	/// arithmetic to mean anything. Measuring exit-against-entry instead means the piece can sit
	/// anywhere in its own scene and still report the one thing the chain needs.
	/// </summary>
	public TrackAnchor ExitAnchor
	{
		get
		{
			Transform3D seam = SeamTransform;
			return new TrackAnchor(seam.Origin, YawOf(-seam.Basis.Z));
		}
	}

	/// <summary>
	/// The exit expressed in the entry's frame. Everything about how this piece joins its
	/// neighbours is in here, including the parts <see cref="TrackAnchor"/> cannot carry — see
	/// <see cref="ExitRollDegrees"/>.
	/// </summary>
	public Transform3D SeamTransform
	{
		get
		{
			Marker3D? entry = Entry;
			Marker3D? exit = Exit;

			if (entry == null || exit == null)
				return Transform3D.Identity;

			return entry.Transform.AffineInverse() * exit.Transform;
		}
	}

	/// <summary>Metres the piece climbs from entry to exit. Negative drops.</summary>
	public float HeightChange => SeamTransform.Origin.Y;

	/// <summary>
	/// How far the exit is rolled relative to the entry, in degrees.
	///
	/// <b>The one mismatch the chain genuinely cannot absorb.</b> Position, heading and height are
	/// free — the next piece is placed <i>at</i> this seam, so those always agree by construction.
	/// Roll does not: <see cref="TrackAnchor"/> is a position and a yaw, so a piece that leaves
	/// banked hands its neighbour a frame with nowhere to record the bank, and the neighbour is
	/// built as though it were flat. Until there is a transition piece to twist between them, this
	/// wants to be zero at both ends.
	/// </summary>
	public float ExitRollDegrees
	{
		get
		{
			Vector3 up = SeamTransform.Basis * Vector3.Up;
			Vector3 forward = SeamTransform.Basis * Vector3.Forward;

			// Roll measured about the direction of travel: how far the piece's up has been turned
			// out of the vertical plane that contains it.
			Vector3 flatRight = forward.Cross(Vector3.Up);
			if (flatRight.LengthSquared() < 1e-6f)
				return 0.0f;

			return Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(
				up.Normalized().Dot(flatRight.Normalized()), -1.0f, 1.0f)));
		}
	}

	/// <summary>
	/// The heading a direction reads as, in the convention <see cref="TrackAnchor"/> uses: yaw 0 runs
	/// down local -Z, and turning right takes it negative.
	/// </summary>
	private static float YawOf(Vector3 direction)
		=> Mathf.Atan2(-direction.X, -direction.Z);

	public override void _Ready()
	{
		if (Engine.IsEditorHint())
		{
			EnsureAnchors();
			LevelSeams();
			SetProcess(true);
		}
		else if (IsBaked)
		{
			// CSG is an authoring tool. A baked piece has everything it needs, and leaving the
			// combiner in the tree would have the engine rebuilding the same shape on load for every
			// tile on the track, plus a second set of collision on top of the baked one.
			Build?.QueueFree();
		}

		// Rebuilt on load as well as on edit, in the game as much as in the editor. The spine is
		// derived from the route, so generating it in only one of those would mean the scene file
		// had to carry a copy — and a saved copy of derived data is a copy that can go stale.
		if (!IsBaked)
			RebuildSpine();

		ApplySurfaceGroup();
	}

	/// <summary>
	/// Keep the configuration warnings honest while the spine is being dragged.
	///
	/// Polled rather than driven by <see cref="Resource.Changed"/>, which only ever covered the
	/// curve's own points — it said nothing about the Spine node being moved, and the connection was
	/// silently lost on a script reload, an undo, or a resource swap. Each of those reads to an
	/// author as the tool having quietly stopped working.
	/// </summary>
	public override void _Process(double delta)
	{
		if (!Engine.IsEditorHint())
			return;

		int shape = SeamFingerprint();
		if (shape == _seam)
			return;

		_seam = shape;

		// Order matters: the seams are levelled first so the spine is generated through frames that
		// already satisfy the contract, rather than being built and then contradicted.
		LevelSeams();
		RebuildSpine();
		UpdateConfigurationWarnings();
	}

	private int _seam;

	/// <summary>
	/// Everything the spine is generated from, so that moving any node in the piece rebuilds the
	/// road through it.
	///
	/// The waypoints are hashed in tree order, which is also the order they are threaded in — so
	/// dragging one up or down the scene tree reroutes the piece and this notices.
	/// </summary>
	private int SeamFingerprint()
	{
		var hash = new System.HashCode();

		hash.Add(Build != null);
		hash.Add(IsBaked);

		foreach (Marker3D node in Route())
		{
			Transform3D at = node.Transform;
			hash.Add(at.Origin);
			hash.Add(at.Basis.X);
			hash.Add(at.Basis.Y);
			hash.Add(at.Basis.Z);
		}

		return hash.ToHashCode();
	}

	/// <summary>Entry, then the waypoints in tree order, then Exit — the road's whole route.</summary>
	private IEnumerable<Marker3D> Route()
	{
		if (Entry is { } entry)
			yield return entry;

		foreach (Marker3D waypoint in Waypoints)
			yield return waypoint;

		if (Exit is { } exit)
			yield return exit;
	}

	/// <summary>
	/// Give a new piece the two markers that are its contract, so that dropping a TrackPiece into a
	/// scene gives you something to drag rather than an error.
	///
	/// No spine is created. A spine is only useful to a piece whose shape is swept along one, and
	/// plenty are not — a jump is a wedge and a box, and inventing a curve for it would be inventing
	/// a requirement.
	/// </summary>
	private void EnsureAnchors()
	{
		if (Entry == null)
		{
			var entry = new Marker3D { Name = EntryName };
			AddChild(entry);
			Adopt(entry);
		}

		// A tile-length straight ahead: the commonest piece there is, and an obvious thing to drag
		// somewhere else.
		if (Exit == null)
		{
			var exit = new Marker3D
			{
				Name = ExitName,
				Position = new Vector3(0.0f, 0.0f, -TileCatalog.ShortRun),
			};
			AddChild(exit);
			Adopt(exit);
		}

		// The spine is generated rather than authored, but it still has to exist as a node for the
		// CSG polygon's path_node to point at.
		if (Spine != null)
			return;

		var spine = new Path3D { Name = SpineName, Curve = new Curve3D { UpVectorEnabled = true } };
		AddChild(spine);
		Adopt(spine);
	}

	/// <summary>
	/// Put the piece in exactly one surface group, and make sure it is the only one — a
	/// <c>GroundRay</c> reads the first group it finds, so a body left in two grips according to
	/// whichever comes back first.
	/// </summary>
	private void ApplySurfaceGroup()
	{
		foreach (string group in new[]
				 { SurfaceGroups.Road, SurfaceGroups.Dirt, SurfaceGroups.Grass, SurfaceGroups.Ice })
		{
			if (IsInGroup(group))
				RemoveFromGroup(group);
		}

		if (SurfaceGroups.IsKnown(Surface))
			AddToGroup(Surface);
	}

	// ---- Baking ----

	/// <summary>
	/// Turn the CSG under <c>Build</c> into a mesh and a collision shape saved with the scene.
	///
	/// A frame is awaited first because CSG updates are deferred by one — baking without it hands
	/// back whatever the shape was before the last edit, which is the sort of bug that looks like
	/// the bake button working intermittently.
	///
	/// The results are given an <see cref="Node.Owner"/>, unlike everything the old generator built:
	/// they are the point of the exercise and have to be written into the file. The CSG stays too,
	/// so the shape remains editable and can be baked again.
	/// </summary>
	private async void RunBake()
	{
		CsgShape3D? build = Build;
		if (build == null)
		{
			GD.PushWarning($"[TrackPiece] {Name} has no {BuildName} node, so there is no CSG to bake. "
						   + "Add a CSGCombiner3D called Build and put the shape under it.");
			return;
		}

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		ArrayMesh mesh = build.BakeStaticMesh();
		if (mesh == null || mesh.GetSurfaceCount() == 0)
		{
			GD.PushWarning($"[TrackPiece] {Name} baked to an empty mesh. Check that {BuildName} is a "
						   + "CSG root with geometry under it.");
			return;
		}

		ConcavePolygonShape3D shape = build.BakeCollisionShape();

		Replace(BakedMeshName, new MeshInstance3D { Name = BakedMeshName, Mesh = mesh });

		if (shape != null && shape.GetFaces().Length > 0)
		{
			// A direct child of the body, never nested under a helper: a CollisionShape3D is only
			// picked up as an immediate child of the body it belongs to.
			Replace(BakedCollisionName,
					new CollisionShape3D { Name = BakedCollisionName, Shape = shape });
		}

		int vertices = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
		int faces = shape?.GetFaces().Length / 3 ?? 0;
		GD.Print($"[TrackPiece] Baked {Name}: {vertices} vertices across "
				 + $"{mesh.GetSurfaceCount()} surface(s), {faces} collision triangle(s).");
	}

	/// <summary>Swap a saved child for a freshly baked one, keeping the name free.</summary>
	private void Replace(string name, Node node)
	{
		if (GetNodeOrNull(name) is { } existing)
		{
			// Detached rather than only queued, so the name is available this instant — QueueFree
			// runs at the end of the frame and Godot would quietly rename the new node.
			RemoveChild(existing);
			existing.QueueFree();
		}

		AddChild(node);
		Adopt(node);
	}

	/// <summary>
	/// Make a node part of the saved scene rather than a runtime child. Without an owner the editor
	/// neither lists it in the tree nor writes it to the file, which is right for scratch geometry
	/// and wrong for a bake.
	/// </summary>
	private void Adopt(Node node)
		=> node.Owner = GetTree()?.EditedSceneRoot ?? Owner ?? this;

	// ---- The railings ----

	/// <summary>
	/// Everything here is a way the piece would fail to join its neighbours, checked against what
	/// <see cref="TrackAnchor"/> actually requires rather than against taste. Nothing here has an
	/// opinion about the shape.
	/// </summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();

		if (Entry == null || Exit == null)
		{
			warnings.Add($"A piece needs an {EntryName} and an {ExitName} Marker3D. They are the "
						 + "whole contract — where the racer arrives and where they leave.");
			return warnings.ToArray();
		}

		if (Build == null && !IsBaked)
			warnings.Add($"No {BuildName} node and nothing baked, so this piece has no shape yet.");

		// A seam carries a position and a heading and says nothing about width, so two pieces of
		// different widths meet with a step down each side — at the exact place a car crosses
		// between them. Tick SnapToRoadWidth to scale the section back without redrawing it.
		float width = RoadWidth;
		if (width > 0.01f && Mathf.Abs(width - TileCatalog.TileSize) > 0.5f)
		{
			warnings.Add($"The road is {width:0.##} m wide against the catalog's "
						 + $"{TileCatalog.TileSize:0.##} m, so it meets its neighbours with a "
						 + $"{Mathf.Abs(width - TileCatalog.TileSize) * 0.5f:0.##} m step down each "
						 + "side. Tick SnapToRoadWidth to scale the section to match.");
		}

		Transform3D seam = SeamTransform;

		if (seam.Origin.LengthSquared() < 1.0f)
		{
			warnings.Add($"{ExitName} is on top of {EntryName}. A piece of no length leaves the head "
						 + "where it already was, so the track would quietly stop growing.");
		}

		// Position, heading and height need no checking at all: the next piece is placed *at* this
		// seam, so those agree by construction. These two are the mismatches that survive that, and
		// they survive it because TrackAnchor is a position and a yaw with nowhere to put them.
		float roll = ExitRollDegrees;
		if (Mathf.Abs(roll) > 1.0f)
		{
			warnings.Add($"{ExitName} is rolled {roll:0.#} degrees relative to {EntryName}. The chain "
						 + "carries no roll, so the next piece will be built flat and the joint will "
						 + "be a step. Level the exit, or wait for transition pieces.");
		}

		Vector3 forward = (seam.Basis * Vector3.Forward).Normalized();
		if (Mathf.Abs(forward.Y) > Mathf.Sin(Mathf.DegToRad(1.0f)))
		{
			warnings.Add($"{ExitName} is pitched {Mathf.RadToDeg(Mathf.Asin(forward.Y)):0.#} degrees. "
						 + "A climb has to flatten out before the seam, or the joint with the next "
						 + "piece is a kerb.");
		}

		return warnings.ToArray();
	}
}
