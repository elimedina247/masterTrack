using Godot;
using MasterTrack.Networking;

namespace MasterTrack.Racer;

/// <summary>
/// A coloured arrowhead floating over a car, so the other racers can be found at a glance.
///
/// The problem it solves is the one every track in this game creates: the road climbs, drops,
/// hairpins back over itself and is full of things that throw a car into the air, so an opponent
/// is very often behind a wall, under a ramp or somewhere off the top of the screen. A car is a
/// few pixels of colour at any distance; a marker drawn over everything is not.
///
/// It wears the car's own <see cref="RacerController.PaintColor"/> — the same colour the Track
/// Master's board chevron uses — so a player is one colour wherever they are looked at from: the
/// road, the board, and the lobby roster.
///
/// Only ever over <i>other</i> people's cars, and only in a real session. Over your own it would
/// be an arrow parked in the middle of your view pointing at yourself, and in solo there is nobody
/// to find.
/// </summary>
[GlobalClass]
public partial class RacerChevron : MeshInstance3D
{
	/// <summary>The car this marker belongs to. Required.</summary>
	[Export] public RacerController? RacerNode { get; set; }

	/// <summary>How far above the car's origin the arrowhead floats, in metres.</summary>
	[Export] public float Height { get; set; } = 2.6f;

	/// <summary>
	/// On-screen size of the arrowhead. Not metres: the material is drawn at a fixed size, so this
	/// is how big it is in the viewport rather than in the world — see <see cref="BuildMaterial"/>.
	///
	/// Small, and it has to be. Fixed size means a car on the far side of the board carries exactly
	/// the same marker as one alongside you, so anything sized to look right up close is a placard
	/// hanging over the horizon.
	/// </summary>
	[Export] public float Size { get; set; } = 0.035f;

	/// <summary>
	/// How much taller than wide the arrowhead is. Above 1 it reads as a pointer; at 1 it is an
	/// equilateral triangle, which from a distance is closer to a blob.
	/// </summary>
	[Export] public float Aspect { get; set; } = 1.15f;

	public override void _Ready()
	{
		if (RacerNode is not { } racer)
		{
			GD.PushWarning("[RacerChevron] No RacerNode assigned; the marker is inert.");
			Visible = false;
			return;
		}

		// Solo has one car and it is yours. A session where this is the only car — the moment
		// before anyone else has loaded in — still gets its marker, because the others are coming
		// and nothing here needs to change when they do.
		if (!NetworkManager.Instance.IsNetworked || racer.IsLocalPlayer)
		{
			Visible = false;
			return;
		}

		Position = new Vector3(0.0f, Height, 0.0f);
		Mesh = BuildArrowhead();
		MaterialOverride = BuildMaterial(racer.PaintColor);
	}

	/// <summary>
	/// A downward-pointing triangle in the local XY plane, apex at the origin so the point sits at
	/// the bottom of the marker and the body hangs above it.
	///
	/// One mesh rather than the three boxes the board's chevron is built from, and that is not a
	/// style choice: billboarding is per-material, so three separately-billboarded boxes would each
	/// turn to face the camera on their own and the arrow would come apart as it was orbited.
	/// </summary>
	private ArrayMesh BuildArrowhead()
	{
		float halfWidth = Size * 0.5f;
		float height = Size * Aspect;

		var vertices = new[]
		{
			new Vector3(0.0f, 0.0f, 0.0f),
			new Vector3(halfWidth, height, 0.0f),
			new Vector3(-halfWidth, height, 0.0f),
		};

		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	/// <summary>
	/// Flat colour, always facing the camera, always the same size on screen, and drawn over the
	/// top of the world.
	///
	/// Every one of those is load-bearing. Unshaded, or the marker takes the scene lighting and
	/// goes dark exactly when a car is somewhere hard to see. Billboarded, or it disappears
	/// edge-on. Fixed size, or the car it is meant to help you find is the one whose marker is too
	/// small to spot. And no depth test, because a marker that hides behind the track is a marker
	/// that vanishes whenever it is doing its job.
	///
	/// Culling off as well: a billboarded single triangle presents its back face to half the
	/// angles it can be seen from.
	/// </summary>
	private static StandardMaterial3D BuildMaterial(Color colour) => new()
	{
		AlbedoColor = colour,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
		FixedSize = true,
		NoDepthTest = true,
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		// Drawn after the world so it lands on top of other markers predictably rather than
		// fighting them for the same pixels.
		RenderPriority = 1,
	};
}
