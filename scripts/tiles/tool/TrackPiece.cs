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
/// Every mismatch between neighbours is free, because the next piece is placed <i>at</i> this
/// one's exit: position, heading, height, pitch and roll all agree by construction — the seam is a
/// full <see cref="Transform3D"/> and <see cref="TrackSnap"/> folds the whole frame, so a corner
/// that leaves banked hands its bank to whatever follows. The one mismatch left is width, which no
/// transform carries; <see cref="TrackConnector.Width"/> declares it and
/// <see cref="_GetConfigurationWarnings"/> names a disagreement rather than forbidding it.
///
/// The legacy chain (<see cref="TrackAnchor"/>, the grid game mode) still carries position and yaw
/// only, so a banked or pitched seam is flattened <i>there</i> — the warnings say so while both
/// paths exist.
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
	/// How often this piece is dealt to the Track Master, relative to every other card in the
	/// catalog — the built-ins run from 2 (tall ramps) to 7 (corners). <b>0 keeps the piece out of
	/// the deck entirely</b>, which is what a piece wants while it is still being shaped, or when
	/// it only makes sense hand-placed in an assembly.
	///
	/// On the piece itself rather than in a registry, so the scene file is the whole truth about a
	/// piece: the catalog reads it straight out of the saved scene, and there is no second list to
	/// forget to update.
	/// </summary>
	[Export(PropertyHint.Range, "0,12,0.5")]
	public float DeckWeight { get; set; } = 4.0f;

	/// <summary>The card's tooltip in the Track Master's tray. Empty gets a serviceable default —
	/// but the built-in tiles set the tone here, and it is worth matching: say what the piece asks
	/// of the driver, not what it is made of.</summary>
	[Export(PropertyHint.MultilineText)]
	public string DeckDescription { get; set; } = "";

	/// <summary>
	/// How hard <see cref="BankWaypoints"/> rolls the road into its corners, in degrees.
	///
	/// The number is a speed, and the equation is the one the hand-built corners were written
	/// against: a bank carries a car round unaided at <c>v = sqrt(g·r·tan θ)</c>. At this game's
	/// gravity and the catalog's 63 m corner that puts 25 degrees at about 100 km/h, 45 at 150,
	/// and 60 at 200 — dead neutral at top speed, which is where the old <c>TurnMaxBank</c> sat.
	/// Under that number the tires make up the difference; over it the bank is holding you up.
	///
	/// Read the height before committing to a steep one: the road is 54 m across, so the span from
	/// low edge to high is <c>54·sin θ</c> — 38 m at 45 degrees, 47 m at 60.
	/// </summary>
	[Export(PropertyHint.Range, "0,70,1")]
	public float BankDegrees { get; set; } = 45.0f;

	/// <summary>
	/// Tick to roll every waypoint into the corner it sits in, by <see cref="BankDegrees"/>.
	/// Unticks itself.
	///
	/// <b>Banking a corner by hand is the fiddliest thing this tool asks for</b>, because it is the
	/// one edit where getting the sign wrong looks plausible — a corner banked the wrong way is
	/// still a banked corner, just one that throws the car off. Each waypoint is rolled by which
	/// way the road actually turns through it, measured from the neighbours either side, so the
	/// road always leans into its own bend and both hands of a corner come out right from the same
	/// button.
	///
	/// <b>The seams are never touched.</b> They are the contract with the pieces either side, and
	/// leaving them level is what lets a banked corner butt onto a flat straight — the bank rises
	/// out of nothing after the entry and is gone again by the exit, which is exactly what the
	/// hand-built corners' <c>BankScale</c> did. A waypoint on a straight stretch is left alone
	/// rather than levelled, so a deliberate camber on a straight survives.
	/// </summary>
	[Export]
	public bool BankWaypoints
	{
		get => false;
		set
		{
			if (value && Engine.IsEditorHint())
				ApplyBank();
		}
	}

	private void ApplyBank()
	{
		var route = new List<Marker3D>(Route());
		if (route.Count < 3)
		{
			GD.PushWarning($"[TrackPiece] {Name} has no waypoints to bank — the seams are held "
						   + "level on purpose. Add one with the ＋ Waypoint button first.");
			return;
		}

		float bank = Mathf.DegToRad(BankDegrees);
		var rolled = 0;

		for (var i = 1; i < route.Count - 1; i++)
		{
			Marker3D node = route[i];

			// Which way the road turns through this node, read off the neighbours either side and
			// flattened, because a bank answers to the bend in plan and not to the climb.
			Vector3 into = node.Transform.Origin - route[i - 1].Transform.Origin;
			Vector3 outOf = route[i + 1].Transform.Origin - node.Transform.Origin;

			into.Y = 0.0f;
			outOf.Y = 0.0f;

			if (into.LengthSquared() < 1e-4f || outOf.LengthSquared() < 1e-4f)
				continue;

			// Positive turning left. The roll wanted is the opposite sign: lifting the outside of
			// a left-hander tips the surface normal to the right, which is a negative roll.
			float turn = into.Normalized().Cross(outOf.Normalized()).Y;

			// Running straight through: there is no bend to lean into, and levelling it would
			// throw away a camber somebody put there deliberately.
			if (Mathf.Abs(turn) < 0.01f)
				continue;

			node.Transform = node.Transform with
			{
				Basis = Rolled(node.Transform.Basis, -Mathf.Sign(turn) * bank),
			};

			rolled++;
		}

		GD.Print($"[TrackPiece] {Name}: banked {rolled} waypoint(s) to {BankDegrees:0.#} degrees. "
				 + $"The road spans {TileCatalog.TileSize * Mathf.Sin(bank):0.#} m from low edge to "
				 + "high there; the seams are untouched.");
	}

	/// <summary>The same heading and pitch a basis already has, rolled by exactly
	/// <paramref name="roll"/> about the direction it faces — the inverse of <see cref="RollOf"/>,
	/// so banking twice does not compound.</summary>
	private static Basis Rolled(Basis basis, float roll)
	{
		Vector3 forward = (basis * Vector3.Forward).Normalized();

		float pitch = Mathf.Asin(Mathf.Clamp(forward.Y, -1.0f, 1.0f));
		float yaw = Mathf.Atan2(-forward.X, -forward.Z);

		Basis aimed = Basis.FromEuler(new Vector3(pitch, yaw, 0.0f));

		return Mathf.IsZeroApprox(roll)
			? aimed
			: aimed.Rotated((aimed * Vector3.Forward).Normalized(), roll);
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
				// A connector is never a waypoint, whatever it is called: a piece with a second
				// exit must not have that exit quietly threaded into the middle of its road.
				// Neither is a hazard slot — it is also a Marker3D, but it marks a spot ON the
				// road, and a road threaded through its own furniture would kink there.
				if (child is Marker3D marker
					&& marker is not TrackConnector
					&& marker is not TrackHazardSlot
					&& marker.Name != EntryName
					&& marker.Name != ExitName)
					yield return marker;
			}
		}
	}

	/// <summary>
	/// Every connector on the piece, in tree order. Today that is Entry and Exit; a split has a
	/// third, and nothing here assumes two.
	/// </summary>
	public IEnumerable<TrackConnector> Connectors
	{
		get
		{
			foreach (Node child in GetChildren())
			{
				if (child is TrackConnector connector)
					yield return connector;
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

		var positions = new Vector3[nodes.Count];
		var headings = new Vector3[nodes.Count];

		for (var i = 0; i < nodes.Count; i++)
		{
			positions[i] = nodes[i].Transform.Origin;
			headings[i] = (nodes[i].Transform.Basis * Vector3.Forward).Normalized();
		}

		Vector3[] tangents = Tangents(positions, headings);

		var curve = new Curve3D { UpVectorEnabled = true };

		for (var i = 0; i < nodes.Count; i++)
		{
			float back = i > 0 ? HandleLength(positions[i - 1], positions[i],
											  tangents[i - 1], tangents[i]) : 0.0f;
			float on = i < nodes.Count - 1 ? HandleLength(positions[i], positions[i + 1],
														 tangents[i], tangents[i + 1]) : 0.0f;

			curve.AddPoint(positions[i], -tangents[i] * back, tangents[i] * on);
		}

		var rolls = new float[nodes.Count];
		for (var i = 0; i < nodes.Count; i++)
			rolls[i] = RollOf(nodes[i].Transform.Basis);

		AlignTilts(curve, rolls);

		spine.Curve = curve;
	}

	/// <summary>
	/// Set each control point's tilt so the road is banked by exactly the roll its node asks for —
	/// cancelling the twist the curve picks up on its own.
	///
	/// <b>A curve that climbs and turns rolls even when nothing asked it to.</b> Godot carries a
	/// frame along a <see cref="Curve3D"/> by transporting it from the start, and on a
	/// three-dimensional path that transport accumulates torsion. Measured on the proving ground,
	/// pieces whose seams were dead level and whose every tilt was zero arrived at their exits
	/// banked by 27, 36 and 45 degrees. The marker reported no rotation and was telling the truth;
	/// the road meeting it was rolled regardless, and the road is what the next tile butts against.
	///
	/// Tilt is applied <i>on top of</i> that transported frame, so the fix is to measure how far the
	/// frame has drifted at each point and tilt by the difference. The drift is read with tilt
	/// switched off, which makes it independent of what is being solved for — so one pass is exact
	/// and there is nothing to iterate.
	/// </summary>
	private static void AlignTilts(Curve3D curve, float[] rolls)
	{
		float length = curve.GetBakedLength();
		if (length < 0.01f)
			return;

		int last = curve.PointCount - 1;

		for (var i = 0; i <= last; i++)
		{
			// The ends are known exactly; only the points between have to be found along the curve.
			float offset = i == 0 ? 0.0f
						 : i == last ? length
						 : curve.GetClosestOffset(curve.GetPointPosition(i));

			Vector3 forward = TangentAt(curve, offset, length);
			if (forward.LengthSquared() < 1e-9f)
				continue;

			forward = forward.Normalized();

			Vector3 right = forward.Cross(Vector3.Up);

			// Running dead vertical: every roll looks the same and there is no horizon to measure
			// against, so leave whatever the transport produced.
			if (right.LengthSquared() < 1e-6f)
				continue;

			right = right.Normalized();

			Vector3 natural = curve.SampleBakedUpVector(offset, applyTilt: false);
			if (natural.LengthSquared() < 1e-9f)
				continue;

			natural = natural.Normalized();

			// Where up should end up: vertical, squared against the direction of travel, then rolled
			// by however far the node is banked.
			Vector3 wanted = right.Cross(forward).Normalized().Rotated(forward, rolls[i]);

			curve.SetPointTilt(i, Mathf.Atan2(forward.Dot(natural.Cross(wanted)),
											  natural.Dot(wanted)));
		}
	}

	/// <summary>The direction of travel a given distance along a curve.</summary>
	private static Vector3 TangentAt(Curve3D curve, float offset, float length)
	{
		float epsilon = Mathf.Max(0.05f, length * 0.001f);

		return curve.SampleBaked(Mathf.Min(length, offset + epsilon), cubic: true)
			   - curve.SampleBaked(Mathf.Max(0.0f, offset - epsilon), cubic: true);
	}

	/// <summary>
	/// The direction the road actually travels through each point: the node's own heading, eased
	/// toward the line its neighbours make, by <see cref="Smoothing"/>.
	///
	/// <b>This is what makes a sharp kink hard to build by accident.</b> A waypoint aimed somewhere
	/// unrelated to where its neighbours sit forces the curve to swing out and back to obey it, and
	/// the swing has to happen in whatever distance is available — put two such points close
	/// together and the road folds. Easing the heading toward the chord through the neighbours
	/// spends that disagreement as a gentler curve instead of a crease.
	///
	/// The seams are never eased. Their headings are the piece's contract with its neighbours, and
	/// a seam quietly rotated to smooth the road would be a piece that no longer joins.
	/// </summary>
	private Vector3[] Tangents(Vector3[] positions, Vector3[] headings)
	{
		var tangents = new Vector3[positions.Length];
		float ease = Mathf.Clamp(Smoothing, 0.0f, 1.0f);

		for (var i = 0; i < positions.Length; i++)
		{
			tangents[i] = headings[i];

			bool seam = i == 0 || i == positions.Length - 1;
			if (seam || ease <= 0.0f)
				continue;

			Vector3 chord = positions[i + 1] - positions[i - 1];
			if (chord.LengthSquared() < 1e-6f)
				continue;

			Vector3 eased = headings[i].Lerp(chord.Normalized(), ease);

			// Only degenerate if the heading is aimed straight back down the chord, where there is
			// no sensible midpoint between them. Keep what the author asked for.
			if (eased.LengthSquared() > 1e-6f)
				tangents[i] = eased.Normalized();
		}

		return tangents;
	}

	/// <summary>
	/// How long a Bezier handle has to be for the segment between two points to trace a circular
	/// arc, given the direction the road leaves one and arrives at the other.
	///
	/// <b>A third of the chord is the wrong answer, and it was the one being used.</b> That rule
	/// only holds for gentle bends: a quarter turn needs <c>0.39</c> of its chord, so at a third
	/// every arc pulled inside itself and spiked the curvature by each control point. Measured on
	/// the proving ground, a hand-built garage helix bent twice as hard as the catalog's own tightest
	/// corner for no reason but this.
	///
	/// The exact figure for an arc turning <c>t</c> across a chord <c>d</c> is
	/// <c>(4/3)·tan(t/4)·d / (2·sin(t/2))</c>, which tends to <c>d/3</c> as the turn goes to nothing
	/// — so a straight run is unchanged and only the corners move.
	/// </summary>
	private static float HandleLength(Vector3 from, Vector3 to, Vector3 leaving, Vector3 arriving)
	{
		float chord = from.DistanceTo(to);
		if (chord < 0.01f)
			return 0.0f;

		float turn = leaving.AngleTo(arriving);

		// Straight, or near enough that the arc formula is dividing two vanishing numbers.
		if (turn < 0.01f)
			return chord / 3.0f;

		// Past a half turn there is no single arc through the pair, and the formula runs away.
		turn = Mathf.Min(turn, Mathf.Pi * 0.9f);

		return 4.0f / 3.0f * Mathf.Tan(turn * 0.25f) * chord / (2.0f * Mathf.Sin(turn * 0.5f));
	}

	/// <summary>
	/// How far a basis is rolled about its own forward axis, in radians — which is what a curve's
	/// tilt means, so banking a piece is rotating a waypoint.
	///
	/// Measured against the un-rolled frame: the up vector you would have if the same heading and
	/// pitch carried no roll at all.
	/// </summary>
	public static float RollOf(Basis basis)
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
	/// If asked, hold the two seams level and on a tidy heading.
	///
	/// <b>Opt-in now, where it used to be the law.</b> While the chain was a position and a yaw, a
	/// seam with pitch or roll in it was a frame the next piece could not be built against, so
	/// every seam was flattened on every edit. <see cref="TrackSnap"/> folds full transforms, which
	/// makes a banked exit a piece's most interesting feature rather than its defect — a corner
	/// that leaves at 30 degrees of bank joins a corner that arrives at 30 degrees of bank, and
	/// the pair make a longer banked sweep.
	///
	/// What survives is the tidiness knob: with <see cref="SeamSnapDegrees"/> set, both seams are
	/// levelled and their headings snapped, which is what a piece meant to chain on square compass
	/// headings wants. At 0 — the default — seams are wherever and however you aim them.
	/// </summary>
	private void SnapSeams()
	{
		if (SeamSnapDegrees <= 0.0f)
			return;

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

			float step = Mathf.DegToRad(SeamSnapDegrees);
			float yaw = Mathf.Round(Mathf.Atan2(-flat.X, -flat.Z) / step) * step;

			var level = new Basis(Vector3.Up, yaw);
			if (!seam.Transform.Basis.IsEqualApprox(level))
				seam.Transform = new Transform3D(level, seam.Transform.Origin);
		}
	}

	private float _seamSnapDegrees;

	/// <summary>
	/// Snap both seams level, with their headings on a multiple of this many degrees. 0 — the
	/// default — leaves them exactly as aimed, banked and pitched included.
	///
	/// <b>The tidiness knob for pieces meant to chain on square headings.</b> A seam's yaw <i>is</i>
	/// the turn the piece makes — a right-hander exits at -90 because that is what turning right
	/// means — and at 90 a piece leaves heading straight on, or square left or right, so the tile
	/// after it starts from an angle that lines up with everything else. Setting it also levels the
	/// seams, because a piece asking for square headings is a piece asking for flat joints.
	///
	/// 45 for pieces that want the diagonals too. Off by default, so it never quietly rotates — or
	/// un-banks — a seam that was deliberately aimed somewhere else.
	/// </summary>
	[Export(PropertyHint.Range, "0,90,15")]
	public float SeamSnapDegrees
	{
		get => _seamSnapDegrees;
		set => _seamSnapDegrees = value;
	}

	private float _smoothing = 0.5f;

	/// <summary>
	/// How far each waypoint's heading is eased toward the line its neighbours make, from 0 (obey
	/// the way the node is aimed, exactly) to 1 (ignore it and follow the neighbours).
	///
	/// The knob for how hard it is to build a crease. A waypoint aimed away from where its
	/// neighbours sit forces the road to swing out and back to obey it, in whatever distance
	/// happens to be available — and two such points close together fold the road rather than curve
	/// it. Easing spends the disagreement as a gentler bend instead.
	///
	/// A half by default: enough that a carelessly aimed point rounds off, little enough that
	/// aiming one still visibly steers the road. Drop it to 0 while matching an exact arc; raise it
	/// when a piece is fighting you.
	///
	/// Seams are never eased whatever this says — their headings are the contract with the pieces
	/// either side, and rotating one to smooth the road would be a piece that no longer joins.
	/// </summary>
	[Export(PropertyHint.Range, "0,1,0.05")]
	public float Smoothing
	{
		get => _smoothing;
		// No rebuild call: the editor's fingerprint poll carries this, and at runtime the spine is
		// generated once in _Ready with whatever the scene file saved.
		set => _smoothing = value;
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
			CsgPolygon3D? road = RoadPolygon;
			Vector2[]? polygon = road?.Polygon;

			if (road == null || polygon is not { Length: >= 2 })
				return 0.0f;

			float low = polygon[0].X;
			float high = polygon[0].X;

			foreach (Vector2 point in polygon)
			{
				low = Mathf.Min(low, point.X);
				high = Mathf.Max(high, point.X);
			}

			// Scaled by whatever sits between the polygon and the piece, because a section drawn 54 m
			// wide under a node scaled by a half is a 27 m road and reporting it as 54 was how
			// SnapToRoadWidth came to decide there was nothing to do.
			return (high - low) * SectionScale();
		}
	}

	/// <summary>
	/// How much the transform chain from the cross-section up to the piece stretches it across.
	///
	/// Stops at the piece rather than going to the scene root on purpose: the piece's own transform
	/// is <i>overwritten</i> when it is placed — by the chain, and by the proving ground — so
	/// anything scaled onto it there is discarded before the game ever sees it. Only what is inside
	/// the piece survives, and only that is worth measuring.
	/// </summary>
	private float SectionScale()
	{
		var scale = 1.0f;

		for (Node3D? node = RoadPolygon; node != null && node != this; node = node.GetParentOrNull<Node3D>())
			scale *= node.Transform.Basis.X.Length();

		return scale;
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
		{
			GD.Print($"[TrackPiece] {Name}'s road already measures "
					 + $"{TileCatalog.TileSize:0.##} m. Nothing to snap.");
			return;
		}

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
	/// The seam the piece is attached <i>by</i>: its first entry connector, or the Entry marker on
	/// an old scene. Everything that places a piece goes through this rather than through
	/// <see cref="Entry"/>, so a piece whose seams are typed and one whose seams are plain markers
	/// place identically.
	/// </summary>
	public Marker3D? EntrySeam
	{
		get
		{
			foreach (TrackConnector connector in Connectors)
			{
				if (connector.Role == ConnectorRole.Entry)
					return connector;
			}

			return Entry;
		}
	}

	/// <summary>
	/// Every seam the piece hands the track on through, in tree order. One for a piece that runs
	/// through, two for a split — and an old scene's Exit marker counts, so nothing has to be
	/// upgraded before it can be built onto.
	/// </summary>
	public IEnumerable<Marker3D> ExitSeams
	{
		get
		{
			var any = false;
			foreach (TrackConnector connector in Connectors)
			{
				if (connector.Role != ConnectorRole.Exit)
					continue;

				any = true;
				yield return connector;
			}

			if (!any && Exit is { } exit)
				yield return exit;
		}
	}

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
	/// <b>No longer a defect — a property.</b> The assembled chain folds full transforms
	/// (<see cref="TrackSnap"/>), so a piece that leaves banked hands the whole banked frame to its
	/// neighbour and the joint is seamless. What this number still is, is information: a piece with
	/// a rolled exit only looks continuous against a piece whose entry expects that roll, and the
	/// legacy <see cref="TrackAnchor"/> chain — position and yaw — flattens it entirely. Read it,
	/// don't fear it.
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
			SnapSeams();
			SetProcess(true);
		}
		else if (IsBaked && !KeepBuildForRebake)
		{
			// CSG is an authoring tool. A baked piece has everything it needs, and leaving the
			// combiner in the tree would have the engine rebuilding the same shape on load for every
			// tile on the track, plus a second set of collision on top of the baked one.
			Build?.QueueFree();
		}

		// Rebuilt on load as well as on edit, in the game as much as in the editor. The spine is
		// derived from the route, so generating it in only one of those would mean the scene file
		// had to carry a copy — and a saved copy of derived data is a copy that can go stale.
		//
		// Baked pieces are regenerated too, but only in the editor: their mesh is already made and
		// does not need the curve, yet a stale spine is exactly what the next bake would build
		// from. At runtime a baked piece needs neither.
		if (Engine.IsEditorHint() || !IsBaked)
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

		// Order matters: the seams are snapped first so the spine is generated through frames that
		// already satisfy the contract, rather than being built and then contradicted.
		SnapSeams();
		RebuildSpine();
		UpdateConfigurationWarnings();

		// The route gizmos draw off these nodes, and only the marker being dragged is refreshed
		// automatically — the piece's own gizmo, and every other marker's, would show the old road.
		UpdateGizmos();
		foreach (Marker3D node in Route())
			node.UpdateGizmos();
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
		hash.Add(Smoothing);
		hash.Add(SeamSnapDegrees);

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
	public IEnumerable<Marker3D> Route()
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
		// TrackConnectors rather than bare markers, so a new piece's seams carry width and profile
		// from the moment they exist. Old scenes whose seams are plain Marker3Ds keep working —
		// everything reads them through the Marker3D type and assumes the connector defaults.
		if (Entry == null)
		{
			var entry = new TrackConnector { Name = EntryName, Role = ConnectorRole.Entry };
			AddChild(entry);
			Adopt(entry);
		}

		// A tile-length straight ahead: the commonest piece there is, and an obvious thing to drag
		// somewhere else.
		if (Exit == null)
		{
			var exit = new TrackConnector
			{
				Name = ExitName,
				Role = ConnectorRole.Exit,
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
	/// Set by <c>tools/BakePieces</c> before it instances anything, when a forced re-bake was
	/// asked for. A baked piece normally frees its Build subtree the moment it enters the tree at
	/// runtime — right for the game, fatal for a tool that is about to bake that very CSG again.
	/// Static because the pieces are instanced by a scene that has no other line to them before
	/// their <c>_Ready</c> runs.
	/// </summary>
	public static bool KeepBuildForRebake { get; set; }

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
	///
	/// Public, and callable outside the editor, because the inspector tick box is not the only
	/// legitimate way to press it: <c>tools/BakePieces.tscn</c> bakes the whole catalog headlessly
	/// in one run, which beats opening seventeen scenes by hand and forgetting the eighteenth.
	/// Completion is observed through <see cref="IsBaked"/> — this is <c>async void</c> and cannot
	/// be awaited; it saves nothing to disk either way, that stays the caller's decision.
	/// </summary>
	public async void RunBake()
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

		if (DeckWeight > 0.0f && !IsBaked && Build != null)
		{
			warnings.Add($"In the deck (weight {DeckWeight:0.#}) but not baked: every mid-race "
						 + "placement rebuilds this CSG on every peer. Tick Bake before it ships, "
						 + "or set DeckWeight to 0 while it is still being shaped.");
		}

		// A piece is positioned by its Entry when it is placed, which overwrites this node's whole
		// transform — scale included. So a scaled root shows you one size in the editor and builds
		// another in the game, which is the most misleading thing a piece can do.
		Vector3 rootScale = Transform.Basis.Scale;
		if (!rootScale.IsEqualApprox(Vector3.One))
		{
			warnings.Add($"The piece's own scale is {rootScale.X:0.##}, {rootScale.Y:0.##}, "
						 + $"{rootScale.Z:0.##}. That is thrown away when the piece is placed, so it "
						 + "only changes how it looks here. Scale the Build subtree instead, or move "
						 + "the markers.");
		}

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

		// Position, heading, height, pitch and roll all need no checking at all: the next piece is
		// placed *at* this seam as a full transform, so every one of them agrees by construction.
		// The one caveat, while the legacy anchor chain still exists, is that *it* carries position
		// and yaw only — a piece meant for the grid game mode wants level seams, and this says so
		// without forbidding the banked ones the assembly workflow is for.
		float roll = ExitRollDegrees;
		Vector3 forward = (seam.Basis * Vector3.Forward).Normalized();
		float pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(forward.Y, -1.0f, 1.0f)));

		if (Mathf.Abs(roll) > 1.0f || Mathf.Abs(pitch) > 1.0f)
		{
			warnings.Add($"{ExitName} leaves banked {roll:0.#} degrees and pitched {pitch:0.#} "
						 + "degrees relative to the entry. Assembled tracks carry that through the "
						 + "joint — just pair it with pieces that expect it. Only the legacy grid "
						 + "chain flattens seams; a piece for that mode wants both at zero.");
		}

		return warnings.ToArray();
	}
}
