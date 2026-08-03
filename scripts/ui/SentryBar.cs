using Godot;
using MasterTrack.Networking;
using MasterTrack.Sentry;
using MasterTrack.TrackMaster;

namespace MasterTrack.UI;

/// <summary>
/// The sentry's toolbar, where the tile tray used to be: the pool that refills, one button per
/// action grouped by how it is aimed, and a status line that says what the armed action wants
/// clicked (and what the server thought of the last request). Only ever exists on the builder's
/// machine, and only once the race phase opens.
///
/// Buttons arm; the board fires. Clicking "Bouncy!" doesn't spend anything — it puts the board
/// into targeting (see <see cref="TrackMasterController.ArmSentryAction"/>), and the points move
/// when the server confirms the click that follows. Buttons the ledger can't cover are dimmed
/// and disabled — the cheap local half of the check; the server still holds the real one — and
/// a button on cooldown counts itself back down with a draining bar under it.
///
/// Redrawn from <c>_Process</c> rather than from signals: the pool regenerates and the
/// cooldowns drain every frame anyway, so the bar's state is a function of "now" and reading it
/// each frame is simpler than keeping four signals honest. Eleven buttons of label checks is
/// nothing next to a frame of the game.
/// </summary>
public partial class SentryBar : Control
{
    /// <summary>The board that does the targeting. Required.</summary>
    public TrackMasterController? Board { get; set; }

    /// <summary>The points ledger and request pipe. Required.</summary>
    public SentryManager? Sentry { get; set; }

    private Label _points = null!;
    private ProgressBar _pool = null!;
    private Label _status = null!;
    private Button _camera = null!;

    /// <summary>One armed button: the kind, the controls, and the label it wears when ready.</summary>
    private readonly record struct ActionButton(
        SentryActionKind Kind, Button Button, ProgressBar Cooldown, string ReadyText);

    private readonly System.Collections.Generic.List<ActionButton> _buttons = new();

    /// <summary>How dim an unaffordable button goes. Dim-but-legible rather than hidden, so the
    /// sentry can see what they are saving toward.</summary>
    private static readonly Color Unaffordable = new(1.0f, 1.0f, 1.0f, 0.45f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        // Anchors AND offsets, for the same reason as BuildPhasePanel: a code-built control has
        // a zero rect, and the anchors-only call preserves it.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var box = new VBoxContainer
        {
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            AnchorTop = 1.0f,
            AnchorBottom = 1.0f,
            OffsetLeft = -470,
            OffsetRight = 470,
            OffsetTop = -280,
            OffsetBottom = -20,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Begin,
        };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);

        _status = AddLabel(box, 18);

        // The header: what is in the pool, how full it is, and the camera toggle. The bar makes
        // the regen legible — a number that creeps up reads as lag; a bar that fills reads as
        // income.
        var header = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        header.AddThemeConstantOverride("separation", 12);
        box.AddChild(header);

        _points = AddLabel(header, 22);

        _pool = new ProgressBar
        {
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(220, 14),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        header.AddChild(_pool);

        // The palette carries the camera toggle everywhere else, and the palette is gone once
        // the race is on — without this the sentry would be stuck in whatever mode the build
        // phase ended in.
        _camera = new Button { FocusMode = FocusModeEnum.None };
        _camera.AddThemeFontSizeOverride("font_size", 16);
        _camera.Pressed += () => Board?.ToggleCameraMode();
        header.AddChild(_camera);

        // One row per way of aiming. The category is the thing a sentry actually thinks in —
        // "who do I hit" against "where do I put it" — and eleven buttons in one flow stopped
        // being a toolbar and became a word search.
        BuildCategoryRow(box, SentryActionCategory.TargetCar, "Cars");
        BuildCategoryRow(box, SentryActionCategory.PlaceOnTrack, "Track");
        BuildCategoryRow(box, SentryActionCategory.Everyone, "All");

        if (Sentry != null)
            Sentry.SentryMessage += OnMessage;

        if (Board != null)
        {
            Board.SentryStatusChanged += OnMessage;
            Board.CameraModeChanged += OnCameraModeChanged;
            OnCameraModeChanged((int)Board.CameraMode);
        }
    }

    /// <summary>One aiming group: a short caption, then its buttons in a wrapping flow.</summary>
    private void BuildCategoryRow(Node parent, SentryActionCategory category, string caption)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        parent.AddChild(row);

        Label label = AddLabel(row, 15);
        label.Text = caption;
        label.CustomMinimumSize = new Vector2(52, 0);
        label.SelfModulate = new Color(1.0f, 1.0f, 1.0f, 0.65f);

        var flow = new HFlowContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        flow.AddThemeConstantOverride("h_separation", 10);
        flow.AddThemeConstantOverride("v_separation", 6);
        row.AddChild(flow);

        foreach (SentryActionKind kind in System.Enum.GetValues<SentryActionKind>())
        {
            if (SentryActions.CategoryOf(kind) != category)
                continue;

            flow.AddChild(BuildActionButton(kind));
        }
    }

    /// <summary>
    /// One action: the button with its hotkey and price in the label, and a thin bar under it
    /// that drains while the action reloads. A stack rather than a bare button so the cooldown
    /// reads at a glance without repainting the button itself.
    /// </summary>
    private Control BuildActionButton(SentryActionKind kind)
    {
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 1);

        string ready = $"{HotkeyLabel(kind)}  {SentryActions.NameOf(kind)} · {SentryActions.CostOf(kind)}p";
        var button = new Button
        {
            Text = ready,
            FocusMode = FocusModeEnum.None,
        };
        button.AddThemeFontSizeOverride("font_size", 17);

        SentryActionKind captured = kind;
        button.Pressed += () => Board?.ArmSentryAction((int)captured);
        stack.AddChild(button);

        var cooldown = new ProgressBar
        {
            ShowPercentage = false,
            MaxValue = 1.0,
            CustomMinimumSize = new Vector2(0, 4),
            // Kept in the layout while hidden, so a reload never reflows the row.
            Visible = false,
        };
        stack.AddChild(cooldown);

        _buttons.Add(new ActionButton(kind, button, cooldown, ready));
        return stack;
    }

    /// <summary>The key that arms this action — the same arithmetic the board's key handler
    /// runs, printed on the button so nobody has to be told twice.</summary>
    private static string HotkeyLabel(SentryActionKind kind)
    {
        int index = (int)kind;
        return index < 9 ? $"[{index + 1}]" : $"[S{index - 8}]";
    }

    /// <summary>The bar is a readout of "now": pool, prices, cooldowns, every frame.</summary>
    public override void _Process(double delta)
    {
        if (Sentry == null)
            return;

        int remaining = Sentry.PointsRemaining;
        int limit = GameManager.Instance.SentryPointLimit;

        _points.Text = $"{remaining} / {limit}";
        _pool.MaxValue = limit;
        _pool.Value = remaining;

        foreach (ActionButton entry in _buttons)
        {
            float left = Sentry.CooldownLeft(entry.Kind);
            bool affordable = remaining >= SentryActions.CostOf(entry.Kind);

            entry.Button.Disabled = left > 0.0f || !affordable;
            entry.Button.SelfModulate = affordable ? Colors.White : Unaffordable;

            string text = left > 0.0f ? $"{entry.ReadyText} · {left:0}s" : entry.ReadyText;
            if (entry.Button.Text != text)
                entry.Button.Text = text;

            entry.Cooldown.Visible = left > 0.0f;
            if (left > 0.0f)
                entry.Cooldown.Value = left / Mathf.Max(SentryActions.CooldownOf(entry.Kind), 0.01f);
        }
    }

    public override void _ExitTree()
    {
        if (Sentry != null)
            Sentry.SentryMessage -= OnMessage;

        if (Board != null)
        {
            Board.SentryStatusChanged -= OnMessage;
            Board.CameraModeChanged -= OnCameraModeChanged;
        }
    }

    private void OnCameraModeChanged(int mode) => _camera.Text =
        (TrackMasterController.BoardCameraMode)mode switch
        {
            TrackMasterController.BoardCameraMode.Follow => "Cam: Track",
            TrackMasterController.BoardCameraMode.Pack => "Cam: Racers",
            _ => "Cam: Free",
        };

    private void OnMessage(string text) => _status.Text = text;

    private static Label AddLabel(Node parent, int fontSize)
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        label.AddThemeConstantOverride("outline_size", 6);
        parent.AddChild(label);
        return label;
    }
}
