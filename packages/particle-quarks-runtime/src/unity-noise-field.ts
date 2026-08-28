export type UnityNoiseQuality = 1 | 2 | 3;

export interface UnityNoiseFieldOptions {
  quality: UnityNoiseQuality;
  frequency: number;
  octaveCount: number;
  octaveMultiplier: number;
  octaveScale: number;
  scrollOffset: number;
}

const HASH = [
  151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225,
  140, 36, 103, 30, 69, 142, 8, 99, 37, 240, 21, 10, 23, 190, 6, 148,
  247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32,
  57, 177, 33, 88, 237, 149, 56, 87, 174, 20, 125, 136, 171, 168, 68, 175,
  74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122,
  60, 211, 133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54,
  65, 25, 63, 161, 1, 216, 80, 73, 209, 76, 132, 187, 208, 89, 18, 169,
  200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186, 3, 64,
  52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212,
  207, 206, 59, 227, 47, 16, 58, 17, 182, 189, 28, 42, 223, 183, 170, 213,
  119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9,
  129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232, 178, 185, 112, 104,
  218, 246, 97, 228, 251, 34, 242, 193, 238, 210, 144, 12, 191, 179, 162, 241,
  81, 51, 145, 235, 249, 14, 239, 107, 49, 192, 214, 31, 181, 199, 106, 157,
  184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93,
  222, 114, 67, 29, 24, 72, 243, 141, 128, 195, 78, 66, 215, 61, 156, 180
] as const;

const GRADIENTS_2D: ReadonlyArray<readonly [number, number]> = [
  [1, 0], [-1, 0], [0, 1], [0, -1],
  [Math.SQRT1_2, Math.SQRT1_2], [-Math.SQRT1_2, Math.SQRT1_2],
  [Math.SQRT1_2, -Math.SQRT1_2], [-Math.SQRT1_2, -Math.SQRT1_2]
];

const GRADIENTS_3D: ReadonlyArray<readonly [number, number, number]> = [
  [1, 1, 0], [-1, 1, 0], [1, -1, 0], [-1, -1, 0],
  [1, 0, 1], [-1, 0, 1], [1, 0, -1], [-1, 0, -1],
  [0, 1, 1], [0, -1, 1], [0, 1, -1], [0, -1, -1],
  [1, 1, 0], [-1, 1, 0], [0, -1, 1], [0, -1, -1]
];

function hash(index: number): number {
  return HASH[index & 255] ?? 0;
}

function smooth(t: number): number {
  return t * t * t * (t * (t * 6 - 15) + 10);
}

function smoothDerivative(t: number): number {
  return 30 * t * t * (t * (t - 2) + 1);
}

function gradient1D(index: number): number {
  return (hash(index) & 1) === 0 ? 1 : -1;
}

function gradient2D(index: number): readonly [number, number] {
  return GRADIENTS_2D[hash(index) & 7] ?? GRADIENTS_2D[0]!;
}

function gradient3D(index: number): readonly [number, number, number] {
  return GRADIENTS_3D[hash(index) & 15] ?? GRADIENTS_3D[0]!;
}

function perlin1D(point: readonly [number, number, number], frequency: number): [number, number] {
  const p = point[0] * frequency;
  const floor0 = Math.floor(p);
  const t0 = p - floor0;
  const t1 = t0 - 1;
  const i0 = floor0 & 255;
  const g0 = gradient1D(i0);
  const g1 = gradient1D(i0 + 1);
  const v0 = g0 * t0;
  const v1 = g1 * t1;
  const t = smooth(t0);
  const dt = smoothDerivative(t0);
  const derivative = (g1 - g0) * t + (v1 - v0) * dt + g0;
  return [derivative * frequency * 2, 0];
}

function perlin2D(point: readonly [number, number, number], frequency: number): [number, number] {
  const px = point[0] * frequency;
  const py = point[1] * frequency;
  const floorX = Math.floor(px);
  const floorY = Math.floor(py);
  const tx0 = px - floorX;
  const ty0 = py - floorY;
  const tx1 = tx0 - 1;
  const ty1 = ty0 - 1;
  const ix0 = floorX & 255;
  const iy0 = floorY & 255;
  const h0 = hash(ix0);
  const h1 = hash(ix0 + 1);
  const g00 = gradient2D(h0 + iy0);
  const g10 = gradient2D(h1 + iy0);
  const g01 = gradient2D(h0 + iy0 + 1);
  const g11 = gradient2D(h1 + iy0 + 1);
  const v00 = g00[0] * tx0 + g00[1] * ty0;
  const v10 = g10[0] * tx1 + g10[1] * ty0;
  const v01 = g01[0] * tx0 + g01[1] * ty1;
  const v11 = g11[0] * tx1 + g11[1] * ty1;
  const tx = smooth(tx0);
  const ty = smooth(ty0);
  const dtx = smoothDerivative(tx0);
  const dty = smoothDerivative(ty0);
  const b = v10 - v00;
  const c = v01 - v00;
  const d = v11 - v01 - v10 + v00;
  const scale = frequency * Math.SQRT2;
  const derivativeX = (g10[0] - g00[0]) * tx +
    ((g01[0] - g00[0]) + (g11[0] - g01[0] - g10[0] + g00[0]) * tx) * ty +
    g00[0] + (ty * d + b) * dtx;
  const derivativeY = (g10[1] - g00[1]) * tx +
    ((g01[1] - g00[1]) + (g11[1] - g01[1] - g10[1] + g00[1]) * tx) * ty +
    g00[1] + (tx * d + c) * dty;
  return [derivativeX * scale, derivativeY * scale];
}

function perlin3D(point: readonly [number, number, number], frequency: number): [number, number] {
  const px = point[0] * frequency;
  const py = point[1] * frequency;
  const pz = point[2] * frequency;
  const floorX = Math.floor(px);
  const floorY = Math.floor(py);
  const floorZ = Math.floor(pz);
  const tx0 = px - floorX;
  const ty0 = py - floorY;
  const tz0 = pz - floorZ;
  const tx1 = tx0 - 1;
  const ty1 = ty0 - 1;
  const tz1 = tz0 - 1;
  const ix0 = floorX & 255;
  const iy0 = floorY & 255;
  const iz0 = floorZ & 255;
  const h0 = hash(ix0);
  const h1 = hash(ix0 + 1);
  const h00 = hash(h0 + iy0);
  const h10 = hash(h1 + iy0);
  const h01 = hash(h0 + iy0 + 1);
  const h11 = hash(h1 + iy0 + 1);
  const g000 = gradient3D(h00 + iz0);
  const g100 = gradient3D(h10 + iz0);
  const g010 = gradient3D(h01 + iz0);
  const g110 = gradient3D(h11 + iz0);
  const g001 = gradient3D(h00 + iz0 + 1);
  const g101 = gradient3D(h10 + iz0 + 1);
  const g011 = gradient3D(h01 + iz0 + 1);
  const g111 = gradient3D(h11 + iz0 + 1);
  const dot = (gradient: readonly [number, number, number], x: number, y: number, z: number) =>
    gradient[0] * x + gradient[1] * y + gradient[2] * z;
  const v000 = dot(g000, tx0, ty0, tz0);
  const v100 = dot(g100, tx1, ty0, tz0);
  const v010 = dot(g010, tx0, ty1, tz0);
  const v110 = dot(g110, tx1, ty1, tz0);
  const v001 = dot(g001, tx0, ty0, tz1);
  const v101 = dot(g101, tx1, ty0, tz1);
  const v011 = dot(g011, tx0, ty1, tz1);
  const v111 = dot(g111, tx1, ty1, tz1);
  const tx = smooth(tx0);
  const ty = smooth(ty0);
  const tz = smooth(tz0);
  const dtx = smoothDerivative(tx0);
  const dty = smoothDerivative(ty0);
  const b = v100 - v000;
  const c = v010 - v000;
  const e = v110 - v010 - v100 + v000;
  const f = v101 - v001 - v100 + v000;
  const g = v011 - v001 - v010 + v000;
  const h = v111 - v011 - v101 + v001 - v110 + v010 + v100 - v000;
  const derivative = (axis: 0 | 1): number => {
    const da = g000[axis];
    const db = g100[axis] - da;
    const dc = g010[axis] - da;
    const dd = g001[axis] - da;
    const de = g110[axis] - g010[axis] - g100[axis] + da;
    const df = g101[axis] - g001[axis] - g100[axis] + da;
    const dg = g011[axis] - g001[axis] - g010[axis] + da;
    const dh = g111[axis] - g011[axis] - g101[axis] + g001[axis] -
      g110[axis] + g010[axis] + g100[axis] - da;
    return (((dh * tx + dg) * ty + (df * tx + dd)) * tz) +
      ((de * tx + dc) * ty + (db * tx + da));
  };
  const derivativeX = derivative(0) + (((h * ty + f) * tz + (e * ty + b)) * dtx);
  const derivativeY = derivative(1) + (((h * tx + g) * tz + (e * tx + c)) * dty);
  return [derivativeX * frequency, derivativeY * frequency];
}

function perlinDerivative(
  point: readonly [number, number, number],
  quality: UnityNoiseQuality,
  frequency: number
): [number, number] {
  if (quality === 1) return perlin1D(point, frequency);
  if (quality === 2) return perlin2D(point, frequency);
  return perlin3D(point, frequency);
}

function accumulatedNoise(
  point: readonly [number, number, number],
  options: UnityNoiseFieldOptions
): [number, number] {
  let frequency = options.frequency;
  let amplitude = 1;
  let range = 1;
  const sum = perlinDerivative(point, options.quality, frequency);
  for (let octave = 1; octave < options.octaveCount; octave += 1) {
    frequency *= options.octaveScale;
    amplitude *= options.octaveMultiplier;
    range += amplitude;
    const sample = perlinDerivative(point, options.quality, frequency);
    sum[0] += sample[0] * amplitude;
    sum[1] += sample[1] * amplitude;
  }
  return [sum[0] / range, sum[1] / range];
}

export function sampleUnityCurlNoise(
  position: readonly [number, number, number],
  options: UnityNoiseFieldOptions
): [number, number, number] {
  const withScroll = (point: [number, number, number]): [number, number, number] => {
    if (options.quality === 1) point[0] += options.scrollOffset;
    else if (options.quality === 2) point[1] += options.scrollOffset;
    else point[2] += options.scrollOffset;
    return point;
  };
  const sampleX = accumulatedNoise(withScroll([position[2], position[1], position[0]]), options);
  const sampleY = accumulatedNoise(withScroll([position[0] + 100, position[2], position[1]]), options);
  const sampleZ = accumulatedNoise(withScroll([position[1], position[0] + 100, position[2]]), options);
  return [
    sampleZ[0] - sampleY[1],
    sampleX[0] - sampleZ[1],
    sampleY[0] - sampleX[1]
  ];
}

export function unityRandom3(seed: number): [number, number, number] {
  let x = seed >>> 0;
  let y = (Math.imul(x, 1812433253) + 1) >>> 0;
  let z = (Math.imul(y, 1812433253) + 1) >>> 0;
  let w = (Math.imul(z, 1812433253) + 1) >>> 0;
  const next = (): number => {
    const t = (x ^ (x << 11)) >>> 0;
    x = y;
    y = z;
    z = w;
    w = ((w ^ (w >>> 19)) ^ (t ^ (t >>> 8))) >>> 0;
    return (w & 0x007fffff) / 8388607;
  };
  return [next(), next(), next()];
}
