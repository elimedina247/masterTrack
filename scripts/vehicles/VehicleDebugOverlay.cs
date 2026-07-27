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
public partial class VehicleDebugOverlay : Control, IVehicleObserver
{
    /// <summary>The vehicle to inspect. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>Whether the overlay starts visible.</summary>
    [Export] public bool ShowDebug { get; set; }

    private static readonly string[] DebugSets =
        { "All", "Inputs", "Grip", "Suspension", "Drift and Boost", "Steering", "Airborne" };

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

        Circle(centerOfGravity, 2.0f, Colors.Blue);

        Text($"Debug: {set}", new Vector2(10, 100), Colors.White);

        if (Engine.PhysicsTicksPerSecond < 120)
        {
            Text("LOW PHYSICS TICK RATE.", new Vector2(10, 300), Colors.Red);
            Text("Set physics ticks per second to at least 120.", new Vector2(10, 320), Colors.Red);
        }

        switch (set)
        {
            case "Grip":
                Text($"Surface: {vehicle.SurfaceType}", new Vector2(10, 120), Colors.White);
                Text($"Grip: {vehicle.CurrentGrip:F2}", new Vector2(10, 140), Colors.Yellow);
                Text($"Sideways: {vehicle.LateralSpeed:F1} m/s", new Vector2(10, 160), Colors.Orange);
                Text($"Grounded rays: {vehicle.GroundedRayCount}/4", new Vector2(10, 180), Colors.White);
                break;

            case "Suspension":
                Text("Spring Force", new Vector2(10, 120), Colors.Red);
                Text("Compression", new Vector2(10, 140), Colors.Green);
                break;

            case "Inputs":
                Text("Throttle", new Vector2(10, 120), Colors.Green);
                Text("Speed / Top Speed", new Vector2(10, 140), Colors.DeepSkyBlue);
                Text("Brake", new Vector2(10, 160), Colors.Red);
                Text("Drift", new Vector2(10, 180), Colors.Orange);
                Text("Boost Remaining", new Vector2(10, 200), Colors.Violet);
                Text($"{vehicle.Speed * 3.6f:F0} km/h", new Vector2(10, 230), Colors.White);
                break;

            case "Drift and Boost":
                Text($"Drift: {(vehicle.IsDrifting ? vehicle.DriftDirection > 0 ? "LEFT" : "RIGHT" : "-")}",
                     new Vector2(10, 120), Colors.Orange);
                Text($"Held: {vehicle.DriftTime:F2} s   Tier: {vehicle.DriftTier}",
                     new Vector2(10, 140), Colors.Orange);
                Text($"Boost: +{vehicle.BoostSpeed:F1} m/s for {vehicle.BoostTimeRemaining:F2} s",
                     new Vector2(10, 160), Colors.Violet);
                Text($"Chain: {vehicle.ChainCount}", new Vector2(10, 180), Colors.Violet);
                Text($"Ceiling: {(vehicle.TopSpeed + vehicle.BoostSpeed) * 3.6f:F0} km/h",
                     new Vector2(10, 200), Colors.DeepSkyBlue);
                Text($"Nitro charges: {vehicle.NitroChargesRemaining}", new Vector2(10, 220), Colors.White);
                break;

            case "Airborne":
                Text($"Grounded: {vehicle.GroundedRayCount}/4  ({vehicle.GroundFraction:P0})",
                     new Vector2(10, 120), Colors.White);
                Text($"Air time: {vehicle.AirTime:F2} s", new Vector2(10, 140), Colors.Aqua);

                // The number to watch when a jump won't flip: anything above 0 is the car being
                // told to be flat, and it should read 0 for the whole of a normal jump.
                Text($"Upright assist: {vehicle.UprightAssist:P0}", new Vector2(10, 160),
                     vehicle.UprightAssist > 0.0f ? Colors.Orange : Colors.Gray);

                Text($"Pitch: {Mathf.RadToDeg(vehicle.GlobalRotation.X):F0} deg   " +
                     $"Roll: {Mathf.RadToDeg(vehicle.GlobalRotation.Z):F0} deg",
                     new Vector2(10, 180), Colors.White);
                Text("Throttle / brake pitch the car in the air", new Vector2(10, 210), Colors.Gray);
                break;

            case "Steering":
                Text("Heading", new Vector2(10, 120), Colors.White);
                Text("Nose", new Vector2(10, 140), Colors.Gray);
                Text($"Error: {Mathf.RadToDeg(vehicle.HeadingError):F1} deg",
                     new Vector2(10, 160), Colors.Yellow);
                Text($"Torque: {vehicle.SteerTorque:F0} Nm", new Vector2(10, 180), Colors.Aqua);
                break;
        }

        if (set is "Inputs" or "All")
            BuildInputBars(vehicle, basis, centerOfGravity);

        if (set is "Steering" or "Drift and Boost" or "All")
            BuildSteering(vehicle, centerOfGravity);

        BuildRays(vehicle, set);
    }

    /// <summary>
    /// The heading the car is being asked to point at, against where its nose actually is. The
    /// gap between the two is the entire steering model — see <see cref="Vehicle.ProcessSteering"/>.
    /// </summary>
    private void BuildSteering(Vehicle vehicle, Vector3 cog)
    {
        Line(cog, vehicle.HeadingDirection * 3.0f, Colors.White);
        Line(cog, -vehicle.GlobalTransform.Basis.Z * 3.0f, Colors.Gray);
    }

    private void BuildInputBars(Vehicle vehicle, Basis basis, Vector3 cog)
    {
        // A row of 0..1 bars beside the car: each gets a black "full scale" backing line.
        Vector3 barOrigin = cog + basis.X * 1.5f;

        Bar(barOrigin, basis, vehicle.ThrottleAmount, Colors.Green, 0.0f);
        Bar(barOrigin, basis, vehicle.SpeedFraction, Colors.DeepSkyBlue, 2.0f);
        Bar(barOrigin, basis, vehicle.BrakeAmount, Colors.Red, 4.0f);
        Bar(barOrigin, basis, vehicle.DriftBlend, Colors.Orange, 6.0f);

        // Time left on the burst, not charges left — the bar is per-boost, and the count is on
        // the HUD. It reads as a fuse burning down.
        Bar(barOrigin, basis, vehicle.NitroFraction, Colors.Violet, 8.0f);
    }

    private void Bar(Vector3 origin, Basis basis, float value, Color color, float screenOffsetX)
    {
        var offset = new Vector2(screenOffsetX, 0.0f);
        Line(origin, basis.Y, Colors.Black, offset);
        Line(origin, basis.Y * value, color, offset);
    }

    private void BuildRays(Vehicle vehicle, string set)
    {
        foreach (GroundRay ray in vehicle.WheelArray)
        {
            // Green = this corner is on the ground, red = it is in the air.
            Circle(ray.GlobalPosition, 2.0f, ray.IsGrounded ? Colors.Green : Colors.Red);

            if (set is not ("Suspension" or "All") || !ray.IsGrounded)
                continue;

            // Along world up, because that is the direction the spring actually pushes.
            Line(ray.GlobalPosition, Vector3.Up * ray.SpringForce * 0.00004f, Colors.Red);

            // A travel gauge offset to whichever side of the car this corner is on.
            var gaugeOffset = new Vector2(ray.Position.X >= 0.0f ? 4.0f : -4.0f, 0.0f);
            Line(ray.GlobalPosition, Vector3.Down, Colors.DarkGreen, gaugeOffset);
            Line(ray.GlobalPosition,
                 Vector3.Down * Mathf.Clamp(1.0f - ray.Compression / Mathf.Max(ray.RestLength, 0.001f), 0.0f, 1.0f),
                 Colors.Green, gaugeOffset);
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
