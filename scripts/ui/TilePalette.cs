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
/// Unless the builder is in <see cref="TrackMasterController.FreeBuild"/>, in which case what
/// they have *is* the catalog and all of it goes on screen. That is two dozen cards rather than
/// six, so the tray wraps onto several rows and grows to fit them; everything below asks the
/// builder what a card is offering rather than asking the hand, and neither mode needs its own
/// path through this file.
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

    /// <summary>Height of the tray in pixels. A floor, not a fixed size — a tray that wraps onto
    /// several rows grows past it.</summary>
    [Export] public int TrayHeight { get; set; } = 132;

    /// <summary>
    /// Most cards to put in one row before wrapping. Only ever reached in free build: a hand is
    /// six slots and stays the single row it has always been.
    /// </summary>
    [Export] public int MaxColumns { get; set; } = 8;

    /// <summary>The nodes making up one slot, so a refresh restyles rather than rebuilds.</summary>
    private sealed class SlotView
    {
        public required PanelContainer Card { get; init; }
        public required ColorRect Swatch { get; init; }
        public required TextureRect Thumb { get; init; }
        public required ProgressBar Cooldown { get; init; }
        public required Label Name { get; init; }
        public required Label Hazard { get; init; }
    }

    private readonly List<SlotView> _slots = new();

    /// <summary>Each catalog tile's rendered portrait, by catalog index, filled in as
    /// <see cref="TileThumbnails"/> works through the pieces.</summary>
    private readonly Dictionary<int, Texture2D> _thumbs = new();
    private Label _status = null!;
    private Button _cameraButton = null!;

    /// <summary>How much of the race is left to build, and the panel it sits in — hidden together
    /// when the track has no limit and there is nothing to count down.</summary>
    private Label _remaining = null!;

    private PanelContainer _remainingPanel = null!;

    /// <summary>Slot the mouse is resting on, or -1. Tracked so the status line and the ghost
    /// can be re-aimed when the hand shifts under a cursor that hasn't moved.</summary>
    private int _hoveredSlot = -1;

    private const string IdleStatus = "Hover a tile to see it on the end of the track — click to place it.";

    private const string FreeBuildIdleStatus =
        "Hover a tile to see it on the end of the track — click to place it, Z to take it back.";

    /// <summary>What the status line says when the cursor is off the tray.</summary>
    private string RestingStatus
        => Builder is { FreeBuild: true } ? FreeBuildIdleStatus : IdleStatus;

    private static readonly Color CardIdle = new(0.12f, 0.13f, 0.16f, 0.92f);
    private static readonly Color CardHover = new(0.20f, 0.22f, 0.27f, 0.95f);
    private static readonly Color EmptySwatch = new(0.09f, 0.10f, 0.12f, 0.9f);

    /// <summary>What sits behind a card's portrait: near the card colour but a step darker, so
    /// the model reads as an object on the card rather than floating in it.</summary>
    private static readonly Color ThumbBackdrop = new(0.07f, 0.08f, 0.10f, 0.95f);

    private static readonly Color StatusIdle = new(0.86f, 0.88f, 0.92f);
    private static readonly Color StatusValid = new(0.55f, 0.95f, 0.60f);
    private static readonly Color StatusInvalid = new(0.95f, 0.60f, 0.55f);

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        BuildStatusBar();
        BuildCameraToggle();
        BuildRemainingCounter();
        BuildTray();
        BuildHazardTray();

        // The cards come up wearing their accent swatches and trade them for these portraits as
        // each one is rendered — the tray never waits on the camera.
        var thumbnails = new TileThumbnails();
        AddChild(thumbnails);
        thumbnails.RenderCatalog((index, texture) =>
        {
            _thumbs[index] = texture;
            RefreshSlots();
        });

        if (Builder == null)
        {
            GD.PushWarning("[TilePalette] No Builder assigned; the tray is inert.");
            return;
        }

        Builder.PreviewChanged += OnPreviewChanged;
        Builder.CameraModeChanged += OnCameraModeChanged;
        Builder.HandChanged += OnHandChanged;
        Builder.HazardHandChanged += RefreshHazardSlots;
        Builder.HazardStatusChanged += OnHazardStatus;
        OnCameraModeChanged((int)Builder.CameraMode);
        RefreshHazardSlots();

        // The counter moves when the track grows, which is exactly when the head changes.
        if (Builder.Track != null)
            Builder.Track.TrackHeadChanged += RefreshRemaining;

        RefreshRemaining();
    }

    /// <summary>
    /// Handlers on a node that outlives this one have to be taken back by hand — the track is in
    /// the scene, but a C# <c>+=</c> is a managed delegate Godot cannot tie to a control's life.
    /// </summary>
    public override void _ExitTree()
    {
        // Dropped eagerly rather than left to the GC: a wrapper for an engine resource still
        // alive at .NET shutdown is this project's known exit crash.
        _thumbs.Clear();

        // A racer's copy of this scene frees the builder and the tray in the same frame, and which
        // of the two goes first is not ours to know — so the builder has to be checked for as
        // carefully as the track it is being asked about.
        if (Builder == null || !IsInstanceValid(Builder))
            return;

        Builder.HazardHandChanged -= RefreshHazardSlots;
        Builder.HazardStatusChanged -= OnHazardStatus;

        if (Builder.Track is { } track && IsInstanceValid(track))
            track.TrackHeadChanged -= RefreshRemaining;
    }

    /// <summary>
    /// How many tiles the Track Master has left to spend on this race, under the camera toggle.
    ///
    /// This is the pace of the whole role in one number. Tiles arrive on a clock and the racers
    /// arrive on their own schedule, but the race is a fixed length — so knowing there are four
    /// left is what turns "make this hurt" into "make these four hurt".
    /// </summary>
    private void BuildRemainingCounter()
    {
        _remainingPanel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            OffsetLeft = 24,
            OffsetTop = 112,
            OffsetRight = 284,
            OffsetBottom = 148,
            Visible = false,
        };
        _remainingPanel.AddThemeStyleboxOverride("panel", Panel(new Color(0.10f, 0.11f, 0.14f, 0.80f)));
        AddChild(_remainingPanel);

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right" })
            margin.AddThemeConstantOverride(side, 12);
        _remainingPanel.AddChild(margin);

        _remaining = new Label { VerticalAlignment = VerticalAlignment.Center };
        _remaining.AddThemeFontSizeOverride("font_size", 18);
        margin.AddChild(_remaining);
    }

    private void RefreshRemaining()
    {
        int left = Builder?.Track?.TilesRemaining ?? -1;

        // A track with no limit is the lobby's free build. There is nothing to count down.
        _remainingPanel.Visible = left >= 0;
        if (left < 0)
            return;

        _remaining.Text = left == 0 ? "No tiles left — that's the race"
                        : left == 1 ? "1 tile left in the race"
                        : $"{left} tiles left in the race";
        _remaining.AddThemeColorOverride("font_color",
                                         left <= 3 ? StatusInvalid : StatusIdle);
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
            Text = RestingStatus,
            VerticalAlignment = VerticalAlignment.Center,
        };
        margin.AddChild(_status);
    }

    /// <summary>
    /// The camera mode toggle, directly under the status line. Its label is driven by the
    /// builder's signal rather than flipped here, so the button can't drift out of step with the
    /// camera.
    ///
    /// Left rather than the opposite corner: the two things say what the board is doing and belong
    /// together, and the top right is where the lobby's roster panel lives — in the lobby the two
    /// were landing on each other.
    /// </summary>
    private void BuildCameraToggle()
    {
        _cameraButton = new Button
        {
            // Never takes focus: with it focused, the space bar and Enter would re-press it,
            // and the Track Master's hands are on WASD.
            FocusMode = FocusModeEnum.None,
            OffsetLeft = 24,
            OffsetTop = 68,
            OffsetRight = 284,
            OffsetBottom = 104,
        };
        // Same dark panels as the rest of the HUD; the default theme's button doesn't belong
        // on top of the board.
        _cameraButton.AddThemeStyleboxOverride("normal", Panel(new Color(0.10f, 0.11f, 0.14f, 0.80f)));
        _cameraButton.AddThemeStyleboxOverride("hover", Panel(new Color(0.18f, 0.20f, 0.25f, 0.90f)));
        _cameraButton.AddThemeStyleboxOverride("pressed", Panel(new Color(0.16f, 0.34f, 0.24f, 0.95f)));

        _cameraButton.Pressed += () => Builder?.ToggleCameraMode();
        AddChild(_cameraButton);
    }

    /// <summary>Height of one card, and so of one row of the tray, in pixels. Grown from 96 when
    /// the swatch became a portrait — a 3D model needs more room than a stripe of colour.</summary>
    private const int CardHeight = 108;

    private const int CardWidth = 132;

    /// <summary>Height of the picture area at the top of a card, in pixels.</summary>
    private const int ThumbHeight = 52;

    private void BuildTray()
    {
        int slots = Builder?.TrayLength ?? 0;
        int columns = Mathf.Max(1, Mathf.Min(slots, MaxColumns));
        int rows = Mathf.Max(1, Mathf.CeilToInt(slots / (float)columns));

        // Grown to fit however many rows the tray wrapped onto, rather than clipping them: the
        // hand's single row still comes out at TrayHeight, and the catalog's three do not.
        int height = Mathf.Max(TrayHeight, rows * CardHeight + (rows - 1) * 8 + 20);
        _trayPixelHeight = height;

        var tray = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            // Pinned across the bottom edge, whatever the window size.
            AnchorLeft = 0.0f,
            AnchorTop = 1.0f,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            OffsetTop = -height,
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

        // A grid rather than a row, so one code path serves both trays: a hand is six cards and
        // fits on one line, and the free-build catalog is two dozen and does not. Centred by
        // wrapping in an HBox — a GridContainer fills from the left whatever the alignment.
        var centre = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        margin.AddChild(centre);

        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        centre.AddChild(grid);

        // One card per slot, built once and then only ever restyled. The hand changes several
        // times a minute; rebuilding the tray each time would throw away the card the mouse is
        // resting on and drop its hover.
        for (int i = 0; i < slots; i++)
            grid.AddChild(BuildSlot(i));

        RefreshSlots();
    }

    private PanelContainer BuildSlot(int slot)
    {
        var card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(CardWidth, CardHeight),
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
            CustomMinimumSize = new Vector2(0, ThumbHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(swatch);

        // The tile's portrait, over the swatch: the shot is transparent, so the swatch is its
        // backdrop — and until the shot is rendered it is the whole picture, as it always was.
        var thumb = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        thumb.SetAnchorsPreset(LayoutPreset.FullRect);
        swatch.AddChild(thumb);

        // Sits where the swatch does, so a slot either shows a tile's picture or the wait for
        // one — never both, and never a jump in the row's height as it switches.
        var cooldown = new ProgressBar
        {
            CustomMinimumSize = new Vector2(0, ThumbHeight),
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
            Thumb = thumb,
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

        // Free build has no deal clock, so no slot is ever the one being counted down into.
        int cooldownSlot = Builder.FreeBuild ? -1 : Builder.Hand.CooldownSlot;

        for (int i = 0; i < _slots.Count; i++)
        {
            SlotView view = _slots[i];
            int index = Builder.CatalogIndexAt(i);
            TileDefinition? definition = TileCatalog.At(index);

            bool isCooldown = i == cooldownSlot;
            view.Cooldown.Visible = isCooldown;
            view.Swatch.Visible = !isCooldown;

            if (definition != null)
            {
                // The portrait when it has been rendered, the accent until then. Behind a
                // portrait the swatch goes dark — it is a backdrop now, not the message.
                bool hasShot = _thumbs.TryGetValue(index, out Texture2D? shot);
                view.Thumb.Texture = hasShot ? shot : null;
                view.Swatch.Color = hasShot ? ThumbBackdrop : definition.Accent;
                view.Name.Text = definition.DisplayName;
                view.Hazard.Text = definition.Hazard == TileHazard.Straight
                    ? "no hazard"
                    : definition.Hazard.DisplayName();
                view.Card.TooltipText = $"{definition.DisplayName}\n{definition.Description}";
                continue;
            }

            // Empty. The one at the end of the row is the tile being dealt; the rest are just
            // room the Track Master hasn't been given anything for yet.
            view.Thumb.Texture = null;
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
        if (Builder == null || Builder.FreeBuild)
            return;

        int slot = Builder.Hand.CooldownSlot;
        if (slot >= 0 && slot < _slots.Count)
            _slots[slot].Cooldown.Value = Builder.Hand.DealProgress;

        int hazardSlot = Builder.HazardHand.CooldownSlot;
        if (hazardSlot >= 0 && hazardSlot < _hazardSlots.Count)
            _hazardSlots[hazardSlot].Cooldown.Value = Builder.HazardHand.DealProgress;
    }

    // ---- The hazard tray ----
    //
    // The hazard hand's own little row, docked above the tile tray on the right: the furniture
    // the builder can bolt onto road that is already down, dealt on its own slow clock. Clicking
    // a card arms placement — every slot it fits lights up on the road, and the click that
    // follows happens out on the board, not in here.

    /// <summary>The nodes making up one hazard card.</summary>
    private sealed class HazardSlotView
    {
        public required PanelContainer Card { get; init; }
        public required ColorRect Swatch { get; init; }
        public required ProgressBar Cooldown { get; init; }
        public required Label Name { get; init; }
    }

    private readonly List<HazardSlotView> _hazardSlots = new();

    /// <summary>What the tile tray actually came out at, so the hazard tray can sit clear of it
    /// even when free build wraps the catalog onto several rows.</summary>
    private int _trayPixelHeight = 132;

    private const int HazardCardWidth = 116;
    private const int HazardCardHeight = 58;

    /// <summary>Each hazard kind's card colour — the same paint its placed node wears, so the
    /// card and the thing it becomes read as one object.</summary>
    private static Color HazardAccent(HazardKind kind) => kind switch
    {
        HazardKind.LaunchPad => new Color(0.95f, 0.78f, 0.12f),
        HazardKind.PopUpRamp => new Color(0.95f, 0.55f, 0.10f),
        _ => new Color(0.6f, 0.6f, 0.6f),
    };

    private void BuildHazardTray()
    {
        int slots = Builder?.HazardTrayLength ?? 0;
        if (slots == 0)
            return;

        var tray = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Stop,
            AnchorLeft = 1.0f,
            AnchorTop = 1.0f,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            // Above the tile tray, hugging the right edge — clear of however tall the tray
            // actually came out, which in free build is several rows.
            OffsetLeft = -(slots + 1) * (HazardCardWidth + 10) - 26,
            OffsetRight = -16,
            OffsetTop = -_trayPixelHeight - HazardCardHeight - 30,
            OffsetBottom = -_trayPixelHeight - 10,
            GrowHorizontal = GrowDirection.Begin,
            GrowVertical = GrowDirection.Begin,
        };
        tray.AddThemeStyleboxOverride("panel", Panel(new Color(0.07f, 0.08f, 0.10f, 0.88f)));
        AddChild(tray);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_bottom", 6);
        tray.AddChild(margin);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 10);
        margin.AddChild(row);

        for (int i = 0; i < slots; i++)
            row.AddChild(BuildHazardSlot(i));

        // The lift card: takes a placed hazard back into the hand. A card rather than a bound
        // key, because the tray is the builder's whole vocabulary.
        var lift = new Button
        {
            Text = "Lift",
            TooltipText = "Take a placed hazard back off the road.\nClick this, then click the hazard.",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(56, HazardCardHeight),
        };
        lift.AddThemeStyleboxOverride("normal", Panel(CardIdle));
        lift.AddThemeStyleboxOverride("hover", Panel(CardHover));
        lift.AddThemeStyleboxOverride("pressed", Panel(new Color(0.16f, 0.34f, 0.24f, 0.95f)));
        lift.Pressed += () => Builder?.ArmHazardLift();
        row.AddChild(lift);

        RefreshHazardSlots();
    }

    private PanelContainer BuildHazardSlot(int slot)
    {
        var card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(HazardCardWidth, HazardCardHeight),
            MouseFilter = MouseFilterEnum.Stop,
        };
        card.AddThemeStyleboxOverride("panel", Panel(CardIdle));

        int captured = slot;
        card.GuiInput += @event =>
        {
            if (@event is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                return;

            Builder?.ArmHazardPlacement(captured);
            card.AcceptEvent();
        };
        card.MouseEntered += () => card.AddThemeStyleboxOverride("panel", Panel(CardHover));
        card.MouseExited += () => card.AddThemeStyleboxOverride("panel", Panel(CardIdle));

        var margin = new MarginContainer();
        foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            margin.AddThemeConstantOverride(side, 6);
        card.AddChild(margin);

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 4);
        margin.AddChild(box);

        var swatch = new ColorRect
        {
            CustomMinimumSize = new Vector2(0, 16),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(swatch);

        var cooldown = new ProgressBar
        {
            CustomMinimumSize = new Vector2(0, 16),
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
        name.AddThemeFontSizeOverride("font_size", 13);
        box.AddChild(name);

        _hazardSlots.Add(new HazardSlotView
        {
            Card = card,
            Swatch = swatch,
            Cooldown = cooldown,
            Name = name,
        });
        return card;
    }

    private void RefreshHazardSlots()
    {
        if (Builder == null)
            return;

        int cooldownSlot = Builder.FreeBuild ? -1 : Builder.HazardHand.CooldownSlot;

        for (int i = 0; i < _hazardSlots.Count; i++)
        {
            HazardSlotView view = _hazardSlots[i];
            int kind = Builder.HazardKindAt(i);

            bool isCooldown = i == cooldownSlot;
            view.Cooldown.Visible = isCooldown;
            view.Swatch.Visible = !isCooldown;

            if (kind != HazardHand.Empty)
            {
                view.Swatch.Color = HazardAccent((HazardKind)kind);
                view.Name.Text = ((HazardKind)kind).DisplayName();
                view.Card.TooltipText = "Click, then click a lit spot on the road.";
                continue;
            }

            view.Swatch.Color = EmptySwatch;
            view.Name.Text = isCooldown ? "next hazard" : "";
            view.Card.TooltipText = isCooldown
                ? "The next hazard is on its way here."
                : "An empty slot. Hazards arrive on their own.";
        }
    }

    /// <summary>The board said something about hazard placement; an empty message means the
    /// gesture ended and the line goes back to resting.</summary>
    private void OnHazardStatus(string text)
        => SetStatus(string.IsNullOrEmpty(text) ? RestingStatus : text, StatusIdle);

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
        SetStatus(RestingStatus, StatusIdle);
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
        // Free build has no empty cards — every one of them is a catalog tile — so there is never
        // anything to explain away.
        if (Builder == null || Builder.FreeBuild || _hoveredSlot < 0
            || Builder.CatalogIndexAt(_hoveredSlot) != TileHand.Empty)
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
        switch ((TrackMasterController.BoardCameraMode)mode)
        {
            case TrackMasterController.BoardCameraMode.Follow:
                _cameraButton.Text = "Camera: Following track";
                _cameraButton.TooltipText =
                    "Riding the end of the track. Mouse wheel zooms.\nClick to hover over the racers instead.";
                break;

            case TrackMasterController.BoardCameraMode.Pack:
                _cameraButton.Text = "Camera: Following racers";
                _cameraButton.TooltipText =
                    "Hovering over the middle of the pack. Mouse wheel zooms.\nClick to fly the camera yourself.";
                break;

            default:
                _cameraButton.Text = "Camera: Free roam";
                _cameraButton.TooltipText =
                    "WASD to fly, hold right mouse to look, wheel for speed.\nClick to go back to following the track.";
                break;
        }
    }

    private static StyleBoxFlat Panel(Color color)
    {
        var style = new StyleBoxFlat { BgColor = color };
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(0);
        return style;
    }
}
