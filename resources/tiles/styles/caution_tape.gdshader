// Yellow/black hazard striping, for the ring that marks a rigged trap's footprint.
//
// The stripes are measured in **metres of object space**, not in UV, for two reasons. A ring is
// four long thin faces and a box's UVs run 0..1 per face, so UV striping stretches differently on
// every side of the same ring. And a hazard has to read at a fixed real-world size — the stripe
// pitch is a legibility decision (how many bands a racer sees in the half second they have), not
// a texturing one. Object space gives one continuous diagonal field across the whole ring,
// whatever the ring is made of, at a pitch that means the same thing on every hazard in the game.
//
// The scroll is the arming tell. At rest it is zero and the tape is still; while a trap is
// winding up the script drives it, and stripes that start crawling are the loudest possible
// "this is about to happen" without adding a light or a sound.
//
// Speaks the road's language otherwise — flat, ROUGHNESS 1, SPECULAR 0 — the same three
// decisions `flat_road.gdshader` and the cars make.
shader_type spatial;
render_mode specular_disabled, diffuse_lambert;

uniform vec4 stripe_warm : source_color = vec4(0.98, 0.76, 0.06, 1.0);
uniform vec4 stripe_dark : source_color = vec4(0.07, 0.07, 0.08, 1.0);

/// Metres between one band and the next, measured along the diagonal.
uniform float stripe_pitch = 2.2;

/// Metres per second the bands crawl. Zero at rest; driven while a trap winds up.
uniform float scroll_speed = 0.0;

/// How hot the warm band glows. Lifts the tape out of shadow when a trap is arming.
uniform float glow : hint_range(0.0, 4.0) = 0.0;

varying vec3 v_local;

void vertex() {
	v_local = VERTEX;
}

void fragment() {
	// x + z is the 45-degree diagonal across the road plane — the direction real hazard tape
	// runs, and the one that stays diagonal on all four sides of a square ring.
	float along = (v_local.x + v_local.z) - TIME * scroll_speed;
	float band = step(0.5, fract(along / stripe_pitch));

	vec3 color = mix(stripe_warm.rgb, stripe_dark.rgb, band);

	ALBEDO = color;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;

	// Only the warm band glows: a black stripe that emits is just a grey stripe.
	EMISSION = stripe_warm.rgb * glow * (1.0 - band);
}
