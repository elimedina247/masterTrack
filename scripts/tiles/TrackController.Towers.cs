using Godot;
using MasterTrack.Networking;

namespace MasterTrack.Tiles;

/// <summary>
/// The one message a turret sends. Its own partial because it is the only thing on the track
/// that fires <i>itself</i> — every other broadcast here is a person's click arriving.
///
/// The split, and the reason it is this way round: aiming happens on every peer for free, off
/// the car poses they already receive, so a turret's barrel tracks correctly everywhere without
/// a byte crossing the wire. What cannot be worked out locally is <b>the moment</b> — two peers
/// resolving "closest car" differently for one frame would be two peers firing different numbers
/// of rockets at different people, and nothing downstream could reconcile that. So the server
/// alone decides, names the target, and every peer builds the identical rocket. The same shape
/// as a detonation, one int wider.
/// </summary>
public partial class TrackController
{
	/// <summary>
	/// Server only: a turret has decided to shoot. Broadcast it and fire locally.
	///
	/// No authorisation check, because there is no sender to check — this comes from the server's
	/// own copy of a hazard it placed itself, not from anybody's client. The guard is simply that
	/// a non-host that somehow reaches here does nothing.
	/// </summary>
	public void FireTower(int tileIndex, int slotIndex, int targetPeerId)
	{
		if (!Networked)
		{
			ApplyTowerFire(tileIndex, slotIndex, targetPeerId);
			return;
		}

		if (!NetworkManager.Instance.IsHost)
			return;

		Rpc(MethodName.ConfirmTowerFired, tileIndex, slotIndex, targetPeerId);
		ApplyTowerFire(tileIndex, slotIndex, targetPeerId);
	}

	/// <summary>
	/// Unreliable on purpose, and the only broadcast here that is.
	///
	/// A placement must arrive or the peer's track is wrong forever; a shot is a moment. A rocket
	/// that went missing on one machine is one racer's near-miss looking slightly different there
	/// — where a rocket that arrived half a second late, after its target had already driven out
	/// of the blast, would be a machine simulating a different race. Late is worse than never.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void ConfirmTowerFired(int tileIndex, int slotIndex, int targetPeerId)
		=> ApplyTowerFire(tileIndex, slotIndex, targetPeerId);

	/// <summary>Put the rocket in the air on this machine. A tower that has crumbled with its
	/// tile is simply not there to fire — the hazard rules' standing answer.</summary>
	private void ApplyTowerFire(int tileIndex, int slotIndex, int targetPeerId)
		=> (HazardAt(tileIndex, slotIndex) as Hazards.RocketTowerHazard)?.FireAt(targetPeerId);
}
