using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// Tiles whose whole shape <i>is</i> the hazard, and which therefore build themselves from
/// scratch instead of taking the floor-walls-line-hazard assembly the core partial offers.
///
/// A tile ends up here when the standard assembly has nothing useful to say about it: it walls
/// off the four edges of a flat straight run, so it cannot describe an L with both openings on
/// one side (the hairpin), a road that is not level (the ramps) or one that leaves the ground
/// altogether (the loop).
/// </summary>
public partial class TrackTile
{
	/// <summary>
	/// Radius of a hairpin's arc, as a multiple of the tile. One tile means the U is two cells
	/// across, which lands its exit on a cell face — see <see cref="BuildBankedArc"/> for why the
	/// radius is not free.
	///
	/// It also sets what a hairpin asks of a driver, and the answer is now a question rather than a
	/// toll. The inside of the U is 30 m and has to be crawled; the outside is 90 m and banked, and
	/// a bank of that radius carries a car at about 200 km/h unaided. So the quick way round is the
	/// long way round, held up on the wall — and getting it wrong up there drops you back down the
	/// bank having lost everything.
	///
	/// The old hairpin was two straight lanes with an apex wall between them. It could only ever be
	/// driven one way: brake to nothing, turn, go. That is not a corner, it is a stop sign.
	/// </summary>
	private const float HairpinRadius = Size;

	/// <summary>A 180-degree banked U. See <see cref="HairpinRadius"/>.</summary>
	private void BuildHairpin(TileDefinition definition)
		=> BuildBankedArc(definition, HairpinRadius, Mathf.Pi);

	/// <summary>
	/// A quarter turn swept round a banked arc through a square block of cells. See
	/// <see cref="TileData.IsWideTurn"/> for why a corner cannot live in one cell.
	/// </summary>
	private void BuildWideTurn(TileDefinition definition)
		=> BuildBankedArc(definition, (Data.TurnSpan - 0.5f) * Size, Mathf.Pi * 0.5f);

	// ---- Wide turns ----

	// These three are a budget, not just a quality setting, and they are the one thing in this file
	// that must not be raised casually.
	//
	// A banked turn is the only tile whose geometry is two-dimensional — segments round the arc
	// times strips across the road — so its cost is the product of these numbers rather than a sum,
	// and it lands as *collision shapes*, two nodes a facet. At 14 x 15 a hairpin alone is 504
	// boxes, and building a proving ground's worth of them segfaults the physics server outright
	// while it is assembling the broadphase. Not an out-of-memory, not an exception: a native crash
	// with nothing in the log. 12 x 9 is about half of that and runs clean with room to spare.
	//
	// So resolution has to be bought rather than spent, which is what the sqrt spacing below is
	// for: it buys the same smoothness at the lip for fewer strips.

	/// <summary>Facets round the arc. Twelve is 7.5 degrees a joint over a quarter turn, and a
	/// hairpin scales to twice as many so it is no coarser for being twice as long.</summary>
	private const int TurnArcSegments = 12;

	/// <summary>
	/// Strips across the flat inner part of the road. One, because that region is a level plane and
	/// a second strip across it would be an extra hundred collision shapes describing the same
	/// surface.
	/// </summary>
	private const int TurnFlatStrips = 1;

	/// <summary>
	/// And across the banked outer part, where all the curvature is. Eight is enough only because
	/// they are not evenly spaced — see the sqrt in <see cref="BuildBankedArc"/>.
	/// </summary>
	private const int TurnBankStrips = 8;

	/// <summary>
	/// Fraction of the road width, measured from the inside, that stays flat. The inside line has
	/// to be drivable by a car that arrived slowly, and a corner banked all the way across has no
	/// such line.
	/// </summary>
	private const float TurnBankStart = 0.35f;

	/// <summary>
	/// Bank angle at the outer lip, in degrees.
	///
	/// <b>This number is not a look, it is a speed.</b> A bank holds a car with no help from the
	/// tires at exactly <c>v = sqrt(g x r x tan(theta))</c>. At 2 g, the outer radius of a default
	/// turn, and 60 degrees, that comes out at about 210 km/h — so the top of the bank is neutral
	/// right at <c>TopSpeed</c>, and riding the wall flat out is the car being held up by its own
	/// cornering rather than by anything being faked for it.
	///
	/// The whole tile falls out of that one equivalence. Below that speed the car sits low on the
	/// bank, above it the car climbs, and a driver who carries too little speed onto the high line
	/// slides back down it — which is what the surface would really do, because the springs push
	/// along the chassis' own up and leave gravity's component along the slope unopposed. See the
	/// gravity-on-slopes section of <c>docs/vehicle-physics.md</c>.
	/// </summary>
	private const float TurnMaxBank = 60.0f;

	/// <summary>
	/// Fraction of the arc spent easing the bank in at each end.
	///
	/// Load-bearing. The tile either side of a corner is flat, so a bank that was at full height
	/// from the first facet would meet its neighbour as a sixteen metre step. Easing it in over the
	/// first quarter of the arc means the corner joins the straight flat and level, exactly the way
	/// <see cref="RampBlend"/> makes a ramp meet the road it starts from.
	/// </summary>
	private const float TurnBankBlend = 0.25f;

	/// <summary>
	/// How much each facet is grown past the chord it spans. Consecutive facets meet at their
	/// shared corners but are rectangles about their own midpoints, so an unstretched pair leaves a
	/// thin wedge open on the outside of every joint. Overlapping is free and a gap in the road is
	/// not.
	/// </summary>
	private const float FacetOverlap = 1.06f;

	/// <summary>
	/// The shape both turns are: a road of one tile's width swept round an arc of
	/// <paramref name="radius"/> through <paramref name="sweep"/> radians, banked.
	///
	/// <b>The radius is not free.</b> The road is a tile wide and centred on the arc, so its edges
	/// come out at <c>radius +/- Half</c> — and for the turn to meet its neighbours squarely those
	/// have to land on a cell face. That is the whole of why a quarter turn's radius is
	/// <c>(span - 0.5) x Size</c> and a hairpin's is a whole tile: at those two values the road
	/// leaves through the middle of a cell's face at exactly full width, and the tile tessellates
	/// with the straights either side of it. Any other radius leaves a step in the road.
	///
	/// The surface is a two-dimensional fan — segments round the arc, strips across the road — and
	/// every facet works out its own frame from its four corners rather than from an angle. That is
	/// the part worth defending: the bank rolls the surface *and* the ease tips it along the
	/// direction of travel, so there is no single axis to rotate about, and taking the frame from
	/// the corners means the geometry cannot disagree with the maths that placed them.
	///
	/// The inside of the arc is open air, not infield. Cutting the corner drops you through it,
	/// which is what makes the inside line a decision rather than a freebie.
	/// </summary>
	private void BuildBankedArc(TileDefinition definition, float radius, float sweep)
	{
		int side = Data.TurnSide;

		// Held to the same degrees-per-joint whatever the sweep, so a hairpin is not twice as
		// coarse as a corner for being twice as long.
		int segments = Mathf.Max(8, Mathf.RoundToInt(TurnArcSegments * sweep / (Mathf.Pi * 0.5f)));

		// The cross-section, from the inside edge outward: where each strip boundary falls across
		// the road, and how far the bank has climbed by the time it gets there.
		int strips = TurnFlatStrips + TurnBankStrips;
		var reach = new float[strips + 1];
		var lift = new float[strips + 1];

		float flat = Size * TurnBankStart;
		float banked = Size - flat;
		float maxBank = Mathf.DegToRad(TurnMaxBank);

		for (int m = 0; m <= strips; m++)
		{
			if (m <= TurnFlatStrips)
			{
				reach[m] = radius - Half + flat * m / TurnFlatStrips;
				continue;
			}

			int b = m - TurnFlatStrips;

			// The bank angle is squared across the road, so it leaves the flat almost
			// imperceptibly and keeps all of its steepness for the top. A linear rise puts a usable
			// amount of bank right where the flat road ends, which reads as a kink rather than as
			// the road turning up.
			//
			// The strips are then spaced by the *inverse* of that — sqrt — so each one turns
			// through the same angle instead of covering the same width: wide and far apart down at
			// the shallow end where a wide flat box is an honest description of the surface, and
			// bunched up at the lip where it is not. Evenly spaced, the last two strips of eight
			// differ by 13 degrees and leave a ridge across the fastest part of the corner; spaced
			// like this every joint is 7.5 degrees for the same eight shapes. It is the same trade
			// <see cref="RampSamples"/> makes along a ramp, for the same reason.
			float v = Mathf.Sqrt((float)b / TurnBankStrips);
			float previous = Mathf.Sqrt((b - 1.0f) / TurnBankStrips);

			reach[m] = radius - Half + flat + banked * v;

			float mid = (v + previous) * 0.5f;
			lift[m] = lift[m - 1] + (reach[m] - reach[m - 1]) * Mathf.Tan(maxBank * mid * mid);
		}

		// A point on the banked surface: strip boundary m, arc segment boundary k. Zero angle is
		// the entry edge, so the whole tile is described from its own entry cell outward.
		Vector3 Surface(int m, int k)
		{
			float t = (float)k / segments;
			float a = t * sweep;

			return new Vector3(
				side * (radius - reach[m] * Mathf.Cos(a)),
				lift[m] * BankScale(t),
				Half - reach[m] * Mathf.Sin(a));
		}

		StandardMaterial3D road = RoadMaterial();
		StandardMaterial3D wall = WallMaterial(definition.Accent);
		StandardMaterial3D line = LineMaterial();

		for (int k = 0; k < segments; k++)
		{
			for (int m = 0; m < strips; m++)
			{
				AddTurnFacet(Surface(m, k), Surface(m + 1, k),
							 Surface(m, k + 1), Surface(m + 1, k + 1), road);
			}

			// Inside edge, then the lip at the top of the bank. The neighbouring boundary is passed
			// with each so the barrier can find which way is off the surface where it stands — at
			// the top of the bank that is more than fifty degrees from vertical.
			AddTurnEdge(Surface(0, k), Surface(0, k + 1),
						Surface(1, k), Surface(1, k + 1), WallHeight, wall);
			AddTurnEdge(Surface(strips, k), Surface(strips, k + 1),
						Surface(strips - 1, k), Surface(strips - 1, k + 1), WallHeight, wall);

			// The seam where the flat road ends and the bank starts. It does the racing line's job
			// from board altitude, and at road level it is the one thing telling a driver where the
			// wall begins.
			AddTurnSeam(Surface(TurnFlatStrips, k), Surface(TurnFlatStrips, k + 1),
						Surface(TurnFlatStrips + 1, k), Surface(TurnFlatStrips + 1, k + 1), line);
		}
	}

	/// <summary>
	/// How much of the full bank is present a fraction <paramref name="t"/> of the way round the
	/// arc: none at either end, all of it through the middle. See <see cref="TurnBankBlend"/>.
	/// </summary>
	private static float BankScale(float t)
	{
		if (t < TurnBankBlend)
			return Mathf.SmoothStep(0.0f, 1.0f, t / TurnBankBlend);

		if (t > 1.0f - TurnBankBlend)
			return Mathf.SmoothStep(0.0f, 1.0f, (1.0f - t) / TurnBankBlend);

		return 1.0f;
	}

	/// <summary>One quad of banked road, laid on the surface its four corners describe.</summary>
	private void AddTurnFacet(Vector3 nearInner, Vector3 nearOuter, Vector3 farInner,
							  Vector3 farOuter, StandardMaterial3D material)
	{
		if (!SurfaceFrame(nearInner, nearOuter, farInner, farOuter,
						  out Basis basis, out Vector3 centre, out float wide, out float along))
			return;

		AddOrientedBox(new Vector3(wide * FacetOverlap, SlopeThickness, along * FacetOverlap),
					   new Transform3D(basis, centre - basis.Y * (SlopeThickness * 0.5f)), material);
	}

	/// <summary>
	/// A barrier standing perpendicular to the banked surface along one of its edges.
	/// <paramref name="innerNear"/> and <paramref name="innerFar"/> are the next boundary in, used
	/// only to find which way is off the surface here.
	/// </summary>
	private void AddTurnEdge(Vector3 near, Vector3 far, Vector3 innerNear, Vector3 innerFar,
							 float height, StandardMaterial3D material)
	{
		if (!SurfaceFrame(near, innerNear, far, innerFar,
						  out Basis basis, out _, out _, out float along))
			return;

		AddOrientedBox(new Vector3(WallThickness, height, along * FacetOverlap),
					   new Transform3D(basis, (near + far) * 0.5f + basis.Y * (height * 0.5f)),
					   material);
	}

	/// <summary>A painted stripe following a boundary across the banked surface. Mesh only.</summary>
	private void AddTurnSeam(Vector3 near, Vector3 far, Vector3 outerNear, Vector3 outerFar,
							 StandardMaterial3D material)
	{
		if (!SurfaceFrame(near, outerNear, far, outerFar,
						  out Basis basis, out _, out _, out float along))
			return;

		AddOrientedBox(new Vector3(Size * 0.05f, 0.02f, along * FacetOverlap),
					   new Transform3D(basis, (near + far) * 0.5f + basis.Y * 0.011f),
					   material, collision: false);
	}

	/// <summary>
	/// The orientation and extent of a surface quad, worked out from its four corners.
	///
	/// Corners rather than angles because the banked turn is curved in two directions at once — it
	/// yaws round the arc and rolls up the bank, and during the ease it tips along the direction of
	/// travel as well. There is no single axis to rotate about, and any attempt to compose one out
	/// of euler angles is a sign bug waiting to happen. Four points cannot be wrong about the
	/// surface they are on.
	///
	/// The basis comes out orthonormal and right-handed with X across the surface, Y off it and Z
	/// along it, which is the axis order a <see cref="BoxMesh"/> reads its size in. Returns false
	/// for a degenerate quad, which is how a zero-width strip declines to be built.
	/// </summary>
	private static bool SurfaceFrame(Vector3 nearInner, Vector3 nearOuter, Vector3 farInner,
									 Vector3 farOuter, out Basis basis, out Vector3 centre,
									 out float wide, out float along)
	{
		centre = (nearInner + nearOuter + farInner + farOuter) * 0.25f;

		Vector3 ahead = (farInner + farOuter - nearInner - nearOuter) * 0.5f;
		Vector3 outward = (nearOuter + farOuter - nearInner - farInner) * 0.5f;

		along = ahead.Length();
		wide = outward.Length();

		basis = Basis.Identity;
		if (along < 0.001f || wide < 0.001f)
			return false;

		Vector3 forward = ahead / along;

		// Flipped to point at the sky rather than reasoned about: which way the cross product comes
		// out depends on whether the corner turns left or right and on whether this is the inside
		// edge or the outside one, and all four combinations occur.
		Vector3 up = forward.Cross(outward).Normalized();
		if (up.Y < 0.0f)
			up = -up;

		basis = new Basis(up.Cross(forward), up, forward);
		return true;
	}

	// ---- Ramps ----

	/// <summary>How many facets a ramp is built from. Enough that the eased profile reads as a
	/// curve and the car does not feel the joints.</summary>
	/// <summary>
	/// Chord facets in each of a ramp's two eased ends.
	///
	/// All of a ramp's curvature is here, so this is the number that decides how big a crease the
	/// car has to be thrown over. The old build spread 8 facets evenly across the whole tile and
	/// left 10–18° kinks. Eight per ease brings the worst crease to 4.3° on a one-cube climb and
	/// 7.9° on a two-cube one, for 20 facets against the old 8.
	/// </summary>
	private const int RampEaseSegments = 8;

	/// <summary>
	/// Chord facets in a ramp's constant-slope middle. These are collinear — there is no crease
	/// between them at any count — so this is only about not spanning the middle with one very
	/// long collision box.
	/// </summary>
	private const int RampMidSegments = 4;

	/// <summary>Thickness of a sloped or looping road slab. Thin, unlike the flat tiles' twenty
	/// metre foundation, because a ramp has open air underneath it and a loop has its own inside.</summary>
	private const float SlopeThickness = 1.6f;

	/// <summary>
	/// A climb or a descent, of one or two cubes over the tile's three cells.
	///
	/// Built as facets along an eased profile rather than as one flat wedge. A constant slope would
	/// meet the flat track at a hard kink — an 18-degree kerb at the bottom of the gentle version
	/// and 34 degrees at the bottom of the steep one — which at racing speed is less a ramp than a
	/// ramp-shaped wall. Easing in and out means the track is level where it joins its neighbours
	/// and steepest in the middle, at the cost of that middle being steeper than the average:
	/// about 27 degrees for one cube and 45 for two.
	/// </summary>
	private void BuildRamp(TileDefinition definition)
	{
		float rise = Data.HeightChange * TileCatalog.HeightStep;

		StandardMaterial3D road = RoadMaterial();
		StandardMaterial3D wall = WallMaterial(definition.Accent);
		StandardMaterial3D line = LineMaterial();

		float[] samples = RampSamples();

		for (int i = 0; i < samples.Length - 1; i++)
		{
			float t0 = samples[i];
			float t1 = samples[i + 1];

			var p0 = new Vector2(HalfLength - t0 * Length, RampHeightAt(t0, rise));
			var p1 = new Vector2(HalfLength - t1 * Length, RampHeightAt(t1, rise));

			AddSlopedSlab(p0, p1, road, wall, line);

			// The tall climb gets a boost strip on its first facet, which is the only stretch of it
			// shallow enough to still be a road. Without one the tile is only passable by a car that
			// happened to arrive flat out, and a racer who met a hazard on the way in is left
			// stalling halfway up an eighty metre hill with nowhere to turn around.
			if (i == 0 && Data.HeightChange >= 2)
				AddClimbBoost(p0, p1);
		}
	}

	/// <summary>
	/// Where along the ramp to put the facet joints, as fractions from entry to exit.
	///
	/// Deliberately not evenly spaced. Every bit of a ramp's curvature is in the two eased ends,
	/// so that is where the facets are worth spending: spreading them evenly puts most of them
	/// down the constant-slope middle where consecutive facets are collinear and the extra
	/// resolution buys nothing at all.
	/// </summary>
	private static float[] RampSamples()
	{
		var samples = new float[RampEaseSegments * 2 + RampMidSegments + 1];
		var n = 0;

		for (var i = 0; i < RampEaseSegments; i++)
			samples[n++] = RampBlend * i / RampEaseSegments;

		for (var i = 0; i < RampMidSegments; i++)
			samples[n++] = RampBlend + (1.0f - 2.0f * RampBlend) * i / RampMidSegments;

		for (var i = 0; i < RampEaseSegments; i++)
			samples[n++] = 1.0f - RampBlend + RampBlend * i / RampEaseSegments;

		samples[n] = 1.0f;
		return samples;
	}

	/// <summary>
	/// A boost pad lying on a ramp facet, firing up the slope rather than along the horizon — on a
	/// climb those are different directions, and only one of them helps.
	/// </summary>
	private void AddClimbBoost(Vector2 from, Vector2 to)
	{
		float theta = Mathf.Atan2(to.Y - from.Y, from.X - to.X);
		float sin = Mathf.Sin(theta);
		float cos = Mathf.Cos(theta);

		var mid = new Vector3(0.0f, (from.Y + to.Y) * 0.5f, (from.X + to.X) * 0.5f);
		var up = new Vector3(0.0f, cos, sin);

		// Local -Z carried round by the facet's own rotation: forward, and tilted up the slope.
		var forward = new Vector3(0.0f, sin, -cos);

		// Nearly the full width of the road, and short enough to lie inside the facet. Unmissable
		// on purpose: a racer who threaded past a narrow pad would be a racer stranded halfway up.
		AddImpulsePad(new Vector2(Size * 0.86f, 13.0f), mid, BoostMaterial(),
					  () => GlobalBasis * forward, ClimbBoostImpulse,
					  rotationDegrees: new Vector3(Mathf.RadToDeg(theta), 0.0f, 0.0f),
					  surfaceUp: up);
	}

	/// <summary>
	/// Fraction of a ramp's length spent easing in at each end. The stretch between is a constant
	/// slope.
	///
	/// The ramp used to be pure smoothstep from end to end, which sounds smooth and is not: the
	/// slope of a smoothstep peaks at <b>1.5×</b> its own average, so a two-cube climb reached 45°
	/// in the middle while being 33° on paper, and the angle was changing everywhere. Every facet
	/// joint was a crease the car had to be thrown over.
	///
	/// A constant slope has creases in only two places, and this eases those two out.
	/// </summary>
	private const float RampBlend = 0.22f;

	/// <summary>
	/// Height of the ramp surface a fraction <paramref name="t"/> of the way from entry to exit.
	///
	/// Trapezoidal: smoothstep into a constant slope, hold it, smoothstep out. Flat at both ends
	/// so the joints with the neighbouring tiles are still tangential, but the middle — which is
	/// most of the ramp — is a single plane at one angle.
	///
	/// With <see cref="RampBlend"/> at 0.22 the constant slope is <c>rise / (run × 0.78)</c>:
	/// <b>23°</b> for a one-cube climb over three cells and <b>40°</b> for two. Both inside the
	/// range a car can actually drive up, and the two-cube climb no longer needs its boost strip
	/// to be passable at all.
	/// </summary>
	private static float RampHeightAt(float t, float rise)
	{
		// Area under a trapezoidal speed profile, normalised so t=1 gives exactly the full rise.
		// The eases are smoothstep, so their integral is the t³/3 − t⁴/6 below.
		float area = Ease(1.0f, RampBlend);
		return rise * Ease(t, RampBlend) / area;
	}

	/// <summary>
	/// Unnormalised height of a trapezoidal slope profile at <paramref name="t"/>: smoothstep up
	/// over the first <paramref name="blend"/>, constant, smoothstep down over the last.
	/// </summary>
	private static float Ease(float t, float blend)
	{
		if (blend <= 0.0f)
			return t;

		// Rising ease: integral of smoothstep(t/blend) scaled back into t.
		if (t < blend)
		{
			float u = t / blend;
			return blend * u * u * u * (1.0f - u * 0.5f);
		}

		// Constant middle: the rising ease contributed half of its span.
		if (t <= 1.0f - blend)
			return blend * 0.5f + (t - blend);

		// Falling ease: the mirror of the rise. The area still to come from here to the end is the
		// rising partial measured backwards, so what has accumulated is the full half minus that.
		float v = (1.0f - t) / blend;
		return blend * 0.5f + (1.0f - 2.0f * blend)
			   + blend * (0.5f - v * v * v * (1.0f - v * 0.5f));
	}

	/// <summary>
	/// One facet of sloped road, spanning from <paramref name="from"/> to <paramref name="to"/>
	/// given as (z, y) pairs, with a barrier down each side and a stripe up the middle.
	///
	/// The rotation is the angle of the chord, and everything is then offset along the facet's own
	/// up vector — that is what puts the road <i>surface</i> on the line between the two points
	/// rather than the slab's centre, and stands the walls up perpendicular to the road instead of
	/// leaning them against gravity.
	/// </summary>
	private void AddSlopedSlab(Vector2 from, Vector2 to, StandardMaterial3D road,
							   StandardMaterial3D wall, StandardMaterial3D line)
	{
		float dz = to.X - from.X;
		float dy = to.Y - from.Y;
		float span = Mathf.Sqrt(dz * dz + dy * dy);

		// A rotation of theta about X sends the slab's local +Z to (0, -sin, cos) and its local up
		// to (0, cos, sin). Matching the first to the chord is what gives the angle.
		float theta = Mathf.Atan2(dy, -dz);
		float degrees = Mathf.RadToDeg(theta);
		var rotation = new Vector3(degrees, 0.0f, 0.0f);
		var up = new Vector3(0.0f, Mathf.Cos(theta), Mathf.Sin(theta));

		var mid = new Vector3(0.0f, (from.Y + to.Y) * 0.5f, (from.X + to.X) * 0.5f);

		AddBox(new Vector3(Size, SlopeThickness, span), mid - up * (SlopeThickness * 0.5f), road,
			   rotationDegrees: rotation);

		foreach (int side in new[] { -1, 1 })
		{
			AddBox(new Vector3(WallThickness, WallHeight, span),
				   mid + up * (WallHeight * 0.5f) + new Vector3(side * WallInset, 0.0f, 0.0f),
				   wall, rotationDegrees: rotation);
		}

		AddBox(new Vector3(Size * 0.05f, 0.02f, span), mid + up * 0.011f, line,
			   rotationDegrees: rotation, collision: false);
	}

	// ---- Loop ----

	/// <summary>Facets the loop is built from. One every eighteen degrees.</summary>
	private const int LoopSegments = 20;

	/// <summary>Radius of the loop, so twice this tall. Wants to be big: the entry speed needed to
	/// stay on the inside of it scales with the square root of the radius, and a small loop is one
	/// a racer can crawl round.</summary>
	private const float LoopRadius = 26.0f;

	/// <summary>Width of the looping ribbon of road.</summary>
	private const float LoopWidth = 13.0f;

	/// <summary>
	/// How far the ribbon slides sideways over the full turn — it enters half this to the left of
	/// centre and leaves half this to the right.
	///
	/// This is what makes the loop buildable at all. A loop that came back to where it started
	/// would have its climb and its descent occupying the same air, and a racer on the way up would
	/// be driving into the underside of themselves on the way down. Sliding across as it goes keeps
	/// the two apart everywhere except the top, where they are the same piece of road anyway. It
	/// has to be wider than <see cref="LoopWidth"/> or they would still overlap near the bottom.
	/// </summary>
	private const float LoopDrift = 20.0f;

	/// <summary>Half the length of the hole under the loop, measured from the tile centre.</summary>
	private const float LoopMouth = 10.0f;

	/// <summary>
	/// Extra gravities of pin holding a car to the inside of the loop, on top of whatever its own
	/// speed is already supplying. This is the arcade in the arcade racer: the maths says a car
	/// needs about 130 km/h at the bottom to hold this radius on momentum alone, and the tire model
	/// was never asked to grip a surface rolled ninety degrees, so the loop leans on this.
	/// </summary>
	private const float LoopAssistG = 1.6f;

	/// <summary>
	/// Speed below which no help arrives, in m/s — 90 km/h. This one number is the difference
	/// between a loop and a decoration: above it the loop is a spectacle, below it the car peels
	/// off the ribbon and falls through the hole, which is the bargain a racer is being offered.
	/// </summary>
	private const float LoopMinSpeed = 25.0f;

	/// <summary>The volume that pins cars to the loop, or null on every tile that isn't one.</summary>
	private Area3D? _loopAssist;

	/// <summary>
	/// A drivable 360-degree loop, and the hole in the road that makes taking it compulsory.
	///
	/// The road runs in from the entry edge, stops at the near lip of the loop, and does not
	/// resume until the far lip — under the loop is open air. So there is nothing to bypass it
	/// along, and a racer who arrives too slowly to hold the inside drops through the middle. The
	/// mouth is off to one side and the right-hand half of the near lip is walled off, so the way
	/// in is a lane rather than a whole road, and missing the lane is its own mistake.
	///
	/// The pinning force that holds a car to the inside is in
	/// <see cref="ApplyLoopAssist"/> — this method only builds the shape.
	/// </summary>
	private void BuildLoop(TileDefinition definition)
	{
		StandardMaterial3D road = RoadMaterial();
		StandardMaterial3D wall = WallMaterial(definition.Accent);
		StandardMaterial3D line = LineMaterial();

		BuildLoopAprons(road, wall, line);

		for (int i = 0; i < LoopSegments; i++)
		{
			float a0 = Mathf.Tau * i / LoopSegments;
			float a1 = Mathf.Tau * (i + 1) / LoopSegments;

			AddLoopSegment(a0, a1, road, wall);
		}

		if (!_isGhost)
			BuildLoopAssistVolume();
	}

	/// <summary>Position on the loop's centre line at angle <paramref name="a"/>, zero at the near
	/// lip and running forward and up.</summary>
	private static Vector3 LoopPoint(float a) => new(
		-LoopDrift * 0.5f + LoopDrift * (a / Mathf.Tau),
		LoopRadius * (1.0f - Mathf.Cos(a)),
		LoopMouth - LoopRadius * Mathf.Sin(a) - LoopMouth * 2.0f * (a / Mathf.Tau));

	/// <summary>
	/// One facet of the ribbon, with a barrier along each side of it.
	///
	/// The facet's up vector points at the loop's axis, not at the sky — the car drives on the
	/// inside — which for the angle of the chord means the rotation is simply the angle round the
	/// loop. Halfway round, up points at the ground.
	/// </summary>
	private void AddLoopSegment(float a0, float a1, StandardMaterial3D road, StandardMaterial3D wall)
	{
		Vector3 p0 = LoopPoint(a0);
		Vector3 p1 = LoopPoint(a1);

		Vector3 mid = (p0 + p1) * 0.5f;
		float span = new Vector2(p1.Z - p0.Z, p1.Y - p0.Y).Length();

		float theta = (a0 + a1) * 0.5f;
		var rotation = new Vector3(Mathf.RadToDeg(theta), 0.0f, 0.0f);
		var up = new Vector3(0.0f, Mathf.Cos(theta), Mathf.Sin(theta));

		AddBox(new Vector3(LoopWidth, SlopeThickness, span), mid - up * (SlopeThickness * 0.5f), road,
			   rotationDegrees: rotation);

		float inset = LoopWidth * 0.5f - WallThickness * 0.5f;
		foreach (int side in new[] { -1, 1 })
		{
			AddBox(new Vector3(WallThickness, WallHeight, span),
				   mid + up * (WallHeight * 0.5f) + new Vector3(side * inset, 0.0f, 0.0f),
				   wall, rotationDegrees: rotation);
		}
	}

	/// <summary>
	/// The road either side of the hole: in from the entry edge to the near lip, and out from the
	/// far lip to the exit edge. Each carries its stripe over toward the lane it feeds, and the
	/// near lip is walled off everywhere except the mouth of the climb.
	/// </summary>
	private void BuildLoopAprons(StandardMaterial3D road, StandardMaterial3D wall,
								 StandardMaterial3D line)
	{
		float apron = HalfLength - LoopMouth;
		float apronZ = (HalfLength + LoopMouth) * 0.5f;
		float mouthX = -LoopDrift * 0.5f;

		foreach (int end in new[] { 1, -1 })
		{
			AddBox(new Vector3(Size, FloorThickness, apron),
				   new Vector3(0.0f, -FloorThickness * 0.5f, end * apronZ), road);

			// Barriers down the sides of the apron only. Across the hole there is nothing to
			// bolt them to.
			foreach (int side in new[] { -1, 1 })
			{
				AddBox(new Vector3(WallThickness, WallHeight, apron),
					   new Vector3(side * WallInset, WallHeight * 0.5f, end * apronZ), wall);
			}

			// The stripe slides across to the lane this apron feeds — the climb starts on one side
			// and the descent lands on the other, and this is the only warning of that.
			AddBox(new Vector3(Size * 0.05f, 0.02f, apron),
				   new Vector3(end * mouthX, 0.011f, end * apronZ), line, collision: false);
		}

		// Two boost pads up the entry apron, sitting in the mouth's lane rather than the middle of
		// the road. They do both of the jobs the run-up has to do: find the speed to hold the
		// inside of the loop, and be in the one lane that leads up into it.
		for (int i = 0; i < 2; i++)
		{
			AddImpulsePad(new Vector2(LoopWidth, LoopWidth),
						  new Vector3(mouthX, 0.0f, LoopMouth + apron * (0.3f + i * 0.4f)),
						  BoostMaterial(), () => -GlobalBasis.Z, LoopBoostImpulse);
		}

		// Wall off the near lip except for the mouth of the climb, so the only way forward is up.
		float openFrom = mouthX - LoopWidth * 0.5f;
		float openTo = mouthX + LoopWidth * 0.5f;
		AddLipWall(-Half, openFrom, wall);
		AddLipWall(openTo, Half, wall);
	}

	/// <summary>A stretch of barrier across the near lip of the loop, between two X coordinates.</summary>
	private void AddLipWall(float fromX, float toX, StandardMaterial3D wall)
	{
		float width = toX - fromX;
		if (width <= 0.0f)
			return;

		AddBox(new Vector3(width, WallHeight, WallThickness),
			   new Vector3((fromX + toX) * 0.5f, WallHeight * 0.5f, LoopMouth), wall);
	}

	/// <summary>
	/// The volume inside which a car is held onto the loop. Sized to swallow the whole ribbon; the
	/// force itself is what decides whether a given car is being helped.
	/// </summary>
	private void BuildLoopAssistVolume()
	{
		_loopAssist = new Area3D
		{
			Name = "LoopAssist",
			Position = new Vector3(0.0f, LoopRadius, 0.0f),
			CollisionMask = uint.MaxValue,
		};
		_loopAssist.AddChild(new CollisionShape3D
		{
			Shape = new BoxShape3D
			{
				Size = new Vector3(LoopDrift + LoopWidth * 2.0f, LoopRadius * 2.2f, LoopRadius * 2.4f),
			},
		});
		AddChild(_loopAssist);

		// No moving parts on this tile, so nothing else has woken the physics step up.
		SetPhysicsProcess(true);
	}

	/// <summary>
	/// Press every car inside the loop outward, away from its axis and so into the road, as long as
	/// it is carrying enough speed to deserve the help. Called from the physics step.
	///
	/// The axis is taken as running across the tile at the height of the loop's centre, which is an
	/// approximation — the ribbon slides forward as it goes round, so the true centre drifts with
	/// it. The force ends up tilted by up to twenty degrees at the extremes, which still presses
	/// the car into the road, and getting it exact would mean solving for where round the loop the
	/// car is.
	/// </summary>
	private void ApplyLoopAssist()
	{
		// A tile still falling is sweeping its assist volume through the air above the track.
		if (_loopAssist == null || _fallRemaining > 0.0f)
			return;

		foreach (Node3D node in _loopAssist.GetOverlappingBodies())
		{
			if (node is not RigidBody3D car || car.LinearVelocity.Length() < LoopMinSpeed)
				continue;

			Vector3 local = ToLocal(car.GlobalPosition);
			var radial = new Vector3(0.0f, local.Y - LoopRadius, local.Z);
			if (radial.LengthSquared() < 1.0f)
				continue;

			Vector3 outward = GlobalBasis * radial.Normalized();
			car.ApplyCentralForce(outward * (LoopAssistG * 9.8f * car.Mass));
		}
	}
}
