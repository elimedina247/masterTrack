using Godot;
using MasterTrack.Audio;
using MasterTrack.Racer;

namespace MasterTrack.Sentry;

/// <summary>
/// The one way anything in the sentry's kit blows up: throw every car in range, and grow an
/// honest fireball over exactly the distance that did the throwing. Shared by the missile and
/// the barrel bomb so "what an explosion does" is decided once — the two weapons differ in how
/// they arrive, not in what arriving means.
///
/// Impulses follow the standing rule: every peer runs this same call off its own copy of the
/// event, and <see cref="RacerController.ApplyExplosionImpulse"/> only moves the cars this
/// machine simulates — so every car is thrown exactly once, by the machine that owns it.
/// </summary>
public static class SentryBlast
{
	/// <summary>The bang itself, shared like everything else here: the missile and the barrel
	/// sound like the same ordnance because they are.</summary>
	private const string ExplosionSfxPath = "res://assets/audio/hazards/explosion.mp3";

	public static void Explode(Node3D source, Vector3 center, float radius, float strength)
	{
		// Fired through the scene root rather than from the weapon, because the weapon frees
		// itself this frame and the bang has to keep ringing over the crater.
		Sfx.PlayAt(source, ExplosionSfxPath, center,
				   volumeDb: 4.0f, unitSize: 45.0f, pitchJitter: 0.06f);

		// Who got caught, counted here rather than inside the impulse: the impulse only runs on
		// the machine that simulates a car, but the geometry — who was inside the radius — reads
		// the same off every peer's replicated poses. That is what lets the sentry's machine know
		// the result of its own shot without the answer ever crossing the wire.
		var caught = new System.Collections.Generic.List<int>();

		foreach (Node node in source.GetTree().GetNodesInGroup(RacerController.GroupName))
		{
			if (node is not RacerController racer)
				continue;

			if (racer.IsInsideTree()
				&& racer.GlobalPosition.DistanceTo(center) <= radius)
				caught.Add(racer.OwnerPeerId);

			racer.ApplyExplosionImpulse(center, radius, strength);
		}

		// The missile and the barrel live as children of the manager, which is how the report
		// finds its way to the sentry's UI. A blast fired from anywhere else simply goes
		// unreported rather than being an error — the feedback is for the sentry's own shots.
		(source.GetParent() as SentryManager)?.ReportBlast(caught.ToArray());

		// Spilled cargo goes flying too — lay a junk field, then missile it into the pack.
		// Debris is local on every peer, so each machine simply throws its own copies; and
		// being a tenth of a car, the junk gets launched properly where a car gets shoved.
		foreach (Node node in source.GetTree().GetNodesInGroup(SentryDebris.GroupName))
		{
			if (node is not RigidBody3D body || !body.IsInsideTree())
				continue;

			Vector3 offset = body.GlobalPosition - center;
			float distance = offset.Length();
			if (distance > radius)
				continue;

			float proximity = 1.0f - distance / radius;
			float falloff = proximity * proximity;
			Vector3 direction = distance > 0.5f ? offset / distance : Vector3.Up;

			body.ApplyCentralImpulse((direction + Vector3.Up * 0.8f)
									 * (strength * falloff * body.Mass * 0.6f));
		}

		SpawnFireball(source, center, radius);
	}

	/// <summary>A fireball that grows to the true blast radius and fades — honest VFX, so what
	/// racers learn to dodge is the distance that actually throws them.</summary>
	private static void SpawnFireball(Node3D source, Vector3 center, float radius)
	{
		var material = new StandardMaterial3D
		{
			AlbedoColor = new Color(1.0f, 0.5f, 0.1f, 0.85f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};

		var blast = new MeshInstance3D
		{
			Name = "Blast",
			Mesh = new SphereMesh { Radius = 1.0f, Height = 2.0f, Material = material },
			Position = center,
			Scale = Vector3.One * 2.0f,
		};

		// Parented beside the weapon, not to it — the weapon frees itself this frame.
		source.GetParent().AddChild(blast);

		Tween tween = blast.CreateTween();
		tween.SetParallel();
		tween.TweenProperty(blast, "scale", Vector3.One * radius, 0.55)
			 .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
		tween.TweenProperty(material, "albedo_color:a", 0.0f, 0.55);
		tween.Chain().TweenCallback(Callable.From(blast.QueueFree));
	}
}
