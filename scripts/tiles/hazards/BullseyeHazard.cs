using Godot;
using MasterTrack.Sentry;

namespace MasterTrack.Tiles.Hazards;

/// <summary>
/// A target painted on the road, and a missile that knows where it is.
///
/// The rigged device with the longest reach and the least local machinery: the bullseye itself
/// does nothing at all, it is a mark. Fired, it calls down a <see cref="SentryMissile"/> — the
/// same ordnance the sentry's own missile button drops, arriving the same way, with the same
/// two seconds of ring and whistle before it lands.
///
/// <b>Reusing the missile rather than reimplementing it is the whole design.</b> The racers have
/// already learned what a red ring on the road and a whistle overhead mean; a bullseye that
/// delivered something subtly different would be teaching them a second lesson for no reason.
/// What the bullseye changes is not the weapon but the economics — the sentry pays once, during
/// the rig, for a spot on the road they can hit again and again.
///
/// Which also makes it the most *readable* trap in the kit from a racer's side, and deliberately
/// so. A twenty-eight metre target painted across the road is not subtle. You know the tile is
/// mined; you do not know which lap.
/// </summary>
public partial class BullseyeHazard : TrackHazard
{
    /// <summary>Seconds the target flashes while the missile is on its way — the missile's own
    /// descent, so the mark stops pulsing at the moment it is hit.</summary>
    private const float InboundSeconds = 2.1f;

    /// <summary>Seconds after the strike before it can be called again. Long: this is a missile,
    /// and a spot on the road that can be shelled every two seconds is not a trap, it is a wall.</summary>
    private const float ReloadSeconds = 11.0f;

    private enum Stage
    {
        /// <summary>Painted, quiet, ready.</summary>
        Marked,

        /// <summary>Called. Something is coming down.</summary>
        Inbound,

        /// <summary>Struck, and waiting out the reload.</summary>
        Reloading,
    }

    private Node3D _rings = null!;
    private Stage _stage = Stage.Marked;
    private float _elapsed;

    public override bool CanDetonate => true;

    public override bool IsReady => _stage == Stage.Marked;

    public override void _Ready() => _rings = GetNode<Node3D>("Rings");

    public override void Detonate()
    {
        if (_stage != Stage.Marked)
            return;

        _stage = Stage.Inbound;
        _elapsed = 0.0f;

        // Parented to the scene root rather than to this hazard, because the missile falls
        // through the world's up and the slot it is standing in may be banked — a missile
        // inheriting a corner's roll would come down sideways. It cleans itself up on impact.
        var missile = new SentryMissile { Name = "BullseyeMissile" };
        (GetTree().CurrentScene ?? GetTree().Root).AddChild(missile);
        missile.GlobalPosition = GlobalPosition;
    }

    public override void _Process(double delta)
    {
        if (_stage == Stage.Marked)
            return;

        _elapsed += (float)delta;

        if (_stage == Stage.Inbound)
        {
            // A lock-on throb, quickening as the missile closes. Done with scale rather than
            // colour so the target reads the same to a racer squinting at it from four hundred
            // metres as it does to the sentry looking straight down on it.
            float rate = 9.0f + _elapsed * 9.0f;
            float pulse = 1.0f + 0.09f * Mathf.Sin(_elapsed * rate);
            _rings.Scale = new Vector3(pulse, 1.0f, pulse);

            if (_elapsed < InboundSeconds)
                return;

            _rings.Scale = Vector3.One;
            _stage = Stage.Reloading;
            _elapsed = 0.0f;
            return;
        }

        if (_elapsed >= ReloadSeconds)
        {
            _stage = Stage.Marked;
            _elapsed = 0.0f;
        }
    }
}
