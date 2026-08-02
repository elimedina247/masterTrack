extends Node3D

# Six Straight tiles side by side, one per candidate look, photographed and done.
#
# Run it headed (the capture needs a real renderer):
#   $GODOT --path . res://tools/TileGallery.tscn -- --shots <output dir>
# Without --shots it just stays up, which is also how it behaves opened from the editor:
# a walk-in comparison room for the tile treatments in resources/tiles/variants.

const Variants: Array = [
	["A  current", "res://resources/tiles/road.tres"],
	["B  clean shoulders", "res://resources/tiles/variants/b_clean_shoulders.tres"],
	["C  curbs", "res://resources/tiles/variants/c_curbs.tres"],
	["D  rhythm", "res://resources/tiles/variants/d_rhythm.tres"],
	["E  panels", "res://resources/tiles/variants/e_panels.tres"],
	["F  race", "res://resources/tiles/variants/f_race.tres"],
]

const Spacing := 80.0

var _camera: Camera3D
var _pieces: Array[Node3D] = []
var _labels: Array[Label3D] = []


func _ready() -> void:
	_build_world()
	_build_pieces()

	var shots := _shots_dir()
	if shots != "":
		_capture(shots)


func _shots_dir() -> String:
	var args := OS.get_cmdline_user_args()
	for i in args.size():
		if args[i] == "--shots" and i + 1 < args.size():
			return args[i + 1]
	return ""


func _build_world() -> void:
	var sky := ProceduralSkyMaterial.new()
	sky.sky_top_color = Color(0.35, 0.46, 0.71)
	sky.sky_horizon_color = Color(0.65, 0.7, 0.78)
	sky.ground_bottom_color = Color(0.2, 0.17, 0.13)
	sky.ground_horizon_color = Color(0.65, 0.7, 0.78)

	var environment := Environment.new()
	environment.background_mode = Environment.BG_SKY
	environment.sky = Sky.new()
	environment.sky.sky_material = sky
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_SKY
	environment.ambient_light_energy = 1.0

	var world := WorldEnvironment.new()
	world.environment = environment
	add_child(world)

	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-50.0, 35.0, 0.0)
	sun.shadow_enabled = true
	sun.light_energy = 1.2
	add_child(sun)

	_camera = Camera3D.new()
	_camera.fov = 55.0
	add_child(_camera)
	_camera.current = true


func _build_pieces() -> void:
	var scene: PackedScene = load("res://scenes/tiles/pieces/Straight.tscn")

	for i in Variants.size():
		var name: String = Variants[i][0]
		var material: Material = load(Variants[i][1])

		var piece := scene.instantiate() as Node3D
		piece.position = Vector3(i * Spacing, 0.0, 0.0)
		add_child(piece)

		var baked := piece.get_node_or_null("BakedMesh") as MeshInstance3D
		if baked == null:
			push_error("Straight has no BakedMesh to dress; bake it first.")
			continue
		baked.material_override = material

		var label := Label3D.new()
		label.text = name
		label.font_size = 320
		label.pixel_size = 0.02
		label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
		label.modulate = Color(0.95, 0.96, 1.0)
		label.outline_size = 60
		label.position = piece.position + Vector3(0.0, 10.0, 14.0)
		add_child(label)

		_pieces.append(piece)
		_labels.append(label)


func _capture(directory: String) -> void:
	DirAccess.make_dir_recursive_absolute(directory)

	# The overview: all six from a high three-quarter angle, pulled back until they all fit.
	var mid := (Variants.size() - 1) * Spacing * 0.5
	_camera.position = Vector3(mid, 210.0, 230.0)
	_camera.look_at(Vector3(mid, 0.0, -54.0))
	await _shoot(directory.path_join("overview.png"))

	# Driver height, one per variant, looking down the lane — the others hidden so each
	# candidate is judged alone.
	for i in Variants.size():
		for j in _pieces.size():
			_pieces[j].visible = j == i
			_labels[j].visible = false

		var x := i * Spacing
		_camera.position = Vector3(x, 7.0, 16.0)
		_camera.look_at(Vector3(x, 0.0, -70.0))
		await _shoot(directory.path_join("driver_%s.png" % Variants[i][0].substr(0, 1)))

	get_tree().quit()


func _shoot(path: String) -> void:
	# Let the frame settle (shadows, sky) before reading the viewport back.
	for i in 8:
		await get_tree().process_frame
	await RenderingServer.frame_post_draw

	var image := get_viewport().get_texture().get_image()
	image.save_png(path)
	print("[TileGallery] wrote ", path)
