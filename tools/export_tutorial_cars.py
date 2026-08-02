"""Export the tutorial cars from assets/cars/tutorial_cars.blend into the game's asset layout.

    "C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" --background assets/cars/tutorial_cars.blend --python tools/export_tutorial_cars.py

Reads the blend it was opened with and writes, per car:

    assets/cars/Body/<name>_Body.fbx    everything that isn't a wheel
    assets/cars/Rims/<name>_Rim_L.fbx   the FL wheel, moved to the origin
    assets/cars/Rims/<name>_Rim_R.fbx   the FR wheel, moved to the origin

It never saves the blend — hand edits stay exactly as they are. Unlike car_blockout.py it does
not rebuild anything, so it is safe to run against an edited file.

One normalisation happens on the way out: wheel part rotations are cleared. The rig consumes
rims authored axle-along-+Y (see assets/cars/README.md), but standing the wheels up for display
in the blend — a plain rotation on the tyre/rim objects — is a natural hand edit. Clearing
object rotations puts every part back in the export orientation without touching the meshes.
"""

import os
import sys

import bpy

CARS = ("H_Derrk", "I_Hachi")

# The repo root: this file lives in tools/.
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def select_only(objects):
    for obj in bpy.data.objects:
        obj.select_set(False)
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0] if objects else None


def export_fbx(path, objects):
    """Same four load-bearing settings as car_blockout.py: the axis conversion, per-face
    smoothing (Godot re-smooths without it, which undoes the art style), meshes only, no
    animation take."""
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
    print(f"[export_tutorial_cars] wrote {path}")


def main():
    for name in CARS:
        root = bpy.data.objects.get(f"Car_{name}")
        if root is None:
            print(f"[export_tutorial_cars] SKIP {name}: no Car_{name} root in this blend")
            continue

        # Everything exports relative to the rig origin, so the car comes home first.
        root.location = (0.0, 0.0, 0.0)

        wheels = {}
        body_parts = []
        for child in root.children:
            if "_Wheel_" in child.name:
                wheels[child.name[-2:]] = child
                # Back to axle-along-+Y: display rotations off every part of the wheel.
                child.rotation_euler = (0.0, 0.0, 0.0)
                for part in child.children:
                    part.rotation_euler = (0.0, 0.0, 0.0)
            elif child.type == "MESH":
                body_parts.append(child)

        export_fbx(os.path.join(ROOT, "assets", "cars", "Body", f"{name}_Body.fbx"),
                   body_parts)

        # The rig instances rims at its own hubs, so the asset must not carry a position of its
        # own — a rim exported in place would arrive doubly offset.
        for label, hub_key in (("L", "FL"), ("R", "FR")):
            hub = wheels.get(hub_key)
            if hub is None:
                print(f"[export_tutorial_cars] SKIP {name} rim {label}: no {hub_key} wheel")
                continue
            held = hub.location.copy()
            hub.location = (0.0, 0.0, 0.0)
            export_fbx(os.path.join(ROOT, "assets", "cars", "Rims", f"{name}_Rim_{label}.fbx"),
                       list(hub.children))
            hub.location = held

    print("[export_tutorial_cars] done (blend not saved; hand edits untouched)")


if __name__ == "__main__":
    main()
