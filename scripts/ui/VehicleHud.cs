using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.UI;

/// <summary>
/// Speed / RPM / gear readout for the car the local player is driving, in the spirit of the
/// source project's demo GUI. Builds its own labels so it can be dropped into any scene with
/// nothing to wire up, and shows the gearbox mode because the transmission toggle is easy to
/// hit by accident.
/// </summary>
[GlobalClass]
public partial class VehicleHud : Control
{
    /// <summary>The vehicle to report on. Set at runtime by whoever spawns the car.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    private Label _speed = null!;
    private Label _rpm = null!;
    private Label _gear = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        var box = new VBoxContainer { Position = new Vector2(24, 24) };
        box.AddThemeConstantOverride("separation", 4);
        AddChild(box);

        _speed = AddLabel(box, 32);
        _rpm = AddLabel(box, 20);
        _gear = AddLabel(box, 20);

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
            _rpm.Text = "";
            _gear.Text = "";
            return;
        }

        _speed.Text = $"{Mathf.Round(VehicleNode.Speed * 3.6f)} km/h";
        _rpm.Text = $"{Mathf.Round(VehicleNode.MotorRpm)} rpm";
        _gear.Text = $"Gear: {GearLabel(VehicleNode.CurrentGear)}" +
                     (VehicleNode.AutomaticTransmission ? "  (auto)" : "  (manual)");
    }

    private static string GearLabel(int gear) => gear switch
    {
        -1 => "R",
        0 => "N",
        _ => gear.ToString(),
    };
}
