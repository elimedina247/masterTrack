using Godot;

namespace MasterTrack.Tiles.Hazards;

/// <summary>
/// A wedge that rises out of the road and stays there, launching whoever meets it forward and
/// up. Cruel-<i>funny</i> rather than cruel: the throw is along the direction of travel, so a
/// racer who lines it up on purpose gets free air and keeps their speed — and a sentry who
/// drops one mid-corner turns somebody's apex into a flight. Sometimes the trap is a gift,
/// which is what keeps it a toy instead of a wall.
///
/// The rise takes the game's one shared fuse (<see cref="Sentry.SentryActions.LeadSeconds"/>),
/// blinking the countdown language everything else speaks. While rising it is a ghost —
/// collision and trigger both wait for the wedge to be fully proud of the road, because solid
/// geometry sweeping up through a car's floor is the physics engine's least favourite trick.
///
/// The launch is an impulse along the slot's own frame — up the bank on a banked piece — and
/// mass-scaled like the pads', a change in velocity every car shares. Thrown cars are airborne
/// immediately, which is what keeps the tire solve out of the conversation.
/// </summary>
public partial class PopUpRampHazard : TrackHazard
{
    /// <summary>Seconds from planted to proud of the road. The reaction window.</summary>
    private const float RiseSeconds = Sentry.SentryActions.LeadSeconds;

    /// <summary>Speed the ramp adds along the direction of travel, in m/s. A push, not the
    /// point — momentum does most of the forward work.</summary>
    private const float ForwardBoost = 12.0f;

    /// <summary>Speed the ramp adds off the road surface, in m/s. The flight itself: about
    /// 16 m of air under normal weight, a great deal more under moon gravity.</summary>
    private const float UpwardBoost = 18.0f;

    /// <summary>Wedge footprint: most of a lane wide, a car-length and a half long.</summary>
    private const float RampWidth = 10.0f;
    private const float RampLength = 9.0f;
    private const float RampHeight = 2.6f;

    private StandardMaterial3D _paint = null!;
    private CsgBox3D _wedge = null!;
    private Area3D _trigger = null!;
    private float _age;

    private bool Risen => _age >= RiseSeconds;
    private bool _armed;

    public override void _Ready()
    {
        // Amber, the colour of machinery about to move. Blinks through the rise.
        _paint = HouseMaterial(new Color(0.95f, 0.55f, 0.10f));

        // The wedge is a tilted slab: leading edge at road level, trailing edge proud. Cheaper
        // and more readable than true wedge geometry, and the difference is under the paint.
        //
        // Orientation is fixed by the slot and the slot faces travel: a car crosses the slot
        // travelling along its local -Z, arriving over the +Z side. So the +Z edge is the low
        // one — positive pitch about X raises the -Z end — and the car always runs UP the
        // slope. There is no wrong way for it to face, because the racers only ever come one way.
        _wedge = new CsgBox3D
        {
            Size = new Vector3(RampWidth, 0.8f, RampLength),
            Material = _paint,
            RotationDegrees = new Vector3(Mathf.RadToDeg(Mathf.Atan2(RampHeight, RampLength)), 0.0f, 0.0f),
            // Starts sunk; _Process raises it.
            Position = new Vector3(0.0f, -RampHeight, 0.0f),
            UseCollision = false,
        };
        AddChild(_wedge);

        // The thrower: a box over the wedge, armed only once risen. Mask is everything, the
        // pads' rule.
        _trigger = new Area3D
        {
            Name = "RampTrigger",
            Position = new Vector3(0.0f, 2.0f, 0.0f),
            CollisionMask = uint.MaxValue,
            Monitoring = false,
        };
        _trigger.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(RampWidth, 4.0f, RampLength) },
        });
        AddChild(_trigger);

        _trigger.BodyEntered += body =>
        {
            if (body is not RigidBody3D car)
                return;

            // Along the slot's frame: -Z is travel, Y is off the road surface.
            Vector3 throwDirection = (-GlobalBasis.Z * ForwardBoost + GlobalBasis.Y * UpwardBoost);
            car.ApplyCentralImpulse(throwDirection * car.Mass);
        };
    }

    /// <summary>Free the wrappers while the engine is still alive — a refcounted resource left
    /// to .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
    public override void _ExitTree()
    {
        _paint.Dispose();
        _paint = null!;
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;

        if (!Risen)
        {
            // Rising: the wedge climbs out of the road, blinking quicker toward live — the
            // condemned-tile countdown, spoken by a thing arriving instead of leaving.
            float up = Mathf.Clamp(_age / RiseSeconds, 0.0f, 1.0f);
            float eased = up * up;
            _wedge.Position = new Vector3(0.0f, -RampHeight * (1.0f - eased) + RampHeight * 0.35f * eased, 0.0f);

            float blink = Mathf.Sin(_age * (8.0f + _age * 6.0f)) > 0.0f ? 1.0f : 0.5f;
            _paint.AlbedoColor = new Color(0.95f * blink, 0.55f * blink, 0.10f * blink);
            return;
        }

        if (!_armed)
        {
            _armed = true;
            _wedge.Position = new Vector3(0.0f, RampHeight * 0.35f, 0.0f);
            _wedge.UseCollision = true;
            _trigger.Monitoring = true;
            _paint.AlbedoColor = new Color(0.95f, 0.55f, 0.10f);
            SetProcess(false);
        }
    }
}
