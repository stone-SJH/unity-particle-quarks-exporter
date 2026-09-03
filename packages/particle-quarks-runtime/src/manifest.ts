import type {
  VfxExtensionDescriptor,
  VfxManifest,
  VfxManifestEffect,
  VfxRuntimeProfile
} from './types.js';

const EFFECT_ID = /^[a-z0-9][a-z0-9-]*$/;
const EXTENSION_ID = /^[A-Za-z][A-Za-z0-9_.-]*$/;
const RUNTIME_MANIFEST_SCHEMAS = new Set([
  'unity_particle_quarks_runtime.manifest.v1'
]);

export const UNITY_PARTICLE_PAIRED_SEMANTICS_EXTENSION: Readonly<VfxExtensionDescriptor> = Object.freeze({
  id: 'unity_particle_paired_semantics',
  version: '1'
});

export function validateVfxManifest(value: unknown): VfxManifest {
  if (isRecord(value) && value.schemaVersion === 'unity_particle_quarks_pipeline.manifest.v1') {
    throw new Error(
      'VFX pipeline manifest is not runtime-loadable. Load the generated runtime-manifest.json instead.'
    );
  }
  if (!isRecord(value) || typeof value.schemaVersion !== 'string' ||
      !RUNTIME_MANIFEST_SCHEMAS.has(value.schemaVersion) || !Array.isArray(value.effects)) {
    throw new Error(
      'VFX manifest must use unity_particle_quarks_runtime.manifest.v1 and contain effects.'
    );
  }
  const ids = new Set<string>();
  const effects = value.effects.map((entry, index) => validateEffect(entry, index, ids));
  return { schemaVersion: value.schemaVersion as VfxManifest['schemaVersion'], effects };
}

export function resolveManifestAsset(baseUrl: string, relativePath: string, field: string): string {
  validateRelativePath(relativePath, field);
  return new URL(relativePath, baseUrl).href;
}

function validateEffect(value: unknown, index: number, ids: Set<string>): VfxManifestEffect {
  if (!isRecord(value) || typeof value.id !== 'string' || !EFFECT_ID.test(value.id) || ids.has(value.id)) {
    throw new Error(`VFX manifest effect ${index} has an invalid or duplicate id.`);
  }
  ids.add(value.id);
  if (value.status !== 'ready' && value.status !== 'partial' && value.status !== 'failed') {
    throw new Error(`VFX manifest effect ${value.id} has an invalid status.`);
  }
  // Manifests without runtimeTier default to paired, while stock profiles can
  // omit the compatibility summary.
  let runtimeTier: 'stock' | 'paired' = value.runtimeProfile === 'stock' ? 'stock' : 'paired';
  if (value.runtimeTier !== undefined) {
    if (value.runtimeTier !== 'stock' && value.runtimeTier !== 'paired') {
      throw new Error(`VFX manifest effect ${value.id} has an invalid runtimeTier.`);
    }
    runtimeTier = value.runtimeTier;
  }
  const runtimeProfile = validateRuntimeProfile(value.runtimeProfile, runtimeTier, value.id);
  const defaultExtensions = runtimeTier === 'paired' ? [UNITY_PARTICLE_PAIRED_SEMANTICS_EXTENSION] : [];
  const extensionsUsed = validateExtensions(
    value.extensionsUsed,
    `${value.id}.extensionsUsed`,
    defaultExtensions
  );
  const extensionsRequired = validateExtensions(
    value.extensionsRequired,
    `${value.id}.extensionsRequired`,
    runtimeTier === 'paired' ? defaultExtensions : []
  );
  const usedKeys = new Set(extensionsUsed.map(extensionKey));
  if (extensionsRequired.some((extension) => !usedKeys.has(extensionKey(extension)))) {
    throw new Error(`VFX manifest effect ${value.id} requires an extension it does not list in extensionsUsed.`);
  }
  if (runtimeProfile === 'stock' && extensionsRequired.length > 0) {
    throw new Error(`VFX manifest effect ${value.id} uses stock profile but requires runtime extensions.`);
  }
  if (runtimeProfile === 'stock' && runtimeTier !== 'stock') {
    throw new Error(`VFX manifest effect ${value.id} uses stock profile but declares paired runtimeTier.`);
  }
  const result: VfxManifestEffect = {
    id: value.id,
    status: value.status,
    runtimeProfile,
    runtimeTier,
    extensionsUsed,
    extensionsRequired
  };
  if (value.url !== undefined) {
    if (typeof value.url !== 'string') throw new Error(`VFX manifest effect ${value.id} url must be a string.`);
    validateRelativePath(value.url, `${value.id}.url`);
    result.url = value.url;
  }
  if (value.fallbackUrl !== undefined) {
    if (typeof value.fallbackUrl !== 'string') throw new Error(`VFX manifest effect ${value.id} fallbackUrl must be a string.`);
    validateRelativePath(value.fallbackUrl, `${value.id}.fallbackUrl`);
    result.fallbackUrl = value.fallbackUrl;
  }
  if (value.conversionReport !== undefined) {
    if (typeof value.conversionReport !== 'string') throw new Error(`VFX manifest effect ${value.id} conversionReport must be a string.`);
    validateRelativePath(value.conversionReport, `${value.id}.conversionReport`);
    result.conversionReport = value.conversionReport;
  }
  if ((result.status === 'ready' || result.status === 'partial') && !result.url && !result.fallbackUrl) {
    throw new Error(`VFX manifest effect ${value.id} has no loadable URL.`);
  }
  if (result.status === 'failed' && !result.fallbackUrl) {
    throw new Error(`Failed VFX manifest effect ${value.id} requires fallbackUrl.`);
  }
  return result;
}

function validateRuntimeProfile(
  value: unknown,
  runtimeTier: 'stock' | 'paired',
  effectId: string
): VfxRuntimeProfile {
  if (value === undefined) return runtimeTier === 'stock' ? 'stock' : 'extended';
  if (value !== 'stock' && value !== 'extended') {
    throw new Error(`VFX manifest effect ${effectId} has an invalid runtimeProfile.`);
  }
  return value;
}

function validateExtensions(
  value: unknown,
  field: string,
  fallback: readonly VfxExtensionDescriptor[]
): VfxExtensionDescriptor[] {
  if (value === undefined) return fallback.map((extension) => ({ ...extension }));
  if (!Array.isArray(value)) throw new Error(`VFX manifest ${field} must be an array.`);
  const seen = new Set<string>();
  return value.map((entry, index) => {
    if (!isRecord(entry) || typeof entry.id !== 'string' || !EXTENSION_ID.test(entry.id) ||
        typeof entry.version !== 'string' || entry.version.length === 0) {
      throw new Error(`VFX manifest ${field}[${index}] is invalid.`);
    }
    const descriptor = { id: entry.id, version: entry.version };
    const key = extensionKey(descriptor);
    if (seen.has(key)) throw new Error(`VFX manifest ${field} contains duplicate extension ${key}.`);
    seen.add(key);
    return descriptor;
  });
}

export function extensionKey(extension: VfxExtensionDescriptor): string {
  return `${extension.id}@${extension.version}`;
}

function validateRelativePath(value: string, field: string): void {
  const decoded = decodeURIComponent(value.replaceAll('\\', '/'));
  if (!value || decoded.startsWith('/') || decoded.split('/').includes('..') || /^[a-z][a-z0-9+.-]*:/i.test(decoded)) {
    throw new Error(`VFX manifest ${field} must be a safe relative URL.`);
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
