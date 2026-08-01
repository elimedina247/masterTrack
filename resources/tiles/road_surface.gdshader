// The one shader every road surface wears. The color vocabulary, applied where the road
// actually asks for it rather than per tile:
//
// - `albedo` is the road at rest — grey on the base material, so a flat straight promises
//   nothing.
// - `slope_color` paints faces by how far they pitch off level — the vertical vocabulary,
//   green on the base material. A ramp's incline goes green while its approach and run-off
//   stay grey, with nothing per-piece to author: the pitch is read off the surface normal.
//   Materials whose albedo already is the message (the bowl) set slope_color to their own
//   albedo, which switches the effect off.
// - `turn_color` paints developed bank wall — the turning blue. How much a facet is wall is
//   not something a world normal can say (a banked wall and a ramp lean the same way to a
//   normal), so the sweep that builds the road writes its lateral lean into UV.x, 0 to 1
//   over 90 degrees, and materials for swept banked roads switch `use_uv_turn` on. Meshes
//   that never wrote that channel leave it off and stay in the normal-driven vocabulary.
// - `edge_color` paints any face too steep to drive on at all — slab sides, undersides, cut
//   faces — near-black, outlining every road against the world.
//
// Every boundary is a smoothstep band a few degrees wide rather than a hard cut. The road
// meshes are facets, and a hard threshold on an interpolated normal saw-tooths wherever the
// iso-line runs along a strip of triangles; a narrow band settles that into one clean line.
// The bands are deliberately tight — this is still three flat colors, not a gradient.
shader_type spatial;

uniform vec4 albedo : source_color = vec4(0.36, 0.37, 0.4, 1.0);
uniform vec4 slope_color : source_color = vec4(0.15, 0.9, 0.3, 1.0);
uniform vec4 turn_color : source_color = vec4(0.15, 0.55, 1.0, 1.0);
uniform vec4 edge_color : source_color = vec4(0.05, 0.05, 0.06, 1.0);

// Pitched past ~10 degrees reads as a climb or a drop; a road's ordinary undulation stays
// comfortably under the start of the band.
uniform float slope_start_degrees : hint_range(0.0, 90.0) = 8.0;
uniform float slope_full_degrees : hint_range(0.0, 90.0) = 14.0;

// How far a facet leans sideways (read from UV.x) before it counts as bank wall. The same
// 8 degrees the mesh bucketing used to cut at, grown to full over a few more.
uniform bool use_uv_turn = false;
uniform float turn_start_degrees : hint_range(0.0, 90.0) = 8.0;
uniform float turn_full_degrees : hint_range(0.0, 90.0) = 14.0;

// Too steep to drive on at all. Starts safely above the 60-degree lip of a full bank wall,
// so the steepest drivable surface keeps its color while a vertical side goes black.
uniform float edge_start_degrees : hint_range(0.0, 90.0) = 68.0;
uniform float edge_full_degrees : hint_range(0.0, 90.0) = 78.0;

void fragment() {
	vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float tilt = degrees(acos(clamp(world_normal.y, -1.0, 1.0)));

	vec3 color = mix(albedo.rgb, slope_color.rgb,
					 smoothstep(slope_start_degrees, slope_full_degrees, tilt));

	if (use_uv_turn) {
		color = mix(color, turn_color.rgb,
					smoothstep(turn_start_degrees, turn_full_degrees, UV.x * 90.0));
	}

	color = mix(color, edge_color.rgb,
				smoothstep(edge_start_degrees, edge_full_degrees, tilt));

	ALBEDO = color;

	// The flat arcade look the StandardMaterial3D versions had: no highlights.
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
