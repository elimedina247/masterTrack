using Godot;

namespace MasterTrack.Game;

/// <summary>
/// The world the track floats in: banks of low-poly cloud, and nothing else.
///
/// There is no ground in this game. The track is a road in the sky that the Track Master builds
/// out in front of the racers, and a racer who leaves it falls until they stop caring. Without
/// something out there, that reads as a bug — a track suspended in empty blue with no way to tell
/// how fast you are going or how far up you are. Clouds fix both: they give the speed something to
/// move against, and they give the drop a floor to be a long way above.
///
/// Two populations, and the split between them is a camera constraint rather than an artistic one.
/// The Track Master looks straight down at the track from a few hundred metres up, so anything
/// placed above the track near it is something drawn over the board they are trying to read.
/// So clouds near the track are only ever <i>below</i> it, and clouds at any height are pushed out
/// past <see cref="InnerRadius"/> where the board camera will never be looking.
///
/// Runs as a <c>[Tool]</c> so the layout is visible while editing, and the generated nodes are
/// left without an owner so none of it is written into the scene file.
/// </summary>
[Tool]
[GlobalClass]
public partial class SkyScenery : Node3D
{
	/// <summary>Clouds in the deck below the track.</summary>
	[Export] public int DeckClouds { get; set; } = 70;

	/// <summary>Clouds in the ring out toward the horizon.</summary>
	[Export] public int RingClouds { get; set; } = 90;

	/// <summary>
	/// How far out the board camera might be looking. Inside this radius clouds are only placed
	/// below the track; outside it they can be at any height.
	/// </summary>
	[Export] public float InnerRadius { get; set; } = 750.0f;

	/// <summary>How far out the furthest clouds go. Keep inside the cameras' far planes.</summary>
	[Export] public float OuterRadius { get; set; } = 3000.0f;

	/// <summary>Top of the deck below the track, in metres below zero. Far enough down that a
	/// two-cube climb still leaves the track clear of it.</summary>
	[Export] public float DeckTop { get; set; } = 220.0f;

	/// <summary>Bottom of the deck. The spread between the two is what gives the drop depth.</summary>
	[Export] public float DeckBottom { get; set; } = 900.0f;

	/// <summary>Fixed so the sky is the same sky every run, and the same on every peer.</summary>
	[Export] public int Seed { get; set; } = 20260726;

	private const string GeneratedRootName = "Clouds";

	/// <summary>Radius of one puff. Clouds are built from a handful of these.</summary>
	private const float MinPuff = 26.0f;
	private const float MaxPuff = 62.0f;

	/// <summary>Puffs per cloud.</summary>
	private const int MinPuffs = 3;
	private const int MaxPuffs = 6;

	public override void _Ready() => Rebuild();

	/// <summary>
	/// Tear down any previous sky and scatter a new one.
	///
	/// Every puff in the sky goes into one <see cref="MultiMesh"/>. There are on the order of a
	/// thousand of them and they are all the same handful of triangles, so as separate nodes they
	/// would be a thousand draw calls spent on scenery — on a game that is already issuing a lot
	/// of them for the track itself.
	/// </summary>
	private void Rebuild()
	{
		Node? previous = GetNodeOrNull(GeneratedRootName);
		if (previous != null)
		{
			RemoveChild(previous);
			previous.QueueFree();
		}

		var rng = new RandomNumberGenerator { Seed = (ulong)Seed };
		var puffs = new Godot.Collections.Array<Transform3D>();

		for (int i = 0; i < DeckClouds; i++)
		{
			// Anywhere under the track, including directly beneath it.
			float angle = rng.RandfRange(0.0f, Mathf.Tau);
			float radius = Mathf.Sqrt(rng.Randf()) * InnerRadius;

			AddCloud(puffs, rng, new Vector3(
				Mathf.Cos(angle) * radius,
				-rng.RandfRange(DeckTop, DeckBottom),
				Mathf.Sin(angle) * radius));
		}

		for (int i = 0; i < RingClouds; i++)
		{
			// Out past where the board camera looks, so these are free to be at eye level or above
			// — which is what stops the horizon being an empty band of blue.
			float angle = rng.RandfRange(0.0f, Mathf.Tau);
			float radius = rng.RandfRange(InnerRadius, OuterRadius);

			AddCloud(puffs, rng, new Vector3(
				Mathf.Cos(angle) * radius,
				rng.RandfRange(-DeckBottom, 500.0f),
				Mathf.Sin(angle) * radius));
		}

		var multi = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			Mesh = PuffMesh(),
			InstanceCount = puffs.Count,
		};

		for (int i = 0; i < puffs.Count; i++)
			multi.SetInstanceTransform(i, puffs[i]);

		AddChild(new MultiMeshInstance3D
		{
			Name = GeneratedRootName,
			Multimesh = multi,
			// Nothing up here should be casting shadows onto the track.
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		});
	}

	/// <summary>
	/// One cloud: a few flattened blobs shoved together, appended to the shared puff list. The
	/// squashing is per-instance scale rather than a differently shaped mesh, which is what lets
	/// every cloud in the sky share one.
	/// </summary>
	private static void AddCloud(Godot.Collections.Array<Transform3D> puffs, RandomNumberGenerator rng,
								 Vector3 position)
	{
		// Turned on its own axis so the facets don't all catch the light identically.
		var basis = new Basis(Vector3.Up, rng.RandfRange(0.0f, Mathf.Tau));
		int count = rng.RandiRange(MinPuffs, MaxPuffs);
		float spread = rng.RandfRange(0.9f, 1.8f);

		for (int i = 0; i < count; i++)
		{
			float radius = rng.RandfRange(MinPuff, MaxPuff);

			var offset = new Vector3(
				rng.RandfRange(-1.0f, 1.0f) * radius * spread,
				rng.RandfRange(-0.25f, 0.25f) * radius,
				rng.RandfRange(-1.0f, 1.0f) * radius * spread);

			// Flattened: a cloud is wider than it is tall, and a row of round blobs reads as
			// bubbles rather than weather.
			var scale = new Vector3(radius, radius * rng.RandfRange(0.5f, 0.75f), radius);

			puffs.Add(new Transform3D(basis.Scaled(scale), position + basis * offset));
		}
	}

	/// <summary>
	/// The one blob every cloud is made of, at unit radius so instances can scale it. Six segments
	/// and three rings is coarse enough that the facets are the point — a smooth sphere would be
	/// the one round thing in a world made of flat faces.
	/// </summary>
	private static SphereMesh PuffMesh() => new()
	{
		Radius = 1.0f,
		Height = 2.0f,
		RadialSegments = 6,
		Rings = 3,
		Material = CloudMaterial(),
	};

	/// <summary>
	/// Cloud white, shaded the same way as everything else so the facets read. Deliberately not
	/// pure white: it has to sit a step above the sky behind it without blowing out against the
	/// track's own white markings.
	/// </summary>
	private static StandardMaterial3D CloudMaterial() => new()
	{
		AlbedoColor = new Color(0.97f, 0.98f, 1.00f, 0.97f),
		ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
		SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
		Metallic = 0.0f,
		Roughness = 1.0f,

		// A hair of transparency, and not for the look of it: it moves the clouds into the
		// transparent pass, where they write no depth and no normals — so the screen-space
		// outline cannot see them, and the sky stays unlined weather instead of a page of
		// ink-edged blobs. (The outline runs pre-transparent, so they also draw over any
		// line behind them.) At 0.97 they still read as solid.
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
	};
}
