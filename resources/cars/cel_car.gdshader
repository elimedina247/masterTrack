// Cel shading for the cars, matching the track's Style A.
//
// Deliberately the same lighting decision as the road shader and nothing else: N·L rounded onto
// a few bands, floored so shadow is a darker colour rather than an absence of light. Everything
// that says what a surface *is* — its albedo, its texture, whether it is glass — is handed in by
// FlatShade from the material the model was authored with, exactly as it does today. Only the
// way light lands changes.
//
// One band count and one shadow floor shared with the road, because a car lit on three steps
// next to a road lit on five reads as two art styles in one frame.
shader_type spatial;
render_mode specular_disabled, diffuse_lambert;

uniform vec4 albedo : source_color = vec4(0.35, 0.70, 0.98, 1.0);
uniform sampler2D albedo_texture : source_color, hint_default_white;
uniform bool use_texture = false;

uniform float light_bands = 3.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.35;

// A touch of sky-side lift so an unlit face is not a flat slab of one colour. The cars are much
// smaller than the road and carry far more curvature, so they need slightly more help than it
// does to keep their form when a whole panel falls into the shadow band.
uniform float ambient_wrap : hint_range(0.0, 0.5) = 0.12;

void fragment() {
	vec4 colour = albedo;
	if (use_texture)
		colour *= texture(albedo_texture, UV);

	ALBEDO = colour.rgb;
	// No ALPHA write, and that is load-bearing: writing ALPHA — even a constant 1.0 — moves the
	// material into the transparent pass, where it writes neither depth nor normals. The
	// screen-space outline reads both, so a car that touches ALPHA is a car with no outline; it
	// also cost an afternoon of hunting a "broken" depth buffer that was in fact correctly
	// recording a scene with no opaque geometry in it. Glass gets its own shader.
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}

void light() {
	// Wrapped before quantising, not after: wrapping afterwards would just lift every band and
	// wash the whole car out, where wrapping first moves where the band boundaries fall and so
	// keeps the steps but puts more of the car in the upper ones.
	float ndl = dot(normalize(NORMAL), normalize(LIGHT));
	float wrapped = clamp((ndl + ambient_wrap) / (1.0 + ambient_wrap), 0.0, 1.0);

	float stepped = floor(wrapped * light_bands + 0.5) / light_bands;
	float lit = mix(shadow_floor, 1.0, stepped);

	DIFFUSE_LIGHT += lit * ATTENUATION * LIGHT_COLOR;
}
