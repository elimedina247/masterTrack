using Godot;

namespace MasterTrack.UI;

/// <summary>
/// The card that announces a phase to the Track Master: a title that fades up over the board,
/// holds, and fades away, taking itself with it.
///
/// It exists because the builder's job changes completely three times in one match and nothing
/// on screen used to say so. The tray quietly swaps rows, the hand stops dealing, a Fire button
/// starts mattering — all of it discoverable, none of it announced. A phase is the largest thing
/// that happens to this role, and it should arrive like one.
///
/// Deliberately not interactive and not a modal: <c>MouseFilterEnum.Ignore</c> throughout, so a
/// builder who already knows what phase 2 is can start rigging through the title while it is
/// still fading. Nothing here is allowed to cost a second of a phase it is only introducing.
///
/// Shown to the Track Master alone. The racers get their own, different, message from
/// <see cref="BuildPhasePanel"/> — they are waiting, not working, and "Phase 2. Set up traps" is
/// news about somebody else's screen.
/// </summary>
public partial class PhaseIntro : Control
{
    /// <summary>The big line. Set before adding to the tree.</summary>
    public string Title { get; set; } = "";

    /// <summary>Seconds of fade in, of hold, and of fade out. The hold carries most of it — a
    /// title that is legible for under a second reads as a flicker, not an announcement.</summary>
    private const float FadeInSeconds = 0.45f;
    private const float HoldSeconds = 1.7f;
    private const float FadeOutSeconds = 0.6f;

    public override void _Ready()
    {
        // Anchors AND offsets: a control built in code starts with a zero rect, and the preset
        // alone would compensate the offsets to preserve it. BuildPhasePanel's note applies here.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Modulate = new Color(1, 1, 1, 0);

        // Sat a third of the way down rather than dead centre: the middle of a board camera is
        // where the track is, and this is the one moment the builder most wants to look at it.
        var label = new Label
        {
            Text = Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0.0f,
            AnchorRight = 1.0f,
            AnchorTop = 0.28f,
            AnchorBottom = 0.28f,
            OffsetTop = -46,
            OffsetBottom = 46,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Both,
        };
        label.AddThemeFontSizeOverride("font_size", 56);
        label.AddThemeColorOverride("font_color", new Color(1.0f, 0.94f, 0.78f));
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        label.AddThemeConstantOverride("outline_size", 12);
        AddChild(label);

        Tween tween = CreateTween();
        tween.TweenProperty(this, "modulate:a", 1.0, FadeInSeconds)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenInterval(HoldSeconds);
        tween.TweenProperty(this, "modulate:a", 0.0, FadeOutSeconds)
             .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);

        // Owned by nobody once it has said its piece — the caller adds it and forgets it.
        tween.Finished += QueueFree;
    }
}
