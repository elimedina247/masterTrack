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

    /// <summary>
    /// How many <i>clients</i> may connect — this is passed straight to ENet's max-peers, which
    /// counts connections, and the host is not one of them. So this is one less than the number
    /// of people in the session: 6 clients plus the host is 7, which is exactly the seven rainbow
    /// colours in <see cref="MasterTrack.Racer.CarVariants.Palette"/>. Everyone in the lobby is
    /// driving, builder included, so the palette has to cover the whole room.
    /// </summary>
    public const int MaxPlayers = 6;

    /// <summary>Convenience singleton so other scripts can do NetworkManager.Instance.</summary>
    public static NetworkManager Instance { get; private set; } = null!;

    [Signal] public delegate void PlayerConnectedEventHandler(int peerId);
    [Signal] public delegate void PlayerDisconnectedEventHandler(int peerId);
    [Signal] public delegate void ServerCreatedEventHandler();
    [Signal] public delegate void ConnectionFailedEventHandler();
    [Signal] public delegate void ConnectedToServerEventHandler();
    [Signal] public delegate void ServerDisconnectedEventHandler();

    public bool IsHost => Multiplayer.MultiplayerPeer != null && Multiplayer.IsServer();

    /// <summary>
    /// Whether this machine is in a real session rather than playing solo.
    ///
    /// "No peer" is not the solo test: Godot installs an implicit
    /// <see cref="OfflineMultiplayerPeer"/> whenever the peer is cleared, and that peer reports
    /// itself connected with unique id 1 — indistinguishable from a host by every other measure.
    /// So the offline peer is what we actually have to rule out.
    ///
    /// Deliberately not a check against a concrete peer type. A transport swap (Steam) replaces
    /// <see cref="ENetMultiplayerPeer"/> with a <see cref="MultiplayerPeerExtension"/>, and any
    /// <c>is ENetMultiplayerPeer</c> test would then quietly decide a genuinely networked game was
    /// solo — every peer simulating every car and diverging, with nothing to say it had happened.
    /// </summary>
    public bool IsNetworked
    {
        get
        {
            MultiplayerPeer? peer = Multiplayer.MultiplayerPeer;
            return peer is not null
                   && peer is not OfflineMultiplayerPeer
                   && peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Disconnected;
        }
    }

    public override void _Ready()
    {
        Instance = this;

        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += () => EmitSignal(SignalName.ConnectedToServer);
        Multiplayer.ConnectionFailed += () => EmitSignal(SignalName.ConnectionFailed);
        Multiplayer.ServerDisconnected += () => EmitSignal(SignalName.ServerDisconnected);
    }

    /// <summary>How a session is carried.</summary>
    public enum Transport
    {
        /// <summary>Raw UDP. Needs an IP everyone can reach — a tailnet, or port forwarding.</summary>
        ENet,

        /// <summary>Steam's relay. No IPs, no port forwarding, but everyone needs Steam running.</summary>
        Steam,
    }

    /// <summary>Which transport the next Host/Join uses. Chosen on the main menu.</summary>
    public Transport Mode { get; set; } = Transport.ENet;

    /// <summary>
    /// Whether an entry in the Join field is an address rather than a SteamID. A SteamID64 is a
    /// bare 17-digit number; anything with a dot or a colon in it is an IPv4 or IPv6 address.
    /// </summary>
    public static bool LooksLikeAddress(string entry) =>
        entry.Contains('.') || entry.Contains(':');

    /// <summary>Start hosting. The host is peer id 1 and is the game authority.</summary>
    public Error HostGame(int port = DefaultPort)
    {
        MultiplayerPeer peer;

        if (Mode == Transport.Steam)
        {
            var steam = new SteamMultiplayerPeer();
            Error steamErr = steam.Host((ushort)port);
            if (steamErr != Error.Ok)
            {
                GD.PushError($"[NetworkManager] Failed to open a Steam socket: {steamErr}");
                return steamErr;
            }

            peer = steam;
            GD.Print($"[NetworkManager] Hosting over Steam. Friends join with {steam.HostSteamId.Value}, " +
                     $"or by address on port {port}.");
        }
        else
        {
            var enet = new ENetMultiplayerPeer();
            Error enetErr = enet.CreateServer(port, MaxPlayers);
            if (enetErr != Error.Ok)
            {
                GD.PushError($"[NetworkManager] Failed to create server: {enetErr}");
                return enetErr;
            }

            peer = enet;
            GD.Print($"[NetworkManager] Hosting on port {port}.");
        }

        Multiplayer.MultiplayerPeer = peer;
        EmitSignal(SignalName.ServerCreated);
        return Error.Ok;
    }

    /// <summary>
    /// Connect to a host. <paramref name="address"/> is an IP for ENet and the host's SteamID64
    /// for Steam — the thing they read off their own lobby and send you.
    /// </summary>
    public Error JoinGame(string address, int port = DefaultPort)
    {
        MultiplayerPeer peer;

        if (Mode == Transport.Steam)
        {
            var steam = new SteamMultiplayerPeer();
            Error steamErr;
            string entry = address.Trim();

            // The entry decides the route, so one field serves both: a friend's SteamID goes
            // through the relay, an address goes straight to the host's local socket. That is
            // what makes two windows on one machine work without a special mode.
            if (LooksLikeAddress(entry))
            {
                steamErr = steam.ConnectToAddress(entry, (ushort)port);
            }
            else if (ulong.TryParse(entry, out ulong hostId))
            {
                steamErr = steam.ConnectToHost(hostId);
            }
            else
            {
                GD.PushError($"[NetworkManager] '{entry}' is neither a SteamID nor an address.");
                return Error.InvalidParameter;
            }

            if (steamErr != Error.Ok)
            {
                GD.PushError($"[NetworkManager] Failed to open a Steam connection: {steamErr}");
                return steamErr;
            }

            peer = steam;
            GD.Print($"[NetworkManager] Connecting over Steam to {address} ...");
        }
        else
        {
            var enet = new ENetMultiplayerPeer();
            Error enetErr = enet.CreateClient(address, port);
            if (enetErr != Error.Ok)
            {
                GD.PushError($"[NetworkManager] Failed to create client: {enetErr}");
                return enetErr;
            }

            peer = enet;
            GD.Print($"[NetworkManager] Connecting to {address}:{port} ...");
        }

        Multiplayer.MultiplayerPeer = peer;
        return Error.Ok;
    }

    /// <summary>Tear down the current session and return to a disconnected state.</summary>
    public void Disconnect()
    {
        // Close() is on the base peer, so this shuts down whatever transport is in use.
        // Harmless on the implicit offline peer, which is all there is when we were never
        // connected in the first place.
        Multiplayer.MultiplayerPeer?.Close();

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
