using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using MasterTrack.Sentry;
using MasterTrack.Tiles;
using MasterTrack.TrackMaster;
using MasterTrack.UI;
using MasterTrack.Vehicles;
using System.Collections.Generic;

namespace MasterTrack.Game;

/// <summary>
/// The 3D world the match plays out in, and the place the two halves of the game are wired
/// together for whichever role this machine is playing.
///
/// - <b>Racer:</b> a car, a chase camera and the driving HUD.
/// - <b>Track Master:</b> the top-down board, the tile tray, and no car at all.
///
/// The cars themselves are <see cref="RacerArena"/>'s business — the same node the lobby uses —
/// so this class is only about the match: who is playing which side, and the hazard warnings that
/// come off the track. Tiles are not replicated as nodes: every peer rebuilds the track from the
/// confirmed placements (see <see cref="TrackController"/>).
/// </summary>
public partial class Game : Node3D
{
    private const string LobbyScenePath = "res://scenes/TestArea.tscn";

    private RacerArena _arena = null!;
    private TrackController _track = null!;
    private TrackMasterController _builder = null!;
    private VehicleHud? _hud;
    private VehicleDebugOverlay? _debug;
    private TilePalette? _palette;
    private RallyCopilot? _copilot;
    private Label? _waiting;

    private PlayerRole _localRole;

    // ---- Sentry mode furniture. All null in Live Build, and only ever some of them at once:
    //      the spectator camera is a racer thing, the sentry bar a builder thing. ----

    private SentryManager? _sentry;
    private BuildSpectatorCamera? _spectator;
    private BuildPhasePanel? _buildPanel;
    private SentryBar? _sentryBar;

    /// <summary>Deal clock during a Sentry build, in seconds. Faster than a live match's: there
    /// is no race to pace the track against, only people waiting for it to be finished.</summary>
    private const float SentryBuildDealInterval = 1.5f;

    private static bool SentryMode => GameManager.Instance.Mode == GameMode.Sentry;

    private bool IsServer => !NetworkManager.Instance.IsNetworked || Multiplayer.IsServer();

    public override void _Ready()
    {
        _arena = GetNode<RacerArena>("RacerArena");
        _track = GetNode<TrackController>("Track");
        _builder = GetNode<TrackMasterController>("TrackMaster");
        _hud = GetNodeOrNull<VehicleHud>("HUD/VehicleHud");
        _debug = GetNodeOrNull<VehicleDebugOverlay>("HUD/VehicleDebug");
        _palette = GetNodeOrNull<TilePalette>("HUD/TilePalette");
        _copilot = GetNodeOrNull<RallyCopilot>("HUD/RallyCopilot");
        _waiting = GetNodeOrNull<Label>("HUD/WaitingLabel");
        if (_waiting != null)
            _waiting.Visible = false;

        // How long the race is, as the host set it in the lobby. Set here rather than in the
        // .tscn because it is a decision somebody made a scene ago — and it can be set this late
        // because nothing reads it until the Track Master plays their first tile.
        _track.TileLimit = GameManager.Instance.RaceLength;

        bool networked = NetworkManager.Instance.IsNetworked;
        _localRole = networked ? GameManager.Instance.LocalRole : GameManager.Instance.SoloRole;

        GD.Print($"[Game] _Ready. networked={networked}, uid={Multiplayer.GetUniqueId()}, " +
                 $"role={_localRole}, race={_track.TileLimit} tiles.");

        ApplyRole();

        if (SentryMode)
        {
            // The whole track gets built and then stood on: this mode's pressure is the sentry's
            // points, not the road crumbling away. Set before any tile can land.
            _track.TrackTrailLength = 0;

            // The RPC endpoint for sentry actions. Added in _Ready on every peer under the same
            // name, which is what gives the RPCs a matching path everywhere.
            _sentry = new SentryManager { Name = "SentryManager" };
            AddChild(_sentry);
            _sentry.DebuffApplied += OnDebuffApplied;

            GameManager.Instance.MatchPhaseChanged += OnMatchPhaseChanged;

            // The spectator camera has done its job the moment this machine's car exists.
            _arena.LocalCarSpawned += OnLocalCarSpawned;
        }

        // Pay every piece's one-time costs now, while the scene is still settling, rather than
        // the first time the Track Master plays each card mid-race.
        WarmPieceCaches();

        // Warnings are computed against the authoritative track, so only the server does it.
        _track.TilePlaced += OnTilePlaced;

        // The track spots the crossing; what it means for the match is decided here. Only the
        // server ever fires this.
        _track.RaceFinished += OnRaceFinished;
        GameManager.Instance.MatchWon += OnMatchWon;

        if (!networked)
        {
            // A solo Sentry build (--role=trackmaster --mode=sentry) runs the phases for real:
            // no car until the build is done. Any other solo combination plays as it always has.
            if (SentryMode && _localRole == PlayerRole.TrackMaster)
                GameManager.Instance.BeginBuildPhase();
            else
                SpawnSolo();

            // One frame later, report the final render state so we can see what happened.
            CallDeferred(nameof(ReportRenderState));
            return;
        }

        // Only the server instantiates cars; the spawner mirrors them to clients. Held until
        // every peer has reported this scene loaded rather than fired a frame after our own
        // _Ready — see the scene-ready handshake in GameManager for why the wait is unbounded.
        if (Multiplayer.IsServer())
            GameManager.Instance.AllPeersReady += OnAllPeersReady;

        GameManager.Instance.SceneReadyProgress += OnSceneReadyProgress;
        GameManager.Instance.GameStateChanged += OnGameStateChanged;
        GameManager.Instance.ReportSceneReady();
    }

    /// <summary>The match is over; everyone goes back to the lobby together.</summary>
    private void OnGameStateChanged(int state)
    {
        if ((GameState)state == GameState.Lobby)
            GetTree().ChangeSceneToFile(LobbyScenePath);
    }

    /// <summary>
    /// Autoloads outlive this scene, and a C# <c>+=</c> handler is a managed delegate Godot
    /// cannot tie to a node's lifetime — so it has to be taken back by hand or the next signal
    /// lands on a disposed node.
    /// </summary>
    public override void _ExitTree()
    {
        GameManager.Instance.AllPeersReady -= OnAllPeersReady;
        GameManager.Instance.SceneReadyProgress -= OnSceneReadyProgress;
        GameManager.Instance.GameStateChanged -= OnGameStateChanged;
        GameManager.Instance.MatchWon -= OnMatchWon;
        GameManager.Instance.MatchPhaseChanged -= OnMatchPhaseChanged;

        // The phase describes this match, and this match is over on this machine whatever the
        // reason — see ResetPhase for why it cannot be left to the graceful path alone.
        GameManager.Instance.ResetPhase();
    }

    /// <summary>
    /// Server only. Somebody got to the chequered bar at the end of the track. That is the race,
    /// so hand it to the manager, which is the only thing that decides matches are over.
    /// </summary>
    private void OnRaceFinished(int peerId) => GameManager.Instance.DeclareWinner(peerId);

    /// <summary>
    /// Instance every deck piece once, out of sight, and throw it away a frame later.
    ///
    /// Baking took the big cost out of placement — the CSG rebuild — but two one-time costs per
    /// piece remain, and both used to land in the middle of a race the first time each card was
    /// played: loading and parsing the scene file (ToiletBowl carries a seventy-thousand-vertex
    /// baked mesh now), and Jolt cooking the baked trimesh into its own format when the shape
    /// first enters the physics space. Both are cached — the resource cache holds the scene, and
    /// the cooked shape lives on the shared shape resource — so touching each piece once here
    /// means every placement for the rest of the session is an instancing, nothing more.
    ///
    /// One piece per frame, far below the board, on every peer and both roles: racers build the
    /// replicated track too. Fire-and-forget, so the handshake with the other peers runs on top
    /// of it rather than after it.
    /// </summary>
    private async void WarmPieceCaches()
    {
        foreach (TileDefinition definition in TileCatalog.All)
        {
            // The scene could be torn down mid-warmup — a host cancelling out of a match during
            // the first second of it. Awaiting across that without checking would instance
            // pieces into a freed parent.
            if (!IsInsideTree())
                return;

            if (definition.ScenePath is not { Length: > 0 } path
                || GD.Load<PackedScene>(path) is not { } scene
                || scene.Instantiate() is not Node3D piece)
                continue;

            piece.Position = new Vector3(0.0f, -3000.0f, 0.0f);
            AddChild(piece);

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            piece.QueueFree();
        }
    }

    /// <summary>
    /// Everyone, server included: say who won and leave it up. The manager takes the session back
    /// to the lobby a few seconds later — see <see cref="OnGameStateChanged"/> for the way out.
    /// </summary>
    private void OnMatchWon(int peerId)
    {
        if (_waiting == null)
            return;

        string who = NetworkManager.Instance.IsNetworked
            ? GameManager.Instance.NameOf(peerId)
            : "You";

        _waiting.Text = $"{who} reached the finish!";
        _waiting.Visible = true;
    }

    /// <summary>
    /// Every peer is in the scene, so nodes spawned from here on will reach all of them. In Live
    /// Build that means cars, now; in Sentry mode it means the build phase can open — the cars
    /// wait until it closes.
    /// </summary>
    private void OnAllPeersReady()
    {
        GameManager.Instance.AllPeersReady -= OnAllPeersReady;

        if (SentryMode)
            GameManager.Instance.BeginBuildPhase();
        else
            CallDeferred(nameof(SpawnNetworkedRacers));
    }

    // ---- Sentry mode: the two phases ----

    private void OnMatchPhaseChanged(int phase, float seconds)
    {
        switch ((MatchPhase)phase)
        {
            case MatchPhase.Building:
                EnterBuildPhase();
                break;

            case MatchPhase.Racing:
                EnterRacePhase();
                break;
        }
    }

    /// <summary>
    /// The track gets built while everyone watches. The builder's board is already up (that is
    /// what <see cref="ApplyRole"/> does); this adds the countdown overlay for both roles, the
    /// eagle-eye camera for the racers, and a quicker deal for the builder.
    /// </summary>
    private void EnterBuildPhase()
    {
        bool isBuilder = _localRole == PlayerRole.TrackMaster;

        _buildPanel = new BuildPhasePanel { Name = "BuildPhasePanel", ShowDoneButton = isBuilder };
        GetNode("HUD").AddChild(_buildPanel);

        if (isBuilder)
        {
            _builder.Hand.DealInterval = SentryBuildDealInterval;
            return;
        }

        _spectator = new BuildSpectatorCamera { Name = "BuildSpectator", Track = _track };
        AddChild(_spectator);
    }

    /// <summary>
    /// The build is over: the track locks as it stands, the cars arrive, and the builder turns
    /// sentry. Runs on every peer; the spawn itself is the server's alone, as always.
    /// </summary>
    private void EnterRacePhase()
    {
        _buildPanel?.QueueFree();
        _buildPanel = null;

        // Every peer locks its own copy off the same broadcast — the bar at the head is the
        // finish now, and the server's placement check starts refusing tiles by itself.
        _track.LockTrack();

        if (IsServer)
        {
            if (NetworkManager.Instance.IsNetworked)
                SpawnNetworkedRacers();
            else
                SpawnSolo();
        }

        if (_localRole != PlayerRole.TrackMaster || _sentry == null)
            return;

        // The hand is history — the sentry bar takes the palette's spot on the screen.
        _palette?.Hide();
        _builder.ClearPreview();

        _builder.EnableSentry(_sentry);
        _sentryBar = new SentryBar { Name = "SentryBar", Board = _builder, Sentry = _sentry };
        GetNode("HUD").AddChild(_sentryBar);
    }

    /// <summary>This machine's car exists; the build-phase spectator camera is done.</summary>
    private void OnLocalCarSpawned(RacerController car)
    {
        _spectator?.QueueFree();
        _spectator = null;
    }

    /// <summary>
    /// A debuff landed on somebody. If that somebody is this machine's player — or everybody,
    /// which a peer id of 0 means — shout it through the copilot, the voice that already warns
    /// about the road. The delayed debuffs shout <i>before</i> they hit: the broadcast lands at
    /// the start of the fuse, so "INCOMING" plus the aura is the reaction window, and a name for
    /// what follows is the difference between "the sentry got me" and "the game broke".
    /// </summary>
    private void OnDebuffApplied(int peerId, int kind)
    {
        if (peerId != 0 && peerId != Multiplayer.GetUniqueId())
            return;

        // The winner banner owns the screen once a match is decided.
        if (GameManager.Instance.WinnerPeerId != 0)
            return;

        var action = (SentryActionKind)kind;
        bool fused = action is SentryActionKind.RunawayBooster
                     or SentryActionKind.CrossedWires
                     or SentryActionKind.MoonGravity;

        string name = SentryActions.NameOf(action);
        string shout = fused
            ? $"INCOMING: {name.TrimEnd('!').ToUpperInvariant()}!"
            : name;

        if (_copilot != null)
        {
            // Clip key is the enum name — drop e.g. crossedwires.wav into the callout folder
            // and the warning gains a voice.
            _copilot.CallOut(shout,
                fused ? new Color(1.0f, 0.25f, 0.2f) : new Color(1.0f, 0.6f, 0.1f),
                action.ToString());
            return;
        }

        if (_waiting == null)
            return;

        _waiting.Text = shout;
        _waiting.Visible = true;

        GetTree().CreateTimer(2.5f).Timeout += () =>
        {
            if (IsInstanceValid(_waiting) && _waiting.Text == shout
                && GameManager.Instance.WinnerPeerId == 0)
                _waiting.Visible = false;
        };
    }

    /// <summary>
    /// Say who we are still waiting on. Without this the wait is indistinguishable from a
    /// hang — which is the whole reason it is a visible count rather than a timeout.
    /// </summary>
    private void OnSceneReadyProgress(int ready, int total)
    {
        if (_waiting == null)
            return;

        _waiting.Text = $"Waiting for players... ({ready}/{total})";
        _waiting.Visible = ready < total;
    }

    /// <summary>Turn on the half of the game this machine is playing, and turn off the other.</summary>
    private void ApplyRole()
    {
        bool isTrackMaster = _localRole == PlayerRole.TrackMaster;

        _builder.SetProcess(isTrackMaster);
        _builder.SetProcessUnhandledInput(isTrackMaster);
        _builder.Visible = isTrackMaster;

        // The Track Master isn't driving, so the speedo and the physics overlay are just noise.
        if (_hud != null)
            _hud.Visible = !isTrackMaster;
        if (_debug != null)
            _debug.Visible = !isTrackMaster;
        if (_palette != null)
            _palette.Visible = isTrackMaster;

        if (isTrackMaster)
            return;

        // A racer must never own the board camera or answer the builder's mouse.
        _builder.QueueFree();
        _palette?.QueueFree();
    }

    private void SpawnSolo()
    {
        // The Track Master gets an unowned car so the board has something on it to build ahead
        // of. It takes no input and never claims the camera.
        int owner = _localRole == PlayerRole.TrackMaster ? -1 : Multiplayer.GetUniqueId();
        _arena.Spawn(owner, 0, 1);
    }

    private void SpawnNetworkedRacers()
    {
        var racers = new List<int>();
        foreach (var kvp in GameManager.Instance.Roles)
        {
            if (kvp.Value == PlayerRole.Racer)
                racers.Add(kvp.Key);
        }

        _arena.SpawnFor(racers);
    }

    /// <summary>
    /// Server only. A tile landed — tell any racer it landed exactly
    /// <see cref="RacerController.WarningLookahead"/> tiles in front of, and nobody else.
    /// After the warning fades it's on them to remember it.
    /// </summary>
    private void OnTilePlaced(int trackIndex, int hazard, int catalogIndex)
    {
        if (NetworkManager.Instance.IsNetworked && !Multiplayer.IsServer())
            return;

        // A Sentry build has nobody on the road to warn. What a placement can do instead is
        // finish the build: the budget's last tile is the builder's Done button pressed for them.
        if (SentryMode)
        {
            if (GameManager.Instance.Phase == MatchPhase.Building && _track.AtTileLimit)
                GameManager.Instance.BeginRacePhase();
            return;
        }

        int warnIndex = trackIndex - RacerController.WarningLookahead;
        if (warnIndex < 0)
            return;

        foreach (Node child in _arena.Racers.GetChildren())
        {
            if (child is not RacerController car)
                continue;

            // Which tile a car is standing on used to be a cell lookup. With the track off the
            // grid there is no lattice to look it up in, and there does not need to be: the car
            // already collides with exactly one TrackTile body, so the ground ray under it is a
            // more accurate answer than a cell ever was.
            if (car.CurrentTrackIndex == warnIndex)
                car.ServerSendHazardWarning(trackIndex, (TileHazard)hazard);
        }
    }

    /// <summary>Debug: what actually ended up in the tree / on screen after spawning.</summary>
    private void ReportRenderState()
    {
        Camera3D current = GetViewport().GetCamera3D();
        GD.Print($"[Game] Racers in tree: {_arena.Racers.GetChildCount()}. " +
                 $"Track tiles: {_track.Grid.Count}, head {_track.Grid.HeadAnchor.Position} " +
                 $"yaw {Mathf.RadToDeg(_track.Grid.HeadAnchor.Yaw):0}deg. Current camera: " +
                 (current != null ? current.GetPath() : "<none>"));
    }
}
