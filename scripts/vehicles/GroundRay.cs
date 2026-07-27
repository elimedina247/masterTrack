using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// One corner of the hovercraft: a ray cast down from the chassis with a spring and damper on it.
///
/// This is <b>not</b> a wheel, and it carries no wheel. It has no tire model, no brakes, no spin
/// and no drive torque — grip and drive live on the body in <see cref="Vehicle"/>, applied as
/// central forces. All this does is hold that corner of the car off the ground and report what it
/// is standing on.
///
/// The visible wheel is a <see cref="WheelVisual"/> parented to the <i>posed shell</i>, which
/// reads this ray's contact point and nothing else. Keeping the two apart is what lets the shell
/// lean and swing while the wheels stay on the road: a mesh hung off this node would be locked to
/// the chassis and would visibly come adrift from the body it belongs to.
///
/// Place these at the <b>top of the suspension travel</b>: the body hangs
/// <see cref="RestLength"/> below the ray origin at rest.
/// </summary>
[GlobalClass]
public partial class GroundRay : RayCast3D
{
    /// <summary>
    /// Width of the tire in metres. Read by <see cref="SkidMarks"/> for the strip width, and by
    /// nothing else.
    /// </summary>
    [Export] public float TireWidth { get; set; } = 0.25f;

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
    /// Fraction of <see cref="RestLength"/> the spring may compress before the bump stop starts
    /// taking over, 0..1.
    /// </summary>
    public float BumpStopStart { get; set; } = 0.6f;

    /// <summary>
    /// How much stiffer the bump stop is than the spring, at the very end of the travel.
    ///
    /// Without one there is nothing between the spring running out and the chassis arriving: the
    /// collision box reaches the road well before the suspension does, and a rigid-body collision
    /// with the road at speed simply deletes the car's momentum. That is what made ramps feel
    /// like driving into a wall — the concave crease at the foot of a climb needs more travel
    /// than the car physically has, so it bottomed out on every one.
    /// </summary>
    public float BumpStopStrength { get; set; } = 12.0f;

    /// <summary>
    /// Largest downward pull this corner may apply when the ray reaches past
    /// <see cref="RestLength"/>, in newtons.
    ///
    /// The spring term goes negative once the ground is further away than the ride height, which
    /// sucks the car onto the road over a crest — worth keeping, it's a lot of what stops an
    /// arcade car pogoing. 0 disables the pull entirely.
    ///
    /// Paired with <see cref="PullFadeDistance"/>, which is what keeps it off real jumps.
    /// </summary>
    public float MaxPullForce { get; set; } = 11000.0f;

    /// <summary>
    /// How far the ground may recede past <see cref="RestLength"/> before the downward pull has
    /// faded to nothing, in metres.
    ///
    /// Short, deliberately. A crease on a ramp drops the road away by centimetres and wants
    /// holding onto; an edge drops it away by everything and wants letting go of. This is the
    /// line between them — and without it, a pull strong enough to follow a crease also sucks the
    /// car back down off every jump.
    /// </summary>
    public float PullFadeDistance { get; set; } = 0.12f;

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

        // Bump stop: past BumpStopStart the rate climbs with the square of how far into the last
        // of the travel we are, so it is soft where it engages and very hard at the end. Squared
        // rather than linear on purpose — a linear stop is either felt as a second bump halfway
        // through the travel or is too weak to stop anything.
        float stopStart = RestLength * BumpStopStart;
        if (Compression > stopStart && RestLength > stopStart)
        {
            float into = (Compression - stopStart) / (RestLength - stopStart);
            force += into * into * BumpStopStrength * SpringStrength * (Compression - stopStart);
        }

        // The downward half is capped and faded out; the upward half is neither.
        //
        // The fade is what separates a crease from a cliff. Following a crease means the ground
        // has receded by centimetres and the car should be held onto it; going over an edge means
        // it has receded by everything and the car should be allowed to fly. A flat cap can't
        // tell those apart, so a strong enough pull to do the first sucks the car back down out
        // of the second and there are no jumps left in the game.
        if (force < 0.0f)
        {
            float pastRest = -Compression;
            float fade = 1.0f - Mathf.Clamp(pastRest / Mathf.Max(PullFadeDistance, 0.001f),
                                            0.0f, 1.0f);
            force = Mathf.Max(force * fade, -MaxPullForce);
        }

        SpringForce = force;

        // Along world up, deliberately — not the surface normal and not the ray direction. A
        // normal-aligned spring shoves the car sideways off a banked or bumpy surface, which on
        // this track reads as the road spitting you off rather than as suspension.
        vehicle.ApplyForce(Vector3.Up * force, origin - vehicle.GlobalPosition);
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
