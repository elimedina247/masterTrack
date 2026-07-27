namespace MasterTrack.Vehicles;

/// <summary>
/// Names of the node groups the vehicle uses to identify what it is driving on.
///
/// A <see cref="GroundRay"/> reads the <b>first</b> group on whatever body it hits and looks
/// that name up in the vehicle's grip dictionaries. So every road, dirt or grass collider needs
/// to be in exactly one of these groups, and it must be the first one — put gameplay groups
/// on the body after the surface group, or on a different node.
/// </summary>
public static class SurfaceGroups
{
    /// <summary>Tarmac: the default racing surface. Track tiles belong here.</summary>
    public const string Road = "Road";

    /// <summary>Loose dirt: less grip, more drag.</summary>
    public const string Dirt = "Dirt";

    /// <summary>Grass runoff: least grip, most drag.</summary>
    public const string Grass = "Grass";

    /// <summary>Sheet ice, used by the Ice Patch hazard tile. Barely any grip at all.</summary>
    public const string Ice = "Ice";

    /// <summary>Whether a node group name is one of the four surfaces.</summary>
    public static bool IsKnown(string group)
        => group is Road or Dirt or Grass or Ice;
}
