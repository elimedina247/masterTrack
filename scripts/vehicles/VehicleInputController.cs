using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Drives a <see cref="Vehicle"/> straight from local input. This is the standalone
/// equivalent of the source project's VehicleController node — handy for a test scene or a
/// tuning bench where there's no networking in the way.
///
/// In an actual match the racer's car drives itself, because it has to gate on "am I the
/// peer that owns this car?" — see <c>RacerController</c>.
/// </summary>
[GlobalClass]
public partial class VehicleInputController : Node
{
	/// <summary>The vehicle to drive. Required.</summary>
	[Export] public Vehicle? VehicleNode { get; set; }

	/// <summary>Which input actions to read. Defaults to the racer_* actions.</summary>
	[Export] public VehicleInputActions Actions { get; set; } = new();

	public override void _Ready()
	{
		if (VehicleNode == null)
			GD.PushError($"[VehicleInputController] {Name} has no VehicleNode assigned; nothing to drive.");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (VehicleNode == null)
			return;

		VehicleInputState.Sample(Actions).ApplyTo(VehicleNode);
	}
}
