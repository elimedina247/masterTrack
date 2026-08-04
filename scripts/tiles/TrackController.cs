using System.Collections.Generic;
using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;

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
///
/// Under <see cref="RaceRules"/> the track is also a moving window rather than a growing one. The
/// back of it crumbles away behind the racers — see <see cref="TrackTrailLength"/> — and a
/// chequered bar sits across the far end of the furthest tile, which is the thing the racers are
/// trying to reach and the Track Master is trying to keep pushing away from them. Both of those
/// are driven by placements and nothing else, so they need no messages of their own: every peer
/// already receives the placements in order, so every peer condemns the same tile and moves the
/// same bar at the same moment without being told to.
/// </summary>
public partial class TrackController : Node3D
{
    /// <summary>Cell the track starts from.</summary>
    /// <summary>
    /// Where the first tile's entry face goes. The default reproduces where the old start cell put
    /// it: three cells up the +Z axis, less the half cell the face sat back from the cell's centre.
    /// </summary>
    [Export] public Vector3 StartPosition { get; set; } = new(0.0f, 0.0f, 189.0f);

    /// <summary>Direction the track runs at the start line.</summary>
    [Export] public TrackDirection StartDirection { get; set; } = TrackDirection.North;

    /// <summary>
    /// The seam the first tile is laid onto, built from <see cref="StartPosition"/> and
    /// <see cref="StartDirection"/>.
    ///
    /// A heading rather than a yaw in the inspector because the start line is the one place a person
    /// still thinks in compass directions — a track is laid out north or south, not at -1.57
    /// radians. Everything downstream of here is angles and metres.
    /// </summary>
    public TrackAnchor StartAnchor => new(StartPosition, StartDirection.Yaw());

    /// <summary>How many plain straights to lay down before the Track Master takes over.</summary>
    [Export] public int StartingStraightLength { get; set; } = 4;

    /// <summary>
    /// How far above the track a placed tile appears before it comes down, in metres. Two and a
    /// half cells up: high enough that a racer sees it coming from a way off, which is the point.
    /// </summary>
    [Export] public float TileFallHeight { get; set; } = TrackTile.Size * 2.5f;

    /// <summary>
    /// How fast a placed tile descends, in metres per second. Its own knob rather than a
    /// duration, so the fall reads at a consistent speed whatever the drop height is.
    ///
    /// It has to be read against how fast the cars are, not against how long it looks nice for. A
    /// racer on a drift chain is doing the better part of 100 m/s, which is a tile every half
    /// second: a leisurely descent means the tile the Track Master just played finishes arriving
    /// several tiles after the leader has already driven through where it was going to be. At the
    /// default this is about three quarters of a second, so a placement lands roughly as the car
    /// that was one tile back reaches it — close enough to be a threat and still visibly a drop.
    /// </summary>
    [Export] public float TileFallSpeed { get; set; } = 130.0f;

    /// <summary>
    /// Whether this track is running a race: the back of it crumbles away, a finish line sits at
    /// the far end of it, and reaching that finish line wins.
    ///
    /// Off in the lobby, where the track is a sandbox somebody is laying out on purpose. Crumbling
    /// a sandbox is destroying somebody's work, and winning at it means nothing.
    /// </summary>
    [Export] public bool RaceRules { get; set; } = true;

    /// <summary>
    /// How many tiles of track stay standing behind the newest one. Everything further back than
    /// this is condemned, blinks for <see cref="TileExpirySeconds"/>, and falls away.
    ///
    /// This is the back half of the pressure, and it is deliberately counted in tiles rather than
    /// in seconds: it means the road behind the racers disappears exactly as fast as the Track
    /// Master lays road in front of them, so a builder who is racing to stay ahead is also
    /// pulling the floor out from under whoever is last. Six is about two hundred metres of
    /// standing track, which is enough to recover a bad corner in and not enough to sit still on.
    /// </summary>
    [Export] public int TrackTrailLength { get; set; } = 6;

    /// <summary>
    /// How long a condemned tile blinks before it goes, in seconds. The blink speeds up as the
    /// time runs out — see <see cref="TrackTile.Condemn"/> — so this is the whole warning, not a
    /// delay before one.
    /// </summary>
    [Export] public float TileExpirySeconds { get; set; } = 4.0f;

    /// <summary>
    /// How many tiles the Track Master may lay before the track is finished, or 0 for no limit.
    ///
    /// Set from the race length the host picked in the lobby. Without it a match runs until
    /// somebody gets bored, because the Track Master's whole job is making sure the racers never
    /// reach the end — the limit is what eventually takes the tiles out of their hands and turns
    /// the bar at the end of the track into a real finish line.
    ///
    /// Counts placements, not cells and not the starting straight: it is the number of tiles the
    /// Track Master is given to spend.
    /// </summary>
    [Export] public int TileLimit { get; set; }

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

    /// <summary>
    /// Group every track joins, so a car can find the world's floor without being handed a node
    /// path — the same reason racers join <see cref="RacerController.GroupName"/>.
    /// </summary>
    public const string GroupName = "tracks";

    /// <summary>
    /// Road height of the lowest tile still standing, in metres. The world's floor as far as
    /// falling goes: below this (less a margin) there is nothing left to land on, and the kill
    /// plane the cars check themselves against hangs off it — see
    /// <see cref="RacerController"/>. Tracked rather than fixed because the track is the only
    /// terrain there is, and it descends.
    /// </summary>
    public float LowestTileY { get; private set; }

    public TrackGrid Grid { get; } = new();

    /// <summary>
    /// Catalog indices of every tile placed onto the starting straight, in order. This is the whole
    /// track as data — the same list that would be enough for any peer to rebuild it — and it is
    /// what a late joiner is sent. The starting straight is not in here; that is laid by
    /// <see cref="BuildStartingTrack"/> on every peer alike.
    /// </summary>
    private readonly List<int> _placed = new();

    /// <summary>
    /// Fired on every peer once a tile has been played. <paramref name="catalogIndex"/> is the
    /// exact entry it came from, which the hazard alone cannot say — the catalog holds pairs that
    /// share a hazard and differ only in which way they turn or how far they climb, and a callout
    /// that said "Curve" without saying which way would be no callout at all.
    /// </summary>
    [Signal] public delegate void TilePlacedEventHandler(int trackIndex, int hazard, int catalogIndex);

    /// <summary>Fired when the head of the track moves, so the builder can re-aim.</summary>
    [Signal] public delegate void TrackHeadChangedEventHandler();

    /// <summary>Fired on every peer once a furniture hazard has gone into a slot.</summary>
    [Signal] public delegate void HazardPlacedEventHandler(int tileIndex, int slotIndex, int kind);

    /// <summary>Fired on every peer once a hazard has been lifted back out of its slot.</summary>
    [Signal] public delegate void HazardRemovedEventHandler(int tileIndex, int slotIndex);

    /// <summary>
    /// Fired on the server the moment a racer crosses the bar at the end of the track. Only ever
    /// once — the track stops looking after the first car across.
    /// </summary>
    [Signal] public delegate void RaceFinishedEventHandler(int peerId);

    /// <summary>True only in real networked play; solo skips the RPC round trip entirely.</summary>
    private static bool Networked => NetworkManager.Instance.IsNetworked;

    /// <summary>The chequered bar at the far end of the track. Null until <c>_Ready</c>.</summary>
    private FinishLine? _finish;

    /// <summary>
    /// Highest track index condemned so far, or -1 for none. Tiles are condemned strictly in
    /// order, so one number is the whole record of what has already been told to go.
    /// </summary>
    private int _condemnedThrough = -1;

    /// <summary>
    /// Who has already crossed the line, so each racer's crossing fires exactly once. A set
    /// rather than the old single flag: the first crossing still decides the match, but cars
    /// arriving during the victory linger complete the race too, and the results board lists
    /// them in the order this sweep saw them.
    /// </summary>
    private readonly HashSet<int> _crossedPeers = new();

    /// <summary>Whether the track has been declared finished regardless of the tile count.</summary>
    private bool _locked;

    /// <summary>Whether the Track Master has spent every tile the race was given.</summary>
    public bool AtTileLimit => _locked || (TileLimit > 0 && _placed.Count >= TileLimit);

    /// <summary>
    /// Declare the track finished as it stands. Sentry mode calls this on every peer when the
    /// race phase opens: the builder may have stopped short of the tile budget — the Done button
    /// and the build clock both end a build early — and from that moment the bar at the head is
    /// the real finish and no further tile may land. Riding on <see cref="AtTileLimit"/> means
    /// the server-side placement check refuses late tiles with no extra rule anywhere.
    /// </summary>
    public void LockTrack()
    {
        _locked = true;
        UpdateFinishLine();
    }

    /// <summary>Tiles the Track Master has left to lay, or -1 if the race has no limit.</summary>
    public int TilesRemaining => TileLimit > 0 ? Mathf.Max(0, TileLimit - _placed.Count) : -1;

    /// <summary>
    /// Where the chequered bar sits: the near face of the head cell, which is the far edge of the
    /// furthest tile. It climbs with the track, so a finish at the top of a ramp is up there too.
    /// </summary>
    public Vector3 FinishWorldPosition
        => Grid.HeadAnchor.Position;

    /// <summary>
    /// How far past the bar still counts as having crossed it, in metres. Generous, because the
    /// check runs on the physics step and a car on a drift chain covers a couple of metres between
    /// two of them — and because whatever is past the bar is thin air, so there is nothing else
    /// out there to mistake for a finisher.
    /// </summary>
    private const float FinishDepth = TileCatalog.TileSize * 2.0f;

    public override void _Ready()
    {
        AddToGroup(GroupName);
        BuildStartingTrack();

        // Built after the starting straight so it has a head to sit on. Only the race has a finish
        // to reach; the lobby's track just stops.
        if (RaceRules)
        {
            _finish = new FinishLine { Name = "FinishLine" };
            AddChild(_finish);
            UpdateFinishLine();
        }

        // Nothing to watch for on a track nobody can win. See CheckForFinishers.
        SetPhysicsProcess(RaceRules);
    }

    private void BuildStartingTrack()
    {
        Grid.BuildStartingStraight(StartAnchor, StartingStraightLength);
        _condemnedThrough = -1;

        // No drop: the racers are already sitting on this straight, so it has to be under them
        // from the first frame rather than descending onto their roofs.
        foreach (PlacedTile tile in Grid.Tiles)
            SpawnTileNode(tile, drop: false);

        GD.Print($"[Track] Start line at {StartPosition} facing {StartDirection.DisplayName()}; "
                 + $"{StartingStraightLength} starting tile(s), head now {Grid.HeadAnchor.Position}.");

        UpdateLowestTile();
        EmitSignal(SignalName.TrackHeadChanged);
    }

    /// <summary>
    /// Re-derive the floor from the tiles still standing. A full scan on every change rather
    /// than an incremental min, because removal — undo, or the tail crumbling away — can take
    /// the lowest tile with it, and un-minimising needs the scan anyway. The track tops out
    /// around a hundred tiles; this is nothing.
    /// </summary>
    private void UpdateLowestTile()
    {
        float lowest = StartAnchor.Position.Y;

        for (int i = Grid.OldestLiveIndex; i < Grid.Count; i++)
        {
            PlacedTile tile = Grid.Tiles[i];
            lowest = Mathf.Min(lowest, Mathf.Min(tile.EntryAnchor.Position.Y,
                                                 tile.ExitAnchor.Position.Y));
        }

        LowestTileY = lowest;
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

        // The race is only so long. Checked here as well as on the server, because solo play never
        // reaches the server's copy of the check — it applies placements directly.
        if (AtTileLimit)
            return;

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

        if (AtTileLimit)
        {
            GD.PushWarning($"[Track] Peer {senderId} played past the end of a {TileLimit}-tile race.");
            return;
        }

        if (!Grid.CanPlace(definition.ToTileData(), out string reason))
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

        // The track grew, so the bar at the end of it moved and the back of it got that much
        // older. Both come off the placement rather than off a clock, which is what keeps every
        // peer's copy of them in step without a word being said about either.
        UpdateFinishLine();
        CondemnStaleTiles();
        UpdateLowestTile();

        EmitSignal(SignalName.TilePlaced, tile.Index, (int)tile.Data.Hazard, catalogIndex);
        EmitSignal(SignalName.TrackHeadChanged);
    }

    // ---- The far end: the finish line, and the race running out of tiles ----

    /// <summary>Put the bar back on the head, and say whether it is the real finish yet.</summary>
    private void UpdateFinishLine()
    {
        if (_finish == null)
            return;

        _finish.PlaceAt(FinishWorldPosition, Grid.HeadAnchor.Yaw);
        _finish.SetFinal(AtTileLimit);
    }

    /// <summary>
    /// How often the finish line is swept for arrivals, in seconds. It does not want to be every
    /// frame: asking the tree for a group allocates, and the check does not need the resolution.
    /// The fastest thing in the game covers ten metres between two sweeps at this rate, against a
    /// <see cref="FinishDepth"/> of eighty — so a car cannot pass through the window unseen, and
    /// a tenth of a second of slop over who was first is well inside a photo finish.
    /// </summary>
    private const float FinishSweepInterval = 0.1f;

    private float _finishSweepCountdown;

    /// <summary>
    /// Watch for the first car across the bar.
    ///
    /// Worked out from where the cars are rather than from a collision body, so that nothing about
    /// winning depends on a car's suspension being the right way up when it gets there — a racer
    /// who arrives sideways off a launch pad has still arrived.
    ///
    /// Server only, and only once. Every peer draws the same bar in the same place, but two
    /// machines watching for the same crossing is two machines with an opinion about who won.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!RaceRules)
            return;

        if (Networked && !Multiplayer.IsServer())
            return;

        _finishSweepCountdown -= (float)delta;
        if (_finishSweepCountdown > 0.0f)
            return;

        _finishSweepCountdown = FinishSweepInterval;

        Vector3 line = FinishWorldPosition;
        Vector3 forward = Grid.HeadAnchor.Forward;
        Vector3 across = Grid.HeadAnchor.Right;

        foreach (Node node in GetTree().GetNodesInGroup(RacerController.GroupName))
        {
            // A car with no owner is the one the board builds ahead of in solo play. It cannot
            // win a race it is not in. A car that already crossed cannot cross again.
            if (node is not RacerController racer || racer.OwnerPeerId <= 0
                || _crossedPeers.Contains(racer.OwnerPeerId))
                continue;

            Vector3 offset = racer.GlobalPosition - line;

            // Past the bar, still between its posts, and somewhere near its height. The last two
            // are what stop a car that fell off the side of a track which later doubled back on
            // itself from being handed the race from half a mile away.
            if (offset.Dot(forward) is < 0.0f or > FinishDepth)
                continue;
            if (Mathf.Abs(offset.Dot(across)) > TileCatalog.TileSize * 0.75f)
                continue;
            if (Mathf.Abs(offset.Y) > TileCatalog.HeightStep * 1.5f)
                continue;

            _crossedPeers.Add(racer.OwnerPeerId);
            GD.Print($"[Track] Peer {racer.OwnerPeerId} crossed the line at tile {Grid.Count} " +
                     $"(finisher #{_crossedPeers.Count}).");
            EmitSignal(SignalName.RaceFinished, racer.OwnerPeerId);
        }
    }

    // ---- The near end: the track crumbling away behind the racers ----

    /// <summary>
    /// Tell every tile that is now too far behind the head that its time is up. They blink for
    /// <see cref="TileExpirySeconds"/> and then call <see cref="OnTileExpired"/> to take
    /// themselves off the track.
    ///
    /// Driven by placement, so the tail advances one tile for every tile the Track Master lays and
    /// every peer condemns the same tile at the same moment. The countdown that follows is a local
    /// clock and will drift between machines by a frame or two, which is a tile-length behind the
    /// last car and nowhere near anything anybody is driving on.
    /// </summary>
    private void CondemnStaleTiles()
    {
        if (!RaceRules || TrackTrailLength <= 0)
            return;

        int oldestKept = Grid.Count - 1 - TrackTrailLength;

        for (int index = _condemnedThrough + 1; index <= oldestKept; index++)
        {
            if (GetNodeOrNull(TileNodeName(index)) is not TrackTile tile)
                continue;

            tile.TileExpired += OnTileExpired;
            tile.Condemn(TileExpirySeconds);
        }

        _condemnedThrough = Mathf.Max(_condemnedThrough, oldestKept);
    }

    /// <summary>
    /// A condemned tile's countdown has run out: take it out of the grid so it stops being road,
    /// and take its node out of the scene.
    ///
    /// <see cref="TrackGrid.RetireThrough"/> rather than "retire the oldest", so the grid and the
    /// nodes cannot get out of step if two tiles happen to expire in the same frame.
    ///
    /// Queued rather than detached first, which is the opposite of what <see cref="ApplyRemoval"/>
    /// does. That one detaches because undo hands the tile's index straight back to the next
    /// placement and the name has to be free again this instant; nothing ever reuses the index of
    /// a tile that crumbled, because the track only ever counts upward. Which leaves QueueFree as
    /// the safer of the two here: this is called from inside the tile's own per-frame step, and
    /// taking a node out of the tree from within its own callback is a thing to avoid rather than
    /// a thing to reason about.
    /// </summary>
    private void OnTileExpired(int trackIndex)
    {
        Grid.RetireThrough(trackIndex);
        GetNodeOrNull(TileNodeName(trackIndex))?.QueueFree();

        // The hazard nodes died with the tile node; this is only the ledger catching up.
        _hazards.RemoveAll(h => h.TileIndex <= trackIndex);

        // The floor may just have crumbled: a race that dived early and climbed late has its
        // lowest road at the back, and the kill plane must rise as that road stops existing.
        UpdateLowestTile();
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

        // Undo and crumbling never actually meet — one is a lobby thing and the other a match
        // thing — but the record of what has been condemned must not be left pointing past the
        // end of a track that just got shorter.
        _condemnedThrough = Mathf.Min(_condemnedThrough, Grid.Count - 1);

        // Any hazards on the undone tile went down with its node; the ledger follows.
        _hazards.RemoveAll(h => h.TileIndex == tile.Index);

        UpdateFinishLine();
        UpdateLowestTile();
        EmitSignal(SignalName.TrackHeadChanged);
    }

    // ---- Furniture hazards ----
    //
    // Same shape as tile placement, because it is the same problem: the builder's client asks,
    // the server checks the asker and the legality, then broadcasts, and every peer applies the
    // broadcast identically. A hazard's address is (tile, slot, kind) — three ints — and its
    // node lives under the tile's own node, so a hazard on a tile that crumbles or is undone
    // leaves with it without a line of cleanup.

    /// <summary>Every hazard standing on the track, in placement order — what a late joiner is
    /// sent, alongside the tile list.</summary>
    private readonly List<PlacedHazard> _hazards = new();

    public IReadOnlyList<PlacedHazard> Hazards => _hazards;

    /// <summary>
    /// Whether hazards are hidden on this machine. Set on the racers' peers for the rig phase and
    /// cleared when the race starts.
    ///
    /// <b>Concealment is local rendering, not a secret.</b> Every peer still receives every
    /// placement and builds the identical node — the rig has to be the same object on every
    /// machine or the traps would fire into different worlds — this only decides whether it is
    /// drawn. A racer who never sees the rig go down meets each trap for the first time at speed,
    /// which is the whole reason the phase is worth hiding; a racer whose game disagreed with the
    /// server about where the traps <i>were</i> would just be a bug.
    /// </summary>
    public bool HazardsConcealed { get; private set; }

    /// <summary>Hide or show every hazard on the track, and every one placed after this. Called
    /// by the match scene as the phases turn.</summary>
    public void SetHazardsConcealed(bool concealed)
    {
        HazardsConcealed = concealed;

        foreach (PlacedHazard placed in _hazards)
        {
            if (HazardAt(placed.TileIndex, placed.SlotIndex) is { } hazard)
                hazard.Visible = !concealed;
        }
    }

    /// <summary>The hazard slots a placed tile offers, or none — generated tiles have no scene
    /// and so no slots; only authored pieces are slotted.</summary>
    public IReadOnlyList<Tool.PieceSlot> SlotsOf(int tileIndex)
    {
        if (tileIndex < Grid.OldestLiveIndex || tileIndex >= Grid.Count)
            return System.Array.Empty<Tool.PieceSlot>();

        PlacedTile tile = Grid.Tiles[tileIndex];
        if (tile.Data.ScenePath.Length == 0)
            return System.Array.Empty<Tool.PieceSlot>();

        return Tool.PieceCatalog.AtPath(tile.Data.ScenePath)?.Slots
               ?? (IReadOnlyList<Tool.PieceSlot>)System.Array.Empty<Tool.PieceSlot>();
    }

    public bool IsSlotFree(int tileIndex, int slotIndex)
    {
        foreach (PlacedHazard hazard in _hazards)
        {
            if (hazard.TileIndex == tileIndex && hazard.SlotIndex == slotIndex)
                return false;
        }

        return true;
    }

    /// <summary>Where a slot sits in the world: the tile's entry frame carrying the slot's
    /// authored transform — the same composition the footprint uses for the route.</summary>
    public Transform3D? SlotWorldTransform(int tileIndex, int slotIndex)
    {
        IReadOnlyList<Tool.PieceSlot> slots = SlotsOf(tileIndex);
        if (slotIndex < 0 || slotIndex >= slots.Count)
            return null;

        return Grid.Tiles[tileIndex].EntryFrame * slots[slotIndex].Local;
    }

    /// <summary>One rulebook for every door a hazard can come through. Public because the
    /// sentry's manager pre-checks it before spending points on the slot.</summary>
    public bool CanPlaceHazard(int tileIndex, int slotIndex, HazardKind kind, out string reason)
    {
        IReadOnlyList<Tool.PieceSlot> slots = SlotsOf(tileIndex);
        if (slots.Count == 0)
        {
            reason = "that tile takes no hazards";
            return false;
        }

        if (slotIndex < 0 || slotIndex >= slots.Count)
        {
            reason = $"no slot {slotIndex} on tile {tileIndex}";
            return false;
        }

        if (slots[slotIndex].Kind != kind.SlotKind())
        {
            reason = $"{kind.DisplayName()} does not fit a {slots[slotIndex].Kind} slot";
            return false;
        }

        if (!IsSlotFree(tileIndex, slotIndex))
        {
            reason = "that slot is taken";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>Client-side intent: ask the server to put a hazard into a slot. The builder's
    /// free door, open during the build only — during a Sentry race the paid door below is the
    /// only way in.</summary>
    public void RequestPlaceHazard(int tileIndex, int slotIndex, HazardKind kind)
    {
        if (!Networked)
        {
            if (CanPlaceHazard(tileIndex, slotIndex, kind, out string why))
                ApplyHazardPlacement(tileIndex, slotIndex, (int)kind);
            else
                GD.PushWarning($"[Track] Hazard refused: {why}.");
            return;
        }

        if (Multiplayer.IsServer())
        {
            AuthorizeAndBroadcastHazard(tileIndex, slotIndex, kind, Multiplayer.GetUniqueId());
            return;
        }

        RpcId(1, MethodName.ServerPlaceHazard, tileIndex, slotIndex, (int)kind);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerPlaceHazard(int tileIndex, int slotIndex, int kind)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        AuthorizeAndBroadcastHazard(tileIndex, slotIndex, (HazardKind)kind,
                                    Multiplayer.GetRemoteSenderId());
    }

    private void AuthorizeAndBroadcastHazard(int tileIndex, int slotIndex, HazardKind kind, int senderId)
    {
        if (!MayBuild(senderId))
        {
            GD.PushWarning($"[Track] Peer {senderId} tried to place a hazard but is not the builder.");
            return;
        }

        // Once a Sentry race is on, free placement closes: the sentry pays points for the same
        // slots through ServerCommitHazard, and a free door beside a paid one is no price at all.
        if (GameManager.Instance.Phase == MatchPhase.Racing)
        {
            GD.PushWarning($"[Track] Peer {senderId} tried to place a free hazard mid-race.");
            return;
        }

        if (!CanPlaceHazard(tileIndex, slotIndex, kind, out string reason))
        {
            GD.PushWarning($"[Track] Rejected {kind.DisplayName()} from peer {senderId}: {reason}.");
            return;
        }

        Rpc(MethodName.ConfirmHazardPlaced, tileIndex, slotIndex, (int)kind);
        ApplyHazardPlacement(tileIndex, slotIndex, (int)kind);
    }

    /// <summary>
    /// The sentry's door. Server only, called by <see cref="Sentry.SentryManager"/> <i>after</i>
    /// the role check — points are only spent when this says the slot was really there to buy.
    /// Returns false with the reason so the manager can refuse before charging.
    /// </summary>
    public bool ServerCommitHazard(int tileIndex, int slotIndex, HazardKind kind, out string reason)
    {
        if (!CanPlaceHazard(tileIndex, slotIndex, kind, out reason))
            return false;

        if (Networked)
            Rpc(MethodName.ConfirmHazardPlaced, tileIndex, slotIndex, (int)kind);
        ApplyHazardPlacement(tileIndex, slotIndex, (int)kind);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ConfirmHazardPlaced(int tileIndex, int slotIndex, int kind)
        => ApplyHazardPlacement(tileIndex, slotIndex, kind);

    /// <summary>Put the hazard in the world, identically on every peer: record it, and stand
    /// its node up under the tile it belongs to, wearing the slot's authored transform.</summary>
    private void ApplyHazardPlacement(int tileIndex, int slotIndex, int kind)
    {
        Transform3D? local = null;
        IReadOnlyList<Tool.PieceSlot> slots = SlotsOf(tileIndex);
        if (slotIndex >= 0 && slotIndex < slots.Count)
            local = slots[slotIndex].Local;

        if (local == null || GetNodeOrNull(TileNodeName(tileIndex)) is not TrackTile tile)
        {
            // A broadcast can land after the tile crumbled on this peer. A hazard on road that
            // is gone is no hazard; skipping is the whole handling.
            return;
        }

        _hazards.Add(new PlacedHazard(tileIndex, slotIndex, (HazardKind)kind));

        TrackHazard node = TrackHazard.Create((HazardKind)kind);
        node.Name = HazardNodeName(slotIndex);

        // Hidden before it is ever drawn, not hidden a frame later: on a racer's machine during
        // the rig this node must never appear, and a single frame of a red plate popping into
        // existence on the road is exactly the tell the phase is meant to withhold.
        node.Visible = !HazardsConcealed;

        // Placed before it enters the tree, not after.
        //
        // A hazard carrying a physics body that syncs to physics — the spring trap's plate — has
        // that body registered with the physics server the moment it is added, at whatever global
        // transform it has then. A parent moved afterwards does not drag it along: the server owns
        // the body's position from that point, so the plate stayed sitting at the tile's origin
        // while the rest of the hazard moved off to its slot. Setting the transform first means
        // the body is registered where it belongs and there is nothing to chase.
        node.Transform = local.Value;
        tile.AddChild(node);

        EmitSignal(SignalName.HazardPlaced, tileIndex, slotIndex, kind);
    }

    /// <summary>Client-side intent: lift a hazard back out of its slot. Build-time only, the
    /// mirror of <see cref="RequestPlaceHazard"/>.</summary>
    public void RequestRemoveHazard(int tileIndex, int slotIndex)
    {
        if (!Networked)
        {
            ApplyHazardRemoval(tileIndex, slotIndex);
            return;
        }

        if (Multiplayer.IsServer())
        {
            AuthorizeAndBroadcastHazardRemoval(tileIndex, slotIndex, Multiplayer.GetUniqueId());
            return;
        }

        RpcId(1, MethodName.ServerRemoveHazard, tileIndex, slotIndex);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRemoveHazard(int tileIndex, int slotIndex)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        AuthorizeAndBroadcastHazardRemoval(tileIndex, slotIndex, Multiplayer.GetRemoteSenderId());
    }

    private void AuthorizeAndBroadcastHazardRemoval(int tileIndex, int slotIndex, int senderId)
    {
        if (!MayBuild(senderId) || GameManager.Instance.Phase == MatchPhase.Racing)
            return;

        if (IsSlotFree(tileIndex, slotIndex))
            return;

        Rpc(MethodName.ConfirmHazardRemoved, tileIndex, slotIndex);
        ApplyHazardRemoval(tileIndex, slotIndex);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ConfirmHazardRemoved(int tileIndex, int slotIndex)
        => ApplyHazardRemoval(tileIndex, slotIndex);

    private void ApplyHazardRemoval(int tileIndex, int slotIndex)
    {
        int found = _hazards.FindIndex(h => h.TileIndex == tileIndex && h.SlotIndex == slotIndex);
        if (found < 0)
            return;

        _hazards.RemoveAt(found);

        if (GetNodeOrNull(TileNodeName(tileIndex)) is TrackTile tile
            && tile.GetNodeOrNull(HazardNodeName(slotIndex)) is Node node)
        {
            tile.RemoveChild(node);
            node.QueueFree();
        }

        EmitSignal(SignalName.HazardRemoved, tileIndex, slotIndex);
    }

    /// <summary>
    /// Client-side intent: fire a rigged device that is already standing on the track.
    ///
    /// The third door into the hazard system, and the one the sentry lives in once the race is
    /// running. It is deliberately <i>not</i> gated on the build phase the way placement and
    /// removal are — a trap that could only be fired while the track was still being built would
    /// be a trap nobody ever drove into. Nothing is spent here either: the card was the price,
    /// and the device's own cooldown is what stops the button being mashed.
    /// </summary>
    public void RequestDetonateHazard(int tileIndex, int slotIndex)
    {
        if (!Networked)
        {
            ApplyHazardDetonation(tileIndex, slotIndex);
            return;
        }

        if (Multiplayer.IsServer())
        {
            AuthorizeAndBroadcastHazardDetonation(tileIndex, slotIndex, Multiplayer.GetUniqueId());
            return;
        }

        RpcId(1, MethodName.ServerDetonateHazard, tileIndex, slotIndex);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerDetonateHazard(int tileIndex, int slotIndex)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        AuthorizeAndBroadcastHazardDetonation(tileIndex, slotIndex, Multiplayer.GetRemoteSenderId());
    }

    private void AuthorizeAndBroadcastHazardDetonation(int tileIndex, int slotIndex, int senderId)
    {
        if (!MayBuild(senderId))
            return;

        // Not while the rig is being laid. A device fired during its own setup phase would be
        // cooling down — or still in the air — when the flag drops, and the one thing the rig
        // owes the race is that every trap on the board is a loaded one. Outside a Sentry match
        // there is no phase at all, and the lobby stays free to set one off to see what it does.
        if (GameManager.Instance.Phase is MatchPhase.Building or MatchPhase.Rigging)
            return;

        // Refused rather than queued when the device is mid-throw or cooling down, so a peer
        // whose plate is already in the air never receives a press that would restart the arc
        // out from under the cars it has just thrown.
        if (HazardAt(tileIndex, slotIndex) is not { CanDetonate: true, IsReady: true })
            return;

        Rpc(MethodName.ConfirmHazardDetonated, tileIndex, slotIndex);
        ApplyHazardDetonation(tileIndex, slotIndex);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ConfirmHazardDetonated(int tileIndex, int slotIndex)
        => ApplyHazardDetonation(tileIndex, slotIndex);

    /// <summary>Fire the device on this peer. Every peer runs the same arc from this one call,
    /// which is what keeps the gap under a hanging plate in the same place on every screen.</summary>
    private void ApplyHazardDetonation(int tileIndex, int slotIndex)
        => HazardAt(tileIndex, slotIndex)?.Detonate();

    /// <summary>The hazard node standing in a slot, or null. Public because the board needs to
    /// ask a hazard whether it is rigged and ready before offering it as a target.</summary>
    public TrackHazard? HazardAt(int tileIndex, int slotIndex)
        => GetNodeOrNull(TileNodeName(tileIndex)) is TrackTile tile
           ? tile.GetNodeOrNull<TrackHazard>(HazardNodeName(slotIndex))
           : null;

    private static string HazardNodeName(int slotIndex) => $"Hazard{slotIndex}";

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

        // Hazards travel as flattened (tile, slot, kind) triples — the same three ints the
        // placement broadcast speaks, in the same order they were placed.
        var hazards = new int[_hazards.Count * 3];
        for (int i = 0; i < _hazards.Count; i++)
        {
            hazards[i * 3] = _hazards[i].TileIndex;
            hazards[i * 3 + 1] = _hazards[i].SlotIndex;
            hazards[i * 3 + 2] = (int)_hazards[i].Kind;
        }

        RpcId(peerId, MethodName.SyncTrack, _placed.ToArray(), hazards);
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
    private void SyncTrack(int[] catalogIndices, int[] hazardTriples)
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
        _hazards.Clear();
        BuildStartingTrack();

        foreach (int catalogIndex in catalogIndices)
            ApplyPlacement(catalogIndex);

        // The hazards replay after every tile is standing — each entry was legal when the
        // server recorded it, so this is reflection, not re-judging.
        for (int i = 0; i + 2 < hazardTriples.Length; i += 3)
            ApplyHazardPlacement(hazardTriples[i], hazardTriples[i + 1], hazardTriples[i + 2]);

        // ApplyPlacement moves the bar each time round, but a peer that arrived before anything
        // had been built replays nothing at all and still needs it back on the start tile.
        UpdateFinishLine();
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
        node.Initialize(tile.Data, tile.Index, tile.EntryAnchor,
                        fallHeight: drop ? TileFallHeight : 0.0f,
                        fallSpeed: TileFallSpeed,
                        entryFrame: tile.EntryFrame);
    }

    /// <summary>
    /// World position at the centre of the next open cell, at the elevation the next tile will
    /// start from — so the board camera and the head marker climb with the track.
    /// </summary>
    public Vector3 HeadWorldPosition
        => Grid.HeadAnchor.Position + Grid.HeadAnchor.Forward * (TrackTile.Size * 0.5f);
}
