using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// The noise the tires make when they let go. One looping squeal sample, with its volume and
/// pitch ridden by how hard the worst wheel is currently sliding.
///
/// This is driven by <see cref="TireSlip"/>, the same measure that decides where
/// <see cref="SkidMarks"/> lays rubber, so the car is always heard sliding exactly when it is
/// seen to slide. Retune one and the other follows.
///
/// <b>Needs a looping sample on <see cref="AudioStreamPlayer3D.Stream"/> to do anything.</b>
/// A one-shot will play its length and stop, no matter what this script does with the volume
/// — see the class remarks in the repo docs for the import settings that matter.
/// </summary>
[GlobalClass]
public partial class TireSqueal : AudioStreamPlayer3D
{
    /// <summary>The vehicle whose wheels to listen to. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    // ---------------------------------------------------------------- When to squeal

    /// <summary>Sideways speed in m/s past which the tires start to sing.</summary>
    [ExportGroup("Slip")]
    [Export] public float SlipThreshold { get; set; } = 1.5f;

    /// <summary>
    /// How far past the threshold sideways speed has to go for a full-volume squeal, in m/s.
    /// </summary>
    [Export] public float FullSlip { get; set; } = 7.0f;

    /// <summary>
    /// Road speed in m/s below which the squeal is faded out entirely. A tire scrubbing at
    /// walking pace scuffs; it doesn't squeal, and without this a car doing donuts on the
    /// spot screams at full volume.
    /// </summary>
    [Export] public float MinSpeed { get; set; } = 3.0f;

    // ---------------------------------------------------------------- How it sounds

    /// <summary>Volume at the quietest audible slide.</summary>
    [ExportGroup("Sound")]
    [Export] public float QuietDb { get; set; } = -34.0f;

    /// <summary>Volume at a full-lock slide.</summary>
    [Export] public float LoudDb { get; set; } = -5.0f;

    /// <summary>Pitch at the quietest audible slide.</summary>
    [Export] public float PitchMin { get; set; } = 0.85f;

    /// <summary>Pitch at a full-lock slide. Rising pitch with slip is most of what sells it.</summary>
    [Export] public float PitchMax { get; set; } = 1.25f;

    /// <summary>How fast the squeal comes up when a tire lets go, per second.</summary>
    [Export] public float Attack { get; set; } = 14.0f;

    /// <summary>
    /// How fast it dies away when the tire hooks up again, per second. Slower than
    /// <see cref="Attack"/> on purpose — it stops the sound chattering on and off across the
    /// threshold when a tire is hovering right at the limit.
    /// </summary>
    [Export] public float Release { get; set; } = 6.0f;

    /// <summary>
    /// Volume scale per surface group. Tarmac squeals; loose surfaces mostly don't, so they
    /// are dialled right down. A surface missing from here is silent.
    ///
    /// Scaling one sample is a stand-in: dirt and grass really want their own gravel-scatter
    /// loop rather than a quiet tarmac squeal. Swapping <see cref="AudioStreamPlayer3D.Stream"/>
    /// per surface is the upgrade path.
    /// </summary>
    [Export] public Godot.Collections.Dictionary<string, float> SurfaceVolume { get; set; } = new()
    {
        { SurfaceGroups.Road, 1.0f },
        { SurfaceGroups.Dirt, 0.25f },
        { SurfaceGroups.Grass, 0.15f },
        { SurfaceGroups.Ice, 0.1f },
    };

    /// <summary>Smoothed slide intensity currently driving the sound, 0..1.</summary>
    public float Intensity { get; private set; }

    public override void _Ready()
    {
        // Set here rather than per-scene so every car answers the pause menu's SFX slider.
        Bus = "SFX";

        if (VehicleNode == null)
            GD.PushWarning($"[TireSqueal] {Name} has no VehicleNode assigned; staying silent.");

        if (Stream == null)
            GD.PushWarning($"[TireSqueal] {Name} has no Stream assigned; staying silent. " +
                           "Drop a looping tire-squeal sample on it, and make sure its import " +
                           "Loop Mode is not Disabled or it will play once and stop.");
    }

    public override void _Process(double delta)
    {
        if (VehicleNode is not { IsVehicleReady: true } vehicle || Stream == null)
            return;

        float target = 0.0f;
        foreach (GroundRay wheel in vehicle.WheelArray)
        {
            float slip = TireSlip.Intensity(vehicle, wheel, SlipThreshold, FullSlip);

            if (slip > 0.0f && SurfaceVolume.TryGetValue(wheel.SurfaceType, out float scale))
                target = Mathf.Max(target, slip * scale);
        }

        target *= Mathf.Clamp(vehicle.Speed / Mathf.Max(MinSpeed, 0.001f), 0.0f, 1.0f);

        float rate = target > Intensity ? Attack : Release;
        Intensity = Mathf.Lerp(Intensity, target, 1.0f - Mathf.Exp(-rate * (float)delta));

        // The release is exponential and never quite reaches zero, so cut it off once it is
        // far enough down that stopping can't be heard.
        if (Intensity <= 0.005f)
        {
            Intensity = 0.0f;
            if (Playing)
                Stop();
            return;
        }

        if (!Playing)
            Play();

        VolumeDb = Mathf.Lerp(QuietDb, LoudDb, Intensity);
        PitchScale = Mathf.Lerp(PitchMin, PitchMax, Intensity);
    }
}
