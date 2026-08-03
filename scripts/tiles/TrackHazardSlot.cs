using Godot;

namespace MasterTrack.Tiles;

/// <summary>
/// A spot on a piece where a hazard may go, authored the way seams are: drop one into a piece
/// scene as a direct child of the root, position it on the road, pick its kind in the
/// inspector. <see cref="Tool.PieceCatalog"/> reads them out of the scene file without
/// instancing anything, exactly as it reads <see cref="Tool.TrackConnector"/> seams.
///
/// Slots rather than free placement on purpose: placement becomes legal by construction — no
/// overlap maths, no clamping to the road edge — and every hazard lands somewhere a human
/// decided was a good spot. A piece with no slots accepts no hazards, which is a design lever:
/// the builder learns which pieces are hazard-friendly, and picking pieces gets more
/// interesting for it.
///
/// <b>The transform is most of the contract</b>, the connector's rule: position is where the
/// hazard sits, local <c>-Z</c> is the direction of travel across it, and the node's up is the
/// road surface's up — author one into a banked corner and everything placed there arrives
/// correctly rolled with no special casing anywhere.
///
/// Extends <see cref="Marker3D"/> like the connector does: visible in the editor, nothing in
/// the game.
/// </summary>
[Tool]
[GlobalClass]
public partial class TrackHazardSlot : Marker3D
{
    /// <summary>What kind of mounting this spot offers. A hazard fits only when its own
    /// required kind matches — see <see cref="HazardKindExtensions.SlotKind"/>.</summary>
    [Export]
    public HazardSlotKind Kind { get; set; } = HazardSlotKind.Surface;
}
