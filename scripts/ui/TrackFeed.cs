using System.Collections.Generic;
using Godot;
using MasterTrack.Tiles;

namespace MasterTrack.UI;

/// <summary>
/// The callout board: a running list, top right, of the tiles the Track Master has dropped.
///
/// It exists because a tile landing is the single most important event in the game and, from
/// inside a car doing 300 km/h, it is very easy to miss. The racer three tiles back gets a private
/// warning from the server; everyone else got nothing at all, including the two people who are
/// about to arrive at it. This is the co-driver reading the road ahead — "HAIRPIN RIGHT",
/// "SLALOM", "GRAVEL BED" — so the whole lobby knows what just went down and can shout about it.
///
/// It reads the same for everyone, racers and Track Master alike. What the Track Master played is
/// not secret; the pressure comes from the racers arriving at it whether they knew or not.
///
/// Builds its own controls, the way <see cref="LobbyPanel"/> and <see cref="VehicleHud"/> do, so
/// dropping the node into a scene and pointing it at a track is the whole of the wiring.
/// </summary>
[GlobalClass]
public partial class TrackFeed : Control
{
    /// <summary>The track to listen to. Required.</summary>
    [Export] public TrackController? Track { get; set; }

    /// <summary>
    /// How far down from the top of the screen the feed starts, in pixels. A knob rather than a
    /// constant because the lobby already has its roster panel in this corner — see TestArea.tscn,
    /// which pushes the feed down past it.
    /// </summary>
    [Export] public int TopOffset { get; set; } = 24;

    /// <summary>Width of the feed in pixels.</summary>
    [Export] public int Width { get; set; } = 300;

    /// <summary>How long a callout stays at full strength before it starts going.</summary>
    [Export] public float HoldSeconds { get; set; } = 4.0f;

    /// <summary>And how long it takes to fade away once it does.</summary>
    [Export] public float FadeSeconds { get; set; } = 1.5f;

    /// <summary>
    /// Most callouts on screen at once. Older ones are dropped rather than queued: a stack of
    /// tiles from thirty seconds ago is not news, and the corner is not that tall.
    /// </summary>
    [Export] public int MaxLines { get; set; } = 5;

    /// <summary>One callout on the board and how long it has left.</summary>
    private sealed class Callout
    {
        public required Control Root { get; init; }
        public float Age;
    }

    private readonly List<Callout> _lines = new();
    private VBoxContainer _stack = null!;

    public override void _Ready()
    {
        // The feed is something to read, never something to click through.
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        _stack = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            OffsetLeft = -(Width + 24),
            OffsetTop = TopOffset,
            OffsetRight = -24,
            GrowHorizontal = GrowDirection.Begin,
        };
        _stack.AddThemeConstantOverride("separation", 4);
        AddChild(_stack);

        if (Track != null)
            Track.TilePlaced += OnTilePlaced;
        else
            GD.PushWarning("[TrackFeed] No Track assigned; nothing will ever be called out.");

        // Nothing to age until something has been said.
        SetProcess(false);
    }

    public override void _ExitTree()
    {
        if (Track != null && IsInstanceValid(Track))
            Track.TilePlaced -= OnTilePlaced;
    }

    /// <summary>
    /// A tile landed. The catalog index rather than the hazard is what names it: "Curve" without a
    /// side, or "Ramp Up" without saying how far, is not a callout anybody can drive on.
    /// </summary>
    private void OnTilePlaced(int trackIndex, int hazard, int catalogIndex)
    {
        TileDefinition? definition = TileCatalog.At(catalogIndex);
        if (definition == null)
            return;

        Announce(definition.DisplayName.ToUpperInvariant(), definition.Accent, trackIndex + 1);
    }

    private void Announce(string text, Color accent, int tileNumber)
    {
        Control line = BuildLine(text, accent, tileNumber);
        _stack.AddChild(line);
        // Newest at the top: the feed is read from the corner down, so the thing that just
        // happened must not be at the bottom of a list of things that already did.
        _stack.MoveChild(line, 0);

        _lines.Insert(0, new Callout { Root = line });

        while (_lines.Count > MaxLines)
        {
            _lines[^1].Root.QueueFree();
            _lines.RemoveAt(_lines.Count - 1);
        }

        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            Callout line = _lines[i];
            line.Age += (float)delta;

            float over = line.Age - HoldSeconds;
            if (over <= 0.0f)
                continue;

            if (over >= FadeSeconds)
            {
                line.Root.QueueFree();
                _lines.RemoveAt(i);
                continue;
            }

            line.Root.Modulate = Colors.White with { A = 1.0f - over / FadeSeconds };
        }

        // An empty board has nothing to count down, and the feed sits idle for most of a race.
        if (_lines.Count == 0)
            SetProcess(false);
    }

    /// <summary>
    /// One callout: a thick bar in the tile's own colour, its name, and how many tiles into the
    /// race it is. The colour is the same one the tile is built in and the same one on the card
    /// the Track Master played, so the board, the road and the feed all agree.
    /// </summary>
    private Control BuildLine(string text, Color accent, int tileNumber)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", Panel(new Color(0.07f, 0.08f, 0.10f, 0.86f)));

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 5);
        margin.AddThemeConstantOverride("margin_bottom", 5);
        panel.AddChild(margin);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        margin.AddChild(row);

        row.AddChild(new ColorRect
        {
            Color = accent,
            CustomMinimumSize = new Vector2(5, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        });

        var name = new Label
        {
            Text = text,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        name.AddThemeFontSizeOverride("font_size", 19);
        name.AddThemeColorOverride("font_color", accent.Lerp(Colors.White, 0.45f));
        name.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        name.AddThemeConstantOverride("outline_size", 5);
        row.AddChild(name);

        var number = new Label
        {
            Text = $"#{tileNumber}",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        number.AddThemeFontSizeOverride("font_size", 14);
        number.AddThemeColorOverride("font_color", new Color(0.60f, 0.63f, 0.70f));
        row.AddChild(number);

        return panel;
    }

    private static StyleBoxFlat Panel(Color color)
    {
        var style = new StyleBoxFlat { BgColor = color };
        style.SetCornerRadiusAll(5);
        style.SetContentMarginAll(0);
        return style;
    }
}
