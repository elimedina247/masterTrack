using Godot;
using MasterTrack.Audio;
using MasterTrack.Sentry;

namespace MasterTrack.Tiles.Hazards;

/// <summary>
/// A charge sat on the road that the sentry lights by hand. Fired, it blinks and beeps through a
/// short fuse and then takes the whole stretch of road with it.
///
/// The cheap decisive option, and the one that asks the most of the sentry's timing. Everything
/// else in the kit throws a car somewhere; this one simply owns a circle of road for two seconds
/// and dares anybody to be in it. Lighting it early is a wasted charge, lighting it late is a
/// bang behind the pack, and the fuse is exactly long enough that a racer who is watching can
/// get out — which is the trade that keeps it fair.
///
/// The fuse is the same beat every delayed thing in this game uses
/// (<see cref="SentryActions.LeadSeconds"/>), and the blast is the barrel bomb's, through the
/// shared <see cref="SentryBlast"/>. A racer who has learned what a blinking red drum means does
/// not have to learn it twice.
///
/// It reloads rather than being spent: a fresh drum is delivered after the smoke clears. Strictly
/// that is silly, and it is still right — the builder bought a *position*, not a shell, and a rig
/// that quietly loses devices as the race runs would end with the sentry holding nothing.
/// </summary>
public partial class BombTrapHazard : TrackHazard
{
    /// <summary>Seconds between lighting it and the bang. The window a racer gets to clear out.</summary>
    private const float FuseSeconds = SentryActions.LeadSeconds;

    /// <summary>How far the blast still throws cars, in metres. The barrel bomb's reach — this
    /// is the same ordnance sitting still instead of being thrown.</summary>
    private const float ExplosionRadius = TrackTile.Size * 1.15f;

    /// <summary>Speed the blast adds to a car at the centre, in m/s. Squared falloff, so the
    /// violence lives near the drum and the rim of the circle is a hard shove.</summary>
    private const float ExplosionStrength = 115.0f;

    /// <summary>Seconds before a fresh drum arrives. The longest reload in the kit: this thing
    /// owns fifty metres of road, and it should not be able to own it twice in ten seconds.</summary>
    private const float ReloadSeconds = 14.0f;

    /// <summary>The fuse, out loud — the audible twin of the blinking lamp.</summary>
    private const string CountdownSfxPath = "res://assets/audio/hazards/bomb_countdown.mp3";

    private enum Stage
    {
        /// <summary>Sat there, dark, ready.</summary>
        Armed,

        /// <summary>Lit. Blinking and beeping down.</summary>
        Burning,

        /// <summary>Gone, and waiting on a replacement.</summary>
        Reloading,
    }

    private Node3D _drum = null!;
    private StandardMaterial3D? _lamp;
    private AudioStreamPlayer3D? _countdown;

    private Stage _stage = Stage.Armed;
    private float _elapsed;

    /// <summary>The point the blast measures from: the middle of the drum, not its foot.</summary>
    private Vector3 Center => GlobalPosition + GlobalBasis.Y.Normalized() * 1.8f;

    public override bool CanDetonate => true;

    public override bool IsReady => _stage == Stage.Armed;

    public override void _Ready()
    {
        _drum = GetNode<Node3D>("Drum");

        // Through the mesh instance's override, not the mesh's own material: a PackedScene's
        // sub-resources are shared by every instance of it, so writing the mesh would have the
        // second bomb on a track blinking the first one's lamp.
        if (_drum.GetNodeOrNull<MeshInstance3D>("Lamp") is { } lamp
            && lamp.Mesh?.SurfaceGetMaterial(0) is StandardMaterial3D paint)
        {
            _lamp = (StandardMaterial3D)paint.Duplicate();
            _lamp.EmissionEnabled = true;
            _lamp.Emission = new Color(1.0f, 0.2f, 0.12f);
            _lamp.EmissionEnergyMultiplier = 0.0f;
            lamp.MaterialOverride = _lamp;
        }
    }

    /// <summary>Free the wrapper while the engine is still alive — a refcounted resource left to
    /// .NET shutdown is disposed after native teardown, which can crash the process on exit.</summary>
    public override void _ExitTree()
    {
        _lamp?.Dispose();
        _lamp = null;
    }

    public override void Detonate()
    {
        if (_stage != Stage.Armed)
            return;

        _stage = Stage.Burning;
        _elapsed = 0.0f;

        // Rides the drum and dies with it, so however much of the countdown plays it always ends
        // in the explosion — the cut into the bang *is* the detonation.
        _countdown = Sfx.Attach(_drum, CountdownSfxPath, volumeDb: 2.0f, unitSize: 30.0f);
    }

    public override void _Process(double delta)
    {
        if (_stage == Stage.Armed)
            return;

        _elapsed += (float)delta;

        if (_stage == Stage.Burning)
        {
            TickFuse();
            return;
        }

        if (_elapsed < ReloadSeconds)
            return;

        // A fresh drum, and dark again.
        _drum.Visible = true;
        if (_lamp != null)
            _lamp.EmissionEnergyMultiplier = 0.0f;

        _stage = Stage.Armed;
        _elapsed = 0.0f;
    }

    private void TickFuse()
    {
        // Blinking faster as it runs down — the countdown the racers actually read, since the
        // beep only carries as far as the audio does.
        float rate = 7.0f + _elapsed * 16.0f;
        if (_lamp != null)
            _lamp.EmissionEnergyMultiplier = Mathf.Sin(_elapsed * rate) > 0.0f ? 3.4f : 0.2f;

        if (_elapsed < FuseSeconds)
            return;

        SentryBlast.Explode(this, Center, ExplosionRadius, ExplosionStrength);

        // The drum goes with the bang rather than sitting there as a dud that still looks live.
        _drum.Visible = false;
        _countdown?.QueueFree();
        _countdown = null;

        _stage = Stage.Reloading;
        _elapsed = 0.0f;
    }
}
