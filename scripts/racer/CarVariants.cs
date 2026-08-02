using Godot;
using System.Collections.Generic;

namespace MasterTrack.Racer;

/// <summary>
/// The car models and the seven colours they come in — everything that decides what a
/// player's car <i>looks</i> like, and nothing about how it drives.
///
/// Kept in one place because three separate things read it: the car builds itself from it, the
/// Track Master's board colours its chevrons from it, and the lobby list names people by it. A
/// player is recognised by their colour, so all three have to agree or the colour stops meaning
/// anything.
/// </summary>
public static class CarVariants
{
    /// <summary>
    /// One body and one pair of rims. <see cref="ModelledFrontRadius"/> /
    /// <see cref="ModelledRearRadius"/> are what the wheels were actually built at in Blender —
    /// see <c>assets/cars/README.md</c> — which is what lets the rig scale them to a single
    /// physical radius instead of each variant handling differently.
    /// </summary>
    public sealed record Variant(
        string Name,
        string BodyPath,
        string RimLeftPath,
        string RimRightPath,
        float ModelledFrontRadius,
        float ModelledRearRadius);

    private const string BodyDir = "res://assets/cars/Body/";
    private const string RimDir = "res://assets/cars/Rims/";

    public static readonly IReadOnlyList<Variant> All = new[]
    {
        new Variant("A_Wedge", $"{BodyDir}A_Wedge_Body.fbx",
                    $"{RimDir}A_Wedge_Rim_L.fbx", $"{RimDir}A_Wedge_Rim_R.fbx", 0.28f, 0.28f),
        new Variant("B_Bubble", $"{BodyDir}B_Bubble_Body.fbx",
                    $"{RimDir}B_Bubble_Rim_L.fbx", $"{RimDir}B_Bubble_Rim_R.fbx", 0.30f, 0.30f),
        new Variant("C_Cartoon", $"{BodyDir}C_Cartoon_Body.fbx",
                    $"{RimDir}C_Cartoon_Rim_L.fbx", $"{RimDir}C_Cartoon_Rim_R.fbx", 0.24f, 0.36f),
        // D is the Cartoon body with a shifted canopy; its wheels are the Cartoon's, so it
        // borrows those rims. E/F/G share the hachi wheelbase and one rim pair the same way.
        new Variant("D_Cartoon", $"{BodyDir}D_Cartoon_Body.fbx",
                    $"{RimDir}C_Cartoon_Rim_L.fbx", $"{RimDir}C_Cartoon_Rim_R.fbx", 0.24f, 0.36f),
        new Variant("E_Hachi", $"{BodyDir}E_Hachi_Body.fbx",
                    $"{RimDir}E_Hachi_Rim_L.fbx", $"{RimDir}E_Hachi_Rim_R.fbx", 0.29f, 0.29f),
        new Variant("F_Hachi", $"{BodyDir}F_Hachi_Body.fbx",
                    $"{RimDir}E_Hachi_Rim_L.fbx", $"{RimDir}E_Hachi_Rim_R.fbx", 0.29f, 0.29f),
        new Variant("G_Panda", $"{BodyDir}G_Panda_Body.fbx",
                    $"{RimDir}E_Hachi_Rim_L.fbx", $"{RimDir}E_Hachi_Rim_R.fbx", 0.29f, 0.29f),
        // H is a hand-built hatchback: C_Cartoon's shell reworked with a longer nose, a fastback
        // cabin and a low wide wing. Its wheels are untouched from C, so it takes C's rims and
        // C's staggered radii — see tools/export_car_variant.py for how it leaves the blend.
        new Variant("H_Hatch", $"{BodyDir}H_Hatch_Body.fbx",
                    $"{RimDir}C_Cartoon_Rim_L.fbx", $"{RimDir}C_Cartoon_Rim_R.fbx", 0.24f, 0.36f),
    };

    /// <summary>
    /// The rainbow, in order. Seven of them, dealt without replacement, which is what caps the
    /// lobby: past seven people two players would be the same colour and neither could be told
    /// apart on the board.
    ///
    /// Lifted off the pure hues towards something that reads on tarmac under the sky's ambient
    /// light — a pure blue car in shadow is a black car.
    /// </summary>
    public static readonly Color[] Palette =
    {
        new(0.94f, 0.24f, 0.24f), // red
        new(0.98f, 0.55f, 0.15f), // orange
        new(0.98f, 0.85f, 0.20f), // yellow
        new(0.36f, 0.82f, 0.33f), // green
        new(0.25f, 0.53f, 0.95f), // blue
        new(0.40f, 0.35f, 0.80f), // indigo
        new(0.70f, 0.40f, 0.90f), // violet
    };

    public static readonly string[] PaletteNames =
        { "Red", "Orange", "Yellow", "Green", "Blue", "Indigo", "Violet" };

    /// <summary>
    /// The paint the C_Cartoon body ships with. What an unassigned car wears — the solo test
    /// drive, and the dummy car the board builds ahead of — so it looks exactly as it always did
    /// when nobody has dealt it a colour.
    /// </summary>
    public static readonly Color DefaultPaint = new(0.349f, 0.701f, 0.978f);

    /// <summary>The variant <c>Racer.tscn</c> is authored with, and what an unassigned car keeps.</summary>
    public const int DefaultVariantIndex = 2;

    /// <summary>
    /// Where the whip antenna may be mounted, in <c>BodyRig</c> space (right is +X, forward is
    /// -Z). Back centre is where <c>Racer.tscn</c> authors it; the other two land on the roof and
    /// the right-hand hood corner. One set of points for every body — the shells differ enough
    /// that these are compromises, tuned by eye rather than measured off any one model.
    /// </summary>
    public static readonly Vector3[] AntennaSpots =
    {
        new(0.0f, 0.85f, 1.1f),   // back centre — the classic RC look
        new(0.0f, 0.95f, 0.0f),   // centre of the roof
        new(0.45f, 0.75f, -0.9f), // front right, on the hood corner
    };

    public static readonly string[] AntennaSpotNames = { "Back", "Center", "Front Right" };

    /// <summary>Matches where <c>Racer.tscn</c> places the antenna, so "no choice" changes nothing.</summary>
    public const int DefaultAntennaSpot = 0;

    public static Variant At(int index) => All[Mathf.PosMod(index, All.Count)];

    public static Color ColourAt(int index) => Palette[Mathf.PosMod(index, Palette.Length)];

    public static string ColourNameAt(int index) => PaletteNames[Mathf.PosMod(index, PaletteNames.Length)];

    public static Vector3 AntennaSpotAt(int index) => AntennaSpots[Mathf.PosMod(index, AntennaSpots.Length)];
}
