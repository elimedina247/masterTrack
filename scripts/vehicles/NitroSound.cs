using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// The bang when a nitro charge fires. Plays a one-shot sample from the top on every
/// <see cref="Vehicle.NitroFired"/>, with a little random pitch so back-to-back charges
/// don't sound like the exact same sample twice.
///
/// Listens to the signal rather than polling <see cref="Vehicle.IsNitroActive"/>: the boost
/// flag also goes up for drift boosts, and it stays up for the whole burst — the signal is
/// the only thing that fires exactly once per charge spent.
/// </summary>
[GlobalClass]
public partial class NitroSound : AudioStreamPlayer3D
{
    /// <summary>The vehicle whose nitro to listen to. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>
    /// Half-width of the random pitch spread around 1.0. Zero plays the sample straight
    /// every time.
    /// </summary>
    [Export] public float PitchJitter { get; set; } = 0.06f;

    public override void _Ready()
    {
        // Set here rather than per-scene so every car answers the pause menu's SFX slider.
        Bus = "SFX";

        if (Stream == null)
            GD.PushWarning($"[NitroSound] {Name} has no Stream assigned; staying silent.");

        if (VehicleNode == null)
        {
            GD.PushWarning($"[NitroSound] {Name} has no VehicleNode assigned; staying silent.");
            return;
        }

        VehicleNode.NitroFired += OnNitroFired;
    }

    public override void _ExitTree()
    {
        if (VehicleNode != null)
            VehicleNode.NitroFired -= OnNitroFired;
    }

    private void OnNitroFired(int chargesRemaining)
    {
        if (Stream == null)
            return;

        PitchScale = 1.0f + (float)GD.RandRange(-PitchJitter, PitchJitter);
        // Play() restarts from the start if the last one is still ringing, which is what a
        // rapid double-tap should sound like.
        Play();
    }
}
