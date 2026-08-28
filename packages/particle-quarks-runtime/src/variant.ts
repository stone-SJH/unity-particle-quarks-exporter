import type { VfxVariant } from './types.js';

export interface AppliedVariant<T> {
  json: T;
  hash: string;
  skippedFields: number;
}

export function applyVfxVariant<T>(source: T, variant?: VfxVariant): AppliedVariant<T> {
  const normalized = normalizeVariant(variant);
  const json = deepClone(source);
  let skippedFields = 0;
  if (Object.keys(normalized).length > 0) {
    visitParticleSystems(json, (ps, emitter) => {
      if (normalized.emissionRateMultiplier !== undefined) {
        skippedFields += multiplyScalar(ps.emissionOverTime, normalized.emissionRateMultiplier) ? 0 : 1;
        if (Array.isArray(ps.emissionBursts)) {
          for (const burst of ps.emissionBursts) skippedFields += multiplyScalar(burst?.count, normalized.emissionRateMultiplier) ? 0 : 1;
        }
      }
      if (normalized.lifetimeMultiplier !== undefined) skippedFields += multiplyScalar(ps.startLife, normalized.lifetimeMultiplier) ? 0 : 1;
      if (normalized.speedMultiplier !== undefined) skippedFields += multiplyScalar(ps.startSpeed, normalized.speedMultiplier) ? 0 : 1;
      if (normalized.sizeMultiplier !== undefined) skippedFields += multiplyScalar(ps.startSize, normalized.sizeMultiplier) ? 0 : 1;
      if (normalized.colorMultiplier !== undefined) skippedFields += multiplyColor(ps.startColor, normalized.colorMultiplier) ? 0 : 1;
      if (normalized.looping !== undefined) ps.looping = normalized.looping;
      skippedFields += applyLimitVelocityVariant(emitter, normalized);
    });
  }
  return { json, hash: hashCanonical(normalized), skippedFields };
}

export function normalizeVariant(variant?: VfxVariant): VfxVariant {
  if (!variant) return {};
  const result: VfxVariant = {};
  if (variant.emissionRateMultiplier !== undefined) result.emissionRateMultiplier = scalar(variant.emissionRateMultiplier, 'emissionRateMultiplier');
  if (variant.lifetimeMultiplier !== undefined) result.lifetimeMultiplier = scalar(variant.lifetimeMultiplier, 'lifetimeMultiplier');
  if (variant.speedMultiplier !== undefined) result.speedMultiplier = scalar(variant.speedMultiplier, 'speedMultiplier');
  if (variant.sizeMultiplier !== undefined) result.sizeMultiplier = scalar(variant.sizeMultiplier, 'sizeMultiplier');
  if (variant.limitVelocityDragMultiplier !== undefined) {
    result.limitVelocityDragMultiplier = scalar(variant.limitVelocityDragMultiplier, 'limitVelocityDragMultiplier');
  }
  if (variant.limitVelocityMultiplyDragByParticleSize !== undefined) {
    result.limitVelocityMultiplyDragByParticleSize = Boolean(variant.limitVelocityMultiplyDragByParticleSize);
  }
  if (variant.limitVelocityMultiplyDragByParticleVelocity !== undefined) {
    result.limitVelocityMultiplyDragByParticleVelocity = Boolean(variant.limitVelocityMultiplyDragByParticleVelocity);
  }
  if (variant.colorMultiplier !== undefined) {
    if (variant.colorMultiplier.length !== 4 || variant.colorMultiplier.some((value) => !Number.isFinite(value))) {
      throw new Error('VFX variant colorMultiplier must contain four finite values.');
    }
    result.colorMultiplier = variant.colorMultiplier.map((value) => clamp(value, 0, 1)) as [number, number, number, number];
  }
  if (variant.looping !== undefined) result.looping = Boolean(variant.looping);
  return result;
}

export function variantHash(variant?: VfxVariant): string {
  return hashCanonical(normalizeVariant(variant));
}

function visitParticleSystems(value: unknown, visit: (ps: Record<string, any>, emitter: Record<string, any>) => void): void {
  if (!isRecord(value)) return;
  if (value.type === 'ParticleEmitter' && isRecord(value.ps)) visit(value.ps, value);
  if (isRecord(value.object)) visitParticleSystems(value.object, visit);
  if (Array.isArray(value.children)) for (const child of value.children) visitParticleSystems(child, visit);
}

function applyLimitVelocityVariant(emitter: Record<string, any>, variant: VfxVariant): number {
  const hasDragVariant = variant.limitVelocityDragMultiplier !== undefined ||
    variant.limitVelocityMultiplyDragByParticleSize !== undefined ||
    variant.limitVelocityMultiplyDragByParticleVelocity !== undefined;
  if (!hasDragVariant) return 0;
  const userData = isRecord(emitter.userData) ? emitter.userData : undefined;
  const exporterData = userData && (isRecord(userData.unityParticleQuarks)
    ? userData.unityParticleQuarks
    : isRecord(userData.unityParticleQuarks) ? userData.unityParticleQuarks : undefined);
  const metadata = exporterData && isRecord(exporterData.limitVelocityOverLifetime)
    ? exporterData.limitVelocityOverLifetime
    : undefined;
  if (!metadata) {
    return Number(variant.limitVelocityDragMultiplier !== undefined) +
      Number(variant.limitVelocityMultiplyDragByParticleSize !== undefined) +
      Number(variant.limitVelocityMultiplyDragByParticleVelocity !== undefined);
  }
  let skipped = 0;
  if (variant.limitVelocityDragMultiplier !== undefined) {
    skipped += multiplyUnityCurve(metadata.drag, variant.limitVelocityDragMultiplier) ? 0 : 1;
  }
  if (variant.limitVelocityMultiplyDragByParticleSize !== undefined) {
    metadata.multiplyDragByParticleSize = variant.limitVelocityMultiplyDragByParticleSize;
  }
  if (variant.limitVelocityMultiplyDragByParticleVelocity !== undefined) {
    metadata.multiplyDragByParticleVelocity = variant.limitVelocityMultiplyDragByParticleVelocity;
  }
  return skipped;
}

function multiplyUnityCurve(curve: unknown, multiplier: number): boolean {
  if (!isRecord(curve) || typeof curve.mode !== 'string') return false;
  if (curve.mode === 'twoCurves') {
    return multiplyScalar(curve.minimum, multiplier) && multiplyScalar(curve.maximum, multiplier);
  }
  return multiplyScalar(curve.value, multiplier);
}

function multiplyScalar(generator: unknown, multiplier: number): boolean {
  if (!isRecord(generator) || typeof generator.type !== 'string') return false;
  switch (generator.type) {
    case 'ConstantValue':
      return multiplyNumber(generator, 'value', multiplier);
    case 'IntervalValue':
      return multiplyNumber(generator, 'a', multiplier) && multiplyNumber(generator, 'b', multiplier);
    case 'PiecewiseBezier':
      if (!Array.isArray(generator.functions)) return false;
      return generator.functions.every((entry) => isRecord(entry) && isRecord(entry.function) &&
        ['p0', 'p1', 'p2', 'p3'].every((key) => multiplyNumber(entry.function, key, multiplier)));
    case 'CLinearFunction':
      if (generator.subType !== 'Number' || !Array.isArray(generator.keys)) return false;
      return generator.keys.every((key) => isRecord(key) && multiplyNumber(key, 'value', multiplier));
    case 'Vector3Function':
      return multiplyScalar(generator.x, multiplier) && multiplyScalar(generator.y, multiplier) && multiplyScalar(generator.z, multiplier);
    default:
      return false;
  }
}

function multiplyColor(generator: unknown, multiplier: [number, number, number, number]): boolean {
  if (!isRecord(generator) || typeof generator.type !== 'string') return false;
  switch (generator.type) {
    case 'ConstantColor':
      return colorObject(generator.color, multiplier);
    case 'ColorRange':
      return colorObject(generator.a, multiplier) && colorObject(generator.b, multiplier);
    case 'Gradient':
      return gradientColor(generator, multiplier);
    case 'RandomColorBetweenGradient':
      return multiplyColor(generator.gradient1, multiplier) && multiplyColor(generator.gradient2, multiplier);
    default:
      return false;
  }
}

function gradientColor(generator: Record<string, any>, multiplier: [number, number, number, number]): boolean {
  if (!isRecord(generator.color) || !Array.isArray(generator.color.keys) || !isRecord(generator.alpha) || !Array.isArray(generator.alpha.keys)) return false;
  const colors = generator.color.keys.every((entry: unknown) => isRecord(entry) && colorObject(entry.value, multiplier, false));
  const alphas = generator.alpha.keys.every((entry: unknown) => isRecord(entry) && multiplyNumber(entry, 'value', multiplier[3], 0, 1));
  return colors && alphas;
}

function colorObject(value: unknown, multiplier: [number, number, number, number], includeAlpha = true): boolean {
  if (!isRecord(value)) return false;
  const rgb = multiplyNumber(value, 'r', multiplier[0]) && multiplyNumber(value, 'g', multiplier[1]) && multiplyNumber(value, 'b', multiplier[2]);
  return includeAlpha ? rgb && multiplyNumber(value, 'a', multiplier[3], 0, 1) : rgb;
}

function multiplyNumber(value: Record<string, any>, key: string, multiplier: number, min = -Number.MAX_VALUE, max = Number.MAX_VALUE): boolean {
  if (typeof value[key] !== 'number' || !Number.isFinite(value[key])) return false;
  value[key] = clamp(value[key] * multiplier, min, max);
  return true;
}

function scalar(value: number, field: string): number {
  if (!Number.isFinite(value)) throw new Error(`VFX variant ${field} must be finite.`);
  return clamp(value, 0, 100);
}

function hashCanonical(value: unknown): string {
  const text = canonical(value);
  let hash = 0x811c9dc5;
  for (let index = 0; index < text.length; index += 1) {
    hash ^= text.charCodeAt(index);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, '0');
}

function canonical(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (isRecord(value)) return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  return JSON.stringify(value);
}

function deepClone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
