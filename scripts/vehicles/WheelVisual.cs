using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// One visible wheel. Purely cosmetic — nothing here is read by the physics.
///
/// <b>This node lives under the posed shell, not under the chassis.</b> That is the whole point
/// of it. The car's wheels are decoration: no force in the game is applied at a wheel, so there
/// is nothing tying the mesh to the ray cast that holds that corner up. Parenting the mesh to the
/// shell means it inherits the body's roll, yaw and pitch for free, and the car reads as one
/// object instead of a body that leans while four wheels stay bolted to an invisible frame.
///
/// The only thing computed here is how far the wheel hangs below its rest position, and that is
/// worked out from the <see cref="Ray"/>'s contact point <b>in world space</b> — so however far
/// the shell has leaned or swung, the wheel still ends up on the road rather than floating above
/// it or sunk into it.
///
/// Suspension travel you can see is therefore real: the wheel stays on the ground and the body
/// springs above it, which is what the ray casts are already doing to the rigid body.
/// </summary>
[GlobalClass]
public partial class WheelVisual : Node3D
{
    /// <summary>The ground ray this wheel belongs to. Required.</summary>
    [Export] public GroundRay? Ray { get; set; }

    /// <summary>The vehicle, for steering input and road speed. Required.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>
    /// Radius of the wheel mesh in metres. Decides how far above the contact point the hub sits,
    /// and how fast it rolls. Nothing in the physics reads it.
    /// </summary>
    [Export] public float TireRadius { get; set; } = 0.3f;

    /// <summary>Whether this wheel turns with the steering.</summary>
    [Export] public bool Steers { get; set; }

    /// <summary>Visible steering lock in degrees at full input.</summary>
    [Export] public float VisualSteerAngle { get; set; } = 28.0f;

    /// <summary>
    /// How far below its rest position the wheel may hang, in metres. Reached when the car is in
    /// the air, and it is what stops a wheel stretching away toward a road forty metres below.
    ///
    /// Place the node itself at the wheel's <b>static</b> centre height, so travel reads zero
    /// with the car sitting still and this is genuinely droop.
    /// </summary>
    [Export] public float MaxDroop { get; set; } = 0.30f;

    /// <summary>
    /// How far above its rest position the wheel may rise into the arch, in metres. The bump half
    /// of the travel — what you see when the car lands or crosses a kerb.
    ///
    /// Deliberately generous enough to cover the springs bottoming out completely (0.48 m on the
    /// racer). Clamping this short does not keep the wheel out of the bodywork, it drives the
    /// wheel <i>through the road</i> — the hub stops rising while the tarmac keeps coming up to
    /// meet it. That is the tires-vanishing-into-the-ramp bug, and it is worse than a wheel
    /// clipping an arch.
    /// </summary>
    [Export] public float MaxLift { get; set; } = 0.50f;

    /// <summary>
    /// How quickly the wheel closes on where the road says it should be, per second.
    ///
    /// High, but not instant. The contact point comes off the last physics tick while this runs
    /// on the rendered frame, so following it exactly makes the wheel jitter against the body
    /// between ticks. A little smoothing hides that and costs nothing visually — the wheel is
    /// still on the ground, it just gets there over a couple of frames.
    /// </summary>
    [Export] public float TravelResponse { get; set; } = 30.0f;

    /// <summary>
    /// How much faster the wheel drops back down than it rises, as a multiple of
    /// <see cref="TravelResponse"/>.
    ///
    /// Under 1 on purpose. A wheel that snaps up over a bump and then eases back down reads as a
    /// damped spring; one that moves at the same rate both ways reads as a mechanism.
    /// </summary>
    [Export] public float ReboundRatio { get; set; } = 0.55f;

    /// <summary>Rest position of the hub, captured from wherever the node was placed.</summary>
    private Vector3 _rest;

    /// <summary>Current travel from rest, in metres. Negative is compressed up into the arch.</summary>
    private float _travel;

    private float _spin;

    public override void _Ready()
    {
        _rest = Position;

        if (Ray == null)
            GD.PushWarning($"[WheelVisual] {Name} has no Ray assigned; it won't follow the road.");

        if (VehicleNode == null)
            GD.PushWarning($"[WheelVisual] {Name} has no VehicleNode assigned; it won't steer.");
    }

    public override void _Process(double delta)
    {
        if (Ray is not { } ray || VehicleNode is not { IsVehicleReady: true } vehicle)
            return;

        var dt = (float)delta;

        float target = TargetTravel(ray);

        // Asymmetric: quick to compress, slower to extend. See ReboundRatio.
        float rate = target < _travel ? TravelResponse : TravelResponse * ReboundRatio;
        _travel = Mathf.Lerp(_travel, target, 1.0f - Mathf.Exp(-rate * dt));

        Position = _rest + new Vector3(0.0f, _travel, 0.0f);

        float steer = Steers ? vehicle.SteeringInput * Mathf.DegToRad(VisualSteerAngle) : 0.0f;

        // The physics yaws the nose toward the stick in either gear, but a real car tracing that
        // same arc in reverse has its front wheels turned the other way — mirror them so the pose
        // matches the path.
        if (vehicle.CurrentGear < 0)
            steer = -steer;

        // Rolled from how far the wheel actually moved through the world, so it stays right for a
        // car somebody else is driving — those are frozen puppets whose pose is assigned rather
        // than integrated, and whose LinearVelocity is therefore permanently zero.
        if (TireRadius > 0.0f)
        {
            Vector3 here = GlobalPosition;
            if (_hasLast)
                _spin = Mathf.Wrap(
                    _spin + -vehicle.GlobalTransform.Basis.Z.Dot(here - _last) / TireRadius,
                    0.0f, Mathf.Tau);
            _last = here;
            _hasLast = true;
        }

        Rotation = new Vector3(_spin, steer, 0.0f);
    }

    private Vector3 _last;
    private bool _hasLast;

    /// <summary>
    /// How far this wheel should sit below its rest position so that it lands on the road.
    ///
    /// Worked out in world space and then measured against this node's own parent, which is what
    /// makes it immune to the shell's pose: however far the body has rolled or yawed over this
    /// corner, the answer is still "put the hub <see cref="TireRadius"/> above the tarmac".
    /// </summary>
    private float TargetTravel(GroundRay ray)
    {
        // Nothing under us: hang at full droop rather than reaching for a road that isn't there.
        if (!ray.IsGrounded || GetParent() is not Node3D parent)
            return -MaxDroop;

        // Where the hub wants to be, in world space, then in the parent's frame so it can be
        // compared against the rest position this node was authored at.
        Vector3 wanted = ray.LastCollisionPoint + ray.LastCollisionNormal * TireRadius;
        Vector3 local = parent.GlobalTransform.AffineInverse() * wanted;

        return Mathf.Clamp(local.Y - _rest.Y, -MaxDroop, MaxLift);
    }
}
