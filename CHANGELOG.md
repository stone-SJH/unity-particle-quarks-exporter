# Changelog

## 0.3.4 - 2026-09-03

- Expanded the runtime Three.js peer range to `>=0.182.0 <0.186.0`, added
  r182-r185 compatibility CI, and kept r186 behind an explicit breaking-change
  gate for the new `Object3D.dispose()` contract.

## 0.3.3 - 2026-09-03

- Added an exporter-generated `runtime-manifest.json` that can be passed
  directly to the browser runtime while retaining `manifest.json` for pipeline
  diagnostics.
- Added shared manifest contracts, Unity-to-Node lifecycle verification,
  browser HTTP smoke coverage, and clean tarball installation checks.
- Added a staged npm publication workflow and documented source-tarball
  installation when a registry release is unavailable.

## 0.3.2 - 2026-08-27

- Added the Unity 2022.3.52f1 and Unity 6000.3.22f1 Built-in/URP compatibility
  rows and the corresponding module-level behavior matrix.
- Documented stock and extended runtime profiles, conversion diagnostics and
  fallback behavior.
