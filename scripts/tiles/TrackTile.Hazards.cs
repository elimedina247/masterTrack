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
	/// <summary>
	/// Length of the hole in a <see cref="TileHazard.Gap"/> tile, along the direction of travel.
	/// Car-scale: whether it can be cleared is a question of speed and gravity.
	///
	/// A flat gap is never <i>jumped</i> — there is no lip to leave the ground from, so the car is
	/// simply a projectile the moment the road stops and it clears the hole only if it does not
	/// drop its own ride height (0.55 m) on the way across. At 2 g that is 0.226 s of fall, which
	/// is <b>12.6 m at <c>TopSpeed</c></b> and 22.6 m on a chained boost.
	///
	/// So this number is a speed check, and 6 m put it far below anything that could fail it: a car
	/// at 200 km/h dropped under three centimetres crossing it, which is not a hazard, it is a
	/// paint stripe. At 14 m the threshold lands just above top speed — flat out you skim it, lift
	/// to 180 km/h and you go in. A longer gap needs a take-off lip like <c>BuildJumpRamp</c>'s
	/// wedge, at which point it is a jump and not a gap.
	/// </summary>
	private const float GapLength = 14.0f;

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
	/// Bigger than a boost tile's, because it is doing a specific job. Two cubes is 72 m of climb,
	/// and at the 2 g the racer runs under that costs 53 m/s of speed in potential energy alone —
	/// which is essentially <c>TopSpeed</c>. A car that arrives at anything less than flat out does
	/// not coast up this hill, it stops partway up one it cannot then turn around on. This is what
	/// makes the tall ramp a tile rather than a wall.
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
	///
	/// These three fractions are what is left of the tile once the hazard has had its say, and the
	/// leftover is the game's dead air. At half a tile the ice was 40 m of sheet with 60 m of plain
	/// road wrapped round it — the racer was off the hazard and coasting for most of the time they
	/// spent on the tile. The aprons still have to exist, because a hazard you meet on the tile
	/// boundary is one you were never given the chance to set up for, but they only have to be long
	/// enough to see the thing coming: at 60 m/s a fifth of a 162 m tile is half a second, which is
	/// about a reaction. Everything past that was the tile waiting for you.
	/// </summary>
	private float IceLength => Length * 0.72f;

	/// <summary>How much of the tile a <see cref="TileHazard.Gravel"/> bed covers.</summary>
	private float GravelLength => Length * 0.75f;

	/// <summary>How much of the tile the <see cref="TileHazard.SplitTrack"/> chasm covers.</summary>
	private float ChasmLength => Length * 0.75f;

	/// <summary>
	/// Width of each ledge left along the walls by a split. Car-scale and unapologetically tight:
	/// the tile is asking whether you can hold a line for a hundred and twenty metres, so the
	/// answer has to be in doubt. Two of these plus the chasm is narrower than the road, which is
	/// why the ledges sit against the walls rather than floating.
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

			case TileHazard.Squiggle:
				BuildSquiggleRunoff();
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

			case TileHazard.Squiggle:
				BuildSquiggle(definition);
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
	///
	/// <b>The spacing is tuned to resonance, not to look right.</b> The suspension's natural
	/// frequency is <c>sqrt(4 × SpringStrength / Mass)</c> — 16.73 rad/s, or <b>2.66 Hz</b>, on the
	/// racer's 84000 N/m springs and 1200 kg. At <c>TopSpeed</c> that is one ridge every 20.9 m.
	///
	/// This is the whole tile. At 7 m the ridges arrived at 7.9 Hz, three times above resonance,
	/// and the springs filtered them out completely: whoops did nothing at all above 70 km/h, which
	/// is the only speed anybody meets them at. Sitting them <i>on</i> resonance instead means each
	/// ridge arrives exactly as the car is coming back down off the last one, so the excursion
	/// compounds — and it stops compounding the moment the driver comes off the throttle. One tile
	/// that punishes precisely one speed band and goes quiet if you lift.
	///
	/// The height is sized against the travel for the same reason. Static compression is 0.14 m of
	/// a 0.48 m stroke, so 0.40 m of ridge at resonance reaches the bump stop, which is what "the
	/// car never settles between them" has to mean if it is to mean anything.
	/// </summary>
	private void BuildWhoops(TileDefinition definition)
	{
		const float ridgeHeight = 0.40f;
		const float ridgeDepth = 1.6f;
		const float spacing = 21.0f;

		StandardMaterial3D material = RampMaterial(definition.Accent);

		int count = Mathf.FloorToInt(Length * 0.78f / spacing);
		float first = -(count - 1) * spacing * 0.5f;

		for (int i = 0; i < count; i++)
		{
			AddBox(new Vector3(Size, ridgeHeight, ridgeDepth),
				   new Vector3(0.0f, ridgeHeight * 0.5f, first + i * spacing), material);
		}
	}

	// ---- Squiggle ----

	/// <summary>
	/// Width of the squiggle's tarmac ribbon, as a fraction of the tile.
	///
	/// This and <see cref="SquiggleWaves"/> are the tile, and they pull against each other in a way
	/// that is not obvious: a <i>wider</i> ribbon is an easier tile twice over. It leaves less room
	/// for the road to swing, and it gives the driver more room to straighten the swing that is
	/// left. Past about 0.45 the second effect wins outright and the tile can be driven in a
	/// straight line — it stops being a hazard and becomes a decoration.
	///
	/// At 0.35 the ribbon is 18.9 m, which is still nine car widths. Nothing here is tight; what is
	/// asked is that the car change direction, three times, at speed.
	/// </summary>
	private const float SquiggleWidth = 0.35f;

	/// <summary>
	/// Sine waves the ribbon makes between entry and exit. 1.5 puts three bends on the tile — a
	/// small one in, a committed one through the middle, a small one out — which is a rhythm rather
	/// than a single lane change, and leaves the hard part where the racer has had the length of
	/// the tile to see it coming.
	///
	/// It does not go up. Two waves shortens the wavelength by a quarter and cornering load goes as
	/// its square, which would take the same ribbon from 6 g to 10.7 against a car that has 8.5.
	/// </summary>
	private const float SquiggleWaves = 1.5f;

	/// <summary>
	/// Fraction of the tile spent opening the mouth out to the full width between the walls at
	/// each end. Without it the racer meets a 19 m ribbon head-on off a 49 m road, which is a wall
	/// with a hole in it rather than a road that narrows.
	/// </summary>
	private const float SquiggleFlare = 0.15f;

	/// <summary>Slabs the ribbon is built from.</summary>
	private const int SquiggleSegments = 32;

	/// <summary>
	/// How much each slab is grown past the chord it spans, and it has to be far more generous
	/// than the corners' <c>FacetOverlap</c>.
	///
	/// Same problem, much worse case. Consecutive slabs share their corners but are rectangles
	/// about their own midpoints, so a yawed joint leaves a wedge open at the outside of it — and
	/// the wedge is proportional to the <i>width</i> of the slab. A banked turn is sliced into nine
	/// narrow strips across the road, so its wedges are small and 6% swallows them. A squiggle is
	/// one slab the full width of the ribbon, at joints yawing three times as hard, which opens a
	/// 2.5 m wedge that 6% does not come close to closing.
	///
	/// 70% closes it with room to spare. It is affordable here only because of what is underneath:
	/// the ribbon is laid <i>on</i> the runoff rather than being the floor itself, so a slab that
	/// overshoots is tarmac over dirt and a slab that fell short would be dirt showing through.
	/// Neither is a hole. The overlapping tops are the same colour, the same material and the same
	/// flat normal, so there is nothing there to z-fight visibly either.
	/// </summary>
	private const float SquiggleOverlap = 1.7f;

	/// <summary>
	/// Metres of daylight left between the ribbon's widest point and the wall. Covers the corner of
	/// an over-long slab swinging out past the ribbon's nominal edge at a hard joint, which is
	/// about 10 cm — the rest is margin, because a slab that intersected the barrier would leave a
	/// stripe of road inside a wall.
	/// </summary>
	private const float SquiggleClearance = 1.0f;

	/// <summary>
	/// How far the runoff sits below the tarmac. Small on purpose: the ribbon has to stay level with
	/// the tiles either side of it, so the drop goes into the runoff rather than the road being
	/// raised, and at 6 cm it is a kerb the car notices without being one it trips over. It also
	/// keeps the two surfaces off the same plane, which is what stops the dirt z-fighting through
	/// the road it is underneath.
	/// </summary>
	private const float SquiggleRunoffDrop = 0.06f;

	/// <summary>
	/// The floor of a squiggle: the whole cell in dirt, with the road laid on top of it by
	/// <see cref="BuildSquiggle"/>.
	///
	/// Built the wrong way round on purpose. The obvious construction is a ribbon of road over
	/// nothing, and it cannot be made to work: the slab joints leave wedges the overlap cannot
	/// close without a lateral subdivision that costs more collision shapes than a hairpin, and
	/// every one of those wedges would be a hole in the road at the exact place the car is closest
	/// to leaving it. Flooring the cell first turns every one of those failures from a hole into a
	/// patch of dirt.
	/// </summary>
	private void BuildSquiggleRunoff()
	{
		var body = new StaticBody3D { Name = "SquiggleRunoff" };
		if (!_isGhost)
			body.AddToGroup(SurfaceGroups.Dirt);
		AddChild(body);

		AddBox(new Vector3(Size, FloorThickness, Length),
			   new Vector3(0.0f, -FloorThickness * 0.5f - SquiggleRunoffDrop, 0.0f),
			   GravelMaterial(), parent: body);
	}

	/// <summary>
	/// A road that snakes, with dirt either side of it instead of a barrier.
	///
	/// The slalom asks the same question with blocks on a full-width straight; this one asks it of
	/// the road itself, so there is no gate to aim at and no line through it that is not a curve.
	/// What it costs to drive, in numbers: nobody drives the centreline — it peaks at 37 degrees
	/// off the tile's axis and would ask 20 g — because the ribbon is wide enough to cut across.
	/// Straightening it as far as the width allows leaves an effective amplitude of 5.7 m on a
	/// 108 m wavelength, and that is the real tile:
	///
	/// - 3.1 g at 145 km/h, which is a lift-and-flow;
	/// - <b>6.0 g at <c>TopSpeed</c></b>, against an 8.5 g <c>MaxGripForce</c> cap — flat out and
	///   unboosted it goes, with just enough left over to get it wrong;
	/// - 9.6 g on a drift boost and 19.6 g on a chained one, both over the cap and therefore not a
	///   matter of skill. Arrive boosted and the tile has already decided.
	///
	/// That last line is the point of it. Every other hazard punishes a racer for being slow or
	/// clumsy; this is the one that punishes them for the boost they just earned, unless they spend
	/// it before they get here. And the punishment is dirt rather than a fall — you keep the car,
	/// you lose the corner and the speed you were carrying, and you are still in the race. It costs
	/// something even clean: 176 m of ribbon across a 162 m tile.
	/// </summary>
	private void BuildSquiggle(TileDefinition definition)
	{
		float ribbon = Size * SquiggleWidth;

		// The most swing the cell can hold, once the barriers and a slab's overshoot have been
		// paid for.
		float swing = Half - WallThickness - ribbon * 0.5f - SquiggleClearance;

		// The mouths open out to the full width between the walls, which is what the tile either
		// side of this one actually offers.
		float mouth = Size - WallThickness * 2.0f;
		float flareFloor = Mathf.Sin(Mathf.Pi * SquiggleFlare);

		// Lateral offset of the ribbon's centreline a fraction t from entry to exit.
		//
		// The sin(pi t) envelope is the load-bearing part, and it is the same trick BankScale plays
		// on the corners: it takes the offset *and its slope* to zero at both ends, so the ribbon
		// leaves and rejoins the straights either side dead centre and pointing dead straight. A
		// bare sine would arrive centred but at an angle, which is a kink in the road at the one
		// place the racer cannot see it coming.
		float Offset(float t)
			=> swing * Mathf.Sin(Mathf.Pi * t) * Mathf.Sin(Mathf.Tau * SquiggleWaves * t);

		float WidthAt(float t)
			=> Mathf.Lerp(mouth, ribbon,
						  Mathf.Min(1.0f, Mathf.Sin(Mathf.Pi * t) / flareFloor));

		// Its own body so it can carry its own surface group: the wheel ray reads the group off
		// whatever it actually hit, so this is what makes the ribbon grip and the rest of the cell
		// not. See BuildSurfacePatch, which does the same thing for a straight patch.
		var road = new StaticBody3D { Name = "SquiggleRoad" };
		if (!_isGhost)
			road.AddToGroup(SurfaceGroups.Road);
		AddChild(road);

		StandardMaterial3D tarmac = RoadMaterial();
		StandardMaterial3D edge = WallMaterial(definition.Accent);

		for (int k = 0; k < SquiggleSegments; k++)
		{
			float t0 = (float)k / SquiggleSegments;
			float t1 = (float)(k + 1) / SquiggleSegments;

			float x0 = Offset(t0);
			float x1 = Offset(t1);
			float z0 = HalfLength - t0 * Length;
			float z1 = HalfLength - t1 * Length;

			// Yaw that turns the slab's own +Z onto the chord. Measured back toward the entry so
			// the angle stays near zero — a box is symmetric end to end, so which way along the
			// chord it is measured makes no difference to the shape.
			float yaw = Mathf.Atan2(x0 - x1, z0 - z1);
			var rotation = new Vector3(0.0f, Mathf.RadToDeg(yaw), 0.0f);

			float chord = new Vector2(x1 - x0, z1 - z0).Length() * SquiggleOverlap;
			float width = WidthAt((t0 + t1) * 0.5f);
			var centre = new Vector3((x0 + x1) * 0.5f, 0.0f, (z0 + z1) * 0.5f);

			AddBox(new Vector3(width, FloorThickness, chord),
				   new Vector3(centre.X, -FloorThickness * 0.5f, centre.Z), tarmac,
				   rotationDegrees: rotation, parent: road);

			// Painted edges rather than barriers. There is deliberately nothing to lean on — a rail
			// down each side would turn the whole thing into a gutter to be bounced along and the
			// tile would stop asking anything — so the edge gets the only other thing that can mark
			// it. Accent-coloured rather than white because at this width the line between road and
			// runoff is the most important thing on the tile.
			//
			// The slab's own across-axis, which is where local +X ends up under that yaw. Inset by
			// half a stripe so the paint lands on the tarmac instead of straddling its edge.
			var across = new Vector3(Mathf.Cos(yaw), 0.0f, -Mathf.Sin(yaw));
			float reach = width * 0.5f - SeamWidth * 0.5f;

			for (int side = -1; side <= 1; side += 2)
			{
				AddBox(new Vector3(SeamWidth, 0.02f, chord),
					   centre + across * (side * reach) + new Vector3(0.0f, 0.011f, 0.0f),
					   edge, rotationDegrees: rotation, collision: false);
			}
		}
	}
}
