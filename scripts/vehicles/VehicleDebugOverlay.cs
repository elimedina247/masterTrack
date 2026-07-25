// C# port of addons/gevp/scripts/debug.gd and debug_ui.gd from Godot-Easy-Vehicle-Physics
// (MIT — see assets/gevp/LICENSE). The original split the readout across a Node and a child
// Control; this merges them into one Control, and rebuilds the draw list every frame instead
// of keeping a dictionary of named shapes.

using System.Collections.Generic;
using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// The tuning overlay: draws the vehicle's centre of gravity, axle positions, per-wheel
/// suspension and tire forces, drivetrain split and stability torques over the 3D view, plus
/// a text readout of the driver inputs.
///
/// This is the thing that makes the physics tunable — the numbers in the inspector don't
/// mean much until you can see what the tires are actually doing. Toggle it with
/// <c>vehicle_debug_toggle</c> (backtick) and page through the sets with
/// <c>vehicle_debug_next</c> / <c>vehicle_debug_prev</c> (comma and period).
/// </summary>
[GlobalClass]
public partial class VehicleDebugOverlay : Control
{
    /// <summary>The vehicle to inspect. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>Whether the overlay starts visible.</summary>
    [Export] public bool ShowDebug { get; set; }

    private static readonly string[] DebugSets =
        { "All", "Inputs", "Tire Forces", "Suspension Forces", "Drivetrain", "Stability" };

    private int _currentDebugSet;

    private readonly List<TextCommand> _text = new();
    private readonly List<LineCommand> _lines = new();
    private readonly List<CircleCommand> _circles = new();

    private Font _font = null!;
    private int _fontSize;

    public override void _Ready()
    {
        _font = ThemeDB.Singleton.FallbackFont;
        _fontSize = ThemeDB.Singleton.FallbackFontSize;

        // The overlay must never eat clicks meant for the game or the builder UI.
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        // No warning when VehicleNode is unset: in a match the overlay is wired up once the
        // local player's car has spawned, which is well after this runs.
    }

    public override void _Process(double delta)
    {
        if (Input.IsActionJustPressed("vehicle_debug_toggle"))
            ShowDebug = !ShowDebug;

        if (Input.IsActionJustPressed("vehicle_debug_next"))
            SwitchDebugSet(_currentDebugSet + 1);

        if (Input.IsActionJustPressed("vehicle_debug_prev"))
            SwitchDebugSet(_currentDebugSet - 1);

        _text.Clear();
        _lines.Clear();
        _circles.Clear();

        if (ShowDebug && VehicleNode is { IsVehicleReady: true } vehicle)
            BuildDebug(vehicle);

        QueueRedraw();
    }

    private void SwitchDebugSet(int value)
    {
        // Wrap in both directions; C#'s % keeps the sign of the dividend.
        _currentDebugSet = (value % DebugSets.Length + DebugSets.Length) % DebugSets.Length;
    }

    private void BuildDebug(Vehicle vehicle)
    {
        string set = DebugSets[_currentDebugSet];
        Basis basis = vehicle.GlobalTransform.Basis;
        Vector3 centerOfGravity = vehicle.GlobalTransform * vehicle.CenterOfMass;
        Vector3 frontAxle = vehicle.GlobalTransform * vehicle.FrontAxlePosition;
        Vector3 rearAxle = vehicle.GlobalTransform * vehicle.RearAxlePosition;
        Vector3 centerAxle = frontAxle.Lerp(rearAxle, 0.5f);

        Circle(centerOfGravity, 2.0f, Colors.Blue);
        Circle(frontAxle, 2.0f, Colors.Green);
        Circle(rearAxle, 2.0f, Colors.Green);
        Circle(centerAxle, 2.0f, Colors.Green);

        Text($"Debug: {set}", new Vector2(10, 100), Colors.White);

        if (Engine.PhysicsTicksPerSecond < 120)
        {
            Text("LOW PHYSICS TICK RATE.", new Vector2(10, 300), Colors.Red);
            Text("Set physics ticks per second to at least 120.", new Vector2(10, 320), Colors.Red);
        }

        switch (set)
        {
            case "Tire Forces":
                Text("Tire Slip", new Vector2(10, 120), Colors.Yellow);
                Text("Tire Force", new Vector2(10, 140), Colors.Red);
                break;

            case "Suspension Forces":
                Text("Total Suspension Force", new Vector2(10, 120), Colors.Red);
                Text("Antiroll Bar Force", new Vector2(10, 140), Colors.Blue);
                Text("Damping Force", new Vector2(10, 160), Colors.Yellow);
                Text("Current Suspension Length", new Vector2(10, 180), Colors.Green);
                break;

            case "Inputs":
                Text("Steering Input", new Vector2(10, 120), Colors.Gray);
                Text("Assisted Steering Input", new Vector2(10, 140), Colors.White);
                Text("Throttle Input", new Vector2(10, 160), Colors.Green);
                Text("Drive Force", new Vector2(10, 180), Colors.Aqua);
                Text("Speed / Top Speed", new Vector2(10, 200), Colors.DeepSkyBlue);
                Text("Brake Input", new Vector2(10, 220), Colors.Red);
                Text("Handbrake Input", new Vector2(10, 240), Colors.Orange);
                break;

            case "Drivetrain":
                Text("Torque Split", new Vector2(10, 120), Colors.Orange);
                break;

            case "Stability":
                Text("Yaw Torque", new Vector2(10, 120), Colors.Red);
                Text("Keep Upright Torque", new Vector2(10, 140), Colors.Magenta);
                break;
        }

        if (set is "Inputs" or "All")
            BuildInputBars(vehicle, basis, centerOfGravity, frontAxle);

        if (set is "Drivetrain" or "All")
            BuildDrivetrain(vehicle, frontAxle, rearAxle, centerAxle);

        if (set is "Stability" or "All")
            BuildStability(vehicle, basis, centerOfGravity, frontAxle, rearAxle);

        BuildWheels(vehicle, set);
    }

    private void BuildInputBars(Vehicle vehicle, Basis basis, Vector3 cog, Vector3 frontAxle)
    {
        // Where the driver asked the wheels to point, versus where the assists put them.
        Line(frontAxle, basis * new Vector3(-Mathf.Sin(vehicle.SteeringInput * vehicle.MaxSteeringAngle),
                                            0.0f,
                                            -Mathf.Cos(vehicle.SteeringInput * vehicle.MaxSteeringAngle)),
             Colors.Gray);
        Line(frontAxle, basis * new Vector3(-Mathf.Sin(vehicle.TrueSteeringAmount),
                                            0.0f,
                                            -Mathf.Cos(vehicle.TrueSteeringAmount)),
             Colors.White);

        // A row of 0..1 bars beside the car: each gets a black "full scale" backing line.
        Vector3 barOrigin = cog + basis.X * 1.5f;
        float driveFraction = vehicle.MaxDriveForce > 0.0f
            ? vehicle.DriveForce / vehicle.MaxDriveForce
            : 0.0f;

        Bar(barOrigin, basis, vehicle.ThrottleAmount, Colors.Green, 0.0f);
        Bar(barOrigin, basis, driveFraction, Colors.Aqua, 2.0f);
        Bar(barOrigin, basis, vehicle.SpeedFraction, Colors.DeepSkyBlue, 4.0f);
        Bar(barOrigin, basis, vehicle.BrakeAmount, Colors.Red, 6.0f);
        Bar(barOrigin, basis, vehicle.HandbrakeInput, Colors.Orange, 8.0f);
    }

    private void Bar(Vector3 origin, Basis basis, float value, Color color, float screenOffsetX)
    {
        var offset = new Vector2(screenOffsetX, 0.0f);
        Line(origin, basis.Y, Colors.Black, offset);
        Line(origin, basis.Y * value, color, offset);
    }

    private void BuildDrivetrain(Vehicle vehicle, Vector3 frontAxle, Vector3 rearAxle, Vector3 centerAxle)
    {
        float split = vehicle.TrueTorqueSplit;
        Line(centerAxle, (frontAxle - centerAxle) * split, Colors.Orange);
        Line(centerAxle, (rearAxle - centerAxle) * (1.0f - split), Colors.Orange);

        float frontSplit = (vehicle.FrontAxle.AppliedSplit + 1.0f) * 0.5f;
        Line(frontAxle, (vehicle.FrontAxle.Wheels[0].GlobalPosition - frontAxle) * frontSplit * split,
             Colors.Orange);
        Line(frontAxle, (vehicle.FrontAxle.Wheels[1].GlobalPosition - frontAxle) * (1.0f - frontSplit) * split,
             Colors.Orange);

        float rearSplit = (vehicle.RearAxle.AppliedSplit + 1.0f) * 0.5f;
        Line(rearAxle, (vehicle.RearAxle.Wheels[0].GlobalPosition - rearAxle) * rearSplit * (1.0f - split),
             Colors.Orange);
        Line(rearAxle, (vehicle.RearAxle.Wheels[1].GlobalPosition - rearAxle) * (1.0f - rearSplit) * (1.0f - split),
             Colors.Orange);
    }

    private void BuildStability(Vehicle vehicle, Basis basis, Vector3 cog,
                                Vector3 frontAxle, Vector3 rearAxle)
    {
        Vector3 normalized = vehicle.StabilityTorqueVector.Normalized();
        Line(cog,
             new Vector3(normalized.Z, 0.0f, normalized.X) * vehicle.StabilityTorqueVector.Length() * 0.001f,
             Colors.Magenta);

        if (vehicle.StabilityYawStrength == 0.0f)
            return;

        float yaw = vehicle.StabilityYawTorque * 0.001f / vehicle.StabilityYawStrength;
        Line(frontAxle, basis.X * yaw, Colors.Red);
        Line(rearAxle, -basis.X * yaw, Colors.Red);
    }

    private void BuildWheels(Vehicle vehicle, string set)
    {
        foreach (Wheel wheel in vehicle.WheelArray)
        {
            // Green = the tire is inside its force limit, red = it's saturated and slipping.
            Circle(wheel.GlobalPosition, 2.0f, wheel.LimitSpin ? Colors.Green : Colors.Red);
            Basis wheelBasis = wheel.GlobalTransform.Basis;

            if (set is "Suspension Forces" or "All")
            {
                Line(wheel.GlobalPosition, wheelBasis.Y * wheel.SpringForce * 0.0002f, Colors.Red);
                Line(wheel.GlobalPosition,
                     wheelBasis.Y * (wheel.AntirollForce + wheel.DampingForce) * 0.0002f, Colors.Blue);
                Line(wheel.GlobalPosition, wheelBasis.Y * wheel.DampingForce * 0.0002f, Colors.Yellow);

                // A travel gauge offset to whichever side of the car this wheel is on.
                var gaugeOffset = new Vector2(4.0f * VehicleMath.SignF(wheel.Position.X), 0.0f);
                Line(wheel.GlobalPosition, -wheelBasis.Y, Colors.DarkGreen, gaugeOffset);
                Line(wheel.GlobalPosition,
                     -wheelBasis.Y * (wheel.SpringCurrentLength / wheel.SpringLength),
                     Colors.Green, gaugeOffset);
            }

            if (set is "Tire Forces" or "All")
            {
                Vector3 force = (wheelBasis.X * wheel.ForceVector.X
                                 + wheelBasis.Z * wheel.ForceVector.Y) * 0.0002f;
                Vector3 slip = (wheelBasis.X * -wheel.SlipVector.X
                                + wheelBasis.Z * -wheel.SlipVector.Y) * 2.0f;
                Line(wheel.LastCollisionPoint, force, Colors.Red);
                Line(wheel.LastCollisionPoint, slip, Colors.Yellow);
            }
        }
    }

    public override void _Draw()
    {
        Camera3D? camera = GetViewport().GetCamera3D();

        foreach (TextCommand text in _text)
            DrawString(_font, text.Position, text.Text, HorizontalAlignment.Left, -1, _fontSize, text.Color);

        if (camera == null)
            return;

        foreach (LineCommand line in _lines)
        {
            Vector2 from = camera.UnprojectPosition(line.Position) + line.ScreenOffset;
            Vector2 to = camera.UnprojectPosition(line.Position + line.Vector) + line.ScreenOffset;
            DrawLine(from, to, line.Color, 2.0f);
        }

        foreach (CircleCommand circle in _circles)
            DrawCircle(camera.UnprojectPosition(circle.Position), circle.Radius, circle.Color);
    }

    // ---- Draw list ----

    private void Text(string text, Vector2 screenPosition, Color color)
        => _text.Add(new TextCommand { Text = text, Position = screenPosition, Color = color });

    private void Line(Vector3 worldPosition, Vector3 worldVector, Color color, Vector2 screenOffset = default)
        => _lines.Add(new LineCommand
        {
            Position = worldPosition, Vector = worldVector, Color = color, ScreenOffset = screenOffset,
        });

    private void Circle(Vector3 worldPosition, float radius, Color color)
        => _circles.Add(new CircleCommand { Position = worldPosition, Radius = radius, Color = color });

    private readonly struct TextCommand
    {
        public string Text { get; init; }
        public Vector2 Position { get; init; }
        public Color Color { get; init; }
    }

    private readonly struct LineCommand
    {
        public Vector3 Position { get; init; }
        public Vector3 Vector { get; init; }
        public Color Color { get; init; }
        public Vector2 ScreenOffset { get; init; }
    }

    private readonly struct CircleCommand
    {
        public Vector3 Position { get; init; }
        public float Radius { get; init; }
        public Color Color { get; init; }
    }
}
