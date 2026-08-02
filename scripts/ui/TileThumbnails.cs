using System;
using System.Threading.Tasks;
using Godot;
using MasterTrack.Tiles;

namespace MasterTrack.UI;

/// <summary>
/// Photographs each catalog piece into a small texture, so a tray card can show the tile itself
/// rather than a colour standing in for it — the Track Master reads the card by shape, the way
/// they read the track.
///
/// One off-screen SubViewport, reused: each piece is instanced into it alone, framed by an
/// orthographic camera off its own bounds, drawn for exactly one frame and read back as an
/// <see cref="ImageTexture"/> — then freed. Nothing renders per-frame; the viewport sits disabled
/// between shots, so the whole catalog costs about three frames a piece once at startup and the
/// tray costs nothing after that.
///
/// The textures belong to whoever asked. This node holds no cache — it lives under the tray, dies
/// with it, and a fresh tray photographs the catalog again rather than trusting a static that
/// would still be holding engine resources at shutdown.
/// </summary>
public partial class TileThumbnails : SubViewport
{
	/// <summary>Pixels of one shot — twice the card's picture area, so the model stays crisp on
	/// a scaled or high-DPI screen rather than being upsampled from exactly card size.</summary>
	private static readonly Vector2I ShotSize = new(232, 104);

	/// <summary>
	/// Where the camera looks from, relative to the piece: front-right and above. Pieces run away
	/// from their entry down -Z, so a viewpoint on +Z looks down the road the way the racer will —
	/// a hairpin reads as a U and a ramp shows its climb, which is the whole point of the picture.
	/// </summary>
	private static readonly Vector3 ViewDirection = new Vector3(0.65f, 0.72f, 1.0f).Normalized();

	private Camera3D _camera = null!;

	public override void _Ready()
	{
		Size = ShotSize;
		OwnWorld3D = true;
		TransparentBg = true;
		RenderTargetUpdateMode = UpdateMode.Disabled;
		Msaa3D = Msaa.Msaa4X;

		_camera = new Camera3D
		{
			Projection = Camera3D.ProjectionType.Orthogonal,
			KeepAspect = Camera3D.KeepAspectEnum.Height,
			Current = true,
			// Flat ambient rather than a sky: the shot is transparent, so there is no sky to
			// borrow light from, and an unlit underside would read as a hole in the card.
			Environment = new Godot.Environment
			{
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = new Color(0.68f, 0.72f, 0.80f),
				AmbientLightEnergy = 1.1f,
			},
		};
		AddChild(_camera);

		// The same high three-quarter sun the tile gallery photographs with. No shadows: at card
		// size they are noise, and they are the only expensive thing about a directional light.
		var sun = new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-52.0f, 28.0f, 0.0f),
			LightEnergy = 1.2f,
		};
		AddChild(sun);
	}

	/// <summary>
	/// Photograph every piece in the catalog, handing each finished texture to
	/// <paramref name="ready"/> with its catalog index. Async on purpose: the tray comes up at
	/// once wearing its swatches, and the models pop in over the first second or so.
	/// </summary>
	public async void RenderCatalog(Action<int, Texture2D> ready)
	{
		for (var i = 0; i < TileCatalog.All.Count; i++)
		{
			if (TileCatalog.At(i) is not { IsScenePiece: true } definition)
				continue;

			Texture2D? shot = await RenderPiece(definition.ScenePath);

			// The awaits outlive a freed tray — the signal fires regardless — so every landing
			// checks the ground before touching anything of this node's.
			if (!IsInstanceValid(this) || !IsInsideTree())
				return;

			if (shot != null)
				ready(i, shot);
		}
	}

	private async Task<Texture2D?> RenderPiece(string scenePath)
	{
		if (GD.Load<PackedScene>(scenePath)?.Instantiate() is not Node3D piece)
			return null;

		AddChild(piece);
		SceneTree tree = GetTree();

		// A frame before measuring: an unbaked piece is still CSG, and CSG builds itself the
		// frame after it enters the tree — measured on arrival, its bounds are empty.
		await ToSignal(tree, SceneTree.SignalName.ProcessFrame);

		if (!IsInstanceValid(this) || !IsInsideTree())
			return null;

		Aabb bounds = MergedBounds(piece);
		if (bounds.Size.LengthSquared() < 0.01f)
		{
			RemoveChild(piece);
			piece.QueueFree();
			return null;
		}

		FrameCamera(bounds);

		// Draw exactly one frame — Once flips itself back to Disabled — and read it after the
		// draw it was written on. Reading before frame_post_draw hands back the previous shot.
		RenderTargetUpdateMode = UpdateMode.Once;
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		if (!IsInstanceValid(this) || !IsInsideTree())
			return null;

		Image image = GetTexture().GetImage();

		// Out of the tree this instant rather than queued: the next piece goes in before the free
		// queue is flushed, and two pieces in one viewport is a double exposure.
		RemoveChild(piece);
		piece.QueueFree();

		// A headless run has no renderer and reads back nothing; a card with no picture keeps
		// its swatch, which is the right degraded state everywhere this can happen.
		return image == null || image.IsEmpty()
			? null
			: ImageTexture.CreateFromImage(image);
	}

	/// <summary>Every visual under the piece, merged into one box in the viewport's world — which
	/// is the piece's own space, since the piece sits at the origin of a world of its own.</summary>
	private static Aabb MergedBounds(Node3D root)
	{
		var merged = default(Aabb);
		var any = false;

		Merge(root);
		return merged;

		void Merge(Node node)
		{
			if (node is VisualInstance3D visual)
			{
				Aabb bounds = visual.GlobalTransform * visual.GetAabb();
				merged = any ? merged.Merge(bounds) : bounds;
				any = true;
			}

			foreach (Node child in node.GetChildren())
				Merge(child);
		}
	}

	/// <summary>
	/// Aim the camera at the piece and open it just wide enough to hold the whole thing: the box's
	/// corners measured along the camera's own axes, against the shot's aspect — so a long
	/// straight and a squat bowl both fill the frame rather than one drowning and one clipping.
	/// </summary>
	private void FrameCamera(Aabb bounds)
	{
		Vector3 centre = bounds.GetCenter();
		float radius = bounds.Size.Length() * 0.5f;

		_camera.LookAtFromPosition(centre + ViewDirection * (radius + 10.0f), centre, Vector3.Up);
		_camera.Near = 0.05f;
		_camera.Far = radius * 2.0f + 20.0f;

		var across = 0.0f;
		var up = 0.0f;
		Basis toCamera = _camera.GlobalTransform.Basis.Inverse();

		for (var corner = 0; corner < 8; corner++)
		{
			Vector3 local = toCamera * (bounds.GetEndpoint(corner) - centre);
			across = Mathf.Max(across, Mathf.Abs(local.X));
			up = Mathf.Max(up, Mathf.Abs(local.Y));
		}

		// Orthographic Size is the vertical span; the 1.08 is a hair of margin so nothing kisses
		// the card's edge.
		float aspect = ShotSize.X / (float)ShotSize.Y;
		_camera.Size = Mathf.Max(up, across / aspect) * 2.0f * 1.08f;
	}
}
