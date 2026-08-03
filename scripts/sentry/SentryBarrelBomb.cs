using Godot;
using MasterTrack.Audio;
using MasterTrack.Tiles;

namespace MasterTrack.Sentry;

/// <summary>
/// A bomb sat on the road: it lands, blinks through a short fuse, then blows on its own in a
/// blast that owns the whole stretch of road it sits on. The fuse is what keeps it fair — the
/// sentry has to lead the pack by the same two-second beat every other delayed action uses,
/// and a racer watching the road gets the blink as their warning to be anywhere else.
///
/// Placeholder art on purpose: a red faceted cylinder in the house material, to be replaced by
/// an authored model later the way the missile's was. The fuse lamp on top burns brighter as
/// the fuse runs down — the countdown the racers actually need to read.
///
/// Not replicated, same as the missile: every peer plants one off the same broadcast and runs
/// the same fuse from the same moment, so they all detonate within a network beat. Each
/// machine's blast only ever throws its own cars — see <see cref="SentryBlast"/> — so a frame
/// of disagreement about the detonation moment changes nothing about who got launched.
/// </summary>
public partial class SentryBarrelBomb : Node3D
{
	/// <summary>Seconds between landing and detonating. The window a racer gets to clear out —
	/// the same beat as <see cref="SentryActions.LeadSeconds"/>.</summary>
	private const float FuseSeconds = 2.0f;

	/// <summary>Bigger than the missile's — the barrel trades the missile's aim-anywhere reach
	/// for a planted spot, so the spot it owns is huge. Public so the aiming ghost can promise
	/// exactly this circle before the barrel exists.</summary>
	public const float ExplosionRadius = TrackTile.Size * 1.3f;

	/// <summary>Speed the blast adds to a car at the centre of it, in m/s. Far past the
	/// missile's 80 and well over double a car's top speed: anyone near the barrel when it goes
	/// is <i>launched</i>, full stop. The squared falloff is what keeps the huge radius honest —
	/// the violence lives close to the crater, and the rim of it is still a hard shove.</summary>
	private const float ExplosionStrength = 130.0f;

	private const float BarrelRadius = 1.6f;
	private const float BarrelHeight = 3.0f;

	/// <summary>The fuse, out loud: a beeping countdown that runs from planting to the bang.
	/// It dies with the barrel, so however much of it plays, it always ends in the explosion —
	/// the audible twin of the blinking lamp.</summary>
	private const string CountdownSfxPath = "res://assets/audio/hazards/bomb_countdown.mp3";

	private StandardMaterial3D _paint = null!;
	private StandardMaterial3D _fuseLamp = null!;
	private float _age;

	/// <summary>The point the blast measures from: the barrel's middle.</summary>
	private Vector3 Center => GlobalPosition + Vector3.Up * (BarrelHeight * 0.5f);

	public override void _Ready()
	{
		// The barrel — "just a red cylinder for now", in the game's own facets and light.
		_paint = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.78f, 0.13f, 0.11f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
			SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
			Metallic = 0.0f,
			Roughness = 1.0f,
		};
		AddChild(new CsgCylinder3D
		{
			Sides = 10, SmoothFaces = false,
			Radius = BarrelRadius, Height = BarrelHeight, Material = _paint,
			Position = new Vector3(0.0f, BarrelHeight * 0.5f, 0.0f),
		});

		// The fuse lamp: unshaded like every glow on the board, dark until the barrel is live.
		_fuseLamp = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.25f, 0.05f, 0.05f),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		AddChild(new CsgSphere3D
		{
			RadialSegments = 8, Rings = 4, SmoothFaces = false,
			Radius = 0.45f, Material = _fuseLamp,
			Position = new Vector3(0.0f, BarrelHeight + 0.35f, 0.0f),
		});

		Sfx.Attach(this, CountdownSfxPath, unitSize: 20.0f);
	}

	/// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
	/// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
	public override void _ExitTree()
	{
		_paint.Dispose();
		_paint = null!;
		_fuseLamp.Dispose();
		_fuseLamp = null!;
	}

	public override void _Process(double delta)
	{
		_age += (float)delta;

		if (_age >= FuseSeconds)
		{
			SentryBlast.Explode(this, Center, ExplosionRadius, ExplosionStrength);
			QueueFree();
			return;
		}

		// The fuse: the whole barrel blinks, quickening toward the bang — the same countdown
		// language as a condemned tile or the missile's ring — and the lamp burns up from
		// ember to flame as the fuse runs out.
		float blink = Mathf.Sin(_age * (8.0f + _age * 6.0f)) > 0.0f ? 1.0f : 0.45f;
		_paint.AlbedoColor = new Color(0.78f * blink, 0.13f * blink, 0.11f * blink);

		float burn = 0.25f + 0.75f * (_age / FuseSeconds);
		_fuseLamp.AlbedoColor = new Color(1.0f * burn, 0.25f * burn, 0.1f * burn);
	}
}
