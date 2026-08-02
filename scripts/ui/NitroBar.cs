using System.Collections.Generic;
using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.UI;

/// <summary>
/// Nitro charges in the same candy-bubble language as <see cref="Speedometer"/>: one long blue
/// bubble per charge, fading to the dial's pale off-colour as they're spent. While a charge is
/// burning, its bubble runs a barberpole — diagonal stripes of a lighter blue scrolling across
/// the base blue for exactly <see cref="Vehicle.NitroDuration"/>.
///
/// The burn is keyed off the <see cref="Vehicle.NitroFired"/> signal rather than
/// <see cref="Vehicle.IsNitroActive"/>, because that flag is true for <i>any</i> boost — a drift
/// burst would spin the barberpole without a charge being spent.
///
/// Each bubble is a Panel with rounded-rect clipping (<see cref="CanvasItem.ClipChildren"/>),
/// so the stripes stay inside the bubble's curved ends instead of poking out of them. Wired to
/// the car like every other overlay: RacerArena hands the local player's vehicle to anything
/// under the HUD implementing <see cref="IVehicleObserver"/>.
/// </summary>
[GlobalClass]
public partial class NitroBar : Control, IVehicleObserver
{
    /// <summary>The vehicle to report on. Set at runtime by whoever spawns the car.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>Bubble size, in pixels. Long on purpose — these are lozenges, not blocks.</summary>
    [Export] public Vector2 BubbleSize { get; set; } = new(64.0f, 28.0f);

    /// <summary>Gap between bubbles, in pixels.</summary>
    [Export] public float Gap { get; set; } = 8.0f;

    /// <summary>Width of one barberpole stripe, in pixels.</summary>
    [Export] public float StripeWidth { get; set; } = 12.0f;

    /// <summary>How fast the barberpole scrolls, in pixels per second.</summary>
    [Export] public float StripeSpeed { get; set; } = 70.0f;

    private static readonly Color Blue = new("3D9BFF");
    private static readonly Color LightBlue = new("7CC2FF");
    private static readonly Color BubbleOff = new("F0E9EC");

    private Vehicle? _bound;
    private readonly List<Panel> _bubbles = new();
    private readonly List<StyleBoxFlat> _boxes = new();
    private readonly List<Control> _stripes = new();

    /// <summary>Which bubble is burning, or -1. Set by the NitroFired signal.</summary>
    private int _burnIndex = -1;

    /// <summary>Seconds of barberpole left on the burning bubble.</summary>
    private float _burnTimer;

    /// <summary>Scroll position of the barberpole, wrapped to one stripe period.</summary>
    private float _phase;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>
    /// Release the style boxes while the engine is still alive. A refcounted wrapper left to
    /// .NET shutdown is disposed after native teardown, which can crash the process on exit.
    /// </summary>
    public override void _ExitTree()
    {
        Unbind();

        foreach (StyleBoxFlat box in _boxes)
            box.Dispose();
        _boxes.Clear();
        _bubbles.Clear();
        _stripes.Clear();
    }

    public override void _Process(double delta)
    {
        if (!ReferenceEquals(_bound, VehicleNode))
            Rebind();

        if (_bound == null || !IsInstanceValid(_bound))
            return;

        int total = Mathf.Max(_bound.NitroCharges, _bound.NitroChargesRemaining);
        if (total != _bubbles.Count)
            RebuildBubbles(total);

        _burnTimer = Mathf.Max(_burnTimer - (float)delta, 0.0f);
        bool burning = _burnTimer > 0.0f && _burnIndex >= 0 && _burnIndex < _bubbles.Count;

        int remaining = _bound.NitroChargesRemaining;
        for (int i = 0; i < _bubbles.Count; i++)
        {
            bool isBurning = burning && i == _burnIndex;
            _boxes[i].BgColor = i < remaining || isBurning ? Blue : BubbleOff;
            _stripes[i].Visible = isBurning;
        }

        if (burning)
        {
            _phase = (_phase + StripeSpeed * (float)delta) % (StripeWidth * 2.0f);
            _stripes[_burnIndex].QueueRedraw();
        }
    }

    private void Rebind()
    {
        Unbind();
        _bound = VehicleNode;

        if (_bound != null && IsInstanceValid(_bound))
            _bound.NitroFired += OnNitroFired;
    }

    private void Unbind()
    {
        if (_bound != null && IsInstanceValid(_bound))
            _bound.NitroFired -= OnNitroFired;
        _bound = null;
    }

    /// <summary>
    /// A charge was just spent: the bubble it emptied is the one that barberpoles, for as long
    /// as the boost it bought is burning.
    /// </summary>
    private void OnNitroFired(int chargesRemaining)
    {
        _burnIndex = chargesRemaining;
        _burnTimer = _bound != null && IsInstanceValid(_bound) ? _bound.NitroDuration : 1.5f;
    }

    private void RebuildBubbles(int total)
    {
        foreach (Panel bubble in _bubbles)
            bubble.QueueFree();
        foreach (StyleBoxFlat box in _boxes)
            box.Dispose();
        _bubbles.Clear();
        _boxes.Clear();
        _stripes.Clear();

        for (int i = 0; i < total; i++)
        {
            var box = new StyleBoxFlat { BgColor = BubbleOff };
            box.SetCornerRadiusAll(Mathf.RoundToInt(BubbleSize.Y * 0.5f));

            var bubble = new Panel
            {
                Position = new Vector2(i * (BubbleSize.X + Gap), 0.0f),
                Size = BubbleSize,
                MouseFilter = MouseFilterEnum.Ignore,
                // The rounded bubble itself is the clip mask, so the stripes never poke out
                // of the curved ends.
                ClipChildren = ClipChildrenMode.AndDraw,
            };
            bubble.AddThemeStyleboxOverride("panel", box);

            var stripes = new Control { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
            stripes.SetAnchorsPreset(LayoutPreset.FullRect);
            Control captured = stripes;
            stripes.Draw += () => DrawStripes(captured);

            bubble.AddChild(stripes);
            AddChild(bubble);

            _bubbles.Add(bubble);
            _boxes.Add(box);
            _stripes.Add(stripes);
        }
    }

    /// <summary>
    /// The barberpole: light-blue parallelograms slanted 45 degrees, marching rightward with
    /// <see cref="_phase"/> over the bubble's base blue.
    /// </summary>
    private void DrawStripes(Control target)
    {
        float w = target.Size.X;
        float h = target.Size.Y;
        float period = StripeWidth * 2.0f;

        for (float x = _phase - period - h; x < w; x += period)
        {
            target.DrawColoredPolygon(new[]
            {
                new Vector2(x, h),
                new Vector2(x + StripeWidth, h),
                new Vector2(x + StripeWidth + h, 0.0f),
                new Vector2(x + h, 0.0f),
            }, LightBlue);
        }
    }
}
