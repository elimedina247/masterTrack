using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// A placed furniture hazard: the node that actually sits in a <see cref="TrackHazardSlot"/>.
/// One implementation per <see cref="HazardKind"/>, one factory, three spawn paths — authored
/// into a piece, dropped in by the builder during the build, planted live by the sentry — and
/// none of the three knows anything the others don't.
///
/// Parented under the tile's own node, which is what solves lifetime without a line of code:
/// a hazard on a tile that crumbles or is undone leaves with it, and nothing is ever left
/// floating where road used to be.
///
/// The local transform is the slot's, so a hazard on a banked piece arrives banked. Everything
/// a subclass does with the car goes through velocity-level edits or mass-scaled impulses —
/// the tire solve eats ordinary forces, which is the standing rule.
/// </summary>
public abstract partial class TrackHazard : Node3D
{
    /// <summary>Which hazard this is, for warnings and bookkeeping. Set by the factory.</summary>
    public HazardKind Kind { get; private set; }

    /// <summary>
    /// Whether this is a <i>rigged</i> device — planted dormant and fired by hand — rather than
    /// one that goes off on contact. False by default, so the contact hazards say nothing about
    /// a mechanic they do not have, and <see cref="TrackController"/> never needs the concrete
    /// type to know whether a click on it means anything.
    /// </summary>
    public virtual bool CanDetonate => false;

    /// <summary>Whether a press right now would actually fire. A rigged device that is mid-throw
    /// or cooling down says no, and the board greys its marker rather than swallowing the click.</summary>
    public virtual bool IsReady => false;

    /// <summary>Fire a rigged device. Called on every peer from one broadcast, so whatever this
    /// does has to be deterministic from nothing but the call itself.</summary>
    public virtual void Detonate() { }

    /// <summary>
    /// The scene a kind is built from, or null for the ones still generating their own geometry
    /// in <c>_Ready</c>.
    ///
    /// Scene-backed is where all of these are going (see `docs/look-plan.md` phase 3): a hazard
    /// you cannot open is a hazard you cannot shape, and "edit a Vector3 and relaunch" is not a
    /// way to author anything that has to look good. The three spawn paths do not change —
    /// they all come through <see cref="Create"/> — so porting a hazard is one scene and one
    /// line here.
    /// </summary>
    private static string? ScenePathOf(HazardKind kind) => kind switch
    {
        HazardKind.SpringTrap => "res://scenes/tiles/hazards/SpringTrap.tscn",
        HazardKind.LogTrap => "res://scenes/tiles/hazards/LogTrap.tscn",
        HazardKind.Bullseye => "res://scenes/tiles/hazards/Bullseye.tscn",
        HazardKind.BombTrap => "res://scenes/tiles/hazards/BombTrap.tscn",
        _ => null,
    };

    /// <summary>The one place a kind becomes a node. Everything that spawns a hazard —
    /// build-phase placement, the sentry, an authored piece — comes through here, so a new
    /// hazard is one class and one case.</summary>
    public static TrackHazard Create(HazardKind kind)
    {
        TrackHazard? hazard = null;

        if (ScenePathOf(kind) is { } path)
        {
            if (ResourceLoader.Load<PackedScene>(path) is { } scene)
                hazard = scene.Instantiate() as TrackHazard;

            if (hazard == null)
                GD.PushError($"[Hazard] {kind} scene missing or wrong root type: {path}.");
        }

        hazard ??= kind switch
        {
            HazardKind.LaunchPad => new Hazards.LaunchPadHazard(),
            HazardKind.PopUpRamp => new Hazards.PopUpRampHazard(),
            _ => new Hazards.LaunchPadHazard(),
        };

        hazard.Kind = kind;
        hazard.Name = $"{kind}";
        return hazard;
    }

    /// <summary>
    /// Turn a freshly created hazard into a preview of itself: inert, intangible, and
    /// translucent.
    ///
    /// The builder is deciding whether a thing belongs somewhere, and the honest answer to that
    /// is the thing itself — a green ring on the road says a hazard fits here, while a ghosted
    /// gantry says what the tile is about to look like. It matters most for the hazards that own
    /// real space: a log trap is fourteen metres of structure over the road, and no abstract
    /// marker can tell the builder whether that is too much on this piece.
    ///
    /// Everything is switched off rather than special-cased inside each hazard: no processing, so
    /// nothing ticks or swings; no collision layers, so cars drive through it; and one translucent
    /// override on every visual, so it reads as intent rather than as an object. A subclass
    /// therefore needs to know nothing about being a ghost.
    /// </summary>
    public void MakeGhost(Color tint)
    {
        var paint = new StandardMaterial3D
        {
            AlbedoColor = tint with { A = 0.42f },
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };

        Ghost(this, paint);

        static void Ghost(Node node, StandardMaterial3D paint)
        {
            node.SetProcess(false);
            node.SetPhysicsProcess(false);

            switch (node)
            {
                case GeometryInstance3D visual:
                    visual.MaterialOverride = paint;
                    break;

                // Off both layer and mask: a preview must not be driven into, and must not
                // notice anything driving through it either.
                case CollisionObject3D solid:
                    solid.CollisionLayer = 0;
                    solid.CollisionMask = 0;
                    break;
            }

            foreach (Node child in node.GetChildren())
                Ghost(child, paint);
        }
    }

    /// <summary>
    /// The house material: per-vertex light, no specular — the same three decisions the cars,
    /// the track and the sentry props all make. Here so every hazard is painted from one tin.
    /// </summary>
    protected static StandardMaterial3D HouseMaterial(Color colour) => new()
    {
        AlbedoColor = colour,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
        Metallic = 0.0f,
        Roughness = 1.0f,
    };
}
