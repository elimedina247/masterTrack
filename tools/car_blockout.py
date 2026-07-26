"""Builds low-poly Speed Racer style cars, as a starting point to be tweaked by hand.

Run it headless and it writes assets/cars/car_variants.blend with three variants standing side by
side, ready to be compared, cannibalised and pushed around:

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" --background --python tools/car_blockout.py

Everything here exists to be thrown away. The point is not that a script can model a car — it
cannot — but that the tedious half is the half a script is good at: getting the wheelbase right,
getting the origin on the rig's origin, getting the forward axis pointing the way Godot expects,
and making sure every face is flat-shaded. That is the part that is annoying to fix by hand and
invisible when it is wrong. The shapes on top of it are a first guess.

Axes. Blender is Z-up and Godot is Y-up, and the glTF conversion Godot runs on import maps Blender
(X, Y, Z) to Godot (X, Z, -Y). So a car authored nose-along-Blender-+Y comes out nose-along-Godot
-Z, which is Godot's forward, and needs no correction node in the scene. The current CC96 body is
rotated ninety degrees in Racer.tscn precisely because it was not authored this way.

Origin. (0, 0, 0) here is the RigidBody's origin in Racer.tscn, so the wheels below are drawn at
the positions the game's ray casts actually use. They are reference: the game instances its own
rims at those hubs. They are here so the body can be judged against where the wheels really are
rather than where they were imagined to be.
"""

import math
import os
import sys

import bpy

# ---- The rig these have to fit, read off scenes/Racer.tscn ----
#
# Godot puts the front hubs at z -1.05 and the rear at +1.00, both at x +/-0.65. Converted to
# Blender axes that is +/-1.05 and -1.00 along Y, which is why the numbers below look flipped.

FRONT_AXLE_Y = 1.05
REAR_AXLE_Y = -1.00
TRACK_HALF = 0.65
FRONT_HUB_Z = 0.315
REAR_HUB_Z = 0.34
TIRE_RADIUS = 0.24

# How far apart the three variants stand in the file.
VARIANT_SPACING = 4.0

OUTPUT = os.path.join("assets", "cars", "car_variants.blend")


# ---- Mesh building ----------------------------------------------------------------------------


def new_material(name, colour, roughness=1.0):
    """A flat matte material. Only the base colour matters — Godot rebuilds these on import via
    FlatShade, which keeps the albedo and throws the rest away. The colour is set on the node tree
    and on diffuse_color so it reads correctly in Blender's solid view too."""
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    material.diffuse_color = (*colour, 1.0)

    for node in material.node_tree.nodes:
        if node.type != "BSDF_PRINCIPLED":
            continue
        node.inputs["Base Color"].default_value = (*colour, 1.0)
        if "Roughness" in node.inputs:
            node.inputs["Roughness"].default_value = roughness
        if "Metallic" in node.inputs:
            node.inputs["Metallic"].default_value = 0.0

    return material


def new_object(name, verts, faces, material, parent):
    """Turn raw geometry into a flat-shaded object under `parent`.

    Normals are recalculated rather than trusted: the loft below emits faces in whatever winding
    falls out of the section order, and a car with half its faces inside out is a horrible thing to
    debug by eye."""
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.validate()
    mesh.update()

    # Flat, every face. This is the whole look — a smooth-shaded mesh averages its normals at the
    # corners and lights as a gradient, which is exactly what the track is not doing.
    for polygon in mesh.polygons:
        polygon.use_smooth = False

    mesh.materials.append(material)

    obj = bpy.data.objects.new(name, mesh)
    obj.parent = parent
    bpy.context.collection.objects.link(obj)

    recalculate_normals(obj)
    return obj


def recalculate_normals(obj):
    import bmesh

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()


def loft(name, sections, material, parent):
    """Bridge a run of rectangular cross-sections into a solid.

    Each section is (y, half_width, z_bottom, z_top): a slice across the car at some point along
    its length. Lofting rectangles is a deliberately blunt tool, and it is the right one — the
    result is all hard quads meeting at hard angles, which is the shape language of the whole
    style. Tapering the width and the two heights independently is enough to describe a wedge, a
    cabin, a nose or a wing."""
    verts = []
    faces = []

    for (y, half_width, z0, z1) in sections:
        verts += [
            (-half_width, y, z0),
            (half_width, y, z0),
            (half_width, y, z1),
            (-half_width, y, z1),
        ]

    for i in range(len(sections) - 1):
        a = i * 4
        b = a + 4
        faces += [
            (a + 0, b + 0, b + 1, a + 1),   # underside
            (a + 1, b + 1, b + 2, a + 2),   # right flank
            (a + 2, b + 2, b + 3, a + 3),   # deck
            (a + 3, b + 3, b + 0, a + 0),   # left flank
        ]

    last = (len(sections) - 1) * 4
    faces += [(0, 1, 2, 3), (last + 0, last + 1, last + 2, last + 3)]

    return new_object(name, verts, faces, material, parent)


def box(name, centre, size, material, parent):
    """An axis-aligned box, for the bits that are just bits: light pods, wing posts, exhausts."""
    cx, cy, cz = centre
    hx, hy, hz = size[0] / 2.0, size[1] / 2.0, size[2] / 2.0

    verts = [
        (cx - hx, cy - hy, cz - hz), (cx + hx, cy - hy, cz - hz),
        (cx + hx, cy + hy, cz - hz), (cx - hx, cy + hy, cz - hz),
        (cx - hx, cy - hy, cz + hz), (cx + hx, cy - hy, cz + hz),
        (cx + hx, cy + hy, cz + hz), (cx - hx, cy + hy, cz + hz),
    ]
    faces = [
        (0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4),
        (2, 3, 7, 6), (1, 2, 6, 5), (3, 0, 4, 7),
    ]
    return new_object(name, verts, faces, material, parent)


def cylinder(name, radius, y0, y1, segments, material, parent):
    """A drum around the Blender Y axis, coarse enough to show its facets.

    Ten sides rather than thirty-two on purpose: a smooth wheel would be the one round thing on a
    car made of flat planes, and the facets catching the light differently as it turns are doing
    real work at speed.

    Y is the axle axis because Blender Y becomes Godot -Z on import, and the rig's wheel hubs in
    Racer.tscn carry a ninety degree turn that takes Godot Z round to Godot X. That is the
    convention the CC96 rims were authored to, so matching it means these drop into the existing
    hubs without touching the scene."""
    verts = []
    faces = []

    for i in range(segments):
        angle = math.tau * i / segments
        x = math.cos(angle) * radius
        z = math.sin(angle) * radius
        verts += [(x, y0, z), (x, y1, z)]

    for i in range(segments):
        j = (i + 1) % segments
        faces.append((2 * i, 2 * j, 2 * j + 1, 2 * i + 1))

    faces.append(tuple(range(0, 2 * segments, 2)))
    faces.append(tuple(range(1, 2 * segments, 2)))

    return new_object(name, verts, faces, material, parent)


def build_wheel(name, radius, width, segments, outward, tire_material, rim_material, parent,
                location=(0.0, 0.0, 0.0)):
    """A tyre with a rim face set into one side of it.

    `outward` is +1 or -1 and says which way the rim faces, which is the only thing making a left
    wheel different from a right one. It is also why the rig needs two files rather than one: both
    hubs in Racer.tscn carry the same rotation, so the mirroring has to already be in the asset."""
    hub = bpy.data.objects.new(name, None)
    hub.location = location
    hub.empty_display_size = 0.15
    hub.parent = parent
    bpy.context.collection.objects.link(hub)

    half = width / 2.0
    cylinder(f"{name}_Tire", radius, -half, half, segments, tire_material, hub)

    # Set into the tyre rather than sitting on it, so the sidewall reads as a sidewall.
    cylinder(f"{name}_Rim", radius * 0.64, outward * half * 0.20, outward * half * 0.92,
             segments, rim_material, hub)
    cylinder(f"{name}_Hubcap", radius * 0.24, outward * half * 0.90, outward * half * 1.08,
             segments, rim_material, hub)

    return hub


# ---- The variants -----------------------------------------------------------------------------
#
# Three readings of the same brief, meant to be compared rather than all kept. They share a
# wheelbase and an origin, so switching between them in the outliner is a fair fight.

VARIANTS = [
    {
        "name": "A_Wedge",
        "blurb": "Countach. Short blunt nose, one unbroken rising line to a high chopped tail, "
                 "enormous wing.",
        "paint": (0.85, 0.09, 0.12),
        # (y, half width, z bottom, z top) from tail to nose. The nose stops well short of where
        # it could: a Countach's front overhang is tiny, and a long tapered snout reads as an F1
        # car instead. Nor does it come to a point — it stays wide and simply gets thin, which is
        # what makes a wedge a wedge.
        "body": [
            (-1.52, 0.62, 0.30, 0.78),
            (-1.15, 0.68, 0.24, 0.80),
            (-0.55, 0.68, 0.22, 0.76),
            (0.15, 0.64, 0.22, 0.62),
            (0.80, 0.60, 0.22, 0.50),
            (1.24, 0.58, 0.24, 0.46),
            (1.46, 0.54, 0.26, 0.44),   # a blunt face, not a blade: the wedge has to stop somewhere
        ],
        # Starting at the body's own width and shrinking, so the cabin grows out of the deck
        # rather than being parked on it.
        "canopy": [
            (-0.80, 0.62, 0.74, 0.80),
            (-0.55, 0.58, 0.78, 1.02),
            (0.10, 0.54, 0.66, 1.02),
            (0.45, 0.50, 0.60, 0.80),
        ],
        "wing": {"y": -1.46, "z": 1.10, "span": 1.50, "chord": 0.34},
        "lights": {"y": 1.42, "x": 0.30, "z": 0.37, "size": (0.24, 0.14, 0.10)},
        "splitter": {"y": 1.44, "z": 0.23, "span": 1.14, "chord": 0.26},
        "wheels": {"front_radius": 0.28, "rear_radius": 0.28, "width": 0.34, "segments": 10},
    },
    {
        "name": "B_Bubble",
        "blurb": "Mach 5. Snub rounded nose, one big glass bubble for a cabin, fat tyres, no wing.",
        "paint": (0.93, 0.93, 0.95),
        "body": [
            (-1.46, 0.62, 0.30, 0.66),
            (-1.10, 0.68, 0.24, 0.72),
            (-0.45, 0.68, 0.22, 0.72),
            (0.30, 0.66, 0.22, 0.64),
            (0.95, 0.62, 0.24, 0.56),
            (1.40, 0.58, 0.28, 0.50),
            (1.62, 0.52, 0.34, 0.46),   # snub: still fat where it ends
        ],
        # The bubble is the whole silhouette, so it is tall, long, and sits low into the body.
        "canopy": [
            (-0.85, 0.60, 0.68, 0.74),
            (-0.55, 0.60, 0.70, 1.06),
            (0.25, 0.58, 0.62, 1.08),
            (0.70, 0.50, 0.56, 0.86),
            (0.95, 0.42, 0.54, 0.62),
        ],
        "wing": None,
        "lights": {"y": 1.56, "x": 0.32, "z": 0.40, "size": (0.28, 0.14, 0.16)},
        "splitter": None,
        "wheels": {"front_radius": 0.30, "rear_radius": 0.30, "width": 0.40, "segments": 10},
    },
    {
        "name": "C_Cartoon",
        "blurb": "Proportions shoved as far as they will go: cabin jammed against the back axle, "
                 "enormous haunches over enormous rear wheels, wing on stilts.",
        "paint": (0.10, 0.45, 0.95),
        "body": [
            (-1.50, 0.58, 0.34, 0.72),
            (-1.20, 0.78, 0.26, 0.88),   # haunches, wider and taller than anything else
            (-0.75, 0.78, 0.24, 0.86),
            (-0.20, 0.62, 0.22, 0.60),
            (0.60, 0.56, 0.22, 0.50),
            (1.26, 0.54, 0.22, 0.46),
            (1.58, 0.50, 0.24, 0.42),
        ],
        "canopy": [
            (-1.00, 0.66, 0.86, 0.92),
            (-0.80, 0.60, 0.88, 1.18),
            (-0.35, 0.54, 0.62, 1.14),
            (-0.05, 0.48, 0.58, 0.76),
        ],
        "wing": {"y": -1.44, "z": 1.34, "span": 1.56, "chord": 0.36},
        "lights": {"y": 1.54, "x": 0.26, "z": 0.35, "size": (0.20, 0.14, 0.09)},
        "splitter": {"y": 1.56, "z": 0.22, "span": 1.02, "chord": 0.22},
        # A hot rod rake. Note this is the one variant whose wheels differ front to rear — Vehicle
        # exposes FrontTireRadius and RearTireRadius separately so it can be matched, but that is
        # a decision about the physics rather than a freebie.
        "wheels": {"front_radius": 0.24, "rear_radius": 0.36, "width": 0.42, "segments": 10},
    },
]


def build_variant(spec, offset_x):
    """Assemble one car as a parented set of objects.

    Kept as separate objects rather than one joined mesh so that each part carries its own
    material and can be shoved around on its own. Godot imports the whole hierarchy happily and
    FlatShade walks all of it, so nothing downstream cares."""
    root = bpy.data.objects.new(f"Car_{spec['name']}", None)
    root.location = (offset_x, 0.0, 0.0)
    root.empty_display_size = 0.5
    bpy.context.collection.objects.link(root)

    paint = new_material(f"{spec['name']}_Paint", spec["paint"])
    glass = new_material(f"{spec['name']}_Glass", (0.10, 0.16, 0.22))
    trim = new_material(f"{spec['name']}_Trim", (0.08, 0.08, 0.09))
    lamp = new_material(f"{spec['name']}_Lights", (1.00, 0.92, 0.62))
    tire = new_material(f"{spec['name']}_Tire", (0.06, 0.06, 0.07))
    rim = new_material(f"{spec['name']}_Rim", (0.78, 0.79, 0.82))

    loft(f"{spec['name']}_Body", spec["body"], paint, root)
    loft(f"{spec['name']}_Canopy", spec["canopy"], glass, root)

    lights = spec["lights"]
    for side in (-1, 1):
        box(f"{spec['name']}_Light_{'R' if side > 0 else 'L'}",
            (side * lights["x"], lights["y"], lights["z"]), lights["size"], lamp, root)

    if spec["wing"]:
        wing = spec["wing"]
        box(f"{spec['name']}_Wing", (0.0, wing["y"], wing["z"]),
            (wing["span"], wing["chord"], 0.07), trim, root)
        for side in (-1, 1):
            box(f"{spec['name']}_WingPost_{'R' if side > 0 else 'L'}",
                (side * 0.50, wing["y"], (wing["z"] + 0.70) / 2.0),
                (0.08, 0.14, wing["z"] - 0.70), trim, root)

    if spec["splitter"]:
        splitter = spec["splitter"]
        box(f"{spec['name']}_Splitter", (0.0, splitter["y"], splitter["z"]),
            (splitter["span"], splitter["chord"], 0.05), trim, root)

    # Exhausts, because a goofy car needs something poking out of the back of it.
    for side in (-1, 1):
        box(f"{spec['name']}_Exhaust_{'R' if side > 0 else 'L'}",
            (side * 0.26, spec["body"][0][0] - 0.10, 0.36), (0.14, 0.22, 0.14), trim, root)

    # Hub heights are the wheel's own radius rather than the rig's ray-cast origin, so every tyre
    # in the file sits on the same ground plane whatever size it is. A car drawn with its wheels
    # at different heights is a car whose stance cannot be judged.
    wheels = spec["wheels"]
    for label, axle_y, radius in (
        ("FL", FRONT_AXLE_Y, wheels["front_radius"]),
        ("FR", FRONT_AXLE_Y, wheels["front_radius"]),
        ("RL", REAR_AXLE_Y, wheels["rear_radius"]),
        ("RR", REAR_AXLE_Y, wheels["rear_radius"]),
    ):
        side = -1 if label.endswith("L") else 1
        build_wheel(f"{spec['name']}_Wheel_{label}", radius, wheels["width"], wheels["segments"],
                    side, tire, rim, root, location=(side * TRACK_HALF, axle_y, radius))

    return root


def clear_scene():
    """Start from nothing. Uses the data API rather than operators because operators want a screen
    context and this runs with --background."""
    for collection in (bpy.data.objects, bpy.data.meshes, bpy.data.materials):
        for item in list(collection):
            collection.remove(item)


def select_only(roots):
    """Select the given objects and everything under them, and nothing else."""
    for obj in bpy.data.objects:
        obj.select_set(False)

    def select(obj):
        obj.select_set(True)
        for child in obj.children:
            select(child)

    for root in roots:
        select(root)

    bpy.context.view_layer.objects.active = roots[0] if roots else None


def export_fbx(path, roots):
    """Write the selected hierarchy out as FBX.

    Four of these settings are the difference between an asset that works and one that arrives
    lying on its side, lit wrong, or dragging an animation track behind it:

      axis_forward / axis_up  Blender is Z-up and Godot is Y-up. '-Z' forward with 'Y' up is the
                              conversion Godot expects, and is what the exporter defaults to.
      mesh_smooth_type FACE   Writes the per-face smoothing, which is what keeps every facet flat
                              on the way in. Set this to 'OFF' and Godot smooths the normals on
                              import, which quietly undoes the entire art style.
      object_types MESH       Leaves out the empties used as parents here, which would otherwise
                              arrive as an extra layer of nodes doing nothing.
      bake_anim False         There is no animation. Without this the file carries an empty take.
    """
    os.makedirs(os.path.dirname(path), exist_ok=True)
    select_only(roots)

    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"MESH"},
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
        bake_anim=False,
        axis_forward="-Z",
        axis_up="Y",
        apply_unit_scale=True,
        global_scale=1.0,
        path_mode="COPY",
    )
    print(f"[car_blockout] wrote {path}")


def variant_files(spec):
    """Build one variant on its own at the origin and write out its source and its assets.

    The body and the wheels go to separate files because that is how the rig consumes them:
    Racer.tscn instances a body under BodyRig and four rims under the wheel hubs, so a single
    combined file would have to be taken apart again on the way in.
    """
    clear_scene()
    root = build_variant(spec, 0.0)

    body_parts = [c for c in root.children if "_Wheel_" not in c.name]
    left = next(c for c in root.children if c.name.endswith("_Wheel_FL"))
    right = next(c for c in root.children if c.name.endswith("_Wheel_FR"))

    export_fbx(os.path.join("assets", "cars", "Body", f"{spec['name']}_Body.fbx"), body_parts)

    # The wheels are exported from the origin, not from where they sit on the car — the rig puts
    # them in place, so an asset carrying its own position would end up doubly offset.
    for label, wheel_root in (("L", left), ("R", right)):
        held = wheel_root.location.copy()
        wheel_root.location = (0.0, 0.0, 0.0)
        export_fbx(os.path.join("assets", "cars", "Rims", f"{spec['name']}_Rim_{label}.fbx"),
                   [wheel_root])
        wheel_root.location = held

    source = os.path.abspath(os.path.join("assets", "cars", "src", f"{spec['name']}.blend"))
    os.makedirs(os.path.dirname(source), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=source)
    print(f"[car_blockout] wrote {source}")


def main():
    for spec in VARIANTS:
        print(f"[car_blockout] {spec['name']}: {spec['blurb']}")
        variant_files(spec)

    # Finally the side-by-side file, for comparing them rather than shipping them.
    clear_scene()
    first = -VARIANT_SPACING * (len(VARIANTS) - 1) / 2.0
    for index, spec in enumerate(VARIANTS):
        build_variant(spec, first + index * VARIANT_SPACING)

    output = os.path.abspath(OUTPUT)
    os.makedirs(os.path.dirname(output), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=output)
    print(f"[car_blockout] wrote {output}")


if __name__ == "__main__":
    main()
