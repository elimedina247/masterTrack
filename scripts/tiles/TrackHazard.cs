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

    /// <summary>The one place a kind becomes a node. Everything that spawns a hazard —
    /// build-phase placement, the sentry, an authored piece — comes through here, so a new
    /// hazard is one class and one case.</summary>
    public static TrackHazard Create(HazardKind kind)
    {
        TrackHazard hazard = kind switch
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
