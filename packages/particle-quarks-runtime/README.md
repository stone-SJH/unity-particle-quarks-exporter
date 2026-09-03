# unity-particle-quarks-runtime

Generic Three.js/Quarks runtime for Unity ParticleSystem JSON exported by the
Unity ParticleSystem to Quarks exporter. It uses `three.quarks` and
`quarks.core` `0.17.1`.

## Prerequisites

Both profiles require Three.js `0.185.0`, `three.quarks@0.17.1` and
`quarks.core@0.17.1`. Node.js `>=18.18.0` is required only for package
build/test tooling. The exporter input must come from Unity `2022.3.52f1` or
Unity 6.3 `6000.3.22f1` on Built-in or URP; HDRP is `source-only`.

### Stock profile

Use a manifest with `runtimeProfile: "stock"` and no required companion
extension. Install the stock Three.Quarks packages listed above. Do not add
the companion runtime package.

### Paired profile

Use `runtimeProfile: "extended"` (the default) when the manifest requires
`unity_particle_paired_semantics@1`. Install
`unity-particle-quarks-runtime@0.3.3` in addition to the stock packages.
The companion adapter is required for the extended behavior; a stock runtime
rejects a manifest that declares this required extension.

### Unity semantics extension

`unity_particle_paired_semantics@1` is a versioned extension descriptor in the
manifest, not another package to install. The id names the Unity-specific
semantics contract and `1` is its contract version. The exporter uses it for
metadata that ordinary Quarks does not interpret on its own, for example
renderer alignment/pivot, particle-head, simulation-speed, texture-sheet,
particle-light and limit-velocity details. An entry in `extensionsUsed` says
the metadata can be present; an entry in `extensionsRequired` makes the
companion adapter a load-time requirement. The stock runtime rejects such an
entry rather than silently reporting exact Unity behavior.

## Runtime profiles

- `stock`: pooling and ordinary Quarks playback only. Exporter extension
  metadata remains inert.
- `extended`: the default. It also applies the versioned
  `unity_particle_paired_semantics@1` adapter when an effect declares that
  extension.

The effect manifest is the capability-negotiation boundary. A stock runtime
rejects an effect whose `extensionsRequired` includes the Unity semantics
extension. An effect exported with `runtimeProfile: "stock"` stays on stock
behavior even when loaded by an extended runtime.

```ts
import { createVfxRuntime } from 'unity-particle-quarks-runtime';

const runtime = createVfxRuntime({
  scene,
  renderer,
  camera,
  runtimeProfile: 'stock'
});
```

Omitting `runtimeProfile` keeps the compatibility default `extended`.

Install the published package with:

```sh
npm install unity-particle-quarks-runtime@0.3.3 three@0.185.0
```

If the registry release is not available yet, create a tarball from the
repository root with `npm pack -w unity-particle-quarks-runtime` and install
the resulting `unity-particle-quarks-runtime-0.3.3.tgz` in the application.

## Manifest

Use `unity_particle_quarks_runtime.manifest.v1` for new manifests.
The Unity exporter writes this contract as `runtime-manifest.json`; its
separate `manifest.json` is a pipeline diagnostics file and is intentionally
rejected by `runtime.loadManifest()`.

```json
{
  "schemaVersion": "unity_particle_quarks_runtime.manifest.v1",
  "effects": [
    {
      "id": "water-impact",
      "status": "ready",
      "runtimeProfile": "extended",
      "runtimeTier": "paired",
      "extensionsUsed": [
        { "id": "unity_particle_paired_semantics", "version": "1" }
      ],
      "extensionsRequired": [
        { "id": "unity_particle_paired_semantics", "version": "1" }
      ],
      "url": "./water-impact/effect.quarks.json"
    }
  ]
}
```

`runtimeTier` is a compatibility summary. Extension descriptors are
authoritative for dependency negotiation.

## API

```ts
runtime.loadManifest(url)
runtime.preload(effectId, variant?)
runtime.spawn(effectId, options?)
runtime.emit(event)
runtime.update(delta)
runtime.release(handle)
runtime.getTelemetry()
runtime.dispose()
```

One runtime owns one shared `BatchedRenderer` per scene. Pooling is bounded;
partial effects remain opt-in through `allowPartial`; VFX does not own gameplay
or physics outcomes.
