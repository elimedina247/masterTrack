using System.Collections.Generic;
using Godot;

namespace MasterTrack.Tiles.Tool;

/// <summary>
/// A waterpark bowl: a full funnel of revolution with a hole in the middle, built as one closed
/// CSG solid around this node's origin.
///
/// <b>The funnel is the continuous version of "roads that wrap around each other while
/// touching".</b> A ribbon of road spiralled into a bowl needs every loop to meet the next
/// exactly, which is a seam per lap to get wrong; revolving one profile is all of those loops at
/// once with no seams at all. The racers' own physics then draws the spiral — any line down the
/// funnel is a surface you can drive, which is precisely what makes a bowl a bowl: fast and high
/// or slow and low is the driver's problem, not the geometry's.
///
/// The profile is steep at the rim and flattens toward the hole —
/// <c>height ∝ radius^<see cref="ProfilePower"/></c> — which is the waterpark shape: a wall to
/// arrive onto, a saucer to drain across, and a lip you finally drop through.
///
/// A <see cref="CsgMesh3D"/> so it unions with the chute and tunnel under the same Build, takes
/// the combiner's collision, and freezes under the piece's ordinary Bake.
/// </summary>
[Tool]
[GlobalClass]
public partial class FunnelBowl : CsgMesh3D
{
	/// <summary>Radius of the outer rim, in metres.</summary>
	[Export(PropertyHint.Range, "20,150,1")]
	public float RimRadius { get; set; } = 66.0f;

	/// <summary>Height of the rim above the node's origin.</summary>
	[Export(PropertyHint.Range, "5,80,0.5")]
	public float RimHeight { get; set; } = 30.0f;

	/// <summary>Radius of the hole the riders finally drop through.</summary>
	[Export(PropertyHint.Range, "4,40,0.5")]
	public float HoleRadius { get; set; } = 13.0f;

	/// <summary>Height of the hole's lip. What is under the hole is somebody else's geometry —
	/// the piece puts a catch tunnel there.</summary>
	[Export(PropertyHint.Range, "0,30,0.5")]
	public float HoleHeight { get; set; } = 6.0f;

	/// <summary>
	/// How the wall's steepness is distributed: height grows with radius to this power. 1 is a
	/// plain cone; higher keeps the middle saucer-flat and stands the outer wall up — 1.8 reads
	/// as the ride in the brochure photos.
	/// </summary>
	[Export(PropertyHint.Range, "1,3,0.05")]
	public float ProfilePower { get; set; } = 1.8f;

	/// <summary>
	/// Swap the power-law funnel for the lower quarter of a sphere — the Globe of Death. The
	/// wall arrives at dead vertical exactly at the rim, which no power ever reaches, so a rider
	/// with the speed can circle level on the wall itself and everything slower spirals the
	/// curve down to the hole. <see cref="RimHeight"/> and <see cref="ProfilePower"/> are
	/// ignored: a sphere's bowl is as tall as its geometry says — the rim sits
	/// <c>sqrt(RimRadius² − HoleRadius²)</c> above the hole lip. The shell offsets radially
	/// from the sphere's centre rather than straight down, because a vertical wall dropped
	/// vertically is a shell of zero thickness.
	/// </summary>
	[Export]
	public bool Spherical { get; set; }

	/// <summary>
	/// Degrees the spherical wall continues past vertical — the Globe of Death's hood. The rim
	/// curls inward over the wall by this much, so a rider fired in hot rides up into a ceiling
	/// that returns them to the wall instead of a lip that lets them fly the bowl entirely.
	/// Spherical only; the funnel profile has no vertical to pass.
	/// </summary>
	[Export(PropertyHint.Range, "0,60,1")]
	public float RimOverhangDegrees { get; set; }

	/// <summary>Shell thickness, straight down from the surface.</summary>
	[Export(PropertyHint.Range, "0.5,10,0.1")]
	public float Thickness { get; set; } = 1.8f;

	/// <summary>
	/// Facets around the bowl. The budget that matters is scallop depth, not angle: the ground
	/// rays ignore facet normals by design, so the ride only feels the sagitta —
	/// <c>R·(1 − cos(180°/segments))</c>. Keep it under a couple of centimetres at the bowl's
	/// radius and the suspension reads the wall as continuous.
	/// </summary>
	[Export(PropertyHint.Range, "16,256,1")]
	public int RadialSegments { get; set; } = 64;

	/// <summary>Rings from hole to rim. Same scallop budget as <see cref="RadialSegments"/>.</summary>
	[Export(PropertyHint.Range, "4,64,1")]
	public int ProfileSteps { get; set; } = 12;

	[Export]
	public Material? SurfaceMaterial { get; set; }

	private int _shape;

	/// <summary>Same rule as <see cref="BankedRoad"/>: the mesh is derived data and never saves.</summary>
	public override void _ValidateProperty(Godot.Collections.Dictionary property)
	{
		if (property["name"].AsStringName() == CsgMesh3D.PropertyName.Mesh)
		{
			property["usage"] = (int)(property["usage"].As<PropertyUsageFlags>()
									  & ~PropertyUsageFlags.Storage);
		}
	}

	public override void _Ready()
	{
		Rebuild();

		if (Engine.IsEditorHint())
			SetProcess(true);
	}

	public override void _Process(double delta)
	{
		var hash = new System.HashCode();
		hash.Add(RimRadius);
		hash.Add(RimHeight);
		hash.Add(HoleRadius);
		hash.Add(HoleHeight);
		hash.Add(ProfilePower);
		hash.Add(Spherical);
		hash.Add(RimOverhangDegrees);
		hash.Add(Thickness);
		hash.Add(RadialSegments);
		hash.Add(ProfileSteps);

		int shape = hash.ToHashCode();
		if (shape == _shape)
			return;

		_shape = shape;
		Rebuild();
	}

	public void Rebuild()
	{
		int around = Mathf.Max(16, RadialSegments);
		int steps = Mathf.Max(4, ProfileSteps);

		// The profile, hole lip outward to rim.
		var radius = new float[steps + 1];
		var height = new float[steps + 1];

		// The sphere's hole sits at this polar angle; stepping the profile by angle rather than
		// radius keeps the facets even where the wall stands up.
		float holeAngle = Mathf.Asin(Mathf.Clamp(HoleRadius / RimRadius, 0.0f, 1.0f));

		for (var i = 0; i <= steps; i++)
		{
			float t = (float)i / steps;

			if (Spherical)
			{
				float rimAngle = Mathf.Pi * 0.5f + Mathf.DegToRad(RimOverhangDegrees);
				float angle = Mathf.Lerp(holeAngle, rimAngle, t);
				radius[i] = RimRadius * Mathf.Sin(angle);
				height[i] = HoleHeight + RimRadius * (Mathf.Cos(holeAngle) - Mathf.Cos(angle));
			}
			else
			{
				radius[i] = Mathf.Lerp(HoleRadius, RimRadius, t);
				height[i] = HoleHeight + (RimHeight - HoleHeight) * Mathf.Pow(t, ProfilePower);
			}
		}

		var shell = new MeshBucket { Smooth = true, Material = SurfaceMaterial };
		Vector3 drop = Vector3.Down * Thickness;

		// The underside: straight down for the funnel, radially out from the sphere's centre for
		// the globe — a vertical wall dropped vertically would be a zero-thickness shell.
		var sphereCentre = new Vector3(0.0f, HoleHeight + RimRadius * Mathf.Cos(holeAngle), 0.0f);
		float shellScale = (RimRadius + Thickness) / RimRadius;

		Vector3 Outer(Vector3 point)
			=> Spherical ? sphereCentre + (point - sphereCentre) * shellScale : point + drop;

		// The color panels a road counts off in metres become pie wedges here: UV.x grows with
		// the angle around the bowl, scaled so a whole even number of shader panels closes the
		// circle — mid-bowl each wedge is about one panel long. Spinning through a funnel is
		// exactly where the eye loses its bearings, and the wedges are the fix.
		float midCircumference = Mathf.Tau * (HoleRadius + RimRadius) * 0.5f;
		int wedges = Mathf.Max(2, 2 * Mathf.RoundToInt(midCircumference / 27.0f / 2.0f));
		float panelSpan = wedges * 27.0f;

		// Each direction is computed once and the last facet wraps back to spokes[0], so the
		// closing seam reuses bit-identical floats. Computed fresh (cos of tau is a hair under
		// one), the seam is a hairline crack, the solid is not manifold, and CSG throws the whole
		// bowl away without a word — measured as a piece whose funnel simply was not there.
		var spokes = new Vector2[around];
		for (var s = 0; s < around; s++)
		{
			float angle = Mathf.Tau * s / around;
			spokes[s] = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
		}

		for (var s = 0; s < around; s++)
		{
			Vector2 nearRing = spokes[s];
			Vector2 farRing = spokes[(s + 1) % around];

			// Not wrapped: the last facet's far edge is panelSpan, not zero, so the wedge
			// pattern arrives back at the seam exactly where it left.
			var nearUv = new Vector2(panelSpan * s / around, 0.0f);
			var farUv = new Vector2(panelSpan * (s + 1) / around, 0.0f);

			Vector3 At(int i, Vector2 ring)
				=> new(radius[i] * ring.X, height[i], radius[i] * ring.Y);

			for (var i = 0; i < steps; i++)
			{
				// Top surface, then the underside wound the other way.
				SolidMesh.Quad(shell, At(i, nearRing), At(i, farRing),
							   At(i + 1, farRing), At(i + 1, nearRing),
							   nearUv, farUv, farUv, nearUv);
				SolidMesh.Quad(shell, Outer(At(i, nearRing)), Outer(At(i + 1, nearRing)),
							   Outer(At(i + 1, farRing)), Outer(At(i, farRing)),
							   nearUv, nearUv, farUv, farUv);
			}

			// The rim's outer edge and the hole's inner lip close the shell into a solid ring.
			SolidMesh.Quad(shell, At(steps, nearRing), At(steps, farRing),
						   Outer(At(steps, farRing)), Outer(At(steps, nearRing)),
						   nearUv, farUv, farUv, nearUv);
			SolidMesh.Quad(shell, At(0, nearRing), Outer(At(0, nearRing)),
						   Outer(At(0, farRing)), At(0, farRing),
						   nearUv, nearUv, farUv, farUv);
		}

		Mesh = SolidMesh.Commit(new List<MeshBucket> { shell });
	}
}
