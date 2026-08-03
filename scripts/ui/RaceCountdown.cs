using Godot;

namespace MasterTrack.UI;

/// <summary>
/// The lights-out moment: <b>3… 2… 1… RACE!</b> across the middle of the screen as the cars
/// arrive, ending in the signal <see cref="Finished"/> that actually releases the controls.
///
/// The match used to begin by simply existing — cars appeared and were already drivable, and
/// nobody could point at the instant the race started. This is that instant. Every peer runs its
/// own copy from the moment its cars spawn, which is the same broadcast everywhere, so the
/// screens agree to within a network beat; the countdown is presentation, and the only thing
/// with teeth — the input lock — is local to each driver anyway.
///
/// Each beat pops in big and shrinks toward rest, so the rhythm is readable from the corner of
/// an eye while the player stares at the road. The hook for audio later is <see cref="OnBeat"/>:
/// one call per number and one for RACE!, which is exactly where the bleeps and the horn go.
/// </summary>
public partial class RaceCountdown : Control
{
	/// <summary>The countdown reached RACE! — controls go live now. Fired exactly once.</summary>
	[Signal] public delegate void FinishedEventHandler();

	/// <summary>Seconds per number. Slightly under a full second keeps the grid impatient.</summary>
	private const float BeatSeconds = 0.85f;

	/// <summary>Seconds RACE! stays up after the release. Long enough to be read at speed.</summary>
	private const float RaceFlashSeconds = 0.9f;

	/// <summary>Scale a beat pops in at before shrinking to rest.</summary>
	private const float PopScale = 1.9f;

	private Label _big = null!;
	private float _clock;
	private int _lastBeat = -1;
	private bool _released;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

		_big = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
		};
		_big.AddThemeFontSizeOverride("font_size", 140);
		_big.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
		_big.AddThemeConstantOverride("outline_size", 18);
		_big.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
		_big.OffsetLeft = -400;
		_big.OffsetRight = 400;
		_big.OffsetTop = -160;
		_big.OffsetBottom = 40;
		AddChild(_big);
	}

	public override void _Process(double delta)
	{
		_clock += (float)delta;

		int beat = Mathf.FloorToInt(_clock / BeatSeconds);

		if (beat != _lastBeat)
		{
			_lastBeat = beat;
			OnBeat(beat);
		}

		// The pop: each beat lands huge and settles, so the rhythm reads without being looked at.
		float into = (_clock % BeatSeconds) / BeatSeconds;
		float scale = Mathf.Lerp(PopScale, 1.0f, Mathf.Min(into * 3.0f, 1.0f));
		_big.PivotOffset = _big.Size * 0.5f;
		_big.Scale = Vector2.One * scale;

		if (_released && _clock >= BeatSeconds * 3.0f + RaceFlashSeconds)
			QueueFree();
	}

	/// <summary>
	/// One call per moment of the sequence — 0, 1, 2 are the numbers, 3 is RACE!. This is the
	/// audio hook: when the countdown gains a voice, each beep and the horn fire from here.
	/// </summary>
	private void OnBeat(int beat)
	{
		switch (beat)
		{
			case 0:
			case 1:
			case 2:
				_big.Text = (3 - beat).ToString();
				_big.SelfModulate = new Color(1.0f, 0.85f, 0.25f);
				break;

			case 3:
				_big.Text = "RACE!";
				_big.SelfModulate = new Color(0.35f, 1.0f, 0.45f);

				if (!_released)
				{
					_released = true;
					EmitSignal(SignalName.Finished);
				}
				break;
		}
	}
}
