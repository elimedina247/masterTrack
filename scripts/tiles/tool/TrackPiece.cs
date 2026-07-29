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

	/// <summary>The authored curve, or null before it exists.</summary>
	public Path3D? Spine => GetNodeOrNull<Path3D>(SpineName);

	/// <summary>The CSG the shape is built out of, or null once it has been baked away.</summary>
	public CsgShape3D? Build => GetNodeOrNull<CsgShape3D>(BuildName);

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
			SetProcess(true);
		}
		else
		{
			// CSG is an authoring tool. A baked piece has everything it needs, and leaving the
			// combiner in the tree would have the engine rebuilding the same shape on load for every
			// tile on the track, plus a second set of collision on top of the baked one.
			if (IsBaked)
				Build?.QueueFree();
		}

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
		UpdateConfigurationWarnings();
	}

	private int _seam;

	private int SeamFingerprint()
	{
		var hash = new System.HashCode();

		Transform3D seam = SeamTransform;
		hash.Add(seam.Origin);
		hash.Add(seam.Basis.X);
		hash.Add(seam.Basis.Y);
		hash.Add(seam.Basis.Z);
		hash.Add(Build != null);
		hash.Add(IsBaked);

		return hash.ToHashCode();
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

		if (Exit != null)
			return;

		// A tile-length straight ahead: the commonest piece there is, and an obvious thing to drag
		// somewhere else.
		var exit = new Marker3D
		{
			Name = ExitName,
			Position = new Vector3(0.0f, 0.0f, -TileCatalog.ShortRun),
		};
		AddChild(exit);
		Adopt(exit);
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
