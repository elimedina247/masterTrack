#[compute]
#version 450

// The game's one outline, as a compute pass over the finished frame.
//
// This is the third attempt at outlines and the first that can work everywhere, so the dead ends
// are worth recording.
//
// An **inverted hull** - inflate the mesh along its normals, flip the faces, paint them black -
// assumes neighbouring faces share averaged normals. The car bodies are exported fully
// flat-shaded, every triangle carrying its own split normals, so the shell has no connectivity:
// it does not inflate, it disintegrates into unconnected floating polygons.
//
// A **full-screen quad reading hint_screen_texture** avoids that, but the screen copy it samples
// is not the finished frame, so the pass painted sky over the scene.
//
// A CompositorEffect runs at a declared point in the render graph with the colour, depth and
// normal-roughness buffers handed to it directly - no draw order to lose, no screen copy to be
// stale, no dependence on mesh normals. One pass outlines road, cars, hazards and props alike.
//
// **Read every input with texelFetch, never texture().** Implicit-LOD sampling is undefined in a
// compute stage - there are no derivatives to derive a LOD from - and this hardware answers it
// with zero. Every sampled input to this shader read as empty, on both D3D12 and Vulkan, while
// the storage-image writes landed perfectly, which made a one-token shader bug look exactly like
// a render-graph problem for a long, expensive afternoon.
//
// Two detectors, because one is not enough. Depth alone misses a crease where two surfaces meet
// at the same distance - the fold from bonnet to windscreen. Normals alone miss the edge where a
// car passes in front of a road it happens to be parallel to. Either firing draws the line.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(rgba16f, set = 0, binding = 0) uniform restrict image2D colour_image;
layout(set = 0, binding = 1) uniform sampler2D depth_sampler;
layout(set = 0, binding = 2) uniform sampler2D normal_sampler;

layout(push_constant, std430) uniform Params {
	mat4 inverse_projection;
	vec4 line_colour;
	vec2 raster_size;
	float thickness;
	float depth_threshold;
	float normal_threshold;
	float fade_begin;
	float fade_end;
	float pad;
} params;

// Raw reversed-Z depth at a texel: geometry > 0, sky exactly 0.
float raw_depth(ivec2 texel) {
	return texelFetch(depth_sampler, texel, 0).r;
}

// Depth to metres in front of the camera, by unprojecting - correct under reversed-Z and
// indifferent to the projection's far-plane convention.
float view_depth(ivec2 texel, vec2 raster_size) {
	vec2 uv = (vec2(texel) + 0.5) / raster_size;
	vec4 view = params.inverse_projection * vec4(uv * 2.0 - 1.0, raw_depth(texel), 1.0);
	return -view.z / max(abs(view.w), 1e-6);
}

// The view-space normal, packed into 0..1 in the normal-roughness buffer.
vec3 view_normal(ivec2 texel) {
	return normalize(texelFetch(normal_sampler, texel, 0).xyz * 2.0 - 1.0);
}

void main() {
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = ivec2(params.raster_size);


	if (texel.x >= size.x || texel.y >= size.y) {
		return;
	}

	// Sky pixels carry the cleared far value and are never outlined - keyed off the raw sample,
	// which is exact, rather than off an unprojected magnitude, which is not.
	float raw = raw_depth(texel);
	if (raw <= 0.0) {
		return;
	}

	float centre = view_depth(texel, params.raster_size);
	vec3 normal = view_normal(texel);

	int reach = max(int(params.thickness + 0.5), 1);

	// Roberts cross on the diagonals: half the taps of a full Sobel for the same edge response,
	// and diagonal pairs answer horizontal and vertical edges equally well.
	ivec2 offsets[4] = ivec2[](
		ivec2(-reach, -reach), ivec2(reach, reach),
		ivec2(-reach, reach), ivec2(reach, -reach)
	);

	float raws[4];
	vec3 normals[4];
	bool skies[4];
	bool sky_adjacent = false;

	for (int i = 0; i < 4; i++) {
		ivec2 at = clamp(texel + offsets[i], ivec2(0), size - 1);
		raws[i] = raw_depth(at);
		skies[i] = raws[i] <= 0.0;
		sky_adjacent = sky_adjacent || skies[i];
		normals[i] = skies[i] ? normal : view_normal(at);
	}

	// Normals get the second difference too, for the same reason depth does. A raw difference
	// asks "do my neighbours face a different way", which is true of EVERY pixel on anything
	// small and round: the antenna ball is a 6 cm sphere whose normals sweep a full hemisphere
	// across a dozen pixels, so a first-difference test inked the whole thing solid black and
	// turned a red ball into a dot of tar. The second difference asks "does the way they turn
	// CHANGE suddenly" — constant on any smoothly curving surface however tight, and still
	// spiking at a genuine crease where two facets meet at an angle.
	float normal_edge = 0.0;
	if (!skies[0] && !skies[1])
		normal_edge = max(normal_edge, length(normals[0] + normals[1] - 2.0 * normal));
	if (!skies[2] && !skies[3])
		normal_edge = max(normal_edge, length(normals[2] + normals[3] - 2.0 * normal));

	// The depth test is a second difference on the RAW depth-buffer value, and the buffer is the
	// load-bearing choice. Raw depth is what the rasteriser interpolates, so it is exactly affine
	// in screen space across any planar surface - a floor scores zero to float precision at any
	// grazing angle. View-space METRES are not affine: z(y) along a ground plane is a hyperbola,
	// and a second difference taken on metres still crossed the threshold from ~180 m out, which
	// painted a black band across the horizon from the driver's seat. (A first difference was
	// worse still - it fired on every ground pixel past ~40 m.)
	float raw_edge = 0.0;
	if (!skies[0] && !skies[1])
		raw_edge = max(raw_edge, abs(raws[0] + raws[1] - 2.0 * raw));
	if (!skies[2] && !skies[3])
		raw_edge = max(raw_edge, abs(raws[2] + raws[3] - 2.0 * raw));

	// Relative to this pixel's own raw depth so the threshold means the same fraction at every
	// distance. A solid pixel bordering sky is a silhouette and always draws - subject to the
	// distance fade below, which is what keeps the far rim of the world from wearing a stroke.
	float depth_hit = sky_adjacent ? 1.0 : step(params.depth_threshold, raw_edge / max(raw, 1e-8));
	float normal_hit = step(params.normal_threshold, normal_edge);

	float edge = max(depth_hit, normal_hit);

	// Far-off track crowds many facets into one pixel, every one of them an edge; without the
	// fade the horizon turns into a black scribble.
	edge *= 1.0 - smoothstep(params.fade_begin, params.fade_end, centre);

	if (edge < 0.5) {
		return;
	}

	vec4 colour = imageLoad(colour_image, texel);
	imageStore(colour_image, texel,
			   vec4(mix(colour.rgb, params.line_colour.rgb, params.line_colour.a), colour.a));
}
