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
//   normal), so the sweep that builds the road writes its lateral lean into UV.y, 0 to 1
//   over 90 degrees, and materials for swept banked roads switch `use_uv_turn` on. Meshes
//   that never wrote that channel leave it off and stay in the normal-driven vocabulary.
// - `edge_color` paints any face too steep to drive on at all — slab sides, undersides, cut
//   faces — near-black, outlining every road against the world.
// - `panel_color` alternates with the albedo in long panels down the road — the two-tone
//   rhythm that gives a wide surface scale and direction. Driven by UV.x, which every road
//   generator fills with metres along the track (CSGPolygon3D's own path UVs already are
//   that; the code sweeps write it deliberately, scaled so each piece ends on a panel
//   boundary and the pattern reads continuously across seams).
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

// How far a facet leans sideways (read from UV.y) before it counts as bank wall. The same
// 8 degrees the mesh bucketing used to cut at, grown to full over a few more.
uniform bool use_uv_turn = false;
uniform float turn_start_degrees : hint_range(0.0, 90.0) = 8.0;
uniform float turn_full_degrees : hint_range(0.0, 90.0) = 14.0;

// The two-tone panels, read from UV.x in metres along the road. Off by default so meshes
// with unrelated UVs stay solid.
uniform bool use_uv_panels = false;
uniform vec4 panel_color : source_color = vec4(0.31, 0.32, 0.35, 1.0);
uniform float panel_length = 27.0;

// Too steep to drive on at all. Starts safely above the 60-degree lip of a full bank wall,
// so the steepest drivable surface keeps its color while a vertical side goes black.
uniform float edge_start_degrees : hint_range(0.0, 90.0) = 68.0;
uniform float edge_full_degrees : hint_range(0.0, 90.0) = 78.0;

// Racetrack kerbs, painted on the shoulder chamfer every swept piece already carries: the 3 m
// face between the road edge and the slab side, which nothing had ever painted. Off by default
// because meshes whose steep faces are the feature (the bowl) would stripe themselves with it.
uniform bool use_kerbs = false;
uniform vec4 kerb_color_a : source_color = vec4(0.88, 0.16, 0.18, 1.0);
uniform vec4 kerb_color_b : source_color = vec4(0.95, 0.95, 0.95, 1.0);
uniform float kerb_length = 7.0;

// The cel half of the look: light() rounds N.L onto this many bands, floored so shadow is a
// darker colour rather than an absence of light.
uniform float light_bands = 3.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.35;

void fragment() {
	vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float tilt = degrees(acos(clamp(world_normal.y, -1.0, 1.0)));

	vec3 color = albedo.rgb;

	// Panels first, so the vocabulary colors paint over them: a ramp's incline stays solid
	// green and a bank wall solid blue, with the rhythm running through the grey between.
	if (use_uv_panels) {
		float phase = fract(UV.x / (panel_length * 2.0));
		float aa = fwidth(UV.x) / (panel_length * 2.0) + 1e-5;
		float dark = smoothstep(0.5 - aa, 0.5 + aa, phase)
					 * (1.0 - smoothstep(1.0 - aa, 1.0, phase));
		color = mix(color, panel_color.rgb, dark);
	}

	float slope = smoothstep(slope_start_degrees, slope_full_degrees, tilt);
	float align = 1.0;
	float lean_across = 0.0;

	// Green means CLIMB, and a tilted face is only a climb if it pitches along the direction
	// of travel. That direction is readable right here: UV.x is metres along the road, so the
	// world direction in which UV.x grows is the way the road runs. A shoulder bevel or a
	// bank's rise tips the normal sideways to that and earns no green; a ramp tips it along
	// and keeps all of it. Meshes with no distance channel (UV.x flat) fall back to plain
	// tilt — the guard keeps them at align = 1.
	if (use_uv_panels) {
		// The world direction in which UV.x grows, as the TRUE surface gradient — the raw
		// derivative sum e1*u1 + e2*u2 is only the gradient when the screen-to-surface mapping
		// is conformal, i.e. when the surface faces the camera squarely. Under foreshortening
		// the raw sum skews toward the compressed screen axis, the travel direction rotates
		// with the CAMERA, and everything gated on it changes with the view: kerb stripes
		// smearing across a whole ramp face from a side angle was this, exactly. Solving the
		// 2x2 metric makes the gradient exact and the answer view-independent.
		vec3 e1 = dFdx(VERTEX);
		vec3 e2 = dFdy(VERTEX);
		float u1 = dFdx(UV.x);
		float u2 = dFdy(UV.x);

		float g11 = dot(e1, e1);
		float g12 = dot(e1, e2);
		float g22 = dot(e2, e2);
		float det = max(g11 * g22 - g12 * g12, 1e-12);

		vec3 gradient = ((g22 * u1 - g12 * u2) * e1 + (g11 * u2 - g12 * u1) * e2) / det;
		vec3 along = (INV_VIEW_MATRIX * vec4(gradient, 0.0)).xyz;
		vec2 travel = vec2(along.x, along.z);
		vec2 downhill = vec2(world_normal.x, world_normal.z);

		if (length(travel) > 1e-6) {
			vec2 t = normalize(travel);

			// The ABSOLUTE sideways component of the surface normal, in units of sin(lean).
			// Deliberately not normalised against the total tilt: a shoulder chamfer leans
			// sideways by the same ~22 degrees whether the road beside it is flat or climbing
			// a wall, but its share of the total tilt shrinks as the climb grows — which is
			// exactly how the kerbs lost to the climb green partway up every steep ramp.
			lean_across = abs(downhill.x * t.y - downhill.y * t.x);

			if (length(downhill) > 1e-4)
				align = abs(dot(t, normalize(downhill)));
		}

		slope *= smoothstep(0.35, 0.7, align);
	}

	// Kerbs: a face that leans ACROSS the direction of travel, found by that absolute lateral
	// lean. Striped by UV.x metres, so the pattern runs continuously across seams like
	// everything else on this road. Developed bank wall leans across too, but it is wall, not
	// shoulder, and keeps its blue.
	float shoulder = 0.0;
	if (use_kerbs && use_uv_panels) {
		// The band sits just under the chamfer's own lean — sin(21.8 deg) = 0.37 on every swept
		// piece — and ABOVE the smoothed-normal interpolation that ramps across the face's edge
		// triangles. Open the band lower (it was 0.14-0.24) and that interpolation zone wears
		// half-strength kerb over the climb green: red over green is a smear of yellow streaks
		// up both edges of every steep ramp.
		shoulder = smoothstep(0.28, 0.35, lean_across)
				   * (1.0 - smoothstep(edge_start_degrees, edge_full_degrees, tilt));
		if (use_uv_turn)
			shoulder *= 1.0 - smoothstep(1.0, turn_start_degrees, UV.y * 90.0);

		vec3 kerb = mix(kerb_color_a.rgb, kerb_color_b.rgb,
						step(0.5, fract(UV.x / (kerb_length * 2.0))));
		color = mix(color, kerb, shoulder);
	}

	// Where the kerb painted, the vertical vocabulary stands down entirely — the fight between
	// the two on a steep ramp's shoulder is a coin toss otherwise, and a kerb that flickers to
	// green partway up the climb reads as a bug rather than as either colour.
	slope *= 1.0 - shoulder;

	if (use_uv_turn) {
		float lean = UV.y * 90.0;

		// A leaning facet's tilt is its lean, so without this the slope green fires all
		// along the base of every bank wall before the blue reaches full strength — a
		// bright green skirt under every turn. It has to stand down from the FIRST degree
		// of lean: starting at half the turn threshold still let a skirt through at the
		// wall's base, visible on every hairpin.
		slope *= 1.0 - smoothstep(1.0, turn_start_degrees, lean);

		color = mix(color, slope_color.rgb, slope);
		color = mix(color, turn_color.rgb,
					smoothstep(turn_start_degrees, turn_full_degrees, lean));
	} else {
		color = mix(color, slope_color.rgb, slope);
	}

	color = mix(color, edge_color.rgb,
				smoothstep(edge_start_degrees, edge_full_degrees, tilt));

	ALBEDO = color;

	// The flat arcade look the StandardMaterial3D versions had: no highlights.
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}

// The Zineth half of the look: N.L rounded onto a few bands, floored so shadow is a darker
// colour rather than an absence of light. Together with the flat SPECULAR 0 above, this is what
// makes the road read as drawn rather than rendered — and the cars' cel shader makes the same
// two decisions with the same numbers, so one sun draws the whole frame in one style.
void light() {
	float ndl = clamp(dot(normalize(NORMAL), normalize(LIGHT)), 0.0, 1.0);
	float stepped = floor(ndl * light_bands + 0.5) / light_bands;
	DIFFUSE_LIGHT += mix(shadow_floor, 1.0, stepped) * ATTENUATION * LIGHT_COLOR;
}
