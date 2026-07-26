using System.Collections.Generic;
using Godot;
using MasterTrack.Racer;

namespace MasterTrack.Vehicles;

/// <summary>
/// Flame out of each exhaust while the nitro burns.
///
/// Cones rather than particles, and unlit rather than shaded. A soft particle plume is the usual
/// way to do this and it would be the one soft thing in a game made of flat facets — next to a
/// faceted car on a faceted track it reads as an effect from somewhere else. Three nested cones
/// pulsing at different rates give the same read in a handful of triangles and stay in the style.
///
/// Unshaded because fire is a light source, not a lit surface: a flame that got darker on the
/// shadowed side of the car would be a very strange flame.
///
/// It reads <see cref="RacerController.IsBoosting"/> rather than the vehicle's own nitro state, so
/// that it lights up on other people's cars too — see that property for why the difference matters.
/// </summary>
[GlobalClass]
public partial class NitroFlame : Node3D
{
	/// <summary>The car whose nitro to watch. Required.</summary>
	[Export] public RacerController? VehicleNode { get; set; }

	/// <summary>
	/// Where one exhaust <i>mouth</i> sits, in car space, mirrored across X for the other — which
	/// is all any of the bodies in <c>assets/cars</c> need.
	///
	/// The default is C_Cartoon's, and it is the back face of the pipe rather than its centre:
	/// the pipe runs from Z 1.49 to 1.71, so a flame rooted at 1.60 would start buried halfway
	/// inside its own exhaust.
	/// </summary>
	[Export] public Vector3 ExhaustOffset { get; set; } = new(0.26f, 0.36f, 1.70f);

	/// <summary>Length of the longest cone, in metres, at full strength.</summary>
	[Export] public float FlameLength { get; set; } = 1.2f;

	/// <summary>
	/// Radius of the widest cone where it leaves the pipe. A little proud of the pipe itself,
	/// which is 0.14 across — flame licking around the rim rather than a plug sitting in it — but
	/// not so much that it stops looking like it came out of there.
	/// </summary>
	[Export] public float FlameRadius { get; set; } = 0.10f;

	/// <summary>How fast the flame comes up, in strength per second. Fast: nitro is a punch.</summary>
	[Export] public float Attack { get; set; } = 16.0f;

	/// <summary>
	/// How fast it dies back. Slower than <see cref="Attack"/> on purpose — a flame that vanishes
	/// on the same frame the charge ends reads as a bug, one that gutters out reads as an engine.
	/// </summary>
	[Export] public float Release { get; set; } = 5.0f;

	/// <summary>Sides on each cone. Few enough that the facets show, which is the whole point.</summary>
	private const int Segments = 7;

	/// <summary>One cone, and how it flickers relative to its siblings.</summary>
	private sealed class Cone
	{
		public required MeshInstance3D Node { get; init; }
		public required float Length { get; init; }

		/// <summary>Flicker cycles per second.</summary>
		public required float Rate { get; init; }

		/// <summary>How much of the length the flicker swings, 0 to 1.</summary>
		public required float Swing { get; init; }
	}

	private sealed class Flame
	{
		public required Node3D Root { get; init; }
		public required List<Cone> Cones { get; init; }

		/// <summary>Offset into the flicker so the two pipes never pulse in lockstep.</summary>
		public required float Phase { get; init; }
	}

	private readonly List<Flame> _flames = new();

	/// <summary>0 when off, 1 at full burn. Eased, so the flame has an attack and a decay.</summary>
	private float _strength;

	private float _elapsed;

	public override void _Ready()
	{
		if (VehicleNode == null)
		{
			GD.PushWarning("[NitroFlame] No VehicleNode assigned; there will be no flames.");
			SetProcess(false);
			return;
		}

		// Outermost first so the brighter, shorter cones draw inside it.
		var layers = new (float LengthScale, float RadiusScale, Color Colour, float Rate, float Swing)[]
		{
			(1.00f, 1.00f, new Color(1.00f, 0.30f, 0.05f), 17.0f, 0.30f),
			(0.68f, 0.66f, new Color(1.00f, 0.62f, 0.10f), 23.0f, 0.22f),
			(0.38f, 0.36f, new Color(1.00f, 0.95f, 0.70f), 31.0f, 0.16f),
		};

		foreach (int side in new[] { -1, 1 })
		{
			var root = new Node3D
			{
				Name = side < 0 ? "FlameLeft" : "FlameRight",
				Position = ExhaustOffset with { X = side * ExhaustOffset.X },
				Visible = false,
			};
			AddChild(root);

			var cones = new List<Cone>();
			foreach (var layer in layers)
			{
				float length = FlameLength * layer.LengthScale;
				cones.Add(new Cone
				{
					Node = BuildCone(FlameRadius * layer.RadiusScale, length, layer.Colour, root),
					Length = length,
					Rate = layer.Rate,
					Swing = layer.Swing,
				});
			}

			_flames.Add(new Flame
			{
				Root = root,
				Cones = cones,
				Phase = side < 0 ? 0.0f : 1.7f,
			});
		}
	}

	/// <summary>
	/// A cone pointing out of the back of the car.
	///
	/// A cylinder with no top is Godot's cone, and its axis is Y — so it gets turned a quarter
	/// turn to lie along +Z, which is backwards in car space. It is also pushed half its length
	/// down that axis, because the mesh is centred on its origin and what wants to be pinned to
	/// the pipe is its base.
	/// </summary>
	private static MeshInstance3D BuildCone(float radius, float length, Color colour, Node parent)
	{
		var mesh = new MeshInstance3D
		{
			Mesh = new CylinderMesh
			{
				TopRadius = 0.0f,
				BottomRadius = radius,
				Height = length,
				RadialSegments = Segments,
				Rings = 1,
				Material = new StandardMaterial3D
				{
					AlbedoColor = colour,
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
				},
			},
			RotationDegrees = new Vector3(90.0f, 0.0f, 0.0f),
			Position = new Vector3(0.0f, 0.0f, length * 0.5f),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		parent.AddChild(mesh);
		return mesh;
	}

	public override void _Process(double delta)
	{
		if (VehicleNode == null || !IsInstanceValid(VehicleNode))
			return;

		bool boosting = VehicleNode.IsBoosting;
		_strength = Mathf.MoveToward(_strength, boosting ? 1.0f : 0.0f,
									 (boosting ? Attack : Release) * (float)delta);

		if (_strength <= 0.001f)
		{
			foreach (Flame flame in _flames)
				flame.Root.Visible = false;
			return;
		}

		_elapsed += (float)delta;

		foreach (Flame flame in _flames)
		{
			flame.Root.Visible = true;

			foreach (Cone cone in flame.Cones)
			{
				// Two beats per cone rather than one: a single sine is a throb, and two that do
				// not divide into each other never quite repeat, which is what reads as fire.
				float beat = Mathf.Sin(_elapsed * cone.Rate + flame.Phase) * 0.6f
							 + Mathf.Sin(_elapsed * cone.Rate * 0.41f + flame.Phase * 2.0f) * 0.4f;

				float stretch = _strength * (1.0f + beat * cone.Swing);
				float width = _strength * (1.0f + beat * cone.Swing * 0.35f);

				// The mesh's own axis is Y after the quarter turn, so that is the one that makes
				// the flame longer; X and Z fatten it.
				cone.Node.Scale = new Vector3(width, stretch, width);

				// Re-pin the base to the pipe now the length has changed, or the flame would grow
				// from its middle and push its own root out of the bodywork.
				cone.Node.Position = new Vector3(0.0f, 0.0f, cone.Length * stretch * 0.5f);
			}
		}
	}
}
