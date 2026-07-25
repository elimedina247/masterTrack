using Godot;
using MasterTrack.Networking;

namespace MasterTrack.UI;

/// <summary>
/// The entry screen. Lets the player Host, Join, or jump straight into a solo Test Drive.
/// Networking rules live in the autoloaded managers — this is just wiring buttons to them
/// and reflecting connection/role state back to the player.
/// </summary>
public partial class MainMenu : Control
{
    private const string GameScenePath = "res://scenes/Game.tscn";

    private LineEdit _ipEdit = null!;
    private LineEdit _portEdit = null!;
    private Button _hostButton = null!;
    private Button _joinButton = null!;
    private Button _soloButton = null!;
    private Button _buildButton = null!;
    private Button _startButton = null!;
    private Label _statusLabel = null!;

    public override void _Ready()
    {
        _ipEdit = GetNode<LineEdit>("%IpEdit");
        _portEdit = GetNode<LineEdit>("%PortEdit");
        _hostButton = GetNode<Button>("%HostButton");
        _joinButton = GetNode<Button>("%JoinButton");
        _soloButton = GetNode<Button>("%SoloButton");
        _buildButton = GetNode<Button>("%BuildButton");
        _startButton = GetNode<Button>("%StartButton");
        _statusLabel = GetNode<Label>("%StatusLabel");

        _hostButton.Pressed += OnHostPressed;
        _joinButton.Pressed += OnJoinPressed;
        _soloButton.Pressed += OnSoloPressed;
        _buildButton.Pressed += OnBuildPressed;
        _startButton.Pressed += OnStartPressed;

        _startButton.Hide();

        // A fresh menu means no active session; the local player owns the mouse.
        Input.MouseMode = Input.MouseModeEnum.Visible;

        var net = NetworkManager.Instance;
        net.ServerCreated += OnServerCreated;
        net.ConnectedToServer += OnConnectedToServer;
        net.ConnectionFailed += OnConnectionFailed;
        net.ServerDisconnected += OnServerDisconnected;
        net.PlayerConnected += OnPlayerConnected;
        net.PlayerDisconnected += OnPlayerDisconnected;

        GameManager.Instance.GameStateChanged += OnGameStateChanged;
        GameManager.Instance.RoleAssigned += OnRoleAssigned;
    }

    private int Port => int.TryParse(_portEdit.Text, out int p) ? p : NetworkManager.DefaultPort;

    private void OnHostPressed()
    {
        if (NetworkManager.Instance.HostGame(Port) == Error.Ok)
        {
            SetStatus("Hosting — waiting for racers to join...");
            LockLobbyButtons();
        }
        else
        {
            SetStatus("Failed to host (port in use?).");
        }
    }

    private void OnJoinPressed()
    {
        string address = string.IsNullOrWhiteSpace(_ipEdit.Text) ? "127.0.0.1" : _ipEdit.Text;
        if (NetworkManager.Instance.JoinGame(address, Port) == Error.Ok)
        {
            SetStatus($"Connecting to {address}:{Port} ...");
            LockLobbyButtons();
        }
        else
        {
            SetStatus("Failed to start connection.");
        }
    }

    private void OnSoloPressed()
    {
        // No peer created -> Game.tscn detects solo mode and spawns one local car.
        GameManager.Instance.SoloRole = PlayerRole.Racer;
        GetTree().ChangeSceneToFile(GameScenePath);
    }

    /// <summary>Jump straight to the Track Master's board, so the builder can be worked on alone.</summary>
    private void OnBuildPressed()
    {
        GameManager.Instance.SoloRole = PlayerRole.TrackMaster;
        GetTree().ChangeSceneToFile(GameScenePath);
    }

    private void OnStartPressed()
    {
        GameManager.Instance.StartMatch();
    }

    // ---- Network callbacks ----

    private void OnServerCreated()
    {
        // Host can start once at least one racer has joined; show the button now.
        _startButton.Show();
    }

    private void OnConnectedToServer() => SetStatus("Connected. Waiting for the host to start...");

    private void OnConnectionFailed()
    {
        SetStatus("Connection failed.");
        UnlockLobbyButtons();
    }

    private void OnServerDisconnected()
    {
        SetStatus("Disconnected from host.");
        UnlockLobbyButtons();
    }

    private void OnPlayerConnected(int peerId)
    {
        if (NetworkManager.Instance.IsHost)
            SetStatus($"Racer {peerId} joined. Press Start when ready.");
    }

    private void OnPlayerDisconnected(int peerId)
    {
        if (NetworkManager.Instance.IsHost)
            SetStatus($"Racer {peerId} left.");
    }

    private void OnRoleAssigned(int peerId, int role)
    {
        if (peerId == Multiplayer.GetUniqueId())
            SetStatus($"You are the {(PlayerRole)role}. Starting...");
    }

    private void OnGameStateChanged(int state)
    {
        if ((GameState)state == GameState.InRound)
            GetTree().ChangeSceneToFile(GameScenePath);
    }

    // ---- Helpers ----

    private void SetStatus(string text) => _statusLabel.Text = text;

    private void LockLobbyButtons()
    {
        _hostButton.Disabled = true;
        _joinButton.Disabled = true;
        _soloButton.Disabled = true;
        _buildButton.Disabled = true;
    }

    private void UnlockLobbyButtons()
    {
        _hostButton.Disabled = false;
        _joinButton.Disabled = false;
        _soloButton.Disabled = false;
        _buildButton.Disabled = false;
        _startButton.Hide();
    }
}
