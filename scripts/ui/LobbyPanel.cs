using Godot;
using MasterTrack.Networking;
using System.Collections.Generic;

namespace MasterTrack.UI;

/// <summary>
/// The lobby's overlay: who is here, and — for the host — the button that starts the match.
///
/// Start lives here rather than on the main menu because the host is out driving on the pad
/// with everyone else by the time they decide to begin, and the menu is two scenes behind them.
///
/// Builds its own controls so it can be dropped into any scene with nothing to wire up, the way
/// <see cref="VehicleHud"/> does. Hides itself entirely when there is no session, so the same
/// scene still works as the solo Test Drive.
/// </summary>
[GlobalClass]
public partial class LobbyPanel : Control
{
    /// <summary>
    /// A match needs one builder and at least one racer, so a host on their own cannot start.
    /// </summary>
    public const int MinPlayersToStart = 2;

    private Label _title = null!;
    private Label _players = null!;
    private Label _hint = null!;
    private Button _start = null!;

    /// <summary>Clients that trigger an automatic Start; 0 for the normal button. See MainMenu.</summary>
    private int _autoStartClients;

    public override void _Ready()
    {
        // Pass clicks through the panel itself — only the button should catch the mouse.
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var box = new VBoxContainer
        {
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            OffsetLeft = -340,
            OffsetTop = 24,
            OffsetRight = -24,
            OffsetBottom = 420,
            GrowHorizontal = GrowDirection.Begin,
        };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);

        _title = AddLabel(box, 26);
        _players = AddLabel(box, 20);
        _hint = AddLabel(box, 18);

        _start = new Button { Text = "Start Match" };
        _start.AddThemeFontSizeOverride("font_size", 22);
        _start.Pressed += OnStartPressed;
        box.AddChild(_start);

        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--autostart=", System.StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg["--autostart=".Length..], out int clients))
                _autoStartClients = clients;
        }

        var net = NetworkManager.Instance;
        net.PlayerConnected += OnRosterChanged;
        net.PlayerDisconnected += OnRosterChanged;
        net.ServerDisconnected += Refresh;

        Refresh();
    }

    /// <summary>
    /// Autoloads outlive this scene, and a C# <c>+=</c> handler is a managed delegate Godot
    /// cannot tie to a node's lifetime — so it has to be taken back by hand or the next signal
    /// lands on a disposed control.
    /// </summary>
    public override void _ExitTree()
    {
        var net = NetworkManager.Instance;
        net.PlayerConnected -= OnRosterChanged;
        net.PlayerDisconnected -= OnRosterChanged;
        net.ServerDisconnected -= Refresh;
    }

    private void OnRosterChanged(int peerId) => Refresh();

    private void Refresh()
    {
        // No session means this is the solo Test Drive; there is no lobby to show.
        if (!NetworkManager.Instance.IsNetworked)
        {
            Visible = false;
            return;
        }

        Visible = true;

        List<int> peers = Roster();
        _title.Text = $"Lobby — {peers.Count} player{(peers.Count == 1 ? "" : "s")}";

        var lines = new List<string>();
        foreach (int id in peers)
        {
            string who = id == Multiplayer.GetUniqueId() ? " (you)" : "";
            string host = id == 1 ? " — host" : "";
            lines.Add($"Peer {id}{who}{host}");
        }

        _players.Text = string.Join("\n", lines);

        bool isHost = NetworkManager.Instance.IsHost;
        bool enoughPlayers = peers.Count >= MinPlayersToStart;

        _start.Visible = isHost;
        _start.Disabled = !enoughPlayers;

        if (!isHost)
            _hint.Text = "Waiting for the host to start...";
        else if (!enoughPlayers)
            _hint.Text = $"Need {MinPlayersToStart} players: one builds, the rest race.";
        else
            _hint.Text = "Drive around. Start when everyone is here.";

        if (isHost && _autoStartClients > 0 && Multiplayer.GetPeers().Length >= _autoStartClients)
            CallDeferred(nameof(OnStartPressed));
    }

    /// <summary>Everyone in the session, ourselves included, in a stable order.</summary>
    private List<int> Roster()
    {
        var peers = new List<int> { Multiplayer.GetUniqueId() };
        peers.AddRange(Multiplayer.GetPeers());
        peers.Sort();
        return peers;
    }

    private void OnStartPressed()
    {
        // Guarded because the deferred autostart call can land after someone drops out.
        if (Roster().Count < MinPlayersToStart)
            return;

        GameManager.Instance.StartMatch();
    }

    private static Label AddLabel(Node parent, int fontSize)
    {
        var label = new Label();
        label.AddThemeFontSizeOverride("font_size", fontSize);
        // An outline keeps the text readable over both tarmac and sky.
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        label.AddThemeConstantOverride("outline_size", 6);
        parent.AddChild(label);
        return label;
    }
}
