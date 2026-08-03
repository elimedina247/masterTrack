# Making it look good

The game plays well and looks amateur. This is the ordered plan for fixing that, written after
measuring the actual frame rather than guessing at it — the comparison harness is
`tools/ShotRunner.tscn`, which photographs fixed camera transforms in each piece's own frame so
two runs either side of a change are identically framed.

## What is actually wrong

Three separate problems, and they were worth separating because they have wildly different
costs and the cheapest one is not the one that looks most broken.

**1. The world was barely lit.** Both `Game.tscn` and `TestArea.tscn` carried their own copy of
an inline `Environment` with sky ambient at 1.5, no SSAO, no glow, no adjustments — and a
`DirectionalLight3D` with `shadow_enabled = true` and nothing else, which leaves Godot's default
`directional_shadow_max_distance` of **100 m**. One tile is 54 m long and the Track Master's
camera sits at board altitude, so essentially nothing that mattered ever cast a shadow. Ambient
that high with shadows that short is a scene with no lit side and no shady side, which is most of
what "flat" meant here.

**2. The hazards are not in the road's art system.** The pieces wear
`resources/tiles/road_surface.gdshader` — grey at rest, green for climb, blue for bank, red for
danger, near-black on undriveable faces, two-tone 27 m panels off UV.x. That is a real art
direction. `LaunchPadHazard` and `PopUpRampHazard` are single `CsgBox3D`es painted with
`TrackHazard.HouseMaterial`, a `StandardMaterial3D` in `PerVertex` shading, in colours (amber,
sprung-steel yellow) that are outside the vocabulary. `SentryOilSlick` is a `CylinderMesh` with
an unshaded transparent albedo. They read as foreign objects sitting on a designed surface, and
that mismatch costs more than the box-ness does.

**3. The hazards have no silhouette.** The pop-up ramp is not a wedge, it is a `CsgBox3D`
pitched about X — the comment in `PopUpRampHazard` says so. At board altitude all three
hazards read as flat coloured rectangles painted on the road. At 200 km/h a racer sees a hazard
for well under a second, so silhouette and colour are the entire read and surface detail is
worth nothing.

## What the lighting pass found

Phase 1 below is done. Worth recording what it cost and what it did not fix:

- The environment is now one shared `resources/environment.tres` instead of two inline copies
  that had already drifted apart (only one of them set a tonemap at all).
- The sun's shadow distance was the single biggest win, and it was a one-line default.
- **ACES tonemapping had to be reverted.** It is the right long-term answer, but
  `road_surface.gdshader`'s palette is authored at near-maximum saturation against Linear's hard
  clip: under ACES the slope green goes pale yellow-green and the bank blue crushes. The tonemap
  change and a palette retune are one job, not two. See Phase 4.
- The hazards look **exactly the same** after the pass as before it, which is the cleanest
  possible confirmation that problems 2 and 3 are geometry and material, not lighting.

---

## Phase 1 — Lighting and post *(done)*

- [x] Extract the duplicated inline `Environment` into `resources/environment.tres`; point
      `Game.tscn` and `TestArea.tscn` at it.
- [x] Ambient 1.5 → 0.5, so the sun contributes something.
- [x] Sun: `directional_shadow_max_distance` 100 → 900 m, 4-split PSSM with blended splits,
      energy 1.15, 1° angular distance for a soft edge.
- [x] SSAO at a 2.5 m radius — contact shadow where furniture meets road.
- [x] Glow, low intensity, high threshold.
- [x] Fog 0.0007 → 0.00025. The old density put half a fog over anything a kilometre out.
- [x] Mild contrast and saturation.
- [ ] The `Sun` node is still duplicated between the two scenes, the same way the environment
      was. Extract it alongside the environment so the match and the proving ground cannot
      disagree about the light.

## Phase 2 — Put hazards in the road's art system

No new geometry, no Blender. This is the cheapest remaining win and it is mostly deleting
special cases.

- [ ] Give hazards a shared material that speaks the road's language: flat, `ROUGHNESS 1`,
      `SPECULAR 0`, and the near-black `edge_color` outline on steep faces, so a hazard is
      outlined against the road the way the road is outlined against the world. Either a sibling
      `hazard_surface.gdshader` or `road_surface.gdshader` with the UV features switched off.
- [ ] Recolour to the vocabulary. `docs/tile-tool.md` states the rule — *red marks danger, and
      only danger* — and the pop-up ramp is currently amber and the launch pad yellow, so the two
      most dangerous objects in the game are the two not wearing the danger colour. Keep one
      accent for the gift hazards (boost) so the distinction survives.
- [ ] Replace `TrackHazard.HouseMaterial` with the shared material, so a new hazard cannot
      accidentally opt out.

## Phase 3 — Make hazards scene-backed

`TrackHazard.Create(kind)` currently `new`s a C# class that builds its own geometry in `_Ready`.
That is the "you cannot look at an enum" problem `docs/tile-tool.md` was written to kill,
reintroduced one layer down: there is nothing to open, nothing to drag, and shaping the ramp
means editing a `Vector3` and relaunching.

- [ ] One `.tscn` per `HazardKind` under `scenes/tiles/hazards/`, with the existing C# class on
      the root. The class keeps the behaviour — the rise, the trigger, the impulse — and finds
      its moving parts by node name.
- [ ] `TrackHazard.Create` loads the scene for the kind instead of building boxes. The three
      spawn paths (authored, builder, sentry) are unchanged; they already all go through here.
- [ ] Delete the dead generated-hazard code. `TileCatalog` is authored pieces only now — every
      entry sets `ScenePath` — so `TrackTile.BuildFloor`/`BuildHazard` and most of
      `TrackTile.Hazards.cs` (674 lines) are unreachable. It is the file that *looks* like where
      hazards live, which makes it an expensive decoy to keep.

This phase is what unblocks everything after it, including any Blender work: once a hazard is a
scene, swapping a `CSGPolygon3D` for an imported mesh touches no physics code.

## Phase 4 — The palette and the tonemap, together

The one that needs care, because it moves every colour in the game at once.

- [ ] Bring `road_surface.gdshader`'s palette off the clip ceiling — slope green, bank blue and
      the danger red all sit near maximum saturation, which is why they read as flat sheets with
      no form even when lit.
- [ ] Then switch to ACES and re-tune. Do these as one change, verified with `ShotRunner`.
- [ ] The bank blue is currently close enough to the sky's blue that a hairpin's outer wall
      merges with the horizon. Whatever the retune does, it has to separate those two.

## Phase 5 — Silhouette

Only now is it worth shaping anything, and it should stay stylised. A simple wedge sharing the
road's shading beats a detailed model that does not, and consistency is the thing a solo project
can actually win at.

- [ ] **Pop-up ramp** — a true wedge (`CSGPolygon3D`, triangle profile, swept across the width),
      a hinge line at the low edge, side cheeks, chevrons up the face, a lip at the take-off.
- [ ] **Launch pad** — a recessed frame with a sprung inner plate that visibly punches up on
      trigger. Nothing about the current flat plate says it will throw you.
- [ ] **Oil slick** — a `Decal`, not a mesh. The current `CylinderMesh` floats 12 cm over the
      road and will clip straight through any banked or ramped surface; a decal projects onto
      whatever is actually there. Irregular blob mask instead of a circle, thin iridescent rim.

Blender earns its place here and only here, for shapes that are genuinely sculptural. Timing
stays in code: the ramp's rise is tied to `SentryActions.LeadSeconds` and its collision arms
mid-animation, and baking that into an imported animation would make it harder to tune and
easier to break. **Blender owns shapes; Godot owns timing and anything that spreads, glows or
reacts.**
