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

    /// <summary>What the address field holds for ENet, and for a second window on this machine.</summary>
    private const string DefaultAddress = "127.0.0.1";

    private LineEdit _nameEdit = null!;
    private LineEdit _ipEdit = null!;
    private LineEdit _portEdit = null!;
    private Button _hostButton = null!;
    private Button _joinButton = null!;
    private Button _soloButton = null!;
    private Button _buildButton = null!;
    private Label _statusLabel = null!;
    private CheckBox _steamCheck = null!;
    private Label _steamIdLabel = null!;

    public override void _Ready()
    {
        _nameEdit = GetNode<LineEdit>("%NameEdit");
        _ipEdit = GetNode<LineEdit>("%IpEdit");
        _portEdit = GetNode<LineEdit>("%PortEdit");
        _hostButton = GetNode<Button>("%HostButton");
        _joinButton = GetNode<Button>("%JoinButton");
        _soloButton = GetNode<Button>("%SoloButton");
        _buildButton = GetNode<Button>("%BuildButton");
        _statusLabel = GetNode<Label>("%StatusLabel");
        _steamCheck = GetNode<CheckBox>("%SteamCheck");
        _steamIdLabel = GetNode<Label>("%SteamIdLabel");

        _steamCheck.Toggled += OnSteamToggled;
        _steamCheck.Disabled = !SteamService.Instance.IsAvailable;
        _steamCheck.ButtonPressed = SteamService.Instance.IsAvailable;
        OnSteamToggled(_steamCheck.ButtonPressed);

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
    /// godot -- --host --autostart=2 --name=Eli   # host; starts once two clients are in
    /// godot -- --join=127.0.0.1 --name=Sam       # client
    /// </code>
    /// <c>--name=</c> is read before Host/Join fire, since both send it on from there.
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
            else if (arg.StartsWith("--name=", System.StringComparison.OrdinalIgnoreCase))
                _nameEdit.Text = arg["--name=".Length..];
            else if (arg.Equals("--steam", System.StringComparison.OrdinalIgnoreCase))
                _steamCheck.ButtonPressed = true;
            else if (arg.Equals("--no-steam-transport", System.StringComparison.OrdinalIgnoreCase))
                _steamCheck.ButtonPressed = false;
        }
    }

    /// <summary>
    /// Steam or ENet, and what the Join field means as a result: over Steam you paste the host's
    /// SteamID, over ENet an IP. The host's own id is shown so they have something to send
    /// people — discovery is a copy-and-paste, not a lobby browser (App ID 480 is shared with
    /// every other Steamworks developer, so a lobby list would be full of strangers).
    /// </summary>
    private void OnSteamToggled(bool useSteam)
    {
        NetworkManager.Instance.Mode = useSteam
            ? NetworkManager.Transport.Steam
            : NetworkManager.Transport.ENet;

        if (!SteamService.Instance.IsAvailable)
        {
            _steamIdLabel.Text = $"Steam unavailable ({SteamService.Instance.UnavailableReason})";
            return;
        }

        _steamIdLabel.Text = useSteam
            ? $"Your Steam ID: {SteamService.Instance.LocalSteamId.Value}  (friends type this)"
            : "";

        // One field, either kind of entry — a friend's SteamID over the relay, or an address for
        // a second window on this machine. Saying so beats leaving "Host IP" over a box that now
        // wants seventeen digits.
        GetNode<Label>("%IpLabel").Text = useSteam ? "Host ID / IP" : "Host IP";

        // And empty it, because the entry now decides the route. Left pre-filled, someone pasting
        // a friend's ID over a leftover 127.0.0.1 who missed the box would quietly dial their own
        // machine and get a timeout with nothing to explain it. Only the default is cleared —
        // anything typed is left alone.
        if (useSteam && _ipEdit.Text == DefaultAddress)
            _ipEdit.Text = "";
        else if (!useSteam && _ipEdit.Text.Length == 0)
            _ipEdit.Text = DefaultAddress;

        _ipEdit.PlaceholderText = useSteam ? "Host's Steam ID" : DefaultAddress;
    }

    private int Port => int.TryParse(_portEdit.Text, out int p) ? p : NetworkManager.DefaultPort;

    private void OnHostPressed()
    {
        // Set before the session exists: the host publishes its own name the moment the server
        // comes up, and a client sends it as soon as it is connected.
        GameManager.Instance.LocalPlayerName = _nameEdit.Text;

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
        GameManager.Instance.LocalPlayerName = _nameEdit.Text;

        // No silent fallback to localhost over Steam: an empty box there means they have not
        // pasted the host's ID yet, and quietly dialling their own machine tells them nothing.
        if (string.IsNullOrWhiteSpace(_ipEdit.Text)
            && NetworkManager.Instance.Mode == NetworkManager.Transport.Steam)
        {
            SetStatus("Paste the host's Steam ID into the Host ID / IP box first.");
            return;
        }

        string address = string.IsNullOrWhiteSpace(_ipEdit.Text) ? DefaultAddress : _ipEdit.Text;
        Error err = NetworkManager.Instance.JoinGame(address, Port);

        if (err == Error.Ok)
        {
            SetStatus($"Connecting to {address} ...");
            LockLobbyButtons();
            return;
        }

        // Specific, because the two ways this fails look identical from the outside and the fix
        // is different for each.
        SetStatus(err switch
        {
            Error.InvalidParameter when !NetworkManager.LooksLikeAddress(address.Trim())
                => "That is not a SteamID. Paste the host's ID, or type an IP for a local window.",
            Error.InvalidParameter
                => "Cannot join yourself over Steam. Use 127.0.0.1 for a second window here.",
            _ => "Failed to start connection.",
        });
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

    private void OnConnectedToServer()
    {
        // Before the scene change, so it is on its way while the lobby is still loading.
        GameManager.Instance.PublishLocalName();
        GetTree().ChangeSceneToFile(LobbyScenePath);
    }

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
