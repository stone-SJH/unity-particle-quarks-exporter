# Unity ParticleSystem to Three.Quarks Exporter

[English](README.md) | [简体中文](README.zh-CN.md)

Convert supported Unity Shuriken ParticleSystem prefabs to deterministic
Three.Quarks JSON. The package includes an optional companion runtime for
behaviors that stock Quarks cannot represent.

## Prerequisites

### Unity exporter

- Unity `2022.3.52f1` or Unity `6000.3.22f1` (Unity 6.3 LTS).
- Built-in Render Pipeline or URP. HDRP is `source-only`.
- A Unity project containing the ParticleSystem prefabs and their referenced
  materials, textures and meshes.

The exporter can write either profile without a browser dependency:

| Export profile | Unity-side requirement | Browser-side requirement |
| --- | --- | --- |
| `stock` | This UPM package | `three@0.185.0`, `three.quarks@0.17.1`, `quarks.core@0.17.1` |
| `extended` (paired) | This UPM package | The same stock packages plus `unity-particle-quarks-runtime@0.3.2` |

Choose `stock` when the manifest has no required companion extension. Choose
`extended` when the manifest requires `unity_particle_paired_semantics@1`; the
paired runtime package must then be available to the browser application.

`unity_particle_paired_semantics@1` is a versioned manifest extension descriptor,
not an additional Unity or npm package. It identifies Unity-specific semantic
metadata emitted alongside the stock Quarks JSON, such as authored renderer
alignment/pivot, particle-head, simulation-speed, texture-sheet, light or
limit-velocity details. `extensionsUsed` means that this metadata may be
present; `extensionsRequired` means that the effect depends on the companion
adapter to apply it. Stock Quarks can still parse the base JSON, but rejects an
effect that declares this extension as required instead of silently claiming
the Unity semantics were preserved.

### Browser runtime

- Three.js `0.185.0` as the application peer dependency.
- `three.quarks@0.17.1` and `quarks.core@0.17.1` for both profiles.
- `unity-particle-quarks-runtime@0.3.2` only for paired/extended
  manifests.
- Node.js `>=18.18.0` only when using the package build/test tooling.

## Components

- **Unity exporter**: reads ParticleSystem modules, materials, textures,
  renderers, trails and sub-emitters, then writes JSON, a manifest and a
  conversion report.
- **Browser runtime**: loads exported JSON with stock `QuarksLoader` or the
  extended companion adapter, with pooling, preload, spawn, update, release
  and telemetry APIs.
- **Compatibility matrix**: lists supported editor and pipeline tuples,
  module behavior, fallbacks and strict/best-effort outcomes in
  [`docs/compatibility-matrix.md`](docs/compatibility-matrix.md).

## Unity exporter

Install the UPM package from `packages/com.yahaha.particle-quarks-exporter`.
Run the batch exporter with:

```text
-executeMethod UnityParticleQuarksExporter.Editor.ParticleQuarksExportBatchmode.RunBatch
-particleQuarksConfig <config.json>
```

Example configuration:

```json
{
  "schemaVersion": "unity_particle_quarks_pipeline.config.v1",
  "outputRoot": "./exports/unity-vfx",
  "mode": "strict",
  "runtimeProfile": "stock",
  "target": "default",
  "sourceRenderPipeline": "current",
  "maxTextureSize": 1024,
  "effects": []
}
```

Use `mode: "strict"` to reject blocking unsupported input. Use
`mode: "best-effort"` only when a named `partial` fallback is acceptable.
`runtimeProfile: "stock"` emits JSON for ordinary Three.Quarks playback;
`runtimeProfile: "extended"` enables the companion adapter when required by
the manifest.

## Browser runtime

The runtime package is `unity-particle-quarks-runtime` and requires
Three.js `0.185.0`. It supports stock and extended profiles:

```ts
import { createVfxRuntime } from 'unity-particle-quarks-runtime';

const runtime = createVfxRuntime({
  scene,
  renderer,
  camera,
  runtimeProfile: 'extended'
});

await runtime.loadManifest('./effects/manifest.json');
await runtime.preload('water-impact');
const handle = runtime.spawn('water-impact');
runtime.update(deltaSeconds);
runtime.release(handle);
```

Use `runtimeProfile: "stock"` when the manifest has no required companion
extension. The extended profile is the default and handles
`unity_particle_paired_semantics@1` metadata.

## Support

The declared editor tuples are Unity `2022.3.52f1` and Unity `6000.3.22f1`
(Unity 6.3 LTS), each with Built-in or URP. HDRP is `source-only`. Browser
runtime requirements are Node.js `>=18.18.0`, Three.js `0.185.0`, and
`three.quarks`/`quarks.core` `0.17.1`. See the
[`compatibility matrix`](docs/compatibility-matrix.md) for module-level
behavior and fallback details.

Every batch run writes a manifest and conversion report below `outputRoot`.
Reports identify the input effect, conversion stage, expected contract,
observed value and next action. `unknown`, `partial`, `unsupported` and
`rejected` remain explicit statuses.

## License

Code is MIT licensed. Third-party dependencies retain their own licenses; see
`NOTICE` and the package notices.
