using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using MasterTrack.Sentry;

namespace MasterTrack.Tiles.Hazards;

/// <summary>
/// A rocket turret on a column beside the road, joined to it by a short bridge — the device
/// Tower Defense mode is built around, and the first one in the kit that <b>plays itself</b>.
///
/// Every other rigged device is a button the sentry presses at a moment of their choosing; this
/// one acquires, leads and fires on its own. That moves the whole decision into the rig phase,
/// which is the point of the mode: a turret is bought for <i>where</i> it stands, and once the
/// flag drops the builder is a spectator of their own plan. It is also why it is the dearest
/// thing on the shelf — see <see cref="HazardKindExtensions.PriceOf"/>.
///
/// <b>Split of authority.</b> Aiming runs on every peer off the replicated car poses, so the
/// barrel visibly tracks on every screen and costs the wire nothing. Only <i>who</i> and
/// <i>when</i> are decided — by the server, once, and broadcast — because two peers resolving a
/// near-tie differently would be two peers firing different numbers of rockets. Same split the
/// rest of the kit uses: the event crosses the wire, the theatre is rebuilt locally.
///
/// <b>What makes it fair.</b> The traverse is deliberately slow and the rocket only mildly
/// homing (see <see cref="TowerRocket"/>). A driver who sees the tower and commits gets past it;
/// a driver who lifts gets hit. A turret that always connected would be a toll rather than a
/// hazard, and the rest of the game is built on hazards you can out-drive.
/// </summary>
public partial class RocketTowerHazard : TrackHazard
{
	/// <summary>How far the turret will shoot, in metres — about two and a half tiles. Short
	/// enough that a tower owns its own corner and not the whole track, which is what keeps the
	/// builder's placement a real decision.</summary>
	private const float Range = TrackTile.Size * 2.5f;

	/// <summary>
	/// Seconds between shots. Long on purpose: a pack takes roughly five seconds to cross a
	/// turret's whole circle at racing speed, so this is <b>about one shot per car that comes
	/// past</b> — which is what makes each one an event rather than weather.
	///
	/// A turret that chattered would make the road impassable rather than dangerous, and it would
	/// spoil the slow rocket besides: at <see cref="TowerRocket.Speed"/> a shot is nearly three
	/// seconds in the air, so a short reload would just stack rockets over the same car and turn
	/// a thing you dodge into a thing you endure.
	/// </summary>
	private const float ReloadSeconds = 7.0f;

	/// <summary>
	/// How fast the head can turn, in degrees per second. <b>The counterplay number.</b> A car at
	/// racing speed crossing close by out-runs the traverse and eats the shot behind it, so the
	/// answer to a turret is commitment — exactly the answer the rest of the game's hazards want.
	/// Raise it and the tower becomes a tax; drop it and it never hits anybody.
	/// </summary>
	private const float TraverseDegrees = 90.0f;

	/// <summary>How far off aim the barrel may be and still fire, in degrees. Tight enough that a
	/// turret still swinging does not get a free shot at whatever it happens to sweep past.</summary>
	private const float AimToleranceDegrees = 4.0f;

	/// <summary>
	/// How far the barrel may drop and rise, in degrees. The floor is what stops a steep shot
	/// being fired down through the turret's own deck, and it doubles as a rule worth having:
	/// <b>there is a dead cone directly under a tower</b>. A car that gets right underneath one
	/// is safe from it, so a driver who reads the road can take the inside line past a turret and
	/// buy themselves the pass. That is counterplay for free, out of a geometry constraint.
	/// </summary>
	private const float MinPitchDegrees = -55.0f;
	private const float MaxPitchDegrees = 40.0f;

	/// <summary>
	/// How high the deck stands above the road, in metres — <b>the number that makes it a tower
	/// rather than a gantry</b>. Read against the road's own 54 m width: at this height the
	/// column is about five times its own thickness and the turret is looking properly down on
	/// the traffic, which is the silhouette the mode is named for.
	///
	/// It is also why the barrel needs so much droop, and why there is a dead cone underneath —
	/// see <see cref="MinPitchDegrees"/>.
	/// </summary>
	private const float DeckHeight = 26.0f;

	/// <summary>How far the column carries on below the road. The track floats, so this ends in
	/// air rather than at any ground — long enough to read as a support and be lost in the haze.</summary>
	private const float ColumnDrop = 60.0f;

	/// <summary>The landing at road level, where the bridge arrives and the column starts up.</summary>
	private const float LandingHeight = 0.4f;
	private const float LandingRadius = 5.0f;

	/// <summary>The deck up top. Kept narrow — barely wider than the turret standing on it — so a
	/// steep shot clears its own lip instead of appearing out of the floor.</summary>
	private const float DeckRadius = 3.6f;

	/// <summary>How far the turret's pivot sits above the deck. Clearance, for the same reason.</summary>
	private const float HeadRise = 3.0f;

	/// <summary>Distance from the head's centre to the muzzles, in metres.</summary>
	private const float MuzzleForward = 3.6f;

	private Node3D _head = null!;
	private Node3D _barrel = null!;
	private MeshInstance3D _flash = null!;
	private MeshInstance3D _reach = null!;

	/// <summary>Every resource wrapper this built, kept only so it can be let go of on the way
	/// out — see <see cref="_ExitTree"/>.</summary>
	private readonly System.Collections.Generic.List<Resource> _made = new();

	private float _reload;
	private float _flashLeft;
	private TrackController? _track;

	/// <summary>Not a rigged device: nothing about it answers to the sentry's Fire mode, and the
	/// board must not offer it as something to press.</summary>
	public override bool CanDetonate => false;

	/// <summary>
	/// Built in the constructor rather than in <c>_Ready</c>, which the code-built hazards
	/// otherwise do — because the <b>ghost</b> is made before the node ever enters the tree.
	/// <see cref="TrackHazard.MakeGhost"/> walks the children it can see and paints them
	/// translucent, so a hazard with no children yet ghosts into a fully solid preview sitting on
	/// the road. The scene-backed hazards do not have the problem: instancing gives them their
	/// children immediately. This does the same, only from code.
	/// </summary>
	public RocketTowerHazard() => BuildTower();

	public override void _Ready()
	{
		StandUpright();
		BuildReachRing();

		// Staggered off its own address so a row of towers does not volley in unison — the same
		// answer arrived at from the same data on every peer, which is what makes it safe to do
		// locally rather than send. A wall of simultaneous rockets reads as one event; the same
		// rockets a beat apart read as a gauntlet.
		int seed = Address.TileIndex * 7 + Address.SlotIndex * 3;
		_reload = ReloadSeconds * (0.35f + 0.65f * (Mathf.Abs(seed) % 17) / 17.0f);
	}

	/// <summary>
	/// Level the tower to the world, keeping the direction the road is in.
	///
	/// The slot frame is built flat, but the tile carrying it need not be — a piece on a climb
	/// hands its hazards its own pitch, and a hazard that sits <i>on</i> the road wants exactly
	/// that (a launch pad in a banked corner should throw you off the bank). A column does not:
	/// it is a thing standing in the world beside the road, and inheriting a slope would lean the
	/// whole tower over the track. So this is the one hazard that argues with its slot.
	/// </summary>
	private void StandUpright()
	{
		Vector3 towardRoad = GlobalBasis.X;
		var flat = new Vector3(towardRoad.X, 0.0f, towardRoad.Z);

		flat = flat.LengthSquared() < 0.0001f ? Vector3.Right : flat.Normalized();
		GlobalBasis = new Basis(flat, Vector3.Up, flat.Cross(Vector3.Up));
	}

	/// <summary>
	/// The tower, built in code out of faceted CSG solids like the rest of the kit's props —
	/// per-vertex light, no specular, no smoothing. Laid out in the pylon frame, where <c>+X</c>
	/// points at the road (see <see cref="HazardSlotKind.Pylon"/>), so the bridge simply runs out
	/// along <c>+X</c> until it meets the road edge.
	/// </summary>
	private void BuildTower()
	{
		StandardMaterial3D steel = Paint(new Color(0.42f, 0.45f, 0.50f));
		StandardMaterial3D dark = Paint(new Color(0.17f, 0.18f, 0.21f));
		StandardMaterial3D warn = Paint(new Color(0.92f, 0.62f, 0.11f));
		StandardMaterial3D red = Paint(new Color(0.78f, 0.13f, 0.11f));

		// One column, running the whole height of the thing: from well below the road, up past
		// the landing the bridge arrives at, to the deck the turret stands on. Built as a single
		// solid rather than a stack so there is no seam to line up as the numbers get tuned.
		float columnTop = DeckHeight - 0.55f;
		float columnBottom = LandingHeight - ColumnDrop;

		AddChild(new CsgCylinder3D
		{
			Name = "Column",
			Sides = 8, SmoothFaces = false,
			Radius = 2.8f, Height = columnTop - columnBottom,
			Material = steel,
			Position = new Vector3(0.0f, (columnTop + columnBottom) * 0.5f, 0.0f),
		});

		// Bands up the shaft. Pure silhouette: an unbroken cylinder twenty-six metres tall reads
		// as a pipe, and the same cylinder with two collars on it reads as a structure.
		foreach (float height in new[] { DeckHeight * 0.35f, DeckHeight * 0.68f })
		{
			AddChild(new CsgCylinder3D
			{
				Name = $"Band{height:0}",
				Sides = 8, SmoothFaces = false,
				Radius = 3.3f, Height = 1.0f,
				Material = warn,
				Position = new Vector3(0.0f, height, 0.0f),
			});
		}

		// The landing at the bottom, where the bridge meets the tower.
		AddChild(new CsgCylinder3D
		{
			Name = "Landing",
			Sides = 8, SmoothFaces = false,
			Radius = LandingRadius, Height = 1.2f,
			Material = steel,
			Position = new Vector3(0.0f, LandingHeight - 0.6f, 0.0f),
		});

		// The deck up top, and a lip around its rim so the silhouette reads from altitude.
		AddChild(new CsgCylinder3D
		{
			Name = "Deck",
			Sides = 8, SmoothFaces = false,
			Radius = DeckRadius, Height = 1.1f,
			Material = steel,
			Position = new Vector3(0.0f, DeckHeight - 0.55f, 0.0f),
		});
		AddChild(new CsgTorus3D
		{
			Name = "Rail",
			Sides = 8, RingSides = 10, SmoothFaces = false,
			InnerRadius = DeckRadius - 0.5f, OuterRadius = DeckRadius,
			Material = warn,
			Position = new Vector3(0.0f, DeckHeight + 0.25f, 0.0f),
		});

		// The bridge out to the road, at road level — the tower goes up from where it lands, so
		// the walk across it stays flat. Reaches a little past the road's edge, so it visibly
		// arrives on the tarmac rather than stopping a metre short of it.
		float roadEdge = Tool.PieceCatalog.PylonOffset - TileCatalog.TileSize * 0.5f;
		float span = roadEdge + 1.5f - LandingRadius;

		AddChild(new CsgBox3D
		{
			Name = "Bridge",
			Size = new Vector3(span, 0.6f, 6.0f),
			Material = steel,
			Position = new Vector3(LandingRadius + span * 0.5f, LandingHeight - 0.3f, 0.0f),
		});

		for (var side = -1; side <= 1; side += 2)
		{
			AddChild(new CsgBox3D
			{
				Name = $"BridgeRail{(side < 0 ? "L" : "R")}",
				Size = new Vector3(span, 0.7f, 0.5f),
				Material = warn,
				Position = new Vector3(LandingRadius + span * 0.5f, LandingHeight + 0.3f, side * 2.9f),
			});
		}

		// The turret: a head that yaws, carrying a cradle that pitches.
		_head = new Node3D
		{
			Name = "Head",
			Position = new Vector3(0.0f, DeckHeight + HeadRise, 0.0f),
		};
		AddChild(_head);

		// The pedestal, filling the gap from the deck up to the pivot — the clearance the steep
		// shots need has to look like it is holding something up.
		_head.AddChild(new CsgCylinder3D
		{
			Name = "Pedestal",
			Sides = 8, SmoothFaces = false,
			Radius = 2.2f, Height = HeadRise,
			Material = dark,
			Position = new Vector3(0.0f, -HeadRise * 0.5f, 0.0f),
		});
		_head.AddChild(new CsgBox3D
		{
			Name = "Housing",
			Size = new Vector3(3.4f, 2.2f, 3.6f),
			Material = red,
		});

		_barrel = new Node3D { Name = "Barrel", Position = new Vector3(0.0f, 0.3f, 0.0f) };
		_head.AddChild(_barrel);

		// Two pods, pointing down the head's -Z — the direction everything in this game that has
		// a front points.
		for (var side = -1; side <= 1; side += 2)
		{
			_barrel.AddChild(new CsgBox3D
			{
				Name = $"Pod{(side < 0 ? "L" : "R")}",
				Size = new Vector3(1.1f, 1.1f, 4.0f),
				Material = dark,
				// Ends where the muzzle is, so the flash sits at the mouth of the tube.
				Position = new Vector3(side * 1.1f, 0.0f, -(MuzzleForward - 2.0f)),
			});
		}

		// The muzzle flash, parked invisible on the barrel: unshaded, like every glow on the board.
		var glow = new StandardMaterial3D
		{
			AlbedoColor = new Color(1.0f, 0.72f, 0.22f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		var ball = new SphereMesh
		{
			Radius = 1.5f, Height = 3.0f, RadialSegments = 8, Rings = 4,
			Material = glow,
		};

		_made.Add(glow);
		_made.Add(ball);

		_flash = new MeshInstance3D
		{
			Name = "Flash",
			Mesh = ball,
			Position = new Vector3(0.0f, 0.0f, -MuzzleForward),
			Visible = false,
		};
		_barrel.AddChild(_flash);
	}

	/// <summary>
	/// The circle on the ground showing how much road this turret covers — the thing that makes
	/// placing towers a decision rather than a guess. Where the gaps between circles fall <i>is</i>
	/// the builder's plan, and until you can see them you are furnishing a track blind.
	///
	/// Drawn at road level rather than at the deck, and sized for road level too: the range is a
	/// distance in three dimensions from a turret standing twenty-nine metres up, so the circle a
	/// car can actually be caught in is the base of that cone, not the range itself. Promising a
	/// metre of road the turret cannot reach would be worse than drawing nothing.
	///
	/// Built in <c>_Ready</c> rather than the constructor, unlike the rest of the tower, because
	/// <see cref="TrackHazard.MakeGhost"/> repaints every child it can see — and it runs before
	/// the node is in the tree. Building the ring afterwards is what keeps it its own colour on
	/// the preview instead of turning ghost-green with the machinery.
	/// </summary>
	private void BuildReachRing()
	{
		// The cone's base: how far from the mast a car on the road can still be inside range.
		float head = DeckHeight + HeadRise;
		float ground = Mathf.Sqrt(Mathf.Max(Range * Range - head * head, 1.0f));

		var paint = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.98f, 0.62f, 0.13f, 0.40f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			// Drawn through the road, the slot markers' rule: a circle that reached over a crest
			// and vanished would be lying about the half of it you cannot see.
			NoDepthTest = true,
		};

		var ring = new TorusMesh
		{
			InnerRadius = ground - 1.8f,
			OuterRadius = ground,

			// Rings slices the big circumference; RingSegments is the tube's cross-section — and
			// they are easy to read the wrong way round. At two hundred and sixty metres across
			// the circle needs every slice it can get, while the tube is a 1.8 m ribbon seen from
			// directly above and needs almost none.
			Rings = 128,
			RingSegments = 8,
			Material = paint,
		};

		_made.Add(paint);
		_made.Add(ring);

		_reach = new MeshInstance3D
		{
			Name = "Reach",
			Mesh = ring,
			Position = new Vector3(0.0f, 0.6f, 0.0f),
		};
		AddChild(_reach);
	}

	/// <summary>The house material, remembered so it can be released while the engine is still
	/// alive — the standing rule for anything refcounted this side of shutdown.</summary>
	private StandardMaterial3D Paint(Color colour)
	{
		StandardMaterial3D material = HouseMaterial(colour);
		_made.Add(material);
		return material;
	}

	/// <summary>Let go of the wrappers while the engine is still there to free them. A refcounted
	/// resource left to .NET shutdown is disposed after native teardown, which can take the
	/// process down on exit.</summary>
	public override void _ExitTree()
	{
		foreach (Resource resource in _made)
			resource.Dispose();

		_made.Clear();
	}

	public override void _Process(double delta)
	{
		var step = (float)delta;

		if (_flashLeft > 0.0f)
		{
			_flashLeft -= step;
			_flash.Visible = _flashLeft > 0.0f;
		}

		// The reach circles are a planning instrument, so they live exactly as long as the
		// planning does. Every tower shows one while the builder is still laying things out —
		// the gaps between them are the plan — and they all go at the drop of the flag, because
		// by then the decision is made and what is left is a race to watch.
		//
		// The ghost never reaches this line: MakeGhost switches its processing off, so the
		// preview's own circle simply stays up. That is the one place a circle is always wanted.
		MatchPhase phase = GameManager.Instance.Phase;
		bool planning = phase is MatchPhase.Building or MatchPhase.Rigging;
		_reach.Visible = planning;

		// Nothing to shoot at while the track is being laid or the rig is going in. Phase is
		// None in Live Build, where there is no rig phase and the racing never stops.
		if (planning)
			return;

		RacerController? target = PickTarget();
		bool onTarget = AimAt(target, step);

		// Only the server decides that a shot happened. Every peer has just aimed at the same car
		// off the same replicated poses, so the barrel is already pointing the right way when the
		// broadcast lands.
		_reload -= step;

		if (!NetworkManager.Instance.IsNetworked || NetworkManager.Instance.IsHost)
		{
			if (onTarget && _reload <= 0.0f && target != null)
			{
				_reload = ReloadSeconds;
				Track?.FireTower(Address.TileIndex, Address.SlotIndex, target.OwnerPeerId);
			}
		}
	}

	/// <summary>
	/// The nearest racer in range, ties broken by peer id so every peer picks the same one.
	///
	/// Nearest rather than furthest along or worst-placed, because nearest is the rule a driver
	/// can <i>read</i>: whoever is closest to the tower is the one being shot at, so leading the
	/// pack past one costs you something and everybody behind gets the gift. That is the kit's
	/// standing rule — every device that punishes somebody should reward somebody else — arrived
	/// at without any bookkeeping.
	/// </summary>
	private RacerController? PickTarget()
	{
		Vector3 from = _head.GlobalPosition;
		RacerController? best = null;
		float bestDistance = Range;

		foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
		{
			if (node is not RacerController racer || !racer.IsInsideTree() || racer.IsEliminated)
				continue;

			if (GameManager.Instance.HasFinished(racer.OwnerPeerId))
				continue;

			float distance = racer.GlobalPosition.DistanceTo(from);
			if (distance > bestDistance)
				continue;

			// The tie-break exists for the frame two cars are equidistant, which is rarer than it
			// is important: peers disagreeing there would be peers aiming two different ways.
			if (distance < bestDistance || best == null || racer.OwnerPeerId < best.OwnerPeerId)
			{
				bestDistance = distance;
				best = racer;
			}
		}

		return best;
	}

	/// <summary>
	/// Swing the head and cradle toward where the target is going to be, capped by the traverse
	/// rate. Returns whether the barrel is close enough to aim to fire.
	///
	/// The lead is two passes of the obvious estimate — guess a flight time from the present
	/// distance, re-measure against where that puts the car, and use that. Good enough to look
	/// aimed, and wrong the moment the driver changes anything, which is the intent.
	/// </summary>
	private bool AimAt(RacerController? target, float delta)
	{
		float traverse = Mathf.DegToRad(TraverseDegrees) * delta;

		if (target == null)
		{
			// Idle: settle level and sweep slowly back to facing the road, so a tower with
			// nothing to do still reads as a machine rather than a statue.
			_barrel.Rotation = new Vector3(
				Mathf.MoveToward(_barrel.Rotation.X, 0.0f, traverse), 0.0f, 0.0f);

			// Parked facing the road: +X is where the road is, and -Z is the barrel's front.
			_head.Rotation = new Vector3(
				0.0f, Mathf.MoveToward(_head.Rotation.Y, -Mathf.Pi * 0.5f, traverse * 0.35f), 0.0f);
			return false;
		}

		Vector3 muzzle = _barrel.GlobalPosition;
		Vector3 lead = target.GlobalPosition;

		for (var pass = 0; pass < 2; pass++)
		{
			float flight = muzzle.DistanceTo(lead) / TowerRocket.Speed;
			lead = target.GlobalPosition + target.EffectiveVelocity * flight;
		}

		// Measured from the barrel's own pivot, in the tower's frame: the pivot sits on the
		// tower's axis, so the yaw is the same either way and the pitch comes out honest.
		Vector3 local = ToLocal(lead) - ToLocal(muzzle);
		float flat = new Vector2(local.X, local.Z).Length();

		// -Z is the front, so a target dead ahead is yaw 0 — the same convention the cars and the
		// missile are authored to.
		float wantYaw = Mathf.Atan2(-local.X, -local.Z);
		float wantPitch = Mathf.Clamp(Mathf.Atan2(local.Y, flat),
									  Mathf.DegToRad(MinPitchDegrees),
									  Mathf.DegToRad(MaxPitchDegrees));

		float yawError = AngleDelta(_head.Rotation.Y, wantYaw);
		float pitchError = AngleDelta(_barrel.Rotation.X, wantPitch);

		_head.Rotation = new Vector3(
			0.0f, _head.Rotation.Y + Mathf.Clamp(yawError, -traverse, traverse), 0.0f);
		_barrel.Rotation = new Vector3(
			_barrel.Rotation.X + Mathf.Clamp(pitchError, -traverse, traverse), 0.0f, 0.0f);

		float tolerance = Mathf.DegToRad(AimToleranceDegrees);
		return Mathf.Abs(yawError) <= tolerance && Mathf.Abs(pitchError) <= tolerance;
	}

	/// <summary>The shortest way round from one angle to another, in radians.</summary>
	private static float AngleDelta(float from, float to)
		=> Mathf.Wrap(to - from, -Mathf.Pi, Mathf.Pi);

	/// <summary>
	/// Put a rocket in the air, on every peer, from one broadcast — see
	/// <c>TrackController.FireTower</c>. Nothing here may consult anything local: the target came
	/// down the wire precisely so that every machine launches the same rocket at the same car.
	/// </summary>
	public void FireAt(int targetPeerId)
	{
		_flashLeft = 0.07f;
		_flash.Visible = true;

		var rocket = new TowerRocket { TargetPeerId = targetPeerId, Name = "TowerRocket" };
		(GetTree().CurrentScene ?? GetTree().Root).AddChild(rocket);

		// Placed after it is in the tree, because the muzzle is a global position and the rocket
		// flies in world space — it is leaving the tower, not riding it.
		rocket.GlobalPosition = _barrel.GlobalPosition
								+ _barrel.GlobalBasis.Z * -MuzzleForward;
		rocket.Launch(-_barrel.GlobalBasis.Z);
	}

	/// <summary>The track, found once and kept — the group lookup the racers use for the same
	/// job. Only the server ever needs it.</summary>
	private TrackController? Track
	{
		get
		{
			if (_track == null || !IsInstanceValid(_track))
				_track = GetTree().GetFirstNodeInGroup(TrackController.GroupName) as TrackController;

			return _track;
		}
	}
}
