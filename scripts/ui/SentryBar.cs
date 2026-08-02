using Godot;
using MasterTrack.Sentry;
using MasterTrack.TrackMaster;

namespace MasterTrack.UI;

/// <summary>
/// The sentry's toolbar, where the tile tray used to be: the points that are left, one button
/// per action, and a status line that says what the armed action wants clicked (and what the
/// server thought of the last request). Only ever exists on the builder's machine, and only
/// once the race phase opens.
///
/// Buttons arm; the board fires. Clicking "Bouncy!" doesn't spend anything — it puts the board
/// into targeting (see <see cref="TrackMasterController.ArmSentryAction"/>), and the points move
/// when the server confirms the click that follows. Buttons the ledger can't cover are disabled,
/// which is the cheap local half of the check; the server still holds the real one.
/// </summary>
public partial class SentryBar : Control
{
    /// <summary>The board that does the targeting. Required.</summary>
    public TrackMasterController? Board { get; set; }

    /// <summary>The points ledger and request pipe. Required.</summary>
    public SentryManager? Sentry { get; set; }

    private Label _points = null!;
    private Label _status = null!;
    private Button _camera = null!;

    private readonly System.Collections.Generic.List<(SentryActionKind Kind, Button Button)> _buttons = new();

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
            OffsetLeft = -430,
            OffsetRight = 430,
            OffsetTop = -190,
            OffsetBottom = -20,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Begin,
        };
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);

        _status = AddLabel(box, 18);

        // A flow rather than a box: the kit has outgrown one line, and a wrap is kinder than a
        // squeeze — the buttons keep their labels readable and simply take a second row.
        var row = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("h_separation", 12);
        row.AddThemeConstantOverride("v_separation", 8);
        box.AddChild(row);

        _points = AddLabel(row, 22);

        foreach (SentryActionKind kind in System.Enum.GetValues<SentryActionKind>())
        {
            var button = new Button
            {
                Text = $"{SentryActions.NameOf(kind)}  ({SentryActions.CostOf(kind)}p)",
                FocusMode = FocusModeEnum.None,
            };
            button.AddThemeFontSizeOverride("font_size", 18);

            SentryActionKind captured = kind;
            button.Pressed += () => Board?.ArmSentryAction((int)captured);

            row.AddChild(button);
            _buttons.Add((kind, button));
        }

        // The palette carries the camera toggle everywhere else, and the palette is gone once
        // the race is on — without this the sentry would be stuck in whatever mode the build
        // phase ended in.
        _camera = new Button { FocusMode = FocusModeEnum.None };
        _camera.AddThemeFontSizeOverride("font_size", 16);
        _camera.Pressed += () => Board?.ToggleCameraMode();
        row.AddChild(_camera);

        if (Sentry != null)
        {
            Sentry.PointsChanged += OnPointsChanged;
            Sentry.SentryMessage += OnMessage;
            OnPointsChanged(Sentry.PointsRemaining);
        }

        if (Board != null)
        {
            Board.SentryStatusChanged += OnMessage;
            Board.CameraModeChanged += OnCameraModeChanged;
            OnCameraModeChanged((int)Board.CameraMode);
        }
    }

    public override void _ExitTree()
    {
        if (Sentry != null)
        {
            Sentry.PointsChanged -= OnPointsChanged;
            Sentry.SentryMessage -= OnMessage;
        }

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

    private void OnPointsChanged(int remaining)
    {
        _points.Text = $"Points: {remaining}";

        foreach ((SentryActionKind kind, Button button) in _buttons)
            button.Disabled = remaining < SentryActions.CostOf(kind);
    }

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
