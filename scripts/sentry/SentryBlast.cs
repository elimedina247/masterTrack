using Godot;
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
    public static void Explode(Node3D source, Vector3 center, float radius, float strength)
    {
        foreach (Node node in source.GetTree().GetNodesInGroup(RacerController.GroupName))
        {
            if (node is RacerController racer)
                racer.ApplyExplosionImpulse(center, radius, strength);
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
