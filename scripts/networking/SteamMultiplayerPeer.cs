using Godot;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MasterTrack.Networking;

/// <summary>
/// A Godot <see cref="MultiplayerPeerExtension"/> carried over Steam's networking sockets, so a
/// session works between friends over the internet with no port forwarding, no VPN and no IP
/// addresses — Steam's relay does the NAT traversal.
///
/// It is a drop-in replacement for <see cref="ENetMultiplayerPeer"/>: everything above it, the
/// whole game, is unchanged. That is what 1.1 bought — nothing outside <see cref="NetworkManager"/>
/// asks what kind of peer it is any more.
///
/// <b>The host relays.</b> Clients hold exactly one connection, to the host, and anything aimed at
/// another client goes through them. This is not an optimisation, it is a requirement: a car's
/// <c>MultiplayerSynchronizer</c> is owned by the peer driving it, so every client is constantly
/// sending pose updates to every other client, and there are no client-to-client connections for
/// that to travel down.
///
/// <b>Peer ids are ours, not Steam's.</b> Godot's ids are 32-bit and a SteamId is 64, so the host
/// hands out 2, 3, 4… as people arrive and keeps the mapping. Clients learn their own id from the
/// <see cref="PacketKind.Welcome"/> that arrives right after connecting, and are not connected as
/// far as Godot is concerned until it does.
/// </summary>
public partial class SteamMultiplayerPeer : MultiplayerPeerExtension
{
    /// <summary>Virtual port. Only has to agree between host and client; nothing else uses it.</summary>
    public const int VirtualPort = 1;

    private const int ServerPeerId = 1;

    /// <summary>
    /// Steam's own send ceiling is 512 KiB. Reported a little under, since every packet carries
    /// <see cref="HeaderSize"/> bytes of ours on top of whatever Godot handed us.
    /// </summary>
    private const int MaxPacketSize = 512 * 1024 - 64;

    private enum PacketKind : byte
    {
        /// <summary>A Godot packet, with a target and a source.</summary>
        Data = 0,

        /// <summary>Host → one client: your id, and who else is already here.</summary>
        Welcome = 1,

        /// <summary>Host → clients: somebody arrived.</summary>
        PeerJoined = 2,

        /// <summary>Host → clients: somebody left.</summary>
        PeerLeft = 3,

        /// <summary>Client → host: I have processed the Welcome and can receive.</summary>
        Hello = 4,
    }

    // Data header: kind(1) + target(4) + source(4) + channel(1) + mode(1).
    private const int HeaderSize = 11;

    private sealed class Incoming
    {
        public required int From { get; init; }
        public required int Channel { get; init; }
        public required TransferModeEnum Mode { get; init; }
        public required byte[] Payload { get; init; }
    }

    private readonly Queue<Incoming> _incoming = new();

    // ---- Host state ----
    // Two listeners, one session. The relay is how friends reach you over the internet; the
    // plain UDP socket is how a second copy on this machine does, because Steam's relay cannot be
    // used to reach yourself — both ends would be the same account. Connections from either
    // arrive at the same callbacks and are indistinguishable from there on.
    private Listener? _relayListener;
    private Listener? _localListener;
    private readonly Dictionary<int, Connection> _clients = new();
    private readonly Dictionary<uint, int> _peerIdByConnection = new();

    /// <summary>Clients that have a connection and an id but have not checked in yet.</summary>
    private readonly HashSet<int> _pending = new();

    private int _nextPeerId = 2;

    // ---- Client state ----
    private Client? _client;

    private bool _isServer;
    private int _uniqueId;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    private bool _refuseConnections;

    private int _targetPeer;
    private int _transferChannel;
    private TransferModeEnum _transferMode = TransferModeEnum.Reliable;

    /// <summary>The host's Steam id, for showing on screen so a friend can be told what to type.</summary>
    public SteamId HostSteamId { get; private set; }

    // ---- Setup ----

    /// <summary>
    /// Start hosting. Opens Steam's relay for friends and a plain UDP socket on
    /// <paramref name="localPort"/> at the same time, so a second instance on this machine — or
    /// anyone on the LAN — can join by address while everyone else joins by SteamID.
    ///
    /// The relay is the one that has to work; the local socket failing is not fatal.
    /// </summary>
    public Error Host(ushort localPort)
    {
        _relayListener = SteamNetworkingSockets.CreateRelaySocket<Listener>(VirtualPort);
        if (_relayListener == null)
            return Error.CantCreate;

        _relayListener.Peer = this;
        HostSteamId = SteamClient.SteamId;

        _localListener = SteamNetworkingSockets.CreateNormalSocket<Listener>(NetAddress.AnyIp(localPort));
        if (_localListener != null)
            _localListener.Peer = this;
        else
            GD.PushWarning($"[SteamPeer] No local socket on port {localPort}; only SteamID joins will work.");

        BecomeServer();
        return Error.Ok;
    }

    /// <summary>Join a host by their Steam id, through the relay.</summary>
    public Error ConnectToHost(SteamId host)
    {
        if (host.Value == SteamClient.SteamId.Value)
        {
            // Worth naming rather than letting it time out: this is what happens when you try to
            // test two windows on one machine by pasting your own id.
            GD.PushError("[SteamPeer] Cannot connect to your own SteamID — the relay has no way " +
                         "to tell the two ends apart. Join by IP instead (127.0.0.1).");
            return Error.InvalidParameter;
        }

        _client = SteamNetworkingSockets.ConnectRelay<Client>(host, VirtualPort);
        if (_client == null)
            return Error.CantConnect;

        _client.Peer = this;
        HostSteamId = host;
        BecomeClient();
        return Error.Ok;
    }

    /// <summary>Join a host by address, over the plain UDP socket it also opened.</summary>
    public Error ConnectToAddress(string address, ushort port)
    {
        _client = SteamNetworkingSockets.ConnectNormal<Client>(NetAddress.From(address, port));
        if (_client == null)
            return Error.CantConnect;

        _client.Peer = this;
        BecomeClient();
        return Error.Ok;
    }

    private void BecomeServer()
    {
        _isServer = true;
        _uniqueId = ServerPeerId;
        // The host is connected the moment it is listening; there is nobody to agree that with.
        _status = ConnectionStatus.Connected;
    }

    private void BecomeClient()
    {
        _isServer = false;
        // Deliberately not Connected yet, and not until Welcome arrives: until we know our own
        // id there is nothing Godot could correctly do with us.
        _status = ConnectionStatus.Connecting;
    }

    // ---- Godot's peer contract ----

    public override void _Poll()
    {
        _relayListener?.Receive();
        _localListener?.Receive();
        _client?.Receive();
    }

    public override void _Close()
    {
        foreach (Connection connection in _clients.Values)
            connection.Close();

        _clients.Clear();
        _peerIdByConnection.Clear();
        _pending.Clear();
        _incoming.Clear();

        _relayListener?.Close();
        _localListener?.Close();
        _client?.Close();
        _relayListener = null;
        _localListener = null;
        _client = null;

        _status = ConnectionStatus.Disconnected;
        _uniqueId = 0;
    }

    public override void _DisconnectPeer(int peer, bool force)
    {
        if (!_isServer || !_clients.TryGetValue(peer, out Connection connection))
            return;

        connection.Close();
        DropPeer(peer);
    }

    public override int _GetUniqueId() => _uniqueId;

    public override bool _IsServer() => _isServer;

    /// <summary>True because <see cref="RelayFromClient"/> below actually implements it.</summary>
    public override bool _IsServerRelaySupported() => true;

    public override ConnectionStatus _GetConnectionStatus() => _status;

    public override int _GetMaxPacketSize() => MaxPacketSize;

    public override int _GetAvailablePacketCount() => _incoming.Count;

    // Peeked rather than popped: Godot asks who a packet is from, and on what channel, before it
    // asks for the packet itself.
    public override int _GetPacketPeer() => _incoming.Count > 0 ? _incoming.Peek().From : 0;

    public override int _GetPacketChannel() => _incoming.Count > 0 ? _incoming.Peek().Channel : 0;

    public override TransferModeEnum _GetPacketMode() =>
        _incoming.Count > 0 ? _incoming.Peek().Mode : TransferModeEnum.Reliable;

    public override byte[] _GetPacketScript() =>
        _incoming.Count > 0 ? _incoming.Dequeue().Payload : Array.Empty<byte>();

    public override void _SetTargetPeer(int peer) => _targetPeer = peer;

    public override void _SetTransferChannel(int channel) => _transferChannel = channel;

    public override int _GetTransferChannel() => _transferChannel;

    public override void _SetTransferMode(TransferModeEnum mode) => _transferMode = mode;

    public override TransferModeEnum _GetTransferMode() => _transferMode;

    public override void _SetRefuseNewConnections(bool enable) => _refuseConnections = enable;

    public override bool _IsRefusingNewConnections() => _refuseConnections;

    public override Error _PutPacketScript(byte[] buffer)
    {
        if (_status != ConnectionStatus.Connected)
            return Error.Unconfigured;

        byte[] framed = Frame(PacketKind.Data, _targetPeer, _uniqueId,
                              (byte)_transferChannel, (byte)_transferMode, buffer);

        // A client has one connection and no say in routing: everything goes to the host, which
        // reads the target out of the header and forwards it.
        if (!_isServer)
            return Send(_client?.Connection, framed, _transferMode);

        if (_targetPeer == 0)
            return SendToAll(framed, _transferMode, except: 0);

        if (_targetPeer < 0)
            return SendToAll(framed, _transferMode, except: -_targetPeer);

        return _clients.TryGetValue(_targetPeer, out Connection target)
            ? Send(target, framed, _transferMode)
            : Error.InvalidParameter;
    }

    // ---- Wire format ----

    private static byte[] Frame(PacketKind kind, int target, int source, byte channel, byte mode,
                                byte[] payload)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = (byte)kind;
        BitConverter.TryWriteBytes(packet.AsSpan(1, 4), target);
        BitConverter.TryWriteBytes(packet.AsSpan(5, 4), source);
        packet[9] = channel;
        packet[10] = mode;
        Buffer.BlockCopy(payload, 0, packet, HeaderSize, payload.Length);
        return packet;
    }

    private static Error Send(Connection? connection, byte[] data, TransferModeEnum mode)
    {
        if (connection == null)
            return Error.Unconfigured;

        Result result = connection.Value.SendMessage(data, ToSendType(mode));
        return result == Result.OK ? Error.Ok : Error.ConnectionError;
    }

    private Error SendToAll(byte[] data, TransferModeEnum mode, int except)
    {
        Error worst = Error.Ok;
        foreach ((int peerId, Connection connection) in _clients)
        {
            if (peerId == except)
                continue;

            Error err = Send(connection, data, mode);
            if (err != Error.Ok)
                worst = err;
        }

        return worst;
    }

    /// <summary>
    /// Steam has no unreliable-but-ordered mode, so ordered collapses to plain unreliable. The
    /// only thing sent that way is the car pose, which carries a full transform every time and is
    /// replaced by the next one — an update arriving late is worth no more than one dropped.
    /// </summary>
    private static SendType ToSendType(TransferModeEnum mode) => mode switch
    {
        TransferModeEnum.Reliable => SendType.Reliable,
        _ => SendType.Unreliable | SendType.NoNagle,
    };

    // ---- Receiving ----

    private void OnHostMessage(Connection connection, IntPtr data, int size)
    {
        byte[] packet = ToArray(data, size);
        if (packet.Length < 1)
            return;

        if (!_peerIdByConnection.TryGetValue(connection.Id, out int from))
            return;

        if ((PacketKind)packet[0] == PacketKind.Hello)
        {
            AnnouncePeer(from);
            return;
        }

        if ((PacketKind)packet[0] != PacketKind.Data || packet.Length < HeaderSize)
            return;

        int target = BitConverter.ToInt32(packet, 1);

        // Rewrite the source: a client could otherwise claim to be anyone simply by putting a
        // different number in the header, and everything above this trusts the sender id.
        BitConverter.TryWriteBytes(packet.AsSpan(5, 4), from);

        RelayFromClient(packet, from, target);
    }

    /// <summary>
    /// Work out who a client's packet was really for, deliver it here if we are one of them, and
    /// pass it on to anyone else it was addressed to.
    /// </summary>
    private void RelayFromClient(byte[] packet, int from, int target)
    {
        var mode = (TransferModeEnum)packet[10];

        bool forUs = target == ServerPeerId || target == 0 || (target < 0 && -target != ServerPeerId);
        if (forUs)
            Deliver(packet, from);

        if (target > 0 && target != ServerPeerId)
        {
            if (_clients.TryGetValue(target, out Connection direct))
                Send(direct, packet, mode);
            return;
        }

        if (target != 0 && target >= 0)
            return;

        // Broadcast, or all-but-one. Never back to the peer that sent it.
        int excluded = target < 0 ? -target : 0;
        foreach ((int peerId, Connection connection) in _clients)
        {
            if (peerId == from || peerId == excluded)
                continue;

            Send(connection, packet, mode);
        }
    }

    private void OnClientMessage(IntPtr data, int size)
    {
        byte[] packet = ToArray(data, size);
        if (packet.Length < 1)
            return;

        switch ((PacketKind)packet[0])
        {
            case PacketKind.Data:
                if (packet.Length >= HeaderSize)
                    Deliver(packet, BitConverter.ToInt32(packet, 5));
                return;

            case PacketKind.Welcome:
                OnWelcome(packet);
                return;

            case PacketKind.PeerJoined:
                if (packet.Length >= 5)
                    EmitSignal(MultiplayerPeer.SignalName.PeerConnected, BitConverter.ToInt32(packet, 1));
                return;

            case PacketKind.PeerLeft:
                if (packet.Length >= 5)
                    EmitSignal(MultiplayerPeer.SignalName.PeerDisconnected, BitConverter.ToInt32(packet, 1));
                return;
        }
    }

    /// <summary>
    /// Our id, and the room as it stands. Only now are we connected as far as Godot is concerned,
    /// and the host has to be announced like any other peer or the high-level API never learns it
    /// has a server to talk to.
    /// </summary>
    private void OnWelcome(byte[] packet)
    {
        if (packet.Length < 9)
            return;

        _uniqueId = BitConverter.ToInt32(packet, 1);
        _status = ConnectionStatus.Connected;

        // Answered before anything else, so the host can stop holding this peer back. Sent
        // straight down the connection rather than through _PutPacketScript, which is Godot's
        // channel and not ours.
        _client?.Connection.SendMessage(new[] { (byte)PacketKind.Hello }, SendType.Reliable);

        EmitSignal(MultiplayerPeer.SignalName.PeerConnected, ServerPeerId);

        int others = BitConverter.ToInt32(packet, 5);
        for (int i = 0; i < others; i++)
        {
            int offset = 9 + i * 4;
            if (offset + 4 > packet.Length)
                break;

            EmitSignal(MultiplayerPeer.SignalName.PeerConnected, BitConverter.ToInt32(packet, offset));
        }
    }

    private void Deliver(byte[] packet, int from)
    {
        var payload = new byte[packet.Length - HeaderSize];
        Buffer.BlockCopy(packet, HeaderSize, payload, 0, payload.Length);

        _incoming.Enqueue(new Incoming
        {
            From = from,
            Channel = packet[9],
            Mode = (TransferModeEnum)packet[10],
            Payload = payload,
        });
    }

    private static byte[] ToArray(IntPtr data, int size)
    {
        var buffer = new byte[size];
        Marshal.Copy(data, buffer, 0, size);
        return buffer;
    }

    // ---- Connection lifecycle, host side ----

    private void OnClientConnecting(Connection connection)
    {
        if (_refuseConnections)
        {
            connection.Close();
            return;
        }

        connection.Accept();
    }

    private void OnClientConnected(Connection connection)
    {
        int peerId = _nextPeerId++;
        _clients[peerId] = connection;
        _peerIdByConnection[connection.Id] = peerId;
        _pending.Add(peerId);

        // Their id and the room, before anyone is told about them — so a client that immediately
        // sends to another peer already knows that peer exists.
        var welcome = new byte[9 + (_clients.Count - 1) * 4];
        welcome[0] = (byte)PacketKind.Welcome;
        BitConverter.TryWriteBytes(welcome.AsSpan(1, 4), peerId);
        BitConverter.TryWriteBytes(welcome.AsSpan(5, 4), _clients.Count - 1);

        int index = 0;
        foreach (int existing in _clients.Keys)
        {
            if (existing == peerId)
                continue;

            BitConverter.TryWriteBytes(welcome.AsSpan(9 + index * 4, 4), existing);
            index++;
        }

        connection.SendMessage(welcome, SendType.Reliable);
        GD.Print($"[SteamPeer] Client accepted as peer {peerId}; waiting for it to check in.");
    }

    /// <summary>
    /// Host side. Only now — once the client has answered the Welcome — is the peer announced to
    /// Godot and to everybody else.
    ///
    /// The wait matters. Godot pushes every already-spawned node at a peer the moment it hears
    /// about it, and a client that has not finished connecting is still sitting on the main menu
    /// with no lobby in its tree. Those spawns arrive addressed to nodes it does not have, the
    /// path cache entry for the spawner fails to resolve, and every later spawn through that
    /// spawner fails with it — including, eventually, that client's own car. It looks like the
    /// player joined into a grey void, because that is exactly what they did.
    ///
    /// ENet gets away with announcing on connect because its handshake leaves both ends level.
    /// This transport learns of a client a round trip before the client knows its own id, so the
    /// wait has to be put back by hand.
    /// </summary>
    private void AnnouncePeer(int peerId)
    {
        if (!_pending.Remove(peerId))
            return;

        byte[] joined = new byte[5];
        joined[0] = (byte)PacketKind.PeerJoined;
        BitConverter.TryWriteBytes(joined.AsSpan(1, 4), peerId);
        foreach ((int other, Connection otherConnection) in _clients)
        {
            if (other != peerId)
                otherConnection.SendMessage(joined, SendType.Reliable);
        }

        GD.Print($"[SteamPeer] Peer {peerId} is ready.");
        EmitSignal(MultiplayerPeer.SignalName.PeerConnected, peerId);
    }

    private void OnClientDisconnected(Connection connection)
    {
        if (_peerIdByConnection.TryGetValue(connection.Id, out int peerId))
            DropPeer(peerId);
    }

    private void DropPeer(int peerId)
    {
        if (!_clients.TryGetValue(peerId, out Connection connection))
            return;

        _clients.Remove(peerId);
        _peerIdByConnection.Remove(connection.Id);

        // Dropped before it ever checked in, so nobody was told it existed and nobody needs
        // telling it is gone.
        if (_pending.Remove(peerId))
            return;

        byte[] left = new byte[5];
        left[0] = (byte)PacketKind.PeerLeft;
        BitConverter.TryWriteBytes(left.AsSpan(1, 4), peerId);
        foreach (Connection other in _clients.Values)
            other.SendMessage(left, SendType.Reliable);

        GD.Print($"[SteamPeer] Peer {peerId} disconnected.");
        EmitSignal(MultiplayerPeer.SignalName.PeerDisconnected, peerId);
    }

    private void OnServerLost()
    {
        _status = ConnectionStatus.Disconnected;
        GD.Print("[SteamPeer] Lost the host.");
    }

    // ---- Facepunch callback plumbing ----
    //
    // Facepunch constructs these itself with a new() constraint, so the peer is attached
    // afterwards rather than passed in. Nothing can arrive in between: messages only surface
    // from Receive(), which is only called from _Poll.

    private sealed class Listener : SocketManager
    {
        public SteamMultiplayerPeer Peer = null!;

        public override void OnConnecting(Connection connection, ConnectionInfo info) =>
            Peer.OnClientConnecting(connection);

        public override void OnConnected(Connection connection, ConnectionInfo info)
        {
            base.OnConnected(connection, info);
            Peer.OnClientConnected(connection);
        }

        public override void OnDisconnected(Connection connection, ConnectionInfo info)
        {
            base.OnDisconnected(connection, info);
            Peer.OnClientDisconnected(connection);
        }

        public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data,
                                       int size, long messageNum, long recvTime, int channel) =>
            Peer.OnHostMessage(connection, data, size);
    }

    private sealed class Client : ConnectionManager
    {
        public SteamMultiplayerPeer Peer = null!;

        public override void OnDisconnected(ConnectionInfo info)
        {
            base.OnDisconnected(info);
            Peer.OnServerLost();
        }

        public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) =>
            Peer.OnClientMessage(data, size);
    }
}
