using Godot;
using MasterTrack.Networking;

namespace MasterTrack.Tiles;

/// <summary>
/// Owns the track: the authoritative <see cref="TrackGrid"/> and the tile nodes in the scene.
///
/// Placement is a *request*. The Track Master's client asks the server to place a tile; the
/// server checks that the sender really is the Track Master and that the tile is legal, then
/// broadcasts the confirmed placement. Every peer applies that placement to its own grid and
/// builds the tile node itself.
///
/// Nothing about the tile geometry goes over the wire — a catalog index and the order of
/// placements is enough for every peer to arrive at an identical track. That also means a
/// client can never place a tile by lying about its shape.
/// </summary>
public partial class TrackController : Node3D
{
    /// <summary>Cell the track starts from.</summary>
    [Export] public Vector2I StartCell { get; set; } = new(0, 3);

    /// <summary>Direction the track runs at the start line.</summary>
    [Export] public TrackDirection StartDirection { get; set; } = TrackDirection.North;

    /// <summary>How many plain straights to lay down before the Track Master takes over.</summary>
    [Export] public int StartingStraightLength { get; set; } = 4;

    public TrackGrid Grid { get; } = new();

    /// <summary>Fired on every peer once a tile has landed.</summary>
    [Signal] public delegate void TilePlacedEventHandler(int trackIndex, int hazard);

    /// <summary>Fired when the head of the track moves, so the builder can re-aim.</summary>
    [Signal] public delegate void TrackHeadChangedEventHandler();

    /// <summary>True only in real networked play; solo skips the RPC round trip entirely.</summary>
    private bool Networked => Multiplayer.MultiplayerPeer is ENetMultiplayerPeer;

    public override void _Ready()
    {
        BuildStartingTrack();
    }

    private void BuildStartingTrack()
    {
        Grid.BuildStartingStraight(StartCell, StartDirection, StartingStraightLength);

        foreach (PlacedTile tile in Grid.Tiles)
            SpawnTileNode(tile);

        EmitSignal(SignalName.TrackHeadChanged);
    }

    /// <summary>
    /// Client-side intent: ask the server to add a tile to the end of the track. Safe to call
    /// on any peer — solo play applies it directly.
    /// </summary>
    public void RequestPlaceTile(int catalogIndex)
    {
        if (TileCatalog.At(catalogIndex) == null)
        {
            GD.PushWarning($"[Track] Ignoring placement request for unknown tile index {catalogIndex}.");
            return;
        }

        if (!Networked)
        {
            ApplyPlacement(catalogIndex);
            return;
        }

        RpcId(1, MethodName.ServerPlaceTile, catalogIndex);
    }

    /// <summary>
    /// Server only. Check the sender is the Track Master and the tile is legal where it would
    /// land, then broadcast the confirmed placement.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerPlaceTile(int catalogIndex)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        int sender = Multiplayer.GetRemoteSenderId();
        if (sender != GameManager.Instance.TrackMasterPeerId)
        {
            GD.PushWarning($"[Track] Peer {sender} tried to place a tile but is not the Track Master.");
            return;
        }

        TileDefinition? definition = TileCatalog.At(catalogIndex);
        if (definition == null)
        {
            GD.PushWarning($"[Track] Peer {sender} requested unknown tile index {catalogIndex}.");
            return;
        }

        if (!Grid.CanPlace(Grid.HeadCell, definition.ToTileData(), out string reason))
        {
            GD.PushWarning($"[Track] Rejected {definition.DisplayName} from peer {sender}: {reason}");
            return;
        }

        Rpc(MethodName.ConfirmTilePlaced, catalogIndex);
        ApplyPlacement(catalogIndex);
    }

    /// <summary>Server -> all: the placement is confirmed; reflect it on every peer.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ConfirmTilePlaced(int catalogIndex) => ApplyPlacement(catalogIndex);

    /// <summary>
    /// Add the tile to this peer's grid and build its node. Runs identically everywhere, which
    /// is what keeps the clients' tracks in step with the server's.
    /// </summary>
    private void ApplyPlacement(int catalogIndex)
    {
        TileDefinition? definition = TileCatalog.At(catalogIndex);
        if (definition == null)
            return;

        PlacedTile? tile = Grid.Place(definition.ToTileData());
        if (tile == null)
            return;

        SpawnTileNode(tile);

        EmitSignal(SignalName.TilePlaced, tile.Index, (int)tile.Data.Hazard);
        EmitSignal(SignalName.TrackHeadChanged);
    }

    private void SpawnTileNode(PlacedTile tile)
    {
        var node = new TrackTile { Name = $"Tile{tile.Index}" };
        // Added to the tree first so the geometry it builds enters the tree with it.
        AddChild(node);
        node.Initialize(tile.Data, tile.Index, tile.Cell, tile.EntryDirection);
    }

    /// <summary>World position at the centre of the next open cell.</summary>
    public Vector3 HeadWorldPosition => TileCatalog.CellToWorld(Grid.HeadCell);
}
