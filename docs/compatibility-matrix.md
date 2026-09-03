# Compatibility Matrix

This is the public support matrix for the exporter and browser runtime. Use it
to check editor and render-pipeline prerequisites, choose the stock or paired
runtime profile, and understand module-level fallbacks. `exact` describes the
declared conversion algorithm, not pixel-for-pixel Unity/Three equivalence.
`partial` and `unsupported` remain explicit and are never promoted to `ready`
silently.

## Version and Pipeline Tuples

| Unity editor | Pipeline | Pipeline package lock | Export/runtime | Browser |
| --- | --- | --- | --- | --- |
| 2022.3.52f1 | Built-in | SRP none | exporter/runtime 0.3.3 | Three 0.185.0, Quarks 0.17.1 |
| 2022.3.52f1 | URP | shadergraph 14.0.11, urp-config 14.0.10 | exporter/runtime 0.3.3 | Three 0.185.0, Quarks 0.17.1 |
| 6000.3.22f1 | Built-in | SRP none | exporter/runtime 0.3.3 | Three 0.185.0, Quarks 0.17.1 |
| 6000.3.22f1 | URP | shadergraph 17.3.0, urp-config 17.0.3 | exporter/runtime 0.3.3 | Three 0.185.0, Quarks 0.17.1 |

HDRP is `source-only` and is not a declared conversion tuple. Node.js
`>=18.18.0` is required for the browser runtime tooling. Unity conversion and
EditMode checks use the declared editor and render-pipeline tuples.

## Canonical Rows

| ID | Unity module / parameter | Stock | Companion | Strict behavior | Best-effort behavior |
| --- | --- | --- | --- | --- | --- |
| `main.startRotation.twoCurves` | Main / startRotation TwoCurves | approx mean | approx mean | fail | partial mean fallback |
| `main.startColor.randomColor` | Main / startColor RandomColor | fallback | exact birth sample | companion required | named stock fallback |
| `main.scaling.shearOrZeroAxis` | Main/Shape / shear or zero axis | corrected basis | corrected basis | fail | partial corrected basis |
| `main.simulationSpeed.nonUnit` | Main / non-unit simulationSpeed | fixed 1 fallback | exact | companion required | stock fixed-speed fallback |
| `main.lifetimeByEmitterSpeed` | Main / lifetimeByEmitterSpeed | omission | approximate | companion required | partial or omission |
| `emission.burst.count.twoCurves` | Emission / burst count TwoCurves | approx mean | approx mean | fail | partial mean/zero fallback |
| `shape.rectangle` | Shape / Rectangle | radial fallback | exact normal/basis | companion required | stock direction fallback |
| `shape.meshMaterialIndex` | Shape / mesh material index | whole-mesh fallback | whole-mesh fallback | fail | partial whole-mesh sample |
| `limitVelocity.separateAxes` | Limit Velocity / separate axes | omission | exact | companion required | partial omission |
| `limitVelocity.dragAndMultipliers` | Limit Velocity / drag/multipliers | omission | exact | companion required | partial omission |
| `sizeBySpeed.twoCurves` | Size by Speed / TwoCurves | approx mean | approx mean | fail | partial mean curve |
| `rotationOverLifetime.twoCurves` | Rotation over Lifetime / TwoCurves | approx mean | approx mean | fail | partial mean curve |
| `rotationBySpeed.meshSpeedIndependentScalar` | Rotation by Speed / mesh scalar | local-Z fallback | exact authored axis | companion required | named stock fallback |
| `textureSheet.singleRow.fixedOrRandom` | Texture Sheet / SingleRow | full-grid fallback | exact row | companion required | named stock fallback |
| `textureSheet.singleRow.meshIndex` | Texture Sheet / SingleRow MeshIndex | row-0 fallback | approximate row-0 | fail for authored index | ready row-0 fallback |
| `textureSheet.timeMode.fpsOrSpeed` | Texture Sheet / FPS or Speed | lifetime fallback | exact phase | companion required | named stock fallback |
| `textureSheet.sprites.singleTexture` | Texture Sheet / single texture atlas | tile fallback | exact sprite geometry | companion required | named stock fallback |
| `textureSheet.sprites.multipleTextures` | Texture Sheet / multiple textures | first-texture fallback | first-texture fallback | fail | partial first texture |
| `lights.randomCurves` | Lights / range-intensity curves | omission | approximate | companion required | partial paired or omission |
| `renderer.mesh.alignment.velocity` | Renderer / mesh velocity alignment | unaligned fallback | exact Local | companion + Local required | named stock fallback |
| `material.alphaAtlas` | Material / alpha atlas mesh | unlit fallback | unlit fallback | fail for lit contract | partial unlit fallback |
| `material.mainTexture.wrapOrDimension` | Material / wrap or dimension | named texture fallback | named texture fallback | fail | partial texture fallback |
| `renderer.mesh.cameraFacing` | Renderer / View or Facing mesh | unaligned fallback | exact Local | companion + Local required | named unaligned fallback |

## Runtime Profiles

- `stock` requires `three@0.185.0`, `three.quarks@0.17.1`, and
  `quarks.core@0.17.1`; companion metadata is inert or uses the row's named
  fallback.
- `extended` adds `unity-particle-quarks-runtime@0.3.3` and negotiates
  `unity_particle_paired_semantics@1` for rows marked `exact_companion_runtime` or
  `approx_companion_runtime`.
- `fatal_fail` rows such as Collision/Trigger are outside the canonical positive
  rows and do not publish a default-target JSON artifact. HDRP remains
  `source-only`.
