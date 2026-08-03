// The black outline for STYLE A, hung on cel_road.tres as its next_pass.
//
// The inverted-hull trick: push every vertex out along its own normal, throw away the front
// faces and keep the back ones, and what is left visible is a shell of solid colour poking out
// from behind the silhouette. Cheap, no post-processing pass, and it outlines against anything
// — including other pieces of track.
//
// It outlines silhouettes only, not interior creases, which suits a road: the shape you want
// drawn is where the slab ends and the sky or the next piece begins.
//
// `width` is in metres, because the mesh is. A 48 m road wants a fat line to read at board
// altitude, and the same line is still sensible from a car.
shader_type spatial;
render_mode cull_front, unshaded, shadows_disabled;

uniform float width = 0.55;
uniform vec4 line_color : source_color = vec4(0.03, 0.03, 0.05, 1.0);

void vertex() {
	VERTEX += normalize(NORMAL) * width;
}

void fragment() {
	ALBEDO = line_color.rgb;
}
