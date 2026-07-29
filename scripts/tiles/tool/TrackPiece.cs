using System.Collections.Generic;
using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// A piece of track authored in the editor: a spine you shape in the viewport, a cross-section
/// swept along it, and whatever you park on top.
///
/// <b>The spine is the truth.</b> Where the piece hands the track on, how much room it takes up and
/// where its centre line runs are all read off the same curve, so they cannot disagree with each
/// other — which is the failure the hand-built tiles kept producing, most visibly as a centre line
/// that was struck down the middle of a straight, replaced by a bank seam on a corner and abandoned
/// altogether on a squiggle.
///
/// The geometry is <b>never saved</b>. A scene file holds the curve, the profile and your props; the
/// road is rebuilt from those on load, in the editor and in the game alike. So a piece costs a few
/// hundred bytes on disk rather than the tens of thousands a baked mesh would, and a change to the
/// profile is picked up by every piece using it instead of leaving a stale copy behind.
///
/// <b>What the chain requires of you.</b> The track is a fold down a list of <see cref="TrackAnchor"/>
/// — a position and a yaw, four floats, no pitch and no roll — so every piece has to hand its
/// neighbour a level frame. That is not a style rule, it is the reason the anchor can stay four
/// floats instead of a basis that drifts. The spine's two ends must therefore sit level and
/// un-banked, and <see cref="_GetConfigurationWarnings"/> says so when they do not, which is the
/// railing this whole tool exists to give you.
/// </summary>
[Tool]
[GlobalClass]
public partial class TrackPiece : StaticBody3D
{
	/// <summary>Marks the nodes this piece builds, so a rebuild can clear its own work without
	/// touching the spine or anything you authored beside it.</summary>
	private const string GeneratedMeta = "track_generated";

	private const string SpineName = "Spine";

	private TrackProfile? _profile;

	/// <summary>Whether <see cref="_profile"/>'s change signal is currently hooked up. Tracked rather
	/// than asked, because the callable a lambda-free handler produces is a new object each time and
	/// would never compare equal to the one that was connected.</summary>
	private bool _watchingProfile;

	/// <summary>
	/// The cross-section carried along the spine. Shared between pieces on purpose — the road is
	/// supposed to be the same width everywhere, and a profile per tile is a hundred chances for one
	/// of them to be 53 m wide.
	/// </summary>
	[Export]
	public TrackProfile? Profile
	{
		get => _profile;
		set
		{
			if (_profile != null && _watchingProfile)
			{
				_profile.Changed -= RequestRebuild;
				_watchingProfile = false;
			}

			_profile = value;

			// Watched so that editing the shared profile reshapes every piece using it at once,
			// which is the point of it being shared.
			if (_profile != null)
			{
				_profile.Changed += RequestRebuild;
				_watchingProfile = true;
			}

			RequestRebuild();
		}
	}

	private string _surface = SurfaceGroups.Road;

	/// <summary>
	/// What the piece is made of, as far as the tires are concerned.
	///
	/// A <c>GroundRay</c> reads the <b>first</b> group on whatever body it hit and looks the name up
	/// in the vehicle's grip tables, so this is the whole of what makes a piece slippery — an ice
	/// tile is this set to <see cref="SurfaceGroups.Ice"/> and nothing else, and a gravel trap is a
	/// piece of <see cref="SurfaceGroups.Dirt"/> beside one of road.
	/// </summary>
	[Export(PropertyHint.Enum, "Road,Dirt,Grass,Ice")]
	public string Surface
	{
		get => _surface;
		set { _surface = value; ApplySurfaceGroup(); }
	}

	private float _segmentLength = 8.0f;

	/// <summary>
	/// Metres of spine per ring of geometry. The quality knob, and a cheap one — see
	/// <see cref="TrackSweep.Frames"/>.
	/// </summary>
	[Export(PropertyHint.Range, "0.5,40,0.5")]
	public float SegmentLength
	{
		get => _segmentLength;
		set { _segmentLength = value; RequestRebuild(); }
	}

	private float _bankBlend = 0.25f;

	/// <summary>
	/// Fraction of the spine spent easing the profile's bank in at each end. See
	/// <see cref="BankScale"/> for why it cannot be zero on anything banked.
	/// </summary>
	[Export(PropertyHint.Range, "0,0.5,0.01")]
	public float BankBlend
	{
		get => _bankBlend;
		set { _bankBlend = value; RequestRebuild(); }
	}

	private bool _generateCollision = true;

	/// <summary>
	/// Whether the piece builds collision as well as a mesh. Off for a ghost — a preview of a
	/// placement that has not happened must never be something a car can hit.
	/// </summary>
	[Export]
	public bool GenerateCollision
	{
		get => _generateCollision;
		set { _generateCollision = value; ApplySurfaceGroup(); RequestRebuild(); }
	}

	/// <summary>Rebuild pending this frame, so a drag across the curve costs one sweep rather than
	/// one per mouse movement.</summary>
	private bool _rebuildQueued;

	// ---- The chain contract, read off the spine ----

	/// <summary>The authored curve, or null before it exists.</summary>
	public Path3D? Spine => GetNodeOrNull<Path3D>(SpineName);

	private Curve3D? Curve
	{
		get
		{
			Curve3D? curve = Spine?.Curve;
			return curve is { PointCount: >= 2 } ? curve : null;
		}
	}

	/// <summary>How far the piece runs along its own spine, in metres. What the catalog calls a
	/// tile's run length, measured rather than declared.</summary>
	public float RunLength => Curve?.GetBakedLength() ?? 0.0f;

	/// <summary>
	/// Where the racer leaves the piece, relative to where they entered it.
	///
	/// This is what the chain folds onto the head, and it is taken straight from the end of the
	/// curve. Nothing declares it separately, so there is no second number to fall out of step with
	/// the road you can see.
	/// </summary>
	public TrackAnchor ExitAnchor
	{
		get
		{
			Curve3D? curve = Curve;
			if (curve == null)
				return new TrackAnchor(Vector3.Zero, 0.0f);

			return new TrackAnchor(curve.GetPointPosition(curve.PointCount - 1),
								   YawOf(ExitTangent(curve)));
		}
	}

	/// <summary>
	/// The direction the spine heads as it leaves the origin, and as it arrives at its far end.
	///
	/// <b>Taken from the Bezier handles rather than by sampling the curve, and the difference is not
	/// academic.</b> A chord measured back over the last half metre of a 63 m corner points 0.227
	/// degrees away from the true tangent — half the angle that half metre sweeps — and that is an
	/// error the chain <i>accumulates</i>, because every piece is laid relative to the one before.
	/// A handle is the tangent, exactly, so there is nothing to accumulate.
	///
	/// A zero handle means the segment is straight, and then the neighbouring point is the honest
	/// answer.
	/// </summary>
	private static Vector3 ExitTangent(Curve3D curve)
	{
		int last = curve.PointCount - 1;
		Vector3 handle = -curve.GetPointIn(last);

		return handle.LengthSquared() > 1e-9f
			? handle
			: curve.GetPointPosition(last) - curve.GetPointPosition(last - 1);
	}

	private static Vector3 EntryTangent(Curve3D curve)
	{
		Vector3 handle = curve.GetPointOut(0);

		return handle.LengthSquared() > 1e-9f
			? handle
			: curve.GetPointPosition(1) - curve.GetPointPosition(0);
	}

	/// <summary>Metres the piece climbs from entry to exit. Negative drops.</summary>
	public float HeightChange => ExitAnchor.Position.Y;

	/// <summary>
	/// The heading a direction reads as, in the convention <see cref="TrackAnchor"/> uses: yaw 0
	/// runs down local -Z, and turning right takes it negative.
	/// </summary>
	private static float YawOf(Vector3 direction)
		=> Mathf.Atan2(-direction.X, -direction.Z);

	public override void _Ready()
	{
		if (Engine.IsEditorHint())
			EnsureSpine();

		if (Spine?.Curve != null)
			Spine.Curve.Changed += RequestRebuild;

		ApplySurfaceGroup();
		Rebuild();
	}

	/// <summary>
	/// Put the piece in exactly one surface group, and make sure it is the only one — a
	/// <c>GroundRay</c> reads the first group it finds, so a body left in two of them grips
	/// according to whichever happens to come back first.
	///
	/// A piece with no collision is a preview of a placement that has not happened. Nothing will
	/// ever raycast it, and putting it in a surface group would be describing the grip of something
	/// that is not there.
	/// </summary>
	private void ApplySurfaceGroup()
	{
		foreach (string group in new[]
				 { SurfaceGroups.Road, SurfaceGroups.Dirt, SurfaceGroups.Grass, SurfaceGroups.Ice })
		{
			if (IsInGroup(group))
				RemoveFromGroup(group);
		}

		if (GenerateCollision && SurfaceGroups.IsKnown(Surface))
			AddToGroup(Surface);
	}

	/// <summary>
	/// Give a new piece a spine to shape, running one tile-length straight ahead.
	///
	/// Created rather than demanded so that a piece is drivable the moment it is dropped into a
	/// scene: an empty <see cref="Path3D"/> and a configuration warning is a worse first five
	/// minutes than a plain straight you can start dragging.
	/// </summary>
	private void EnsureSpine()
	{
		if (Spine != null)
			return;

		var curve = new Curve3D { UpVectorEnabled = true };
		curve.AddPoint(Vector3.Zero);
		curve.AddPoint(new Vector3(0.0f, 0.0f, -TileCatalog.ShortRun));

		var spine = new Path3D { Name = SpineName, Curve = curve };
		AddChild(spine);

		// Owned by the scene's root rather than by this node, which is what puts it in the tree the
		// editor shows and saves it with the file. The generated geometry deliberately gets neither.
		spine.Owner = GetTree()?.EditedSceneRoot ?? Owner ?? this;
	}

	/// <summary>Coalesce a rebuild into the end of the frame. Dragging a curve point fires the
	/// change signal continuously and each sweep would otherwise be paid in full.</summary>
	private void RequestRebuild()
	{
		if (_rebuildQueued || !IsInsideTree())
			return;

		_rebuildQueued = true;
		CallDeferred(MethodName.Rebuild);
	}

	/// <summary>
	/// Throw the old geometry away and sweep it again.
	///
	/// Everything this builds is marked and un-owned: marked so the next rebuild can find it, and
	/// un-owned so the editor neither lists it in the scene tree nor writes it into the file. What
	/// you see in the tree is the spine and your own props, which is the whole of what a piece
	/// actually is.
	/// </summary>
	public void Rebuild()
	{
		_rebuildQueued = false;

		ClearGenerated();
		UpdateConfigurationWarnings();

		Curve3D? curve = Curve;
		TrackProfile? profile = Profile;
		if (curve == null || profile == null)
			return;

		// The tilt is what banks a corner, and it is read through the curve's up vectors. Left off,
		// every section stands straight up and the bank silently does nothing.
		if (!curve.UpVectorEnabled)
			curve.UpVectorEnabled = true;

		TrackSweep.Frame[] frames = TrackSweep.Frames(curve, SegmentLength);
		if (frames.Length < 2)
			return;

		Vector2[] section = profile.Section();
		if (section.Length < 2)
			return;

		Vector2[][] surfaces = BlendBank(section, frames.Length);

		var road = new List<Vector3>();
		var roadNormals = new List<Vector3>();
		TrackSweep.SweepClosed(frames, Solidify(surfaces, profile.Thickness), road, roadNormals);

		var walls = new List<Vector3>();
		var wallNormals = new List<Vector3>();
		BuildWalls(frames, surfaces, profile, walls, wallNormals);

		var paint = new List<Vector3>();
		var paintNormals = new List<Vector3>();
		BuildCentreLine(frames, surfaces, profile, paint, paintNormals);

		BuildMesh(profile, road, roadNormals, walls, wallNormals, paint, paintNormals);

		if (GenerateCollision)
			BuildCollision(road, walls);
	}

	private void ClearGenerated()
	{
		foreach (Node child in GetChildren())
		{
			if (!child.HasMeta(GeneratedMeta))
				continue;

			// Detached before being freed so the name is free again this instant — QueueFree runs at
			// the end of the frame, and the rebuild that follows would find its names taken and get
			// them silently renamed.
			RemoveChild(child);
			child.QueueFree();
		}
	}

	/// <summary>
	/// How much of the profile's bank is present, a fraction <paramref name="t"/> of the way along
	/// the spine: none at either end, all of it through the middle.
	///
	/// <b>Load-bearing, and carried over unchanged from the corners this replaces.</b> The piece
	/// either side of a bank is flat, so a bank at full height from the first ring would meet its
	/// neighbour as a step the height of the lip — sixteen metres, on a default corner. Easing it in
	/// means the piece joins the straight flat and level, which is the same requirement
	/// <see cref="_GetConfigurationWarnings"/> enforces on the spine's tilt.
	/// </summary>
	private float BankScale(float t)
	{
		if (BankBlend <= 0.0f)
			return 1.0f;

		if (t < BankBlend)
			return Mathf.SmoothStep(0.0f, 1.0f, t / BankBlend);

		if (t > 1.0f - BankBlend)
			return Mathf.SmoothStep(0.0f, 1.0f, (1.0f - t) / BankBlend);

		return 1.0f;
	}

	/// <summary>The section at every ring along the spine, with its bank eased in and out.</summary>
	private Vector2[][] BlendBank(Vector2[] section, int rings)
	{
		var sections = new Vector2[rings][];

		for (int i = 0; i < rings; i++)
		{
			float scale = BankScale(rings > 1 ? (float)i / (rings - 1) : 0.0f);

			var scaled = new Vector2[section.Length];
			for (int j = 0; j < section.Length; j++)
				scaled[j] = section[j] with { Y = section[j].Y * scale };

			sections[i] = scaled;
		}

		return sections;
	}

	/// <summary>Give every ring's surface a thickness, so the sweep describes a solid.</summary>
	private static Vector2[][] Solidify(Vector2[][] surfaces, float thickness)
	{
		var solid = new Vector2[surfaces.Length][];
		for (int i = 0; i < surfaces.Length; i++)
			solid[i] = TrackSweep.Solidify(surfaces[i], thickness);

		return solid;
	}

	/// <summary>
	/// Barriers standing on the road's two edges, each a small closed section swept the length of
	/// the piece.
	///
	/// Built from the surface's own end points at every ring rather than from the width, so a wall
	/// on a banked corner rides up the lip with the road instead of standing vertically through a
	/// surface that has tilted out from under it.
	/// </summary>
	private static void BuildWalls(TrackSweep.Frame[] frames, Vector2[][] surfaces,
								   TrackProfile profile, List<Vector3> vertices,
								   List<Vector3> normals)
	{
		if (profile.WallHeight <= 0.0f || profile.WallThickness <= 0.0f)
			return;

		float thickness = profile.WallThickness;
		float height = profile.WallHeight;

		if (profile.LeftWall)
		{
			var left = new Vector2[surfaces.Length][];
			for (int i = 0; i < surfaces.Length; i++)
			{
				Vector2 edge = surfaces[i][0];
				left[i] = TrackSweep.Solidify(new[]
				{
					new Vector2(edge.X, edge.Y + height),
					new Vector2(edge.X + thickness, edge.Y + height),
				}, height);
			}

			TrackSweep.SweepClosed(frames, left, vertices, normals);
		}

		if (!profile.RightWall)
			return;

		var right = new Vector2[surfaces.Length][];
		for (int i = 0; i < surfaces.Length; i++)
		{
			Vector2 edge = surfaces[i][^1];
			right[i] = TrackSweep.Solidify(new[]
			{
				new Vector2(edge.X - thickness, edge.Y + height),
				new Vector2(edge.X, edge.Y + height),
			}, height);
		}

		TrackSweep.SweepClosed(frames, right, vertices, normals);
	}

	/// <summary>
	/// The centre line, painted along the spine.
	///
	/// <b>This is the bug the rewrite was partly for.</b> A stripe used to be struck per tile from
	/// that tile's own idea of its middle, and the ideas disagreed: a straight painted x=0, a corner
	/// painted the flat/bank seam a third of the way in from the inside and had no centre line at
	/// all, the loop's aprons jumped theirs ten metres sideways and the squiggle abandoned it. Here
	/// the line is the spine, so every piece agrees with every other by construction.
	///
	/// Two centimetres proud and mesh only — a lip in the road would be a bump the suspension reads
	/// for no reason.
	/// </summary>
	private static void BuildCentreLine(TrackSweep.Frame[] frames, Vector2[][] surfaces,
										TrackProfile profile, List<Vector3> vertices,
										List<Vector3> normals)
	{
		if (profile.CentreLineWidth <= 0.0f)
			return;

		float half = profile.CentreLineWidth * 0.5f;

		// Read off the surface at each ring rather than assumed flat, so the stripe still lies on
		// the road where the bank has lifted the middle of it.
		var stripe = new Vector2[surfaces.Length][];
		for (int i = 0; i < surfaces.Length; i++)
		{
			stripe[i] = new[]
			{
				new Vector2(-half, HeightAt(surfaces[i], -half) + 0.02f),
				new Vector2(half, HeightAt(surfaces[i], half) + 0.02f),
			};
		}

		TrackSweep.SweepRibbon(frames, stripe, vertices, normals);
	}

	/// <summary>Height of the surface at a lateral offset, interpolated between the section's
	/// samples.</summary>
	private static float HeightAt(Vector2[] surface, float lateral)
	{
		if (surface.Length == 0)
			return 0.0f;

		if (lateral <= surface[0].X)
			return surface[0].Y;

		for (int i = 1; i < surface.Length; i++)
		{
			if (lateral > surface[i].X)
				continue;

			float span = surface[i].X - surface[i - 1].X;
			if (span <= 0.0f)
				return surface[i].Y;

			return Mathf.Lerp(surface[i - 1].Y, surface[i].Y, (lateral - surface[i - 1].X) / span);
		}

		return surface[^1].Y;
	}

	/// <summary>
	/// One mesh, three surfaces, three materials.
	///
	/// One, rather than the several hundred <see cref="MeshInstance3D"/>s a hand-built corner used to
	/// need. That is the bulk of what the sweep buys: a hairpin was some 288 boxes, each with its own
	/// node and its own <c>BoxMesh</c> resource, all of it constructed from C# a property at a time
	/// while a tile was being placed mid-race.
	/// </summary>
	private void BuildMesh(TrackProfile profile,
						   List<Vector3> road, List<Vector3> roadNormals,
						   List<Vector3> walls, List<Vector3> wallNormals,
						   List<Vector3> paint, List<Vector3> paintNormals)
	{
		var mesh = new ArrayMesh();

		AddSurface(mesh, road, roadNormals, Finish(profile.RoadColor));
		AddSurface(mesh, walls, wallNormals, Finish(profile.WallColor));
		AddSurface(mesh, paint, paintNormals, Finish(profile.LineColor));

		if (mesh.GetSurfaceCount() == 0)
			return;

		Adopt(new MeshInstance3D { Name = "Surface", Mesh = mesh });
	}

	private static void AddSurface(ArrayMesh mesh, List<Vector3> vertices, List<Vector3> normals,
								   Material material)
	{
		if (vertices.Count == 0)
			return;

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();

		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
	}

	/// <summary>
	/// The house style, unchanged from the hand-built tiles: per-vertex shading, no specular, and
	/// nothing set on a material but its colour.
	///
	/// It is Gouraud shading, which is what the arcade hardware this look comes from actually did.
	/// Specular is off and roughness pinned at 1 because a highlight sliding across a surface is the
	/// single most modern-looking thing a renderer does.
	/// </summary>
	private static StandardMaterial3D Finish(Color color) => new()
	{
		AlbedoColor = color,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
		SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
		Metallic = 0.0f,
		Roughness = 1.0f,
	};

	/// <summary>
	/// One collision shape for the whole piece, from the same triangles the mesh is drawn from.
	///
	/// A <see cref="ConcavePolygonShape3D"/> rather than the stack of boxes the hand-built tiles
	/// used. The swept sections are closed and capped, so the soup describes a solid rather than a
	/// sheet, and what keeps a car out of it is the physics step being too short to cross the slab:
	/// at the project's 120 Hz a car at <c>TopSpeed</c> moves 0.46 m against a 1.6 m thickness. See
	/// <see cref="TrackProfile.Thickness"/> before thinning that.
	///
	/// The paint is left out. It is two centimetres of decoration and giving it a surface to be the
	/// top of is how you get a lip in the middle of the road.
	/// </summary>
	private void BuildCollision(List<Vector3> road, List<Vector3> walls)
	{
		if (road.Count == 0 && walls.Count == 0)
			return;

		var faces = new Vector3[road.Count + walls.Count];
		road.CopyTo(faces, 0);
		walls.CopyTo(faces, road.Count);

		Adopt(new CollisionShape3D
		{
			Name = "Collision",
			// A direct child of the body, never nested under a helper: a CollisionShape3D is only
			// picked up as an immediate child of the body it belongs to.
			Shape = new ConcavePolygonShape3D { Data = faces },
		});
	}

	/// <summary>
	/// Take ownership of a node this piece built: mark it so the next rebuild can find it, and leave
	/// it un-owned so it is neither listed in the editor's tree nor written into the scene file.
	/// </summary>
	private void Adopt(Node node)
	{
		node.SetMeta(GeneratedMeta, true);
		AddChild(node);
	}

	/// <summary>
	/// The railings. Everything here is a way the piece would not join its neighbours, checked
	/// against what <see cref="TrackAnchor"/> actually requires rather than against taste.
	/// </summary>
	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();

		if (Profile == null)
			warnings.Add("No TrackProfile, so there is no cross-section to sweep. Assign one.");

		Curve3D? curve = Curve;
		if (curve == null)
		{
			warnings.Add($"The {SpineName} needs a Curve3D with at least two points.");
			return warnings.ToArray();
		}

		if (!curve.GetPointPosition(0).IsEqualApprox(Vector3.Zero))
		{
			warnings.Add("The spine must start at the piece's origin — that point is the seam the "
						 + "previous tile hands the racer over on.");
		}

		// The chain carries a position and a yaw and nothing else, so a piece that ends pitched or
		// rolled hands its neighbour a frame the anchor cannot represent. Both ends, because a piece
		// is entered as well as left.
		CheckSeam(curve, 0, EntryTangent(curve), "entry", warnings);
		CheckSeam(curve, curve.PointCount - 1, ExitTangent(curve), "exit", warnings);

		Vector3 entry = EntryTangent(curve);
		if (entry.LengthSquared() > 1e-9f && Mathf.Abs(YawOf(entry)) > Mathf.DegToRad(1.0f))
		{
			warnings.Add("The spine must leave the origin heading down local -Z. The racer arrives "
						 + "along that axis, so a piece that starts turned meets them at an angle.");
		}

		return warnings.ToArray();
	}

	/// <summary>
	/// Whether one end of the spine is level and un-banked. Both are required and for the same
	/// reason: <see cref="TrackAnchor"/> is a position and a yaw, so there is nowhere for a pitch or
	/// a roll at the seam to be recorded, and the next piece would simply be built as though it
	/// were not there.
	/// </summary>
	private static void CheckSeam(Curve3D curve, int point, Vector3 tangent, string which,
								  List<string> warnings)
	{
		if (Mathf.Abs(curve.GetPointTilt(point)) > Mathf.DegToRad(0.5f))
		{
			warnings.Add($"The {which} point is banked. Tilt has to ease to zero at both ends or the "
						 + "piece meets its neighbour rolled, and the chain cannot carry a roll.");
		}

		if (tangent.LengthSquared() < 1e-9f)
			return;

		if (Mathf.Abs(tangent.Normalized().Y) > Mathf.Sin(Mathf.DegToRad(1.0f)))
		{
			warnings.Add($"The {which} point is pitched. A ramp has to flatten out before the seam, "
						 + "or the joint with the next piece is a kerb.");
		}
	}
}
