// STYLE A — Cel-shaded low-poly with bold black outlines. The Zenith look.
//
// Two decisions make this style, and neither is the colour:
//
// 1. **The light is quantised.** A custom light() rounds N·L onto a small number of bands, so a
//    curved surface lights in discrete steps instead of a gradient. That is what makes it read
//    as drawn rather than rendered, and it is why the bank wall stops being one flat sheet of
//    blue — the band boundary sweeps across it as it curves.
// 2. **The outline is a second pass**, not part of this shader. cel_outline.gdshader is hung on
//    this material's next_pass and inflates the mesh along its normals with the faces flipped,
//    so what shows is a shell of solid black behind every silhouette.
//
// The road's colour vocabulary survives intact — grey at rest, green for climb, blue for bank,
// near-black for undriveable — because it is the game's language, not its style. What changes is
// that the boundaries are hard steps rather than smoothstep bands: this look wants edges.
shader_type spatial;
render_mode specular_disabled, diffuse_lambert;

uniform vec4 albedo : source_color = vec4(0.42, 0.44, 0.5, 1.0);
uniform vec4 panel_color : source_color = vec4(0.35, 0.37, 0.43, 1.0);
uniform vec4 slope_color : source_color = vec4(0.25, 0.85, 0.38, 1.0);
uniform vec4 turn_color : source_color = vec4(0.22, 0.52, 0.95, 1.0);
uniform vec4 edge_color : source_color = vec4(0.06, 0.06, 0.09, 1.0);

// The shoulder chamfer — the 3 m face between the road edge and the slab side, which every
// swept piece already carries and nothing has ever painted. Kerbs live here.
uniform vec4 kerb_a : source_color = vec4(0.88, 0.16, 0.18, 1.0);
uniform vec4 kerb_b : source_color = vec4(0.95, 0.95, 0.95, 1.0);
uniform float kerb_length = 7.0;

uniform bool use_uv_turn = false;
uniform bool use_uv_panels = true;
uniform float panel_length = 27.0;

uniform float slope_degrees = 11.0;
uniform float turn_degrees = 11.0;
uniform float edge_degrees = 72.0;

// How many steps the light is rounded onto. Three is the classic cel count: lit, mid, shadow.
uniform float light_bands = 3.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.35;

void fragment() {
	vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float tilt = degrees(acos(clamp(world_normal.y, -1.0, 1.0)));

	vec3 color = albedo.rgb;

	if (use_uv_panels) {
		float phase = fract(UV.x / (panel_length * 2.0));
		color = mix(color, panel_color.rgb, step(0.5, phase));
	}

	// Which way the road runs, read off the UV distance channel, so a face that tilts ACROSS
	// travel is a shoulder and a face that tilts ALONG it is a climb. Same test the base shader
	// uses to keep green off bevels; here it also picks the faces that get kerbs.
	float along_travel = 1.0;
	if (use_uv_panels) {
		vec3 along = (INV_VIEW_MATRIX
					  * vec4(dFdx(VERTEX) * dFdx(UV.x) + dFdy(VERTEX) * dFdy(UV.x), 0.0)).xyz;
		vec2 travel = vec2(along.x, along.z);
		vec2 downhill = vec2(world_normal.x, world_normal.z);
		if (length(travel) > 1e-6 && length(downhill) > 1e-4)
			along_travel = abs(dot(normalize(travel), normalize(downhill)));
	}

	// The boundaries are narrow smoothstep bands, not hard steps, and that is not a softening of
	// the style. The road is facets: a hard threshold on an interpolated normal saw-tooths
	// wherever its iso-line happens to run along a strip of triangles, which is a ragged edge
	// rather than a crisp one. A degree or two of band settles it into a clean line and is still
	// far too tight to read as a gradient.
	float tilted = smoothstep(slope_degrees, slope_degrees + 1.5, tilt)
				   * (1.0 - smoothstep(edge_degrees, edge_degrees + 2.0, tilt));
	float across = 1.0 - smoothstep(0.30, 0.42, along_travel);
	float lean = use_uv_turn ? smoothstep(turn_degrees, turn_degrees + 2.0, UV.y * 90.0) : 0.0;

	// Kerbs: a tilted face that leans across the road rather than along it, striped by metres
	// travelled. Developed bank wall is excluded — it leans across too, but it is wall, not
	// shoulder, and it has its own colour to wear.
	float is_shoulder = tilted * across * (1.0 - lean);
	vec3 kerb = mix(kerb_a.rgb, kerb_b.rgb, step(0.5, fract(UV.x / (kerb_length * 2.0))));
	color = mix(color, kerb, is_shoulder);

	// Climb green: tilted, and tilted the way the road runs.
	//
	// The second guard is the one that is easy to miss. A leaning facet's tilt IS its lean, so
	// without standing the vertical vocabulary down wherever the mesh reports any lean at all,
	// green fires along the whole base of every bank wall before the blue has grown strong
	// enough to cover it — a bright green skirt under every corner. It has to start fading well
	// before the blue starts, hence half turn_degrees.
	float lean_onset = use_uv_turn
		? smoothstep(1.0, turn_degrees, UV.y * 90.0)
		: 0.0;
	color = mix(color, slope_color.rgb, tilted * (1.0 - across) * (1.0 - lean_onset));

	color = mix(color, turn_color.rgb, lean);
	color = mix(color, edge_color.rgb, smoothstep(edge_degrees, edge_degrees + 2.0, tilt));

	ALBEDO = color;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}

// The style, in six lines. N·L is rounded onto `light_bands` steps and floored so shadow never
// goes black — cel shadow is a darker colour, not an absence of light.
void light() {
	float ndl = clamp(dot(normalize(NORMAL), normalize(LIGHT)), 0.0, 1.0);
	float stepped = floor(ndl * light_bands + 0.5) / light_bands;
	float lit = mix(shadow_floor, 1.0, stepped);

	DIFFUSE_LIGHT += lit * ATTENUATION * LIGHT_COLOR;
}
