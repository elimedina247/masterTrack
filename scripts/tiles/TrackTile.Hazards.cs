using System;
using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.Tiles;

/// <summary>
/// The still hazards: everything a tile can carry that is built once and then just sits there.
/// Moving parts live in <c>TrackTile.Moving.cs</c> and tiles whose whole shape is the hazard —
/// hairpins, ramps, the loop — in <c>TrackTile.Shapes.cs</c>.
///
/// Every measurement here obeys the rule set out on the core partial: the road's own dimensions
/// scale with the tile, and anything the car has to fit through, clear or drop into is in
/// car-scale metres so that widening the road never quietly makes a hazard easier.
///
/// Two of these hazards act on the car with a force rather than by being in the way — the launch
/// pads and the boost pads. Both do it from the tile's own side, through a trigger volume that
/// hands the car an impulse, so nothing about the vehicle needed changing to make them work.
/// </summary>
public partial class TrackTile
{
	/// <summary>Length of the hole in a <see cref="TileHazard.Gap"/> tile, along the direction
	/// of travel. Car-scale: whether it can be cleared is a question of speed and gravity.</summary>
	private const float GapLength = 6.0f;

	/// <summary>
	/// Upward impulse a launch pad gives, as a multiple of the car's own weight-second — so it
	/// throws every car to the same height whatever it masses. About 28 m up at this value, which
	/// is a long way to be in the air and a long time to be steering nothing.
	/// </summary>
	private const float LaunchImpulse = 24.0f;

	/// <summary>Forward impulse a boost pad gives, in the same units. Roughly 18 m/s of free speed.</summary>
	private const float BoostImpulse = 18.0f;

	/// <summary>
	/// Forward impulse on the pad built into a tall ramp, in the same units.
	///
	/// Bigger than a boost tile's, because it is doing a specific job: eighty metres of climb costs
	/// 40 m/s of speed in potential energy alone, so a car arriving at anything less than about
	/// 150 km/h simply stops partway up a hill it cannot then turn around on. This is what makes
	/// the tall ramp a tile rather than a wall.
	/// </summary>
	private const float ClimbBoostImpulse = 26.0f;

	/// <summary>
	/// Forward impulse from each of the two pads on the loop's run-up. Two of them, because the
	/// loop needs about 128 km/h at the bottom to be held on momentum and the run-up is only fifty
	/// metres long — not enough road to build it on the throttle alone.
	/// </summary>
	private const float LoopBoostImpulse = 20.0f;

	/// <summary>
	/// The colour boost wears, wherever it turns up. Shared rather than taken from each tile's own
	/// accent: a racer learns once that these chevrons mean speed, and then has to recognise them
	/// at 200 km/h on a tile whose colour is busy saying something else.
	/// </summary>
	private static readonly Color BoostAccent = new(0.10f, 1.00f, 0.72f);

	/// <summary>
	/// How much of the tile an <see cref="TileHazard.IcePatch"/> covers. Scales with the tile
	/// rather than the car: ice is a surface, so on a longer tile it should be a longer sheet,
	/// not the same short patch adrift in the middle of a straight.
	/// </summary>
	private float IceLength => Length * 0.5f;

	/// <summary>How much of the tile a <see cref="TileHazard.Gravel"/> bed covers.</summary>
	private float GravelLength => Length * 0.6f;

	/// <summary>How much of the tile the <see cref="TileHazard.SplitTrack"/> chasm covers.</summary>
	private float ChasmLength => Length * 0.62f;

	/// <summary>
	/// Width of each ledge left along the walls by a split. Car-scale and unapologetically tight:
	/// the tile is asking whether you can hold a line for seventy metres, so the answer has to be
	/// in doubt. Two of these plus the chasm is narrower than the road, which is why the ledges sit
	/// against the walls rather than floating.
	/// </summary>
	private const float LedgeWidth = 9.0f;

	private void BuildFloor(TileDefinition definition)
	{
		switch (Data.Hazard)
		{
			case TileHazard.Gap:
				AddAprons(GapLength, RoadMaterial());
				break;

			case TileHazard.IcePatch:
				// The ice replaces the middle of the road rather than sitting on top of it,
				// so there's no lip for the suspension to trip over.
				AddAprons(IceLength, RoadMaterial());
				BuildIcePatch();
				break;

			case TileHazard.Gravel:
				AddAprons(GravelLength, RoadMaterial());
				BuildSurfacePatch("GravelSurface", SurfaceGroups.Dirt, GravelLength, GravelMaterial());
				break;

			case TileHazard.SplitTrack:
				BuildSplitFloor();
				break;

			default:
				AddBox(new Vector3(Size, FloorThickness, Length), new Vector3(0, -FloorThickness * 0.5f, 0),
					   RoadMaterial());
				break;
		}
	}

	/// <summary>
	/// Road either side of a centred feature of the given length: the entry apron a racer
	/// arrives on and the exit apron they have to reach.
	/// </summary>
	private void AddAprons(float featureLength, StandardMaterial3D material)
	{
		float length = (Length - featureLength) * 0.5f;
		float offset = (Length - length) * 0.5f;

		AddBox(new Vector3(Size, FloorThickness, length),
			   new Vector3(0, -FloorThickness * 0.5f, offset), material);
		AddBox(new Vector3(Size, FloorThickness, length),
			   new Vector3(0, -FloorThickness * 0.5f, -offset), material);
	}

	/// <summary>
	/// The ice needs its own body so it can carry its own surface group — the tire model
	/// looks the group up on whatever the wheel ray actually hits.
	/// </summary>
	private void BuildIcePatch()
		=> BuildSurfacePatch("IceSurface", SurfaceGroups.Ice, IceLength, IceMaterial());

	/// <summary>
	/// A stretch of road that grips differently. Its own body, because the wheel ray reads the
	/// surface group off whatever it actually hit, and set into the road rather than laid on top
	/// so there is no lip at the edge of it.
	/// </summary>
	private void BuildSurfacePatch(string name, string surfaceGroup, float length,
								   StandardMaterial3D material)
	{
		var body = new StaticBody3D { Name = name };
		if (!_isGhost)
			body.AddToGroup(surfaceGroup);
		AddChild(body);

		AddBox(new Vector3(Size, FloorThickness, length), new Vector3(0, -FloorThickness * 0.5f, 0),
			   material, parent: body);
	}

	private void BuildHazard(TileDefinition definition)
	{
		switch (Data.Hazard)
		{
			case TileHazard.JumpAhead:
				BuildJumpRamp(definition);
				break;

			case TileHazard.Bottleneck:
				BuildBottleneck(definition);
				break;

			case TileHazard.SplitTrack:
				BuildSplitMarkings();
				break;

			case TileHazard.LaunchPad:
				BuildLaunchPads(definition);
				break;

			case TileHazard.BoostPad:
				BuildBoostPads(definition);
				break;

			case TileHazard.Slalom:
				BuildSlalom(definition);
				break;

			case TileHazard.Whoops:
				BuildWhoops(definition);
				break;

			case TileHazard.LogTrap:
				BuildLogTrap(definition);
				break;

			case TileHazard.Crusher:
				BuildCrushers(definition);
				break;

			case TileHazard.Spinner:
				BuildSpinner(definition);
				break;
		}
	}

	private void BuildJumpRamp(TileDefinition definition)
	{
		// A take-off ramp rising toward the exit. It spans the road so it can't be driven around,
		// but its run-up and rise are car-scale — a ramp four times longer at the same angle would
		// fire the car off the far end of the track.
		const float angle = 12.0f;
		const float length = 10.0f;
		const float thickness = 0.4f;
		float rise = length * 0.5f * Mathf.Sin(Mathf.DegToRad(angle));

		// Sunk so the low lip sits flush with the road; the take-off edge ends up about 2 m up.
		// Centred, which on a long tile is the point: the run-up is what lets a racer choose the
		// speed they take it at, and the run-out is what they land on.
		AddBox(new Vector3(Size * 0.9f, thickness, length),
			   new Vector3(0, rise - thickness * 0.5f + 0.02f, 0),
			   RampMaterial(definition.Accent), rotationDegrees: new Vector3(angle, 0, 0));
	}

	private void BuildBottleneck(TileDefinition definition)
	{
		// Intrusions from both walls leaving a car-scale slot down the middle. The slot
		// deliberately doesn't scale with the road: on a wide tile that's what turns this from a
		// decoration into a squeeze racers have to fight over.
		//
		// The depth is a fraction of one cell rather than of the whole tile, so the pinch stays a
		// pinch — a squeeze that went on for a hundred metres would just be a narrow road.
		const float slot = 7.0f;
		const float depth = Size * 0.35f;
		const float width = (Size - slot) * 0.5f;
		const float offset = (Size + slot) * 0.25f;

		StandardMaterial3D material = WallMaterial(definition.Accent);
		AddBox(new Vector3(width, WallHeight, depth), new Vector3(offset, WallHeight * 0.5f, 0), material);
		AddBox(new Vector3(width, WallHeight, depth), new Vector3(-offset, WallHeight * 0.5f, 0), material);
	}

	// ---- Split Track ----

	/// <summary>
	/// The middle of the road falls away and leaves a ledge along each wall. Full-width aprons at
	/// both ends, because a racer needs somewhere to pick a side from and somewhere to rejoin.
	///
	/// Read as ledges rather than as literal wall-riding: holding a car against a vertical face
	/// would need the tire model to grip something other than the ground, and the vehicle was to
	/// be left alone.
	/// </summary>
	private void BuildSplitFloor()
	{
		AddAprons(ChasmLength, RoadMaterial());

		float ledgeX = Half - LedgeWidth * 0.5f;
		StandardMaterial3D material = RoadMaterial();

		AddBox(new Vector3(LedgeWidth, FloorThickness, ChasmLength),
			   new Vector3(ledgeX, -FloorThickness * 0.5f, 0), material);
		AddBox(new Vector3(LedgeWidth, FloorThickness, ChasmLength),
			   new Vector3(-ledgeX, -FloorThickness * 0.5f, 0), material);
	}

	/// <summary>
	/// Two lines instead of one, down the middle of each ledge. The centre line this replaces
	/// would have been painted straight down the middle of the hole.
	/// </summary>
	private void BuildSplitMarkings()
	{
		const float width = Size * 0.05f;
		const float y = 0.011f;
		float ledgeX = Half - LedgeWidth * 0.5f;

		StandardMaterial3D material = LineMaterial();
		AddBox(new Vector3(width, 0.02f, Length), new Vector3(ledgeX, y, 0), material, collision: false);
		AddBox(new Vector3(width, 0.02f, Length), new Vector3(-ledgeX, y, 0), material, collision: false);
	}

	// ---- Impulse pads ----

	/// <summary>
	/// Sprung pads set flush into the road. Drive over one and it throws the car into the air,
	/// where it keeps whatever speed it arrived with and none of its steering.
	///
	/// Three of them, staggered corner to corner, so there is a way through for a racer who spots
	/// them early and no way through for one who doesn't look.
	/// </summary>
	private void BuildLaunchPads(TileDefinition definition)
	{
		var pad = new Vector2(11.0f, 11.0f);
		float lateral = Size * 0.28f;
		float along = Length * 0.2f;

		StandardMaterial3D material = RampMaterial(definition.Accent);

		AddImpulsePad(pad, new Vector3(-lateral, 0.0f, along), material, () => Vector3.Up, LaunchImpulse);
		AddImpulsePad(pad, new Vector3(0.0f, 0.0f, 0.0f), material, () => Vector3.Up, LaunchImpulse);
		AddImpulsePad(pad, new Vector3(lateral, 0.0f, -along), material, () => Vector3.Up, LaunchImpulse);
	}

	/// <summary>
	/// Arrows down the middle that slam the car forward. The one tile in the catalog that is pure
	/// gift — everything else the Track Master holds is a way to slow the race down, and a hand of
	/// nothing but punishment turns into a track nobody can drive.
	///
	/// Boost is along the tile's own forward, not the car's, so it cannot be farmed by arriving
	/// sideways: crossing the pad at an angle throws the car straight down the track and leaves it
	/// pointing the wrong way.
	/// </summary>
	private void BuildBoostPads(TileDefinition definition)
	{
		var pad = new Vector2(14.0f, 14.0f);
		float along = Length * 0.22f;

		StandardMaterial3D material = BoostMaterial();

		for (int i = -1; i <= 1; i++)
		{
			AddImpulsePad(pad, new Vector3(0.0f, 0.0f, -i * along), material,
						  () => -GlobalBasis.Z, BoostImpulse);
		}
	}

	/// <summary>
	/// One pad: a flat plate in the road, and a trigger box standing on it that hands an impulse
	/// to whatever drives in.
	///
	/// <paramref name="direction"/> is evaluated at the moment of the trigger and in world space,
	/// so a pad can push along the tile's forward without having to know its own rotation. The
	/// impulse scales with the car's mass, which makes it a change in velocity rather than a
	/// change in momentum — every car leaves a pad at the same speed.
	///
	/// Ghosts get the plate but no trigger. A preview that launched cars would be a preview that
	/// changed the race.
	/// </summary>
	/// <param name="size">Across the road, then along it. Separate, because a pad the racer must
	/// not be able to drive around is a different shape from one they should be able to.</param>
	private void AddImpulsePad(Vector2 size, Vector3 position, StandardMaterial3D material,
							   Func<Vector3> direction, float impulse,
							   Vector3 rotationDegrees = default, Vector3? surfaceUp = null)
	{
		const float plateThickness = 0.5f;
		const float triggerHeight = 5.0f;

		// Which way is "off the road" here. Straight up on a flat tile, but a pad set into a ramp
		// has to be sunk into the slope it lies on, not into the horizon.
		Vector3 up = surfaceUp ?? Vector3.Up;

		// Set into the road with 2 cm proud, enough to be seen from board altitude and not enough
		// for the suspension to read as a bump.
		AddBox(new Vector3(size.X, plateThickness, size.Y),
			   position + up * (0.02f - plateThickness * 0.5f),
			   material, rotationDegrees: rotationDegrees, collision: false);

		if (_isGhost)
			return;

		var trigger = new Area3D
		{
			Name = "PadTrigger",
			Position = position + up * (triggerHeight * 0.5f),
			// Everything: the pad should fire for anything that can drive, and which layer the
			// car scene happens to sit on is not this tile's business.
			CollisionMask = uint.MaxValue,
		};
		trigger.AddChild(new CollisionShape3D
		{
			Shape = new BoxShape3D { Size = new Vector3(size.X, triggerHeight, size.Y) },
		});
		AddChild(trigger);

		trigger.BodyEntered += body =>
		{
			// A tile still on its way down must not fire. It is sweeping through the air above the
			// track, and anything it passes on the way would be launched by a tile that has not
			// been placed yet.
			if (_fallRemaining > 0.0f || body is not RigidBody3D car)
				return;

			car.ApplyCentralImpulse(direction().Normalized() * impulse * car.Mass);
		};
	}

	// ---- Slalom and whoops ----

	/// <summary>
	/// Blocks alternating either side of the road, close enough together that they have to be
	/// threaded rather than aimed at. No trick to it and nothing that pushes back — after a
	/// catalog of traps this is the one tile that just asks whether you can drive.
	/// </summary>
	private void BuildSlalom(TileDefinition definition)
	{
		const float blockDepth = 4.0f;
		const float blockHeight = 2.4f;
		float blockWidth = Size * 0.34f;
		float lateral = (Size - blockWidth) * 0.5f;

		StandardMaterial3D material = WallMaterial(definition.Accent);

		// Four gates from the entry end to the exit end, each forcing the opposite way to the last.
		for (int i = 0; i < 4; i++)
		{
			float t = (i + 0.5f) / 4.0f;
			float z = HalfLength - t * Length;
			float x = i % 2 == 0 ? lateral : -lateral;

			AddBox(new Vector3(blockWidth, blockHeight, blockDepth),
				   new Vector3(x, blockHeight * 0.5f, z), material);
		}
	}

	/// <summary>
	/// A washboard across the full width of the road. Sized off the car rather than the tile —
	/// what makes whoops whoops is how they land against the wheelbase and the dampers, which do
	/// not care how wide the track is.
	///
	/// Ridges a little taller and further apart than the proving ground's bump strip, because this
	/// one is meant to be hit at racing speed and to unsettle the car when it is.
	/// </summary>
	private void BuildWhoops(TileDefinition definition)
	{
		const float ridgeHeight = 0.22f;
		const float ridgeDepth = 1.6f;
		const float spacing = 7.0f;

		StandardMaterial3D material = RampMaterial(definition.Accent);

		int count = Mathf.FloorToInt(Length * 0.78f / spacing);
		float first = -(count - 1) * spacing * 0.5f;

		for (int i = 0; i < count; i++)
		{
			AddBox(new Vector3(Size, ridgeHeight, ridgeDepth),
				   new Vector3(0.0f, ridgeHeight * 0.5f, first + i * spacing), material);
		}
	}
}
