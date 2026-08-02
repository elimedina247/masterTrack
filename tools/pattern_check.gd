extends Node3D

# Photograph the panel rhythm where it has to earn its keep: a chained run of pieces (do the
# panels read as one road?), the toilet bowl (is the chute still disorienting?), and the pieces
# built from boxes and ramps (does anything look wrong?).
#
#   $GODOT --path . res://tools/PatternCheck.tscn -- --shots <output dir>

const Chain: Array = [
	"res://scenes/tiles/pieces/Straight.tscn",
	"res://scenes/tiles/pieces/CurveLeft.tscn",
	"res://scenes/tiles/pieces/Straight.tscn",
	"res://scenes/tiles/pieces/SBend.tscn",
]

const Solo: Array = [
	"res://scenes/tiles/pieces/ToiletBowl.tscn",
	"res://scenes/tiles/pieces/SquareWave.tscn",
	"res://scenes/tiles/pieces/RampSmall.tscn",
	"res://scenes/tiles/pieces/spiral_ramp.tscn",
]

var _camera: Camera3D


func _ready() -> void:
	_build_world()

	var head := Transform3D.IDENTITY
	for path in Chain:
		var piece := (load(path) as PackedScene).instantiate() as Node3D
		piece.transform = head
		add_child(piece)

		var entry := piece.get_node("Entry") as Node3D
		var exit := piece.get_node("Exit") as Node3D
		head = head * (entry.transform.affine_inverse() * exit.transform)

	for i in Solo.size():
		var piece := (load(Solo[i]) as PackedScene).instantiate() as Node3D
		piece.position = Vector3(400.0 + i * 350.0, 0.0, 0.0)
		add_child(piece)

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
	_camera.far = 4000.0
	add_child(_camera)
	_camera.current = true


func _capture(directory: String) -> void:
	DirAccess.make_dir_recursive_absolute(directory)

	# The chain from above: Straight into CurveLeft into Straight into SBend.
	_camera.position = Vector3(-40.0, 260.0, 60.0)
	_camera.look_at(Vector3(-80.0, 0.0, -220.0))
	await _shoot(directory.path_join("chain_overview.png"))

	# Driver height crossing the first seam, where the pattern has to hand over cleanly.
	_camera.position = Vector3(0.0, 7.0, -60.0)
	_camera.look_at(Vector3(-10.0, 0.0, -160.0))
	await _shoot(directory.path_join("chain_seam.png"))

	# The toilet bowl: the chute climbing to the globe, the hooded sphere, the tunnel out.
	_camera.position = Vector3(160.0, 170.0, 60.0)
	_camera.look_at(Vector3(460.0, 30.0, -180.0))
	await _shoot(directory.path_join("toilet_bowl.png"))

	# Inside the globe: the wall the riders circle, the chute's entry portal, the hole below.
	_camera.position = Vector3(430.0, 110.0, -100.0)
	_camera.look_at(Vector3(480.0, 10.0, -200.0))
	await _shoot(directory.path_join("toilet_bowl_inside.png"))

	# Boxes and ramps: SquareWave, RampSmall, the spiral.
	_camera.position = Vector3(760.0, 180.0, 200.0)
	_camera.look_at(Vector3(890.0, 20.0, -120.0))
	await _shoot(directory.path_join("boxes_and_ramps.png"))

	get_tree().quit()


func _shoot(path: String) -> void:
	for i in 8:
		await get_tree().process_frame
	await RenderingServer.frame_post_draw

	var image := get_viewport().get_texture().get_image()
	image.save_png(path)
	print("[PatternCheck] wrote ", path)
