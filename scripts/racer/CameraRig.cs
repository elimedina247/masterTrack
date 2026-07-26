using Godot;
using MasterTrack.Vehicles;

namespace MasterTrack.Racer;

/// <summary>
/// Third-person chase camera for a Racer. Sits as a child of the car at its origin, with
/// the actual <see cref="Camera3D"/> offset up and behind. Because the rig is parented to
/// the car, "no rotation" already means *looking straight ahead down the track*.
///
/// Controls: hold <c>camera_look</c> (right mouse) to free-look around the car; the mouse
/// is captured while held. Release it and the camera eases back to straight-ahead, so a
/// racer driving in a straight line without touching the mouse always faces forward.
///
/// Only the local player's rig processes input and owns the active camera.
/// </summary>
public partial class CameraRig : Node3D
{
    /// <summary>Radians of look per pixel of mouse movement.</summary>
    [Export] public float Sensitivity = 0.005f;

    /// <summary>How quickly the camera eases back to straight-ahead (higher = snappier).</summary>
    [Export] public float RecenterSpeed = 6.0f;

    [Export] public float MinPitch = -0.5f; // look down
    [Export] public float MaxPitch = 0.6f;  // look up

    // ---------------------------------------------------------------- Nitro FOV

    /// <summary>
    /// Degrees of field of view added while the nitro is burning. The resting FOV is whatever
    /// the <c>Camera</c> node is set to in the scene — this is an offset from it, so the scene
    /// stays the one place the camera's normal look is decided.
    ///
    /// Widening the lens is what actually sells the speed: the car keeps the same size on
    /// screen while everything around it stretches and rushes past the edges.
    /// </summary>
    [ExportGroup("Nitro")]
    [Export] public float NitroFovBoost = 12.0f;

    /// <summary>How fast the FOV opens up when a boost lights, per second. Fast: this is the punch.</summary>
    [Export] public float NitroFovAttack = 9.0f;

    /// <summary>
    /// How fast it settles back when the boost ends, per second. Deliberately slower than
    /// <see cref="NitroFovAttack"/> — snapping back reads as a glitch, easing back reads as
    /// the car running out of shove.
    /// </summary>
    [Export] public float NitroFovRelease = 3.5f;

    // ---------------------------------------------------------------- Speed shake

    /// <summary>Road speed in <b>km/h</b> at which the camera starts to tremble.</summary>
    [ExportGroup("Speed Shake")]
    [Export] public float ShakeStartSpeed = 100.0f;

    /// <summary>
    /// Road speed in <b>km/h</b> at which the shake reaches full strength. Defaults to the
    /// racer's normal top speed, so the shake saturates where the car normally runs out and
    /// stays pinned through anything the nitro adds on top.
    /// </summary>
    [Export] public float ShakeFullSpeed = 200.0f;

    /// <summary>
    /// Peak wobble in <b>degrees</b>, applied to the camera's pitch, yaw and roll. Keep it
    /// small — this should be felt rather than seen, and past about a degree it stops reading
    /// as speed and starts reading as a fault.
    /// </summary>
    [Export] public float ShakeAngle = 0.35f;

    /// <summary>Roughly how many wobbles a second at full strength.</summary>
    [Export] public float ShakeFrequency = 14.0f;

    private float _yaw;
    private float _pitch;
    private bool _active;
    private bool _wasLooking;
    private Camera3D _camera = null!;
    private Vehicle? _vehicle;
    private float _baseFov;
    private Vector3 _baseCameraRotation;
    private readonly FastNoiseLite _shakeNoise = new();
    private float _shakeTime;

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera");
        _baseFov = _camera.Fov;

        // The camera sits tilted down in the scene; the shake is an offset from that pose, not
        // a replacement for it.
        _baseCameraRotation = _camera.Rotation;

        // Smooth noise rather than a fresh random number per frame. At 60+ fps random values
        // are uncorrelated frame to frame, which looks like video static; Perlin drifts, which
        // looks like a car at speed.
        _shakeNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        _shakeNoise.Frequency = 1.0f;
        _shakeNoise.Seed = (int)GD.Randi();

        // The rig is always a child of the car it films, so there is nothing to wire up.
        _vehicle = GetParentOrNull<Vehicle>();
        if (_vehicle == null)
            GD.PushWarning($"[CameraRig] {Name} is not a child of a Vehicle; nitro FOV is off.");

        // Dormant until the owning RacerController hands us control.
        SetProcess(false);
        SetProcessUnhandledInput(false);
    }

    /// <summary>Called by the owning car: activate the camera + input only for the local player.</summary>
    public void SetActive(bool local)
    {
        _active = local;
        _camera.Current = local;
        SetProcess(local);
        SetProcessUnhandledInput(local);
        GD.Print($"[CameraRig] SetActive({local}); camera current = {_camera.Current}.");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!_active)
            return;

        // Free-look only while the look button is held.
        if (@event is InputEventMouseMotion motion && Input.IsActionPressed("camera_look"))
        {
            _yaw -= motion.Relative.X * Sensitivity;
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * Sensitivity, MinPitch, MaxPitch);
        }
    }

    public override void _Process(double delta)
    {
        if (!_active)
            return;

        bool looking = Input.IsActionPressed("camera_look");

        // Capture the mouse while looking so it doesn't hit the screen edge; free it after.
        if (looking != _wasLooking)
        {
            Input.MouseMode = looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            _wasLooking = looking;
        }

        // Not looking -> ease the camera back to straight-ahead (behind the car).
        if (!looking)
        {
            float t = 1.0f - Mathf.Exp(-RecenterSpeed * (float)delta);
            _yaw = Mathf.Lerp(_yaw, 0.0f, t);
            _pitch = Mathf.Lerp(_pitch, 0.0f, t);
        }

        Rotation = new Vector3(_pitch, _yaw, 0.0f);

        ProcessNitroFov((float)delta);
        ProcessSpeedShake((float)delta);
    }

    private void ProcessNitroFov(float delta)
    {
        if (_vehicle == null)
            return;

        float target = _baseFov + (_vehicle.IsNitroActive ? NitroFovBoost : 0.0f);
        float rate = target > _camera.Fov ? NitroFovAttack : NitroFovRelease;
        _camera.Fov = Mathf.Lerp(_camera.Fov, target, 1.0f - Mathf.Exp(-rate * delta));
    }

    private void ProcessSpeedShake(float delta)
    {
        if (_vehicle == null)
            return;

        float kmh = _vehicle.Speed * 3.6f;
        float ramp = Mathf.Clamp(
            Mathf.InverseLerp(ShakeStartSpeed, Mathf.Max(ShakeFullSpeed, ShakeStartSpeed + 0.001f), kmh),
            0.0f, 1.0f);

        // Squared, so the shake creeps in either side of the threshold instead of switching on
        // the moment the speedometer ticks past it.
        float amount = ramp * ramp;

        if (amount <= 0.0f)
        {
            _camera.Rotation = _baseCameraRotation;
            return;
        }

        // Advance faster the harder the shake, so speed changes the texture of it and not just
        // the size — a fast tremble reads very differently to a slow sway of the same size.
        _shakeTime += delta * ShakeFrequency * Mathf.Lerp(0.6f, 1.0f, amount);

        // Three well-separated slices of the same noise field: one field, three uncorrelated
        // wobbles, no three objects to seed and keep in step.
        float angle = Mathf.DegToRad(ShakeAngle) * amount;
        _camera.Rotation = _baseCameraRotation + new Vector3(
            _shakeNoise.GetNoise2D(_shakeTime, 0.0f),
            _shakeNoise.GetNoise2D(_shakeTime, 37.0f),
            _shakeNoise.GetNoise2D(_shakeTime, 74.0f)) * angle;
    }
}
