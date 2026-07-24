using Godot;

namespace MasterTrack.Networking;

/// <summary>
/// Autoload. Owns the transport layer: creating/joining an ENet session and tracking
/// peer connect/disconnect. Kept deliberately thin — game rules live in
/// <see cref="GameManager"/>. The host acts as the authoritative server.
/// </summary>
public partial class NetworkManager : Node
{
    public const int DefaultPort = 8910;
    public const int MaxPlayers = 8;

    /// <summary>Convenience singleton so other scripts can do NetworkManager.Instance.</summary>
    public static NetworkManager Instance { get; private set; } = null!;

    [Signal] public delegate void PlayerConnectedEventHandler(int peerId);
    [Signal] public delegate void PlayerDisconnectedEventHandler(int peerId);
    [Signal] public delegate void ServerCreatedEventHandler();
    [Signal] public delegate void ConnectionFailedEventHandler();
    [Signal] public delegate void ConnectedToServerEventHandler();
    [Signal] public delegate void ServerDisconnectedEventHandler();

    public bool IsHost => Multiplayer.MultiplayerPeer != null && Multiplayer.IsServer();

    public override void _Ready()
    {
        Instance = this;

        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += () => EmitSignal(SignalName.ConnectedToServer);
        Multiplayer.ConnectionFailed += () => EmitSignal(SignalName.ConnectionFailed);
        Multiplayer.ServerDisconnected += () => EmitSignal(SignalName.ServerDisconnected);
    }

    /// <summary>Start hosting. The host is peer id 1 and is the game authority.</summary>
    public Error HostGame(int port = DefaultPort)
    {
        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateServer(port, MaxPlayers);
        if (err != Error.Ok)
        {
            GD.PushError($"[NetworkManager] Failed to create server: {err}");
            return err;
        }

        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[NetworkManager] Hosting on port {port} (peer id {Multiplayer.GetUniqueId()}).");
        EmitSignal(SignalName.ServerCreated);
        return Error.Ok;
    }

    /// <summary>Connect to a host.</summary>
    public Error JoinGame(string address, int port = DefaultPort)
    {
        var peer = new ENetMultiplayerPeer();
        Error err = peer.CreateClient(address, port);
        if (err != Error.Ok)
        {
            GD.PushError($"[NetworkManager] Failed to create client: {err}");
            return err;
        }

        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[NetworkManager] Connecting to {address}:{port} ...");
        return Error.Ok;
    }

    /// <summary>Tear down the current session and return to a disconnected state.</summary>
    public void Disconnect()
    {
        if (Multiplayer.MultiplayerPeer is ENetMultiplayerPeer peer)
            peer.Close();

        Multiplayer.MultiplayerPeer = null;
        GD.Print("[NetworkManager] Disconnected.");
    }

    private void OnPeerConnected(long id)
    {
        GD.Print($"[NetworkManager] Peer connected: {id}");
        EmitSignal(SignalName.PlayerConnected, (int)id);
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"[NetworkManager] Peer disconnected: {id}");
        EmitSignal(SignalName.PlayerDisconnected, (int)id);
    }
}
