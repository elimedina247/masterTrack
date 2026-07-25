using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;

namespace MasterTrack.Game;

/// <summary>
/// The 3D world the match plays out in. Responsible for putting racer cars into the scene:
/// - Solo test drive (no network): one local car so you can feel the driving + camera.
/// - Networked: the host (authority) spawns a car per racer under <c>Racers</c>, and the
///   <see cref="MultiplayerSpawner"/> replicates them to every peer. The Track Master (host)
///   gets a simple top-down camera for now.
///
/// Transform replication (a MultiplayerSynchronizer on each car) is the next step — today
/// each player drives their own car; remote cars won't move on other screens yet.
/// </summary>
public partial class Game : Node3D
{
    [Export] public PackedScene RacerScene = null!;

    private const string RacerScenePath = "res://scenes/Racer.tscn";

    /// <summary>Where racers line up; each gets an X offset from here.</summary>
    [Export] public Vector3 StartLine = new(0, 1, 0);
    [Export] public float RacerSpacing = 3.0f;

    private Node3D _racers = null!;

    public override void _Ready()
    {
        _racers = GetNode<Node3D>("Racers");

        string peerType = Multiplayer.MultiplayerPeer?.GetType().Name ?? "null";
        GD.Print($"[Game] _Ready. peerType={peerType}, uid={Multiplayer.GetUniqueId()}.");

        // Fall back to loading by path if the export wasn't bound in the scene.
        RacerScene ??= GD.Load<PackedScene>(RacerScenePath);
        if (RacerScene == null)
        {
            GD.PushError($"[Game] Could not load racer scene from {RacerScenePath}.");
            return;
        }

        // Godot installs an implicit OfflineMultiplayerPeer for single-player, so "no peer"
        // isn't a reliable solo test. Real networked play always uses an ENetMultiplayerPeer.
        bool networked = Multiplayer.MultiplayerPeer is ENetMultiplayerPeer;
        if (!networked)
        {
            SpawnSolo();
            // One frame later, report the final render state so we can see what happened.
            CallDeferred(nameof(ReportRenderState));
            return;
        }

        if (GameManager.Instance.LocalRole == PlayerRole.TrackMaster)
            EnableTopDownCamera();

        // Only the server instantiates cars; the spawner mirrors them to clients. Deferred
        // so every peer has finished loading this scene before nodes start replicating.
        if (Multiplayer.IsServer())
            CallDeferred(nameof(SpawnNetworkedRacers));
    }

    private void SpawnSolo()
    {
        int localId = Multiplayer.GetUniqueId();
        SpawnRacer(localId, 0);
    }

    private void SpawnNetworkedRacers()
    {
        int slot = 0;
        foreach (var kvp in GameManager.Instance.Roles)
        {
            if (kvp.Value != PlayerRole.Racer)
                continue;
            SpawnRacer(kvp.Key, slot++);
        }
    }

    private void SpawnRacer(int peerId, int slot)
    {
        var car = RacerScene.Instantiate<RacerController>();
        // Name = peer id so the spawner-replicated copies on clients can recover ownership.
        car.Name = peerId.ToString();
        car.OwnerPeerId = peerId;
        car.Position = StartLine + new Vector3(slot * RacerSpacing, 0, 0);
        _racers.AddChild(car, forceReadableName: true);
        GD.Print($"[Game] Spawned racer for peer {peerId} at {car.Position} (local={car.IsLocalPlayer}).");
    }

    /// <summary>Debug: what actually ended up in the tree / on screen after spawning.</summary>
    private void ReportRenderState()
    {
        Camera3D current = GetViewport().GetCamera3D();
        GD.Print($"[Game] Racers in tree: {_racers.GetChildCount()}. Current camera: " +
                 (current != null ? current.GetPath() : "<none>"));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Quick escape hatch while we iterate (fullscreen makes this handy).
        if (@event.IsActionPressed("ui_cancel"))
            GetTree().Quit();
    }

    private void EnableTopDownCamera()
    {
        var cam = new Camera3D
        {
            Position = new Vector3(0, 40, 0),
            RotationDegrees = new Vector3(-90, 0, 0),
            Current = true,
        };
        AddChild(cam);
    }
}
