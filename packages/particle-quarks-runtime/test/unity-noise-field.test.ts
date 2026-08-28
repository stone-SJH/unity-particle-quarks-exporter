import { describe, expect, it } from 'vitest';
import { sampleUnityCurlNoise, unityRandom3 } from '../src/unity-noise-field.js';

describe('Unity Noise spatial field', () => {
  it('keeps Unity xorshift field offsets deterministic', () => {
    expect(unityRandom3(0)).toEqual([
      0.5841396551298684,
      0.5840824346640628,
      0.6736069528588
    ]);
    expect(unityRandom3(123)).toEqual([
      0.5996938466660794,
      0.6256340295832192,
      0.7448789769266816
    ]);
  });

  it.each([
    [1, [-1.3449600000000008, 0.5457958593749997, 1.3449600000000017]],
    [2, [0.1865621900902953, 0.1546144185726246, -0.33652597863536504]],
    [3, [0.8789539024651392, -0.17378482089105707, -0.8586371078491213]]
  ] as const)('matches the fixed Unity source port sample for quality %i', (quality, expected) => {
    const sample = sampleUnityCurlNoise([1.25, -0.75, 2.5], {
      quality,
      frequency: 0.5,
      octaveCount: 1,
      octaveMultiplier: 0.5,
      octaveScale: 2,
      scrollOffset: 0.35
    });
    sample.forEach((value, index) => expect(value).toBeCloseTo(expected[index]!, 12));
  });

  it('normalizes accumulated octaves', () => {
    const sample = sampleUnityCurlNoise([1.25, -0.75, 2.5], {
      quality: 3,
      frequency: 0.5,
      octaveCount: 3,
      octaveMultiplier: 0.4,
      octaveScale: 2.25,
      scrollOffset: 0.35
    });
    const expected = [0.5364676949056957, 0.06009043552951776, -0.8861595780837622];
    sample.forEach((value, index) => expect(value).toBeCloseTo(expected[index]!, 12));
  });
});
