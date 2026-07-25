using Godot;

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

    private float _yaw;
    private float _pitch;
    private bool _active;
    private bool _wasLooking;
    private Camera3D _camera = null!;

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera");
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
    }
}
