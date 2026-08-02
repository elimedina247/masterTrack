"""Export one hand-built car out of assets/cars/car_variants.blend into the game's asset layout.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" --background \
        assets/cars/car_variants.blend --python tools/export_car_variant.py -- Car_H_Hatch H_Hatch

Arguments after the `--` are the root object in the blend and the variant name to write as:

    assets/cars/Body/<name>_Body.fbx     everything under the root that is not a wheel
    assets/cars/Rims/<name>_Rim_L.fbx    the FL wheel, from the origin  (only with --rims)
    assets/cars/Rims/<name>_Rim_R.fbx    the FR wheel, from the origin  (only with --rims)

Rims are opt-in because most hand-built variants are a clone of an existing car with a new shell:
they keep the donor's wheels, so `CarVariants` points them at the donor's rim files and no new
rim asset is needed. Pass --rims only when the wheels themselves were changed.

Like tools/export_tutorial_cars.py this never saves the blend, so it is safe to run against a
file you are still editing — and unlike tools/car_blockout.py it builds nothing, so hand edits
are never overwritten.

Object scales are applied on the way out. A part that was scaled in Object Mode — the natural way
to stretch a wing to fit — otherwise leaves its scale on the object rather than in the mesh, and
the exporter has to fold it into a node transform once the parent Empty is dropped. Applying it
here means the geometry that reaches Godot is the geometry seen in the viewport.
"""

import os
import sys

import bpy

# The repo root: this file lives in tools/.
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def select_only(objects):
    for obj in bpy.data.objects:
        obj.select_set(False)
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0] if objects else None


def apply_transforms(objects):
    """Bake object rotation and scale into the meshes. Location is left alone: it is what places
    each part on the car, and the car as a whole is brought to the origin by moving the root."""
    select_only(objects)
    if objects:
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)


def export_fbx(path, objects):
    """The four settings that decide whether the asset arrives usable — see assets/cars/README.md:
    the -Z/Y axis conversion, per-face smoothing (Godot re-smooths without it, undoing the flat
    look), meshes only, and no animation take."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    select_only(objects)
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
    print(f"[export_car_variant] wrote {path}")


def report_bounds(label, objects):
    """Print the world-space extent of what was exported, so the caller can check the asset that
    lands matches the car in the blend rather than trusting the exporter."""
    lo = [float("inf")] * 3
    hi = [float("-inf")] * 3
    for obj in objects:
        for corner in obj.bound_box:
            world = obj.matrix_world @ __import__("mathutils").Vector(corner)
            for axis in range(3):
                lo[axis] = min(lo[axis], world[axis])
                hi[axis] = max(hi[axis], world[axis])
    size = [hi[i] - lo[i] for i in range(3)]
    print(f"[export_car_variant] {label} bounds "
          f"min=({lo[0]:.3f}, {lo[1]:.3f}, {lo[2]:.3f}) "
          f"max=({hi[0]:.3f}, {hi[1]:.3f}, {hi[2]:.3f}) "
          f"size=({size[0]:.3f}, {size[1]:.3f}, {size[2]:.3f})")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if len(argv) < 2:
        print("[export_car_variant] usage: -- <RootObjectName> <VariantName> [--rims]")
        return

    root_name, variant = argv[0], argv[1]
    want_rims = "--rims" in argv

    root = bpy.data.objects.get(root_name)
    if root is None:
        print(f"[export_car_variant] no object named {root_name} in this blend")
        return

    # Everything exports relative to the rig origin, so the car comes home first. The parts are
    # parented to the root, so they come with it.
    root.location = (0.0, 0.0, 0.0)

    body_parts = [c for c in root.children if "_Wheel_" not in c.name and c.type == "MESH"]
    wheels = {c.name[-6:-4] if c.name[-4:].startswith(".") else c.name[-2:]: c
              for c in root.children if "_Wheel_" in c.name}

    if not body_parts:
        print(f"[export_car_variant] {root_name} has no body parts to export")
        return

    print(f"[export_car_variant] {root_name} -> {variant}: "
          f"{len(body_parts)} body part(s), {len(wheels)} wheel(s)")
    for part in sorted(body_parts, key=lambda o: o.name):
        mats = [ms.material.name if ms.material else "<NO MATERIAL>" for ms in part.material_slots]
        if not mats:
            mats = ["<NO MATERIAL>"]
        print(f"[export_car_variant]     {part.name}  {mats}")

    apply_transforms(body_parts)
    report_bounds(f"{variant} body", body_parts)
    export_fbx(os.path.join(ROOT, "assets", "cars", "Body", f"{variant}_Body.fbx"), body_parts)

    if not want_rims:
        print("[export_car_variant] rims not exported (no --rims); point CarVariants at the "
              "donor car's rim files")
        return

    for label, hub_key in (("L", "FL"), ("R", "FR")):
        hub = wheels.get(hub_key)
        if hub is None:
            print(f"[export_car_variant] SKIP rim {label}: no {hub_key} wheel under {root_name}")
            continue
        # The rig instances rims at its own hubs, so the asset must not carry a position of its
        # own — a rim exported in place would arrive doubly offset.
        held = hub.location.copy()
        hub.location = (0.0, 0.0, 0.0)
        apply_transforms([hub])
        export_fbx(os.path.join(ROOT, "assets", "cars", "Rims", f"{variant}_Rim_{label}.fbx"),
                   [hub])
        hub.location = held

    print("[export_car_variant] done (blend not saved; hand edits untouched)")


if __name__ == "__main__":
    main()
