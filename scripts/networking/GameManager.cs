using Godot;
using MasterTrack.Racer;
using System.Collections.Generic;

namespace MasterTrack.Networking;

/// <summary>
/// What one player's car looks like: which of the three models, and which rainbow colour. Dealt
/// once at join time and kept for the whole session, so your car is the same in the lobby and in
/// the match — the colour is how people tell each other apart.
/// </summary>
public readonly record struct RacerAppearance(int VariantIndex, int ColourIndex)
{
    /// <summary>Colour index meaning "nobody dealt this car a colour" — solo play, and the
    /// unowned car the board builds ahead of.</summary>
    public const int NoColour = -1;

    public static RacerAppearance Unassigned =>
        new(CarVariants.DefaultVariantIndex, NoColour);

    public Color Paint =>
        ColourIndex == NoColour ? CarVariants.DefaultPaint : CarVariants.ColourAt(ColourIndex);

    public string ColourName =>
        ColourIndex == NoColour ? "Stock" : CarVariants.ColourNameAt(ColourIndex);
}

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

    /// <summary>A peer's car model and colour are now known on this machine.</summary>
    [Signal] public delegate void AppearanceAssignedEventHandler(int peerId, int variant, int colour);

    /// <summary>A peer's name is now known on this machine.</summary>
    [Signal] public delegate void PlayerNameChangedEventHandler(int peerId, string name);

    /// <summary>The race length has been set or re-published. Known on every peer.</summary>
    [Signal] public delegate void RaceLengthChangedEventHandler(int tiles);

    /// <summary>Somebody crossed the line. Fired on every peer, with the winner's peer id.</summary>
    [Signal] public delegate void MatchWonEventHandler(int peerId);

    /// <summary>Tiles dealt to the Track Master at the start of each round.</summary>
    public const int TilesPerRound = 5;

    // ---- Race length ----
    //
    // How many tiles the Track Master is given to spend, and so how long the race is. The host
    // picks it in the lobby before the match, because without it a race has no end: the Track
    // Master's whole job is stopping the racers reaching the end of the track, and they are very
    // good at it. When the tiles run out the bar at the end of the track stops moving and becomes
    // a finish line somebody can actually get to.

    /// <summary>Lengths the lobby offers, in tiles.</summary>
    public static readonly int[] RaceLengthChoices = { 10, 20, 30, 50 };

    public const int DefaultRaceLength = 20;

    private const int MinRaceLength = 5;
    private const int MaxRaceLength = 99;

    /// <summary>
    /// How many tiles this match runs for. The host owns it and everyone is told; a client that
    /// set its own would disagree with the server about when the Track Master had run dry.
    /// </summary>
    public int RaceLength { get; private set; } = DefaultRaceLength;

    /// <summary>Who won the current match, or 0 if it is still being raced.</summary>
    public int WinnerPeerId { get; private set; }

    /// <summary>
    /// How long the winner's name stays up before everyone is taken back to the lobby. Long enough
    /// to see who it was and swear about it.
    /// </summary>
    private const float VictoryLingerSeconds = 7.0f;

    /// <summary>Server-side truth. On clients this is only populated by RPC.</summary>
    public readonly Dictionary<int, PlayerRole> Roles = new();

    /// <summary>
    /// What each peer's car looks like. Same deal as <see cref="Roles"/>: the server decides and
    /// every peer is told, so nobody's machine has its own opinion about what colour you are.
    /// </summary>
    public readonly Dictionary<int, RacerAppearance> Appearances = new();

    /// <summary>Server only. Rainbow colours nobody is wearing yet.</summary>
    private readonly List<int> _freeColours = new();

    /// <summary>What each peer calls themselves. Replicated to everyone, like the appearances.</summary>
    public readonly Dictionary<int, string> Names = new();

    /// <summary>
    /// The name this machine plays under, set from the main menu before hosting or joining.
    /// Sent to the server once connected; the server hands it to everyone else.
    /// </summary>
    public string LocalPlayerName { get; set; } = "";

    /// <summary>Longest name accepted, so nobody can push the lobby list off the screen.</summary>
    public const int MaxNameLength = 16;

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
        NetworkManager.Instance.PlayerConnected += OnPeerConnected;
        NetworkManager.Instance.ServerCreated += OnServerCreated;
    }

    private void OnPeerDisconnected(int peerId)
    {
        // Every peer forgets them, so nobody is left listing a player who has gone.
        Names.Remove(peerId);
        if (Appearances.Remove(peerId, out RacerAppearance gone) && NetworkManager.Instance.IsHost)
            _freeColours.Add(gone.ColourIndex);

        if (!NetworkManager.Instance.IsHost)
            return;

        _sceneReadyPeers.Remove(peerId);
        // Only re-check: never un-fire. Once cars have spawned, a leaver is just a leaver.
        if (!_allPeersReady)
            EvaluateSceneReady();
    }

    // ---- Appearance: which car, which colour ----
    //
    // Dealt on join rather than at match start, because everyone is already driving around the
    // lobby by then and a car that changed colour on the way into the match would undo the one
    // thing the colour is for. The builder holds a colour for the whole wait too — they are only
    // singled out when the host presses Start — which is why the palette has to cover the lobby
    // rather than just the racers.

    /// <summary>Server only. Fresh session: nobody has a colour yet, and we take the first.</summary>
    private void OnServerCreated()
    {
        Appearances.Clear();
        Names.Clear();

        _freeColours.Clear();
        for (int i = 0; i < CarVariants.Palette.Length; i++)
            _freeColours.Add(i);

        AssignAppearance(Multiplayer.GetUniqueId());
        PublishLocalName();
    }

    /// <summary>Server only. Deal to the newcomer, then catch them up on everyone already here.</summary>
    private void OnPeerConnected(int peerId)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        AssignAppearance(peerId);

        foreach (var kvp in Appearances)
        {
            if (kvp.Key != peerId)
                RpcId(peerId, MethodName.NotifyAppearanceAssigned, kvp.Key,
                      kvp.Value.VariantIndex, kvp.Value.ColourIndex);
        }

        foreach (var kvp in Names)
        {
            if (kvp.Key != peerId)
                RpcId(peerId, MethodName.NotifyPlayerName, kvp.Key, kvp.Value);
        }

        // The lobby shows the race length to everybody, not only to the host who set it, so a
        // newcomer has to be told what was decided before they arrived.
        RpcId(peerId, MethodName.NotifyRaceLength, RaceLength);
    }

    // ---- Race length ----

    /// <summary>
    /// Host only. Set how many tiles the next match runs for, and tell everyone. Called from the
    /// lobby; ignored anywhere else, because the length has to be settled before roles are dealt.
    /// </summary>
    public void SetRaceLength(int tiles)
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
        {
            GD.PushWarning("[GameManager] Only the host sets the race length; ignored.");
            return;
        }

        int clamped = Mathf.Clamp(tiles, MinRaceLength, MaxRaceLength);
        if (clamped == RaceLength)
            return;

        RaceLength = clamped;
        GD.Print($"[GameManager] Race length set to {RaceLength} tiles.");

        if (NetworkManager.Instance.IsNetworked)
            Rpc(MethodName.NotifyRaceLength, RaceLength);

        EmitSignal(SignalName.RaceLengthChanged, RaceLength);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyRaceLength(int tiles)
    {
        RaceLength = tiles;
        EmitSignal(SignalName.RaceLengthChanged, tiles);
    }

    // ---- Winning ----

    /// <summary>
    /// Server only. A racer reached the chequered bar at the end of the track. Tell everyone who,
    /// then take the whole session back to the lobby once they have had a moment to see it.
    ///
    /// The track works out that somebody crossed the line — it is the thing that knows where the
    /// line is — but only this decides that the match is therefore over, because the match is not
    /// the track's to end.
    /// </summary>
    public void DeclareWinner(int peerId)
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
            return;

        if (WinnerPeerId != 0)
            return;

        GD.Print($"[GameManager] Peer {peerId} wins.");

        // Solo stops at the announcement. There is no session to change the state of — SetState
        // broadcasts, and broadcasting without a peer is an error — and no lobby to be sent back
        // to either; the match scene's own Escape handler is the way out of a solo run.
        if (!NetworkManager.Instance.IsNetworked)
        {
            NotifyWinner(peerId);
            return;
        }

        Rpc(MethodName.NotifyWinner, peerId);
        NotifyWinner(peerId);

        SetState(GameState.MatchOver);

        GetTree().CreateTimer(VictoryLingerSeconds).Timeout += () =>
        {
            if (State == GameState.MatchOver)
                EndMatch();
        };
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyWinner(int peerId)
    {
        WinnerPeerId = peerId;
        EmitSignal(SignalName.MatchWon, peerId);
    }

    private void AssignAppearance(int peerId)
    {
        if (Appearances.ContainsKey(peerId))
            return;

        // Model with replacement, colour without: three models cannot cover seven people, but
        // the colours can, and the colour is the half that has to be unique.
        var appearance = new RacerAppearance(GD.RandRange(0, CarVariants.All.Count - 1), TakeColour());
        Appearances[peerId] = appearance;

        GD.Print($"[GameManager] Peer {peerId} gets {CarVariants.At(appearance.VariantIndex).Name} " +
                 $"in {appearance.ColourName}.");

        Rpc(MethodName.NotifyAppearanceAssigned, peerId, appearance.VariantIndex, appearance.ColourIndex);
        NotifyAppearanceAssigned(peerId, appearance.VariantIndex, appearance.ColourIndex);
    }

    private int TakeColour()
    {
        // NetworkManager.MaxPlayers is set so this cannot happen. Saying so anyway, because a
        // pool that runs dry silently hands two people the same identity.
        if (_freeColours.Count == 0)
        {
            GD.PushWarning("[GameManager] Rainbow palette exhausted; two players will share a colour.");
            return GD.RandRange(0, CarVariants.Palette.Length - 1);
        }

        int index = GD.RandRange(0, _freeColours.Count - 1);
        int colour = _freeColours[index];
        _freeColours.RemoveAt(index);
        return colour;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyAppearanceAssigned(int peerId, int variant, int colour)
    {
        Appearances[peerId] = new RacerAppearance(variant, colour);
        EmitSignal(SignalName.AppearanceAssigned, peerId, variant, colour);
    }

    /// <summary>What a peer's car looks like, or the stock paint if nobody has dealt them one.</summary>
    public RacerAppearance AppearanceOf(int peerId) =>
        Appearances.TryGetValue(peerId, out RacerAppearance a) ? a : RacerAppearance.Unassigned;

    // ---- Names ----
    //
    // A peer id is a random 32-bit number, which is no use for "watch out, it's behind you".
    // Names travel the same way appearances do — the server holds the list and tells everyone —
    // except that the name comes *from* the client, so it has to be sanitised on arrival rather
    // than trusted.

    /// <summary>What a peer calls themselves, falling back to something readable.</summary>
    public string NameOf(int peerId) =>
        Names.TryGetValue(peerId, out string? name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : $"Racer {peerId}";

    /// <summary>Call once connected. The host sets its own directly; a client asks the server.</summary>
    public void PublishLocalName()
    {
        if (!NetworkManager.Instance.IsNetworked)
            return;

        if (NetworkManager.Instance.IsHost)
            ServerSetName(Multiplayer.GetUniqueId(), LocalPlayerName);
        else
            RpcId(1, MethodName.ServerRequestName, LocalPlayerName);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestName(string name) => ServerSetName(Multiplayer.GetRemoteSenderId(), name);

    private void ServerSetName(int peerId, string name)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        // Trimmed and capped here, on the authority, because the string arrived from a client.
        string clean = name.StripEdges();
        if (clean.Length > MaxNameLength)
            clean = clean[..MaxNameLength];

        if (string.IsNullOrWhiteSpace(clean))
            clean = $"Racer {peerId}";

        Names[peerId] = clean;
        Rpc(MethodName.NotifyPlayerName, peerId, clean);
        NotifyPlayerName(peerId, clean);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyPlayerName(int peerId, string name)
    {
        Names[peerId] = name;
        EmitSignal(SignalName.PlayerNameChanged, peerId, name);
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
        WinnerPeerId = 0;

        // Re-published rather than assumed. It was last sent when it was chosen or when a peer
        // joined, and the length is what every peer's track measures itself against — a peer that
        // somehow missed it would be building toward a different finish line to everyone else.
        Rpc(MethodName.NotifyRaceLength, RaceLength);

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

    /// <summary>
    /// Server only. Wind the match up and put everyone back in the lobby together.
    ///
    /// Everyone, rather than whoever asked: the server is the only thing that spawns cars and it
    /// is in the match scene, so a lone peer that wandered back to the lobby on its own would
    /// stand there without one. Appearances survive — you keep your car and your colour for the
    /// whole session, which is the point of them.
    /// </summary>
    public void EndMatch()
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        Roles.Clear();
        _sceneReadyPeers.Clear();
        _allPeersReady = false;
        TrackMasterPeerId = 0;
        RoundNumber = 0;
        WinnerPeerId = 0;

        GD.Print("[GameManager] Match ended; returning to the lobby.");
        SetState(GameState.Lobby);
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
