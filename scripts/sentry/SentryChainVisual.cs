using Godot;
using MasterTrack.Racer;

namespace MasterTrack.Sentry;

/// <summary>
/// The rope everyone can see between a chained pair. Pure cosmetics: the actual tether physics
/// lives on each car (see <c>RacerController.ApplyChain</c>), simulated by whichever machine
/// owns that car. This just draws a bar between the two cars, on every peer, for as long as the
/// debuff runs — without it, two cars snapping back toward each other reads as lag, not a chain.
///
/// Keeps its own copy of the duration rather than asking the cars, so it dies on time even on a
/// peer where neither car is locally simulated.
/// </summary>
public partial class SentryChainVisual : Node3D
{
    /// <summary>Where on the car the rope attaches, in metres up. Roof height, roughly.</summary>
    private const float AttachHeight = 1.0f;

    private RacerController? _a;
    private RacerController? _b;
    private float _timeLeft;

    private Node3D _beam = null!;

    public void Initialize(RacerController a, RacerController b, float duration)
    {
        _a = a;
        _b = b;
        _timeLeft = duration;
    }

    public override void _Ready()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.75f, 0.65f, 0.25f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };

        // A cylinder points along its own Y; rotated a quarter turn it lies along this node's Z,
        // which LookAt then aims at the other car. Stretch is a scale on the child's Y.
        _beam = new MeshInstance3D
        {
            Name = "Beam",
            Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.28f, Height = 1.0f, Material = material },
            RotationDegrees = new Vector3(90.0f, 0.0f, 0.0f),
        };
        AddChild(_beam);
    }

    public override void _Process(double delta)
    {
        _timeLeft -= (float)delta;

        if (_timeLeft <= 0.0f
            || _a is null || _b is null
            || !IsInstanceValid(_a) || !IsInstanceValid(_b)
            || !_a.IsInsideTree() || !_b.IsInsideTree())
        {
            QueueFree();
            return;
        }

        Vector3 from = _a.GlobalPosition + Vector3.Up * AttachHeight;
        Vector3 to = _b.GlobalPosition + Vector3.Up * AttachHeight;
        float length = from.DistanceTo(to);

        // Two cars in exactly the same place is a LookAt with no direction in it.
        if (length < 0.5f)
        {
            _beam.Visible = false;
            return;
        }

        _beam.Visible = true;
        GlobalPosition = (from + to) * 0.5f;
        LookAt(to, Vector3.Up.Cross(to - from).LengthSquared() > 0.001f ? Vector3.Up : Vector3.Right);
        _beam.Scale = new Vector3(1.0f, length, 1.0f);
    }
}
