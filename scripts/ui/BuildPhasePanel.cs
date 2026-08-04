using Godot;
using MasterTrack.Networking;

namespace MasterTrack.UI;

/// <summary>
/// The build phase's overlay: what is happening and how long is left, for both roles — plus,
/// for the builder only, the button that ends the wait early. Builds its own controls the way
/// <see cref="LobbyPanel"/> does, so the match scene can spawn one with nothing to wire up.
///
/// The countdown is read straight off <see cref="GameManager.BuildSecondsLeft"/> every frame
/// rather than replicated on its own: the deadline was set once by the phase broadcast, and a
/// label is not a reason for more traffic.
/// </summary>
public partial class BuildPhasePanel : Control
{
    /// <summary>Whether this machine gets the button that ends the phase early. The builder does.</summary>
    public bool ShowDoneButton { get; set; }

    /// <summary>Which phase this panel is describing. Set before adding to the tree — the two
    /// timed phases wear the same furniture and differ only in what the words say.</summary>
    public MatchPhase Phase { get; set; } = MatchPhase.Building;

    private Label _timer = null!;
    private Button _done = null!;

    /// <summary>What the caption says, for each phase and each role. The racers' half is
    /// deliberately vague about the rig: "setting traps" tells them a thing is being done to
    /// them, and telling them <i>what</i> is the sentry's job to do with a spring, later.</summary>
    private string Caption => (Phase, ShowDoneButton) switch
    {
        (MatchPhase.Rigging, true) => "Set your traps.",
        (MatchPhase.Rigging, false) => "The Track Master is setting traps...",
        (_, true) => "Build the track!",
        (_, false) => "The Track Master is building the track...",
    };

    private string DoneText => Phase == MatchPhase.Rigging ? "Start the race" : "Done building";

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        // Anchors AND offsets. SetAnchorsPreset alone compensates the offsets to preserve the
        // control's current rect — and a control built in code starts with a zero rect, so it
        // would stay zero-sized with the anchors dutifully set. A control authored in a scene
        // never sees the difference, which is why LobbyPanel gets away with the short form.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var box = new VBoxContainer
        {
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            OffsetLeft = -240,
            OffsetRight = 240,
            OffsetTop = 18,
            GrowHorizontal = GrowDirection.Both,
            Alignment = BoxContainer.AlignmentMode.Begin,
        };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);

        Label caption = AddLabel(box, 24);
        caption.Text = Caption;

        _timer = AddLabel(box, 32);

        if (ShowDoneButton)
        {
            _done = new Button
            {
                Text = DoneText,
                // The builder's hands are on the board; a focused button would eat the space bar.
                FocusMode = FocusModeEnum.None,
            };
            _done.AddThemeFontSizeOverride("font_size", 20);
            _done.Pressed += OnDonePressed;

            // Centred under the labels rather than stretched across the whole column.
            var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            row.AddChild(_done);
            box.AddChild(row);
        }
    }

    public override void _Process(double delta)
    {
        float seconds = GameManager.Instance.BuildSecondsLeft;
        _timer.Text = $"{(int)(seconds / 60.0f)}:{(int)seconds % 60:00}";
    }

    private void OnDonePressed()
    {
        // Disabled rather than hidden once pressed: the server ends the phase, not this button,
        // and until that lands the builder should see their click took.
        _done.Disabled = true;
        GameManager.Instance.RequestFinishBuilding();
    }

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
