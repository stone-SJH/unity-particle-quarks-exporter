# Unity ParticleSystem to Three.Quarks Exporter

This UPM package converts Unity ParticleSystem prefabs into deterministic,
stock-loadable Three Object3D JSON, a manifest and a conversion report.

## Prerequisites

- Unity `2022.3.52f1` or Unity 6.3 `6000.3.22f1`.
- Built-in Render Pipeline or URP. HDRP is `source-only`.
- A Unity project containing the ParticleSystem prefab and referenced source
  materials, textures and meshes.

The exporter package itself has no browser runtime dependency. Select
`runtimeProfile: "stock"` for manifests consumed by stock Quarks. Select
`runtimeProfile: "extended"` for paired manifests; the browser application
must additionally install `unity-particle-quarks-runtime@0.3.2` and load
the `unity_particle_paired_semantics@1` extension.

The browser contract is Three.js `0.185.0`, `three.quarks`/`quarks.core`
`0.17.1`, and Node.js `>=18.18.0` for runtime tooling.

See [`Documentation~/index.md`](Documentation~/index.md) for batch export and
conversion-report usage. See the
[`compatibility matrix`](../../docs/compatibility-matrix.md) for module-level
support and fallback behavior.
