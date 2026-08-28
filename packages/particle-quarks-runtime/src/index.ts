export { createVfxRuntime, VfxNotPreloadedError, VfxRuntimeImpl } from './runtime.js';
export {
  extensionKey,
  resolveManifestAsset,
  UNITY_PARTICLE_PAIRED_SEMANTICS_EXTENSION,
  validateVfxManifest
} from './manifest.js';
export { applyVfxVariant, normalizeVariant, variantHash } from './variant.js';
export type {
  AppliedVariant
} from './variant.js';
export type {
  CreateVfxRuntimeOptions,
  VfxExtensionDescriptor,
  VfxEffectTelemetry,
  VfxEvent,
  VfxHandle,
  VfxManifest,
  VfxManifestEffect,
  VfxOverflowPolicy,
  VfxPoolOptions,
  VfxRuntime,
  VfxRuntimeProfile,
  VfxSpawnOptions,
  VfxTelemetry,
  VfxTransform,
  VfxVariant
} from './types.js';
