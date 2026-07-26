// Radial blur smeared outward from the centre of the screen. Driven by scripts/ui/SpeedBlur.cs.
//
// The middle of the screen — where the car is, and where the player is actually looking — stays
// sharp; the smear ramps in toward the edges. That is what makes it read as speed rather than as
// the player having gone blind: the thing you are steering stays legible while the world tears
// past around it.

shader_type canvas_item;
render_mode unshaded;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear;

/// How far a pixel at the very corner is smeared, in UV. 0 is off.
uniform float strength : hint_range(0.0, 0.25) = 0.0;

/// Distance from centre, 0 at the middle and 1 at the corners, inside which nothing is touched.
uniform float clear_radius : hint_range(0.0, 1.0) = 0.18;

/// How far past clear_radius the smear takes to reach full strength.
uniform float falloff : hint_range(0.01, 2.0) = 0.75;

// Fixed count so the loop unrolls. Ten taps is enough for a smear this short; more shows up as
// cost, not as quality.
const int SAMPLES = 10;

void fragment() {
	vec2 dir = SCREEN_UV - vec2(0.5);

	// SCREEN_PIXEL_SIZE is 1/viewport, so y/x is the aspect ratio. The *distance* is corrected
	// by it so the sharp middle is a circle rather than a wide ellipse; the smear *direction*
	// is deliberately left in raw UV, because in pixel space that already points straight out
	// from the centre.
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	vec2 scale = vec2(aspect, 1.0);
	float dist = length(dir * scale) / (0.5 * length(scale));

	float amount = strength * smoothstep(clear_radius, clear_radius + falloff, dist);

	if (amount <= 0.0001) {
		COLOR = texture(screen_texture, SCREEN_UV);
	} else {
		vec4 sum = vec4(0.0);
		for (int i = 0; i < SAMPLES; i++) {
			float t = float(i) / float(SAMPLES - 1);
			sum += texture(screen_texture, SCREEN_UV - dir * amount * t);
		}
		COLOR = sum / float(SAMPLES);
	}
}
