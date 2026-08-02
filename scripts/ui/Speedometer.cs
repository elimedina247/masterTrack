using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.UI;

/// <summary>
/// The "Candy Bubble" speedometer: a semicircular arc of fat rounded blocks that fill in three
/// candy zones (mint, yellow, coral) up to the 200 km/h limit, with a separated cluster of red
/// overdrive blocks past it — territory only a drift boost can reach, since the unboosted
/// <see cref="Vehicle.TopSpeed"/> is exactly the limit. A lollipop needle sweeps the dial and
/// the readout sits in a pill that flips red in overdrive.
///
/// Past the limit the whole dial trembles and the needle shakes hard — going over 200 should
/// feel like the gauge itself can barely cope.
///
/// Everything is drawn in <see cref="_Draw"/> in a fixed design space and scaled to whatever
/// rect the scene gives the control, so one node placement decides its size. Wired to the car
/// the same way as every other overlay: RacerArena hands the local player's vehicle to anything
/// under the HUD implementing <see cref="IVehicleObserver"/>.
/// </summary>
[GlobalClass]
public partial class Speedometer : Control, IVehicleObserver
{
    /// <summary>The vehicle to report on. Set at runtime by whoever spawns the car.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>Speed the dial treats as the limit, in km/h. The red blocks live past this.</summary>
    [Export] public float LimitKmh { get; set; } = 200.0f;

    /// <summary>How hard the whole dial trembles in overdrive, in design-space pixels.</summary>
    [Export] public float DialShake { get; set; } = 3.5f;

    /// <summary>How hard the needle shakes in overdrive, in degrees either way.</summary>
    [Export] public float NeedleShakeDegrees { get; set; } = 9.0f;

    // The dial is laid out in this fixed design space and scaled to the control's rect.
    private const float DesignW = 360.0f;
    private const float DesignH = 230.0f;
    private const float CenterX = 180.0f;
    private const float CenterY = 178.0f;

    // 21 zone blocks cover 0..LimitKmh; 3 overdrive blocks sit past an angular gap.
    private const int ZoneSegments = 21;
    private const int OverSegments = 3;
    private const float GapDegrees = 7.0f;
    private const float SegmentRadius = 132.0f; // to a block's centre
    private const float SegmentW = 20.0f;
    private const float SegmentH = 40.0f;
    private const float NeedleLength = 106.0f;

    // Full sweep of the needle. Past the limit the dial reads "over"; the exact top hardly
    // matters because overdrive is about the shake, not the number of degrees left.
    private const float DialMaxKmh = 235.0f;

    /// <summary>Roughly how much overdrive each red block represents, in km/h.</summary>
    private const float OverStepKmh = 12.0f;

    private static readonly Color Mint = new("3ECF8E");
    private static readonly Color Sun = new("FFC94D");
    private static readonly Color Coral = new("FF6B6B");
    private static readonly Color OverRed = new("E8253D");
    private static readonly Color BlockOff = new("F0E9EC");
    private static readonly Color Ink = new("3A2E39");
    private static readonly Color ReadoutText = new(1.0f, 0.99f, 0.97f);

    private StyleBoxFlat _segmentBox = null!;
    private StyleBoxFlat _pillBox = null!;
    private Font _font = null!;

    private readonly System.Random _rng = new();

    /// <summary>Smoothed speed the needle and blocks show, in km/h.</summary>
    private float _displayKmh;

    /// <summary>0..1 fade into the overdrive shake, so crossing 200 doesn't pop.</summary>
    private float _shakeBlend;

    private Vector2 _dialJitter;
    private float _needleJitterDeg;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        _font = ThemeDB.Singleton.FallbackFont;

        _segmentBox = new StyleBoxFlat();
        _pillBox = new StyleBoxFlat { BgColor = Ink };
    }

    /// <summary>
    /// Release the style boxes while the engine is still alive. A refcounted wrapper left to
    /// .NET shutdown is disposed after native teardown, which can crash the process on exit.
    /// </summary>
    public override void _ExitTree()
    {
        _segmentBox?.Dispose();
        _pillBox?.Dispose();
        _segmentBox = _pillBox = null!;
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;

        float kmh = VehicleNode != null && IsInstanceValid(VehicleNode)
            ? VehicleNode.Speed * 3.6f
            : 0.0f;

        // The needle chases the real speed rather than tracking it exactly — a physical gauge
        // has inertia, and the little lag reads as weight instead of jitter.
        _displayKmh = Mathf.Lerp(_displayKmh, kmh, 1.0f - Mathf.Exp(-10.0f * dt));

        _shakeBlend = Mathf.MoveToward(_shakeBlend, kmh > LimitKmh ? 1.0f : 0.0f, dt * 8.0f);

        if (_shakeBlend > 0.0f)
        {
            _dialJitter = new Vector2(Jitter(), Jitter()) * (DialShake * _shakeBlend);
            _needleJitterDeg = Jitter() * NeedleShakeDegrees * _shakeBlend;
        }
        else
        {
            _dialJitter = Vector2.Zero;
            _needleJitterDeg = 0.0f;
        }

        QueueRedraw();
    }

    private float Jitter() => (float)(_rng.NextDouble() * 2.0 - 1.0);

    public override void _Draw()
    {
        if (VehicleNode == null || !IsInstanceValid(VehicleNode))
            return;

        float s = Mathf.Min(Size.X / DesignW, Size.Y / DesignH);
        if (s <= 0.0f)
            return;

        // Everything below places design-space points through this: centred in the rect,
        // scaled, and trembling as one piece when the dial is in overdrive.
        Vector2 origin = (Size - new Vector2(DesignW, DesignH) * s) * 0.5f;
        Vector2 P(float x, float y) => origin + (new Vector2(x, y) + _dialJitter) * s;

        DrawBlocks(s, P);
        DrawNeedle(s, P);
        DrawReadout(s, P);
    }

    private void DrawBlocks(float s, System.Func<float, float, Vector2> P)
    {
        int total = ZoneSegments + OverSegments;
        float span = 180.0f - GapDegrees;
        var rect = new Rect2(
            -SegmentW * 0.5f * s, -(SegmentRadius + SegmentH * 0.5f) * s,
            SegmentW * s, SegmentH * s);

        _segmentBox.SetCornerRadiusAll(Mathf.RoundToInt(10.0f * s));

        for (int i = 0; i < total; i++)
        {
            float phi = -90.0f + (i + 0.5f) / total * span + (i >= ZoneSegments ? GapDegrees : 0.0f);

            bool lit = i < ZoneSegments
                ? _displayKmh >= (i + 1) * (LimitKmh / ZoneSegments) - 0.01f
                : _displayKmh > LimitKmh + (i - ZoneSegments) * OverStepKmh;

            _segmentBox.BgColor = lit ? BlockColor(i) : BlockOff;

            DrawSetTransform(P(CenterX, CenterY), Mathf.DegToRad(phi), Vector2.One);
            DrawStyleBox(_segmentBox, rect);
        }

        DrawSetTransform(Vector2.Zero);
    }

    private static Color BlockColor(int index) => index switch
    {
        < 7 => Mint,
        < 14 => Sun,
        < ZoneSegments => Coral,
        _ => OverRed,
    };

    private void DrawNeedle(float s, System.Func<float, float, Vector2> P)
    {
        float sweep = Mathf.Clamp(_displayKmh / DialMaxKmh, 0.0f, 1.0f);
        float angle = -90.0f + sweep * 180.0f + _needleJitterDeg;
        var tip = new Vector2(0.0f, -NeedleLength * s);

        DrawSetTransform(P(CenterX, CenterY), Mathf.DegToRad(angle), Vector2.One);

        // Lollipop: a thick stick, a candy ball on the tip, a hub over the pivot.
        DrawLine(Vector2.Zero, tip, Ink, 7.0f * s, antialiased: true);
        DrawCircle(tip, 10.0f * s, Coral);
        DrawArc(tip, 10.0f * s, 0.0f, Mathf.Tau, 32, Ink, 3.0f * s, antialiased: true);
        DrawCircle(Vector2.Zero, 11.0f * s, ReadoutText);
        DrawArc(Vector2.Zero, 11.0f * s, 0.0f, Mathf.Tau, 32, Ink, 4.0f * s, antialiased: true);

        DrawSetTransform(Vector2.Zero);
    }

    private void DrawReadout(float s, System.Func<float, float, Vector2> P)
    {
        bool over = _displayKmh > LimitKmh;

        _pillBox.BgColor = over ? OverRed : Ink;
        _pillBox.SetCornerRadiusAll(Mathf.RoundToInt(17.0f * s));

        var pillSize = new Vector2(130.0f, 34.0f) * s;
        DrawStyleBox(_pillBox, new Rect2(P(CenterX - 65.0f, CenterY + 14.0f), pillSize));

        DrawString(_font,
            P(CenterX - 65.0f, CenterY + 37.0f),
            $"{Mathf.RoundToInt(_displayKmh)} km/h",
            HorizontalAlignment.Center, pillSize.X, Mathf.RoundToInt(18.0f * s), ReadoutText);
    }
}
