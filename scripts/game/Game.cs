using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
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
    private RacerArena _arena = null!;
    private TrackController _track = null!;
    private TrackMasterController _builder = null!;
    private VehicleHud? _hud;
    private VehicleDebugOverlay? _debug;
    private TilePalette? _palette;
    private Label? _waiting;

    private PlayerRole _localRole;

    public override void _Ready()
    {
        _arena = GetNode<RacerArena>("RacerArena");
        _track = GetNode<TrackController>("Track");
        _builder = GetNode<TrackMasterController>("TrackMaster");
        _hud = GetNodeOrNull<VehicleHud>("HUD/VehicleHud");
        _debug = GetNodeOrNull<VehicleDebugOverlay>("HUD/VehicleDebug");
        _palette = GetNodeOrNull<TilePalette>("HUD/TilePalette");
        _waiting = GetNodeOrNull<Label>("HUD/WaitingLabel");
        if (_waiting != null)
            _waiting.Visible = false;

        bool networked = NetworkManager.Instance.IsNetworked;
        _localRole = networked ? GameManager.Instance.LocalRole : GameManager.Instance.SoloRole;

        GD.Print($"[Game] _Ready. networked={networked}, uid={Multiplayer.GetUniqueId()}, " +
                 $"role={_localRole}.");

        ApplyRole();

        // Warnings are computed against the authoritative track, so only the server does it.
        _track.TilePlaced += OnTilePlaced;

        if (!networked)
        {
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
        GameManager.Instance.ReportSceneReady();
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
    }

    /// <summary>Every peer is in the scene, so spawned cars will reach all of them.</summary>
    private void OnAllPeersReady()
    {
        GameManager.Instance.AllPeersReady -= OnAllPeersReady;
        CallDeferred(nameof(SpawnNetworkedRacers));
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
    private void OnTilePlaced(int trackIndex, int hazard)
    {
        if (NetworkManager.Instance.IsNetworked && !Multiplayer.IsServer())
            return;

        int warnIndex = trackIndex - RacerController.WarningLookahead;
        if (warnIndex < 0)
            return;

        foreach (Node child in _arena.Racers.GetChildren())
        {
            if (child is not RacerController car)
                continue;

            PlacedTile? on = _track.Grid.TileAtWorld(car.GlobalPosition);
            if (on?.Index == warnIndex)
                car.ServerSendHazardWarning(trackIndex, (TileHazard)hazard);
        }
    }

    /// <summary>Debug: what actually ended up in the tree / on screen after spawning.</summary>
    private void ReportRenderState()
    {
        Camera3D current = GetViewport().GetCamera3D();
        GD.Print($"[Game] Racers in tree: {_arena.Racers.GetChildCount()}. " +
                 $"Track tiles: {_track.Grid.Count}, head {_track.Grid.HeadCell} " +
                 $"heading {_track.Grid.HeadDirection.DisplayName()}. Current camera: " +
                 (current != null ? current.GetPath() : "<none>"));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Quick escape hatch while we iterate (fullscreen makes this handy).
        if (@event.IsActionPressed("ui_cancel"))
            GetTree().Quit();
    }
}
