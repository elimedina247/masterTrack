using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.UI;

/// <summary>
/// Speed readout for the car the local player is driving. Builds its own label so it can be
/// dropped into any scene with nothing to wire up.
///
/// Speed only, deliberately. The car has no gearbox — the revs and gear number the engine note
/// sweeps through are computed backwards from road speed (see <see cref="FakeGearbox"/>), so
/// showing them would just be a second speedometer dressed up as a drivetrain.
/// </summary>
[GlobalClass]
public partial class VehicleHud : Control
{
    /// <summary>The vehicle to report on. Set at runtime by whoever spawns the car.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    private Label _speed = null!;
    private Label _direction = null!;
    private Label _nitro = null!;

    private static readonly Color NitroReady = new(1.0f, 1.0f, 1.0f);
    private static readonly Color NitroBurning = new(1.0f, 0.62f, 0.15f);
    private static readonly Color NitroEmpty = new(0.55f, 0.55f, 0.55f);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var box = new VBoxContainer { Position = new Vector2(24, 24) };
        box.AddThemeConstantOverride("separation", 4);
        AddChild(box);

        _speed = AddLabel(box, 32);
        _direction = AddLabel(box, 20);
        _nitro = AddLabel(box, 22);

        Refresh();
    }

    private static Label AddLabel(Node parent, int fontSize)
    {
        var label = new Label();
        label.AddThemeFontSizeOverride("font_size", fontSize);
        // An outline keeps the text readable over both tarmac and sky.
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.75f));
        label.AddThemeConstantOverride("outline_size", 6);
        parent.AddChild(label);
        return label;
    }

    public override void _Process(double delta) => Refresh();

    private void Refresh()
    {
        if (VehicleNode == null || !IsInstanceValid(VehicleNode))
        {
            _speed.Text = "";
            _direction.Text = "";
            _nitro.Text = "";
            return;
        }

        _speed.Text = $"{Mathf.Round(VehicleNode.Speed * 3.6f)} km/h";

        // Reverse is the one bit of "gear" that's real — it genuinely flips the drive force.
        _direction.Text = VehicleNode.CurrentGear == -1 ? "REVERSE" : "";

        RefreshNitro();
    }

    /// <summary>
    /// Charges as a row of pips, so the count reads at a glance without being counted. Drawn
    /// with ASCII brackets rather than block or diamond glyphs — the default theme font is the
    /// one thing here that isn't the project's to choose, and a missing glyph shows as tofu.
    /// </summary>
    private void RefreshNitro()
    {
        int remaining = VehicleNode!.NitroChargesRemaining;
        int total = Mathf.Max(VehicleNode.NitroCharges, remaining);

        var pips = new System.Text.StringBuilder(total * 2);
        for (int i = 0; i < total; i++)
            pips.Append(i < remaining ? "[]" : "..");

        if (VehicleNode.IsNitroActive)
        {
            _nitro.Text = $"NITRO {pips}  BOOST";
            _nitro.AddThemeColorOverride("font_color", NitroBurning);
            return;
        }

        _nitro.Text = $"NITRO {pips}";
        _nitro.AddThemeColorOverride("font_color", remaining > 0 ? NitroReady : NitroEmpty);
    }
}
