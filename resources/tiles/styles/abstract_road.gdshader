// STYLE C — Abstract / surreal, brightly coloured, low-poly. NaissanceE's spatial strangeness,
// but lit in neon rather than NaissanceE's white-on-black.
//
// The decision that makes this style: **colour is a function of where you are on the track, not
// of what the surface is doing.** The hue cycles with metres travelled, so a piece of road is
// never one colour — it runs through a palette as it goes, and two pieces chained together
// continue the cycle because UV.x is continuous across seams by construction. The road stops
// describing itself and starts describing the journey.
//
// It is faceted like Style B (same derivative trick) but the facets are lit far less: most of
// the brightness is EMISSION, so the track glows against a dark void instead of being lit by a
// sun. That is what makes it read as architecture in an empty space rather than as a racetrack
// on a sunny day.
//
// The vocabulary is not abandoned, it is re-encoded: climb and bank push the hue forward
// through the cycle and lift the emission, so a ramp still announces itself — as a colour shift
// and a brightening rather than as green.
shader_type spatial;
render_mode specular_disabled, diffuse_lambert;

// The palette the track cycles through. Four stops, wrapped.
uniform vec4 stop_a : source_color = vec4(0.95, 0.15, 0.55, 1.0);
uniform vec4 stop_b : source_color = vec4(0.35, 0.10, 0.85, 1.0);
uniform vec4 stop_c : source_color = vec4(0.05, 0.75, 0.95, 1.0);
uniform vec4 stop_d : source_color = vec4(0.55, 0.95, 0.20, 1.0);
uniform vec4 edge_color : source_color = vec4(0.02, 0.01, 0.06, 1.0);

// Metres of road one full trip through the palette takes. Long, so a single piece is a slice of
// the cycle rather than a rainbow.
uniform float cycle_meters = 320.0;

uniform bool use_uv_turn = false;
uniform float turn_degrees = 12.0;
uniform float slope_degrees = 11.0;
uniform float edge_degrees = 70.0;

uniform float emission_energy = 1.6;
uniform float band_contrast : hint_range(0.0, 1.0) = 0.45;

vec3 palette(float t) {
	t = fract(t) * 4.0;
	if (t < 1.0) return mix(stop_a.rgb, stop_b.rgb, t);
	if (t < 2.0) return mix(stop_b.rgb, stop_c.rgb, t - 1.0);
	if (t < 3.0) return mix(stop_c.rgb, stop_d.rgb, t - 2.0);
	return mix(stop_d.rgb, stop_a.rgb, t - 3.0);
}

void fragment() {
	// The true face normal, rebuilt from the derivatives of view-space position.
	//
	// The sign has to be forced. Which way cross(dFdx, dFdy) points depends on the
	// triangle's winding and on the derivative convention, so half the faces come out
	// inside-out — and an inverted normal reads as tilt 180 degrees, which sends the whole
	// surface past the undriveable threshold and paints the road in edge colour. Facing it
	// back toward the camera is correct for anything actually being drawn.
	vec3 face_normal = normalize(cross(dFdx(VERTEX), dFdy(VERTEX)));
	face_normal *= dot(face_normal, VERTEX) > 0.0 ? -1.0 : 1.0;
	NORMAL = face_normal;

	vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(face_normal, 0.0)).xyz);
	float tilt = degrees(acos(clamp(world_normal.y, -1.0, 1.0)));

	// Where along the cycle this metre of road sits. Bank and climb push it forward, so a
	// feature is a shift in hue against the road either side of it.
	float t = UV.x / cycle_meters;
	if (use_uv_turn)
		t += 0.12 * smoothstep(turn_degrees, turn_degrees + 8.0, UV.y * 90.0);
	t += 0.08 * step(slope_degrees, tilt);

	vec3 color = palette(t);

	// Hard bands across the surface, so a large face is never one colour — the low-poly reading
	// depends on the facets being told apart.
	float band = step(0.5, fract(UV.x / 24.0));
	color *= 1.0 - band_contrast * band;

	float undriveable = step(edge_degrees, tilt);
	color = mix(color, edge_color.rgb, undriveable);

	ALBEDO = color;
	// Most of the light in this style comes from the road itself. Undriveable faces stay dark,
	// which is what leaves the silhouette floating in the void.
	EMISSION = color * emission_energy * (1.0 - undriveable);
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
