import { describe, expect, it } from 'vitest';
import { applyVfxVariant, variantHash } from '../src/variant.js';

const source = {
  object: {
    type: 'Group',
    children: [{
      type: 'ParticleEmitter',
      ps: {
        looping: true,
        emissionOverTime: { type: 'ConstantValue', value: 10 },
        emissionBursts: [{ count: { type: 'IntervalValue', a: 2, b: 4 } }],
        startLife: { type: 'PiecewiseBezier', functions: [{ function: { p0: 1, p1: 2, p2: 3, p3: 4 }, start: 0 }] },
        startSpeed: { type: 'ConstantValue', value: 3 },
        startSize: { type: 'Vector3Function', x: { type: 'ConstantValue', value: 1 }, y: { type: 'ConstantValue', value: 2 }, z: { type: 'ConstantValue', value: 3 } },
        startColor: { type: 'ConstantColor', color: { r: 0.8, g: 0.5, b: 0.25, a: 1 } }
      }
    }]
  }
};

describe('VFX variants', () => {
  it('mutates only known cloned generator fields', () => {
    const applied = applyVfxVariant(source, {
      emissionRateMultiplier: 2,
      lifetimeMultiplier: 0.5,
      speedMultiplier: 3,
      sizeMultiplier: 4,
      colorMultiplier: [0.5, 1, 1, 0.25],
      looping: false
    });
    const ps = (applied.json as typeof source).object.children[0]!.ps;
    expect(ps.emissionOverTime.value).toBe(20);
    expect(ps.emissionBursts[0]!.count).toEqual({ type: 'IntervalValue', a: 4, b: 8 });
    expect(ps.startLife.functions[0]!.function).toEqual({ p0: 0.5, p1: 1, p2: 1.5, p3: 2 });
    expect(ps.startSpeed.value).toBe(9);
    expect(ps.startSize.z.value).toBe(12);
    expect(ps.startColor.color).toEqual({ r: 0.4, g: 0.5, b: 0.25, a: 0.25 });
    expect(ps.looping).toBe(false);
    expect(source.object.children[0]!.ps.emissionOverTime.value).toBe(10);
    expect(applied.skippedFields).toBe(0);
  });

  it('has a canonical hash and clamps public values', () => {
    expect(variantHash({ speedMultiplier: 2, lifetimeMultiplier: 3 }))
      .toBe(variantHash({ lifetimeMultiplier: 3, speedMultiplier: 2 }));
    const applied = applyVfxVariant(source, { speedMultiplier: 200, colorMultiplier: [2, -1, 0.5, 1] });
    const ps = (applied.json as typeof source).object.children[0]!.ps;
    expect(ps.startSpeed.value).toBe(300);
    expect(ps.startColor.color).toEqual({ r: 0.8, g: 0, b: 0.125, a: 1 });
  });

  it('overrides Limit Velocity drag metadata without changing the source object', () => {
    const dragSource = structuredClone(source) as any;
    dragSource.object.children[0].userData = {
      unityParticleQuarks: {
        limitVelocityOverLifetime: {
          drag: { mode: 'constant', value: { type: 'ConstantValue', value: 0.5 } },
          multiplyDragByParticleSize: false,
          multiplyDragByParticleVelocity: false
        }
      }
    };
    const applied = applyVfxVariant(dragSource, {
      limitVelocityDragMultiplier: 3,
      limitVelocityMultiplyDragByParticleSize: true,
      limitVelocityMultiplyDragByParticleVelocity: true
    });
    const metadata = (applied.json as any).object.children[0].userData.unityParticleQuarks.limitVelocityOverLifetime;
    expect(metadata.drag.value.value).toBe(1.5);
    expect(metadata.multiplyDragByParticleSize).toBe(true);
    expect(metadata.multiplyDragByParticleVelocity).toBe(true);
    expect(dragSource.object.children[0].userData.unityParticleQuarks.limitVelocityOverLifetime.drag.value.value).toBe(0.5);
    expect(applied.skippedFields).toBe(0);
  });

  it('preserves source HDR RGB when an identity color variant is applied', () => {
    const hdrSource = structuredClone(source);
    hdrSource.object.children[0]!.ps.startColor.color = { r: 3.44, g: 13.76, b: 1.91, a: 1 };

    const applied = applyVfxVariant(hdrSource, { colorMultiplier: [1, 1, 1, 1] });
    const ps = (applied.json as typeof hdrSource).object.children[0]!.ps;

    expect(ps.startColor.color).toEqual({ r: 3.44, g: 13.76, b: 1.91, a: 1 });
  });

  it('records unknown generators instead of traversing arbitrary numbers', () => {
    const unknown = structuredClone(source) as any;
    unknown.object.children[0].ps.startSpeed = { type: 'VendorGenerator', value: 12 };
    const applied = applyVfxVariant(unknown, { speedMultiplier: 2 });
    expect(applied.json.object.children[0].ps.startSpeed.value).toBe(12);
    expect(applied.skippedFields).toBe(1);
  });
});
