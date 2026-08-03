using Godot;
using MasterTrack.Racer;

namespace MasterTrack.Sentry;

/// <summary>
/// One piece of spilled cargo: a heavy faceted cube or sphere that falls, thuds, and stays on
/// the road as terrain. Cubes settle and become furniture; spheres never quite stop and stay a
/// live hazard, getting punted around by whoever hits them.
///
/// Heavy on purpose — around a tenth of a car — because confetti-light junk scatters and feels
/// like nothing. The real effect on a car is not the solver's contact force (the tire model
/// grips that away) but <see cref="RacerController.ApplyDebrisHit"/>, fired from the contact:
/// a yaw kick that makes an off-centre clip cost you your line.
///
/// Local on every peer, never replicated: the spill that spawned it did so from a shared seed,
/// and heavy damping settles every peer's copy into nearly the same place. Each machine's
/// debris only ever kicks the cars that machine simulates — the standing rule.
/// </summary>
public partial class SentryDebris : RigidBody3D
{
    /// <summary>How the magnet and the blast find every piece on the board.</summary>
    public const string GroupName = "sentry_debris";

    /// <summary>Seconds a piece lives before it fades off the road. Decay keeps the last lap
    /// from being run through a junkyard; crank it if the deterioration turns out to be the fun.</summary>
    private const float Lifetime = 45.0f;

    /// <summary>Seconds the dying piece takes to shrink away.</summary>
    private const float FadeSeconds = 0.8f;

    /// <summary>Milliseconds between kicks handed to the same car. A contact that stutters
    /// across frames is one hit, not three.</summary>
    private const ulong HitCooldownMsec = 400;

    /// <summary>Cube or sphere, decided by the spill's seeded plan before entering the tree.</summary>
    public bool IsCube { get; set; } = true;

    /// <summary>Edge length or diameter, in metres. From the seeded plan.</summary>
    public float SizeMetres { get; set; } = 2.0f;

    /// <summary>Paint, from the seeded plan — same index on every peer, same crate everywhere.</summary>
    public Color Paint { get; set; } = new(0.78f, 0.55f, 0.25f);

    private StandardMaterial3D _material = null!;
    private Shape3D _shape = null!;
    private PhysicsMaterial _surface = null!;
    private Node3D _visual = null!;
    private float _age;
    private bool _dying;

    private readonly System.Collections.Generic.Dictionary<int, ulong> _lastHit = new();

    public override void _Ready()
    {
        AddToGroup(GroupName);

        // A tenth of a car: one is a wobble, a run of three is a spin. See ApplyDebrisHit.
        Mass = IsCube ? 110.0f : 80.0f;

        // Runs heavy like the cars do, so the rainfall arrives on the game's clock, not Earth's.
        GravityScale = 2.0f;

        // Settle fast: the shorter the tumble, the smaller the window in which peers' copies
        // can disagree — and a sleeping body is nearly free, which is what makes persistence
        // affordable at all. Spheres get less angular damp so they keep their roll.
        LinearDamp = 0.8f;
        AngularDamp = IsCube ? 3.0f : 0.8f;

        _surface = new PhysicsMaterial { Bounce = 0.05f, Friction = 1.0f, Rough = true };
        PhysicsMaterialOverride = _surface;

        // The house material: per-vertex light, no specular, and flat CSG facets — a crate off
        // the same truck as everything else on the board.
        _material = new StandardMaterial3D
        {
            AlbedoColor = Paint,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            Metallic = 0.0f,
            Roughness = 1.0f,
        };

        if (IsCube)
        {
            _shape = new BoxShape3D { Size = Vector3.One * SizeMetres };
            _visual = new CsgBox3D { Size = Vector3.One * SizeMetres, Material = _material };
        }
        else
        {
            _shape = new SphereShape3D { Radius = SizeMetres * 0.5f };
            _visual = new CsgSphere3D
            {
                RadialSegments = 8, Rings = 4, SmoothFaces = false,
                Radius = SizeMetres * 0.5f, Material = _material,
            };
        }
        AddChild(_visual);

        AddChild(new CollisionShape3D { Shape = _shape });

        // The tripwire for the yaw kick. Contacts are only reported when asked.
        ContactMonitor = true;
        MaxContactsReported = 4;
        BodyEntered += OnBodyEntered;
    }

    /// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
    /// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
    public override void _ExitTree()
    {
        _material.Dispose();
        _material = null!;
        _shape.Dispose();
        _shape = null!;
        _surface.Dispose();
        _surface = null!;
    }

    private void OnBodyEntered(Node body)
    {
        if (_dying || body is not RacerController racer || !racer.IsInsideTree())
            return;

        // One kick per contact, not one per frame the bumper spends touching the crate.
        ulong now = Time.GetTicksMsec();
        if (_lastHit.TryGetValue(racer.OwnerPeerId, out ulong last) && now - last < HitCooldownMsec)
            return;
        _lastHit[racer.OwnerPeerId] = now;

        racer.ApplyDebrisHit(GlobalPosition);
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;

        if (_age < Lifetime)
            return;

        if (!_dying)
        {
            _dying = true;

            // Out of the world before out of sight: no ghost collisions from a fading crate.
            CollisionLayer = 0;
            CollisionMask = 0;
            Freeze = true;
        }

        float gone = (_age - Lifetime) / FadeSeconds;
        if (gone >= 1.0f)
        {
            QueueFree();
            return;
        }

        // Shrinking rather than alpha-fading: transparent junk piles into overdraw, and a
        // crate that pops out of existence at full size reads as a bug. The visual child is
        // what shrinks — a scaled RigidBody3D is something the physics engine holds against you.
        _visual.Scale = Vector3.One * Mathf.Max(1.0f - gone, 0.01f);
    }
}
