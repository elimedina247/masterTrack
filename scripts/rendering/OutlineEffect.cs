using Godot;

namespace MasterTrack.Rendering;

/// <summary>
/// Runs <c>resources/styles/outline.glsl</c> over the finished frame.
///
/// Assign one to a <see cref="Compositor"/> on the camera (or on the WorldEnvironment) and every
/// edge in the game — road, cars, hazards, props — gets the same line, at the same pixel width,
/// with nothing baked into any mesh. The shader file explains why it had to be a compute pass
/// rather than the two cheaper techniques that came before it.
///
/// Everything here happens on the rendering thread. The RenderingDevice belongs to that thread
/// and touching it from anywhere else is a crash waiting for a race, which is why construction
/// defers through <see cref="RenderingServer.CallOnRenderThread"/> rather than doing its work in
/// the constructor.
/// </summary>
[GlobalClass]
public partial class OutlineEffect : CompositorEffect
{
	private const string ShaderPath = "res://resources/styles/outline.glsl";

	/// <summary>Line width in pixels. Constant across the frame, which is the point of doing this
	/// in screen space: a mesh-based outline thins with distance and has to be corrected for.</summary>
	[Export(PropertyHint.Range, "0.5,6.0,0.1")]
	public float Thickness { get; set; } = 1.6f;

	[Export] public Color LineColor { get; set; } = new(0.04f, 0.04f, 0.07f);

	/// <summary>How much nearer a neighbour must be, as a fraction of this pixel's own distance,
	/// to count as a step rather than as the same surface receding.</summary>
	[Export(PropertyHint.Range, "0.001,0.2,0.001")]
	public float DepthThreshold { get; set; } = 0.022f;

	/// <summary>How far two neighbouring normals must diverge to count as a crease. 0.35 is about
	/// 40 degrees — past the road's own faceting, short of a real fold.</summary>
	[Export(PropertyHint.Range, "0.05,1.0,0.01")]
	public float NormalThreshold { get; set; } = 0.35f;

	[Export] public float FadeBegin { get; set; } = 500.0f;
	[Export] public float FadeEnd { get; set; } = 1400.0f;

	private RenderingDevice? _rd;
	private Rid _shader;
	private Rid _pipeline;
	private Rid _sampler;

	public OutlineEffect()
	{
		// After transparent, so anything drawn in that pass is outlined too and the depth buffer
		// is complete by the time it is read.
		// Pre-transparent: after the opaque pass and the sky, so depth and normals are complete
		// and the colour buffer holds everything the line should sit on — and before transparent,
		// so nitro flames and window glass draw over the line rather than being scribbled on.
		//
		// The stage matters more than it looks: at post-opaque the callback lands before this
		// backend's colour clear, so every write is wiped; at post-transparent the depth buffer
		// reads empty. This is the one stage where both inputs and the output are live.
		EffectCallbackType = EffectCallbackTypeEnum.PreTransparent;

		// Without this the normal-roughness prepass is simply not rendered, and the crease
		// detector silently does nothing.
		NeedsNormalRoughness = true;

		RenderingServer.CallOnRenderThread(Callable.From(InitialiseOnRenderThread));
	}

	private void InitialiseOnRenderThread()
	{
		_rd = RenderingServer.GetRenderingDevice();
		if (_rd == null)
		{
			// Headless runs have no renderer at all — nothing to outline, nothing to report.
			// A real Forward+ misconfiguration would surface plenty of other ways.
			return;
		}

		var file = GD.Load<RDShaderFile>(ShaderPath);
		if (file == null)
		{
			GD.PushError($"[OutlineEffect] Could not load {ShaderPath}. A .glsl file has to be "
						 + "imported by the editor before the game can load it — run the editor once.");
			return;
		}

		RDShaderSpirV spirV = file.GetSpirV();
		_shader = _rd.ShaderCreateFromSpirV(spirV);
		if (!_shader.IsValid)
		{
			GD.PushError("[OutlineEffect] The outline shader would not compile.");
			return;
		}

		_pipeline = _rd.ComputePipelineCreate(_shader);

		// Depth and normals are read at neighbouring texels, so sampling has to clamp: wrapping at
		// the frame edge would compare a pixel on the left of the screen with one on the right and
		// draw a line down both borders.
		_sampler = _rd.SamplerCreate(new RDSamplerState
		{
			MinFilter = RenderingDevice.SamplerFilter.Nearest,
			MagFilter = RenderingDevice.SamplerFilter.Nearest,
			RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
			RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
			RepeatW = RenderingDevice.SamplerRepeatMode.ClampToEdge,
		});
	}

	/// <summary>
	/// Free the GPU resources on the render thread while it is still alive.
	///
	/// <see cref="NotificationPredelete"/> rather than a destructor: the RenderingDevice outlives
	/// neither .NET finalisation order nor the render thread, and freeing a Rid from the wrong
	/// thread after teardown is the same class of crash the rest of this project frees eagerly to
	/// avoid.
	/// </summary>
	public override void _Notification(int what)
	{
		if (what != NotificationPredelete || _rd == null)
			return;

		if (_sampler.IsValid)
			_rd.FreeRid(_sampler);
		if (_pipeline.IsValid)
			_rd.FreeRid(_pipeline);
		if (_shader.IsValid)
			_rd.FreeRid(_shader);
	}

	public override void _RenderCallback(int effectCallbackType, RenderData renderData)
	{
		if (_rd == null || !_pipeline.IsValid)
			return;

		if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers
			|| renderData.GetRenderSceneData() is not RenderSceneDataRD sceneData)
			return;

		Vector2I size = buffers.GetInternalSize();
		if (size.X == 0 || size.Y == 0)
			return;


		// Eight-by-eight groups, rounded up; the shader discards the overhang itself.
		uint groupsX = (uint)((size.X - 1) / 8 + 1);
		uint groupsY = (uint)((size.Y - 1) / 8 + 1);

		// One dispatch per view, so this works in stereo without a special case.
		for (uint view = 0; view < buffers.GetViewCount(); view++)
		{
			Rid colour = buffers.GetColorLayer(view);
			Rid depth = buffers.GetDepthLayer(view);
			Rid normal = buffers.GetTextureSlice("forward_clustered", "normal_roughness", view, 0, 1, 1);

			if (!colour.IsValid || !depth.IsValid || !normal.IsValid)
				continue;


			var colourUniform = new RDUniform
			{
				UniformType = RenderingDevice.UniformType.Image,
				Binding = 0,
			};
			colourUniform.AddId(colour);

			var depthUniform = new RDUniform
			{
				UniformType = RenderingDevice.UniformType.SamplerWithTexture,
				Binding = 1,
			};
			depthUniform.AddId(_sampler);
			depthUniform.AddId(depth);

			var normalUniform = new RDUniform
			{
				UniformType = RenderingDevice.UniformType.SamplerWithTexture,
				Binding = 2,
			};
			normalUniform.AddId(_sampler);
			normalUniform.AddId(normal);

			Rid uniformSet = UniformSetCacheRD.GetCache(_shader, 0,
				new Godot.Collections.Array<RDUniform> { colourUniform, depthUniform, normalUniform });

			long compute = _rd.ComputeListBegin();
			_rd.ComputeListBindComputePipeline(compute, _pipeline);
			_rd.ComputeListBindUniformSet(compute, uniformSet, 0);
			_rd.ComputeListSetPushConstant(compute, PushConstants(sceneData, view, size),
										   (uint)PushConstantBytes);
			_rd.ComputeListDispatch(compute, groupsX, groupsY, 1);
			_rd.ComputeListEnd();
		}
	}

	/// <summary>28 floats: a mat4, a colour, the raster size and the five knobs. 112 bytes, which
	/// is a multiple of 16 and inside the 128-byte push-constant floor every driver guarantees.</summary>
	private const int PushConstantBytes = 28 * sizeof(float);

	private byte[] PushConstants(RenderSceneDataRD sceneData, uint view, Vector2I size)
	{
		// Unprojecting is what turns a reversed-Z depth sample into metres, and doing it from the
		// inverse projection keeps the shader indifferent to which depth convention is in play.
		Projection inverse = sceneData.GetViewProjection(view).Inverse();

		var values = new float[28];
		var column = 0;

		foreach (Vector4 axis in new[] { inverse.X, inverse.Y, inverse.Z, inverse.W })
		{
			values[column++] = axis.X;
			values[column++] = axis.Y;
			values[column++] = axis.Z;
			values[column++] = axis.W;
		}

		values[16] = LineColor.R;
		values[17] = LineColor.G;
		values[18] = LineColor.B;
		values[19] = LineColor.A;
		values[20] = size.X;
		values[21] = size.Y;
		values[22] = Thickness;
		values[23] = DepthThreshold;
		values[24] = NormalThreshold;
		values[25] = FadeBegin;
		values[26] = FadeEnd;
		values[27] = 0.0f;

		var bytes = new byte[PushConstantBytes];
		System.Buffer.BlockCopy(values, 0, bytes, 0, PushConstantBytes);
		return bytes;
	}
}
