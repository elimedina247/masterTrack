using Godot;

namespace MasterTrack.Vehicles;

/// <summary>
/// Restyles an imported model to match the track, which builds its own materials and never has
/// this problem. The car arrives as an FBX carrying whatever materials it was authored with —
/// smooth-shaded, with roughness and metallic values that catch the light — and next to a track
/// made of flat facets it reads as a prop from a different game.
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

	public override void _Ready()
	{
		Node? root = Target ?? GetParent();
		if (root == null)
		{
			GD.PushWarning("[FlatShade] Nothing to restyle: no Target and no parent.");
			return;
		}

		Restyle(root);
	}

	private static void Restyle(Node node)
	{
		if (node is MeshInstance3D mesh)
			RestyleSurfaces(mesh);

		foreach (Node child in node.GetChildren())
			Restyle(child);
	}

	private static void RestyleSurfaces(MeshInstance3D mesh)
	{
		if (mesh.Mesh == null)
			return;

		for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
		{
			var source = mesh.GetActiveMaterial(surface) as BaseMaterial3D;

			var flat = new StandardMaterial3D
			{
				// Everything that says what this surface *is* comes across unchanged.
				AlbedoColor = source?.AlbedoColor ?? Colors.White,
				AlbedoTexture = source?.AlbedoTexture,

				// Transparency and culling carry over too, or the windscreen turns into bodywork
				// and anything modelled as a single-sided sheet turns inside out.
				Transparency = source?.Transparency ?? BaseMaterial3D.TransparencyEnum.Disabled,
				CullMode = source?.CullMode ?? BaseMaterial3D.CullModeEnum.Back,

				// Everything that says how light behaves is replaced. Same three decisions the
				// track's own materials make in TrackTile.Finish, for the same reason: per-vertex
				// light is what the hardware this look comes from did, and a specular highlight
				// sliding across a curve is the most modern-looking thing a renderer does.
				ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
				SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
				Metallic = 0.0f,
				Roughness = 1.0f,
			};

			mesh.SetSurfaceOverrideMaterial(surface, flat);
		}
	}
}
