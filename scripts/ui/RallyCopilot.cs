using System.Collections.Generic;
using Godot;
using MasterTrack.Tiles;
using MasterTrack.Vehicles;

namespace MasterTrack.UI;

/// <summary>
/// The co-driver: reads the road ahead of the local car and calls the next tile before the car
/// arrives at it — a voice clip and a banner, top centre, the way pace notes come over the
/// intercom in a rally car.
///
/// <see cref="TrackFeed"/> already announces every tile <i>as it is placed</i>, to everybody.
/// This is the other half of that conversation: what matters to a driver is not what just got
/// played somewhere down the track, it is what is coming up <i>for them, now</i>. So where the
/// feed listens to the Track Master, the copilot watches its own car.
///
/// <b>Position-swept, not trigger-flagged.</b> The next tile is announced when the car is within
/// <see cref="LeadSeconds"/> of its entry seam at current speed — a fixed amount of <i>warning
/// time</i>, where a trigger volume at a fixed spot would give a fast car a fraction of the
/// notice a slow one gets. It is also how this codebase already detects arrival: the finish
/// sweep in <see cref="TrackController"/> reads car positions rather than collision bodies, so
/// that nothing depends on a car being on the ground and the right way up when it gets there. A
/// racer sailing over a tile off a launch pad still hears about the hairpin they are about to
/// land in.
///
/// Runs entirely locally — the car it watches is this machine's own, so there is nothing to
/// replicate and no server with an opinion. The Track Master has no car, never gets
/// <see cref="VehicleNode"/> bound, and the copilot stays quiet for them.
///
/// Wired like every other overlay: <see cref="MasterTrack.Game.RacerArena"/> hands the local
/// car to anything under the HUD implementing <see cref="IVehicleObserver"/>, and the track is
/// pointed at in the scene the same way <see cref="TrackFeed"/>'s is.
/// </summary>
[GlobalClass]
public partial class RallyCopilot : Control, IVehicleObserver
{
    /// <summary>The track whose tiles get called. Required.</summary>
    [Export] public TrackController? Track { get; set; }

    /// <summary>The car being driven. Set at runtime by whoever spawns the car.</summary>
    [Export] public Vehicle? VehicleNode { get; set; }

    /// <summary>
    /// How far ahead the call comes, in seconds at current speed. Around the time a tile takes
    /// to cross at racing pace, so each call lands about one tile before its subject.
    /// </summary>
    [Export] public float LeadSeconds { get; set; } = 2.5f;

    /// <summary>
    /// Floor on the speed the lead is scaled by, in m/s. Pure time-scaling never fires for a
    /// crawling car — distance over nothing is forever — so below this the call comes at a
    /// fixed <c>LeadSeconds * MinCallSpeed</c> metres instead.
    /// </summary>
    [Export] public float MinCallSpeed { get; set; } = 12.0f;

    /// <summary>How long the banner holds before it starts to fade.</summary>
    [Export] public float HoldSeconds { get; set; } = 2.2f;

    /// <summary>And how long the fade takes once it does.</summary>
    [Export] public float FadeSeconds { get; set; } = 0.7f;

    /// <summary>How far down from the top of the screen the banner sits, in pixels.</summary>
    [Export] public int TopOffset { get; set; } = 96;

    /// <summary>Voice clip volume, in dB. The intercom has to cut over the engine — which sits
    /// around 0 dB at full load, so the voice rides well above it rather than level with it.</summary>
    [Export] public float VoiceVolumeDb { get; set; } = 6.0f;

    /// <summary>
    /// Where the voice clips live. One file per piece, named after the piece's scene file and
    /// matched case-insensitively — <c>hairpinLeft.wav</c> and <c>HairpinLeft.wav</c> both say
    /// HairpinLeft.tscn. A piece with no clip still gets its banner, plus one warning naming
    /// the file it went looking for.
    /// </summary>
    public const string CalloutFolder = "res://assets/audio/callouts";

    /// <summary>
    /// How often the road ahead is swept, in seconds. Same rate and same reasoning as the
    /// finish sweep: the fastest car covers ten metres between sweeps against a lead distance
    /// of a hundred and fifty, so a call can land a tenth late but never be missed.
    /// </summary>
    private const float SweepInterval = 0.1f;

    private float _sweepCountdown;

    /// <summary>
    /// Highest tile index already called (or already driven, which supersedes calling it).
    /// Monotonic: a car knocked backwards or respawned does not get its notes read twice.
    /// </summary>
    private int _announcedThrough = -1;

    /// <summary>
    /// Each tile's entry-to-exit chord, cached by tile index. A tile never moves once placed,
    /// and <see cref="PlacedTile.ExitAnchor"/> recomputes the exit fold on every read — worth
    /// paying once per tile, not per sweep.
    /// </summary>
    private readonly List<(Vector3 A, Vector3 B)> _chords = new();

    /// <summary>Clip file paths by lower-cased piece name, from one scan of the folder.</summary>
    private readonly Dictionary<string, string> _clipPaths = new();

    /// <summary>Loaded clips by the same key. A null entry is a miss already warned about.</summary>
    private readonly Dictionary<string, AudioStream?> _clips = new();

    private AudioStreamPlayer _voice = null!;
    private PanelContainer _banner = null!;
    private StyleBoxFlat _bannerStyle = null!;
    private ColorRect _accentBar = null!;
    private Label _label = null!;
    private float _bannerAge;

    public override void _Ready()
    {
        // Something to read and hear, never something to click through.
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);

        if (Track == null)
            GD.PushWarning("[RallyCopilot] No Track assigned; nothing will ever be called.");

        // Set here rather than per-scene so the voice answers the pause menu's SFX slider,
        // exactly as NitroSound does.
        _voice = new AudioStreamPlayer { Bus = "SFX", VolumeDb = VoiceVolumeDb };
        AddChild(_voice);

        BuildBanner();
        ScanClips();

        // Nothing to fade until something has been said.
        SetProcess(false);
    }

    /// <summary>
    /// Release the wrappers while the engine is still alive: a refcounted resource left to
    /// .NET shutdown is disposed after native teardown, which can crash the process on exit.
    /// </summary>
    public override void _ExitTree()
    {
        _voice.Stop();
        _voice.Stream = null;

        foreach (AudioStream? clip in _clips.Values)
            clip?.Dispose();
        _clips.Clear();

        _bannerStyle?.Dispose();
        _bannerStyle = null!;
    }

    // ---- The sweep: where is the car, and what is next ----

    public override void _PhysicsProcess(double delta)
    {
        _sweepCountdown -= (float)delta;
        if (_sweepCountdown > 0.0f)
            return;

        _sweepCountdown = SweepInterval;

        if (Track == null || VehicleNode == null || !IsInstanceValid(VehicleNode))
            return;

        TrackGrid grid = Track.Grid;

        // The track shrank, which only a reset (or a sandbox undo) can do: whatever notes were
        // read were for a road that no longer exists.
        if (grid.Count < _chords.Count)
        {
            _chords.Clear();
            _announcedThrough = -1;
        }

        Vector3 position = VehicleNode.GlobalPosition;

        int current = NearestLiveTile(grid, position);
        if (current < 0)
            return;

        // Road already underfoot needs no introduction — and neither does anything behind it.
        // This is also what absorbs a skip: a car that flew clean over a tile has its counter
        // dragged past it, so the next call is for the road actually ahead.
        if (current > _announcedThrough)
            _announcedThrough = current;

        int next = current + 1;
        if (next >= grid.Count || next <= _announcedThrough)
            return;

        // A fixed amount of warning time, not of road. Distance is measured to the entry seam
        // — the point the racer actually crosses — in full 3D, so a spiral passing overhead
        // does not read as arriving.
        float callDistance = LeadSeconds * Mathf.Max(VehicleNode.Speed, MinCallSpeed);
        if (position.DistanceTo(grid.Tiles[next].EntryAnchor.Position) > callDistance)
            return;

        _announcedThrough = next;
        Announce(grid.Tiles[next]);
    }

    /// <summary>
    /// The live tile the car is on: nearest by distance to each tile's entry-to-exit chord.
    /// Nearest-chord rather than "has it crossed the next entry plane", because a car in this
    /// game can arrive on a tile from the air, sideways, or after falling off two tiles back —
    /// where it <i>is</i> is reliable in a way that how it got there is not. The chord is an
    /// approximation of the road's course, and height is included, which is what tells two
    /// tiles apart when the track corkscrews over itself.
    /// </summary>
    private int NearestLiveTile(TrackGrid grid, Vector3 position)
    {
        while (_chords.Count < grid.Count)
        {
            PlacedTile tile = grid.Tiles[_chords.Count];
            _chords.Add((tile.EntryAnchor.Position, tile.ExitAnchor.Position));
        }

        int nearest = -1;
        float best = float.MaxValue;

        for (int i = grid.OldestLiveIndex; i < grid.Count; i++)
        {
            (Vector3 a, Vector3 b) = _chords[i];
            float distance = DistanceSquaredToChord(position, a, b);

            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        return nearest;
    }

    private static float DistanceSquaredToChord(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 chord = b - a;
        float lengthSquared = chord.LengthSquared();

        // A degenerate chord is a point; a hairpin's is merely short. Both are fine.
        float t = lengthSquared > 0.0001f
            ? Mathf.Clamp((point - a).Dot(chord) / lengthSquared, 0.0f, 1.0f)
            : 0.0f;

        return point.DistanceSquaredTo(a + chord * t);
    }

    // ---- The call: a clip and a banner ----

    private void Announce(PlacedTile tile)
    {
        // Match on the full data rather than carrying catalog indices around: the catalog holds
        // pairs that differ in one field, and Match compares them all — including the scene
        // path, which is the whole identity of an authored piece.
        TileDefinition definition = TileCatalog.Match(tile.Data);

        _label.Text = Spaced(definition.DisplayName).ToUpperInvariant();
        _label.AddThemeColorOverride("font_color", definition.Accent.Lerp(Colors.White, 0.45f));
        _accentBar.Color = definition.Accent;

        _banner.Modulate = Colors.White;
        _banner.Visible = true;
        _bannerAge = 0.0f;
        SetProcess(true);

        PlayClip(definition);
    }

    /// <summary>
    /// A call that isn't a tile: the sentry's incoming-debuff warnings ride the same banner and
    /// the same voice slot as the pace notes, because to the driver they are the same thing —
    /// the co-driver shouting about what is about to happen. The clip is looked up by
    /// <paramref name="clipKey"/> in the callout folder, and looked up <i>quietly</i>: warning
    /// clips are optional, and a missing file should be banner-only, not a log per race.
    /// </summary>
    public void CallOut(string text, Color accent, string clipKey)
    {
        _label.Text = text;
        _label.AddThemeColorOverride("font_color", accent.Lerp(Colors.White, 0.45f));
        _accentBar.Color = accent;

        _banner.Modulate = Colors.White;
        _banner.Visible = true;
        _bannerAge = 0.0f;
        SetProcess(true);

        string key = ClipKey(clipKey);
        if (!_clips.TryGetValue(key, out AudioStream? clip))
        {
            clip = _clipPaths.TryGetValue(key, out string? path)
                ? GD.Load<AudioStream>(path)
                : null;
            _clips[key] = clip;
        }

        if (clip == null)
            return;

        _voice.Stop();
        _voice.Stream = clip;
        _voice.Play();
    }

    private void PlayClip(TileDefinition definition)
    {
        // The piece's file base name — "HairpinLeft" — which is how a sequence refers to a
        // piece everywhere else, so renaming a file renames its clip lookup with it.
        string key = ClipKey(definition.IsScenePiece
            ? definition.ScenePath.GetFile().GetBaseName()
            : definition.DisplayName);

        if (!_clips.TryGetValue(key, out AudioStream? clip))
        {
            clip = _clipPaths.TryGetValue(key, out string? path)
                ? GD.Load<AudioStream>(path)
                : null;

            if (clip == null)
            {
                GD.PushWarning($"[RallyCopilot] No clip for '{definition.DisplayName}' — "
                               + $"expected something like {CalloutFolder}/{key}.wav "
                               + "(name matched case-insensitively). Banner only.");
            }

            // Cached even as a miss, so one absent file is one warning rather than one per lap.
            _clips[key] = clip;
        }

        if (clip == null)
            return;

        // A new note supersedes whatever the voice was still saying: late but current beats
        // finished but stale, and the co-driver only has one mouth.
        _voice.Stop();
        _voice.Stream = clip;
        _voice.Play();
    }

    /// <summary>
    /// One scan of the clip folder into a name map. Suffix handling is the same as
    /// <see cref="Tiles.Tool.PieceCatalog"/>'s: an exported project lists imported files under
    /// their sidecar names, and they have to be asked for under their original ones.
    /// </summary>
    private void ScanClips()
    {
        using DirAccess? dir = DirAccess.Open(CalloutFolder);
        if (dir == null)
        {
            GD.PushWarning($"[RallyCopilot] {CalloutFolder} does not exist; every call will be "
                           + "banner-only until it does.");
            return;
        }

        foreach (string file in dir.GetFiles())
        {
            string name = file;

            if (name.EndsWith(".import", System.StringComparison.Ordinal))
                name = name[..^".import".Length];
            else if (name.EndsWith(".remap", System.StringComparison.Ordinal))
                name = name[..^".remap".Length];

            if (name.GetExtension().ToLowerInvariant() is not ("wav" or "ogg" or "mp3"))
                continue;

            // In the editor a clip is listed twice — itself and its sidecar — and both spell
            // the same entry, so the overwrite is harmless.
            _clipPaths[ClipKey(name.GetBaseName())] = $"{CalloutFolder}/{name}";
        }
    }

    /// <summary>
    /// A clip name reduced to what it means: lower-cased, separators dropped. "S_Bend",
    /// "s-bend" and "sBend" are all somebody spelling SBend.tscn, and a lookup that made them
    /// rename files to prove it would be pedantry, not a contract.
    /// </summary>
    private static string ClipKey(string name)
    {
        var key = new System.Text.StringBuilder(name.Length);

        foreach (char c in name)
        {
            if (c is not ('_' or '-' or ' '))
                key.Append(char.ToLowerInvariant(c));
        }

        return key.ToString();
    }

    /// <summary>
    /// "HairpinLeft" is a file name; "HAIRPIN LEFT" is a pace note. Splits the camel-case
    /// piece name at each word boundary, keeping runs of capitals together: SBend becomes
    /// "S Bend", ToiletBowl "Toilet Bowl".
    /// </summary>
    private static string Spaced(string name)
    {
        var spaced = new System.Text.StringBuilder(name.Length + 4);

        for (int i = 0; i < name.Length; i++)
        {
            bool boundary = i > 0 && char.IsUpper(name[i])
                && (char.IsLower(name[i - 1])
                    || (i + 1 < name.Length && char.IsLower(name[i + 1])));

            if (boundary)
                spaced.Append(' ');

            spaced.Append(name[i]);
        }

        return spaced.ToString();
    }

    // ---- The banner itself ----

    public override void _Process(double delta)
    {
        _bannerAge += (float)delta;

        float over = _bannerAge - HoldSeconds;
        if (over <= 0.0f)
            return;

        if (over >= FadeSeconds)
        {
            _banner.Visible = false;
            SetProcess(false);
            return;
        }

        _banner.Modulate = Colors.White with { A = 1.0f - over / FadeSeconds };
    }

    /// <summary>
    /// One reusable banner, top centre, in <see cref="TrackFeed"/>'s visual language — dark
    /// panel, accent bar, the tile's own colour — but sized to be read at speed in peripheral
    /// vision rather than studied in a corner.
    /// </summary>
    private void BuildBanner()
    {
        _bannerStyle = new StyleBoxFlat { BgColor = new Color(0.07f, 0.08f, 0.10f, 0.86f) };
        _bannerStyle.SetCornerRadiusAll(6);
        _bannerStyle.SetContentMarginAll(0);

        _banner = new PanelContainer
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f,
            AnchorRight = 0.5f,
            OffsetTop = TopOffset,
            GrowHorizontal = GrowDirection.Both,
        };
        _banner.AddThemeStyleboxOverride("panel", _bannerStyle);
        AddChild(_banner);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        _banner.AddChild(margin);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 12);
        margin.AddChild(row);

        _accentBar = new ColorRect
        {
            CustomMinimumSize = new Vector2(7, 0),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddChild(_accentBar);

        _label = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", 32);
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        _label.AddThemeConstantOverride("outline_size", 7);
        row.AddChild(_label);
    }
}
