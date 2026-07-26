using Godot;
using System.Collections.Generic;

namespace MasterTrack.Networking;

public enum PlayerRole
{
    Unassigned,
    TrackMaster,
    Racer,
}

public enum GameState
{
    Lobby,
    InRound,
    RoundOver,
    MatchOver,
}

/// <summary>
/// Autoload. The authoritative game brain. Assigns roles (one Track Master, the rest
/// Racers), tracks the current round, and is the single place the server drives match
/// flow. Clients receive role/state updates via RPC — they never decide these locally.
/// </summary>
public partial class GameManager : Node
{
    public static GameManager Instance { get; private set; } = null!;

    [Signal] public delegate void RoleAssignedEventHandler(int peerId, int role);
    [Signal] public delegate void GameStateChangedEventHandler(int state);
    [Signal] public delegate void RoundStartedEventHandler(int roundNumber);

    /// <summary>
    /// Server only. Every peer in the session has reported its match scene loaded, so nodes
    /// spawned from here on will actually reach all of them.
    /// </summary>
    [Signal] public delegate void AllPeersReadyEventHandler();

    /// <summary>
    /// Server only. One peer has its current scene loaded. The lobby spawns per peer as they
    /// arrive; the match waits for <see cref="AllPeersReady"/> instead.
    /// </summary>
    [Signal] public delegate void PeerSceneReadyEventHandler(int peerId);

    /// <summary>Fired on every peer as the scene-ready count climbs, for a "waiting" message.</summary>
    [Signal] public delegate void SceneReadyProgressEventHandler(int ready, int total);

    /// <summary>Tiles dealt to the Track Master at the start of each round.</summary>
    public const int TilesPerRound = 5;

    /// <summary>Server-side truth. On clients this is only populated by RPC.</summary>
    public readonly Dictionary<int, PlayerRole> Roles = new();

    public GameState State { get; private set; } = GameState.Lobby;
    public int RoundNumber { get; private set; }

    /// <summary>Which peer is the Track Master (0 = none yet). Valid on all peers once set.</summary>
    public int TrackMasterPeerId { get; private set; }

    /// <summary>
    /// Role to use when the game is launched without a session. Roles are normally handed out
    /// by the server, but both sides of an asymmetric game need to be reachable on their own
    /// for testing, so the main menu sets this before loading straight into the world.
    /// </summary>
    public PlayerRole SoloRole { get; set; } = PlayerRole.Racer;

    /// <summary>
    /// Server only. Peers that have reported their match scene loaded. On clients this stays
    /// empty — they learn about progress through <see cref="SceneReadyProgress"/> instead.
    /// </summary>
    private readonly HashSet<int> _sceneReadyPeers = new();

    /// <summary>Server only. Guards <see cref="AllPeersReady"/> against firing twice.</summary>
    private bool _allPeersReady;

    /// <summary>Everyone in the session: every connected peer plus ourselves.</summary>
    private int SessionPeerCount => Multiplayer.GetPeers().Length + 1;

    public override void _Ready()
    {
        Instance = this;
        ApplyCommandLineRole();

        // A peer that drops while we are waiting on it must not strand everyone else in the
        // lobby forever — it also lowers the bar, which can complete the handshake outright.
        NetworkManager.Instance.PlayerDisconnected += OnPeerDisconnected;
    }

    private void OnPeerDisconnected(int peerId)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        _sceneReadyPeers.Remove(peerId);
        // Only re-check: never un-fire. Once cars have spawned, a leaver is just a leaver.
        if (!_allPeersReady)
            EvaluateSceneReady();
    }

    /// <summary>
    /// Let a solo launch pick its side from the command line, so either half of the game can
    /// be opened straight from an editor run or a script:
    /// <code>godot res://scenes/Game.tscn -- --role=trackmaster</code>
    /// Ignored entirely once a real session assigns roles.
    /// </summary>
    private void ApplyCommandLineRole()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--role=", System.StringComparison.OrdinalIgnoreCase))
                continue;

            string value = arg["--role=".Length..];
            if (value.Equals("trackmaster", System.StringComparison.OrdinalIgnoreCase))
                SoloRole = PlayerRole.TrackMaster;
            else if (value.Equals("racer", System.StringComparison.OrdinalIgnoreCase))
                SoloRole = PlayerRole.Racer;
            else
                GD.PushWarning($"[GameManager] Unknown --role value '{value}'. Use trackmaster or racer.");

            GD.Print($"[GameManager] Solo role set from command line: {SoloRole}.");
        }
    }

    /// <summary>
    /// Server only. Call once everyone has joined and the host presses "Start".
    /// One peer is drawn at random to be the Track Master; everyone else races.
    /// </summary>
    public void StartMatch()
    {
        if (!NetworkManager.Instance.IsHost)
        {
            GD.PushWarning("[GameManager] StartMatch called on a non-host; ignored.");
            return;
        }

        Roles.Clear();
        _sceneReadyPeers.Clear();
        _allPeersReady = false;

        var peers = new List<int> { Multiplayer.GetUniqueId() };
        peers.AddRange(Multiplayer.GetPeers());

        // Drawn here and nowhere else. A client rolling for itself would get a different
        // answer from every other machine, and no two peers would agree who was building —
        // which the tile code would then reject as an impostor placing tiles.
        //
        // The host is in the draw like everybody else: hosting is how the session got started,
        // not a claim on the role.
        int trackMaster = peers[GD.RandRange(0, peers.Count - 1)];
        GD.Print($"[GameManager] Track Master drawn: peer {trackMaster} of {peers.Count}.");

        foreach (int peerId in peers)
            AssignRole(peerId, peerId == trackMaster ? PlayerRole.TrackMaster : PlayerRole.Racer);

        RoundNumber = 0;
        StartNextRound();
    }

    /// <summary>Server only. Advance to the next round and deal a fresh hand.</summary>
    public void StartNextRound()
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        RoundNumber++;
        SetState(GameState.InRound);
        Rpc(MethodName.NotifyRoundStarted, RoundNumber);
        // Tile-dealing for the Track Master hooks in here later.
    }

    // ---- Scene-ready handshake ----
    //
    // A MultiplayerSpawner only sends spawn packets to the peers connected *at spawn time*, and
    // never retroactively spawns for a peer whose scene turned up late. Clients only begin
    // loading the match scene when the state-change RPC reaches them, so spawning a frame after
    // the host's own _Ready races every client on the wire: a straggler that misses the window
    // ends up permanently carless, which reads as a physics bug rather than a missed packet.
    //
    // So the server waits for every peer to check in — deliberately with no timeout. A peer that
    // genuinely hangs stays visible in the count, and the host can restart; a timeout would
    // instead quietly produce the silent failure this exists to prevent.

    /// <summary>
    /// Call from the match scene's <c>_Ready</c> on every peer. Solo play has nobody to tell.
    /// </summary>
    public void ReportSceneReady()
    {
        if (!NetworkManager.Instance.IsNetworked)
            return;

        if (NetworkManager.Instance.IsHost)
            ServerMarkSceneReady(Multiplayer.GetUniqueId());
        else
            RpcId(1, MethodName.ServerNotifySceneReady);
    }

    /// <summary>Client -> server: my copy of the match scene is loaded.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerNotifySceneReady() => ServerMarkSceneReady(Multiplayer.GetRemoteSenderId());

    private void ServerMarkSceneReady(int peerId)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        if (!_sceneReadyPeers.Add(peerId))
            return;

        EmitSignal(SignalName.PeerSceneReady, peerId);
        EvaluateSceneReady();
    }

    /// <summary>Server only. Publish the count, and release the spawn once it is everyone.</summary>
    private void EvaluateSceneReady()
    {
        int ready = _sceneReadyPeers.Count;
        int total = SessionPeerCount;

        Rpc(MethodName.NotifySceneReadyProgress, ready, total);
        NotifySceneReadyProgress(ready, total);

        if (ready < total || _allPeersReady)
            return;

        _allPeersReady = true;
        GD.Print($"[GameManager] All {total} peers reported their scene ready.");
        EmitSignal(SignalName.AllPeersReady);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifySceneReadyProgress(int ready, int total) =>
        EmitSignal(SignalName.SceneReadyProgress, ready, total);

    private void AssignRole(int peerId, PlayerRole role)
    {
        Roles[peerId] = role;
        if (role == PlayerRole.TrackMaster)
            TrackMasterPeerId = peerId;

        // Tell everyone, including the host itself, about this assignment.
        Rpc(MethodName.NotifyRoleAssigned, peerId, (int)role);
        NotifyRoleAssigned(peerId, (int)role);
    }

    private void SetState(GameState state)
    {
        State = state;
        Rpc(MethodName.NotifyGameStateChanged, (int)state);
        NotifyGameStateChanged((int)state);
    }

    // ---- RPCs: server -> all clients. CallLocal is false so the server invokes the
    //      local copy directly (above) and avoids double-firing signals. ----

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyRoleAssigned(int peerId, int role)
    {
        Roles[peerId] = (PlayerRole)role;
        if ((PlayerRole)role == PlayerRole.TrackMaster)
            TrackMasterPeerId = peerId;
        EmitSignal(SignalName.RoleAssigned, peerId, role);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyGameStateChanged(int state)
    {
        State = (GameState)state;
        EmitSignal(SignalName.GameStateChanged, state);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyRoundStarted(int roundNumber)
    {
        RoundNumber = roundNumber;
        EmitSignal(SignalName.RoundStarted, roundNumber);
    }

    /// <summary>Local role of this peer, if assigned yet.</summary>
    public PlayerRole LocalRole =>
        Roles.TryGetValue(Multiplayer.GetUniqueId(), out PlayerRole r) ? r : PlayerRole.Unassigned;
}
