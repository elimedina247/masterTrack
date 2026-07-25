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

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var box = new VBoxContainer { Position = new Vector2(24, 24) };
        box.AddThemeConstantOverride("separation", 4);
        AddChild(box);

        _speed = AddLabel(box, 32);
        _direction = AddLabel(box, 20);

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
            return;
        }

        _speed.Text = $"{Mathf.Round(VehicleNode.Speed * 3.6f)} km/h";

        // Reverse is the one bit of "gear" that's real — it genuinely flips the drive force.
        _direction.Text = VehicleNode.CurrentGear == -1 ? "REVERSE" : "";
    }
}
