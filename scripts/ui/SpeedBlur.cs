using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.UI;

/// <summary>
/// Fullscreen radial blur that ramps in while the nitro burns. Builds its own
/// <see cref="ColorRect"/> and material from <c>shaders/speed_blur.gdshader</c>, so it can be
/// dropped into a <see cref="CanvasLayer"/> with nothing to wire up but the car and the settings.
///
/// <b>Put it above whatever should be blurred and below whatever shouldn't.</b> The shader
/// samples the screen as it stands when the rect draws, so anything drawn earlier — the 3D
/// scene — is smeared, and anything drawn later — the speed readout, the debug overlay — stays
/// sharp. In practice that means first child of the HUD layer.
///
/// The rect hides itself outright when the blur is at rest. That isn't a micro-optimisation: a
/// visible screen-reading shader costs a full backbuffer copy plus ten taps a pixel every
/// frame, and this effect is off for most of a run.
/// </summary>
[GlobalClass]
public partial class SpeedBlur : ColorRect, IVehicleObserver
{
    /// <summary>The vehicle to watch. Set at runtime by whoever spawns the car.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>
    /// The look knobs, shared with <see cref="SpeedLines"/> and with every other scene pointing
    /// at the same file. Left empty this falls back to the defaults and tunes alone.
    /// </summary>
    [Export] public NitroEffectSettings? Settings { get; set; }

    private const string ShaderPath = "res://shaders/speed_blur.gdshader";

    /// <summary>Below this the effect is invisible, so the rect stops drawing entirely.</summary>
    private const float OffThreshold = 0.0002f;

    private ShaderMaterial? _material;
    private NitroEffectSettings _settings = null!;
    private float _strength;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        // Transparent, not white. The shader replaces every pixel it draws so this never shows
        // in normal operation — but if it ever fails to compile, Godot falls back to drawing the
        // rect plainly, and a white fullscreen rect is a flashbang. Transparent fails to nothing.
        Color = Colors.Transparent;

        _settings = Settings ?? new NitroEffectSettings();

        var shader = GD.Load<Shader>(ShaderPath);
        if (shader == null)
        {
            GD.PushWarning($"[SpeedBlur] {Name} could not load {ShaderPath}; staying off.");
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
        float target = boosting ? _settings.BlurStrength : 0.0f;

        float rate = target > _strength ? _settings.BlurAttack : _settings.BlurRelease;
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
        _material.SetShaderParameter("clear_radius", _settings.BlurClearRadius);
        _material.SetShaderParameter("falloff", _settings.BlurFalloff);
    }
}
