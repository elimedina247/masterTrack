using Godot;
using MasterTrack.Audio;

namespace MasterTrack.Tiles.Hazards;

/// <summary>
/// The first rigged device: a red plate on a spring, ringed with hazard tape, that the sentry
/// plants during the rig phase and fires by hand during the race.
///
/// <b>It is visible the whole time, and that is the design.</b> A racer sees the red square and
/// the tape from a long way off and crosses it fast; the sentry's job is to be pressing the
/// button at the moment somebody is standing on it. That trade — permanent warning against
/// perfect timing — is what makes it a duel instead of a dice roll, and it is why detonation
/// gets a third of a second of wind-up rather than the two-second <see cref="Sentry.SentryActions.LeadSeconds"/>
/// fuse the placed tools use. A two-second fuse on top of a permanently visible trap would just
/// be an instruction to brake.
///
/// The plate hangs at the top of its throw for over a second, and <b>the gap underneath is
/// open road</b>. Whoever was on it is in the air; whoever is behind them gets a clean run
/// through the hole. Every rigged device owes somebody a gift, and this is the spring's.
///
/// Physically the plate is an <see cref="AnimatableBody3D"/> on a scripted arc rather than a
/// rigid body being thrown, because every peer has to see the same plate in the same place —
/// a real body launched by a real spring would diverge within a frame and the gap racers are
/// aiming at would be somewhere different on every screen. Cars are thrown by an explicit
/// mass-scaled impulse at the instant of firing, the launch pad's rule, which is a velocity
/// change the tire solve has nothing to say about.
/// </summary>
public partial class SpringTrapHazard : TrackHazard
{
	// ---- Timing ----

	/// <summary>Seconds of visible compression before the plate fires. The whole reaction
	/// window, and deliberately short — see the class note.</summary>
	private const float WindUpSeconds = 0.30f;

	/// <summary>Seconds from road to peak. Violent on purpose: a spring that eases up is a lift.</summary>
	private const float RiseSeconds = 0.26f;

	/// <summary>Seconds the plate hangs at the top. This is the window racers drive through.</summary>
	private const float HangSeconds = 1.20f;

	/// <summary>Seconds from peak back to the road.</summary>
	private const float FallSeconds = 0.50f;

	/// <summary>Seconds of bounce after the slam before the plate is flush again.</summary>
	private const float SettleSeconds = 0.28f;

	/// <summary>Seconds after settling before it can be fired again. The trap is reusable — one
	/// mistimed press should cost the sentry a moment, not the card.</summary>
	private const float RearmSeconds = 6.0f;

	// ---- Geometry, in metres. Must match SpringTrap.tscn. ----

	/// <summary>
	/// How proud of the road the plate sits at rest. Nearly flush, and it has to be: the tire
	/// radius is 0.24 m, so anything much over a tenth of a metre stops being a bump the car
	/// rolls over and becomes a curb it hits. The read at rest is carried by the colour and the
	/// tape ring instead of by height — a red square on a grey road is not subtle.
	/// </summary>
	private const float RestHeight = 0.06f;

	/// <summary>Plate thickness, so the rest position can be worked out from its centre.</summary>
	private const float PlateThickness = 1.2f;

	/// <summary>How far the plate sinks while winding up.</summary>
	private const float CompressDrop = 0.45f;

	/// <summary>How high the plate's underside gets. Generous — the gap has to read as a door
	/// from board altitude, not as a crack.</summary>
	private const float PeakHeight = 11.0f;

	// ---- Forces ----

	/// <summary>Speed added straight up to whatever is standing on the plate, in m/s. Bigger than
	/// the launch pad's, because this one is aimed rather than driven over.</summary>
	private const float LaunchSpeed = 26.0f;

	/// <summary>Speed the plate drives anything caught underneath into the road with. Lingering
	/// in the gap is the risk that pays for the reward.</summary>
	private const float SwatSpeed = 16.0f;

	/// <summary>How fast the tape crawls while the trap is winding up, in metres per second.</summary>
	private const float ArmingScroll = 9.0f;

	/// <summary>
	/// The BOING — the same sample a Bouncy! car makes, on purpose. A cartoon spring is a
	/// cartoon spring, and a racer who has been thrown by one already knows what this means
	/// before they work out what hit them. Played at the release rather than at the wind-up:
	/// this is the sound of the spring letting go, and the tape and the glow are the warning.
	/// </summary>
	private const string BoingSfxPath = "res://assets/audio/hazards/boing.mp3";

	private enum Stage
	{
		/// <summary>Flush, quiet, ready. What racers see nearly all the time.</summary>
		Dormant,

		/// <summary>Sinking and blinking. The only warning, and it is short.</summary>
		WindUp,

		/// <summary>On its way up, or hanging at the top with the gap open.</summary>
		Thrown,

		/// <summary>Coming down, and dangerous to anything still under it.</summary>
		Falling,

		/// <summary>Down, bouncing off the slam, then waiting out the re-arm.</summary>
		Settling,
	}

	private AnimatableBody3D _plate = null!;
	private Area3D _trigger = null!;
	private Node3D? _coil;
	private ShaderMaterial? _tape;
	private StandardMaterial3D? _plateSkin;

	private Stage _stage = Stage.Dormant;
	private float _elapsed;

	/// <summary>Where the plate's centre sits when the trap is at rest.</summary>
	private float _restY;

	/// <summary>The plate's centre at the top of the throw.</summary>
	private float _peakY;

	/// <summary>Whether the plate is currently up far enough to have thrown its passengers, so
	/// the launch impulse is handed out exactly once per firing.</summary>
	private bool _thrown;

	/// <summary>A rigged device the sentry can fire. This is the whole reason
	/// <see cref="TrackHazard.CanDetonate"/> exists.</summary>
	public override bool CanDetonate => true;

	/// <summary>Whether a press right now would do anything. The board greys the marker off this
	/// rather than letting the sentry fire into a cooldown and wonder why nothing happened.</summary>
	public override bool IsReady => _stage == Stage.Dormant;

	public override void _Ready()
	{
		_plate = GetNode<AnimatableBody3D>("Plate");
		_trigger = GetNode<Area3D>("Trigger");
		_coil = GetNodeOrNull<Node3D>("Coil");

		// The tape's material is an override on the ring so the whole ring — however it is built —
		// wears one continuous stripe field. Duplicated per instance because the scroll and glow
		// are this trap's state, and two traps on one track wind up at different moments.
		if (GetNodeOrNull<GeometryInstance3D>("TapeRing") is { } ring
			&& ring.MaterialOverride is ShaderMaterial shared)
		{
			_tape = (ShaderMaterial)shared.Duplicate();
			ring.MaterialOverride = _tape;
		}

		// Through the mesh instance's override rather than by writing the mesh's own material:
		// a PackedScene's sub-resources are shared across every instance of it, so assigning to
		// the BoxMesh would have the second trap on a track quietly steal the first one's paint.
		// The override lives on the node, and nodes are what instancing actually copies.
		if (_plate.GetNodeOrNull<MeshInstance3D>("Slab") is { } slab
			&& slab.Mesh?.SurfaceGetMaterial(0) is StandardMaterial3D skin)
		{
			_plateSkin = (StandardMaterial3D)skin.Duplicate();
			_plateSkin.EmissionEnabled = true;
			_plateSkin.Emission = new Color(1.0f, 0.25f, 0.1f);
			_plateSkin.EmissionEnergyMultiplier = 0.0f;
			slab.MaterialOverride = _plateSkin;
		}

		_restY = RestHeight - PlateThickness * 0.5f;
		_peakY = _restY + PeakHeight;

		_plate.Position = new Vector3(0.0f, _restY, 0.0f);
		UpdateCoil();
	}

	/// <summary>Free the wrappers while the engine is still alive — a refcounted resource left to
	/// .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
	public override void _ExitTree()
	{
		_tape?.Dispose();
		_tape = null;
		_plateSkin?.Dispose();
		_plateSkin = null;
	}

	/// <summary>
	/// Fire it. Called on every peer from the same broadcast, so the arc runs identically
	/// everywhere; a press that arrives while the plate is already moving is dropped rather than
	/// restarting the throw mid-air.
	/// </summary>
	public override void Detonate()
	{
		if (_stage != Stage.Dormant)
			return;

		_stage = Stage.WindUp;
		_elapsed = 0.0f;
		_thrown = false;

		if (_tape != null)
			_tape.SetShaderParameter("scroll_speed", ArmingScroll);
	}

	/// <summary>
	/// The whole arc, in physics time: the plate is an animatable body and the cars it has to
	/// push are rigid bodies, so moving it anywhere else would be moving it between the frames
	/// that matter.
	/// </summary>
	public override void _PhysicsProcess(double delta)
	{
		if (_stage == Stage.Dormant)
			return;

		_elapsed += (float)delta;

		switch (_stage)
		{
			case Stage.WindUp:
				TickWindUp();
				break;

			case Stage.Thrown:
				TickThrown();
				break;

			case Stage.Falling:
				TickFalling((float)delta);
				break;

			case Stage.Settling:
				TickSettling();
				break;
		}

		UpdateCoil();
	}

	/// <summary>Sinking into the road and blinking. Nothing is thrown yet — this is the beat the
	/// racer gets to be somewhere else in.</summary>
	private void TickWindUp()
	{
		float t = Mathf.Clamp(_elapsed / WindUpSeconds, 0.0f, 1.0f);

		// Eased in: the compression accelerates, which reads as something loading rather than
		// something descending.
		SetPlateY(_restY - CompressDrop * (t * t));

		// Tape crawling, plate heating. Two tells for one beat, because a third of a second is
		// not long enough for a racer to notice only one of them.
		if (_tape != null)
			_tape.SetShaderParameter("glow", 1.6f * t);

		if (_plateSkin != null)
			_plateSkin.EmissionEnergyMultiplier = 2.2f * t;

		if (t < 1.0f)
			return;

		// The instant of firing: everything standing on the plate leaves with it, and the whole
		// corner of the track hears about it. Carried further than a bumper's boing (unit size
		// 15) and less far than a blast (45) — a sixteen-metre steel plate is loud, but it is
		// still a spring rather than ordnance.
		Sfx.PlayAt(this, BoingSfxPath, _plate.GlobalPosition,
				   volumeDb: 3.0f, unitSize: 32.0f, pitchJitter: 0.07f);

		ThrowPassengers();

		_stage = Stage.Thrown;
		_elapsed = 0.0f;
		_thrown = true;
	}

	/// <summary>Up, then hanging with the gap open underneath.</summary>
	private void TickThrown()
	{
		if (_elapsed < RiseSeconds)
		{
			// Eased out: fastest off the road, slowing into the hang. A spring spends its force
			// immediately and coasts the rest.
			float t = _elapsed / RiseSeconds;
			float eased = 1.0f - (1.0f - t) * (1.0f - t);
			SetPlateY(Mathf.Lerp(_restY - CompressDrop, _peakY, eased));
			return;
		}

		SetPlateY(_peakY);

		// Spent: the heat goes out of both the moment the throw is over.
		if (_tape != null)
			_tape.SetShaderParameter("glow", 0.0f);

		if (_plateSkin != null)
			_plateSkin.EmissionEnergyMultiplier = 0.0f;

		if (_elapsed >= RiseSeconds + HangSeconds)
		{
			_stage = Stage.Falling;
			_elapsed = 0.0f;
		}
	}

	/// <summary>Coming down, and swatting anything that stayed in the gap too long.</summary>
	private void TickFalling(float delta)
	{
		float t = Mathf.Clamp(_elapsed / FallSeconds, 0.0f, 1.0f);

		// Eased in: gravity's shape, so the slam is the fast part.
		SetPlateY(Mathf.Lerp(_peakY, _restY, t * t));

		SwatStragglers(delta);

		if (t < 1.0f)
			return;

		_stage = Stage.Settling;
		_elapsed = 0.0f;
	}

	/// <summary>One decaying bounce off the slam, then the re-arm wait.</summary>
	private void TickSettling()
	{
		if (_elapsed < SettleSeconds)
		{
			// A single damped hop, small: the plate has landed, this is just the ring of it.
			float t = _elapsed / SettleSeconds;
			float bounce = Mathf.Sin(t * Mathf.Pi * 2.0f) * (1.0f - t) * 0.35f;
			SetPlateY(_restY + bounce);
			return;
		}

		SetPlateY(_restY);

		if (_elapsed < SettleSeconds + RearmSeconds)
			return;

		_stage = Stage.Dormant;
		_thrown = false;

		if (_tape != null)
		{
			_tape.SetShaderParameter("scroll_speed", 0.0f);
			_tape.SetShaderParameter("glow", 0.0f);
		}
	}

	private void SetPlateY(float y) => _plate.Position = new Vector3(0.0f, y, 0.0f);

	/// <summary>
	/// Throw everything standing on the plate straight off it. Along the slot's own up rather
	/// than the world's, so a spring in a banked corner fires off the bank — correct, and
	/// funnier. Mass-scaled, the launch pad's rule: a change in velocity, so every car in the
	/// fleet leaves at the same speed.
	/// </summary>
	private void ThrowPassengers()
	{
		Vector3 up = GlobalBasis.Y.Normalized();

		foreach (Node3D body in _trigger.GetOverlappingBodies())
		{
			if (body is RigidBody3D car)
				car.ApplyCentralImpulse(up * (LaunchSpeed * car.Mass));
		}
	}

	/// <summary>
	/// Drive anything still under the falling plate into the road. Scaled by the frame so a
	/// straggler is pressed down rather than pinged once, and applied every frame of the fall
	/// because a car that entered the gap late never crossed the trigger's boundary.
	/// </summary>
	private void SwatStragglers(float delta)
	{
		Vector3 down = -GlobalBasis.Y.Normalized();

		foreach (Node3D body in _trigger.GetOverlappingBodies())
		{
			if (body is RigidBody3D car)
				car.ApplyCentralImpulse(down * (SwatSpeed * car.Mass * delta * 4.0f));
		}
	}

	/// <summary>
	/// Spread the coil's rings between the pit floor and the plate's underside, so the spring
	/// stretches with the throw instead of the plate flying off a stack of loose hoops. Cheap —
	/// it is a handful of positions — and it is the thing that makes the plate read as *sprung*
	/// rather than as a box on a lift.
	/// </summary>
	private void UpdateCoil()
	{
		if (_coil == null)
			return;

		int rings = _coil.GetChildCount();
		if (rings == 0)
			return;

		float underside = _plate.Position.Y - PlateThickness * 0.5f;
		float floor = _restY - PlateThickness * 0.5f - 0.1f;
		float span = Mathf.Max(underside - floor, 0.05f);

		for (int i = 0; i < rings; i++)
		{
			if (_coil.GetChild(i) is not Node3D ring)
				continue;

			// Evenly spaced, bottom ring on the floor and top ring against the plate.
			float t = rings == 1 ? 0.0f : (float)i / (rings - 1);
			ring.Position = new Vector3(0.0f, floor + span * t, 0.0f);
		}
	}
}
