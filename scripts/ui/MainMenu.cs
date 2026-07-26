using Godot;
using MasterTrack.Networking;

namespace MasterTrack.UI;

/// <summary>
/// The entry screen. Lets the player Host, Join, or jump straight into a solo Test Drive.
/// Networking rules live in the autoloaded managers — this is just wiring buttons to them
/// and reflecting connection state back to the player.
///
/// Once a session exists this screen is done: host and clients alike drop into the lobby and
/// wait there, driving, until the host starts the match. Nothing about roles or match flow is
/// decided here any more — see <see cref="MasterTrack.Game.PhysicsTestArea"/> and
/// <see cref="LobbyPanel"/>.
/// </summary>
public partial class MainMenu : Control
{
    private const string GameScenePath = "res://scenes/Game.tscn";

    /// <summary>The lobby, and the physics playground: open tarmac, grass and one of every tile.</summary>
    private const string LobbyScenePath = "res://scenes/TestArea.tscn";

    private LineEdit _ipEdit = null!;
    private LineEdit _portEdit = null!;
    private Button _hostButton = null!;
    private Button _joinButton = null!;
    private Button _soloButton = null!;
    private Button _buildButton = null!;
    private Label _statusLabel = null!;

    public override void _Ready()
    {
        _ipEdit = GetNode<LineEdit>("%IpEdit");
        _portEdit = GetNode<LineEdit>("%PortEdit");
        _hostButton = GetNode<Button>("%HostButton");
        _joinButton = GetNode<Button>("%JoinButton");
        _soloButton = GetNode<Button>("%SoloButton");
        _buildButton = GetNode<Button>("%BuildButton");
        _statusLabel = GetNode<Label>("%StatusLabel");

        _hostButton.Pressed += OnHostPressed;
        _joinButton.Pressed += OnJoinPressed;
        _soloButton.Pressed += OnSoloPressed;
        _buildButton.Pressed += OnBuildPressed;

        // A fresh menu means no active session; the local player owns the mouse.
        Input.MouseMode = Input.MouseModeEnum.Visible;

        var net = NetworkManager.Instance;
        net.ServerCreated += OnServerCreated;
        net.ConnectedToServer += OnConnectedToServer;
        net.ConnectionFailed += OnConnectionFailed;
        net.ServerDisconnected += OnServerDisconnected;

        ApplyCommandLine();
    }

    /// <summary>
    /// Drop the manager subscriptions on the way out.
    ///
    /// The managers are autoloads, so they outlive every scene. A C# <c>+=</c> handler is a
    /// managed delegate rather than a connection Godot can tie to a node's lifetime, so it is
    /// <i>not</i> cleaned up when this menu is freed — the host quitting would otherwise fire
    /// ServerDisconnected straight into a disposed Label.
    /// </summary>
    public override void _ExitTree()
    {
        var net = NetworkManager.Instance;
        net.ServerCreated -= OnServerCreated;
        net.ConnectedToServer -= OnConnectedToServer;
        net.ConnectionFailed -= OnConnectionFailed;
        net.ServerDisconnected -= OnServerDisconnected;
    }

    /// <summary>
    /// Drive the menu from the command line so a session can be brought up without hands on
    /// three keyboards. Same reasoning as <c>--role=</c> in GameManager: the networked paths are
    /// the ones that cannot be exercised from a single editor run, so they need a way in.
    /// <code>
    /// godot -- --host --autostart=2      # host; the lobby starts once two clients are in
    /// godot -- --join=127.0.0.1          # client
    /// </code>
    /// <c>--autostart=</c> is read by <see cref="LobbyPanel"/>, which owns the Start button.
    /// </summary>
    private void ApplyCommandLine()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.Equals("--host", System.StringComparison.OrdinalIgnoreCase))
                CallDeferred(nameof(OnHostPressed));
            else if (arg.StartsWith("--join=", System.StringComparison.OrdinalIgnoreCase))
            {
                _ipEdit.Text = arg["--join=".Length..];
                CallDeferred(nameof(OnJoinPressed));
            }
        }
    }

    private int Port => int.TryParse(_portEdit.Text, out int p) ? p : NetworkManager.DefaultPort;

    private void OnHostPressed()
    {
        if (NetworkManager.Instance.HostGame(Port) == Error.Ok)
        {
            SetStatus("Hosting — opening the lobby...");
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

    /// <summary>
    /// Drop straight into the physics playground. Without a session it brings its own car and
    /// HUD and shows no lobby, so it's purely for feeling out how the car drives.
    /// </summary>
    private void OnSoloPressed()
    {
        GetTree().ChangeSceneToFile(LobbyScenePath);
    }

    /// <summary>Jump straight to the Track Master's board, so the builder can be worked on alone.</summary>
    private void OnBuildPressed()
    {
        GameManager.Instance.SoloRole = PlayerRole.TrackMaster;
        GetTree().ChangeSceneToFile(GameScenePath);
    }

    // ---- Network callbacks ----

    /// <summary>
    /// Host and client both go straight to the lobby, on their own, the moment the session
    /// exists. Nobody waits on the menu: the point of the lobby is that you are already driving
    /// while the group assembles.
    /// </summary>
    private void OnServerCreated() => GetTree().ChangeSceneToFile(LobbyScenePath);

    private void OnConnectedToServer() => GetTree().ChangeSceneToFile(LobbyScenePath);

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
    }
}
