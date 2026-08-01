using System.Collections.Generic;
using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// An RC whip antenna with a ball on the end, faked with a spring rather than simulated.
///
/// The obvious build — a chain of little rigid bodies pinned together — would be real physics
/// spent on something purely cosmetic, and joint chains hate exactly what this game does to them:
/// 2× gravity, nitro impulses, hard landings. This instead tracks one point, the ball, as a
/// spring in world space. The mount moves with the car; the ball lags behind it, gets pulled back
/// by the spring, and the shaft is bent to connect the two. Every behaviour an antenna should
/// have — whipping back off the line, flopping forward under braking, swinging wide in a drift,
/// shuddering on a landing — falls out of the mount moving and the ball not keeping up. Nothing
/// here reads the vehicle at all.
///
/// Put it under <c>BodyRig</c>, not the rigid body: BodyLean's exaggerated pose then moves the
/// mount too, so the antenna answers the fake weight transfer as well as the real motion.
///
/// The shaft is a chain of tapered cylinder segments, each bent a little more than the one below
/// it, so it curves like a whip instead of tilting like a stick. Meshes are built in _Ready the
/// same way NitroFlame builds its cones; FlatShade restyles them along with everything else and
/// keeps their colours.
/// </summary>
[GlobalClass]
public partial class AntennaWobble : Node3D
{
	// ---------------------------------------------------------------- Shape

	/// <summary>Length of the shaft, in metres. The rest tip sits this far up the local Y axis.</summary>
	[Export] public float Length { get; set; } = 0.95f;

	/// <summary>
	/// Cylinder segments in the shaft. More bends smoother; five is already a curve at this size.
	/// </summary>
	[Export] public int Segments { get; set; } = 5;

	/// <summary>Shaft radius at the mount, in metres.</summary>
	[Export] public float BaseRadius { get; set; } = 0.02f;

	/// <summary>Shaft radius at the tip. Thinner than the base, so the whip reads as a whip.</summary>
	[Export] public float TipRadius { get; set; } = 0.009f;

	/// <summary>Radius of the ball on the end.</summary>
	[Export] public float BallRadius { get; set; } = 0.06f;

	/// <summary>Shaft colour. Defaults to the same near-black the cars use for trim.</summary>
	[Export] public Color ShaftColour { get; set; } = new(0.08f, 0.08f, 0.09f);

	/// <summary>Ball colour. Red regardless of livery, like every RC car ever sold.</summary>
	[Export] public Color BallColour { get; set; } = new(0.85f, 0.09f, 0.12f);

	// ---------------------------------------------------------------- Spring

	/// <summary>
	/// Spring rate pulling the ball back over the mount, in 1/s². √(this)/2π is the wobble
	/// frequency: 380 is about 3 Hz, a fast flutter rather than a slow sway.
	/// </summary>
	[Export] public float Stiffness { get; set; } = 380.0f;

	/// <summary>
	/// Velocity decay per second. Low on purpose — an antenna that settles in one bounce reads as
	/// stiff plastic; this one rings for a moment after a landing.
	/// </summary>
	[Export] public float Damping { get; set; } = 3.5f;

	/// <summary>
	/// Downward pull on the ball, in m/s². What makes the antenna sag downhill on a banked wall
	/// and float straight during a jump. Matches the cars' doubled gravity rather than the world's.
	/// </summary>
	[Export] public float GravityPull { get; set; } = 20.0f;

	/// <summary>
	/// Furthest the shaft may bend off vertical, in degrees. The cap is what keeps a monster hit
	/// from folding the antenna flat into the bodywork — or into the wing behind it.
	/// </summary>
	[Export] public float MaxSwingDegrees { get; set; } = 55.0f;

	/// <summary>One joint per segment; each is a child of the one before, so bends compound.</summary>
	private readonly List<Node3D> _joints = new();

	/// <summary>The ball's position and velocity, in world space. This is the whole simulation.</summary>
	private Vector3 _tip;
	private Vector3 _tipVel;

	/// <summary>False until the first frame has snapped the ball to its rest point.</summary>
	private bool _primed;

	public override void _Ready()
	{
		var shaftMaterial = FlatMaterial(ShaftColour);
		float segmentLength = Length / Segments;

		Node3D parent = this;
		for (int i = 0; i < Segments; i++)
		{
			var joint = new Node3D
			{
				Name = $"Joint{i}",
				// The first joint sits on the mount; each one after sits on top of the segment below.
				Position = i == 0 ? Vector3.Zero : new Vector3(0.0f, segmentLength, 0.0f),
			};
			parent.AddChild(joint);
			_joints.Add(joint);
			parent = joint;

			// Radii picked so the taper runs continuously across the segment boundaries.
			float bottom = Mathf.Lerp(BaseRadius, TipRadius, (float)i / Segments);
			float top = Mathf.Lerp(BaseRadius, TipRadius, (float)(i + 1) / Segments);
			joint.AddChild(new MeshInstance3D
			{
				Name = "Shaft",
				Position = new Vector3(0.0f, segmentLength * 0.5f, 0.0f),
				Mesh = new CylinderMesh
				{
					BottomRadius = bottom,
					TopRadius = top,
					Height = segmentLength,
					RadialSegments = 6,
					Rings = 1,
					Material = shaftMaterial,
				},
				// A centimetre-wide sliver of shadow is nothing but shimmer.
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			});
		}

		// The ball, impaled on the top of the last segment.
		parent.AddChild(new MeshInstance3D
		{
			Name = "Ball",
			Position = new Vector3(0.0f, segmentLength, 0.0f),
			Mesh = new SphereMesh
			{
				Radius = BallRadius,
				Height = BallRadius * 2.0f,
				RadialSegments = 8,
				Rings = 4,
				Material = FlatMaterial(BallColour),
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});

		// A stub at the mount so the shaft grows out of a fitting rather than straight out of paint.
		AddChild(new MeshInstance3D
		{
			Name = "Mount",
			Position = new Vector3(0.0f, 0.03f, 0.0f),
			Mesh = new CylinderMesh
			{
				BottomRadius = 0.035f,
				TopRadius = 0.03f,
				Height = 0.06f,
				RadialSegments = 6,
				Rings = 1,
				Material = shaftMaterial,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});
	}

	/// <summary>
	/// Same three decisions FlatShade makes, made up front — FlatShade will rebuild these anyway
	/// when it restyles the car, but building them flat means they never render shiny, not even
	/// for the first frame.
	/// </summary>
	private static StandardMaterial3D FlatMaterial(Color colour) => new()
	{
		AlbedoColor = colour,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
		SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
		Metallic = 0.0f,
		Roughness = 1.0f,
	};

	public override void _Process(double delta)
	{
		// Clamped so a hitch can't feed the integrator a giant step and launch the ball.
		float dt = Mathf.Min((float)delta, 1.0f / 30.0f);
		if (dt <= 0.0f)
			return;

		Vector3 basePosition = GlobalPosition;
		Vector3 rest = GlobalTransform * new Vector3(0.0f, Length, 0.0f);

		// First frame, or the car teleported (respawn): don't whip across the map, just start over.
		if (!_primed || (_tip - rest).LengthSquared() > Length * Length * 9.0f)
		{
			_tip = rest;
			_tipVel = Vector3.Zero;
			_primed = true;
		}

		// Semi-implicit Euler: spring toward the rest point, gravity, exponential drag.
		_tipVel += (rest - _tip) * (Stiffness * dt);
		_tipVel += Vector3.Down * (GravityPull * dt);
		_tipVel *= Mathf.Exp(-Damping * dt);
		_tip += _tipVel * dt;

		// Everything below turns the ball's position into a bend: direction in mount space...
		Vector3 offset = _tip - basePosition;
		Basis mountBasis = GlobalBasis;
		Vector3 localDirection = offset.LengthSquared() > 0.000001f
			? (mountBasis.Transposed() * offset).Normalized()
			: Vector3.Up;

		float bend = Mathf.Acos(Mathf.Clamp(localDirection.Y, -1.0f, 1.0f));
		Vector3 axis = Vector3.Up.Cross(localDirection);

		// Straight up leaves no bend axis to speak of, and no bend worth drawing either.
		if (axis.LengthSquared() < 0.000001f)
		{
			foreach (Node3D joint in _joints)
				joint.Basis = Basis.Identity;
			_tip = basePosition + mountBasis * (Vector3.Up * Length);
			return;
		}
		axis = axis.Normalized();

		float maxBend = Mathf.DegToRad(MaxSwingDegrees);
		if (bend > maxBend)
		{
			bend = maxBend;
			localDirection = Vector3.Up.Rotated(axis, maxBend);
		}

		// The ball is constrained to the shaft, not free: pin it back onto the sphere the tip can
		// actually reach, so the spring next frame works from where the ball is drawn.
		_tip = basePosition + mountBasis * (localDirection * Length);

		// Distribute the bend up the chain, each joint taking more than the one below — weights
		// 1, 2, …, n — which is roughly how a real whip loads and what makes it curve. Rotating
		// about an axis leaves that axis where it was, so the same local axis is right for every
		// joint even though each is expressed in the frame of the one before.
		float weightSum = Segments * (Segments + 1) * 0.5f;
		for (int i = 0; i < _joints.Count; i++)
			_joints[i].Basis = new Basis(axis, bend * (i + 1) / weightSum);
	}
}
