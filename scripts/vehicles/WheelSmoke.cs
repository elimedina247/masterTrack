using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Puffs tire smoke out of any corner that's on the ground while the car is sliding. One particle
/// system covers all four: each particle is emitted manually at that corner's contact point with
/// a velocity that trails the way the car is scrubbing.
///
/// There is no per-wheel slip to read any more — the car has one sideways velocity and one grip
/// number — so how hard it is sliding comes from <see cref="TireSlip"/>, the same place the skid
/// marks and the squeal get it, and all three agree on what counts as a drift.
///
/// What each corner does with that is <see cref="OutsideWheelsOnly"/>: only the pair on the
/// outside of the slide smokes. That is where the load goes when a car is thrown sideways — the
/// inside wheels unload and go light — so smoke pouring off all four reads as a car that is
/// simply broken loose, where two reads as one being steered on the throttle.
///
/// The particle look is built in <see cref="_Ready"/> rather than authored into the car scene, for
/// the same reason the track builds its own geometry: one definition, and any Vehicle that wants
/// smoke gets it by having one of these as a child.
/// </summary>
[GlobalClass]
public partial class WheelSmoke : GpuParticles3D
{
	/// <summary>The vehicle to watch. Required.</summary>
	[Export] public Vehicle? VehicleNode { get; set; }

	/// <summary>Sideways speed in m/s past which the tires start to smoke.</summary>
	[Export] public float SlipThreshold { get; set; } = 3.0f;

	/// <summary>How far past the threshold sideways speed reaches full smoke, in m/s.</summary>
	[Export] public float FullSlip { get; set; } = 9.0f;

	/// <summary>
	/// How much of the car's scrub velocity a particle inherits. Well under 1: smoke is left
	/// behind by the car, it doesn't travel with it.
	/// </summary>
	[Export] public float ScrubInherit { get; set; } = 0.25f;

	/// <summary>
	/// Puffs per second from one corner at full slip, scaled down by how hard the car is actually
	/// sliding. A rate rather than one per frame, so the smoke is the same density on a 60 Hz
	/// machine and a 144 Hz one — and so a car that has only just started to let go trails a wisp
	/// instead of the same wall of smoke as a full drift.
	/// </summary>
	[Export] public float EmissionRate { get; set; } = 45.0f;

	/// <summary>How long a puff lasts, in seconds.</summary>
	[Export] public float SmokeLifetime { get; set; } = 0.9f;

	/// <summary>Width of a puff at full size, in metres.</summary>
	[Export] public float SmokeSize { get; set; } = 0.9f;

	/// <summary>Colour of the smoke, and how opaque a fresh puff is.</summary>
	[Export] public Color SmokeColor { get; set; } = new(0.82f, 0.82f, 0.86f, 0.5f);

	/// <summary>
	/// Smoke from the outside pair of wheels only, rather than all four. Off restores every
	/// grounded corner smoking together.
	///
	/// Halves the number of emitters, so it also roughly halves the smoke — raise
	/// <see cref="EmissionRate"/> if the effect wants to be as thick as it was.
	/// </summary>
	[Export] public bool OutsideWheelsOnly { get; set; } = true;

	/// <summary>
	/// Fractional puffs owed to each corner, carried between frames.
	///
	/// Without this the rate would be rounded down to a whole particle every frame, which at any
	/// sane frame time is a rate of zero — the whole effect would simply never emit.
	/// </summary>
	private float[] _owed = System.Array.Empty<float>();

	public override void _Ready()
	{
		Lifetime = SmokeLifetime;

		// Enough for every corner to be emitting flat out with none dying early. Four corners at
		// the full rate, each alive for a whole lifetime.
		Amount = Mathf.Max(8, Mathf.CeilToInt(EmissionRate * SmokeLifetime * 4.0f));

		// World space: smoke is left on the road the car has already driven past. In local space
		// the whole cloud would be dragged along with the car, which is the one thing it must not
		// do — see also EmitSmoke, where the same choice decides what space the velocity is in.
		LocalCoords = false;

		// Manual emission only. Left true, the built-in emitter would also pour particles out of
		// the car's centre the entire time it was on screen, sliding or not.
		Emitting = false;

		ProcessMaterial = BuildProcessMaterial();
		DrawPass1 = BuildPuffMesh();
	}

	public override void _Process(double delta)
	{
		if (VehicleNode is not { IsVehicleReady: true } vehicle)
			return;

		if (_owed.Length != vehicle.WheelArray.Count)
			_owed = new float[vehicle.WheelArray.Count];

		int outside = OutsideWheelsOnly ? OutsideSide(vehicle) : 0;

		for (var i = 0; i < vehicle.WheelArray.Count; i++)
		{
			GroundRay ray = vehicle.WheelArray[i];
			float intensity = TireSlip.Intensity(vehicle, ray, SlipThreshold, FullSlip);

			if (intensity > 0.0f && outside != 0 && !IsOnSide(vehicle, ray, outside))
				intensity = 0.0f;

			if (intensity <= 0.0f)
			{
				// Nothing owed once a corner stops sliding: a wheel that scrubbed for an instant
				// should not fire a puff several seconds later when it next touches down.
				_owed[i] = 0.0f;
				continue;
			}

			_owed[i] += EmissionRate * intensity * (float)delta;

			while (_owed[i] >= 1.0f)
			{
				_owed[i] -= 1.0f;
				EmitSmoke(vehicle, ray);
			}
		}
	}

	/// <summary>
	/// Sideways speed below which the slide is too small to say which way it is going, in m/s.
	/// Only reachable through <see cref="TireSlip"/>'s drift floor — an actual slide has to clear
	/// <see cref="SlipThreshold"/> to register at all — but without it the outside would flip from
	/// side to side on noise in the instant a drift is pressed.
	/// </summary>
	private const float SideDeadzone = 1.0f;

	/// <summary>
	/// Which side of the car is on the outside of the slide: +1 right, −1 left, 0 for "can't say".
	///
	/// The outside is the side the car is sliding <i>toward</i>. Throw it into a right-hander and
	/// the nose comes round to the right of where the car is actually travelling, so relative to
	/// the body that travel points out to the left — and the left is the outside of the corner,
	/// which is where the load has gone and where the tires are being dragged hardest.
	///
	/// Read off the velocity rather than the steering, so it works for any slide and not only one
	/// with the drift button held — ice and a fast corner both smoke. <see cref="Vehicle.DriftDirection"/>
	/// is the fallback for the one moment velocity cannot answer: the instant a drift is pressed,
	/// before the car has built any sideways speed, which is exactly what
	/// <see cref="TireSlip"/>'s drift floor exists to cover.
	/// </summary>
	private static int OutsideSide(Vehicle vehicle)
	{
		if (Mathf.Abs(vehicle.LateralSpeed) > SideDeadzone)
			return vehicle.LateralSpeed > 0.0f ? 1 : -1;

		// +1 is a drift to the left, and the outside of a left-hander is the car's right — the
		// same sign, which is why this needs no flip.
		return vehicle.DriftDirection;
	}

	/// <summary>
	/// Whether a corner sits on the given side of the car. Local +X is the right-hand side.
	///
	/// Taken through the vehicle's own space rather than off the ray's <c>Position</c>, so it does
	/// not quietly depend on the rays being direct children of the car.
	/// </summary>
	private static bool IsOnSide(Vehicle vehicle, GroundRay ray, int side)
		=> vehicle.ToLocal(ray.GlobalPosition).X * side > 0.0f;

	/// <summary>
	/// One puff at a corner's contact patch, trailing the way that corner is being dragged.
	/// </summary>
	private void EmitSmoke(Vehicle vehicle, GroundRay ray)
	{
		Transform3D smokeTransform = ray.GlobalTransform;
		smokeTransform.Origin = ray.LastCollisionPoint;

		// Sideways only: what makes smoke is the tire being dragged across the road, and with no
		// wheel spin to model that is the whole of it.
		//
		// Left in world space on purpose. Both the transform and the velocity handed to
		// EmitParticle are read in the emitter's coordinate space, and LocalCoords is false — so
		// rotating this into the node's basis, as this used to, aimed the scrub somewhere further
		// off true the harder the car was turned. Which is precisely when it shows.
		Vector3 velocity = vehicle.GlobalTransform.Basis.X * (-vehicle.LateralSpeed * ScrubInherit);

		EmitParticle(smokeTransform, velocity, Colors.White, Colors.White,
					 (uint)(EmitFlags.Position | EmitFlags.Velocity));
	}

	/// <summary>
	/// How a puff behaves once it exists: drifts up off the road, spreads, swells and fades out.
	/// </summary>
	private ParticleProcessMaterial BuildProcessMaterial()
	{
		var gradient = new Gradient();
		gradient.SetColor(0, SmokeColor);
		gradient.SetColor(1, SmokeColor with { A = 0.0f });

		// Swells as it goes. Smoke that stayed one size reads as a sprite rather than as something
		// coming off the tire.
		var scale = new Curve();
		scale.AddPoint(new Vector2(0.0f, 0.35f));
		scale.AddPoint(new Vector2(1.0f, 1.0f));

		return new ParticleProcessMaterial
		{
			Direction = Vector3.Up,
			Spread = 35.0f,
			InitialVelocityMin = 0.6f,
			InitialVelocityMax = 2.2f,

			// Rises gently rather than falling. This is smoke, not debris.
			Gravity = new Vector3(0.0f, 1.4f, 0.0f),

			// Kills the inherited scrub over the puff's life, so it is left behind on the road
			// instead of sailing off across the track.
			DampingMin = 2.0f,
			DampingMax = 4.0f,

			ScaleMin = SmokeSize * 0.7f,
			ScaleMax = SmokeSize * 1.3f,
			ScaleCurve = new CurveTexture { Curve = scale },

			AngularVelocityMin = -45.0f,
			AngularVelocityMax = 45.0f,

			ColorRamp = new GradientTexture1D { Gradient = gradient },
		};
	}

	/// <summary>
	/// The quad a puff is drawn on. Unshaded and billboarded — smoke that took the scene's lighting
	/// would go black in a tunnel, and one that did not face the camera would vanish edge-on.
	/// </summary>
	private static QuadMesh BuildPuffMesh() => new()
	{
		Size = Vector2.One,
		Material = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
			VertexColorUseAsAlbedo = true,
			// Particles are drawn in a heap on top of each other; depth-writing them would have
			// each puff cut a hole in the ones behind it.
			DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
			AlbedoColor = Colors.White,
		},
	};
}
