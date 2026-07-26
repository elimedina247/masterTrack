// Sky that lights the scene from one texture and is *seen* as another.
//
// From https://godotshaders.com/shader/separate-sky-lighting-hdri-or-other-from-overlay/ (CC0).
//
// Godot renders the sky twice: once into a cubemap the renderer samples for ambient light and
// reflections, and once into the frame the player actually looks at. This shader answers those
// two passes differently — the cubemap pass returns `hdri_background`, so the *lighting* comes
// from a real environment, while the visible pass returns a stylised sky with clouds mixed over
// it. That splits "what the world is lit by" from "what the sky looks like", which is otherwise
// one texture doing both jobs badly.
//
// With no ground plane under the track, this sky is also the floor: everything below the
// horizon is on screen whenever a racer looks down or falls off a tile.
//
// All four textures are panoramic (equirectangular) and sampled with SKY_COORDS, whose Y runs
// 0 at the zenith to 1 straight down.

shader_type sky;

/// Lights the scene. An HDRI if you have one; a gradient reads as flat, even daylight.
uniform sampler2D hdri_background : filter_linear, source_color, hint_default_black;

/// The sky the player sees, behind the clouds.
uniform sampler2D sky_background : filter_linear, source_color, hint_default_black;

/// Cloud coverage. Only the red channel is read, as a mask.
uniform sampler2D clouds : filter_linear, source_color, hint_default_black;

/// What the clouds are tinted with where the mask is solid.
uniform sampler2D cloud_color : source_color;

/// Brightness of the lighting pass only — turning this up lifts the scene, not the sky.
uniform float exposure : hint_range(0, 128) = 1.0;

void sky() {
	if (AT_CUBEMAP_PASS) {
		COLOR = texture(hdri_background, SKY_COORDS).rgb * exposure;
	} else {
		COLOR = mix(texture(sky_background, SKY_COORDS).rgb, texture(cloud_color, SKY_COORDS).rgb, texture(clouds, SKY_COORDS).r);
	}
}
