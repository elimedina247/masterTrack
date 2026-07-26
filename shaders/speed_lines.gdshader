// Speed lines streaking outward from the centre of the screen, confined to the edges. Driven by
// scripts/ui/SpeedLines.cs.
//
// The screen is read in polar coordinates around the centre. The angle is sliced into
// `line_count` slices, each carrying one stroke of constant thickness; the radius carries a
// scrolling comet that each slice starts at its own random phase. Nothing is drawn near the
// middle, so the car and the HUD stay clear.
//
// PI, TAU and E are built into the shading language — declaring them here is a redefinition
// error, and a shader that fails to compile takes the whole effect down with it.

shader_type canvas_item;
render_mode blend_add, unshaded;

/// Master fade, 0 is off. Everything the script animates goes through here.
uniform float strength : hint_range(0.0, 1.0) = 0.0;

uniform vec4 line_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);

/// How many lines around the full circle.
uniform float line_count : hint_range(8.0, 256.0) = 44.0;

/// Distance from centre (0 middle, 1 corners) inside which nothing is drawn.
uniform float inner_radius : hint_range(0.0, 1.0) = 0.62;

/// How fast the streaks travel outward.
uniform float speed : hint_range(0.0, 8.0) = 2.2;

/// Streaks along the radius. Higher packs more, shorter comets into the same space.
uniform float density : hint_range(0.5, 6.0) = 1.6;

/// Half-thickness of a stroke, where 1.0 is centre to corner. Constant along its whole length.
uniform float line_width : hint_range(0.0005, 0.03) = 0.003;

/// Fraction of the thickness that is feathered, so the edges aren't aliased. 0 is a hard edge.
uniform float softness : hint_range(0.0, 1.0) = 0.6;

float hash(float n) {
	return fract(sin(n * 127.1) * 43758.5453123);
}

void fragment() {
	vec2 dir = SCREEN_UV - vec2(0.5);

	// Same aspect correction as the blur: SCREEN_PIXEL_SIZE is 1/viewport, so y/x is the
	// aspect ratio. Without it the lines bunch up at the sides of a widescreen and the clear
	// middle comes out as an ellipse.
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	vec2 scale = vec2(aspect, 1.0);
	float dist = length(dir * scale) / (0.5 * length(scale));

	float angle = atan(dir.y, dir.x * aspect);
	float a = (angle + PI) / TAU;

	float slice = floor(a * line_count);
	float within = fract(a * line_count);
	float seed = hash(slice);

	// Across the line: how far this pixel sits from its slice's axis, measured *perpendicular*
	// rather than as an angle. Multiplying the angle by the radius is the whole difference
	// between a stroke and a sunburst — a constant angular width widens as it travels outward,
	// which is what reads as a star.
	float angular = (within - 0.5) * (TAU / line_count);
	float perp = abs(angular) * dist;
	float across = 1.0 - smoothstep(line_width * (1.0 - softness), line_width, perp);

	// Along the radius: a sawtooth scrolling outward, raised to a power so it climbs to a
	// bright head and falls off behind into a tail.
	float scroll = fract(dist * density - TIME * speed + seed);
	float streak = pow(scroll, 3.0);

	// Keep it off the middle of the screen and let it build toward the corners.
	float edge = smoothstep(inner_radius, 1.0, dist);

	float mask = across * streak * edge * strength;
	COLOR = vec4(line_color.rgb, line_color.a * mask);
}
