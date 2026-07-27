using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// One corner of the hovercraft: a ray cast down from the chassis with a spring and damper on it.
///
/// This is <b>not</b> a wheel. It carries no tire model, no brakes, no spin and no drive torque —
/// grip and drive live on the body in <see cref="Vehicle"/>, applied as central forces. All this
/// does is hold that corner of the car off the ground and report what it is standing on. The
/// wheel you can see is decoration hung off <see cref="WheelNode"/>.
///
/// Place these at the <b>top of the suspension travel</b>, the same as the old ray-cast wheels:
/// the body hangs <see cref="RestLength"/> below the ray origin at rest.
/// </summary>
[GlobalClass]
public partial class GroundRay : RayCast3D
{
    /// <summary>
    /// The visible wheel for this corner. Positioned at the contact patch plus
    /// <see cref="TireRadius"/> every frame, so it tracks the ground rather than the body.
    /// Optional — leave it unset for an invisible probe.
    /// </summary>
    [Export] public Node3D? WheelNode { get; set; }

    /// <summary>
    /// Radius of the visible wheel, in metres. Only used to sit the mesh on the road and to
    /// pick a rolling speed for it; nothing in the physics reads it.
    /// </summary>
    [Export] public float TireRadius { get; set; } = 0.3f;

    /// <summary>Width of the visible wheel in metres. Read by <see cref="SkidMarks"/> for the strip width.</summary>
    [Export] public float TireWidth { get; set; } = 0.25f;

    /// <summary>
    /// Whether this corner's visible wheel turns with the steering. Front corners only, and
    /// purely cosmetic — steering is a torque on the body, not an angle on a wheel.
    /// </summary>
    [Export] public bool Steers { get; set; }

    /// <summary>Visible steering lock in degrees, at full input. Cosmetic.</summary>
    [Export] public float VisualSteerAngle { get; set; } = 28.0f;

    // ---------------------------------------------------------------- Set by the vehicle

    /// <summary>
    /// Ride height: how far below the ray origin the chassis wants to float. Walaber's
    /// <c>spring_d := 0.8 - hit_d</c> with the 0.8 pulled out as a knob.
    /// </summary>
    public float RestLength { get; set; } = 0.5f;

    /// <summary>Spring rate in newtons per metre of compression.</summary>
    public float SpringStrength { get; set; } = 40000.0f;

    /// <summary>Damping in newtons per m/s of vertical closing speed.</summary>
    public float SpringDamping { get; set; } = 3000.0f;

    /// <summary>
    /// Largest downward pull this corner may apply when the ray reaches past
    /// <see cref="RestLength"/>, in newtons.
    ///
    /// The spring term goes negative once the ground is further away than the ride height, which
    /// sucks the car onto the road over a crest — worth keeping, it's a lot of what stops an
    /// arcade car pogoing. Left unbounded it also yanks the nose down off a ramp and kills the
    /// jump, so it's capped rather than removed. 0 disables the pull entirely.
    /// </summary>
    public float MaxPullForce { get; set; } = 4000.0f;

    // ---------------------------------------------------------------- Reported state

    /// <summary>Whether the ray reached anything this step.</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>Where the ray hit, in world space. Only meaningful while <see cref="IsGrounded"/>.</summary>
    public Vector3 LastCollisionPoint { get; private set; } = Vector3.Zero;

    /// <summary>Surface normal at the contact point.</summary>
    public Vector3 LastCollisionNormal { get; private set; } = Vector3.Up;

    /// <summary>Whatever the ray hit, or null.</summary>
    public GodotObject? LastCollider { get; private set; }

    /// <summary>
    /// Surface group under this corner — see <see cref="SurfaceGroups"/>. Sticky: a collider with
    /// no group leaves it on whatever it was, so a seam between colliders can't flick the car
    /// onto a different surface for one step.
    /// </summary>
    public string SurfaceType { get; private set; } = SurfaceGroups.Road;

    /// <summary>
    /// How far the spring is compressed from <see cref="RestLength"/>, in metres. Positive means
    /// loaded, negative means the ground has dropped away below the ride height.
    /// </summary>
    public float Compression { get; private set; }

    /// <summary>Spring + damper force actually applied this step, in newtons. For the debug overlay.</summary>
    public float SpringForce { get; private set; }

    /// <summary>Rolling angle of the visible wheel, radians. Cosmetic.</summary>
    private float _spin;

    /// <summary>Where this corner was last frame, for measuring how far the wheel rolled.</summary>
    private Vector3 _lastPosition;

    /// <summary>Guards the first frame, where there is no previous position to subtract.</summary>
    private bool _hasLastPosition;

    /// <summary>Warned once per node about an unknown surface group, so a bad tile isn't a log flood.</summary>
    private bool _warnedUnknownSurface;

    public override void _Ready()
    {
        // Driven explicitly from Vehicle._PhysicsProcess so suspension, drive and steering all
        // land in a known order within the step.
        Enabled = true;
        ExcludeParent = true;
    }

    /// <summary>
    /// Read the ground and push this corner of the body up off it. Called once per physics step
    /// by <see cref="Vehicle"/>, before drive and grip.
    /// </summary>
    public void ApplyGroundForce(Vehicle vehicle)
    {
        ForceRaycastUpdate();

        IsGrounded = IsColliding();
        if (!IsGrounded)
        {
            LastCollider = null;
            Compression = 0.0f;
            SpringForce = 0.0f;
            return;
        }

        LastCollisionPoint = GetCollisionPoint();
        LastCollisionNormal = GetCollisionNormal();
        LastCollider = GetCollider();
        ReadSurface();

        Vector3 origin = GlobalPosition;
        Compression = RestLength - origin.DistanceTo(LastCollisionPoint);

        // Vertical speed of *this point on the body*, not of the body's centre — that difference
        // is what damps roll and pitch rather than just bounce.
        float closingSpeed = Vector3.Up.Dot(vehicle.VelocityAtPoint(origin));

        float force = (Compression * SpringStrength) - (closingSpeed * SpringDamping);

        // See MaxPullForce: the downward half is capped, the upward half isn't.
        if (force < 0.0f)
            force = Mathf.Max(force, -MaxPullForce);

        SpringForce = force;

        // Along world up, deliberately — not the surface normal and not the ray direction. A
        // normal-aligned spring shoves the car sideways off a banked or bumpy surface, which on
        // this track reads as the road spitting you off rather than as suspension.
        vehicle.ApplyForce(Vector3.Up * force, origin - vehicle.GlobalPosition);
    }

    /// <summary>
    /// Sit the visible wheel on the road, roll it, and turn it if this corner steers. Cosmetic
    /// only, and safe to call from <c>_Process</c> at frame rate rather than tick rate.
    ///
    /// Reads the ray itself rather than the state <see cref="ApplyGroundForce"/> cached, because
    /// a car somebody else is driving is frozen and never simulated on this machine — off the
    /// cached state every remote car in a race would drive around with its wheels hanging.
    /// </summary>
    public void UpdateVisual(Vehicle vehicle, float delta)
    {
        if (WheelNode is not { } wheel)
            return;

        // Droop to full travel when there's nothing under us, so a car in the air has its wheels
        // hanging rather than tucked into the arches.
        float drop = IsColliding()
            ? Mathf.Clamp(GlobalPosition.DistanceTo(GetCollisionPoint()), 0.0f, RestLength)
            : RestLength;

        wheel.Position = new Vector3(0.0f, -(drop - TireRadius), 0.0f);

        // Positive SteeringInput is left, and a positive rotation about local +Y swings the wheel's
        // −Z toward −X, which is also left. The two already agree; negating it here was the bug
        // that had the wheels pointing the wrong way.
        float steer = Steers ? vehicle.SteeringInput * Mathf.DegToRad(VisualSteerAngle) : 0.0f;

        // Rolled from road speed rather than from a simulated spin, because there is no spin to
        // simulate — the wheels are along for the ride.
        //
        // Measured from how far this node actually moved, not from the body's velocity: a car
        // somebody else is driving is a frozen puppet whose pose is assigned rather than
        // integrated, so its LinearVelocity is permanently zero and its wheels would never turn.
        if (TireRadius > 0.0f && delta > 0.0f)
        {
            Vector3 here = GlobalPosition;
            float travelled = _hasLastPosition
                ? -vehicle.GlobalTransform.Basis.Z.Dot(here - _lastPosition)
                : 0.0f;
            _lastPosition = here;
            _hasLastPosition = true;

            _spin = Mathf.Wrap(_spin + travelled / TireRadius, 0.0f, Mathf.Tau);
        }

        wheel.Rotation = new Vector3(_spin, steer, 0.0f);
    }

    /// <summary>
    /// Identify the surface from the <i>first node group</i> on whatever we hit — same contract
    /// as the old wheel, so every drivable collider that already worked still works.
    /// </summary>
    private void ReadSurface()
    {
        if (LastCollider is not Node node)
            return;

        Godot.Collections.Array<StringName> groups = node.GetGroups();
        if (groups.Count == 0)
            return;

        string group = groups[0].ToString();
        if (SurfaceGroups.IsKnown(group))
        {
            SurfaceType = group;
            return;
        }

        if (_warnedUnknownSurface)
            return;

        _warnedUnknownSurface = true;
        GD.PushWarning($"[GroundRay] {GetPath()} hit '{node.Name}' whose first group is " +
                       $"'{group}', which is not a surface. Keeping '{SurfaceType}'.");
    }
}
