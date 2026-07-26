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
	/// A 180-degree turn: the racer arrives up one lane, sweeps round an apex at the far end, and
	/// comes back down a second lane alongside the first, heading the way they came.
	///
	/// Two cells side by side in local space — the entry lane on the origin, the outgoing lane one
	/// cell to whichever side <see cref="TileData.TurnSide"/> points at — so both openings are on
	/// the +Z edge and every wall below is either the outer shell or the apex between the lanes.
	///
	/// The apex is the tile. Without a barrier between the lanes this would be an eighty metre bay
	/// that a racer could clip the corner of and drive straight out of; with one they have to go
	/// to the far end and turn around, which is what a hairpin costs.
	/// </summary>
	private void BuildHairpin(TileDefinition definition)
	{
		int side = Data.TurnSide;

		// Centre of the outgoing lane, one cell to the side of the entry lane.
		float lane = side * Size;

		// How far the apex reaches back from the open edge. Half the cell leaves the far half of
		// both lanes clear to turn around in.
		const float apex = Size * 0.5f;

		// Both lanes as one slab: two cells across, one deep, centred between them.
		AddBox(new Vector3(Size * 2.0f, FloorThickness, Size),
			   new Vector3(lane * 0.5f, -FloorThickness * 0.5f, 0), RoadMaterial());

		StandardMaterial3D wall = WallMaterial(definition.Accent);

		// The outer shell: across the far end of both lanes, then down the outside of each.
		AddBox(new Vector3(Size * 2.0f, WallHeight, WallThickness),
			   new Vector3(lane * 0.5f, WallHeight * 0.5f, -LengthInset), wall);
		AddBox(new Vector3(WallThickness, WallHeight, Size),
			   new Vector3(-side * WallInset, WallHeight * 0.5f, 0), wall);
		AddBox(new Vector3(WallThickness, WallHeight, Size),
			   new Vector3(lane + side * WallInset, WallHeight * 0.5f, 0), wall);

		// The apex, on the boundary the two lanes share, reaching back from the open edge.
		AddBox(new Vector3(WallThickness, WallHeight, apex),
			   new Vector3(side * Half, WallHeight * 0.5f, Half - apex * 0.5f), wall);

		BuildHairpinRacingLine(lane);
	}

	/// <summary>
	/// The racing line as a U: up the entry lane, across the open end past the apex, and back down
	/// the outgoing lane. Same job as <see cref="BuildRacingLine"/> — from board altitude this is
	/// what tells the Track Master which way the track leaves a tile.
	/// </summary>
	private void BuildHairpinRacingLine(float lane)
	{
		const float width = Size * 0.05f;
		const float y = 0.011f;

		// The crossover sits north of the apex, in the clear half of the tile — drawn anywhere
		// south of it the line would run through a wall.
		const float crossZ = -Half * 0.5f;
		const float legLength = Half - crossZ;
		const float legZ = (Half + crossZ) * 0.5f;

		StandardMaterial3D material = LineMaterial();

		AddBox(new Vector3(width, 0.02f, legLength), new Vector3(0, y, legZ), material, collision: false);
		AddBox(new Vector3(Mathf.Abs(lane), 0.02f, width), new Vector3(lane * 0.5f, y, crossZ), material,
			   collision: false);
		AddBox(new Vector3(width, 0.02f, legLength), new Vector3(lane, y, legZ), material, collision: false);
	}

	// ---- Ramps ----

	/// <summary>How many facets a ramp is built from. Enough that the eased profile reads as a
	/// curve and the car does not feel the joints.</summary>
	private const int RampSegments = 8;

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

		for (int i = 0; i < RampSegments; i++)
		{
			float t0 = (float)i / RampSegments;
			float t1 = (float)(i + 1) / RampSegments;

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
	/// Height of the ramp surface a fraction <paramref name="t"/> of the way from entry to exit.
	///
	/// Smoothstep: flat at both ends, so the joints with the neighbouring tiles are tangential.
	/// </summary>
	private static float RampHeightAt(float t, float rise) => rise * t * t * (3.0f - 2.0f * t);

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
