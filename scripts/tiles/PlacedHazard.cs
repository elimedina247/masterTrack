namespace MasterTrack.Tiles;

/// <summary>
/// A furniture hazard committed to the track, addressed the only way a hazard is ever
/// addressed: which tile, which of that tile's slots, which kind. Three ints on the wire.
///
/// Addressing by slot rather than by transform is most of the design: placement is legal by
/// construction (the slot's transform came out of an authored scene), the world position falls
/// out of the tile's entry frame, and lifetime is solved — a hazard whose tile crumbles is
/// gone, because it never had a position of its own to be orphaned at.
/// </summary>
public readonly record struct PlacedHazard(int TileIndex, int SlotIndex, HazardKind Kind);
