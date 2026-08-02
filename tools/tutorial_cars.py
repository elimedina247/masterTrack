"""Two new cars in the Derek Elliott lowpoly style (youtube.com/watch?v=ZJXWIFQXqrI).

H_Derrk is the car the tutorial builds: an orange Datsun-510-ish rally coupe, quad round
headlights, chrome bumpers, ducktail, white 22 livery. I_Hachi is the same language applied to an
AE86 Trueno: panda two-tone, pop-up pods, black plastic bumpers.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" --background --python tools/tutorial_cars.py -- [render_dir]

Writes assets/cars/tutorial_cars.blend with both cars side by side. **Overwrites that file every
run** — once hand-editing starts, stop running this or change OUTPUT. If a render_dir is given
after the "--", also renders preview PNGs there (the render rig is added after the save, so the
blend stays clean).

Same conventions as car_blockout.py: nose along Blender +Y, wheel axles along +Y, origin on the
rig's origin, every face flat-shaded, wheels as their own objects so the rig can instance rims
separately. The tutorial look adds two things the blockout script didn't have: bevel modifiers
(the chunky rounded edge is most of the style) and per-face materials (the greenhouse is one mesh
whose window faces are glass and whose roof is paint, which is how the video does it).
"""

import math
import os
import sys

import bpy
from mathutils import Vector

# ---- The rig these have to fit, read off scenes/Racer.tscn (see car_blockout.py) ----
FRONT_AXLE_Y = 1.05
REAR_AXLE_Y = -1.00
TRACK_HALF = 0.65
TIRE_RADIUS = 0.29          # what E/F/G were modelled at; keeps the physics numbers familiar

VARIANT_SPACING = 4.4
OUTPUT = os.path.join("assets", "cars", "tutorial_cars.blend")


# ---- Mesh building ----------------------------------------------------------------------------


def new_material(name, colour, roughness=1.0):
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


def add_bevel(obj, width):
    modifier = obj.modifiers.new("Bevel", "BEVEL")
    modifier.width = width
    modifier.segments = 2
    modifier.limit_method = "ANGLE"
    modifier.angle_limit = math.radians(40.0)
    modifier.use_clamp_overlap = True
    modifier.harden_normals = False
    return modifier


def new_object(name, verts, faces, materials, parent, face_materials=None, bevel=0.0,
               location=(0.0, 0.0, 0.0), rotation=(0.0, 0.0, 0.0)):
    """Raw geometry to flat-shaded object. `materials` is a list; `face_materials` gives a slot
    index per face (default all slot 0). `bevel` adds a live bevel modifier — the rounded-off
    edge is most of what makes the tutorial cars read as friendly rather than boxy."""
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.validate()
    mesh.update()

    for material in materials:
        mesh.materials.append(material)
    if face_materials is not None:
        for polygon, index in zip(mesh.polygons, face_materials):
            polygon.material_index = index

    for polygon in mesh.polygons:
        polygon.use_smooth = False

    obj = bpy.data.objects.new(name, mesh)
    obj.parent = parent
    obj.location = location
    obj.rotation_euler = rotation
    bpy.context.collection.objects.link(obj)

    recalculate_normals(obj)

    if bevel > 0.0:
        add_bevel(obj, bevel)

    return obj


def recalculate_normals(obj):
    import bmesh
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()


def loft(name, sections, materials, parent, face_material_bands=None, bevel=0.0):
    """Bridge rectangular cross-sections, tail to nose. Each section is
    (y, half_width, z_bottom, z_top). `face_material_bands` maps the four bands and two caps to
    material slots as (underside, right, deck, left, tail_cap, nose_cap) — that is how the
    greenhouse gets glass flanks under a painted roof without being two objects."""
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

    face_materials = None
    if face_material_bands is not None:
        underside, right, deck, left, tail_cap, nose_cap = face_material_bands
        face_materials = []
        for _ in range(len(sections) - 1):
            face_materials += [underside, right, deck, left]
        face_materials += [tail_cap, nose_cap]

    return new_object(name, verts, faces, materials, parent,
                      face_materials=face_materials, bevel=bevel)


def frustum_box(name, centre, size, material, parent, top_shift=(0.0, 0.0), top_size=None,
                bevel=0.0):
    """A box whose top face can be shifted (in X, Y) and resized: one helper that covers plain
    boxes, ducktails, air dams, mirror shells and pop-up pods."""
    cx, cy, cz = centre
    hx, hy, hz = size[0] / 2.0, size[1] / 2.0, size[2] / 2.0
    tx = size[0] / 2.0 if top_size is None else top_size[0] / 2.0
    ty = size[1] / 2.0 if top_size is None else top_size[1] / 2.0
    sx, sy = top_shift

    verts = [
        (cx - hx, cy - hy, cz - hz), (cx + hx, cy - hy, cz - hz),
        (cx + hx, cy + hy, cz - hz), (cx - hx, cy + hy, cz - hz),
        (cx - tx + sx, cy - ty + sy, cz + hz), (cx + tx + sx, cy - ty + sy, cz + hz),
        (cx + tx + sx, cy + ty + sy, cz + hz), (cx - tx + sx, cy + ty + sy, cz + hz),
    ]
    faces = [
        (0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4),
        (2, 3, 7, 6), (1, 2, 6, 5), (3, 0, 4, 7),
    ]
    return new_object(name, verts, faces, [material], parent, bevel=bevel)


def cylinder(name, radius, a0, a1, segments, material, parent, location=(0.0, 0.0, 0.0),
             axis="Y"):
    """A faceted drum. Axis "Y" is the wheel-asset convention (Blender +Y becomes Godot -Z,
    which the rig's hub rotation then takes round to the axle); axis "X" is for parts that live
    on the car standing up, like arch liners. Coarse on purpose; the facets are the style."""
    verts = []
    faces = []
    for i in range(segments):
        angle = math.tau * i / segments
        u = math.cos(angle) * radius
        v = math.sin(angle) * radius
        if axis == "Y":
            verts += [(u, a0, v), (u, a1, v)]
        else:
            verts += [(a0, u, v), (a1, u, v)]
    for i in range(segments):
        j = (i + 1) % segments
        faces.append((2 * i, 2 * j, 2 * j + 1, 2 * i + 1))
    faces.append(tuple(range(0, 2 * segments, 2)))
    faces.append(tuple(range(1, 2 * segments, 2)))
    return new_object(name, verts, faces, [material], parent, location=location)


def cut_arches(body, name, axle_ys, radius, segments=12):
    """Carve wheel arches through the body flanks with an applied boolean, one faceted tunnel per
    axle. Applied rather than left live so the blend carries no cutter objects and no modifier
    order traps for whoever edits it next."""
    cutters = []
    for index, axle_y in enumerate(axle_ys):
        # Parented to the car's root so the cut lands on the car wherever the car stands.
        cutter = cylinder(f"{name}_ArchCut{index}", radius, -1.2, 1.2, segments, None,
                          body.parent, axis="X")
        cutter.location = (0.0, axle_y, TIRE_RADIUS)
        cutters.append(cutter)

    for cutter in cutters:
        modifier = body.modifiers.new("Arch", "BOOLEAN")
        modifier.operation = "DIFFERENCE"
        modifier.object = cutter
        modifier.solver = "EXACT"

    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = body.evaluated_get(depsgraph)
    baked = bpy.data.meshes.new_from_object(evaluated)
    for polygon in baked.polygons:
        polygon.use_smooth = False

    old = body.data
    for material in old.materials:
        baked.materials.append(material)
    body.modifiers.clear()
    body.data = baked
    bpy.data.meshes.remove(old)

    for cutter in cutters:
        mesh = cutter.data
        bpy.data.objects.remove(cutter)
        bpy.data.meshes.remove(mesh)


def text_mesh(name, body, size, material, parent, location, rotation, extrude=0.012):
    """The 22 on the doors. A FONT curve converted to real mesh so the blend carries no live text
    dependency and the FBX exporter has nothing to guess about."""
    curve = bpy.data.curves.new(f"{name}_Curve", type="FONT")
    curve.body = body
    curve.size = size
    curve.extrude = extrude
    curve.align_x = "CENTER"
    curve.align_y = "CENTER"

    holder = bpy.data.objects.new(f"{name}_Holder", curve)
    bpy.context.collection.objects.link(holder)

    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = holder.evaluated_get(depsgraph)
    mesh = bpy.data.meshes.new_from_object(evaluated)

    bpy.data.objects.remove(holder)
    bpy.data.curves.remove(curve)

    for polygon in mesh.polygons:
        polygon.use_smooth = False
    mesh.materials.append(material)

    obj = bpy.data.objects.new(name, mesh)
    obj.parent = parent
    obj.location = location
    obj.rotation_euler = rotation
    bpy.context.collection.objects.link(obj)
    return obj


def annulus(name, r_out, r_in, a0, a1, segments, material, parent):
    """A tyre-shaped ring around Blender Y: outer tread, two side walls, inner bore. The hole is
    the point — a closed drum would hide the rim behind its own end cap."""
    verts = []
    faces = []
    for i in range(segments):
        angle = math.tau * i / segments
        c = math.cos(angle)
        s = math.sin(angle)
        verts += [
            (c * r_out, a0, s * r_out), (c * r_out, a1, s * r_out),
            (c * r_in, a0, s * r_in), (c * r_in, a1, s * r_in),
        ]
    for i in range(segments):
        j = (i + 1) % segments
        a, b = 4 * i, 4 * j
        faces += [
            (a + 0, b + 0, b + 1, a + 1),   # tread
            (a + 0, b + 0, b + 2, a + 2),   # side wall
            (a + 1, b + 1, b + 3, a + 3),   # other side wall
            (a + 2, b + 2, b + 3, a + 3),   # bore
        ]
    return new_object(name, verts, faces, [material], parent)


def build_wheel(name, radius, width, segments, outward, tire_material, rim_material,
                cap_material, parent, location):
    """Tyre ring around a deep-dish rim. `outward` (+/-1) is what makes a left wheel a left
    wheel; the rig carries the same rotation on all four hubs, so the mirroring must be in the
    asset (see assets/cars/README.md)."""
    hub = bpy.data.objects.new(name, None)
    hub.location = location
    hub.empty_display_size = 0.15
    hub.parent = parent
    bpy.context.collection.objects.link(hub)

    half = width / 2.0
    face = outward * half
    annulus(f"{name}_Tire", radius, radius * 0.63, -half, half, segments, tire_material, hub)

    # The barrel's end cap, recessed into the tyre, is the dish floor; the centre cap steps back
    # out toward the face. Reads as a deep-dish rally wheel without a single spoke.
    cylinder(f"{name}_Rim", radius * 0.615, -outward * half, face * 0.60, segments,
             rim_material, hub)
    cylinder(f"{name}_Hubcap", radius * 0.24, face * 0.35, face * 0.86, segments,
             cap_material, hub)

    return hub


# ---- The two cars -----------------------------------------------------------------------------

DERRK = {
    "name": "H_Derrk",
    "blurb": "The tutorial car: orange 510 rally coupe, quad lights, chrome, ducktail, 22.",
    # Boxy three-box sedan. The beltline stays high and nearly flat — the 510 look is a straight
    # shoulder from nose to tail with almost no wedge — and the slab is deep enough that the
    # wheels tuck under it instead of dwarfing it.
    "paint": (0.930, 0.190, 0.030),
    "arch_radius": 0.35,
    "flare_material": "paint",
    "flare_z": (0.16, 0.66),
    "flare_length": 0.86,
    "body": [
        (-1.58, 0.56, 0.30, 0.66),   # tail panel
        (-1.50, 0.62, 0.22, 0.71),   # over the rear bumper
        (-0.95, 0.64, 0.14, 0.73),   # rear haunch
        (0.00, 0.64, 0.14, 0.72),    # doors
        (0.90, 0.64, 0.14, 0.70),    # front fender
        (1.45, 0.62, 0.20, 0.66),    # over the front bumper
        (1.56, 0.56, 0.26, 0.62),    # nose panel the lights sit in
    ],
    # One mesh: glass flanks and caps, painted roof.
    "greenhouse": [
        (-1.02, 0.52, 0.70, 0.76),   # base of the C pillar
        (-0.80, 0.49, 0.72, 1.06),   # rear screen rake
        (0.02, 0.47, 0.72, 1.08),    # roof
        (0.55, 0.43, 0.70, 0.80),    # windscreen rake
    ],
    "two_tone": None,
    "wheels": {"radius": TIRE_RADIUS, "width": 0.26, "segments": 10,
               "rim": (0.82, 0.83, 0.86), "cap": (0.92, 0.92, 0.94)},
}

HACHI = {
    "name": "I_Hachi",
    "blurb": "Hachi roku: panda Trueno, pop-up pods, black plastic, liftback tail.",
    "paint": (0.930, 0.930, 0.940),
    "arch_radius": 0.32,
    "flare_material": "trim",
    "flare_z": (0.13, 0.60),
    "flare_length": 0.76,
    # Flatter and squarer than the 510: the AE86 hood is nearly level with pop-up pods on its
    # leading edge, and the liftback glass runs almost to the tail before dropping.
    "body": [
        (-1.60, 0.55, 0.30, 0.57),   # tail panel
        (-1.52, 0.62, 0.22, 0.61),   # over the rear bumper
        (-0.90, 0.64, 0.14, 0.68),   # rear quarter
        (0.05, 0.64, 0.14, 0.68),    # doors
        (0.95, 0.64, 0.14, 0.64),    # front fender
        (1.50, 0.62, 0.20, 0.60),    # nearly-flat hood to the nose
        (1.60, 0.56, 0.24, 0.57),
    ],
    "greenhouse": [
        (-1.38, 0.50, 0.60, 0.64),   # liftback glass runs nearly to the tail
        (-0.76, 0.49, 0.64, 1.00),
        (-0.02, 0.47, 0.66, 1.04),   # roof
        (0.52, 0.43, 0.64, 0.74),    # windscreen
    ],
    # The panda split: a black band riding proud of the lower body, nose to tail.
    "two_tone": {"colour": (0.055, 0.055, 0.060), "z0": 0.15, "z1": 0.32, "grow": 0.015},
    "wheels": {"radius": TIRE_RADIUS, "width": 0.26, "segments": 10,
               "rim": (0.36, 0.34, 0.31), "cap": (0.72, 0.70, 0.66)},
}


def build_derrk_details(root, mats):
    """Everything that makes the tutorial car the tutorial car."""
    paint, trim, chrome, lamp, white, red = (
        mats["paint"], mats["trim"], mats["chrome"], mats["lamp"], mats["white"], mats["red"])

    # Chrome bumpers, slightly wider than the body so they read as separate metal.
    frustum_box("H_Derrk_BumperF", (0.0, 1.58, 0.26), (1.38, 0.16, 0.11), chrome, root,
                bevel=0.015)
    frustum_box("H_Derrk_BumperR", (0.0, -1.60, 0.26), (1.32, 0.14, 0.11), chrome, root,
                bevel=0.015)

    # Grille recess: a dark slab across the nose the lights sit in front of.
    frustum_box("H_Derrk_Grille", (0.0, 1.555, 0.47), (1.10, 0.06, 0.17), trim, root)

    # Quad round headlights: two drums a side, poking through the grille panel.
    for side in (-1, 1):
        for pair, x in ((0, 0.43), (1, 0.25)):
            cylinder(f"H_Derrk_Lamp_{'R' if side > 0 else 'L'}{pair}", 0.075, -0.04, 0.05, 8,
                     lamp, root, location=(side * x, 1.575, 0.47))

    # Tail: light bar ends and a chrome trunk strip.
    for side in (-1, 1):
        frustum_box(f"H_Derrk_Tail_{'R' if side > 0 else 'L'}",
                    (side * 0.38, -1.585, 0.53), (0.30, 0.045, 0.11), red, root)
    frustum_box("H_Derrk_TailTrim", (0.0, -1.585, 0.43), (1.04, 0.035, 0.05), chrome, root)

    # Ducktail: the top face swept back and shrunk so the lip kicks up.
    frustum_box("H_Derrk_Ducktail", (0.0, -1.475, 0.745), (1.10, 0.28, 0.08), paint, root,
                top_shift=(0.0, -0.05), top_size=(1.10, 0.15), bevel=0.012)

    # Mirrors, bedded into the body side so they cannot float.
    for side in (-1, 1):
        frustum_box(f"H_Derrk_Mirror_{'R' if side > 0 else 'L'}",
                    (side * 0.67, 0.56, 0.755), (0.14, 0.08, 0.10), paint, root,
                    top_shift=(side * 0.02, 0.0), top_size=(0.14, 0.06), bevel=0.010)

    # Livery: white hood panel (a sloped slab lofted to follow the hood), white 22 on the doors,
    # white nose square.
    loft("H_Derrk_HoodPanel", [(0.78, 0.34, 0.700, 0.714), (1.26, 0.34, 0.668, 0.682)],
         [white], root)
    text_mesh("H_Derrk_HoodText", "DERRK", 0.17, trim, root,
              location=(0.0, 1.02, 0.703), rotation=(0.0, 0.0, math.pi))
    frustum_box("H_Derrk_NosePanel", (0.0, 1.565, 0.60), (0.44, 0.03, 0.075), white, root)
    for side, rz in ((1, math.pi / 2.0), (-1, -math.pi / 2.0)):
        text_mesh(f"H_Derrk_No22_{'R' if side > 0 else 'L'}", "22", 0.40, white, root,
                  location=(side * 0.648, -0.30, 0.44), rotation=(math.pi / 2.0, 0.0, rz))

    # Exhaust, offset left like the video's.
    frustum_box("H_Derrk_Exhaust", (-0.30, -1.64, 0.18), (0.10, 0.14, 0.08), trim, root)


def build_hachi_details(root, mats):
    """Everything that makes the hachi the hachi."""
    paint, trim, lamp, red, amber = (
        mats["paint"], mats["trim"], mats["lamp"], mats["red"], mats["amber"])

    # Black plastic bumpers — body-width, chunkier than the 510's chrome.
    frustum_box("I_Hachi_BumperF", (0.0, 1.60, 0.25), (1.34, 0.18, 0.12), trim, root,
                bevel=0.012)
    frustum_box("I_Hachi_AirDam", (0.0, 1.58, 0.165), (1.26, 0.14, 0.07), trim, root,
                top_shift=(0.0, 0.03), bevel=0.010)
    frustum_box("I_Hachi_BumperR", (0.0, -1.62, 0.25), (1.30, 0.16, 0.12), trim, root,
                bevel=0.012)

    # Pop-up pods, up, at the front corners of the hood, with a lamp face on the front.
    for side in (-1, 1):
        tag = "R" if side > 0 else "L"
        frustum_box(f"I_Hachi_Pod_{tag}", (side * 0.42, 1.26, 0.64), (0.30, 0.26, 0.12),
                    paint, root, top_shift=(0.0, -0.03), top_size=(0.28, 0.20), bevel=0.010)
        frustum_box(f"I_Hachi_PodLamp_{tag}", (side * 0.42, 1.392, 0.64), (0.24, 0.02, 0.085),
                    lamp, root)

    # Corner indicators tucked above the bumper line.
    for side in (-1, 1):
        frustum_box(f"I_Hachi_Indicator_{'R' if side > 0 else 'L'}",
                    (side * 0.50, 1.595, 0.335), (0.14, 0.03, 0.045), amber, root)

    # Liftback tail: full-width red bar over black trim, and a small black lip spoiler.
    frustum_box("I_Hachi_TailBar", (0.0, -1.595, 0.50), (1.06, 0.045, 0.11), red, root)
    frustum_box("I_Hachi_TailTrim", (0.0, -1.595, 0.41), (1.10, 0.035, 0.04), trim, root)
    frustum_box("I_Hachi_Lip", (0.0, -1.51, 0.615), (1.08, 0.20, 0.05), trim, root,
                top_shift=(0.0, -0.04), top_size=(1.08, 0.11), bevel=0.010)

    # Mirrors: black shells bedded into the body side.
    for side in (-1, 1):
        frustum_box(f"I_Hachi_Mirror_{'R' if side > 0 else 'L'}",
                    (side * 0.66, 0.54, 0.715), (0.13, 0.08, 0.09), trim, root,
                    top_shift=(side * 0.02, 0.0), top_size=(0.13, 0.055), bevel=0.008)

    # Exhaust, right side, because it is that car.
    frustum_box("I_Hachi_Exhaust", (0.32, -1.66, 0.18), (0.11, 0.14, 0.08), trim, root)


def build_variant(spec, offset_x, detail_builder):
    root = bpy.data.objects.new(f"Car_{spec['name']}", None)
    root.location = (offset_x, 0.0, 0.0)
    root.empty_display_size = 0.5
    bpy.context.collection.objects.link(root)

    name = spec["name"]
    mats = {
        "paint": new_material(f"{name}_Paint", spec["paint"]),
        "glass": new_material(f"{name}_Glass", (0.09, 0.13, 0.17)),
        "trim": new_material(f"{name}_Trim", (0.07, 0.07, 0.08)),
        "chrome": new_material(f"{name}_Chrome", (0.80, 0.81, 0.84), roughness=0.35),
        "lamp": new_material(f"{name}_Lamp", (1.00, 0.95, 0.78)),
        "white": new_material(f"{name}_White", (0.96, 0.96, 0.96)),
        "red": new_material(f"{name}_Red", (0.62, 0.05, 0.05)),
        "amber": new_material(f"{name}_Amber", (0.95, 0.55, 0.10)),
    }

    arch = spec["arch_radius"]
    body = loft(f"{name}_Body", spec["body"], [mats["paint"]], root)
    cut_arches(body, name, (FRONT_AXLE_Y, REAR_AXLE_Y), arch)
    add_bevel(body, 0.024)

    # Box fender flares, arch-cut like the body, so the wheels sit in openings rather than
    # hanging off the flanks — the tutorial car's rally-flare look. Plain slabs: a tapered top
    # left ugly seams across the arch ring.
    flare_mat = mats[spec["flare_material"]]
    flare_z0, flare_z1 = spec["flare_z"]
    for label, axle_y in (("F", FRONT_AXLE_Y), ("R", REAR_AXLE_Y)):
        for side in (-1, 1):
            tag = f"{label}{'R' if side > 0 else 'L'}"
            flare = frustum_box(f"{name}_Flare_{tag}",
                                (side * 0.715, axle_y, (flare_z0 + flare_z1) / 2.0),
                                (0.13, spec["flare_length"], flare_z1 - flare_z0),
                                flare_mat, root)
            cut_arches(flare, f"{name}_Flare_{tag}", (axle_y,), arch)
            add_bevel(flare, 0.012)

    # Dark drums filling the arch tunnels, so looking through an arch shows a wheel well and not
    # daylight out the far side. Body geometry, not wheel geometry — they don't turn.
    for label, axle_y in (("F", FRONT_AXLE_Y), ("R", REAR_AXLE_Y)):
        cylinder(f"{name}_ArchLiner_{label}", arch - 0.018, -0.72, 0.72, 12, mats["trim"],
                 root, location=(0.0, axle_y, TIRE_RADIUS), axis="X")

    # Painted roof over glass flanks and caps, one mesh, per-face materials.
    loft(f"{name}_Greenhouse", spec["greenhouse"], [mats["paint"], mats["glass"]], root,
         face_material_bands=(1, 1, 0, 1, 1, 1), bevel=0.018)

    if spec["two_tone"]:
        band = spec["two_tone"]
        sections = [(y, hw + band["grow"], band["z0"], band["z1"])
                    for (y, hw, _z0, _z1) in spec["body"]]
        tone = loft(f"{name}_TwoTone", sections,
                    [new_material(f"{name}_Black", band["colour"])], root)
        cut_arches(tone, f"{name}_TwoTone", (FRONT_AXLE_Y, REAR_AXLE_Y), arch)
        add_bevel(tone, 0.012)

    detail_builder(root, mats)

    wheels = spec["wheels"]
    tire = new_material(f"{name}_Tire", (0.055, 0.055, 0.06))
    rim = new_material(f"{name}_Rim", wheels["rim"], roughness=0.5)
    cap = new_material(f"{name}_Cap", wheels["cap"], roughness=0.5)
    for label, axle_y in (("FL", FRONT_AXLE_Y), ("FR", FRONT_AXLE_Y),
                          ("RL", REAR_AXLE_Y), ("RR", REAR_AXLE_Y)):
        side = -1 if label.endswith("L") else 1
        build_wheel(f"{name}_Wheel_{label}", wheels["radius"], wheels["width"],
                    wheels["segments"], side, tire, rim, cap, root,
                    location=(side * TRACK_HALF, axle_y, wheels["radius"]))

    return root


# ---- Scene / render ---------------------------------------------------------------------------


def clear_scene():
    for collection in (bpy.data.objects, bpy.data.meshes, bpy.data.materials,
                       bpy.data.curves, bpy.data.cameras, bpy.data.lights):
        for item in list(collection):
            collection.remove(item)


def set_hidden(root, hidden):
    root.hide_render = hidden
    for child in root.children:
        set_hidden(child, hidden)


def add_render_rig():
    floor_mat = new_material("Preview_Floor", (0.55, 0.56, 0.58))
    frustum_box("Preview_Floor", (0.0, 0.0, -0.05), (40.0, 40.0, 0.1), floor_mat, None)

    camera_data = bpy.data.cameras.new("Preview_Camera")
    camera_data.lens = 55.0
    camera = bpy.data.objects.new("Preview_Camera", camera_data)
    bpy.context.collection.objects.link(camera)
    bpy.context.scene.camera = camera

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.display.shading.show_shadows = True
    scene.display.shading.shadow_intensity = 0.35
    scene.display.render_aa = "8"
    scene.render.resolution_x = 1024
    scene.render.resolution_y = 768
    return camera


def render_view(camera, path, location, target):
    camera.location = location
    direction = Vector(target) - Vector(location)
    camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene = bpy.context.scene
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print(f"[tutorial_cars] rendered {path}")


def render_previews(render_dir, roots):
    camera = add_render_rig()
    os.makedirs(render_dir, exist_ok=True)

    # The wheel assets are authored axle-along-+Y (the export convention — see
    # assets/cars/README.md), which photographs as a coin facing the camera. For the previews
    # only, stand them up: rotate each hub so its wheel sits the way the rig will sit it. This
    # runs after the blend is saved, so the file keeps the export orientation.
    for _name, _offset, root in roots:
        for child in root.children:
            if "_Wheel_" not in child.name:
                continue
            side = 1 if child.name.endswith("R") else -1
            child.rotation_euler = (0.0, 0.0, -side * math.pi / 2.0)

    # Each car is shot alone — the other one hidden — so a side view is a side view and not a
    # portrait of the neighbour's bumper.
    for name, offset, root in roots:
        for other_name, _o, other_root in roots:
            set_hidden(other_root, other_root is not root)

        target = (offset, 0.0, 0.42)
        views = {
            "front34": (offset + 3.1, 3.4, 1.75),
            "rear34": (offset - 3.1, -3.4, 1.75),
            "side": (offset + 5.6, 0.0, 0.80),
            "front": (offset + 0.0, 5.0, 0.75),
            "hood": (offset + 0.0, 3.4, 3.2),
        }
        for view, location in views.items():
            render_view(camera, os.path.join(render_dir, f"{name}_{view}.png"),
                        location, target)

    for _name, _offset, root in roots:
        set_hidden(root, False)
    both_x = sum(offset for _n, offset, _r in roots) / len(roots)
    render_view(camera, os.path.join(render_dir, "both_beauty.png"),
                (both_x + 4.4, 4.6, 2.3), (both_x, 0.0, 0.42))


def main():
    clear_scene()

    roots = []
    for index, (spec, details) in enumerate(((DERRK, build_derrk_details),
                                             (HACHI, build_hachi_details))):
        offset = (index - 0.5) * VARIANT_SPACING
        print(f"[tutorial_cars] {spec['name']}: {spec['blurb']}")
        root = build_variant(spec, offset, details)
        roots.append((spec["name"], offset, root))

    output = os.path.abspath(OUTPUT)
    os.makedirs(os.path.dirname(output), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=output)
    print(f"[tutorial_cars] wrote {output}")

    argv = sys.argv
    if "--" in argv:
        extra = argv[argv.index("--") + 1:]
        if extra:
            render_previews(extra[0], roots)


if __name__ == "__main__":
    main()
