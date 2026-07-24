using Godot;
using System.Collections.Generic;
using MasterTrack.Networking;
using MasterTrack.Tiles;

namespace MasterTrack.TrackMaster;

/// <summary>
/// The Track Master's side of the game: the top-down board view and the hand of hazard
/// tiles. Placement is a *request* — the client asks the server to place a tile, the
/// server validates and (if legal) spawns/replicates it to everyone. Nothing here trusts
/// the client's placement directly.
/// </summary>
public partial class TrackMasterController : Node3D
{
    /// <summary>The tiles currently in this Track Master's hand for the round.</summary>
    public readonly List<TileData> Hand = new();

    [Signal] public delegate void HandChangedEventHandler();
    [Signal] public delegate void TilePlacedEventHandler(int trackIndex, int hazard);

    public override void _Ready()
    {
        // Only the real Track Master peer should drive this controller.
        if (GameManager.Instance.LocalRole != PlayerRole.TrackMaster)
            SetProcess(false);
    }

    /// <summary>
    /// Client-side intent: ask the server to place the given tile at a track index.
    /// </summary>
    public void RequestPlaceTile(TileData tile, int trackIndex)
    {
        RpcId(1, MethodName.ServerPlaceTile, tile.ToDict(), trackIndex);
    }

    /// <summary>
    /// Server only. Validates the placement (is it this peer's turn / hand / a legal
    /// slot?) and, if valid, broadcasts the confirmed placement. Actual tile spawning
    /// will be wired to a MultiplayerSpawner.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerPlaceTile(Godot.Collections.Dictionary tileDict, int trackIndex)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        int sender = Multiplayer.GetRemoteSenderId();
        if (sender != GameManager.Instance.TrackMasterPeerId)
        {
            GD.PushWarning($"[TrackMaster] Peer {sender} tried to place a tile but is not the Track Master.");
            return;
        }

        TileData tile = TileData.FromDict(tileDict);
        // TODO: validate trackIndex is the next open slot and the tile is in the hand.

        Rpc(MethodName.ConfirmTilePlaced, tileDict, trackIndex);
        ConfirmTilePlaced(tileDict, trackIndex);
    }

    /// <summary>Server -> all: the placement is confirmed; reflect it on every peer.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ConfirmTilePlaced(Godot.Collections.Dictionary tileDict, int trackIndex)
    {
        TileData tile = TileData.FromDict(tileDict);
        EmitSignal(SignalName.TilePlaced, trackIndex, (int)tile.Hazard);
    }
}
