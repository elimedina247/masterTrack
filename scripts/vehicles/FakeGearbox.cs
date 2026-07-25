using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// A gearbox that exists only for the ears and the HUD.
///
/// The car has no transmission — drive force comes straight off a speed curve. But a
/// continuously rising engine note reads as an electric motor or a CVT, and it gives the
/// player almost nothing to judge their speed by. Sawtoothing a fake rev counter through
/// bands as road speed climbs sounds like a geared car and makes speed audible, without a
/// single gear existing in the physics. Plenty of arcade racers do exactly this.
///
/// Bands are spaced by a power law rather than evenly, because a real gearbox has a very short
/// first gear and a very long top gear — evenly spaced bands sound wrong immediately.
/// </summary>
public static class FakeGearbox
{
    /// <summary>One sampled state of the imaginary gearbox.</summary>
    public readonly struct Reading
    {
        /// <summary>1-based gear number.</summary>
        public int Gear { get; init; }

        /// <summary>Revs to pitch the engine sample by.</summary>
        public float Rpm { get; init; }
    }

    /// <summary>
    /// Work out which imaginary gear a given road speed falls in, and how far up the revs it
    /// sits within that gear.
    /// </summary>
    /// <param name="speedFraction">Road speed over top speed, 0..1.</param>
    /// <param name="gearCount">How many bands to divide the speed range into.</param>
    /// <param name="idleRpm">Revs at a standstill.</param>
    /// <param name="maxRpm">Revs at the top of every band.</param>
    /// <param name="shiftDownFraction">
    /// Where the revs drop to at the start of a band, as a fraction of the idle-to-max range.
    /// Bigger = closer ratios and a smaller audible drop on each imaginary upshift.
    /// </param>
    /// <param name="bandExponent">
    /// Higher makes the low gears cover less ground and the top gear more, which is how a real
    /// gearbox is spaced. 1.0 would give evenly sized bands.
    /// </param>
    public static Reading Sample(float speedFraction, int gearCount, float idleRpm, float maxRpm,
                                 float shiftDownFraction = 0.42f, float bandExponent = 1.7f)
    {
        gearCount = Mathf.Max(gearCount, 1);
        float t = Mathf.Clamp(speedFraction, 0.0f, 1.0f);

        // Invert the band spacing to land directly on the gear instead of searching for it.
        float scaled = Mathf.Pow(t, 1.0f / bandExponent) * gearCount;
        int index = Mathf.Min((int)scaled, gearCount - 1);
        float withinBand = scaled - index;

        float bandLow = idleRpm + (maxRpm - idleRpm) * shiftDownFraction;
        float rpm = Mathf.Lerp(bandLow, maxRpm, withinBand);

        // Ease down to a genuine idle as the car comes to rest, so a stopped car doesn't sit
        // there holding revs.
        float idleBlend = Mathf.Clamp(t / 0.02f, 0.0f, 1.0f);
        rpm = Mathf.Lerp(idleRpm, rpm, idleBlend);

        return new Reading { Gear = index + 1, Rpm = rpm };
    }
}
