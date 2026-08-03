using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Restyles an imported model to match the track, which builds its own materials and never has
/// this problem. The car arrives as an FBX carrying whatever materials it was authored with —
/// smooth-shaded, with roughness and metallic values that catch the light — and next to a track
/// made of flat facets it reads as a prop from a different game.
///
/// Since the cel pass landed this builds ShaderMaterials on the cars' cel shader — the same
/// banded light() the road runs, same band count, same shadow floor — so one sun draws the cars
/// and the track in one style. Two shaders, chosen by whether the authored surface carried real
/// transparency: bodywork must stay in the opaque pass (writing ALPHA at all would cost it its
/// depth, its normals and therefore its outline), and only genuine glass pays the transparent
/// price.
///
/// Per surface rather than a single <c>material_override</c>: an override replaces every surface
/// with one material, which would paint the windows and the lights the same colour as the body.
/// Each surface keeps its own albedo, so the model keeps its livery; only the way light lands on
/// it changes.
///
/// Attach as a child of whatever you want restyled — it walks its parent by default — or point
/// <see cref="Target"/> somewhere specific.
/// </summary>
[GlobalClass]
public partial class FlatShade : Node
{
	/// <summary>What to restyle. Defaults to this node's parent.</summary>
	[Export] public Node3D? Target { get; set; }

	/// <summary>
	/// Material name suffix for the surfaces that are bodywork. <c>tools/car_blockout.py</c> names
	/// them <c>&lt;Variant&gt;_Paint</c> and the names survive the FBX import intact, so the paint
	/// can be found by name rather than by guessing at a surface index per model.
	/// </summary>
	private const string PaintSuffix = "_Paint";

	/// <summary>The colour this car is painted, or null to keep whatever the model shipped with.</summary>
	private Color? _paint;

	public override void _Ready() => Restyle(_paint);

	/// <summary>
	/// Rebuild the materials, optionally repainting the bodywork.
	///
	/// Public and callable again because a car swaps in a different body <i>after</i> this node's
	/// <c>_Ready</c> has already run — Godot readies children before parents — so a car built from
	/// a variant would otherwise render smooth, shiny, and in the wrong colour.
	/// </summary>
	public void Restyle(Color? paint)
	{
		_paint = paint;

		Node? root = Target ?? GetParent();
		if (root == null)
		{
			GD.PushWarning("[FlatShade] Nothing to restyle: no Target and no parent.");
			return;
		}

		Restyle(root, paint);
	}

	private static void Restyle(Node node, Color? paint)
	{
		if (node is MeshInstance3D mesh)
			RestyleSurfaces(mesh, paint);

		foreach (Node child in node.GetChildren())
			Restyle(child, paint);
	}

	private static void RestyleSurfaces(MeshInstance3D mesh, Color? paint)
	{
		if (mesh.Mesh == null)
			return;

		// Cached by ResourceLoader, so these loads are lookups after the first car. Held as
		// locals rather than statics on purpose — a static Godot resource reference alive at
		// .NET shutdown is disposed after native teardown, which can crash the process on exit.
		var body = GD.Load<Shader>("res://resources/cars/cel_car.gdshader");
		var glass = GD.Load<Shader>("res://resources/cars/cel_car_glass.gdshader");

		for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
		{
			// The material as authored on the mesh, not the active one. This runs more than once
			// on the same car — once when this node readies, again once the car knows its colour —
			// and the active material after the first pass is the cel one built below, which
			// carries no name. Reading the mesh keeps every pass working from the original, so
			// the paint surface is still findable and restyling stays idempotent.
			var source = (mesh.Mesh.SurfaceGetMaterial(surface)
			              ?? mesh.GetActiveMaterial(surface)) as BaseMaterial3D;

			// Only the bodywork takes the player's colour. Painting every surface would put it
			// on the windows and the headlights too, which is the whole reason this class
			// rebuilds materials per surface instead of using a material_override.
			bool isPaint = paint.HasValue
			               && source?.ResourceName.EndsWith(PaintSuffix, System.StringComparison.Ordinal) == true;

			// The queue decision: authored transparency goes to the glass shader and keeps its
			// see-through; everything else is bodywork and must stay opaque so it writes the
			// depth and normals the outline pass reads.
			bool transparent = source != null
			                   && source.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;

			var cel = new ShaderMaterial { Shader = transparent ? glass : body };
			cel.SetShaderParameter("albedo", isPaint ? paint!.Value : source?.AlbedoColor ?? Colors.White);

			if (source?.AlbedoTexture is { } texture)
			{
				cel.SetShaderParameter("albedo_texture", texture);
				cel.SetShaderParameter("use_texture", true);
			}

			mesh.SetSurfaceOverrideMaterial(surface, cel);
		}
	}
}
