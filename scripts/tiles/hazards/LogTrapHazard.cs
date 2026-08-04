using Godot;
using MasterTrack.Audio;

namespace MasterTrack.Tiles.Hazards;

/// <summary>
/// A gantry over the road with a log slung from ropes at one side: four posts, a top frame, and
/// several tonnes of timber held up where everybody can see it. Fired, the ropes let go and the
/// log falls into a pendulum that swings <i>across</i> the road, lying along it — anything caught
/// in the middle of the tile at the bottom of the arc gets swept sideways off the racing line.
///
/// <b>It threatens the middle, not the whole width.</b> A gantry this tall on ropes this long can
/// reach about ten metres either side of centre before the log would have to come down below the
/// road to go further; that is geometry, not a setting. It makes the trap a reason to go wide
/// rather than a wall — which is the better hazard anyway, because it punishes a line instead of
/// punishing speed.
///
/// <b>The first hazard that owns a tile rather than a spot on one.</b> It mounts to a
/// <see cref="HazardSlotKind.FullWidth"/> slot, which no other hazard uses, so a piece either
/// declares the gantry mounting or cannot take one — and a piece that does gives up its whole
/// middle to it. That is the trade the price is paying for: a log trap is not furniture on a
/// straight, it is what that straight now <i>is</i>.
///
/// Everything about it is legible from a long way off, which is the rigged-device contract. A
/// racer sees a fourteen-metre gantry from the far end of the straight and knows the tile is
/// dangerous; what they cannot know is *when*, and that is the sentry's half.
///
/// The swing is integrated here rather than left to a physics joint, the spring plate's rule and
/// for the spring plate's reason: every peer has to see the log in the same place at the same
/// moment, and a real constrained body would diverge inside a second. A scripted pendulum is
/// deterministic from the broadcast alone, and it is also far easier to tune — the arc is three
/// numbers rather than an emergent property of six.
/// </summary>
public partial class LogTrapHazard : TrackHazard
{
    // ---- The swing ----

    /// <summary>How far back the log is held, in radians from straight down. Near horizontal:
    /// the drop has to be worth watching, and a log that starts low never gets moving.</summary>
    private const float HeldAngle = 1.36f;

    /// <summary>Distance from the pivot to the log's centre, in metres. Must match
    /// <c>LogTrap.tscn</c> — it is what puts the log at car height at the bottom of the arc.</summary>
    private const float RopeLength = 10.2f;

    /// <summary>
    /// Gravity for the pendulum alone, in m/s². Well above the world's, because a log on a ten
    /// metre rope under real gravity has a four second period and reads as a balloon. This is the
    /// number to turn if the swing feels wrong; nothing else in the game sees it.
    /// </summary>
    private const float SwingGravity = 40.0f;

    /// <summary>Fraction of angular speed bled off per second. Sized so the log is worth dodging
    /// for several passes and then plainly finished.</summary>
    private const float SwingDamping = 0.34f;

    // ---- Timing ----

    /// <summary>Seconds of visible strain before the ropes let go. The reaction window, and the
    /// same beat the spring trap gives: long enough to shout, short enough not to brake.</summary>
    private const float CreakSeconds = 0.55f;

    /// <summary>Seconds the log is left swinging before it is winched back up.</summary>
    private const float SwingSeconds = 7.0f;

    /// <summary>Seconds to haul it back to the held angle. Slow and obvious — the trap visibly
    /// reloading is information the racers are entitled to.</summary>
    private const float WinchSeconds = 3.5f;

    /// <summary>Seconds after the winch before it can be fired again.</summary>
    private const float RearmSeconds = 5.0f;

    // ---- The hit ----

    /// <summary>Speed the log adds to whatever it sweeps, in m/s, along its own direction of
    /// travel — which is sideways, so a hit is a car leaving the racing line rather than a car
    /// being slowed. Mass-scaled like every other hazard kick, so the whole fleet is thrown
    /// alike.</summary>
    private const float HitSpeed = 24.0f;

    /// <summary>Speed added straight up on a hit. A car that is only shoved slides; a car that is
    /// shoved and lifted tumbles, and the tumble is the joke.</summary>
    private const float HitLift = 6.0f;

    /// <summary>The rope-snap. The spring's till of a sound is wrong here — this one is timber
    /// and it is falling — so it borrows the tile impact, which is the heaviest thing to hand.</summary>
    private const string SnapSfxPath = "res://assets/audio/hazards/tile_impact.mp3";

    private enum Stage
    {
        /// <summary>Held up, ropes taut, doing nothing. What racers see nearly all the time.</summary>
        Held,

        /// <summary>Sagging and trembling. The ropes are going.</summary>
        Creaking,

        /// <summary>Loose, swinging, dangerous.</summary>
        Swinging,

        /// <summary>Being hauled back up to the held angle.</summary>
        Winching,

        /// <summary>Up, and waiting out the re-arm.</summary>
        Reloading,
    }

    private Node3D _pivot = null!;
    private Node3D _log = null!;
    private Area3D _sweep = null!;

    private Stage _stage = Stage.Held;
    private float _elapsed;

    /// <summary>Angle from straight down, in radians. Positive is back toward the oncoming cars.</summary>
    private float _theta = HeldAngle;

    /// <summary>Angular speed, in radians per second.</summary>
    private float _omega;

    /// <summary>Where the winch started from, so the haul back is a clean interpolation.</summary>
    private float _winchFrom;

    /// <summary>The log's global position last physics frame, for working out which way it is
    /// actually moving. Read off the transform rather than derived from the pendulum maths — the
    /// answer is then right by construction however the arc is later re-tuned.</summary>
    private Vector3 _lastLogPosition;
    private Vector3 _logVelocity;

    public override bool CanDetonate => true;

    public override bool IsReady => _stage == Stage.Held;

    public override void _Ready()
    {
        _pivot = GetNode<Node3D>("Pivot");
        _log = GetNode<Node3D>("Pivot/Log");
        _sweep = GetNode<Area3D>("Pivot/Log/Sweep");

        // Live from the start: the log is solid and eight metres up even while held, and a car
        // launched into it by something else should hit it. Only the *kick* waits for the swing.
        _sweep.BodyEntered += OnSwept;

        ApplyAngle(HeldAngle);
        _lastLogPosition = _log.GlobalPosition;
    }

    /// <summary>Cut it loose. Deterministic from this call alone, so every peer's log falls the
    /// same way at the same moment.</summary>
    public override void Detonate()
    {
        if (_stage != Stage.Held)
            return;

        _stage = Stage.Creaking;
        _elapsed = 0.0f;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        if (_stage != Stage.Held && _stage != Stage.Reloading)
            _elapsed += dt;
        else if (_stage == Stage.Reloading)
            TickReload(dt);

        switch (_stage)
        {
            case Stage.Creaking:
                TickCreak();
                break;

            case Stage.Swinging:
                TickSwing(dt);
                break;

            case Stage.Winching:
                TickWinch();
                break;
        }

        // Measured every frame including at rest, so a hit landing on the first frame of the
        // swing already knows which way the log is going.
        Vector3 now = _log.GlobalPosition;
        _logVelocity = dt > 0.0f ? (now - _lastLogPosition) / dt : Vector3.Zero;
        _lastLogPosition = now;
    }

    /// <summary>Sagging against the ropes, with a tremble in it. The log has not moved anywhere
    /// yet — this is the beat that says it is about to.</summary>
    private void TickCreak()
    {
        float t = Mathf.Clamp(_elapsed / CreakSeconds, 0.0f, 1.0f);

        // A few degrees of droop, plus a shudder that quickens as the ropes go.
        float shudder = Mathf.Sin(_elapsed * (26.0f + _elapsed * 40.0f)) * 0.012f * t;
        ApplyAngle(HeldAngle - 0.10f * t * t + shudder);

        if (t < 1.0f)
            return;

        Sfx.PlayAt(this, SnapSfxPath, _log.GlobalPosition,
                   volumeDb: 3.0f, unitSize: 40.0f, pitchJitter: 0.06f);

        _stage = Stage.Swinging;
        _elapsed = 0.0f;
        _omega = 0.0f;
    }

    /// <summary>
    /// The pendulum, integrated straight: angular acceleration is <c>-(g/L)·sin θ</c>, damped a
    /// little each second so the swing runs down instead of going forever. Semi-implicit — speed
    /// updated before position — because it is stable at this step size where the naive order
    /// slowly gains energy and the log would climb higher every pass.
    /// </summary>
    private void TickSwing(float dt)
    {
        _omega += -(SwingGravity / RopeLength) * Mathf.Sin(_theta) * dt;
        _omega *= Mathf.Max(0.0f, 1.0f - SwingDamping * dt);
        ApplyAngle(_theta + _omega * dt);

        if (_elapsed < SwingSeconds)
            return;

        _stage = Stage.Winching;
        _elapsed = 0.0f;
        _winchFrom = _theta;
    }

    /// <summary>Hauled back up. Eased so it arrives gently rather than snapping to the stop.</summary>
    private void TickWinch()
    {
        float t = Mathf.Clamp(_elapsed / WinchSeconds, 0.0f, 1.0f);
        float eased = t * t * (3.0f - 2.0f * t);

        ApplyAngle(Mathf.Lerp(_winchFrom, HeldAngle, eased));

        if (t < 1.0f)
            return;

        _stage = Stage.Reloading;
        _elapsed = 0.0f;
        _omega = 0.0f;
    }

    private void TickReload(float dt)
    {
        _elapsed += dt;
        if (_elapsed >= RearmSeconds)
            _stage = Stage.Held;
    }

    /// <summary>Point the whole assembly — ropes and log together — at an angle. The pivot is the
    /// only thing that ever moves; everything hanging off it comes along, which is what keeps the
    /// ropes attached to the thing they are holding.</summary>
    private void ApplyAngle(float theta)
    {
        _theta = theta;

        // About Z, so the log swings *across* the road while lying along it. This one line
        // decides the trap's whole character: rotate about X instead and you get a log that
        // spans the width and swings up the road at the cars, which is a different hazard.
        _pivot.Rotation = new Vector3(0.0f, 0.0f, theta);
    }

    /// <summary>
    /// Something got swept. The kick goes along the log's own measured direction of travel, so a
    /// car caught on the way out is thrown the other way from one caught on the way back — plus a
    /// lift, because a car that is only shoved slides and a car that is lifted tumbles.
    ///
    /// Only while swinging: the log is solid the whole time and a car that drives into it while
    /// it is parked overhead should bump off it, not be fired across the map by a stationary
    /// object.
    /// </summary>
    private void OnSwept(Node3D body)
    {
        if (_stage != Stage.Swinging || body is not RigidBody3D car)
            return;

        // Sideways, in whichever direction the log happens to be travelling — the fallback is the
        // sweep axis rather than the road's, because the log crosses the road rather than
        // running down it.
        Vector3 along = _logVelocity.LengthSquared() > 0.01f
            ? _logVelocity.Normalized()
            : GlobalBasis.X;

        car.ApplyCentralImpulse((along * HitSpeed + GlobalBasis.Y.Normalized() * HitLift) * car.Mass);
    }
}
