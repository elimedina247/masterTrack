using Godot;

namespace MasterTrack.Sentry;

/// <summary>
/// A cargo spill: a couple of seconds of heavy junk raining onto a marked patch of road, which
/// then <i>stays there</i> as terrain. The first hazard in the kit that is a place rather than
/// an event — the pack that swept through clean on lap two threads a debris field on lap three.
///
/// Deterministic from the broadcast: the server picks a seed, and every peer grows the same
/// plan from it — every crate's timing, landing spot, size, shape and paint — before anything
/// falls. The tumbling itself is local physics on each machine and free to disagree slightly;
/// the heavy damping on <see cref="SentryDebris"/> settles every copy into nearly the same
/// field within a couple of seconds, and nobody ever syncs a rigid body over the wire.
///
/// The spill node is the rainmaker only: debris is parented beside it, each piece owning its
/// own lifetime, and the spill leaves once the last crate is out of the sky.
/// </summary>
public partial class SentryCargoSpill : Node3D
{
    /// <summary>How many pieces one spill drops.</summary>
    private const int DropCount = 26;

    /// <summary>How far from the aim point a piece can land, in metres. Two-thirds of a road
    /// width — a field you thread, not a wall you stop for. Public so the aiming ghost can
    /// promise exactly this circle before the rain starts.</summary>
    public const float SpillRadius = 17.0f;

    /// <summary>How high the junk spawns, in metres. With the debris' 2 g, about two and a half
    /// seconds of visible incoming — the warning is the rain itself.</summary>
    private const float DropHeight = 70.0f;

    /// <summary>Seconds the rainfall is spread across.</summary>
    private const float SpillSeconds = 2.2f;

    /// <summary>Most junk on the board at once, across every spill. Past the cap the oldest
    /// pieces leave first — the board fills, it never floods.</summary>
    private const int DebrisCap = 150;

    /// <summary>Cargo paint: crate timbers, drum red, tarp blue, sack grey. Picked by the
    /// seeded plan, so the same crate wears the same colour on every screen.</summary>
    private static readonly Color[] Palette =
    {
        new(0.72f, 0.51f, 0.26f),
        new(0.78f, 0.13f, 0.11f),
        new(0.20f, 0.42f, 0.75f),
        new(0.55f, 0.55f, 0.58f),
    };

    /// <summary>The seed the whole drop grows from. Set before entering the tree.</summary>
    public int Seed { get; set; }

    /// <summary>One planned piece of falling cargo. Everything decided up front, because the
    /// plan is the only thing peers share.</summary>
    private readonly record struct Drop(
        float Time, Vector3 Offset, bool IsCube, float Size, Color Paint, Vector3 Tumble);

    private Drop[] _plan = System.Array.Empty<Drop>();
    private StandardMaterial3D _warning = null!;
    private MeshInstance3D _disc = null!;
    private int _next;
    private float _age;

    public override void _Ready()
    {
        var rng = new RandomNumberGenerator { Seed = unchecked((ulong)Seed) };

        // The plan, in one fixed order — every rng call below happens identically on every
        // peer, which is the entire trick.
        _plan = new Drop[DropCount];
        for (int i = 0; i < DropCount; i++)
        {
            float angle = rng.Randf() * Mathf.Tau;
            // Square root, so the junk lands evenly over the disc instead of bunching mid-air
            // over the centre.
            float distance = Mathf.Sqrt(rng.Randf()) * SpillRadius;

            _plan[i] = new Drop(
                Time: i * (SpillSeconds / DropCount) + rng.Randf() * 0.08f,
                Offset: new Vector3(Mathf.Cos(angle) * distance, 0.0f, Mathf.Sin(angle) * distance),
                IsCube: rng.Randf() < 0.65f,
                Size: rng.RandfRange(1.5f, 2.9f),
                Paint: Palette[rng.RandiRange(0, Palette.Length - 1)],
                Tumble: new Vector3(rng.RandfRange(-3.0f, 3.0f), rng.RandfRange(-3.0f, 3.0f),
                                    rng.RandfRange(-3.0f, 3.0f)));
        }

        // The warned patch: a faint disc the honest size of the spill, gone once the sky is
        // empty — the missile's ring rule, in cargo colours.
        _warning = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.7f, 0.15f, 0.28f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _disc = new MeshInstance3D
        {
            Name = "Warning",
            Mesh = new CylinderMesh
            {
                TopRadius = SpillRadius, BottomRadius = SpillRadius, Height = 0.1f,
                RadialSegments = 24,
                Material = _warning,
            },
            Position = new Vector3(0.0f, 0.4f, 0.0f),
        };
        AddChild(_disc);
    }

    /// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
    /// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
    public override void _ExitTree()
    {
        _warning.Dispose();
        _warning = null!;
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;

        while (_next < _plan.Length && _age >= _plan[_next].Time)
        {
            Spawn(_plan[_next]);
            _next++;
        }

        // The warning flickers urgently while junk is still in the sky.
        float flicker = 0.5f + 0.5f * Mathf.Sin(_age * 14.0f);
        Color colour = _warning.AlbedoColor;
        colour.A = 0.16f + 0.18f * flicker;
        _warning.AlbedoColor = colour;

        // Everything is out of the sky ~2.7 s after the last spawn (70 m at 2 g). The debris
        // owns itself from here; the rainmaker leaves.
        if (_next >= _plan.Length && _age >= SpillSeconds + 3.0f)
            QueueFree();
    }

    private void Spawn(Drop drop)
    {
        // The cap, enforced at the door: FIFO by tree order, which is spawn order, because
        // every piece is parented to the same node.
        var field = GetTree().GetNodesInGroup(SentryDebris.GroupName);
        for (int i = 0; i <= field.Count - DebrisCap; i++)
        {
            if (field[i] is Node old && !old.IsQueuedForDeletion())
                old.QueueFree();
        }

        var piece = new SentryDebris
        {
            IsCube = drop.IsCube,
            SizeMetres = drop.Size,
            Paint = drop.Paint,
            Position = GlobalPosition + drop.Offset + Vector3.Up * DropHeight,
            Rotation = drop.Tumble * 0.3f,
            AngularVelocity = drop.Tumble,
        };

        // Beside the spill, not under it: the junk outlives the rainmaker.
        GetParent().AddChild(piece);
    }
}
