using Godot;
using MasterTrack.Networking;
using System.Collections.Generic;

namespace MasterTrack.UI;

/// <summary>
/// The lobby's overlay: who is here, how long the race will be, and — for the host — the button
/// that starts the match.
///
/// Start lives here rather than on the main menu because the host is out driving on the pad
/// with everyone else by the time they decide to begin, and the menu is two scenes behind them.
/// The race length is here for the same reason, and because it is the last thing anybody wants to
/// argue about: it has to be visible to everyone while they are still standing around, not
/// discovered when the tiles run out.
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
    private RichTextLabel _players = null!;
    private Label _hint = null!;
    private Button _start = null!;

    /// <summary>The host's race length picker. Only the host gets to touch it.</summary>
    private OptionButton _raceLength = null!;

    /// <summary>What everyone else sees in its place: the length, but not a control.</summary>
    private Label _raceLengthLabel = null!;

    /// <summary>Clients that trigger an automatic Start; 0 for the normal button. See MainMenu.</summary>
    private int _autoStartClients;

    /// <summary>So autostart fires once rather than on every redraw of the roster.</summary>
    private bool _autoStarted;

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

        // Rich text so each name can carry that player's colour. FitContent because it sits in
        // a VBox that has no idea how many people are in the room.
        _players = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _players.AddThemeFontSizeOverride("normal_font_size", 20);
        _players.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        _players.AddThemeConstantOverride("outline_size", 6);
        box.AddChild(_players);

        BuildRaceLength(box);

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

        // A client learns someone has joined before it learns what colour they are, so the list
        // has to be redrawn when the appearance lands as well.
        GameManager.Instance.AppearanceAssigned += OnAppearanceAssigned;
        GameManager.Instance.PlayerNameChanged += OnPlayerNameChanged;
        GameManager.Instance.RaceLengthChanged += OnRaceLengthChanged;

        Refresh();
    }

    /// <summary>
    /// How many tiles the race runs for. A short list of lengths rather than a free number: the
    /// question is "a quick one or a long one", and the host is standing on a start line, not
    /// filling in a form.
    ///
    /// The picker and the plain label are the same row in two states — the host sets it, everyone
    /// else watches it — so a client can still see what they are about to be signed up for.
    /// </summary>
    private void BuildRaceLength(Node parent)
    {
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 8);
        parent.AddChild(row);

        Label caption = AddLabel(row, 18);
        caption.Text = "Race length";

        _raceLengthLabel = AddLabel(row, 18);

        _raceLength = new OptionButton
        {
            // The host's hands are on the keyboard driving around the pad; a focused OptionButton
            // would open itself on the space bar.
            FocusMode = FocusModeEnum.None,
        };
        _raceLength.AddThemeFontSizeOverride("font_size", 18);

        foreach (int tiles in GameManager.RaceLengthChoices)
            _raceLength.AddItem($"{tiles} tiles", tiles);

        _raceLength.ItemSelected += OnRaceLengthPicked;
        row.AddChild(_raceLength);
    }

    /// <summary>The id carries the length, so the list can be reordered without this caring.</summary>
    private void OnRaceLengthPicked(long index)
        => GameManager.Instance.SetRaceLength(_raceLength.GetItemId((int)index));

    private void OnRaceLengthChanged(int tiles) => Refresh();

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
        GameManager.Instance.AppearanceAssigned -= OnAppearanceAssigned;
        GameManager.Instance.PlayerNameChanged -= OnPlayerNameChanged;
        GameManager.Instance.RaceLengthChanged -= OnRaceLengthChanged;
    }

    private void OnAppearanceAssigned(int peerId, int variant, int colour) => Refresh();

    private void OnPlayerNameChanged(int peerId, string name) => Refresh();

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

        // Each name in the colour of that player's car, so the list and the pad outside the
        // window are read the same way. BBCode rather than a label each: the roster is rebuilt
        // on every join and leave.
        var lines = new List<string>();
        foreach (int id in peers)
        {
            RacerAppearance appearance = GameManager.Instance.AppearanceOf(id);
            string who = id == Multiplayer.GetUniqueId() ? " (you)" : "";
            string host = id == 1 ? " — host" : "";
            lines.Add($"[color=#{appearance.Paint.ToHtml(false)}]" +
                      $"{GameManager.Instance.NameOf(id)}[/color]{who}{host}");
        }

        _players.Text = string.Join("\n", lines);

        bool isHost = NetworkManager.Instance.IsHost;
        bool enoughPlayers = peers.Count >= MinPlayersToStart;

        int length = GameManager.Instance.RaceLength;
        _raceLength.Visible = isHost;
        _raceLengthLabel.Visible = !isHost;
        _raceLengthLabel.Text = $"{length} tiles";

        // Kept in step with whatever the manager actually holds rather than with what was last
        // clicked: the length can be clamped on its way in, and a picker showing a length nobody
        // is racing is worse than no picker.
        for (int i = 0; i < _raceLength.ItemCount; i++)
        {
            if (_raceLength.GetItemId(i) != length)
                continue;

            _raceLength.Selected = i;
            break;
        }

        _start.Visible = isHost;
        _start.Disabled = !enoughPlayers;

        if (!isHost)
            _hint.Text = "Waiting for the host to start...";
        else if (!enoughPlayers)
            _hint.Text = $"Need {MinPlayersToStart} players: one builds, the rest race.";
        else
            _hint.Text = "Drive around. Start when everyone is here.";

        if (isHost && !_autoStarted && _autoStartClients > 0
            && Multiplayer.GetPeers().Length >= _autoStartClients)
        {
            _autoStarted = true;
            CallDeferred(nameof(OnStartPressed));
        }
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
        // A deferred call can land a frame after the match has already begun and taken this
        // scene away with it. A detached Control has no Multiplayer API to ask about the
        // roster, so check we are still here before asking.
        if (!IsInsideTree() || Roster().Count < MinPlayersToStart)
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
