using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// How hard the car is sliding, reduced to a single 0..1 number.
///
/// Every effect that keys off slip goes through here so they all agree on what "sliding" means.
/// If the skid marks and the squeal each carried their own copy of this they would drift apart
/// the first time one was tuned, and a car would lay rubber in silence — or squeal while driving
/// a clean line.
///
/// There is no per-wheel slip to read any more: the car has no tire model, only a body that
/// resists sideways motion by a percentage (see <see cref="Vehicle.ProcessGrip"/>). So slip is
/// measured on the body and gated per corner by whether that corner is touching anything.
/// </summary>
public static class TireSlip
{
    /// <summary>
    /// Slide intensity at one corner, 0..1. A corner off the ground always reads 0, so a car
    /// mid-jump neither squeals nor lays rubber.
    /// </summary>
    /// <param name="vehicle">The car. Sideways speed and drift state are read from it.</param>
    /// <param name="ray">The corner being asked about.</param>
    /// <param name="threshold">Sideways speed in m/s before it counts as sliding.</param>
    /// <param name="range">
    /// How far past the threshold sideways speed has to go to read 1.0. Smaller makes effects
    /// reach full strength sooner.
    /// </param>
    public static float Intensity(Vehicle vehicle, GroundRay ray, float threshold, float range)
    {
        if (!ray.IsGrounded)
            return 0.0f;

        float excess = Mathf.Abs(vehicle.LateralSpeed) - threshold;
        float slide = excess <= 0.0f
            ? 0.0f
            : Mathf.Clamp(excess / Mathf.Max(range, 0.001f), 0.0f, 1.0f);

        // A held drift always reads as sliding, even in the instant before the car has actually
        // built any sideways speed. Without this the tires come in a beat late every time, which
        // reads as the effects lagging the input rather than as grip taking a moment to go.
        return Mathf.Max(slide, vehicle.DriftBlend * DriftFloor);
    }

    /// <summary>
    /// How hard a fully committed drift reads even with no sideways speed yet. Not 1.0 — a drift
    /// that has properly let go should still be visibly harder than one just starting.
    /// </summary>
    private const float DriftFloor = 0.7f;
}
