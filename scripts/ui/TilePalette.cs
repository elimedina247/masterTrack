using System.Collections.Generic;
using Godot;
using MasterTrack.Tiles;
using MasterTrack.TrackMaster;

namespace MasterTrack.UI;

/// <summary>
/// The Track Master's tile tray: a strip of the available tiles across the bottom of the
/// screen. Built from <see cref="TileCatalog"/> in code, so a new tile type appears here with
/// no UI work.
///
/// Picking a tile happens on mouse-<i>down</i>, not on click. That's what makes both natural
/// gestures work off one code path: drag a tile out of the tray and release over the board,
/// or click a tile and then click the board — either way the release over the board is what
/// places it (see <see cref="TrackMasterController"/>).
/// </summary>
[GlobalClass]
public partial class TilePalette : Control
{
    /// <summary>The builder to drive. Required.</summary>
    [Export] public TrackMasterController? Builder { get; set; }

    /// <summary>Height of the tray in pixels.</summary>
    [Export] public int TrayHeight { get; set; } = 132;

    private readonly List<PanelContainer> _cards = new();
    private Label _status = null!;
    private int _selectedIndex = -1;

    private static readonly Color CardIdle = new(0.12f, 0.13f, 0.16f, 0.92f);
    private static readonly Color CardHover = new(0.20f, 0.22f, 0.27f, 0.95f);
    private static readonly Color CardSelected = new(0.16f, 0.34f, 0.24f, 0.98f);

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        BuildStatusBar();
        BuildTray();

        if (Builder != null)
            Builder.HoverChanged += OnHoverChanged;
        else
            GD.PushWarning("[TilePalette] No Builder assigned; tiles can be picked but not placed.");
    }

    private void BuildStatusBar()
    {
        var bar = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            OffsetLeft = 24,
            OffsetTop = 24,
            OffsetRight = 560,
            OffsetBottom = 60,
        };
        bar.AddThemeStyleboxOverride("panel", Panel(new Color(0.10f, 0.11f, 0.14f, 0.80f)));
        AddChild(bar);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right" })
            margin.AddThemeConstantOverride(side, 12);
        bar.AddChild(margin);

        _status = new Label
        {
            Text = "Pick a tile below, then drop it on the highlighted cell.",
            VerticalAlignment = VerticalAlignment.Center,
        };
        margin.AddChild(_status);
    }

    private void BuildTray()
    {
        var tray = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            // Pinned across the bottom edge, whatever the window size.
            AnchorLeft = 0.0f,
            AnchorTop = 1.0f,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            OffsetTop = -TrayHeight,
            GrowHorizontal = GrowDirection.Both,
            GrowVertical = GrowDirection.Begin,
        };
        tray.AddThemeStyleboxOverride("panel", Panel(new Color(0.07f, 0.08f, 0.10f, 0.88f)));
        AddChild(tray);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        tray.AddChild(margin);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 10);
        margin.AddChild(row);

        for (int i = 0; i < TileCatalog.All.Count; i++)
            row.AddChild(BuildCard(TileCatalog.All[i], i));
    }

    private PanelContainer BuildCard(TileDefinition definition, int index)
    {
        var card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(132, 0),
            TooltipText = $"{definition.DisplayName}\n{definition.Description}",
            MouseFilter = MouseFilterEnum.Stop,
        };
        card.AddThemeStyleboxOverride("panel", Panel(CardIdle));

        // Handled here rather than with a Button so the pick happens on press, which is what
        // lets a drag out of the tray read as one continuous gesture.
        card.GuiInput += @event => OnCardInput(@event, index);
        card.MouseEntered += () => OnCardHover(index, true);
        card.MouseExited += () => OnCardHover(index, false);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 8);
        card.AddChild(margin);

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 6);
        margin.AddChild(box);

        box.AddChild(new ColorRect
        {
            Color = definition.Accent,
            CustomMinimumSize = new Vector2(0, 40),
            MouseFilter = MouseFilterEnum.Ignore,
        });

        var name = new Label
        {
            Text = definition.DisplayName,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        name.AddThemeFontSizeOverride("font_size", 15);
        box.AddChild(name);

        var hazard = new Label
        {
            Text = definition.Hazard == TileHazard.Straight ? "no hazard" : definition.Hazard.DisplayName(),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        hazard.AddThemeFontSizeOverride("font_size", 11);
        hazard.AddThemeColorOverride("font_color", new Color(0.68f, 0.70f, 0.76f));
        box.AddChild(hazard);

        _cards.Add(card);
        return card;
    }

    private void OnCardInput(InputEvent @event, int index)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            return;

        Select(index);
        // Not marking the event handled: the matching release still has to reach the builder
        // if the player drags out over the board.
    }

    private void OnCardHover(int index, bool entered)
    {
        if (index == _selectedIndex)
            return;

        _cards[index].AddThemeStyleboxOverride("panel", Panel(entered ? CardHover : CardIdle));
    }

    private void Select(int index)
    {
        _selectedIndex = index;
        Builder?.SelectTile(index);

        for (int i = 0; i < _cards.Count; i++)
            _cards[i].AddThemeStyleboxOverride("panel", Panel(i == index ? CardSelected : CardIdle));

        TileDefinition? definition = TileCatalog.At(index);
        if (definition != null)
            _status.Text = $"{definition.DisplayName} — drop it on the highlighted cell.";
    }

    private void OnHoverChanged(bool valid, string reason)
    {
        if (_selectedIndex < 0)
            return;

        _status.Text = reason;
        _status.AddThemeColorOverride("font_color",
            valid ? new Color(0.55f, 0.95f, 0.60f) : new Color(0.95f, 0.60f, 0.55f));
    }

    private static StyleBoxFlat Panel(Color color)
    {
        var style = new StyleBoxFlat { BgColor = color };
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(0);
        return style;
    }
}
