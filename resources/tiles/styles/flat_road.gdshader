// STYLE B — Minimalist flat-shaded low-poly. The Superflight look.
//
// One decision makes this style: **the normal is thrown away and rebuilt per facet.** The
// screen-space derivatives of the view-space position give the true geometric normal of the
// triangle being drawn, so every face lights as one uniform tone no matter how the mesh was
// shaded. That is what faceting is, and doing it here means it does not depend on the bake —
// the road's driveable top is baked smooth, and this style overrides that.
//
// Everything else is subtraction. No panel rhythm, no kerbs, no lines, no outline. The colour
// vocabulary shrinks to two facts: whether a face can be driven on, and whether it is a wall.
// Form comes entirely from facets catching the sun at different angles, which is why the mesh's
// own faceting has to be the loudest thing in the frame.
//
// The one concession to the game: the turning blue survives, because a bank wall a racer cannot
// distinguish from road is a hazard the style invented.
shader_type spatial;
render_mode specular_disabled, diffuse_lambert;

uniform vec4 albedo : source_color = vec4(0.30, 0.33, 0.42, 1.0);
uniform vec4 turn_color : source_color = vec4(0.20, 0.30, 0.55, 1.0);
uniform vec4 edge_color : source_color = vec4(0.13, 0.15, 0.22, 1.0);

uniform bool use_uv_turn = false;
uniform float turn_degrees = 14.0;
uniform float edge_degrees = 70.0;

// How much a facet's own angle tints it, on top of the light. Small: this is a nudge that keeps
// large co-planar areas from flattening out, not a second colour scheme.
uniform float facet_tint : hint_range(0.0, 0.5) = 0.12;

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

	vec3 color = albedo.rgb;

	if (use_uv_turn)
		color = mix(color, turn_color.rgb, smoothstep(turn_degrees, turn_degrees + 6.0, UV.y * 90.0));

	color = mix(color, edge_color.rgb, step(edge_degrees, tilt));

	// A facet facing away from the sun's side of the sky reads a touch cooler. Ties the palette
	// together without adding a colour to it.
	color *= 1.0 + facet_tint * (world_normal.y - 0.5);

	ALBEDO = color;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}
