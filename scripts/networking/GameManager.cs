using Godot;
using System.Collections.Generic;

namespace MasterTrack.Networking;

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

    /// <summary>Tiles dealt to the Track Master at the start of each round.</summary>
    public const int TilesPerRound = 5;

    /// <summary>Server-side truth. On clients this is only populated by RPC.</summary>
    public readonly Dictionary<int, PlayerRole> Roles = new();

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

    public override void _Ready()
    {
        Instance = this;
        ApplyCommandLineRole();
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
    /// The host (peer 1) becomes the Track Master; everyone else races.
    /// </summary>
    public void StartMatch()
    {
        if (!NetworkManager.Instance.IsHost)
        {
            GD.PushWarning("[GameManager] StartMatch called on a non-host; ignored.");
            return;
        }

        Roles.Clear();

        int hostId = Multiplayer.GetUniqueId();
        AssignRole(hostId, PlayerRole.TrackMaster);
        foreach (int peerId in Multiplayer.GetPeers())
            AssignRole(peerId, PlayerRole.Racer);

        RoundNumber = 0;
        StartNextRound();
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
