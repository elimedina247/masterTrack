namespace MasterTrack.Vehicles;

/// <summary>
/// A node that reads one vehicle and shows something about it — the speedo, the physics overlay,
/// the speed-blur and speed-line effects.
///
/// These live in the HUD rather than on the car, so they can't be wired up in the scene: which
/// car is *yours* isn't known until one spawns, and in a match it arrives by replication. The
/// interface is what lets <see cref="MasterTrack.Game.RacerArena"/> bind the whole HUD in one
/// pass without naming each overlay, so a new overlay is wired up by existing code the moment it
/// is added to the HUD.
/// </summary>
public interface IVehicleObserver
{
    /// <summary>The vehicle to watch. Set at runtime by whoever spawns the car.</summary>
    Vehicle? VehicleNode { get; set; }
}
