using Godot;
using MasterTrack.Networking;
using MasterTrack.Racer;
using MasterTrack.Tiles;
using MasterTrack.Vehicles;
using System.Collections.Generic;

namespace MasterTrack.Game;

/// <summary>How cars are laid out when they spawn.</summary>
public enum RacerSpawnLayout
{
    /// <summary>A grid across the width of the road, centred on the arena's origin.</summary>
    StartLineRow,

    /// <summary>Spread around the pad, for a lobby where nobody is racing yet.</summary>
    ScatteredPad,
}

/// <summary>
/// Everything it takes to have cars in a scene: the container they live in, the
/// <see cref="MultiplayerSpawner"/> that mirrors them to the other peers, one car per racer, and
/// the HUD pointed at whichever of them is yours.
///
/// Both the lobby and the match need all of that, and the only thing they disagree about is
/// where the cars go — a start-line row versus spread around the pad — so that is the one knob
/// (<see cref="Layout"/>). Keeping it in one node means the lobby cannot drift out of step with
/// the match on the part that is hardest to test: whether a replicated car reaches everybody.
///
/// The container and spawner are built in code rather than authored into each scene, for the same
/// reason <see cref="RacerController"/> builds its synchronizer that way: a spawn path typo'd in
/// one of two .tscn files replicates nothing and says nothing about it.
/// </summary>
public partial class RacerArena : Node3D
{
    private const string RacerScenePath = "res://scenes/Racer.tscn";

    /// <summary>Keys in the spawn packet. Short, because they go over the wire on every spawn.</summary>
    private const string PeerKey = "p";
    private const string PositionKey = "x";
    private const string VariantKey = "v";
    private const string ColourKey = "c";
    private const string AntennaKey = "a";

    /// <summary>The car to spawn. Falls back to <see cref="RacerScenePath"/> if left unset.</summary>
    [Export] public PackedScene? RacerScene { get; set; }

    [Export] public RacerSpawnLayout Layout { get; set; } = RacerSpawnLayout.StartLineRow;

    /// <summary>
    /// Where the cars go, in this node's parent space. Only just above the surface: the
    /// suspension has ~0.15 m of travel, so dropping a car in from any height bottoms it out.
    /// </summary>
    [Export] public Vector3 Origin { get; set; } = new(0, 0.15f, 0);

    /// <summary>Gap between cars across the width of the road, in metres. Row layout only.</summary>
    [Export] public float Spacing { get; set; } = 3.0f;

    /// <summary>
    /// How far down the starting straight the row sits, measured in tiles so it stays on the
    /// back cell of that straight whatever the tile size is. Row layout only.
    /// </summary>
    [Export] public float StartLineTiles { get; set; } = 3.2f;

    /// <summary>Radius of the ring the lobby spreads cars around, in metres.</summary>
    [Export] public float ScatterRadius { get; set; } = 14.0f;

    /// <summary>
    /// The HUD root. Everything under it implementing <see cref="IVehicleObserver"/> is pointed
    /// at the local player's car once one exists. Optional — the Track Master has no car.
    /// </summary>
    [Export] public Node? Hud { get; set; }

    /// <summary>
    /// This machine's own car has spawned and knows who it belongs to. Fired on every peer for
    /// its own car — including the ones that arrive by replication, which the scene above never
    /// sees spawned and could otherwise only find by polling.
    /// </summary>
    [Signal] public delegate void LocalCarSpawnedEventHandler(RacerController car);

    /// <summary>The node cars are parented to. What the spawner replicates into.</summary>
    public Node3D Racers { get; private set; } = null!;

    private MultiplayerSpawner _spawner = null!;

    public override void _Ready()
    {
        Racers = new Node3D { Name = "Racers" };
        AddChild(Racers);

        _spawner = new MultiplayerSpawner
        {
            Name = "RacerSpawner",
            SpawnPath = "../Racers",
            // A custom spawn function rather than the scene list, because a car has to arrive
            // somewhere in particular. Letting the spawner replicate a bare scene instance sends
            // no transform with it, so every client's own car would start at the world origin —
            // and since a peer is the authority for its own car, nothing would ever correct it.
            SpawnFunction = Callable.From<Variant, Node>(BuildCar),
        };
        AddChild(_spawner);

        // Cars that arrive by replication are never seen by Spawn, so watch the container
        // instead of only wiring up the ones we create ourselves.
        Racers.ChildEnteredTree += OnRacerEnteredTree;

        RacerScene ??= GD.Load<PackedScene>(RacerScenePath);
        if (RacerScene == null)
            GD.PushError($"[RacerArena] Could not load the racer scene from {RacerScenePath}.");
    }

    /// <summary>
    /// Server only. One car per peer in the list, laid out according to <see cref="Layout"/>.
    /// Clients receive them through the spawner.
    /// </summary>
    public void SpawnFor(IReadOnlyList<int> peerIds)
    {
        for (int i = 0; i < peerIds.Count; i++)
            Spawn(peerIds[i], i, peerIds.Count);
    }

    /// <summary>
    /// Add one car owned by <paramref name="peerId"/>. <paramref name="slot"/> and
    /// <paramref name="total"/> only decide where it lands.
    /// </summary>
    public RacerController? Spawn(int peerId, int slot, int total)
    {
        // Carried in the spawn packet rather than looked up on arrival: the car is then built
        // right the first time on every peer, with no assumption about an appearance RPC having
        // landed before the spawn did.
        RacerAppearance appearance = GameManager.Instance.AppearanceOf(peerId);

        var data = new Godot.Collections.Dictionary
        {
            { PeerKey, peerId },
            { PositionKey, SpawnPoint(slot, total) },
            { VariantKey, appearance.VariantIndex },
            { ColourKey, appearance.ColourIndex },
            { AntennaKey, appearance.AntennaSpot },
        };

        // Goes through the spawner even when we are alone, so the local and networked paths are
        // the same one code path rather than two that can disagree.
        var car = _spawner.Spawn(data) as RacerController;
        if (car == null)
        {
            GD.PushError($"[RacerArena] Failed to spawn a car for peer {peerId}.");
            return null;
        }

        GD.Print($"[RacerArena] Spawned racer for peer {peerId} at {car.Position} " +
                 $"(local={car.IsLocalPlayer}).");
        return car;
    }

    /// <summary>
    /// Builds one car from the spawn packet. Runs on every peer, the server included, so the
    /// car is put together identically everywhere — in particular it is placed <i>before</i> it
    /// enters the tree, which is what stops a client's own car from starting at the origin.
    /// </summary>
    private Node BuildCar(Variant data)
    {
        var dict = data.AsGodotDictionary();

        RacerScene ??= GD.Load<PackedScene>(RacerScenePath);
        var car = RacerScene.Instantiate<RacerController>();
        car.PrepareForSpawn(dict[PeerKey].AsInt32(), dict[PositionKey].AsVector3(),
                            new RacerAppearance(dict[VariantKey].AsInt32(), dict[ColourKey].AsInt32(),
                                                dict[AntennaKey].AsInt32()));
        return car;
    }

    /// <summary>
    /// Server only. A peer now has a scene to put cars in, so re-ask every car already out there
    /// whether it may be shown to them. Held back until this point on purpose — see
    /// <c>RacerController.BuildSpawnGate</c> — and this is what lets them through.
    /// </summary>
    public void RevealTo(int peerId)
    {
        foreach (Node child in Racers.GetChildren())
            (child as RacerController)?.RefreshSpawnVisibility(peerId);
    }

    /// <summary>
    /// Server only. Take away the car belonging to a peer that has left. The spawner mirrors the
    /// removal, so nobody is left staring at an abandoned car parked on the pad.
    /// </summary>
    public void Despawn(int peerId)
    {
        foreach (Node child in Racers.GetChildren())
        {
            if (child is RacerController car && car.OwnerPeerId == peerId)
                car.QueueFree();
        }
    }

    private Vector3 SpawnPoint(int slot, int total) => Layout switch
    {
        // A ring rather than genuine scatter: it needs no RNG to stay collision-free, and a
        // lobby car that reappears in the same place is easier to make sense of than one that
        // does not. Alone on the pad there is nothing to avoid, so take the middle.
        RacerSpawnLayout.ScatteredPad when total <= 1 => Origin,
        RacerSpawnLayout.ScatteredPad =>
            Origin + new Vector3(Mathf.Cos(Mathf.Tau * slot / total), 0,
                                 Mathf.Sin(Mathf.Tau * slot / total)) * ScatterRadius,

        // Centred on the road rather than running off the right-hand edge.
        _ => Origin + new Vector3((slot - (total - 1) * 0.5f) * Spacing, 0,
                                  StartLineTiles * TileCatalog.TileSize),
    };

    /// <summary>
    /// Point the HUD at this machine's car. Deferred by a frame because a replicated car
    /// recovers its owner id in _Ready, which hasn't run yet at this point.
    /// </summary>
    private void OnRacerEnteredTree(Node node)
    {
        if (node is not RacerController car)
            return;

        Callable.From(() =>
        {
            // IsInstanceValid is not enough: a car can be out of the tree by the time this
            // runs — the scene changing underneath it is exactly that case — and asking a
            // detached node who owns it goes through a Multiplayer API it no longer has.
            if (!IsInstanceValid(car) || !car.IsInsideTree() || !car.IsLocalPlayer)
                return;

            BindHud(car);
            EmitSignal(SignalName.LocalCarSpawned, car);
        }).CallDeferred();
    }

    /// <summary>
    /// Hand the car to every overlay in the HUD at once. By interface rather than by name so
    /// adding an overlay to the HUD is all it takes to have it wired up.
    /// </summary>
    private void BindHud(Vehicle car)
    {
        if (Hud == null)
            return;

        if (Hud is IVehicleObserver root)
            root.VehicleNode = car;

        foreach (Node node in Hud.FindChildren("*", recursive: true, owned: false))
        {
            if (node is IVehicleObserver observer)
                observer.VehicleNode = car;
        }
    }
}
