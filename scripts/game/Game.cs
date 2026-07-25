using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using MasterTrack.Tiles;
using MasterTrack.TrackMaster;
using MasterTrack.UI;
using MasterTrack.Vehicles;

namespace MasterTrack.Game;

/// <summary>
/// The 3D world the match plays out in, and the place the two halves of the game are wired
/// together for whichever role this machine is playing.
///
/// - <b>Racer:</b> a car, a chase camera and the driving HUD.
/// - <b>Track Master:</b> the top-down board, the tile tray, and no car at all.
///
/// The host spawns a car per racer under <c>Racers</c> and the <see cref="MultiplayerSpawner"/>
/// replicates them. Tiles are not replicated as nodes — every peer rebuilds the track from the
/// confirmed placements (see <see cref="TrackController"/>).
///
/// Transform replication (a MultiplayerSynchronizer on each car) is the next step — today
/// each player drives their own car; remote cars won't move on other screens yet.
/// </summary>
public partial class Game : Node3D
{
    [Export] public PackedScene RacerScene = null!;

    private const string RacerScenePath = "res://scenes/Racer.tscn";

    /// <summary>
    /// Where racers line up, on the starting straight the track builds itself. Only just above
    /// the road: the suspension has ~0.15 m of travel, so dropping a car in from any height
    /// bottoms it out on landing.
    /// </summary>
    [Export] public Vector3 StartLine = new(0, 0.15f, 32);

    /// <summary>Gap between racers across the width of the road, in metres.</summary>
    [Export] public float RacerSpacing = 3.0f;

    private Node3D _racers = null!;
    private TrackController _track = null!;
    private TrackMasterController _builder = null!;
    private VehicleHud? _hud;
    private VehicleDebugOverlay? _debug;
    private TilePalette? _palette;

    private PlayerRole _localRole;

    public override void _Ready()
    {
        _racers = GetNode<Node3D>("Racers");
        _track = GetNode<TrackController>("Track");
        _builder = GetNode<TrackMasterController>("TrackMaster");
        _hud = GetNodeOrNull<VehicleHud>("HUD/VehicleHud");
        _debug = GetNodeOrNull<VehicleDebugOverlay>("HUD/VehicleDebug");
        _palette = GetNodeOrNull<TilePalette>("HUD/TilePalette");

        // Godot installs an implicit OfflineMultiplayerPeer for single-player, so "no peer"
        // isn't a reliable solo test. Real networked play always uses an ENetMultiplayerPeer.
        bool networked = Multiplayer.MultiplayerPeer is ENetMultiplayerPeer;
        _localRole = networked ? GameManager.Instance.LocalRole : GameManager.Instance.SoloRole;

        GD.Print($"[Game] _Ready. networked={networked}, uid={Multiplayer.GetUniqueId()}, " +
                 $"role={_localRole}.");

        ApplyRole();

        // Cars that arrive by replication are never seen by SpawnRacer, so watch the
        // container instead of only wiring up the ones we create ourselves.
        _racers.ChildEnteredTree += OnRacerEnteredTree;

        // Warnings are computed against the authoritative track, so only the server does it.
        _track.TilePlaced += OnTilePlaced;

        // Fall back to loading by path if the export wasn't bound in the scene.
        RacerScene ??= GD.Load<PackedScene>(RacerScenePath);
        if (RacerScene == null)
        {
            GD.PushError($"[Game] Could not load racer scene from {RacerScenePath}.");
            return;
        }

        if (!networked)
        {
            SpawnSolo();
            // One frame later, report the final render state so we can see what happened.
            CallDeferred(nameof(ReportRenderState));
            return;
        }

        // Only the server instantiates cars; the spawner mirrors them to clients. Deferred
        // so every peer has finished loading this scene before nodes start replicating.
        if (Multiplayer.IsServer())
            CallDeferred(nameof(SpawnNetworkedRacers));
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
        if (_localRole == PlayerRole.TrackMaster)
        {
            // An unowned car so the board has something on it to build ahead of. It takes no
            // input and never claims the camera.
            SpawnRacer(-1, 0, 1);
            return;
        }

        SpawnRacer(Multiplayer.GetUniqueId(), 0, 1);
    }

    private void SpawnNetworkedRacers()
    {
        int racerCount = 0;
        foreach (var kvp in GameManager.Instance.Roles)
        {
            if (kvp.Value == PlayerRole.Racer)
                racerCount++;
        }

        int slot = 0;
        foreach (var kvp in GameManager.Instance.Roles)
        {
            if (kvp.Value != PlayerRole.Racer)
                continue;
            SpawnRacer(kvp.Key, slot++, racerCount);
        }
    }

    private void SpawnRacer(int peerId, int slot, int totalRacers)
    {
        var car = RacerScene.Instantiate<RacerController>();
        // Name = peer id so the spawner-replicated copies on clients can recover ownership.
        car.Name = peerId.ToString();
        car.OwnerPeerId = peerId;
        // Centre the grid on the road rather than running it off the right-hand edge.
        float offset = (slot - (totalRacers - 1) * 0.5f) * RacerSpacing;
        car.Position = StartLine + new Vector3(offset, 0, 0);
        _racers.AddChild(car, forceReadableName: true);
        GD.Print($"[Game] Spawned racer for peer {peerId} at {car.Position} (local={car.IsLocalPlayer}).");
    }

    /// <summary>
    /// Point the HUD and debug overlay at this machine's car. Deferred by a frame because
    /// a replicated car recovers its owner id in _Ready, which hasn't run yet at this point.
    /// </summary>
    private void OnRacerEnteredTree(Node node)
    {
        if (node is not RacerController car)
            return;

        Callable.From(() =>
        {
            if (!IsInstanceValid(car) || !car.IsLocalPlayer)
                return;

            if (_hud != null)
                _hud.VehicleNode = car;
            if (_debug != null)
                _debug.VehicleNode = car;
        }).CallDeferred();
    }

    /// <summary>
    /// Server only. A tile landed — tell any racer it landed exactly
    /// <see cref="RacerController.WarningLookahead"/> tiles in front of, and nobody else.
    /// After the warning fades it's on them to remember it.
    /// </summary>
    private void OnTilePlaced(int trackIndex, int hazard)
    {
        if (Multiplayer.MultiplayerPeer is ENetMultiplayerPeer && !Multiplayer.IsServer())
            return;

        int warnIndex = trackIndex - RacerController.WarningLookahead;
        if (warnIndex < 0)
            return;

        foreach (Node child in _racers.GetChildren())
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
        GD.Print($"[Game] Racers in tree: {_racers.GetChildCount()}. " +
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
