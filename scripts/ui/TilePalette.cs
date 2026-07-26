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
/// The tray is the whole placement interface: hovering a card ghosts that tile onto the end of
/// the track, and clicking it puts it there. There's no held tile and no second click, because
/// the end of the track is the only place a tile can go (see <see cref="TrackMasterController"/>).
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

    private readonly List<PanelContainer> _cards = new();
    private Label _status = null!;
    private Button _cameraButton = null!;

    private const string IdleStatus = "Hover a tile to see it on the end of the track — click to place it.";

    private static readonly Color CardIdle = new(0.12f, 0.13f, 0.16f, 0.92f);
    private static readonly Color CardHover = new(0.20f, 0.22f, 0.27f, 0.95f);

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

        // Handled here rather than with a Button so the tile lands on press: at racing speed
        // the wait for a release is felt.
        card.GuiInput += @event => OnCardInput(card, @event, index);
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

    private void OnCardInput(PanelContainer card, InputEvent @event, int index)
    {
        if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            return;

        Builder?.PlaceTile(index);
        // The board has nothing left to do with this click, and letting it through would only
        // give the builder's camera a chance to react to it.
        card.AcceptEvent();
    }

    private void OnCardHover(int index, bool entered)
    {
        _cards[index].AddThemeStyleboxOverride("panel", Panel(entered ? CardHover : CardIdle));

        if (entered)
        {
            Builder?.PreviewTile(index);
            return;
        }

        Builder?.ClearPreview();
        _status.Text = IdleStatus;
        _status.AddThemeColorOverride("font_color", StatusIdle);
    }

    /// <summary>The builder previewed a tile on the head: say what clicking would do.</summary>
    private void OnPreviewChanged(bool valid, string reason)
    {
        _status.Text = reason;
        _status.AddThemeColorOverride("font_color", valid ? StatusValid : StatusInvalid);
    }

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
