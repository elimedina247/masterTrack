using Godot;

namespace MasterTrack.UI;

/// <summary>
/// Autoload. The one way scenes change: fade the screen to black, hold a small loading card up
/// while the next scene loads off the main thread, then fade back in on the other side.
///
/// An autoload rather than a node in any scene because it has to survive the very thing it
/// exists to cover — it draws <i>over</i> the gap between two scenes, so it can belong to
/// neither. Every <c>ChangeSceneToFile</c> call site in the game goes through
/// <see cref="TransitionTo"/> instead; a scene that swapped itself directly would cut, and one
/// cut in a game full of fades reads as a crash.
///
/// The load itself is <see cref="ResourceLoader.LoadThreadedRequest"/>, so the loading card can
/// actually animate — a synchronous load would freeze the fade at its darkest and turn the
/// cover into the thing it was covering for. Progress comes from the loader and drives the bar;
/// most scenes here load fast enough that the card is a blink, which is the good outcome.
///
/// Deliberately knows nothing about the network. The scene-ready handshake in
/// <c>GameManager</c> still runs inside the destination scene's <c>_Ready</c>; this only
/// changes what the eyes see while machines get there at their own speeds.
/// </summary>
public partial class SceneFader : CanvasLayer
{
	public static SceneFader Instance { get; private set; } = null!;

	/// <summary>Seconds to fade to black. Quick — this is a blink, not a scene of its own.</summary>
	private const float FadeOutSeconds = 0.25f;

	/// <summary>Seconds to fade back in. A touch slower, so arrival reads as arrival.</summary>
	private const float FadeInSeconds = 0.35f;

	private ColorRect _black = null!;
	private Label _loading = null!;
	private ProgressBar _progress = null!;

	/// <summary>Path being loaded, or null while idle. Doubles as the reentry guard.</summary>
	private string? _loadingPath;

	/// <summary>Whether the black is fully down and the threaded load may be polled.</summary>
	private bool _covered;

	private readonly Godot.Collections.Array _progressBox = new();

	public override void _Ready()
	{
		Instance = this;

		// Above everything, including the pause menu; and it still draws while the tree is
		// paused, because a transition may be asked for from a paused menu.
		Layer = 100;
		ProcessMode = ProcessModeEnum.Always;

		_black = new ColorRect
		{
			Color = new Color(0.0f, 0.0f, 0.0f, 0.0f),
			// Only solid black may eat clicks. See SetCoverInteractive.
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_black.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(_black);

		// The loading card: a word and a thin bar, centred low. Small on purpose — it is a
		// heartbeat, not a splash screen.
		_loading = new Label
		{
			Text = "Loading…",
			HorizontalAlignment = HorizontalAlignment.Center,
			Visible = false,
		};
		_loading.AddThemeFontSizeOverride("font_size", 26);
		_loading.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
		_loading.OffsetTop = -140;
		_loading.OffsetBottom = -100;
		_loading.OffsetLeft = -200;
		_loading.OffsetRight = 200;
		AddChild(_loading);

		_progress = new ProgressBar
		{
			MinValue = 0.0,
			MaxValue = 1.0,
			ShowPercentage = false,
			Visible = false,
		};
		_progress.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
		_progress.OffsetTop = -92;
		_progress.OffsetBottom = -84;
		_progress.OffsetLeft = -160;
		_progress.OffsetRight = 160;
		AddChild(_progress);

		SetProcess(false);
	}

	/// <summary>
	/// Fade out, load, switch, fade in. Safe to call from anywhere; a second call while a
	/// transition is already running is dropped rather than queued — the first destination wins,
	/// which is the only answer that cannot ping-pong.
	/// </summary>
	public void TransitionTo(string scenePath)
	{
		if (_loadingPath != null)
			return;

		_loadingPath = scenePath;
		_covered = false;

		// While covered the game underneath must not keep taking clicks through the black.
		_black.MouseFilter = Control.MouseFilterEnum.Stop;

		Tween tween = CreateTween();
		tween.TweenProperty(_black, "color:a", 1.0f, FadeOutSeconds);
		tween.TweenCallback(Callable.From(OnCovered));
	}

	/// <summary>The screen is black: start the threaded load and put the card up.</summary>
	private void OnCovered()
	{
		if (_loadingPath == null)
			return;

		_covered = true;
		_loading.Visible = true;
		_progress.Visible = true;
		_progress.Value = 0.0;

		ResourceLoader.LoadThreadedRequest(_loadingPath);
		SetProcess(true);
	}

	/// <summary>Poll the loader while covered. Runs only between fade-out and the switch.</summary>
	public override void _Process(double delta)
	{
		if (!_covered || _loadingPath == null)
			return;

		ResourceLoader.ThreadLoadStatus status =
			ResourceLoader.LoadThreadedGetStatus(_loadingPath, _progressBox);

		switch (status)
		{
			case ResourceLoader.ThreadLoadStatus.InProgress:
				if (_progressBox.Count > 0)
					_progress.Value = _progressBox[0].AsDouble();
				return;

			case ResourceLoader.ThreadLoadStatus.Loaded:
				FinishTransition();
				return;

			default:
				// A scene that will not load is a bug, but a black screen forever is a worse
				// one: say so, lift the cover, and leave the game where it was.
				GD.PushError($"[SceneFader] Failed to load {_loadingPath}; staying put.");
				AbortTransition();
				return;
		}
	}

	private void FinishTransition()
	{
		if (ResourceLoader.LoadThreadedGet(_loadingPath!) is not PackedScene scene)
		{
			AbortTransition();
			return;
		}

		SetProcess(false);
		_covered = false;
		_loadingPath = null;
		_loading.Visible = false;
		_progress.Visible = false;

		GetTree().ChangeSceneToPacked(scene);

		// The new scene's first frame happens under the black, so its own _Ready spikes are part
		// of the darkness rather than a visible hitch on arrival.
		Tween tween = CreateTween();
		tween.TweenProperty(_black, "color:a", 0.0f, FadeInSeconds);
		tween.TweenCallback(Callable.From(() => _black.MouseFilter = Control.MouseFilterEnum.Ignore));
	}

	private void AbortTransition()
	{
		SetProcess(false);
		_covered = false;
		_loadingPath = null;
		_loading.Visible = false;
		_progress.Visible = false;

		Tween tween = CreateTween();
		tween.TweenProperty(_black, "color:a", 0.0f, FadeInSeconds);
		tween.TweenCallback(Callable.From(() => _black.MouseFilter = Control.MouseFilterEnum.Ignore));
	}
}
