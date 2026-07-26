using System.Collections.Generic;
using Godot;
using MasterTrack.Tiles;
using MasterTrack.TrackMaster;

namespace MasterTrack.UI;

/// <summary>
/// The Track Master's tile tray: their <see cref="TileHand"/> drawn as a row of slots across
/// the bottom of the screen. It shows what they have, not what exists — the catalog is no
/// longer on screen, because they can't reach for it.
///
/// The tray is the whole placement interface: hovering a slot ghosts its tile onto the end of
/// the track, and clicking it puts it there. There's no held tile and no second click, because
/// the end of the track is the only place a tile can go (see <see cref="TrackMasterController"/>).
///
/// The slot at the end of the row carries the countdown to the next tile whenever there's room
/// for one. It sits still there because the hand keeps its tiles packed left, so the empties are
/// always one run on the right — and when the row is full the countdown disappears along with
/// the space it was filling, which is the whole message.
///
/// This also owns the rest of the Track Master's screen furniture — the status line and the
/// camera mode toggle — since the tray is the only HUD they have.
/// </summary>
[GlobalClass]
public partial class TilePalette : Control
{
    /// <summary>The builder to drive. Required.</summary>
    [Export] public TrackMasterController? Builder { get; set; }

    /// <summary>Height of the tray in pixels.</summary>
    [Export] public int TrayHeight { get; set; } = 132;

    /// <summary>The nodes making up one slot, so a refresh restyles rather than rebuilds.</summary>
    private sealed class SlotView
    {
        public required PanelContainer Card { get; init; }
        public required ColorRect Swatch { get; init; }
        public required ProgressBar Cooldown { get; init; }
        public required Label Name { get; init; }
        public required Label Hazard { get; init; }
    }

    private readonly List<SlotView> _slots = new();
    private Label _status = null!;
    private Button _cameraButton = null!;

    /// <summary>Slot the mouse is resting on, or -1. Tracked so the status line and the ghost
    /// can be re-aimed when the hand shifts under a cursor that hasn't moved.</summary>
    private int _hoveredSlot = -1;

    private const string IdleStatus = "Hover a tile to see it on the end of the track — click to place it.";

    private static readonly Color CardIdle = new(0.12f, 0.13f, 0.16f, 0.92f);
    private static readonly Color CardHover = new(0.20f, 0.22f, 0.27f, 0.95f);
    private static readonly Color EmptySwatch = new(0.09f, 0.10f, 0.12f, 0.9f);

    private static readonly Color StatusIdle = new(0.86f, 0.88f, 0.92f);
    private static readonly Color StatusValid = new(0.55f, 0.95f, 0.60f);
    private static readonly Color StatusInvalid = new(0.95f, 0.60f, 0.55f);

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        BuildStatusBar();
        BuildCameraToggle();
        BuildTray();

        if (Builder != null)
        {
            Builder.PreviewChanged += OnPreviewChanged;
            Builder.CameraModeChanged += OnCameraModeChanged;
            Builder.HandChanged += OnHandChanged;
            OnCameraModeChanged((int)Builder.CameraMode);
        }
        else
        {
            GD.PushWarning("[TilePalette] No Builder assigned; the tray is inert.");
        }
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
            Text = IdleStatus,
            VerticalAlignment = VerticalAlignment.Center,
        };
        margin.AddChild(_status);
    }

    /// <summary>
    /// The camera mode toggle, opposite the status line. Its label is driven by the builder's
    /// signal rather than flipped here, so the button can't drift out of step with the camera.
    /// </summary>
    private void BuildCameraToggle()
    {
        _cameraButton = new Button
        {
            // Never takes focus: with it focused, the space bar and Enter would re-press it,
            // and the Track Master's hands are on WASD.
            FocusMode = FocusModeEnum.None,
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            OffsetLeft = -260,
            OffsetTop = 24,
            OffsetRight = -24,
            OffsetBottom = 60,
            GrowHorizontal = GrowDirection.Begin,
        };
        // Same dark panels as the rest of the HUD; the default theme's button doesn't belong
        // on top of the board.
        _cameraButton.AddThemeStyleboxOverride("normal", Panel(new Color(0.10f, 0.11f, 0.14f, 0.80f)));
        _cameraButton.AddThemeStyleboxOverride("hover", Panel(new Color(0.18f, 0.20f, 0.25f, 0.90f)));
        _cameraButton.AddThemeStyleboxOverride("pressed", Panel(new Color(0.16f, 0.34f, 0.24f, 0.95f)));

        _cameraButton.Pressed += () => Builder?.ToggleCameraMode();
        AddChild(_cameraButton);
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

        // One card per slot, built once and then only ever restyled. The hand changes several
        // times a minute; rebuilding the tray each time would throw away the card the mouse is
        // resting on and drop its hover.
        int slots = Builder?.Hand.SlotCount ?? 0;
        for (int i = 0; i < slots; i++)
            row.AddChild(BuildSlot(i));

        RefreshSlots();
    }

    private PanelContainer BuildSlot(int slot)
    {
        var card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(132, 0),
            MouseFilter = MouseFilterEnum.Stop,
        };
        card.AddThemeStyleboxOverride("panel", Panel(CardIdle));

        // Handled here rather than with a Button so the tile lands on press: at racing speed
        // the wait for a release is felt.
        card.GuiInput += @event => OnSlotInput(card, @event, slot);
        card.MouseEntered += () => OnSlotHover(slot, true);
        card.MouseExited += () => OnSlotHover(slot, false);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 8);
        card.AddChild(margin);

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 6);
        margin.AddChild(box);

        var swatch = new ColorRect
        {
            CustomMinimumSize = new Vector2(0, 40),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(swatch);

        // Sits where the swatch does, so a slot either shows a tile's colour or the wait for
        // one — never both, and never a jump in the row's height as it switches.
        var cooldown = new ProgressBar
        {
            CustomMinimumSize = new Vector2(0, 40),
            MinValue = 0.0,
            MaxValue = 1.0,
            ShowPercentage = false,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        cooldown.AddThemeStyleboxOverride("background", Panel(new Color(0.06f, 0.07f, 0.09f, 0.9f)));
        cooldown.AddThemeStyleboxOverride("fill", Panel(new Color(0.30f, 0.45f, 0.62f, 0.95f)));
        box.AddChild(cooldown);

        var name = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        name.AddThemeFontSizeOverride("font_size", 15);
        box.AddChild(name);

        var hazard = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        hazard.AddThemeFontSizeOverride("font_size", 11);
        hazard.AddThemeColorOverride("font_color", new Color(0.68f, 0.70f, 0.76f));
        box.AddChild(hazard);

        _slots.Add(new SlotView
        {
            Card = card,
            Swatch = swatch,
            Cooldown = cooldown,
            Name = name,
            Hazard = hazard,
        });
        return card;
    }

    /// <summary>
    /// Repaint every slot from the hand. Called when the hand changes rather than every frame —
    /// the only thing that moves per-frame is the countdown bar, in <see cref="_Process"/>.
    /// </summary>
    private void RefreshSlots()
    {
        if (Builder == null)
            return;

        TileHand hand = Builder.Hand;
        int cooldownSlot = hand.CooldownSlot;

        for (int i = 0; i < _slots.Count; i++)
        {
            SlotView view = _slots[i];
            TileDefinition? definition = TileCatalog.At(hand.At(i));

            bool isCooldown = i == cooldownSlot;
            view.Cooldown.Visible = isCooldown;
            view.Swatch.Visible = !isCooldown;

            if (definition != null)
            {
                view.Swatch.Color = definition.Accent;
                view.Name.Text = definition.DisplayName;
                view.Hazard.Text = definition.Hazard == TileHazard.Straight
                    ? "no hazard"
                    : definition.Hazard.DisplayName();
                view.Card.TooltipText = $"{definition.DisplayName}\n{definition.Description}";
                continue;
            }

            // Empty. The one at the end of the row is the tile being dealt; the rest are just
            // room the Track Master hasn't been given anything for yet.
            view.Swatch.Color = EmptySwatch;
            view.Name.Text = isCooldown ? "next tile" : "";
            view.Hazard.Text = "";
            view.Card.TooltipText = isCooldown
                ? "The next tile is on its way here."
                : "An empty slot. Tiles arrive on their own.";
        }
    }

    public override void _Process(double delta)
    {
        if (Builder == null)
            return;

        int slot = Builder.Hand.CooldownSlot;
        if (slot >= 0 && slot < _slots.Count)
            _slots[slot].Cooldown.Value = Builder.Hand.DealProgress;
    }

    private void OnSlotInput(PanelContainer card, InputEvent @event, int slot)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            return;

        Builder?.PlaceFromSlot(slot);
        // The board has nothing left to do with this click, and letting it through would only
        // give the builder's camera a chance to react to it.
        card.AcceptEvent();
    }

    private void OnSlotHover(int slot, bool entered)
    {
        _slots[slot].Card.AddThemeStyleboxOverride("panel", Panel(entered ? CardHover : CardIdle));

        if (entered)
        {
            _hoveredSlot = slot;
            Builder?.PreviewSlot(slot);
            SayWhatIsUnderTheCursor();
            return;
        }

        if (_hoveredSlot == slot)
            _hoveredSlot = -1;

        Builder?.ClearPreview();
        SetStatus(IdleStatus, StatusIdle);
    }

    /// <summary>
    /// A tile was dealt or spent. Both can happen under a resting cursor — the hand closes up
    /// when a tile is spent, so the slot the mouse is on now holds something else — so the
    /// preview and the status line are re-aimed at whatever is there now.
    /// </summary>
    private void OnHandChanged()
    {
        RefreshSlots();

        if (_hoveredSlot >= 0)
            Builder?.PreviewSlot(_hoveredSlot);

        SayWhatIsUnderTheCursor();
    }

    /// <summary>
    /// The builder only speaks up when it has a tile to preview, so an empty slot under the
    /// cursor would otherwise leave the last tile's message sitting there.
    /// </summary>
    private void SayWhatIsUnderTheCursor()
    {
        if (Builder == null || _hoveredSlot < 0 || Builder.Hand.At(_hoveredSlot) != TileHand.Empty)
            return;

        SetStatus(_hoveredSlot == Builder.Hand.CooldownSlot
                  ? "Waiting on the next tile."
                  : "Nothing in this slot yet.", StatusIdle);
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color);
    }

    /// <summary>The builder previewed a tile on the head: say what clicking would do.</summary>
    private void OnPreviewChanged(bool valid, string reason)
        => SetStatus(reason, valid ? StatusValid : StatusInvalid);

    private void OnCameraModeChanged(int mode)
    {
        bool follow = (TrackMasterController.BoardCameraMode)mode
                      == TrackMasterController.BoardCameraMode.Follow;

        _cameraButton.Text = follow ? "Camera: Following track" : "Camera: Free roam";
        _cameraButton.TooltipText = follow
            ? "Riding the end of the track. Mouse wheel zooms.\nClick to fly the camera yourself."
            : "WASD to fly, hold right mouse to look, wheel for speed.\nClick to go back to following the track.";
    }

    private static StyleBoxFlat Panel(Color color)
    {
        var style = new StyleBoxFlat { BgColor = color };
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(0);
        return style;
    }
}
