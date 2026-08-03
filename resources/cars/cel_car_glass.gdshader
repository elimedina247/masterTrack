// The transparent sibling of cel_car.gdshader, for the few surfaces authored with real alpha.
//
// A separate shader rather than a uniform toggle because the split IS the render queue: writing
// ALPHA at all — even a constant 1.0 — moves a material into the transparent pass, where it
// writes neither depth nor normals and so can never carry the screen-space outline. Bodywork
// must stay opaque for the outline to exist; only surfaces that genuinely need to be seen
// through pay the transparent price. The outline pass runs pre-transparent on purpose, so glass
// draws over the drawn lines the way a windscreen sits over a drawn car.
shader_type spatial;
render_mode specular_disabled, diffuse_lambert, depth_draw_opaque;

uniform vec4 albedo : source_color = vec4(0.7, 0.85, 0.95, 0.6);
uniform sampler2D albedo_texture : source_color, hint_default_white;
uniform bool use_texture = false;

uniform float light_bands = 3.0;
uniform float shadow_floor : hint_range(0.0, 1.0) = 0.35;
uniform float ambient_wrap : hint_range(0.0, 0.5) = 0.12;

void fragment() {
	vec4 colour = albedo;
	if (use_texture)
		colour *= texture(albedo_texture, UV);

	ALBEDO = colour.rgb;
	ALPHA = colour.a;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
}

void light() {
	float ndl = dot(normalize(NORMAL), normalize(LIGHT));
	float wrapped = clamp((ndl + ambient_wrap) / (1.0 + ambient_wrap), 0.0, 1.0);
	float stepped = floor(wrapped * light_bands + 0.5) / light_bands;

	DIFFUSE_LIGHT += mix(shadow_floor, 1.0, stepped) * ATTENUATION * LIGHT_COLOR;
}
