# Co-driver callout clips

One voice clip per track piece, played by `RallyCopilot` as the local racer closes in on that
piece. Drop `.wav` (or `.ogg`/`.mp3`) files in this folder — no registration step, the folder is
scanned at startup.

**Naming: the piece's scene file name, matched ignoring case, underscores, hyphens and
spaces.** `hairpinLeft.wav`, `HairpinLeft.wav`, `hairpin_left.wav` and `hairpinleft.wav` all say
`HairpinLeft.tscn`. A piece with no clip still gets its on-screen banner, plus one console
warning naming the file it went looking for.

Current pieces and the file each one looks for:

| Piece scene | Clip file (any casing) |
| --- | --- |
| Bottleneck.tscn | `bottleneck.wav` |
| Corkscrew.tscn | `corkscrew.wav` |
| CurveLeft.tscn | `curveLeft.wav` |
| CurveRight.tscn | `curveRight.wav` |
| HairpinLeft.tscn | `hairpinLeft.wav` |
| HairpinRight.tscn | `hairpinRight.wav` |
| HalfpipeDown.tscn | `halfpipeDown.wav` |
| Jump.tscn | `jump.wav` |
| Khopesh.tscn | `khopesh.wav` |
| RampLarge.tscn | `rampLarge.wav` |
| RampSmall.tscn | `rampSmall.wav` |
| SBend.tscn | `sBend.wav` |
| SHairpin.tscn | `sHairpin.wav` |
| Slalom.tscn | `slalom.wav` |
| SlopeLarge.tscn | `slopeLarge.wav` |
| SplitVertical.tscn | `splitVertical.wav` |
| SquareWave.tscn | `squareWave.wav` |
| Straight.tscn | `straight.wav` |
| ToiletBowl.tscn | `toiletBowl.wav` |

Adding a new piece to `scenes/tiles/pieces/` adds its lookup here automatically — the clip name
follows the scene file name, so renaming a piece renames the clip it wants.
