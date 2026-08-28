import { describe, expect, it } from 'vitest';
import { resolveManifestAsset, validateVfxManifest } from '../src/manifest.js';

describe('VFX manifest', () => {
  it('accepts a ready effect with an explicit fallback', () => {
    const manifest = validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{
        id: 'water-impact',
        status: 'ready',
        runtimeTier: 'paired',
        url: './local/water-impact.json',
        fallbackUrl: './fallback/water-impact.json'
      }]
    });
    expect(manifest.effects[0]?.id).toBe('water-impact');
    expect(manifest.effects[0]?.runtimeTier).toBe('paired');
    expect(manifest.effects[0]?.runtimeProfile).toBe('extended');
    expect(manifest.effects[0]?.extensionsRequired).toEqual([
      { id: 'unity_particle_paired_semantics', version: '1' }
    ]);
  });

  it('treats legacy manifests without runtimeTier as paired', () => {
    const manifest = validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'legacy-impact', status: 'ready', url: './legacy-impact.json' }]
    });
    expect(manifest.effects[0]?.runtimeTier).toBe('paired');
  });

  it('accepts the neutral schema and an adapter-optional stock profile', () => {
    const manifest = validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{
        id: 'stock-impact',
        status: 'ready',
        runtimeProfile: 'stock',
        runtimeTier: 'stock',
        extensionsUsed: [{ id: 'unity_particle_paired_semantics', version: '1' }],
        extensionsRequired: [],
        url: './stock-impact.json'
      }]
    });

    expect(manifest.schemaVersion).toBe('unity_particle_quarks_runtime.manifest.v1');
    expect(manifest.effects[0]).toMatchObject({
      runtimeProfile: 'stock',
      runtimeTier: 'stock',
      extensionsRequired: []
    });
  });

  it('derives the compatibility tier when a neutral stock effect omits runtimeTier', () => {
    const manifest = validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{
        id: 'stock-without-tier',
        status: 'ready',
        runtimeProfile: 'stock',
        extensionsUsed: [{ id: 'unity_particle_paired_semantics', version: '1' }],
        extensionsRequired: [],
        url: './stock-without-tier.json'
      }]
    });

    expect(manifest.effects[0]?.runtimeTier).toBe('stock');
  });

  it('rejects duplicate ids, failed entries without fallback, and unsafe paths', () => {
    expect(() => validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'same', status: 'ready', url: './a.json' }, { id: 'same', status: 'ready', url: './b.json' }]
    })).toThrow(/duplicate/);
    expect(() => validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'failed', status: 'failed' }]
    })).toThrow(/fallback/);
    expect(() => validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'unsafe', status: 'ready', url: '../outside.json' }]
    })).toThrow(/safe relative/);
    expect(() => validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'wrong-tier', status: 'ready', runtimeTier: 'modified-quarks', url: './a.json' }]
    })).toThrow(/runtimeTier/);
    expect(() => validateVfxManifest({
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{
        id: 'stock-requires-extension',
        status: 'ready',
        runtimeProfile: 'stock',
        runtimeTier: 'stock',
        extensionsUsed: [{ id: 'unity_particle_paired_semantics', version: '1' }],
        extensionsRequired: [{ id: 'unity_particle_paired_semantics', version: '1' }],
        url: './a.json'
      }]
    })).toThrow(/stock profile/);
  });

  it('resolves an asset relative to its manifest', () => {
    expect(resolveManifestAsset('https://example.test/vfx/manifest.json', './impact/effect.json', 'url'))
      .toBe('https://example.test/vfx/impact/effect.json');
  });
});
