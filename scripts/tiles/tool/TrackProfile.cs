using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// The shape of the road <i>across</i> its direction of travel: how wide it is, how it banks, how
/// thick the slab is and what stands along its edges.
///
/// This is the half of a piece that <see cref="TrackPiece"/>'s spine does not describe. A spine says
/// where the road goes; a profile says what is being carried along it. Every tile the old
/// <c>TrackTile.Shapes</c> built by hand — a level straight, a banked arc, a climbing ramp — is the
/// same sweep with a different pair of those two, which is the whole reason this exists.
///
/// <b>Heights here are relative to the road plane, and the road plane is what the chain joins on.</b>
/// A profile may lift its edges as far as it likes; it must not move the centre. See
/// <see cref="TrackPiece"/> for why the seams have to stay level.
/// </summary>
[Tool]
[GlobalClass]
public partial class TrackProfile : Resource
{
	/// <summary>
	/// Width of the road, in metres. Defaults to the catalog's, because a piece that is not as wide
	/// as its neighbours meets them with a step.
	/// </summary>
	[Export(PropertyHint.Range, "4,200,0.5")]
	public float Width { get; set; } = TileCatalog.TileSize;

	/// <summary>
	/// Fraction of the width, measured from the inside edge, that stays flat.
	///
	/// Carried over from the hand-built corners, where it was load-bearing and still is: the inside
	/// line has to be drivable by a car that arrived slowly, and a corner banked all the way across
	/// has no such line. At 1.0 the whole section is flat, which is what a straight wants.
	/// </summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float FlatFraction { get; set; } = 1.0f;

	/// <summary>
	/// Bank angle at the outer lip, in degrees.
	///
	/// <b>Not a look — a speed.</b> A bank holds a car with no help from the tires at exactly
	/// <c>v = sqrt(g * r * tan(theta))</c>, so this and the spine's radius together decide the speed
	/// at which riding the wall flat out is the car holding itself up. The hand-built corners used 60
	/// degrees against a 63 m radius, which lands neutral at about 199 km/h on a 200 km/h top speed.
	/// Change the radius and this wants re-reading against it.
	/// </summary>
	[Export(PropertyHint.Range, "0,80,0.5")]
	public float EdgeBankDegrees { get; set; } = 60.0f;

	/// <summary>
	/// Which edge the bank climbs toward. False banks to the right of travel, true to the left.
	///
	/// The outside of a corner is the side the car is thrown toward, which is the <i>opposite</i> of
	/// the way it turns: a right-hand turn banks to the left. Getting this backwards builds a corner
	/// that tips you off it, and it is the first thing to check when one drives wrong.
	/// </summary>
	[Export] public bool BankToLeft { get; set; }

	/// <summary>
	/// Lateral samples across the road.
	///
	/// Cheap now, in a way it very much was not before. The hand-built corners paid a
	/// <c>MeshInstance3D</c>, a <c>BoxMesh</c>, a <c>CollisionShape3D</c> and a <c>BoxShape3D</c> for
	/// every strip of every segment — 9 strips was a budget fought over, and raising it segfaulted the
	/// physics server. A swept section pays vertices instead, so resolution costs almost nothing and
	/// this can simply be high enough that the lip reads smooth.
	/// </summary>
	[Export(PropertyHint.Range, "2,48,1")]
	public int LateralSamples { get; set; } = 16;

	/// <summary>
	/// Thickness of the road slab, in metres.
	///
	/// Also the tunnelling margin, which is the number actually worth defending. Collision is a
	/// watertight surface rather than a stack of solid boxes, so what stops a car passing through the
	/// road is the physics step being short enough that it cannot cross the slab in one. At the
	/// project's 120 Hz a car at <c>TopSpeed</c> moves 0.46 m per step, so this is better than three
	/// times the margin it needs. Thinning it is a physics decision, not a visual one.
	/// </summary>
	[Export(PropertyHint.Range, "0.2,40,0.1")]
	public float Thickness { get; set; } = 1.6f;

	/// <summary>Height of the barriers along the edges, in metres. Car-scale — a taller wall on a
	/// wider road buys nothing.</summary>
	[Export(PropertyHint.Range, "0,20,0.1")]
	public float WallHeight { get; set; } = 2.0f;

	[Export(PropertyHint.Range, "0.1,10,0.05")]
	public float WallThickness { get; set; } = TileCatalog.TileSize * 0.045f;

	/// <summary>Whether a barrier stands along each edge. Off on both for the tiles whose whole
	/// threat is being shoved off the road, which needs there to be nothing to be shoved into.</summary>
	[Export] public bool LeftWall { get; set; } = true;

	[Export] public bool RightWall { get; set; } = true;

	/// <summary>
	/// Width of the painted centre line, in metres. Zero paints none.
	///
	/// It is painted from the spine, which is the point of the rewrite: the old build had five
	/// different ideas of where the middle of the road was — straights struck it at x=0, corners
	/// painted the flat/bank seam instead and had no centre line at all, and the loop and the split
	/// jumped it sideways with no taper. Here the spine <i>is</i> the centre line, so it cannot
	/// disagree with itself.
	/// </summary>
	[Export(PropertyHint.Range, "0,10,0.05")]
	public float CentreLineWidth { get; set; } = TileCatalog.TileSize * 0.05f;

	[Export] public Color RoadColor { get; set; } = new(0.36f, 0.37f, 0.40f);

	[Export] public Color WallColor { get; set; } = new(0.70f, 0.74f, 0.80f);

	[Export] public Color LineColor { get; set; } = new(0.96f, 0.96f, 0.92f);

	/// <summary>Half the road's width — the lateral reach of the surface either side of the spine.</summary>
	public float HalfWidth => Width * 0.5f;

	/// <summary>
	/// The section as a run of points across the road, from the left edge to the right, each a
	/// lateral offset from the spine and a height above the road plane.
	///
	/// The bank is built the way the hand-written corners built it, because the reasoning held up.
	/// The angle is <i>squared</i> across the banked part, so the surface leaves the flat almost
	/// imperceptibly and keeps its steepness for the top — a linear rise puts usable bank right where
	/// the flat ends and reads as a kink rather than as the road turning up. The samples are then
	/// spaced by the inverse of that, so each one turns through about the same angle instead of
	/// covering the same width.
	/// </summary>
	public Vector2[] Section()
	{
		int samples = Mathf.Max(2, LateralSamples);
		float flat = Mathf.Clamp(FlatFraction, 0.0f, 1.0f);
		float maxBank = Mathf.DegToRad(Mathf.Clamp(EdgeBankDegrees, 0.0f, 85.0f));

		// Measured from the inside edge outward, then flipped at the end if the bank climbs the
		// other way. Building it one-handed and mirroring is far less error-prone than carrying a
		// sign through the integration.
		var section = new List<Vector2>(samples + 1);

		float flatWidth = Width * flat;
		float bankedWidth = Width - flatWidth;

		// The flat part is a plane, so two points describe it exactly however many the caller asked
		// for. Everything else is spent where the curvature is.
		section.Add(new Vector2(0.0f, 0.0f));

		if (flatWidth > 0.0f)
			section.Add(new Vector2(flatWidth, 0.0f));

		if (bankedWidth > 0.0f && maxBank > 0.0f)
		{
			float height = 0.0f;
			float previousReach = flatWidth;

			for (int i = 1; i <= samples; i++)
			{
				float u = Mathf.Sqrt((float)i / samples);
				float previousU = Mathf.Sqrt((i - 1.0f) / samples);

				float reach = flatWidth + bankedWidth * u;
				float mid = (u + previousU) * 0.5f;

				// Integrate the slope across the strip rather than evaluating a height, so the
				// surface is the shape the angles describe instead of an approximation of it.
				height += (reach - previousReach) * Mathf.Tan(maxBank * mid * mid);

				section.Add(new Vector2(reach, height));
				previousReach = reach;
			}
		}
		else if (bankedWidth > 0.0f)
		{
			section.Add(new Vector2(Width, 0.0f));
		}

		// Re-centre on the spine, and point the bank at whichever edge was asked for.
		var result = new Vector2[section.Count];
		for (int i = 0; i < section.Count; i++)
		{
			Vector2 point = section[i];
			float lateral = point.X - HalfWidth;
			result[i] = new Vector2(BankToLeft ? -lateral : lateral, point.Y);
		}

		// Mirroring reverses the run, and a section has to stay ordered left to right or every quad
		// swept from it comes out inside-out.
		if (BankToLeft)
			System.Array.Reverse(result);

		return result;
	}
}
