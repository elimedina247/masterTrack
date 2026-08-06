using Godot;
using MasterTrack.Racer;
using System.Collections.Generic;

namespace MasterTrack.Networking;

/// <summary>
/// What one player's car looks like: which model, which rainbow colour, and where the antenna
/// sits. Settled at join time and kept for the whole session, so your car is the same in the
/// lobby and in the match — the colour is how people tell each other apart. The model and the
/// antenna are the player's own pick from the main menu; the colour is a pick too, but only
/// granted if nobody in the session is already wearing it.
/// </summary>
public readonly record struct RacerAppearance(int VariantIndex, int ColourIndex,
                                              int AntennaSpot = CarVariants.DefaultAntennaSpot)
{
    /// <summary>Colour index meaning "nobody dealt this car a colour" — solo play, the unowned
    /// car the board builds ahead of, and a menu pick of "auto".</summary>
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

/// <summary>Which shape of match the host has picked for the session.</summary>
public enum GameMode
{
    /// <summary>The classic game: the track is dealt and laid while the racers are already driving on it.</summary>
    LiveBuild,

    /// <summary>
    /// The Track Master builds the whole track first, then turns sentry: they spend a
    /// regenerating points pool on sabotage — debuffs on cars, missiles at the road — while the
    /// racers run what they built. See <see cref="MatchPhase"/> for where a match of this shape
    /// currently is.
    /// </summary>
    Sentry,

    /// <summary>
    /// Tower Defense. The same three phases as <see cref="Sentry"/> — build the track, furnish
    /// it, then watch — but what the builder furnishes it with plays itself: rocket turrets on
    /// columns beside the road, which acquire and fire on their own.
    ///
    /// The difference from Sentry is about <i>when the decisions happen</i>. Sentry keeps the
    /// builder's hands busy through the race, pressing traps at the right moment; here the whole
    /// game is spending the rig phase well, and the race is the answer coming back. Which of the
    /// two is more fun is exactly what having both is for.
    /// </summary>
    TowerDefense,
}

/// <summary>
/// Where a <see cref="GameMode.Sentry"/> match currently is. Always <see cref="None"/> in Live
/// Build, which has no phases — building and racing are the same stretch of time there.
/// </summary>
public enum MatchPhase
{
    None,
    Building,
    Racing,

    /// <summary>
    /// Between the two: the track is finished and locked, and the Track Master is planting the
    /// dormant devices they will fire during the race. Nobody is driving yet, and the racers
    /// cannot see what is being planted — see <c>docs/sentry-mode-plan.md</c>.
    ///
    /// Appended rather than slotted between <see cref="Building"/> and <see cref="Racing"/>
    /// where it belongs in time, because the phase crosses the wire as its integer value.
    /// </summary>
    Rigging,
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

    /// <summary>A peer's car model, colour and antenna spot are now known on this machine.</summary>
    [Signal] public delegate void AppearanceAssignedEventHandler(int peerId, int variant, int colour, int antenna);

    /// <summary>A peer's name is now known on this machine.</summary>
    [Signal] public delegate void PlayerNameChangedEventHandler(int peerId, string name);

    /// <summary>The race length has been set or re-published. Known on every peer.</summary>
    [Signal] public delegate void RaceLengthChangedEventHandler(int tiles);

    /// <summary>The sentry point limit has been set or re-published. Known on every peer.</summary>
    [Signal] public delegate void SentryPointLimitChangedEventHandler(int points);

    /// <summary>The host picked a game mode. Known on every peer, like the race length.</summary>
    [Signal] public delegate void GameModeChangedEventHandler(int mode);

    /// <summary>
    /// A Sentry match moved between phases. Fired on every peer. <c>seconds</c> is how long the
    /// new phase runs before the server moves it along on its own, or 0 for open-ended.
    /// </summary>
    [Signal] public delegate void MatchPhaseChangedEventHandler(int phase, float seconds);

    /// <summary>
    /// The match concluded. Fired on every peer with the winner's peer id — or <b>0 for nobody</b>,
    /// which means every racer died and the race went unfinished. (In Sentry mode that case never
    /// carries 0: a board that killed everyone belongs to the sentry, and the id is theirs.)
    /// </summary>
    [Signal] public delegate void MatchWonEventHandler(int peerId);

    /// <summary>A racer crossed the line, in what place. Fired on every peer; feeds the results
    /// board. The first of these is also the <see cref="MatchWon"/> winner.</summary>
    [Signal] public delegate void RacerFinishedEventHandler(int peerId, int place);

    /// <summary>A racer is out of the race — fell where no respawn could put them back. Fired on
    /// every peer; the car is switched off and the dead player goes spectating off this.</summary>
    [Signal] public delegate void RacerEliminatedEventHandler(int peerId);

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

    // ---- Sentry point limit ----
    //
    // How big the sentry's points pool is in Sentry mode. A lobby decision like the race length,
    // because it is the knob that sets how much chaos the racers signed up for: the pool starts
    // full, regenerates during the race (see Sentry.SentryActions.RegenPerSecond), and never
    // grows past this.

    /// <summary>Pool sizes the lobby offers, in points.</summary>
    public static readonly int[] SentryPointLimitChoices = { 100, 250, 500, 1000 };

    public const int DefaultSentryPointLimit = 500;

    private const int MinSentryPointLimit = 10;
    private const int MaxSentryPointLimit = 9999;

    /// <summary>The sentry's pool size for this session. Host owns it; everyone is told.</summary>
    public int SentryPointLimit { get; private set; } = DefaultSentryPointLimit;

    // ---- Game mode and match phase ----
    //
    // The mode is a lobby decision, owned by the host and told to everyone the way the race
    // length is. The phase is a match decision, owned by the server: in Sentry mode the match is
    // two distinct stretches of time — everyone waiting while the track is built, then everyone
    // racing while the builder snipes — and every peer has to agree which one it is in, because
    // cars only exist in the second.

    /// <summary>Which shape of match the host picked. Replicated; see <see cref="SetGameMode"/>.</summary>
    public GameMode Mode { get; private set; } = GameMode.LiveBuild;

    /// <summary>Where a phased match currently is. <see cref="MatchPhase.None"/> outside one.</summary>
    public MatchPhase Phase { get; private set; } = MatchPhase.None;

    /// <summary>
    /// Whether this mode runs build → rig → race rather than building live under the wheels.
    ///
    /// Asked rather than comparing against <see cref="GameMode.Sentry"/> by name, because the
    /// phase machine was never really about the sentry: it is about a match where the track is
    /// finished before anybody drives on it, and Tower Defense wants exactly the same shape. Any
    /// mode added later that furnishes a finished track gets the whole flow by answering true
    /// here. What the modes actually differ on is what the rig phase sells.
    /// </summary>
    public bool IsPhasedMode => Mode is GameMode.Sentry or GameMode.TowerDefense;

    // The build clock. A flat floor plus a per-tile allowance, because the builder's job scales
    // with the race length the host picked: a 50-tile track deserves more wall time than a
    // 10-tile one, but neither deserves forever — the timer is what stops a builder stalling
    // with everyone else sitting in a spectator camera.
    private const float BuildSecondsBase = 60.0f;
    private const float BuildSecondsPerTile = 6.0f;

    // The rig clock. Shorter and flatter than the build's, because rigging is a handful of
    // decisions about a track that already exists rather than a track's worth of building —
    // and because everybody else is now waiting on a finished course they cannot drive yet.
    private const float RigSecondsBase = 40.0f;
    private const float RigSecondsPerTile = 1.5f;

    /// <summary>When the running phase ends, on this machine's clock. Set from the phase RPC.</summary>
    private ulong _buildEndsAtMsec;

    /// <summary>Whether the phase currently running is one with a clock the builder works against.</summary>
    public bool IsTimedPhase => Phase is MatchPhase.Building or MatchPhase.Rigging;

    /// <summary>Seconds left in the current timed phase, for countdown labels. 0 outside one.
    /// Serves the rig phase as well as the build — one deadline is set per phase, and which
    /// phase it belongs to is whichever one is running.</summary>
    public float BuildSecondsLeft => IsTimedPhase
        ? Mathf.Max(0.0f, (_buildEndsAtMsec - (float)Time.GetTicksMsec()) / 1000.0f)
        : 0.0f;

    /// <summary>Who won the current match, or 0 if it is still being raced.</summary>
    public int WinnerPeerId { get; private set; }

    // ---- The race's ledger: who finished, who died ----
    //
    // Both lists are replicated by broadcast and identical on every peer, because the results
    // board reads them locally the moment the match concludes. Order matters in both: finish
    // order is the leaderboard, and elimination order is the story of who lasted longest.

    /// <summary>Peers that crossed the line, in the order they crossed it.</summary>
    public readonly List<int> FinishOrder = new();

    /// <summary>Peers that died during the race, in the order they went.</summary>
    public readonly List<int> EliminatedOrder = new();

    /// <summary>Whether the match ended with every racer dead and nobody across the line.
    /// In Sentry mode that outcome crowns the sentry instead — see <see cref="ServerEliminate"/>.</summary>
    public bool MatchUnfinished { get; private set; }

    /// <summary>Whether a peer is out of the race.</summary>
    public bool IsEliminated(int peerId) => EliminatedOrder.Contains(peerId);

    /// <summary>Whether a peer has already crossed the line.</summary>
    public bool HasFinished(int peerId) => FinishOrder.Contains(peerId);

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

    /// <summary>
    /// The car this machine wants: model and antenna are always honoured, the colour only if
    /// nobody in the session is wearing it (<see cref="RacerAppearance.NoColour"/> = no
    /// preference, deal me one). Kept up to date by the garage pane in the main menu, and — like
    /// <see cref="LocalPlayerName"/> — sent to the server once connected. Solo play reads it
    /// directly through <see cref="AppearanceOf"/>.
    /// </summary>
    public RacerAppearance LocalPreference { get; set; } = RacerAppearance.Unassigned;

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

        // _Process is only the build clock, and only the server's. Off until a build phase starts.
        SetProcess(false);

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

    // ---- Appearance: which car, which colour, where the antenna sits ----
    //
    // Settled on join rather than at match start, because everyone is already driving around the
    // lobby by then and a car that changed colour on the way into the match would undo the one
    // thing the colour is for. The builder holds a colour for the whole wait too — they are only
    // singled out when the host presses Start — which is why the palette has to cover the lobby
    // rather than just the racers.
    //
    // The model, colour and antenna are picked in the main menu, but the server still owns the
    // result: a client's pick arrives as a request (like its name does), gets sanitised, and the
    // colour is only granted if it is free — the colour is the half that has to stay unique.
    // A newcomer is dealt a random hand the moment they connect and their request lands a moment
    // later, re-dealing them before their car exists: both RPCs ride the same reliable ordered
    // channel, and the request is sent before the client even starts loading the lobby scene.

    /// <summary>Server only. Fresh session: nobody has a colour yet, and we take our pick.</summary>
    private void OnServerCreated()
    {
        Appearances.Clear();
        Names.Clear();

        _freeColours.Clear();
        for (int i = 0; i < CarVariants.Palette.Length; i++)
            _freeColours.Add(i);

        AssignAppearance(Multiplayer.GetUniqueId(), LocalPreference);
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
                      kvp.Value.VariantIndex, kvp.Value.ColourIndex, kvp.Value.AntennaSpot);
        }

        foreach (var kvp in Names)
        {
            if (kvp.Key != peerId)
                RpcId(peerId, MethodName.NotifyPlayerName, kvp.Key, kvp.Value);
        }

        // The lobby shows the race length to everybody, not only to the host who set it, so a
        // newcomer has to be told what was decided before they arrived. Same for the mode and
        // the sentry pool.
        RpcId(peerId, MethodName.NotifyRaceLength, RaceLength);
        RpcId(peerId, MethodName.NotifyGameMode, (int)Mode);
        RpcId(peerId, MethodName.NotifySentryPointLimit, SentryPointLimit);
    }

    // ---- Game mode ----

    /// <summary>
    /// Host only. Pick the shape of the next match, and tell everyone. Called from the lobby,
    /// the same way <see cref="SetRaceLength"/> is.
    /// </summary>
    public void SetGameMode(GameMode mode)
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
        {
            GD.PushWarning("[GameManager] Only the host sets the game mode; ignored.");
            return;
        }

        if (mode == Mode)
            return;

        Mode = mode;
        GD.Print($"[GameManager] Game mode set to {Mode}.");

        if (NetworkManager.Instance.IsNetworked)
            Rpc(MethodName.NotifyGameMode, (int)Mode);

        EmitSignal(SignalName.GameModeChanged, (int)Mode);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyGameMode(int mode)
    {
        Mode = (GameMode)mode;
        EmitSignal(SignalName.GameModeChanged, mode);
    }

    // ---- Match phase (Sentry mode) ----

    /// <summary>
    /// Server only. Open the build phase: the Track Master lays the whole track while everyone
    /// else watches, against a clock sized to the race length. Called by the match scene once
    /// every peer is in it — the same moment Live Build would have spawned the cars.
    /// </summary>
    public void BeginBuildPhase()
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
            return;

        if (!IsPhasedMode || Phase != MatchPhase.None)
            return;

        float seconds = BuildSecondsBase + RaceLength * BuildSecondsPerTile;
        GD.Print($"[GameManager] Build phase open: {seconds:0}s for {RaceLength} tiles.");

        SetPhase(MatchPhase.Building, seconds);
        SetProcess(true);
    }

    /// <summary>
    /// Server only. Close the build phase and open the rig. Reached three ways — the builder's
    /// Done button, the build clock running out, or the tile budget being spent — and idempotent,
    /// because two of those can land on the same frame.
    ///
    /// The track being finished no longer means the race starts. It means the builder stops
    /// laying road and starts laying traps, with the whole course in front of them for the first
    /// time; that is the point of splitting the phase at all.
    /// </summary>
    public void BeginRigPhase()
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
            return;

        if (Phase != MatchPhase.Building)
            return;

        float seconds = RigSecondsBase + RaceLength * RigSecondsPerTile;
        GD.Print($"[GameManager] Build over; rig phase open for {seconds:0}s.");

        SetPhase(MatchPhase.Rigging, seconds);
        SetProcess(true);
    }

    /// <summary>
    /// Server only. Close the rig and let the race begin — the builder's Ready button or the rig
    /// clock running out.
    /// </summary>
    public void BeginRacePhase()
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
            return;

        if (Phase != MatchPhase.Rigging)
            return;

        SetProcess(false);
        GD.Print("[GameManager] Rig phase over; the race is on.");
        SetPhase(MatchPhase.Racing, 0.0f);
    }

    /// <summary>The phase clock. Only ever running on the server, and only while a timed phase
    /// is. Each expiry advances to the next phase, so one clock serves both.</summary>
    public override void _Process(double delta)
    {
        if (!IsTimedPhase)
        {
            SetProcess(false);
            return;
        }

        if (BuildSecondsLeft > 0.0f)
            return;

        if (Phase == MatchPhase.Building)
            BeginRigPhase();
        else
            BeginRacePhase();
    }

    /// <summary>
    /// The builder says they are done with whatever phase they are in — the track is finished, or
    /// the rig is. One button, two meanings, because from the builder's side it is the same
    /// sentence both times. Anyone may call; the server checks the asker really is the Track
    /// Master before advancing anything.
    /// </summary>
    public void RequestFinishBuilding()
    {
        if (!NetworkManager.Instance.IsNetworked)
        {
            AdvanceTimedPhase();
            return;
        }

        if (NetworkManager.Instance.IsHost)
        {
            ServerFinishBuilding(Multiplayer.GetUniqueId());
            return;
        }

        RpcId(1, MethodName.ServerRequestFinishBuilding);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestFinishBuilding() => ServerFinishBuilding(Multiplayer.GetRemoteSenderId());

    private void ServerFinishBuilding(int senderId)
    {
        if (!NetworkManager.Instance.IsHost)
            return;

        if (senderId != TrackMasterPeerId)
        {
            GD.PushWarning($"[GameManager] Peer {senderId} tried to end the build phase but is not the builder.");
            return;
        }

        AdvanceTimedPhase();
    }

    /// <summary>Move the builder on to whatever comes next: build to rig, rig to race. A no-op
    /// anywhere else, which is what makes the Done button safe to press twice.</summary>
    private void AdvanceTimedPhase()
    {
        if (Phase == MatchPhase.Building)
            BeginRigPhase();
        else if (Phase == MatchPhase.Rigging)
            BeginRacePhase();
    }

    /// <summary>
    /// Local, no broadcast: forget the phase. Called by the match scene on its way out, because
    /// the phase must never outlive the match it describes — the graceful path is reset by
    /// <see cref="EndMatch"/>'s broadcast, but a solo Escape or a dropped connection leaves this
    /// machine's copy stranded wherever it was, and <see cref="BeginBuildPhase"/> refuses to
    /// open over a phase that never closed.
    /// </summary>
    public void ResetPhase()
    {
        SetProcess(false);
        Phase = MatchPhase.None;

        // The race's ledger describes this match too, and solo runs never pass through
        // StartMatch/EndMatch — without this a solo win (or death) would carry into the next
        // solo run and quietly refuse to declare its winner.
        WinnerPeerId = 0;
        FinishOrder.Clear();
        EliminatedOrder.Clear();
        MatchUnfinished = false;
    }

    private void SetPhase(MatchPhase phase, float seconds)
    {
        if (NetworkManager.Instance.IsNetworked)
            Rpc(MethodName.NotifyMatchPhase, (int)phase, seconds);

        NotifyMatchPhase((int)phase, seconds);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyMatchPhase(int phase, float seconds)
    {
        Phase = (MatchPhase)phase;

        // Each peer runs the countdown on its own clock from here. The start is what is shared;
        // a few network milliseconds of disagreement about the deadline hurts nothing, because
        // only the server's copy actually ends the phase.
        _buildEndsAtMsec = Time.GetTicksMsec() + (ulong)(Mathf.Max(seconds, 0.0f) * 1000.0f);

        EmitSignal(SignalName.MatchPhaseChanged, phase, seconds);
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

    // ---- Sentry point limit ----

    /// <summary>
    /// Host only. Set the sentry's pool size for the next match, and tell everyone. Called from
    /// the lobby, the same way <see cref="SetRaceLength"/> is.
    /// </summary>
    public void SetSentryPointLimit(int points)
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
        {
            GD.PushWarning("[GameManager] Only the host sets the sentry point limit; ignored.");
            return;
        }

        int clamped = Mathf.Clamp(points, MinSentryPointLimit, MaxSentryPointLimit);
        if (clamped == SentryPointLimit)
            return;

        SentryPointLimit = clamped;
        GD.Print($"[GameManager] Sentry point limit set to {SentryPointLimit}.");

        if (NetworkManager.Instance.IsNetworked)
            Rpc(MethodName.NotifySentryPointLimit, SentryPointLimit);

        EmitSignal(SignalName.SentryPointLimitChanged, SentryPointLimit);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifySentryPointLimit(int points)
    {
        SentryPointLimit = points;
        EmitSignal(SignalName.SentryPointLimitChanged, points);
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

    /// <summary>
    /// Server only. A racer crossed the line. The first crossing is the win and ends the match
    /// the way it always has; the rest — cars arriving during the victory linger — are appended
    /// to the finish order so the results board can list everyone who completed the race, not
    /// only whoever completed it first.
    /// </summary>
    public void RecordFinish(int peerId)
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
            return;

        if (FinishOrder.Contains(peerId))
            return;

        int place = FinishOrder.Count + 1;

        if (NetworkManager.Instance.IsNetworked)
            Rpc(MethodName.NotifyRacerFinished, peerId, place);
        NotifyRacerFinished(peerId, place);

        if (place == 1)
            DeclareWinner(peerId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyRacerFinished(int peerId, int place)
    {
        if (!FinishOrder.Contains(peerId))
            FinishOrder.Add(peerId);

        EmitSignal(SignalName.RacerFinished, peerId, place);
    }

    // ---- Elimination ----
    //
    // Death is detected where the car is simulated — the owner's machine is the only one that
    // knows its car fell somewhere no respawn can fix — and becomes real here, the way every
    // sentry action does: the owner asks, the server checks, everyone is told. Elimination only
    // exists inside a match; the lobby and the proving ground never send these.

    /// <summary>
    /// Called by the dying car's own machine. The car has already decided the fall is fatal
    /// (see <c>RacerController.UpdateKillPlane</c>); this routes the fact to the authority.
    /// </summary>
    public void ReportSelfEliminated()
    {
        if (!NetworkManager.Instance.IsNetworked || NetworkManager.Instance.IsHost)
        {
            ServerEliminate(Multiplayer.GetUniqueId());
            return;
        }

        RpcId(1, MethodName.ServerRequestEliminated);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestEliminated() => ServerEliminate(Multiplayer.GetRemoteSenderId());

    /// <summary>
    /// Server only. Make a death real, tell everyone, and see whether it ended the match: with
    /// every racer dead and nobody across the line, a Sentry board belongs to the sentry —
    /// <c>"{their name} WON !!!"</c> — and a Live Build race simply went unfinished.
    /// </summary>
    private void ServerEliminate(int peerId)
    {
        if (NetworkManager.Instance.IsNetworked && !NetworkManager.Instance.IsHost)
            return;

        // Only mid-match, only racers, only once, and never someone already across the line — a
        // finisher who then falls off the world has still finished.
        if (EliminatedOrder.Contains(peerId) || FinishOrder.Contains(peerId))
            return;

        if (NetworkManager.Instance.IsNetworked
            && (State != GameState.InRound || Roles.GetValueOrDefault(peerId) != PlayerRole.Racer))
            return;

        if (NetworkManager.Instance.IsNetworked)
            Rpc(MethodName.NotifyEliminated, peerId);
        NotifyEliminated(peerId);

        CheckAllRacersOut();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyEliminated(int peerId)
    {
        if (EliminatedOrder.Contains(peerId))
            return;

        EliminatedOrder.Add(peerId);
        EmitSignal(SignalName.RacerEliminated, peerId);
    }

    /// <summary>
    /// Server only. If that death was the last racer standing, conclude the match. Solo has no
    /// role table, so a solo death is always the last one.
    /// </summary>
    private void CheckAllRacersOut()
    {
        if (WinnerPeerId != 0 || MatchUnfinished || FinishOrder.Count > 0)
            return;

        if (NetworkManager.Instance.IsNetworked)
        {
            foreach (var kvp in Roles)
            {
                if (kvp.Value == PlayerRole.Racer && !EliminatedOrder.Contains(kvp.Key))
                    return;
            }
        }

        // Everybody died. In the phased modes that is not a draw, it is the builder's win —
        // wiping the pack is the role's fantasy and the board should say so with their name on
        // it, whether they did it by hand or by turret.
        if (IsPhasedMode && TrackMasterPeerId != 0)
        {
            GD.Print($"[GameManager] Every racer is down — the sentry (peer {TrackMasterPeerId}) wins.");
            DeclareWinner(TrackMasterPeerId);
            return;
        }

        DeclareUnfinished();
    }

    /// <summary>
    /// Server only. Nobody finished and nobody is left to: the race goes in the book as
    /// unfinished. The same shape as <see cref="DeclareWinner"/> with nobody's name on it.
    /// </summary>
    private void DeclareUnfinished()
    {
        GD.Print("[GameManager] Every racer is down; the race is unfinished.");

        if (!NetworkManager.Instance.IsNetworked)
        {
            NotifyUnfinished();
            return;
        }

        Rpc(MethodName.NotifyUnfinished);
        NotifyUnfinished();

        SetState(GameState.MatchOver);

        GetTree().CreateTimer(VictoryLingerSeconds).Timeout += () =>
        {
            if (State == GameState.MatchOver)
                EndMatch();
        };
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyUnfinished()
    {
        MatchUnfinished = true;
        EmitSignal(SignalName.MatchWon, 0);
    }

    /// <summary>
    /// Server only. Deal a peer its car — its own picks where a preference is known, random
    /// where it is not. Model and antenna are taken as asked; the colour goes through
    /// <see cref="TakeColour"/>, which only grants a pick that is still free.
    /// </summary>
    private void AssignAppearance(int peerId, RacerAppearance? preference = null)
    {
        if (Appearances.ContainsKey(peerId))
            return;

        // Model with replacement, colour without: models may repeat across the lobby, but
        // the colours cannot, and the colour is the half that has to be unique.
        int variant = preference is { } p
            ? Mathf.PosMod(p.VariantIndex, CarVariants.All.Count)
            : GD.RandRange(0, CarVariants.All.Count - 1);
        int antenna = Mathf.PosMod(preference?.AntennaSpot ?? CarVariants.DefaultAntennaSpot,
                                   CarVariants.AntennaSpots.Length);

        var appearance = new RacerAppearance(variant,
            TakeColour(preference?.ColourIndex ?? RacerAppearance.NoColour), antenna);
        Appearances[peerId] = appearance;

        GD.Print($"[GameManager] Peer {peerId} gets {CarVariants.At(appearance.VariantIndex).Name} " +
                 $"in {appearance.ColourName}.");

        Rpc(MethodName.NotifyAppearanceAssigned, peerId,
            appearance.VariantIndex, appearance.ColourIndex, appearance.AntennaSpot);
        NotifyAppearanceAssigned(peerId,
            appearance.VariantIndex, appearance.ColourIndex, appearance.AntennaSpot);
    }

    /// <summary>Call once connected. The host's pick is applied when its server comes up; a
    /// client sends its pick to the server, the way <see cref="PublishLocalName"/> does.</summary>
    public void PublishLocalPreference()
    {
        if (!NetworkManager.Instance.IsNetworked || NetworkManager.Instance.IsHost)
            return;

        RacerAppearance p = LocalPreference;
        RpcId(1, MethodName.ServerRequestAppearance, p.VariantIndex, p.ColourIndex, p.AntennaSpot);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerRequestAppearance(int variant, int colour, int antenna)
    {
        int peerId = Multiplayer.GetRemoteSenderId();
        if (!NetworkManager.Instance.IsHost)
            return;

        // The peer was dealt a random hand the moment it connected; this is its actual pick
        // arriving, so re-deal. Indices are sanitised here, on the authority, because they came
        // from a client — same treatment its name gets.
        if (Appearances.Remove(peerId, out RacerAppearance dealt))
            _freeColours.Add(dealt.ColourIndex);

        int wantedColour = colour == RacerAppearance.NoColour
            ? RacerAppearance.NoColour
            : Mathf.PosMod(colour, CarVariants.Palette.Length);

        AssignAppearance(peerId, new RacerAppearance(variant, wantedColour, antenna));
    }

    /// <summary>
    /// Take a colour from the pool: the asked-for one if it is still free, any free one
    /// otherwise. Asking for a colour somebody is wearing quietly gets you a different one —
    /// first to join keeps it, because the colour is an identity and identities don't transfer.
    /// </summary>
    private int TakeColour(int preferred)
    {
        // NetworkManager.MaxPlayers is set so this cannot happen. Saying so anyway, because a
        // pool that runs dry silently hands two people the same identity.
        if (_freeColours.Count == 0)
        {
            GD.PushWarning("[GameManager] Rainbow palette exhausted; two players will share a colour.");
            return GD.RandRange(0, CarVariants.Palette.Length - 1);
        }

        if (preferred != RacerAppearance.NoColour && _freeColours.Remove(preferred))
            return preferred;

        int index = GD.RandRange(0, _freeColours.Count - 1);
        int colour = _freeColours[index];
        _freeColours.RemoveAt(index);
        return colour;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyAppearanceAssigned(int peerId, int variant, int colour, int antenna)
    {
        Appearances[peerId] = new RacerAppearance(variant, colour, antenna);
        EmitSignal(SignalName.AppearanceAssigned, peerId, variant, colour, antenna);
    }

    /// <summary>
    /// What a peer's car looks like. Solo there is no session to deal anything, so your own car
    /// wears your menu picks directly; anyone genuinely unknown gets the stock car.
    /// </summary>
    public RacerAppearance AppearanceOf(int peerId)
    {
        if (Appearances.TryGetValue(peerId, out RacerAppearance a))
            return a;

        return !NetworkManager.Instance.IsNetworked && peerId == Multiplayer.GetUniqueId()
            ? LocalPreference
            : RacerAppearance.Unassigned;
    }

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
            if (arg.StartsWith("--role=", System.StringComparison.OrdinalIgnoreCase))
            {
                string value = arg["--role=".Length..];
                if (value.Equals("trackmaster", System.StringComparison.OrdinalIgnoreCase))
                    SoloRole = PlayerRole.TrackMaster;
                else if (value.Equals("racer", System.StringComparison.OrdinalIgnoreCase))
                    SoloRole = PlayerRole.Racer;
                else
                    GD.PushWarning($"[GameManager] Unknown --role value '{value}'. Use trackmaster or racer.");

                GD.Print($"[GameManager] Solo role set from command line: {SoloRole}.");
            }

            // The same door for the mode, so a solo Sentry build can be opened straight from an
            // editor run: godot res://scenes/Game.tscn -- --role=trackmaster --mode=sentry
            if (arg.StartsWith("--mode=", System.StringComparison.OrdinalIgnoreCase))
            {
                string value = arg["--mode=".Length..];
                if (value.Equals("sentry", System.StringComparison.OrdinalIgnoreCase))
                    Mode = GameMode.Sentry;
                else if (value.Equals("livebuild", System.StringComparison.OrdinalIgnoreCase))
                    Mode = GameMode.LiveBuild;
                else if (value.Equals("towers", System.StringComparison.OrdinalIgnoreCase))
                    Mode = GameMode.TowerDefense;
                else
                    GD.PushWarning($"[GameManager] Unknown --mode value '{value}'. "
                                   + "Use sentry, towers or livebuild.");

                GD.Print($"[GameManager] Game mode set from command line: {Mode}.");
            }
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
        FinishOrder.Clear();
        EliminatedOrder.Clear();
        MatchUnfinished = false;

        // Re-published rather than assumed. It was last sent when it was chosen or when a peer
        // joined, and the length is what every peer's track measures itself against — a peer that
        // somehow missed it would be building toward a different finish line to everyone else.
        // The sentry pool rides along for the same reason: the sentry's own machine seeds its
        // ledger from it when the match scene loads.
        Rpc(MethodName.NotifyRaceLength, RaceLength);
        Rpc(MethodName.NotifySentryPointLimit, SentryPointLimit);

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
        FinishOrder.Clear();
        EliminatedOrder.Clear();
        MatchUnfinished = false;

        // A Sentry match that ends mid-phase must not leave the phase (or its clock) running
        // into the lobby — the next match checks Phase == None before it will open a build.
        SetProcess(false);
        if (Phase != MatchPhase.None)
            SetPhase(MatchPhase.None, 0.0f);

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

    /// <summary>
    /// Server only. Whether a peer has reported its current scene loaded, and so has somewhere
    /// to put anything we send it. Read by the replication gate every car carries — see
    /// <c>RacerController.BuildSpawnGate</c> — which is what stops the engine pushing cars at a
    /// peer that is still on the main menu. Always false on a client, which never counts.
    /// </summary>
    public bool IsSceneReady(int peerId) => _sceneReadyPeers.Contains(peerId);

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
