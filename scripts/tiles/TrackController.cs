using System.Collections.Generic;
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

    /// <summary>
    /// Who is allowed to add to this track.
    /// </summary>
    public enum BuildAuthority
    {
        /// <summary>The peer holding the Track Master role. What a match runs on.</summary>
        TrackMaster,

        /// <summary>
        /// Whoever is hosting. What the lobby runs on: there is no Track Master yet — the role is
        /// unassigned and <see cref="GameManager.TrackMasterPeerId"/> is 0 — and the track being
        /// built there is a sandbox, not a race.
        /// </summary>
        Host,
    }

    /// <summary>Which peer the server will take placements from. See <see cref="BuildAuthority"/>.</summary>
    [Export] public BuildAuthority Authority { get; set; } = BuildAuthority.TrackMaster;

    /// <summary>
    /// Whether tiles can be taken back off the end. Off in a match — an undo would rewrite track
    /// the racers may already be standing on — and on in the lobby, where building is the point.
    /// </summary>
    [Export] public bool AllowUndo { get; set; }

    public TrackGrid Grid { get; } = new();

    /// <summary>
    /// Catalog indices of every tile placed onto the starting straight, in order. This is the whole
    /// track as data — the same list that would be enough for any peer to rebuild it — and it is
    /// what a late joiner is sent. The starting straight is not in here; that is laid by
    /// <see cref="BuildStartingTrack"/> on every peer alike.
    /// </summary>
    private readonly List<int> _placed = new();

    /// <summary>Fired on every peer once a tile has landed.</summary>
    [Signal] public delegate void TilePlacedEventHandler(int trackIndex, int hazard);

    /// <summary>Fired when the head of the track moves, so the builder can re-aim.</summary>
    [Signal] public delegate void TrackHeadChangedEventHandler();

    /// <summary>True only in real networked play; solo skips the RPC round trip entirely.</summary>
    private static bool Networked => NetworkManager.Instance.IsNetworked;

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

        GD.Print($"[Track] Start line at {StartCell} facing {StartDirection.DisplayName()}; "
                 + $"{StartingStraightLength} starting tile(s), head now {Grid.HeadCell}.");

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
        if (!MayBuild(senderId))
        {
            GD.PushWarning($"[Track] Peer {senderId} tried to place a tile but is not the builder.");
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

    /// <summary>
    /// Server only. Whether a peer is the one this track takes tiles from.
    ///
    /// Solo play answers yes to everything: there is one peer, it is the server, and asking it to
    /// prove it is the Track Master when no roles have been handed out would make the lobby's
    /// builder inert for the only person who can use it.
    /// </summary>
    private bool MayBuild(int senderId)
    {
        if (!Networked)
            return true;

        // Godot's high-level multiplayer always gives the server peer id 1 — the same 1 the
        // client-side RpcId above sends to.
        return Authority == BuildAuthority.Host
            ? senderId == 1
            : senderId == GameManager.Instance.TrackMasterPeerId;
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

        _placed.Add(catalogIndex);
        SpawnTileNode(tile);

        EmitSignal(SignalName.TilePlaced, tile.Index, (int)tile.Data.Hazard);
        EmitSignal(SignalName.TrackHeadChanged);
    }

    // ---- Undo ----

    /// <summary>
    /// Client-side intent: ask the server to take the last tile back off the end. The mirror of
    /// <see cref="RequestPlaceTile"/>, and it takes the same route for the same reasons.
    /// </summary>
    public void RequestRemoveTile()
    {
        if (!AllowUndo)
            return;

        if (!Networked)
        {
            ApplyRemoval();
            return;
        }

        if (Multiplayer.IsServer())
        {
            AuthorizeAndBroadcastRemoval(Multiplayer.GetUniqueId());
            return;
        }

        RpcId(1, MethodName.ServerRemoveTile);
    }

    /// <summary>Server only. A remote peer is asking to take a tile back.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRemoveTile()
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        AuthorizeAndBroadcastRemoval(Multiplayer.GetRemoteSenderId());
    }

    /// <summary>
    /// Server only. Check the requester may build and that there is something of theirs to take
    /// back, then broadcast it.
    /// </summary>
    private void AuthorizeAndBroadcastRemoval(int senderId)
    {
        if (!AllowUndo || !MayBuild(senderId))
        {
            GD.PushWarning($"[Track] Peer {senderId} tried to undo a tile but is not the builder.");
            return;
        }

        // Nothing placed yet. The starting straight is not undoable — it is the anchor the whole
        // track hangs off, and a track with no start has nowhere to put the next tile.
        if (_placed.Count == 0)
            return;

        Rpc(MethodName.ConfirmTileRemoved);
        ApplyRemoval();
    }

    /// <summary>Server -> all: the removal is confirmed; reflect it on every peer.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ConfirmTileRemoved() => ApplyRemoval();

    /// <summary>
    /// Take the last tile off this peer's grid and free its node. Runs identically everywhere, the
    /// same way <see cref="ApplyPlacement"/> does.
    /// </summary>
    private void ApplyRemoval()
    {
        if (_placed.Count == 0)
            return;

        PlacedTile? tile = Grid.RemoveLast();
        if (tile == null)
            return;

        _placed.RemoveAt(_placed.Count - 1);

        // Detached now rather than only queued, so the name is free again immediately: the next
        // tile placed takes the same track index, and a node still sitting on that name would get
        // the new one renamed and leave it un-undoable.
        Node? node = GetNodeOrNull(TileNodeName(tile.Index));
        if (node != null)
        {
            RemoveChild(node);
            node.QueueFree();
        }

        EmitSignal(SignalName.TrackHeadChanged);
    }

    // ---- Catch-up ----

    /// <summary>
    /// Server only. Hand a peer the whole track as it currently stands.
    ///
    /// Placements are broadcast as they happen, which is all a match needs — everybody loads the
    /// scene together and then watches it grow. The lobby is the opposite: people arrive whenever,
    /// and without this a late joiner sees a bare start tile while everyone else drives on track
    /// that, for them, is not there to be driven on or fallen off.
    /// </summary>
    public void SyncTo(int peerId)
    {
        if (!Networked || !NetworkManager.Instance.IsHost || peerId == 1)
            return;

        RpcId(peerId, MethodName.SyncTrack, _placed.ToArray());
    }

    /// <summary>
    /// Server -> one peer: throw away whatever track this peer has and replay the real one.
    ///
    /// Rebuilt from scratch rather than topped up, because "what have you got and what are you
    /// missing" is a conversation, and a list of catalog indices is small enough that replaying it
    /// whole is cheaper than having it.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncTrack(int[] catalogIndices)
    {
        // Detached before being freed, not just queued: QueueFree runs at the end of the frame, so
        // the old Tile0 would still be holding that name when the replay tries to add the new one,
        // and Godot would quietly rename it out from under the undo lookup.
        foreach (Node child in GetChildren())
        {
            if (child is not TrackTile)
                continue;

            RemoveChild(child);
            child.QueueFree();
        }

        _placed.Clear();
        BuildStartingTrack();

        foreach (int catalogIndex in catalogIndices)
            ApplyPlacement(catalogIndex);
    }

    private static string TileNodeName(int trackIndex) => $"Tile{trackIndex}";

    /// <summary>
    /// Build the node for a placed tile. <paramref name="drop"/> is what separates a tile the
    /// Track Master just played — which falls in from above, so the racers see it arrive — from
    /// the starting straight, which is simply the ground the race begins on.
    /// </summary>
    private void SpawnTileNode(PlacedTile tile, bool drop = true)
    {
        var node = new TrackTile { Name = TileNodeName(tile.Index) };
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
