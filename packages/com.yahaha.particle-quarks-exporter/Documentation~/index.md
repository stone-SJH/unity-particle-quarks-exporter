# Unity ParticleSystem To Quarks Exporter

This package exports Unity ParticleSystem prefabs to deterministic Three
Object3D JSON that stock `three.quarks` `0.17.1` can parse.

Install the UPM package with technical id
`com.yahaha.particle-quarks-exporter`.

## Prerequisites

The Unity exporter runs in Unity `2022.3.52f1` or Unity `6000.3.22f1` with
Built-in or URP. The input project must contain the ParticleSystem prefab and
its referenced materials, textures and meshes. HDRP is `source-only`.

The `stock` profile writes JSON for `three.quarks@0.17.1` and
`quarks.core@0.17.1`; no companion package is needed in the browser. The
`extended` profile writes paired metadata and requires
`unity-particle-quarks-runtime@0.3.4` in the browser when the manifest
requires `unity_particle_paired_semantics@1`. Both profiles use Three.js
`>=0.182.0 <0.186.0`.

`unity_particle_paired_semantics@1` is a versioned manifest extension descriptor,
not a separate Unity or browser dependency. It marks Unity-specific semantic
metadata that the companion adapter understands. `extensionsUsed` records
that the metadata may be present; `extensionsRequired` means the effect must
be loaded with the extended companion runtime. Stock Quarks rejects a required
extension instead of silently claiming that all Unity semantics were retained.

## Profiles

- `runtimeProfile: "stock"` requires ordinary Three/Quarks only. Versioned
  exporter metadata may remain in `userData`, but it is optional and inert.
- `runtimeProfile: "extended"` requires
  `unity-particle-quarks-runtime` when the manifest lists
  `unity_particle_paired_semantics@1` in `extensionsRequired`.

Both profiles use the same stock-loadable JSON and the same strict diagnostics.
The profile does not change omission/failure handling. Configs without the
field default to `extended`.

## Batch export

New integrations use:

```text
-executeMethod UnityParticleQuarksExporter.Editor.ParticleQuarksExportBatchmode.RunBatch
-particleQuarksConfig <config.json>
```

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

The diagnostic output schemas are `unity_particle_quarks_pipeline.manifest.v1`
and `unity_particle_quarks_conversion.report.v1`. A fully publishable batch
also writes `runtime-manifest.json` with schema
`unity_particle_quarks_runtime.manifest.v1`; pass that file directly to
`runtime.loadManifest()`. Each effect records
`runtimeProfile`, the v1 `runtimeTier` compatibility summary,
`extensionsUsed`, and `extensionsRequired`.

The pipeline manifest can retain `failed`, `profile_required`, and
`review_only` diagnostics. If any such entry exists, no runtime manifest is
published. A `partial` entry is runtime-loadable only when the browser runtime
is created with `allowPartial: true`.

## Conversion policy

`mode` and `target` are independent from `runtimeProfile`:

- strict rejects active blocking unsupported fields;
- best-effort may publish a named `partial` fallback;
- the default target keeps ParticleSystem Collision/Trigger fatal;
- `presentation` may intentionally omit those physics behaviors and publish
  `partial` for presentation-only use.

Missing/invalid shaders, no exportable emitter, invalid output, and other
playback-blocking conditions remain failed. Disabled/default-zero modules are
inactive, not failures. Nothing unsupported is silently dropped.

## Output and compatibility

Every batch run writes a manifest and conversion report below `outputRoot`.
Failure entries identify the effect, conversion stage, expected contract,
observed value and next action. `unknown`, `partial`, `unsupported` and
`rejected` remain explicit statuses; strict mode rejects blocking unsupported
input and best-effort mode may emit a named partial fallback.

Support is keyed by the Unity editor and render-pipeline tuple, ParticleSystem
module behavior, material/texture semantics, renderer alignment, sub-emitter
and trail behavior, runtime profile, diagnostics and reproducibility. See the
[`compatibility matrix`](../../../docs/compatibility-matrix.md) for the full
module-level contract.
