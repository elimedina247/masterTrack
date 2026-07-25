using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// How hard a tire is sliding, reduced to a single 0..1 number.
///
/// Every effect that keys off slip goes through here so they all agree on what "sliding"
/// means. If the skid marks and the squeal each carried their own copy of this they would
/// drift apart the first time one was tuned, and a car would lay rubber in silence — or
/// squeal while driving a clean line.
/// </summary>
public static class TireSlip
{
    /// <summary>
    /// Lateral and longitudinal slip are each measured against their own threshold and the
    /// worse of the two wins, so a wheel locked up in a straight line reads just as hard as
    /// one sliding sideways.
    /// </summary>
    /// <param name="wheel">The wheel to measure. A wheel off the ground always reads 0.</param>
    /// <param name="lateralThreshold">Slip angle in radians before the tire counts as sliding.</param>
    /// <param name="longitudinalThreshold">Slip ratio before the tire counts as sliding.</param>
    /// <param name="range">
    /// How far past the threshold slip has to go to read as 1.0. Smaller makes effects reach
    /// full strength sooner.
    /// </param>
    public static float Intensity(Wheel wheel, float lateralThreshold,
                                  float longitudinalThreshold, float range)
    {
        // LastCollider tracks the ray cast from the same physics step that filled SlipVector,
        // so this is also what tells callers the collision point and normal are usable.
        if (wheel.LastCollider == null)
            return 0.0f;

        // SlipVector.Y is signed — negative under wheelspin, positive under lock-up — so both
        // components have to be taken as magnitudes.
        float lateral = Mathf.Abs(wheel.SlipVector.X) - lateralThreshold;
        float longitudinal = Mathf.Abs(wheel.SlipVector.Y) - longitudinalThreshold;
        float excess = Mathf.Max(lateral, longitudinal);

        return excess <= 0.0f
            ? 0.0f
            : Mathf.Clamp(excess / Mathf.Max(range, 0.001f), 0.0f, 1.0f);
    }
}
