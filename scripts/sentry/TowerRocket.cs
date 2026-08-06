using Godot;
using MasterTrack.Audio;
using MasterTrack.Racer;
using MasterTrack.Tiles;
using MasterTrack.Vehicles;

namespace MasterTrack.Sentry;

/// <summary>
/// What a <see cref="Tiles.Hazards.RocketTowerHazard"/> shoots: a small rocket that flies flat
/// out of the barrel, corrects a little on the way, and shoves whatever it lands near.
///
/// <b>The little brother of <see cref="SentryMissile"/>, and deliberately so.</b> The missile is
/// an event — a hundred and fifty metres of falling, a ring on the road, and a blast that puts a
/// car in the air. This is punctuation. A turret fires every few seconds without anybody
/// deciding to, so its shot has to cost a corner rather than a race: a quarter of the missile's
/// blast radius and about a third of its push. Getting hit should spin you, spoil your line,
/// hand the place behind you a gift — not delete you.
///
/// <b>It is supposed to miss.</b> The turn rate is capped well below what a car can do, so the
/// rocket is committed to roughly the lead the turret guessed at launch. A driver who holds
/// their speed and their line eats it; a driver who lifts, or floors it, or takes the inside,
/// generally does not. That gap is the entire counterplay, and it is the reason this homes at
/// all rather than either flying straight (never hits anyone) or tracking perfectly (always
/// does, which is a toll booth).
///
/// Not replicated: every peer builds one from the same broadcast and flies it with the same
/// constants, so they all arrive at the same place. The blast follows the standing rule — see
/// <see cref="SentryBlast"/> — where each machine throws only the cars it simulates.
/// </summary>
public partial class TowerRocket : Node3D
{
	/// <summary>
	/// Flight speed, in metres per second. <b>The number that matters is a car's top speed of
	/// 55.6</b> (<see cref="Vehicles.Vehicle.TopSpeed"/>), and this sits about a quarter above it.
	///
	/// That threshold is the whole tuning story. At 50 the rocket was slower than a car flat out,
	/// so it could never run anybody down — every shot at a racer driving away trailed them
	/// forever and the turrets felt toothless whatever else was true of them. Above the line it
	/// closes, and a tower is a threat again.
	///
	/// Still deliberately not a bullet: nearly two seconds in the air at the turret's full reach,
	/// which is long enough to watch it come and pick a line. And a car <i>drifting</i> is well
	/// over its base top speed, so driving beautifully still outruns the shot — which is exactly
	/// who should get away with it.
	///
	/// The turret's lead maths reads this constant, so changing it here keeps the aim honest.
	/// </summary>
	public const float Speed = 70.0f;

	/// <summary>How hard it may correct, in degrees per second. <b>The fairness number</b> — see
	/// the class remarks. A car at speed can out-turn this comfortably.</summary>
	private const float TurnDegrees = 45.0f;

	/// <summary>How far the blast still throws cars, in metres. A quarter of the missile's, so
	/// the car beside the one that was hit usually keeps its race.</summary>
	private const float BlastRadius = TrackTile.Size * 0.45f;

	/// <summary>Speed the blast adds at the centre, in m/s. About a third of the missile's: a
	/// hard shove that costs a corner, not a launch.</summary>
	private const float BlastStrength = 28.0f;

	/// <summary>How near the target counts as a hit, in metres. A proximity fuse, because a
	/// rocket that had to touch a moving car would mostly sail through it between frames.</summary>
	private const float ProximityFuse = 5.5f;

	/// <summary>Seconds before an unspent rocket gives up and goes off wherever it is. Stops a
	/// shot at a car that then falls off the track from flying to the horizon forever.</summary>
	private const float LifeSeconds = 6.0f;

	/// <summary>The car this was fired at, by peer id. Comes down the wire with the shot: every
	/// peer flies the same rocket at the same car, whoever happens to be simulating it.</summary>
	public int TargetPeerId { get; set; }

	private Vector3 _heading = Vector3.Forward;
	private float _age;
	private RacerController? _target;

	/// <summary>The materials this built, kept so they can be released while the engine is still
	/// alive — see <see cref="_ExitTree"/>.</summary>
	private readonly System.Collections.Generic.List<Resource> _made = new();

	public void Launch(Vector3 direction)
	{
		_heading = direction.LengthSquared() < 0.0001f
			? Vector3.Forward
			: direction.Normalized();

		LookAtHeading();
	}

	public override void _Ready()
	{
		BuildBody();

		// Quieter and higher than the missile's, off the same tin: the racers have learned what
		// that whistle means, and this is the same thing arriving in a smaller size.
		if (Sfx.Attach(this, SentryMissile.WhistleSfxPath, volumeDb: -8.0f, unitSize: 18.0f)
			is { } whistle)
			whistle.PitchScale = 1.45f;
	}

	/// <summary>
	/// A stubby rocket in the house style — faceted solids, per-vertex light, no specular. Built
	/// from code rather than the missile's imported model on purpose: at this size the model
	/// would read as the big missile shrunk, and the two are not supposed to be confused at a
	/// glance.
	/// </summary>
	private void BuildBody()
	{
		StandardMaterial3D red = Paint(new Color(0.80f, 0.16f, 0.12f));
		StandardMaterial3D cream = Paint(new Color(0.90f, 0.87f, 0.78f));

		// Nose down the local -Z, the direction everything here points.
		AddChild(new CsgCylinder3D
		{
			Name = "Nose",
			Cone = true, Sides = 8, SmoothFaces = false,
			Radius = 0.55f, Height = 1.3f,
			Material = red,
			Position = new Vector3(0.0f, 0.0f, -1.4f),
			RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f),
		});
		AddChild(new CsgCylinder3D
		{
			Name = "Shell",
			Sides = 8, SmoothFaces = false,
			Radius = 0.55f, Height = 2.2f,
			Material = cream,
			RotationDegrees = new Vector3(90.0f, 0.0f, 0.0f),
		});

		for (var i = 0; i < 3; i++)
		{
			var fin = new Node3D { RotationDegrees = new Vector3(0.0f, 0.0f, 120.0f * i) };
			fin.AddChild(new CsgBox3D
			{
				Size = new Vector3(0.12f, 0.9f, 0.9f),
				Material = red,
				Position = new Vector3(0.0f, 0.6f, 1.0f),
			});
			AddChild(fin);
		}

		// The flame, unshaded like every glow on the board.
		var flame = new StandardMaterial3D
		{
			AlbedoColor = new Color(1.0f, 0.6f, 0.15f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		_made.Add(flame);

		AddChild(new CsgCylinder3D
		{
			Name = "Flame",
			Cone = true, Sides = 8, SmoothFaces = false,
			Radius = 0.4f, Height = 1.8f,
			Material = flame,
			Position = new Vector3(0.0f, 0.0f, 1.9f),
			RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f),
		});
	}

	/// <summary>The house material — per-vertex light, no specular, the same three decisions the
	/// cars and the track make — remembered so it can be released on the way out.</summary>
	private StandardMaterial3D Paint(Color colour)
	{
		var material = new StandardMaterial3D
		{
			AlbedoColor = colour,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
			SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
			Metallic = 0.0f,
			Roughness = 1.0f,
		};

		_made.Add(material);
		return material;
	}

	/// <summary>Let go of the wrappers while the engine is still there to free them — a rocket
	/// frees itself on every hit, so this runs constantly during a race.</summary>
	public override void _ExitTree()
	{
		foreach (Resource resource in _made)
			resource.Dispose();

		_made.Clear();
	}

	public override void _Process(double delta)
	{
		var step = (float)delta;
		_age += step;

		if (_age >= LifeSeconds)
		{
			Detonate(GlobalPosition);
			return;
		}

		if (FindTarget() is { } target)
		{
			// Aimed at where the car will be by the time we could get there, then capped — the
			// cap is what makes the correction a nudge instead of a leash.
			Vector3 lead = target.GlobalPosition
						   + target.EffectiveVelocity
						   * (GlobalPosition.DistanceTo(target.GlobalPosition) / Speed);

			Vector3 wanted = lead - GlobalPosition;
			if (wanted.LengthSquared() > 0.0001f)
			{
				Vector3 want = wanted.Normalized();
				float turn = Mathf.DegToRad(TurnDegrees) * step;
				float angle = _heading.AngleTo(want);

				_heading = angle <= turn ? want : _heading.Slerp(want, turn / angle).Normalized();
			}

			if (GlobalPosition.DistanceTo(target.GlobalPosition) <= ProximityFuse)
			{
				Detonate(target.GlobalPosition);
				return;
			}
		}

		Vector3 from = GlobalPosition;
		Vector3 to = from + _heading * (Speed * step);

		// Anything solid in the way ends the flight where it was met — road, a tile's underside,
		// a car. Cheaper and more predictable than giving the rocket a body: it is travelling far
		// enough per frame to tunnel through the road otherwise.
		if (GetWorld3D() is { DirectSpaceState: { } space })
		{
			var query = PhysicsRayQueryParameters3D.Create(from, to);
			query.CollideWithAreas = false;

			Godot.Collections.Dictionary hit = space.IntersectRay(query);
			if (hit.Count > 0)
			{
				Detonate(hit["position"].AsVector3());
				return;
			}
		}

		GlobalPosition = to;
		LookAtHeading();
	}

	/// <summary>The car it was fired at, while it is still in play. A target that finished,
	/// died or left simply stops being corrected for — the rocket flies on and goes off where
	/// it lands, which is both cheaper and funnier than making it vanish.</summary>
	private RacerController? FindTarget()
	{
		if (_target != null && IsInstanceValid(_target) && _target.IsInsideTree())
			return _target;

		_target = null;

		foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
		{
			if (node is RacerController racer && racer.OwnerPeerId == TargetPeerId)
			{
				_target = racer;
				break;
			}
		}

		return _target;
	}

	private void LookAtHeading()
	{
		// Straight up or straight down has no unique roll, and LookAt refuses the degenerate
		// case rather than guessing — so hand it a different up when it comes to that.
		Vector3 up = Mathf.Abs(_heading.Dot(Vector3.Up)) > 0.999f ? Vector3.Forward : Vector3.Up;
		LookAt(GlobalPosition + _heading, up);
	}

	private void Detonate(Vector3 at)
	{
		SentryBlast.Explode(this, at, BlastRadius, BlastStrength);
		QueueFree();
	}
}
