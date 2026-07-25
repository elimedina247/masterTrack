namespace MasterTrack.Vehicles;

/// <summary>
/// Small maths helpers shared by the vehicle physics. These exist so the port can match
/// GDScript's semantics exactly where C#'s built-ins differ.
/// </summary>
internal static class VehicleMath
{
    /// <summary>
    /// GDScript's <c>signf</c>: -1, 0 or +1 as a float. Godot's <c>Mathf.Sign</c> and
    /// <c>Math.Sign</c> both return an int, and the physics relies on multiplying by this
    /// in float expressions where an exact 0 for 0 matters.
    /// </summary>
    public static float SignF(float value)
    {
        if (value > 0.0f)
            return 1.0f;
        if (value < 0.0f)
            return -1.0f;
        return 0.0f;
    }

    /// <summary>Component-wise reciprocal, matching GDScript's <c>Vector3.inverse()</c>.</summary>
    public static Godot.Vector3 Inverse(Godot.Vector3 v) => new(1.0f / v.X, 1.0f / v.Y, 1.0f / v.Z);
}
