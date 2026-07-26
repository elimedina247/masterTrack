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

    /// <summary>
    /// How far above the track a placed tile appears before it comes down, in metres. Three
    /// cells up: high enough that a racer sees it coming from a way off, which is the point.
    /// </summary>
    [Export] public float TileFallHeight { get; set; } = TrackTile.Size * 3.0f;

    /// <summary>
    /// How fast a placed tile descends, in metres per second. Its own knob rather than a
    /// duration, so the fall reads at a consistent speed whatever the drop height is — at the
    /// defaults it works out to a five second descent, which is about one dealt tile's worth.
    /// </summary>
    [Export] public float TileFallSpeed { get; set; } = 24.0f;

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

        // No drop: the racers are already sitting on this straight, so it has to be under them
        // from the first frame rather than descending onto their roofs.
        foreach (PlacedTile tile in Grid.Tiles)
            SpawnTileNode(tile, drop: false);

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

        // The Track Master is normally the host, so this would be an RPC to ourselves —
        // and Godot does not deliver a self-targeted RpcId to a method declared
        // CallLocal = false. Go straight to the authoritative path rather than round-trip
        // through the network layer.
        if (Multiplayer.IsServer())
        {
            AuthorizeAndBroadcast(catalogIndex, Multiplayer.GetUniqueId());
            return;
        }

        RpcId(1, MethodName.ServerPlaceTile, catalogIndex);
    }

    /// <summary>Server only. A remote peer is asking to place a tile.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerPlaceTile(int catalogIndex)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        AuthorizeAndBroadcast(catalogIndex, Multiplayer.GetRemoteSenderId());
    }

    /// <summary>
    /// Server only. Check the requester really is the Track Master and that the tile is legal
    /// where it would land, then broadcast the confirmed placement.
    ///
    /// Takes the requester's id rather than reading it from the multiplayer API, because this
    /// runs both for a remote request and for the host asking on its own behalf.
    /// </summary>
    private void AuthorizeAndBroadcast(int catalogIndex, int senderId)
    {
        if (senderId != GameManager.Instance.TrackMasterPeerId)
        {
            GD.PushWarning($"[Track] Peer {senderId} tried to place a tile but is not the Track Master.");
            return;
        }

        TileDefinition? definition = TileCatalog.At(catalogIndex);
        if (definition == null)
        {
            GD.PushWarning($"[Track] Peer {senderId} requested unknown tile index {catalogIndex}.");
            return;
        }

        if (!Grid.CanPlace(Grid.HeadCell, definition.ToTileData(), out string reason))
        {
            GD.PushWarning($"[Track] Rejected {definition.DisplayName} from peer {senderId}: {reason}");
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

    /// <summary>
    /// Build the node for a placed tile. <paramref name="drop"/> is what separates a tile the
    /// Track Master just played — which falls in from above, so the racers see it arrive — from
    /// the starting straight, which is simply the ground the race begins on.
    /// </summary>
    private void SpawnTileNode(PlacedTile tile, bool drop = true)
    {
        var node = new TrackTile { Name = $"Tile{tile.Index}" };
        // Added to the tree first so the geometry it builds enters the tree with it.
        AddChild(node);
        node.Initialize(tile.Data, tile.Index, tile.Cell, tile.EntryDirection, tile.EntryHeight,
                        fallHeight: drop ? TileFallHeight : 0.0f,
                        fallSpeed: TileFallSpeed);
    }

    /// <summary>
    /// World position at the centre of the next open cell, at the elevation the next tile will
    /// start from — so the board camera and the head marker climb with the track.
    /// </summary>
    public Vector3 HeadWorldPosition => TileCatalog.CellToWorld(Grid.HeadCell, Grid.HeadHeight);
}
