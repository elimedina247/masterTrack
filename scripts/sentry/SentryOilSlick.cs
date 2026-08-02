using Godot;
using MasterTrack.Racer;

namespace MasterTrack.Sentry;

/// <summary>
/// A dark puddle poured onto the road: it spreads for a moment, then anything crossing it drives
/// on soap for a while, then it dries up. The spread <i>is</i> the lead time — the same
/// courtesy the barrel's arm delay pays, a racer watching the road sees the stain growing before
/// it can do anything to them — and the fully-spread disc is the honest extent of the hazard,
/// the way the missile's ring and the blast's fireball are.
///
/// The slick doesn't touch grip itself; it tells each car standing in it that it is being stood
/// in (see <see cref="RacerController.TouchOilSlick"/>), and the car's own debuff upkeep turns
/// that into a grip multiplier inside the tire solve — the only place slippery can actually be
/// said. Not replicated, same as the barrel: every peer pours one off the same broadcast and
/// sweeps the same replicated car positions.
/// </summary>
public partial class SentryOilSlick : Node3D
{
    /// <summary>Seconds the puddle takes to spread to full size, and how long a racer has
    /// before it is slick. The one shared fuse — see <see cref="SentryActions.LeadSeconds"/>.</summary>
    private const float SpreadSeconds = SentryActions.LeadSeconds;

    /// <summary>Seconds the dried-out puddle takes to fade away.</summary>
    private const float FadeSeconds = 1.5f;

    /// <summary>How far above a slick a car still counts as in it, in metres. Enough for a car
    /// bouncing across it on its suspension; not enough to soap a bridge overhead.</summary>
    private const float CatchHeight = 3.5f;

    private StandardMaterial3D _oil = null!;
    private MeshInstance3D _puddle = null!;
    private float _age;

    private bool Slippery => _age >= SpreadSeconds && _age < SpreadSeconds + SentryActions.OilSlickDuration;

    private bool Spent => _age >= SpreadSeconds + SentryActions.OilSlickDuration;

    public override void _Ready()
    {
        // Near-black with a violet sheen, unshaded so it reads as a stain on any road at any
        // sun angle. Slightly proud of the surface to stay clear of z-fighting.
        _oil = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.09f, 0.05f, 0.14f, 0.92f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };

        _puddle = new MeshInstance3D
        {
            Name = "Puddle",
            Mesh = new CylinderMesh
            {
                TopRadius = SentryActions.OilSlickRadius,
                BottomRadius = SentryActions.OilSlickRadius,
                Height = 0.1f,
                RadialSegments = 24,
                Material = _oil,
            },
            Position = new Vector3(0.0f, 0.12f, 0.0f),
            Scale = new Vector3(0.1f, 1.0f, 0.1f),
        };
        AddChild(_puddle);
    }

    /// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
    /// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
    public override void _ExitTree()
    {
        _oil.Dispose();
        _oil = null!;
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;

        if (_age < SpreadSeconds)
        {
            // Spreading: the disc grows to its true extent with a wet shimmer, quickening the
            // way the barrel's blink does — the countdown language the racers already know.
            float spread = Mathf.Clamp(_age / SpreadSeconds, 0.0f, 1.0f);
            float eased = 1.0f - (1.0f - spread) * (1.0f - spread);
            _puddle.Scale = new Vector3(Mathf.Max(eased, 0.1f), 1.0f, Mathf.Max(eased, 0.1f));

            float shimmer = 0.5f + 0.5f * Mathf.Sin(_age * (10.0f + _age * 8.0f));
            _oil.AlbedoColor = new Color(0.09f + 0.20f * shimmer, 0.05f + 0.12f * shimmer,
                                         0.14f + 0.30f * shimmer, 0.92f);
            return;
        }

        if (!Spent)
        {
            _puddle.Scale = Vector3.One;
            _oil.AlbedoColor = new Color(0.09f, 0.05f, 0.14f, 0.92f);
            return;
        }

        // Dried up: fade out, then leave.
        float gone = (_age - SpreadSeconds - SentryActions.OilSlickDuration) / FadeSeconds;
        if (gone >= 1.0f)
        {
            QueueFree();
            return;
        }

        _oil.AlbedoColor = new Color(0.09f, 0.05f, 0.14f, 0.92f * (1.0f - gone));
    }

    /// <summary>
    /// The sweep: every car standing in the puddle gets told so, every physics tick. On the
    /// physics tick rather than the frame because it is a question about where the cars are —
    /// the same reasoning as the barrel's tripwire.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!Slippery)
            return;

        Vector3 center = GlobalPosition;

        foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
        {
            if (node is not RacerController racer || !racer.IsInsideTree())
                continue;

            Vector3 offset = racer.GlobalPosition - center;
            float height = offset.Y;
            offset.Y = 0.0f;

            if (offset.Length() <= SentryActions.OilSlickRadius
                && height > -1.0f && height < CatchHeight)
                racer.TouchOilSlick();
        }
    }
}
