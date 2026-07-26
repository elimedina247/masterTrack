using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// Hazards with moving parts, and the clock that drives them.
///
/// A moving part is an <see cref="AnimatableBody3D"/> rather than a plain mesh, because that is
/// the body Godot will let shove a <see cref="RigidBody3D"/> around: moved during the physics
/// step, it carries whatever it is touching with it. A mesh on a StaticBody would simply appear
/// somewhere else each frame and let cars tunnel through it.
///
/// <b>Phase is per-peer, on purpose.</b> Every part runs off this tile's own elapsed time, so two
/// machines watching the same log will have it a fraction of a swing apart. That is fine, and it
/// is the same bargain the game already takes on the cars: a racer is simulated by whoever owns
/// it and its pose is what gets replicated, so each car is knocked about by its own owner's view
/// of the hazard and everyone sees the consequence. The alternative — a synchronised hazard clock
/// — would buy agreement about where a log is, which is not something anyone can see, at the cost
/// of a clock to keep in step.
/// </summary>
public partial class TrackTile
{
	/// <summary>How a moving part gets from one end of its cycle to the other.</summary>
	private enum MotionKind
	{
		/// <summary>Sine sweep along local X: side to side across the road.</summary>
		SweepX,

		/// <summary>Slam down along local Y, dwell, haul back up, dwell. See <see cref="SlamOffset"/>.</summary>
		SlamY,

		/// <summary>Constant rotation about local Y.</summary>
		SpinY,
	}

	private sealed class MovingPart
	{
		public required Node3D Node { get; init; }
		public required Vector3 Origin { get; init; }
		public required MotionKind Kind { get; init; }

		/// <summary>Metres of travel for a sweep or a slam. Ignored by a spin.</summary>
		public required float Amplitude { get; init; }

		/// <summary>Seconds for one full cycle.</summary>
		public required float Period { get; init; }

		/// <summary>Where in the cycle this part starts, 0 to 1.</summary>
		public required float Phase { get; init; }
	}

	private readonly List<MovingPart> _movingParts = new();

	/// <summary>Seconds this tile has been alive. The only clock the moving parts read.</summary>
	private float _elapsed;

	/// <summary>
	/// A fixed offset into every cycle on this tile, spread by track index so that a run of
	/// crushers reads as a rippling row of them rather than one wall going up and down.
	/// Multiplying by the golden ratio and taking the fraction is the cheap way to get indices
	/// 0, 1, 2, 3 to land far apart in 0..1 instead of marching in step.
	/// </summary>
	private float IndexPhase => Mathf.PosMod(TrackIndex * 0.6180339f, 1.0f);

	/// <summary>
	/// Register a box that moves. Real tiles get an <see cref="AnimatableBody3D"/> that can push
	/// cars; ghosts get a bare mesh, since a preview must never collide with anything — it still
	/// animates, because watching the log swing is how the Track Master reads what the tile does.
	/// </summary>
	private void AddMovingBox(Vector3 size, Vector3 position, StandardMaterial3D material,
							  MotionKind kind, float amplitude, float period, float phase = 0.0f,
							  Vector3 rotationDegrees = default)
	{
		Node3D mover;

		if (_isGhost)
		{
			mover = new Node3D { Name = "MovingPart", Position = position };
		}
		else
		{
			mover = new AnimatableBody3D { Name = "MovingPart", Position = position };
			mover.AddChild(new CollisionShape3D
			{
				Shape = new BoxShape3D { Size = size },
				RotationDegrees = rotationDegrees,
			});
		}

		AddChild(mover);

		// The mesh is a child of the mover rather than the mover itself so that a rotated part
		// can spin about its own axis while the box inside it stays askew.
		mover.AddChild(new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = size, Material = material },
			RotationDegrees = rotationDegrees,
		});

		_movingParts.Add(new MovingPart
		{
			Node = mover,
			Origin = position,
			Kind = kind,
			Amplitude = amplitude,
			Period = period,
			Phase = Mathf.PosMod(phase + IndexPhase, 1.0f),
		});

		SetPhysicsProcess(true);
	}

	/// <summary>
	/// Drive every moving part. In the physics step rather than the frame step because an
	/// AnimatableBody3D only pushes what it touches if it is moved here.
	/// </summary>
	public override void _PhysicsProcess(double delta)
	{
		_elapsed += (float)delta;

		// The loop has no moving parts but does have a force to apply, and this is the step to
		// apply it in.
		ApplyLoopAssist();

		foreach (MovingPart part in _movingParts)
		{
			float cycle = Mathf.PosMod(_elapsed / part.Period + part.Phase, 1.0f);

			switch (part.Kind)
			{
				case MotionKind.SweepX:
					part.Node.Position = part.Origin with
					{
						X = part.Origin.X + Mathf.Sin(cycle * Mathf.Tau) * part.Amplitude,
					};
					break;

				case MotionKind.SlamY:
					part.Node.Position = part.Origin with
					{
						Y = part.Origin.Y + SlamOffset(cycle) * part.Amplitude,
					};
					break;

				case MotionKind.SpinY:
					part.Node.Rotation = new Vector3(0.0f, cycle * Mathf.Tau, 0.0f);
					break;
			}
		}
	}

	/// <summary>
	/// Height of a slamming part above its bottom position, 0 to 1, across one cycle.
	///
	/// Deliberately not a sine. A piston that eases into the road is a piston you can watch and
	/// time; one that hangs at the top, drops in a fifth of the cycle, sits on the road long
	/// enough to be a wall, and then hauls itself back up is one you have to commit to running.
	/// </summary>
	private static float SlamOffset(float cycle) => cycle switch
	{
		< 0.15f => 1.0f - cycle / 0.15f,          // dropping
		< 0.35f => 0.0f,                          // down, blocking the road
		< 0.75f => (cycle - 0.35f) / 0.4f,        // hauling back up
		_ => 1.0f,                                // raised, waiting
	};

	// ---- Log Trap ----

	/// <summary>
	/// A log that rolls back and forth across the road, shoving anything it catches sideways.
	///
	/// The tile has no side walls, which is the whole point and was asked for explicitly: being
	/// shoved has to mean leaving the track, so there is nothing to be shoved into.
	///
	/// The log covers a little over half the road, so there is always a gap — but it is on one
	/// side, then the other, and the tile is asking whether you can be in it at the right moment.
	/// A log that covered the whole road would be a closed door rather than a hazard.
	/// </summary>
	private void BuildLogTrap(TileDefinition definition)
	{
		// Long enough to cover the road at any point in the sweep, and tall enough that it hits
		// the car's body rather than riding up over the bonnet.
		const float logLength = Size * 0.55f;
		const float logRadius = 2.6f;
		const float sweep = Size * 0.32f;
		const float period = 3.2f;

		// Laid across the direction of travel, so its long axis is local X. A box rather than a
		// cylinder for the collider: the tire model and the car body both deal in boxes here, and
		// a rolling pin that catches on its corners is more predictable than one that rolls a car
		// up over itself.
		AddMovingBox(new Vector3(logLength, logRadius * 2.0f, logRadius * 2.0f),
					 new Vector3(0.0f, logRadius, 0.0f),
					 WallMaterial(definition.Accent),
					 MotionKind.SweepX, sweep, period);
	}

	// ---- Crushers ----

	/// <summary>
	/// Pistons that hang above the road and hammer down onto it, staggered across the width and
	/// out of step with each other so that the way through is a moving gap rather than a gate.
	///
	/// The threat here is timing, which nothing else in the catalog asks for: a gap is about speed
	/// and a bottleneck is about position, but a crusher is about <i>when</i>. Coming in slowly to
	/// read the rhythm is a real option, and a real cost.
	/// </summary>
	private void BuildCrushers(TileDefinition definition)
	{
		const float blockHeight = 7.0f;
		const float lift = 10.0f;
		const float period = 2.6f;
		float blockSide = Size * 0.3f;

		StandardMaterial3D material = WallMaterial(definition.Accent);

		// Three of them on a diagonal, a third of a cycle apart, so the row ripples.
		for (int i = 0; i < 3; i++)
		{
			float lateral = (i - 1) * Size * 0.3f;
			float along = (1 - i) * Length * 0.24f;

			AddMovingBox(new Vector3(blockSide, blockHeight, blockSide),
						 new Vector3(lateral, blockHeight * 0.5f, along),
						 material, MotionKind.SlamY, lift, period, phase: i / 3.0f);
		}
	}

	// ---- Spinner ----

	/// <summary>
	/// A single arm turning about the middle of the tile, sweeping the road like a clock hand.
	///
	/// One arm rather than a cross on purpose: a cross leaves four quadrants and no way to wait,
	/// where one arm leaves a wide gap behind it that a racer can follow through. There is no
	/// respawn in this game, so a hazard that cannot be beaten by driving well is a hazard that
	/// ends someone's race by lottery.
	/// </summary>
	private void BuildSpinner(TileDefinition definition)
	{
		const float armHeight = 1.6f;
		const float armThickness = 3.0f;
		const float period = 3.4f;
		float armLength = Size * 0.86f;

		StandardMaterial3D material = WallMaterial(definition.Accent);

		AddMovingBox(new Vector3(armLength, armHeight, armThickness),
					 new Vector3(0.0f, armHeight * 0.5f, 0.0f),
					 material, MotionKind.SpinY, 0.0f, period);

		// A hub so the arm reads as being driven from somewhere rather than floating.
		AddBox(new Vector3(armThickness * 1.6f, armHeight * 1.4f, armThickness * 1.6f),
			   new Vector3(0.0f, armHeight * 0.7f, 0.0f), material);
	}
}
