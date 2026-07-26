using Godot;

namespace MasterTrack.UI;

/// <summary>
/// Every look knob for the nitro screen effects, in one resource.
///
/// <see cref="SpeedBlur"/> and <see cref="SpeedLines"/> exist as separate nodes in every scene
/// that has a HUD, so tuning them per node means tuning them once per scene and watching the
/// values drift apart. Pointing all of them at one saved <c>.tres</c> makes it a single edit
/// that lands everywhere — the same trick <c>VehicleInputActions</c> uses for the input map.
///
/// Both effects fall back to a fresh instance of this when nothing is assigned, so a node with
/// no resource still works; it just tunes alone.
/// </summary>
[GlobalClass]
public partial class NitroEffectSettings : Resource
{
	// ---------------------------------------------------------------- Radial blur

	/// <summary>
	/// Smear at the corners of the screen while boosting, in UV. The knob to reach for first —
	/// past about 0.09 it stops reading as speed and starts reading as a smudge.
	/// </summary>
	[ExportGroup("Blur")]
	[Export] public float BlurStrength { get; set; } = 0.055f;

	/// <summary>How fast the blur ramps in when a boost lights, per second.</summary>
	[Export] public float BlurAttack { get; set; } = 12.0f;

	/// <summary>
	/// How fast it clears when the boost ends, per second. Slower than the attack, and tuned to
	/// sit near the camera's <c>NitroFovRelease</c> — the two are selling the same moment and
	/// shouldn't disagree about how long it lasts.
	/// </summary>
	[Export] public float BlurRelease { get; set; } = 4.5f;

	/// <summary>Distance from centre (0 middle, 1 corners) left completely sharp.</summary>
	[Export] public float BlurClearRadius { get; set; } = 0.18f;

	/// <summary>How far past <see cref="BlurClearRadius"/> the smear reaches full strength.</summary>
	[Export] public float BlurFalloff { get; set; } = 0.75f;

	// ---------------------------------------------------------------- Speed lines

	/// <summary>Peak opacity of the lines while boosting.</summary>
	[ExportGroup("Lines")]
	[Export] public float LinesStrength { get; set; } = 0.85f;

	/// <summary>How fast the lines come in when a boost lights, per second.</summary>
	[Export] public float LinesAttack { get; set; } = 14.0f;

	/// <summary>
	/// How fast they clear when the boost ends, per second. Quicker than the blur's release on
	/// purpose — the lines are the most literal of the effects, and lingering after the shove
	/// has gone reads as the effect being stuck rather than as momentum.
	/// </summary>
	[Export] public float LinesRelease { get; set; } = 6.0f;

	[Export] public Color LineColor { get; set; } = new(1.0f, 1.0f, 1.0f, 1.0f);

	/// <summary>
	/// How many lines around the full circle. Interacts with <see cref="LinesInnerRadius"/>:
	/// pushing the radius further out shortens every line, which makes the same count look
	/// denser in the space that's left.
	/// </summary>
	[Export] public float LineCount { get; set; } = 44.0f;

	/// <summary>Distance from centre (0 middle, 1 corners) left completely clear.</summary>
	[Export] public float LinesInnerRadius { get; set; } = 0.62f;

	/// <summary>How fast the streaks travel outward.</summary>
	[Export] public float LinesSpeed { get; set; } = 2.2f;

	/// <summary>
	/// Streaks along the radius. Higher packs more, shorter comets into the same space — if the
	/// lines read as dashes rather than streaks, this is what to bring down.
	/// </summary>
	[Export] public float LinesDensity { get; set; } = 1.6f;

	/// <summary>
	/// Half-thickness of a stroke, where 1.0 is centre to corner. Constant along the line's
	/// whole length — on a 1080p screen the default is about six pixels wide.
	/// </summary>
	[Export] public float LineWidth { get; set; } = 0.003f;

	/// <summary>Fraction of the thickness that is feathered. 0 gives hard, aliased edges.</summary>
	[Export] public float LinesSoftness { get; set; } = 0.6f;
}
