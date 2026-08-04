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

    /// <summary>
    /// The point on the car the nose trace follows, in the vehicle's own space. Defaults to the
    /// middle of the front face of the collision box — far enough forward that a degree of
    /// rotation moves it a readable distance.
    /// </summary>
    [ExportGroup("Nose trace")]
    [Export] public Vector3 NoseOffset { get; set; } = new(0.0f, 0.59f, -1.24f);

    /// <summary>Seconds of nose history kept. The trail is sampled once per physics tick.</summary>
    [Export(PropertyHint.Range, "0.5,10,0.5")]
    public float TraceSeconds { get; set; } = 3.0f;

    private static readonly string[] DebugSets =
        { "All", "Inputs", "Grip", "Suspension", "Drift and Boost", "Steering", "Airborne", "Nose" };

    private int _currentDebugSet;

    private readonly List<TextCommand> _text = new();
    private readonly List<LineCommand> _lines = new();
    private readonly List<CircleCommand> _circles = new();

    /// <summary>Where the nose has been. See <see cref="SampleNose"/> for why there are two.</summary>
    private readonly Queue<NoseSample> _trace = new();

    private BodyLean? _bodyLean;
    private Vehicle? _bodyLeanOwner;

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

    // ---- Nose trace ----

    /// <summary>
    /// Sampled on the physics tick, not the render frame, so the trail is the motion the
    /// simulation actually produced rather than whatever the renderer happened to catch.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!ShowDebug || VehicleNode is not { IsVehicleReady: true } vehicle)
        {
            _trace.Clear();
            return;
        }

        SampleNose(vehicle);

        var capacity = Mathf.Max(2, Mathf.RoundToInt(TraceSeconds * Engine.PhysicsTicksPerSecond));
        while (_trace.Count > capacity)
            _trace.Dequeue();
    }

    /// <summary>
    /// Two nose positions, and the gap between them is the whole point of this trace.
    ///
    /// <b>Chassis</b> is the nose carried by the rigid body alone — where the physics put the car.
    /// <b>Shell</b> is the same point on the visible model, which hangs off a <see cref="BodyLean"/>
    /// that poses it in roll, yaw and pitch on top of the chassis. Those two separating is a car
    /// that is being *posed* oddly; those two moving together but roughly is a car that is being
    /// *simulated* oddly. Without both traces the difference is invisible, because the player only
    /// ever sees the second one.
    /// </summary>
    private void SampleNose(Vehicle vehicle)
    {
        Vector3 chassis = vehicle.GlobalTransform * NoseOffset;

        BodyLean? lean = ResolveBodyLean(vehicle);
        Vector3 shell = lean != null ? lean.GlobalTransform * NoseOffset : chassis;

        _trace.Enqueue(new NoseSample
        {
            Chassis = chassis, Shell = shell, Airborne = vehicle.IsAirborne,
        });
    }

    /// <summary>
    /// The posing node under this vehicle, found rather than wired: the car is spawned at runtime
    /// and the overlay is handed only the vehicle (see <see cref="IVehicleObserver"/>), so there
    /// is no scene path to export.
    /// </summary>
    private BodyLean? ResolveBodyLean(Vehicle vehicle)
    {
        if (_bodyLeanOwner == vehicle && IsInstanceValid(_bodyLean))
            return _bodyLean;

        _bodyLeanOwner = vehicle;
        _bodyLean = FindBodyLean(vehicle);
        return _bodyLean;
    }

    private static BodyLean? FindBodyLean(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is BodyLean lean)
                return lean;

            if (FindBodyLean(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    /// <summary>
    /// Both trails, plus the readouts that say which of the two is doing the moving.
    ///
    /// Airborne samples are drawn bright and grounded ones dim, so a flip stands out of the lap
    /// either side of it without having to catch the trace at the right moment.
    /// </summary>
    private void BuildNose(Vehicle vehicle)
    {
        NoseSample? previous = null;

        foreach (NoseSample sample in _trace)
        {
            if (previous is { } last)
            {
                Line(last.Chassis, sample.Chassis - last.Chassis,
                     sample.Airborne ? Colors.Aqua : Colors.Teal);
                Line(last.Shell, sample.Shell - last.Shell,
                     sample.Airborne ? Colors.Orange : Colors.SaddleBrown);
            }

            previous = sample;
        }

        if (previous is not { } current)
            return;

        Circle(current.Chassis, 3.0f, Colors.Aqua);
        Circle(current.Shell, 3.0f, Colors.Orange);

        // The separation itself, drawn where it happens. A long red stick between the two dots is
        // the pose disagreeing with the chassis by that much, right now.
        Line(current.Chassis, current.Shell - current.Chassis, Colors.Red);

        float split = current.Chassis.DistanceTo(current.Shell);

        Text("Chassis nose (physics)", new Vector2(10, 120), Colors.Aqua);
        Text("Shell nose (what you see)", new Vector2(10, 140), Colors.Orange);
        Text($"Split: {split * 100.0f:F0} cm", new Vector2(10, 160),
             split > 0.25f ? Colors.Red : Colors.White);

        // The chassis' own rotation, in its own axes. Smooth numbers here with a jumping split
        // above means the physics is fine and the pose is lying.
        Basis basis = vehicle.GlobalTransform.Basis;
        Text($"Chassis rate  pitch {Mathf.RadToDeg(vehicle.AngularVelocity.Dot(basis.X)):F0}"
             + $"  yaw {Mathf.RadToDeg(vehicle.AngularVelocity.Dot(Vector3.Up)):F0}"
             + $"  roll {Mathf.RadToDeg(vehicle.AngularVelocity.Dot(basis.Z)):F0} deg/s",
             new Vector2(10, 190), Colors.Aqua);

        if (ResolveBodyLean(vehicle) is { } lean)
        {
            Vector3 pose = lean.PoseEuler;
            Text($"Shell pose    pitch {Mathf.RadToDeg(pose.X):F1}"
                 + $"  yaw {Mathf.RadToDeg(pose.Y):F1}"
                 + $"  roll {Mathf.RadToDeg(pose.Z):F1} deg"
                 + $"  shift {lean.PoseSideways * 100.0f:F0} cm",
                 new Vector2(10, 210), Colors.Orange);
        }

        // The two quantities BodyLean poses from. Both are ground concepts — a heading flattened
        // into the horizontal plane, and a cornering g worked out from yaw rate times forward
        // speed — so watch what they do to a car that is pointing at the sky.
        Text($"HeadingError: {Mathf.RadToDeg(vehicle.HeadingError):F0} deg"
             + $"   nose tilt: {Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(-basis.Z.Y, -1.0f, 1.0f))):F0} deg",
             new Vector2(10, 240), Colors.Yellow);
        Text($"Yaw rate x forward speed: {vehicle.AngularVelocity.Y * vehicle.ForwardSpeed / 9.8f:F2} g",
             new Vector2(10, 260), Colors.Yellow);
        Text($"Airborne: {vehicle.IsAirborne}   air time {vehicle.AirTime:F2} s",
             new Vector2(10, 280), Colors.White);
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
                Text($"Fall speed: {-vehicle.LinearVelocity.Y:F1} m/s", new Vector2(10, 160),
                     Colors.Aqua);

                Text($"Pitch: {Mathf.RadToDeg(vehicle.GlobalRotation.X):F0} deg   " +
                     $"Roll: {Mathf.RadToDeg(vehicle.GlobalRotation.Z):F0} deg",
                     new Vector2(10, 180), Colors.White);

                // Nothing drives or damps roll any more, so a car that takes off rolling keeps
                // rolling. Worth seeing the number when a landing goes wrong.
                Text($"Roll rate: {Mathf.RadToDeg(vehicle.AngularVelocity.Dot(vehicle.GlobalBasis.Z)):F0} deg/s",
                     new Vector2(10, 200), Colors.Gray);

                Text("Throttle / brake pitch, steering yaws. No auto-level.",
                     new Vector2(10, 230), Colors.Gray);
                break;

            case "Steering":
                Text("Heading", new Vector2(10, 120), Colors.White);
                Text("Nose", new Vector2(10, 140), Colors.Gray);
                Text($"Error: {Mathf.RadToDeg(vehicle.HeadingError):F1} deg",
                     new Vector2(10, 160), Colors.Yellow);
                Text($"Torque: {vehicle.SteerTorque:F0} Nm", new Vector2(10, 180), Colors.Aqua);
                break;
        }

        if (set is "Nose" or "All")
            BuildNose(vehicle);

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

    private readonly struct NoseSample
    {
        public Vector3 Chassis { get; init; }
        public Vector3 Shell { get; init; }
        public bool Airborne { get; init; }
    }
}
