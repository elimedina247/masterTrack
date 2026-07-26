# Cars

Low-poly car assets, and how they get from Blender into the game.

```
src/*.blend        the sources you edit
Body/*.fbx         one body per variant, instanced under BodyRig
Rims/*.fbx         one wheel per side per variant, instanced under the wheel hubs
car_variants.blend all three side by side, for comparing rather than shipping
```

`tools/car_blockout.py` generated all of it. **It overwrites everything it writes**, so once you
start editing by hand, stop running it — or change `OUTPUT` first.

```bash
"C:/Program Files/Blender Foundation/Blender 5.1/blender.exe" --background --python tools/car_blockout.py
```

## The two axis conventions, and why the files look rotated

Blender is **Z-up**. Godot is **Y-up with −Z forward**. Something has to convert between them, and
the FBX exporter is where that happens.

So in Blender you author a car **nose along +Y**, which feels wrong until you know why: the
exporter's `axis_forward='-Z', axis_up='Y'` turns Blender +Y into Godot −Z, which is Godot's
forward. The car then arrives pointing the right way with an identity transform on the node.

The old CC96 body was *not* authored this way, which is why `BodyModel` in `Racer.tscn` carries a
90° rotation. Bodies from this folder don't need it.

Wheels are the same idea one step further. Their axle is along **Blender +Y**, so it arrives along
**Godot Z** — and then each `RimFL` / `RimFR` / `RimRL` / `RimRR` node in `Racer.tscn` carries
`Transform3D(0, 0, 1, 0, 1, 0, -1, 0, 0, …)`, which takes local Z round to world −X: the axis a
wheel's axle actually belongs on. (The rotation sits on the rim instance itself, not on the
`WheelFLHub` above it — the hubs are identity, and `Wheel.cs` drives their Y position for the
suspension travel.) That rotation already existed for the CC96 rims, so authoring to the same
convention means these drop straight in.

**Left and right are separate files** because all four rim nodes carry the *same* rotation, so the
mirroring has to already be in the asset. The only difference between `_Rim_L` and `_Rim_R` is
which side the rim face is set into: `_Rim_L` has it toward local +Z, which that rotation sends to
world −X — outward on the left of the car.

## Exporting by hand

The script does this, but the same four settings matter if you export from the Blender UI —
**File → Export → FBX**:

| Setting | Value | Why |
| --- | --- | --- |
| Limit to **Selected Objects** | on | Otherwise you export the whole scene, wheels and all |
| **Object Types** | Mesh only | Leaves out the empties used as parents, which would arrive as dead nodes |
| **Geometry → Smoothing** | **Face** | Writes per-face smoothing. Set this to *Normals Only* or *Off* and Godot smooths the normals on import, which quietly undoes the flat-shaded look the whole game is built on |
| **Transform → Forward / Up** | −Z Forward, Y Up | The Blender-to-Godot conversion. These are the defaults, but check them |
| **Bake Animation** | off | There is none; leaving it on writes an empty take |

Export the body with only the body objects selected, and each wheel with only that wheel selected
and **moved to the origin first** — an asset carrying its own position gets offset twice, once by
the mesh and once by the hub it is parented to.

## Hooking a variant into the rig

In `scenes/Racer.tscn`:

1. **`BodyRig/BodyModel`** — swap the instanced scene to `Body/<variant>_Body.fbx`, then clear the
   transform to identity. The existing `Transform3D(-4.37e-08, 0, 1, …, 0, 0.2, -0.1)` is the CC96
   rotation fudge and is wrong for these.
2. **`WheelFL/WheelFLHub/RimFL`** and **`WheelRL/…/RimRL`** — swap to `Rims/<variant>_Rim_L.fbx`.
3. **`WheelFR/…/RimFR`** and **`WheelRR/…/RimRR`** — swap to `Rims/<variant>_Rim_R.fbx`.
4. **Match the tyre radius.** `Wheel.cs` positions and rotates the hub but never scales it, so the
   rim renders at whatever size it was modelled at while the physics uses `FrontTireRadius` /
   `RearTireRadius` on the `Racer` node. If they disagree the wheel looks sunk into the road or
   floating above it. The variants were modelled at:

   | Variant | Front | Rear |
   | --- | --- | --- |
   | A_Wedge | 0.28 | 0.28 |
   | B_Bubble | 0.30 | 0.30 |
   | C_Cartoon | 0.24 | **0.36** |

   The rig currently has both at **0.24**. C's staggered rake is a real change to the physics, not
   just a look.

Nothing else needs doing: `FlatShade.cs` walks whatever model is under the racer and rebuilds its
materials, keeping each surface's own colour and replacing only how light lands on it.

## If something arrives wrong

- **Car lies on its side or faces sideways** — the export's Forward/Up were not −Z / Y, or the
  node still carries the old CC96 rotation.
- **Wheels spin about the wrong axis** — the axle was authored along the wrong Blender axis. It
  wants to be +Y.
- **Rim face points inward** — `_Rim_L` and `_Rim_R` are the wrong way round. Swap them; it costs
  nothing and is the most likely thing to be back to front.
- **The car looks smooth and shiny** — Smoothing was not set to Face on export.
