using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.UI;

/// <summary>
/// Speed lines streaking outward from the centre of the screen while the nitro burns. Builds
/// its own <see cref="ColorRect"/> and material from <c>shaders/speed_lines.gdshader</c>, so it
/// can be dropped into a <see cref="CanvasLayer"/> with nothing to wire up but the car and the
/// settings.
///
/// Goes <b>after</b> <see cref="SpeedBlur"/> in the HUD layer and before anything that has to
/// stay legible. The blur reads the screen underneath it, so lines drawn first would be smeared
/// along with the scene — they want to be crisp over the top of it.
///
/// Unlike the blur this doesn't read the screen at all, so it costs a plain overdraw rather
/// than a backbuffer copy. It still hides itself when idle: the shader runs per-pixel over the
/// whole viewport, and the effect is off for most of a run.
/// </summary>
[GlobalClass]
public partial class SpeedLines : ColorRect, IVehicleObserver
{
    /// <summary>The vehicle to watch. Set at runtime by whoever spawns the car.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>
    /// The look knobs, shared with <see cref="SpeedBlur"/> and with every other scene pointing
    /// at the same file. Left empty this falls back to the defaults and tunes alone.
    /// </summary>
    [Export] public NitroEffectSettings? Settings { get; set; }

    private const string ShaderPath = "res://shaders/speed_lines.gdshader";

    /// <summary>Below this the lines are invisible, so the rect stops drawing entirely.</summary>
    private const float OffThreshold = 0.002f;

    private ShaderMaterial? _material;
    private NitroEffectSettings _settings = null!;
    private float _strength;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        // Transparent, not white. The shader writes its own colour so this never shows in normal
        // operation — but if it ever fails to compile, Godot falls back to drawing the rect
        // plainly, and a white fullscreen rect is a flashbang. Transparent fails to nothing.
        Color = Colors.Transparent;

        _settings = Settings ?? new NitroEffectSettings();

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader == null)
        {
            GD.PushWarning($"[SpeedLines] {Name} could not load {ShaderPath}; staying off.");
            Visible = false;
            SetProcess(false);
            return;
        }

        _material = new ShaderMaterial { Shader = shader };
        Material = _material;
        ApplyStrength(0.0f);
    }

    public override void _Process(double delta)
    {
        if (_material == null)
            return;

        bool boosting = VehicleNode != null && IsInstanceValid(VehicleNode) && VehicleNode.IsNitroActive;
        float target = boosting ? _settings.LinesStrength : 0.0f;

        float rate = target > _strength ? _settings.LinesAttack : _settings.LinesRelease;
        _strength = Mathf.Lerp(_strength, target, 1.0f - Mathf.Exp(-rate * (float)delta));

        // The release is exponential and never quite reaches zero, so cut it off once it is far
        // enough down to be invisible — otherwise the rect never stops drawing.
        if (_strength < OffThreshold)
            _strength = 0.0f;

        ApplyStrength(_strength);
    }

    private void ApplyStrength(float strength)
    {
        Visible = strength > 0.0f;
        if (!Visible)
            return;

        _material!.SetShaderParameter("strength", strength);
        _material.SetShaderParameter("line_color", _settings.LineColor);
        _material.SetShaderParameter("line_count", _settings.LineCount);
        _material.SetShaderParameter("inner_radius", _settings.LinesInnerRadius);
        _material.SetShaderParameter("speed", _settings.LinesSpeed);
        _material.SetShaderParameter("density", _settings.LinesDensity);
        _material.SetShaderParameter("line_width", _settings.LineWidth);
        _material.SetShaderParameter("softness", _settings.LinesSoftness);
    }
}
