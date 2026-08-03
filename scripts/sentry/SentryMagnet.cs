using Godot;
using MasterTrack.Racer;

namespace MasterTrack.Sentry;

/// <summary>
/// A magnet planted on the road: for a few seconds it drags every car near it toward itself —
/// and every piece of spilled cargo, which is how a magnet over a junk field becomes a vortex.
///
/// The trick to using it is that the pull is only worth points <i>sideways</i>: planted on a
/// straight it is a mild brake anyone can drive through, planted on the outside of a corner it
/// walks a car wide into the wall. Placement is the skill, which is why it aims like the
/// missile — a spot in the world, not a car.
///
/// Not replicated, same as the rest of the kit: every peer plants one off the same broadcast,
/// and each machine only ever pulls the cars it simulates (see
/// <see cref="RacerController.ApplyMagnetPull"/>) and its own local debris.
/// </summary>
public partial class SentryMagnet : Node3D
{
    /// <summary>How long the field holds, in seconds.</summary>
    private const float Duration = 5.0f;

    /// <summary>Seconds of visible wind-up before the field grabs — the one shared fuse.</summary>
    private const float SpinUpSeconds = SentryActions.LeadSeconds;

    /// <summary>How far the field reaches, in metres. Most of a road width either side. Public
    /// so the aiming ghost can promise exactly this circle before the magnet exists.</summary>
    public const float Radius = 22.0f;

    /// <summary>The drag on a car inside the field, in m/s². Sized against the chain's numbers:
    /// firmly felt, well under <c>ChainMaxPull</c> — a magnet walks you off your line, it does
    /// not reel you in like a fish.</summary>
    private const float Pull = 26.0f;

    /// <summary>Seconds the spent magnet takes to fade away.</summary>
    private const float FadeSeconds = 0.8f;

    /// <summary>Ground rings the field draws itself with — each one a different radius, lit in
    /// turn from the rim inward so the glow itself travels the way the pull does.</summary>
    private readonly StandardMaterial3D[] _ringPaint = new StandardMaterial3D[3];

    private StandardMaterial3D _column = null!;
    private float _age;

    private bool Active => _age >= SpinUpSeconds && _age < SpinUpSeconds + Duration;

    private bool Spent => _age >= SpinUpSeconds + Duration;

    public override void _Ready()
    {
        // Concentric rings on the road, unshaded like every glow on the board, sized so the
        // outermost is the honest reach of the field — the missile's ring rule.
        for (int i = 0; i < _ringPaint.Length; i++)
        {
            _ringPaint[i] = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.35f, 0.55f, 1.0f, 0.5f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };

            float radius = Radius * (1.0f - i * 0.3f);
            AddChild(new MeshInstance3D
            {
                Name = $"Ring{i}",
                Mesh = new TorusMesh
                {
                    InnerRadius = radius * 0.9f,
                    OuterRadius = radius,
                    Material = _ringPaint[i],
                },
                Position = new Vector3(0.0f, 0.5f, 0.0f),
            });
        }

        // A faint column so the field reads from the board camera and over a crest.
        _column = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.35f, 0.55f, 1.0f, 0.16f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        AddChild(new MeshInstance3D
        {
            Name = "Column",
            Mesh = new CylinderMesh
            {
                TopRadius = 1.4f, BottomRadius = 2.6f, Height = 26.0f,
                RadialSegments = 10,
                Material = _column,
            },
            Position = new Vector3(0.0f, 13.0f, 0.0f),
        });
    }

    /// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
    /// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
    public override void _ExitTree()
    {
        for (int i = 0; i < _ringPaint.Length; i++)
        {
            _ringPaint[i]?.Dispose();
            _ringPaint[i] = null!;
        }

        _column.Dispose();
        _column = null!;
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;

        if (Spent)
        {
            float gone = (_age - SpinUpSeconds - Duration) / FadeSeconds;
            if (gone >= 1.0f)
            {
                QueueFree();
                return;
            }

            float alpha = 1.0f - gone;
            for (int i = 0; i < _ringPaint.Length; i++)
                SetRingAlpha(i, 0.5f * alpha);
            _column.AlbedoColor = new Color(0.35f, 0.55f, 1.0f, 0.16f * alpha);
            return;
        }

        // The glow marches inward, rim to centre — the direction the field pulls. Quicker and
        // brighter through the spin-up, the countdown language the racers already know.
        float rate = Active ? 3.0f : 6.0f + _age * 4.0f;
        for (int i = 0; i < _ringPaint.Length; i++)
        {
            float wave = 0.5f + 0.5f * Mathf.Sin(_age * rate - i * 1.6f);
            SetRingAlpha(i, 0.25f + 0.45f * wave);
        }
    }

    private void SetRingAlpha(int ring, float alpha)
    {
        Color colour = _ringPaint[ring].AlbedoColor;
        colour.A = alpha;
        _ringPaint[ring].AlbedoColor = colour;
    }

    /// <summary>
    /// The field: every car near it is offered the pull, every physics tick — their own
    /// ownership checks sort out whose machine actually moves them. Debris is pulled here
    /// directly, because debris is local on every peer by construction.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!Active)
            return;

        Vector3 centre = GlobalPosition;

        foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
        {
            if (node is RacerController racer && racer.IsInsideTree())
                racer.ApplyMagnetPull(centre, Radius, Pull, (float)delta);
        }

        foreach (Node node in GetTree().GetNodesInGroup(SentryDebris.GroupName))
        {
            if (node is not RigidBody3D body || !body.IsInsideTree())
                continue;

            Vector3 toCentre = centre - body.GlobalPosition;
            toCentre.Y = 0.0f;
            float distance = toCentre.Length();
            if (distance > Radius || distance < 1.5f)
                continue;

            // An impulse rather than a velocity write, because it also wakes a settled body —
            // the junk stirring and sliding toward the magnet is half the show.
            body.ApplyCentralImpulse(toCentre / distance * (Pull * 1.5f * (float)delta * body.Mass));
        }
    }
}
