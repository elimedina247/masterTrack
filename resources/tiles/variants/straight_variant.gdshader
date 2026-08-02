// Taste-test shader for the Straight tile gallery (tools/TileGallery.tscn): every surface
// treatment under consideration behind uniform switches, so each candidate look is one
// material resource and the geometry never changes.
//
// Paints in the piece's own model space — X across the road, Z along it — which is honest
// for a straight and keeps the gallery free of UV politics. If a treatment wins, the real
// implementation carries distance-along-spine through the generators' UV channel so curves
// and chutes get the same rhythm.
//
// Everything is flat color with fwidth anti-aliasing on the pattern edges: the low-poly
// look lives or dies on lines staying crisp at every distance.
shader_type spatial;

uniform vec4 base_color : source_color = vec4(0.36, 0.37, 0.4, 1.0);
uniform vec4 edge_color : source_color = vec4(0.05, 0.05, 0.06, 1.0);

// The shoulder is everything outboard of the driving surface (|x| > road_half_width).
uniform vec4 shoulder_color : source_color = vec4(0.36, 0.37, 0.4, 1.0);
uniform bool shoulder_curbs = false;
uniform vec4 curb_color : source_color = vec4(0.92, 0.93, 0.95, 1.0);
uniform vec4 curb_alt_color : source_color = vec4(0.9, 0.12, 0.1, 1.0);
uniform float curb_length = 6.75;

uniform bool edge_lines = false;
uniform vec4 line_color : source_color = vec4(0.92, 0.93, 0.95, 1.0);
uniform float line_width = 1.2;
uniform float line_inset = 0.9;

uniform bool center_dashes = false;
uniform float dash_length = 6.0;
uniform float dash_gap = 7.5;
uniform float dash_width = 1.0;

// A thin brighter joint across the whole road every few metres — pure scale-and-speed cue.
uniform bool rungs = false;
uniform vec4 rung_color : source_color = vec4(0.44, 0.45, 0.49, 1.0);
uniform float rung_interval = 13.5;
uniform float rung_width = 1.4;

// Long alternating panels of two greys — reads as low-poly patchwork from above.
uniform bool panels = false;
uniform vec4 panel_color : source_color = vec4(0.31, 0.32, 0.35, 1.0);
uniform float panel_length = 27.0;

uniform float road_half_width = 24.0;

uniform float edge_start_degrees : hint_range(0.0, 90.0) = 68.0;
uniform float edge_full_degrees : hint_range(0.0, 90.0) = 78.0;

varying vec3 local_pos;

void vertex() {
	local_pos = VERTEX;
}

// 1 inside the first `len` of every `period` metres of `coord`, anti-aliased.
float stripe(float coord, float period, float len) {
	float t = mod(coord, period);
	float aa = fwidth(coord) + 1e-4;
	return smoothstep(0.0, aa, t) * (1.0 - smoothstep(len, len + aa, t));
}

void fragment() {
	vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float tilt = degrees(acos(clamp(world_normal.y, -1.0, 1.0)));

	float x = abs(local_pos.x);
	float z = -local_pos.z;
	float aa_x = fwidth(x) + 1e-4;

	vec3 color = base_color.rgb;

	if (panels) {
		float parity = step(0.5, mod(floor(z / panel_length), 2.0));
		color = mix(color, panel_color.rgb, parity);
	}

	if (rungs) {
		color = mix(color, rung_color.rgb, stripe(z, rung_interval, rung_width));
	}

	if (center_dashes) {
		float along = stripe(z, dash_length + dash_gap, dash_length);
		float across = 1.0 - smoothstep(dash_width * 0.5, dash_width * 0.5 + aa_x, x);
		color = mix(color, line_color.rgb, along * across);
	}

	if (edge_lines) {
		float inner = road_half_width - line_inset - line_width;
		float outer = road_half_width - line_inset;
		float line = smoothstep(inner, inner + aa_x, x) * (1.0 - smoothstep(outer, outer + aa_x, x));
		color = mix(color, line_color.rgb, line);
	}

	vec3 shoulder = shoulder_color.rgb;
	if (shoulder_curbs) {
		shoulder = mix(curb_color.rgb, curb_alt_color.rgb, stripe(z, curb_length * 2.0, curb_length));
	}
	color = mix(color, shoulder, smoothstep(road_half_width, road_half_width + aa_x, x));

	color = mix(color, edge_color.rgb, smoothstep(edge_start_degrees, edge_full_degrees, tilt));

	ALBEDO = color;

	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
