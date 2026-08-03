using Godot;
using MasterTrack.Sentry;

namespace MasterTrack.Racer;

/// <summary>
/// The sentry's debuffs, as they land on a car. See <see cref="SentryManager"/> for how they
/// get here: the server broadcasts, every peer marks the same car, and then the one machine
/// that simulates that car is the one whose marks do physics. Everything in this file follows
/// that split — state is set everywhere (the aura has to glow on every screen), forces are
/// applied only behind an <c>IsRemote</c> check.
///
/// The chain is a velocity-level rope rather than a force. Forces fed to this car from outside
/// get eaten by the grip solve — the tires treat them as one more disturbance to be gripped
/// away — so a spring force would mostly be an argument with the tire model. Editing the
/// velocity directly is the same trick the car-to-car impact layer uses, and it reads as what
/// it is: a rope going taut.
/// </summary>
public partial class RacerController
{
	/// <summary>The least launch a touch on a bouncy car sells, as a closing speed in m/s.
	/// A parking-lot nudge on a bumper still throws you; that is the whole joke.</summary>
	private const float BouncyLaunchSpeed = 15.0f;

	/// <summary>How much harder a bouncy car's impacts hit than a normal collision.</summary>
	private const float BouncyBounceScale = 2.2f;

	/// <summary>Pull toward the partner per metre of chain stretch, in m/s² per metre.</summary>
	private const float ChainSpringRate = 8.0f;

	/// <summary>Hardest the chain may pull, in m/s². A cap, so a huge stretch is a yank, not a railgun.</summary>
	private const float ChainMaxPull = 50.0f;

	private ulong _bouncyUntilMsec;
	private ulong _chainUntilMsec;
	private RacerController? _chainPartner;
	private MeshInstance3D? _bouncyAura;

	// The timed debuffs each keep a start and an end: between "applied" and start the fuse is
	// burning and the car only glows; the physics begins at start. See SentryActions.LeadSeconds.
	private ulong _boosterStartMsec, _boosterUntilMsec;
	private ulong _wiresStartMsec, _wiresUntilMsec;
	private ulong _moonStartMsec, _moonUntilMsec;

	/// <summary>A short latch rather than a flag — see <see cref="TouchOilSlick"/>.</summary>
	private ulong _slickedUntilMsec;

	private bool _moonApplied;
	private float _normalGravityScale;

	private MeshInstance3D? _debuffAura;
	private StandardMaterial3D? _debuffAuraMaterial;

	/// <summary>Whether touching this car currently launches people. Read by <i>other</i> cars'
	/// contact processing, which is why it is public and why it must be true on every peer.</summary>
	public bool IsBouncyActive => Time.GetTicksMsec() < _bouncyUntilMsec;

	private bool IsChainActive =>
		Time.GetTicksMsec() < _chainUntilMsec
		&& _chainPartner != null && IsInstanceValid(_chainPartner) && _chainPartner.IsInsideTree();

	private bool IsBoosterActive => Within(_boosterStartMsec, _boosterUntilMsec);
	private bool IsWiresActive => Within(_wiresStartMsec, _wiresUntilMsec);
	private bool IsMoonActive => Within(_moonStartMsec, _moonUntilMsec);

	private bool IsBoosterIncoming => Incoming(_boosterStartMsec, _boosterUntilMsec);
	private bool IsWiresIncoming => Incoming(_wiresStartMsec, _wiresUntilMsec);

	private bool IsSlicked => Time.GetTicksMsec() < _slickedUntilMsec;

	private static bool Within(ulong start, ulong until)
	{
		ulong now = Time.GetTicksMsec();
		return now >= start && now < until;
	}

	/// <summary>The fuse is lit but the debuff hasn't landed: the reaction window.</summary>
	private static bool Incoming(ulong start, ulong until)
		=> until != 0 && Time.GetTicksMsec() < start;

	/// <summary>Make this car a bumper for a while. Called on every peer by the sentry broadcast.</summary>
	public void ApplyBouncy(float duration)
	{
		_bouncyUntilMsec = Time.GetTicksMsec() + (ulong)(duration * 1000.0f);
		GD.Print($"[Racer {OwnerPeerId}] Bouncy! for {duration:0.#}s.");
	}

	/// <summary>Rope this car to another for a while. Called on every peer; only the owning
	/// machine's copy ever pulls (see <see cref="UpdateDebuffs"/>).</summary>
	public void ApplyChain(RacerController partner, float duration)
	{
		_chainPartner = partner;
		_chainUntilMsec = Time.GetTicksMsec() + (ulong)(duration * 1000.0f);
		GD.Print($"[Racer {OwnerPeerId}] Chained to peer {partner.OwnerPeerId} for {duration:0.#}s.");
	}

	/// <summary>Jam the throttle open after a fuse. Called on every peer by the sentry broadcast;
	/// the input override and the speed bonus only ever run on the owning machine.</summary>
	public void ApplyRunawayBooster(float delay, float duration)
	{
		_boosterStartMsec = Time.GetTicksMsec() + (ulong)(delay * 1000.0f);
		_boosterUntilMsec = _boosterStartMsec + (ulong)(duration * 1000.0f);
		GD.Print($"[Racer {OwnerPeerId}] Runaway booster in {delay:0.#}s, for {duration:0.#}s.");
	}

	/// <summary>Wire the steering backwards after a fuse. Called on every peer.</summary>
	public void ApplyCrossedWires(float delay, float duration)
	{
		_wiresStartMsec = Time.GetTicksMsec() + (ulong)(delay * 1000.0f);
		_wiresUntilMsec = _wiresStartMsec + (ulong)(duration * 1000.0f);
		GD.Print($"[Racer {OwnerPeerId}] Crossed wires in {delay:0.#}s, for {duration:0.#}s.");
	}

	/// <summary>Drop gravity after a fuse. Called for every car on every peer — the moon is not
	/// aimed at anyone — and the gravity change only runs on the machine that simulates this car.</summary>
	public void ApplyMoonGravity(float delay, float duration)
	{
		_moonStartMsec = Time.GetTicksMsec() + (ulong)(delay * 1000.0f);
		_moonUntilMsec = _moonStartMsec + (ulong)(duration * 1000.0f);
	}

	/// <summary>
	/// Called by an oil slick every physics sweep while this car is inside it. A short latch
	/// rather than a set/clear pair, so nothing depends on whether the slick or the car ticks
	/// first this frame — the latch simply lapses a beat after the car leaves the puddle.
	/// </summary>
	public void TouchOilSlick()
		=> _slickedUntilMsec = Time.GetTicksMsec() + 200;

	/// <summary>
	/// The debuffs that lie to the car about its driver, run just after the real input lands.
	/// Wires flip the wheel; the booster stands on the throttle and unbolts the brake pedal.
	/// Drift, hop and nitro stay the player's — a runaway car you can still slide is chaos,
	/// one you can do nothing with is a cutscene.
	/// </summary>
	private void ApplyInputDebuffs()
	{
		if (IsWiresActive)
			SteeringInput = -SteeringInput;

		if (IsBoosterActive)
		{
			ThrottleInput = 1.0f;
			BrakeInput = 0.0f;
		}
	}

	/// <summary>
	/// A missile went off nearby — maybe. Called for every car on every peer; the distance
	/// check and the ownership check decide whether anything happens to this one. The throw is
	/// mostly radial with a deliberate upward slice, because a car in the air has no grip, no
	/// drive and no say — being launched <i>up</i> is what makes a blast cost time, not just heading.
	/// </summary>
	public void ApplyExplosionImpulse(Vector3 center, float radius, float strength)
	{
		if (IsRemote || !IsInsideTree())
			return;

		Vector3 offset = GlobalPosition - center;
		float distance = offset.Length();
		if (distance > radius)
			return;

		// Squared, not linear: a near hit keeps almost all of the strength, and the edge of the
		// radius is a nudge — the violence lives close to the crater.
		float proximity = 1.0f - distance / radius;
		float falloff = proximity * proximity;
		Vector3 direction = distance > 0.5f ? offset / distance : Vector3.Up;

		LinearVelocity += direction * (strength * falloff)
						  + Vector3.Up * (strength * 0.6f * falloff);

		// The stun and the camera shake ride the existing impact machinery, so a blast feels
		// like the biggest hit in the game rather than a different kind of event.
		RegisterImpact(Mathf.Clamp(falloff * 1.2f, 0.0f, 1.0f), center);

		GD.Print($"[Racer {OwnerPeerId}] Caught a blast {distance:0.#}m out ({falloff:P0}).");
	}

	/// <summary>
	/// A magnet is dragging at this car — maybe. Called every physics tick for every car near
	/// one; the distance and ownership checks decide whether anything happens to this copy. A
	/// velocity edit rather than a force for the same reason the chain is: a force would just be
	/// one more disturbance for the tire solve to grip away.
	///
	/// The pull is flattened to the road plane — a magnet that pressed cars into the tarmac
	/// would only be helping their grip, and one that lifted them would be a different tool.
	/// </summary>
	public void ApplyMagnetPull(Vector3 centre, float radius, float accel, float delta)
	{
		if (IsRemote || !IsInsideTree())
			return;

		Vector3 toCentre = centre - GlobalPosition;
		toCentre.Y = 0.0f;
		float distance = toCentre.Length();

		// Inside a couple of metres the magnet has caught you; yanking through the centre
		// would slingshot, which is the wrong joke.
		if (distance > radius || distance < 2.0f)
			return;

		LinearVelocity += toCentre / distance * (accel * delta);
	}

	/// <summary>
	/// This car clipped a piece of spilled cargo. The physics contact already shoved the debris
	/// — this is the debris shoving <i>back</i>, through the same velocity-level door every
	/// outside hit uses, because the solver's contact force on the car mostly gets eaten by the
	/// tire model.
	///
	/// The design lives in the split below: a dead-centre hit is a thump that scrubs a little
	/// speed and nothing else, an off-centre clip puts <i>yaw</i> into the car. One piece of
	/// junk is a wobble; threading a field of it clean at full tilt is a skill, and hitting
	/// three in a second is a spin. The hazard punishes a bad line, never speed itself.
	/// </summary>
	public void ApplyDebrisHit(Vector3 debrisPosition)
	{
		if (IsRemote || !IsInsideTree())
			return;

		float speed = LinearVelocity.Length();
		if (speed < 3.0f)
			return;

		Vector3 offset = debrisPosition - GlobalPosition;
		offset.Y = 0.0f;
		if (offset.LengthSquared() < 0.01f)
			return;
		Vector3 toDebris = offset.Normalized();

		// Which side took the hit: -1 hard left, +1 hard right, ~0 dead ahead.
		float side = GlobalBasis.X.Dot(toDebris);

		// Faster hits kick harder, saturating around top speed — the scale, not the shape.
		float pace = Mathf.Clamp(speed / 50.0f, 0.2f, 1.2f);

		// The nose deflects away from the side that clipped: in Godot +Y yaw swings the nose
		// left, so a hit on the right (+side) yaws positive. Centred hits barely steer.
		AngularVelocity += Vector3.Up * (side * 0.9f * pace);

		// The clip also shoulders the car off its line a touch, away from the debris.
		Vector3 shove = (LinearVelocity.Normalized() - toDebris * 0.6f).Normalized();
		LinearVelocity += shove * (2.0f * pace * Mathf.Abs(side));

		// And the thump itself: a small scrub whoever you are, biggest when dead centre.
		LinearVelocity *= 1.0f - 0.04f * (1.0f - Mathf.Abs(side) * 0.5f);

		GD.Print($"[Racer {OwnerPeerId}] Clipped cargo (side {side:0.00}) at {speed:0} m/s.");
	}

	/// <summary>
	/// Per-physics-step debuff upkeep, called for every car — simulated and puppet alike,
	/// because the aura is everyone's business even where the physics isn't.
	/// </summary>
	private void UpdateDebuffs(float delta)
	{
		UpdateBouncyAura();
		UpdateDebuffAura();

		if (IsRemote)
			return;

		if (IsChainActive)
			ApplyChainRope(delta);

		// The vehicle hooks are rewritten every step rather than set and cleared on the edges,
		// so a missed edge — a respawn, a mid-debuff scene change — can never leave a car
		// permanently soaped or permanently fast.
		DebuffGripMultiplier = IsSlicked ? SentryActions.OilGripMultiplier : 1.0f;
		DebuffSpeedBonus = IsBoosterActive ? SentryActions.BoosterSpeedBonus : 0.0f;
		DebuffAccelMultiplier = IsBoosterActive ? SentryActions.BoosterAccelMultiplier : 1.0f;

		UpdateMoonGravity();
	}

	/// <summary>
	/// Gravity is the one debuff that has to be edge-handled: <c>GravityScale</c> is a tuned
	/// export (the racer runs heavy on purpose — see <c>Vehicle</c>), so the normal value is
	/// captured on the way in and restored on the way out rather than assumed.
	/// </summary>
	private void UpdateMoonGravity()
	{
		bool active = IsMoonActive;
		if (active == _moonApplied)
			return;

		if (active)
		{
			_normalGravityScale = GravityScale;
			GravityScale *= SentryActions.MoonGravityFactor;
			GD.Print($"[Racer {OwnerPeerId}] Moon gravity on.");
		}
		else
		{
			GravityScale = _normalGravityScale;
			GD.Print($"[Racer {OwnerPeerId}] Moon gravity off.");
		}

		_moonApplied = active;
	}

	/// <summary>The rope going taut: kill any velocity that stretches it further, then reel.</summary>
	private void ApplyChainRope(float delta)
	{
		Vector3 toPartner = _chainPartner!.GlobalPosition - GlobalPosition;
		float distance = toPartner.Length();
		float stretch = distance - SentryActions.ChainLength;
		if (stretch <= 0.0f || distance < 0.01f)
			return;

		Vector3 direction = toPartner / distance;

		// Moving away with the chain already taut: that component of the velocity is spent.
		float away = -LinearVelocity.Dot(direction);
		if (away > 0.0f)
			LinearVelocity += direction * away;

		LinearVelocity += direction * (Mathf.Min(stretch * ChainSpringRate, ChainMaxPull) * delta);
	}

	/// <summary>An unmissable glow while the car is a bumper, built on first use.</summary>
	private void UpdateBouncyAura()
	{
		bool active = IsBouncyActive;

		if (_bouncyAura == null)
		{
			if (!active)
				return;

			_bouncyAura = new MeshInstance3D
			{
				Name = "BouncyAura",
				Mesh = new SphereMesh
				{
					Radius = 3.2f,
					Height = 6.4f,
					Material = new StandardMaterial3D
					{
						AlbedoColor = new Color(1.0f, 0.45f, 0.05f, 0.28f),
						Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
						ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					},
				},
			};
			AddChild(_bouncyAura);
		}

		_bouncyAura.Visible = active;
		if (active)
		{
			float pulse = 1.0f + 0.12f * Mathf.Sin(Time.GetTicksMsec() * 0.012f);
			_bouncyAura.Scale = Vector3.One * pulse;
		}
	}

	private static readonly Color BoosterAuraColor = new(0.15f, 0.65f, 1.0f, 0.30f);
	private static readonly Color WiresAuraColor = new(0.70f, 0.20f, 1.0f, 0.30f);

	/// <summary>
	/// One glow shared by the timed debuffs, colour-coded per affliction and shown on every
	/// screen — the telegraph is as much for the racers around the victim as for the victim.
	/// Strobes hard through the fuse (the "get ready" the lead time exists for), then holds a
	/// steady pulse while the debuff runs. Wires outrank the booster when both are on: a
	/// two-colour strobe would read as a bug, not as two debuffs.
	/// </summary>
	private void UpdateDebuffAura()
	{
		bool wires = IsWiresActive || IsWiresIncoming;
		bool booster = IsBoosterActive || IsBoosterIncoming;

		if (!wires && !booster)
		{
			if (_debuffAura != null)
				_debuffAura.Visible = false;
			return;
		}

		if (_debuffAura == null)
		{
			_debuffAuraMaterial = new StandardMaterial3D
			{
				AlbedoColor = WiresAuraColor,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			};
			_debuffAura = new MeshInstance3D
			{
				Name = "DebuffAura",
				Mesh = new SphereMesh { Radius = 3.2f, Height = 6.4f, Material = _debuffAuraMaterial },
			};
			AddChild(_debuffAura);
		}

		_debuffAuraMaterial!.AlbedoColor = wires ? WiresAuraColor : BoosterAuraColor;

		bool incoming = wires ? IsWiresIncoming : IsBoosterIncoming;
		float t = Time.GetTicksMsec() * 0.001f;

		_debuffAura.Visible = !incoming || Mathf.Sin(t * 24.0f) > 0.0f;
		_debuffAura.Scale = Vector3.One * (1.0f + 0.12f * Mathf.Sin(t * 12.0f));
	}
}
