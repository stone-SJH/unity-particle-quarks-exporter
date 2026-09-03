import { Euler, Matrix4, PerspectiveCamera, PointLight, Quaternion, Scene, Texture, Vector3, type WebGLRenderer } from 'three';
import { Quaternion as QuarksQuaternion, Vector4 as QuarksVector4 } from 'quarks.core';
import { describe, expect, it, vi } from 'vitest';
import { createVfxRuntime, VfxNotPreloadedError } from '../src/index.js';
import type { VfxManifest, VfxRuntime, VfxVariant } from '../src/types.js';
import { fixtureJson, subemitterFixtureJson } from './test-inputs.js';

describe('VFX runtime', () => {
  it('prewarms, spawns, drains, releases, and exposes one batch renderer', async () => {
    const runtime = await readyRuntime({ prewarm: 2, max: 3 });
    const handle = runtime.spawn('water-impact', { position: [1, 2, 3], normal: [0, 1, 0] });
    expect(handle.dropped).toBe(false);
    expect(runtime.getTelemetry()).toMatchObject({
      activeInstances: 1,
      activeSystemCount: 1,
      idleInstances: 1,
      allocatedInstances: 2,
      batchCount: 1
    });
    handle.endEmit();
    for (let index = 0; index < 120; index += 1) runtime.update(1 / 60);
    expect(handle.released).toBe(true);
    expect(runtime.getTelemetry()).toMatchObject({ released: 1, activeSystemCount: 0 });
    runtime.dispose();
    expect(runtime.getTelemetry().batchCount).toBe(0);
  });

  it('drops newest at a bounded maximum and keeps telemetry explicit', async () => {
    const runtime = await readyRuntime({ prewarm: 1, max: 1 });
    const first = runtime.spawn('water-impact');
    const second = runtime.spawn('water-impact');
    expect(first.dropped).toBe(false);
    expect(second.dropped).toBe(true);
    expect(runtime.getTelemetry()).toMatchObject({ allocatedInstances: 1, dropped: 1 });
    first.release();
    first.release();
    expect(runtime.getTelemetry().released).toBe(1);
    runtime.dispose();
  });

  it('reuses the oldest instance when configured', async () => {
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'reuse-oldest');
    const first = runtime.spawn('water-impact');
    const second = runtime.spawn('water-impact');
    expect(first.released).toBe(true);
    expect(second.dropped).toBe(false);
    expect(runtime.getTelemetry().reused).toBe(1);
    runtime.dispose();
  });

  it('requires the exact variant to be preloaded', async () => {
    const variant: VfxVariant = { sizeMultiplier: 2, colorMultiplier: [1, 0.5, 0.5, 1] };
    const runtime = await readyRuntime({ prewarm: 0, max: 2 }, 'drop-newest', variant);
    expect(() => runtime.spawn('water-impact')).toThrow(VfxNotPreloadedError);
    const handle = runtime.spawn('water-impact', { variant });
    expect(handle.dropped).toBe(false);
    runtime.dispose();
  });

  it('loads the synthetic fallback after a primary failure', async () => {
    const scene = new Scene();
    const manifest: VfxManifest = {
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'water-impact', status: 'ready', url: './missing.json', fallbackUrl: './fallback.json' }]
    };
    const fetcher = mapFetch(new Map([
      ['http://test/manifest.json', jsonResponse(manifest)],
      ['http://test/missing.json', new Response('missing', { status: 404 })],
      ['http://test/fallback.json', jsonResponse(fixtureJson)]
    ]));
    const runtime = createVfxRuntime({ scene, renderer: {} as WebGLRenderer, camera: new PerspectiveCamera(), fetch: fetcher });
    await runtime.loadManifest('http://test/manifest.json');
    await runtime.preload('water-impact');
    expect(runtime.getTelemetry()).toMatchObject({ fallbackLoads: 1, loadFailures: 1 });
    expect(Object.values(runtime.getTelemetry().effects)[0]?.source).toBe('synthetic-fallback');
    runtime.dispose();
  });

  it('enforces one runtime per scene until dispose', () => {
    const scene = new Scene();
    const options = { scene, renderer: {} as WebGLRenderer, camera: new PerspectiveCamera(), fetch: mapFetch(new Map()) };
    const first = createVfxRuntime(options);
    expect(() => createVfxRuntime(options)).toThrow(/only one/);
    first.dispose();
    const second = createVfxRuntime(options);
    second.dispose();
  });

  it('rejects extended effects when the runtime explicitly selects stock profile', async () => {
    const manifest: VfxManifest = {
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'water-impact', status: 'ready', runtimeTier: 'paired', url: './effect.json' }]
    };
    const runtime = createVfxRuntime({
      scene: new Scene(),
      renderer: {} as WebGLRenderer,
      camera: new PerspectiveCamera(),
      runtimeProfile: 'stock',
      fetch: mapFetch(new Map([
        ['http://test/manifest.json', jsonResponse(manifest)]
      ]))
    });

    await expect(runtime.loadManifest('http://test/manifest.json')).rejects.toThrow(
      /requires unsupported extension unity_particle_paired_semantics@1 under stock runtime profile/
    );
    runtime.dispose();
  });

  it('merges a sample manifest after an incomplete licensed manifest without changing asset bases', async () => {
    const scene = new Scene();
    const manifestLicensed: VfxManifest = {
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'licensed-impact', status: 'ready', url: './effect.json' }, {
        id: 'weather-rain', status: 'ready', url: './missing-weather.json'
      }]
    };
    const manifestSample: VfxManifest = {
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'weather-rain', status: 'ready', url: './weather/effect.json' }]
    };
    const fetcher = mapFetch(new Map([
      ['http://test/licensed/manifest.json', jsonResponse(manifestLicensed)],
      ['http://test/licensed/effect.json', jsonResponse(fixtureJson)],
      ['http://test/licensed/missing-weather.json', new Response('missing', { status: 404 })],
      ['http://test/sample/manifest.json', jsonResponse(manifestSample)],
      ['http://test/sample/weather/effect.json', jsonResponse(fixtureJson)]
    ]));
    const runtime = createVfxRuntime({
      scene,
      renderer: {} as WebGLRenderer,
      camera: new PerspectiveCamera(),
      pool: { prewarm: 0, max: 1 },
      fetch: fetcher
    });

    await runtime.loadManifest('http://test/licensed/manifest.json');
    await runtime.preload('licensed-impact');
    await runtime.loadManifest('http://test/sample/manifest.json');
    await runtime.preload('weather-rain');

    const loadedEffects = Object.values(runtime.getTelemetry().effects);
    expect(runtime.getTelemetry()).toMatchObject({ effectsLoaded: 2, manifestLoaded: true });
    expect(loadedEffects.some((effect) => effect.url === 'http://test/sample/weather/effect.json')).toBe(true);
    expect(loadedEffects.some((effect) => effect.url === 'http://test/licensed/effect.json')).toBe(true);
    runtime.dispose();
  });

  it('does not retain effects from a manifest that fails extension negotiation', async () => {
    const rejectedManifest: VfxManifest = {
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [
        {
          id: 'stale-stock',
          status: 'ready',
          runtimeProfile: 'stock',
          runtimeTier: 'stock',
          extensionsUsed: [],
          extensionsRequired: [],
          url: './stale.json'
        },
        {
          id: 'extended-effect',
          status: 'ready',
          runtimeProfile: 'extended',
          runtimeTier: 'paired',
          extensionsUsed: [{ id: 'unity_particle_paired_semantics', version: '1' }],
          extensionsRequired: [{ id: 'unity_particle_paired_semantics', version: '1' }],
          url: './extended.json'
        }
      ]
    };
    const acceptedManifest: VfxManifest = {
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{
        id: 'current-stock',
        status: 'ready',
        runtimeProfile: 'stock',
        runtimeTier: 'stock',
        extensionsUsed: [],
        extensionsRequired: [],
        url: './current.json'
      }]
    };
    const runtime = createVfxRuntime({
      scene: new Scene(),
      renderer: {} as WebGLRenderer,
      camera: new PerspectiveCamera(),
      runtimeProfile: 'stock',
      fetch: mapFetch(new Map([
        ['http://test/rejected.json', jsonResponse(rejectedManifest)],
        ['http://test/accepted.json', jsonResponse(acceptedManifest)]
      ]))
    });

    await expect(runtime.loadManifest('http://test/rejected.json')).rejects.toThrow(/unsupported extension/);
    await runtime.loadManifest('http://test/accepted.json');
    await expect(runtime.preload('stale-stock')).rejects.toThrow(/does not define effect/);
    runtime.dispose();
  });

  it('keeps stock effects on stock behavior even when the extended adapter is available', async () => {
    for (const runtimeProfile of ['stock', 'extended'] as const) {
      const source = exporterFixture();
      source.metadata.generator = 'UnityParticleQuarksExporter';
      const emitter = source.object.children[0];
      emitter.userData.unityParticleQuarks.particleCapacity = {
        schemaVersion: 'unity_particle_quarks_exporter.particle_capacity.v1',
        maxParticles: 1
      };
      emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 3 };
      const manifest: VfxManifest = {
        schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
        effects: [{
          id: 'water-impact',
          status: 'ready',
          runtimeProfile: 'stock',
          runtimeTier: 'stock',
          extensionsUsed: [{ id: 'unity_particle_paired_semantics', version: '1' }],
          extensionsRequired: [],
          url: './effect.json'
        }]
      };
      const runtime = createVfxRuntime({
        scene: new Scene(),
        renderer: {} as WebGLRenderer,
        camera: new PerspectiveCamera(),
        runtimeProfile,
        pool: { prewarm: 0, max: 1 },
        fetch: mapFetch(new Map([
          ['http://test/manifest.json', jsonResponse(manifest)],
          ['http://test/effect.json', jsonResponse(source)]
        ]))
      });

      await runtime.loadManifest('http://test/manifest.json');
      await runtime.preload('water-impact');
      const handle = runtime.spawn('water-impact') as any;
      runtime.update(0.01);
      expect(handle.instance?.systems[0]?.particleNum).toBe(3);
      expect(runtime.getTelemetry()).toMatchObject({ runtimeProfile });
      handle.release();
      runtime.dispose();
    }
  });

  it('applies extended semantics from canonical userData', async () => {
    const source = exporterFixture();
    source.metadata.generator = 'Object3D.toJSON';
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.particleCapacity = {
      schemaVersion: 'unity_particle_quarks_exporter.particle_capacity.v1',
      maxParticles: 1
    };
    emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 3 };
    const manifest: VfxManifest = {
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{
        id: 'water-impact',
        status: 'ready',
        runtimeProfile: 'extended',
        runtimeTier: 'paired',
        extensionsUsed: [{ id: 'unity_particle_paired_semantics', version: '1' }],
        extensionsRequired: [{ id: 'unity_particle_paired_semantics', version: '1' }],
        url: './effect.json'
      }]
    };
    const runtime = createVfxRuntime({
      scene: new Scene(),
      renderer: {} as WebGLRenderer,
      camera: new PerspectiveCamera(),
      pool: { prewarm: 0, max: 1 },
      fetch: mapFetch(new Map([
        ['http://test/manifest.json', jsonResponse(manifest)],
        ['http://test/effect.json', jsonResponse(source)]
      ]))
    });

    await runtime.loadManifest('http://test/manifest.json');
    await runtime.preload('water-impact');
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    expect(handle.instance?.systems[0]?.particleNum).toBe(1);
    expect(runtime.getTelemetry().enabledExtensions).toContain('unity_particle_paired_semantics@1');
    handle.release();
    runtime.dispose();
  });

  it('disables soft-particle sampling explicitly when the host has no depth texture', async () => {
    const softJson = structuredClone(fixtureJson);
    softJson.object.children[0].ps.softParticles = true;
    const manifest: VfxManifest = {
      schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
      effects: [{ id: 'water-impact', status: 'ready', url: './effect.json' }]
    };
    const fetcher = mapFetch(new Map([
      ['http://test/manifest.json', jsonResponse(manifest)],
      ['http://test/effect.json', jsonResponse(softJson)]
    ]));
    const runtime = createVfxRuntime({ scene: new Scene(), renderer: {} as WebGLRenderer, camera: new PerspectiveCamera(), fetch: fetcher });
    await runtime.loadManifest('http://test/manifest.json');
    await runtime.preload('water-impact');
    expect(runtime.getTelemetry()).toMatchObject({
      softParticleDepthMode: 'disabled-no-depth',
      softParticleSystemsDisabled: 1
    });
    runtime.dispose();
  });

  it('preserves soft particles when the host provides a depth texture', async () => {
    const scene = new Scene();
    const options = {
      scene,
      renderer: {} as WebGLRenderer,
      camera: new PerspectiveCamera(),
      depthTexture: new Texture(),
      fetch: mapFetch(new Map())
    };
    const runtime = createVfxRuntime(options);
    expect(runtime.getTelemetry()).toMatchObject({
      softParticleDepthMode: 'provided-depth',
      softParticleSystemsDisabled: 0
    });
    runtime.dispose();
    options.depthTexture.dispose();
  });

  it('reports non-unit scaling of world-space spatial behaviors as approximate', async () => {
    const runtime = await readyRuntime({ prewarm: 1, max: 1 });
    const handle = runtime.spawn('water-impact', { scale: 0.5 });
    expect(runtime.getTelemetry().worldSpaceScaleApproximations).toBe(1);
    handle.setTransform({ scale: 0.25 });
    expect(runtime.getTelemetry().worldSpaceScaleApproximations).toBe(1);
    handle.release();
    runtime.dispose();
  });

  it('restores source prewarm on pooled clones omitted by Quarks 0.17.1', async () => {
    const prewarmedJson = structuredClone(fixtureJson);
    prewarmedJson.object.children[0].ps.looping = true;
    prewarmedJson.object.children[0].ps.prewarm = true;
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, prewarmedJson);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: { systems: Array<{ prewarm: boolean }> } | null;
      release(): void;
    };
    expect(handle.instance?.systems.map((system) => system.prewarm)).toEqual([true]);
    handle.release();
    runtime.dispose();
  });

  it('enforces exporter maxParticles for bursts and after pooled reuse', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.particleCapacity = {
      schemaVersion: 'unity_particle_quarks_exporter.particle_capacity.v1',
      maxParticles: 3
    };
    emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 10 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);

    let handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    expect(handle.instance?.systems[0]?.particleNum).toBe(3);
    handle.release();

    handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    expect(handle.instance?.systems[0]?.particleNum).toBe(3);
    handle.release();
    runtime.dispose();
  });

  it('advances exporter ParticleSystems at main.simulationSpeed with bounded substeps', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.prewarm = false;
    emitter.userData.unityParticleQuarks.simulationSpeed = {
      schemaVersion: 'unity_particle_quarks_exporter.simulation_speed.v1',
      value: 3
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;

    // Let Quarks finish its first-frame batch registration without advancing time.
    runtime.update(0);
    runtime.update(0.1);

    expect(handle.instance?.systems[0]?.emissionState.time).toBeCloseTo(0.3, 6);
    handle.release();
    runtime.dispose();
  });

  it('samples exporter start delay once and replays the seeded sample on pool restart', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.startDelay = {
      schemaVersion: 'unity_particle_quarks_exporter.start_delay.v1',
      randomSeed: 17,
      delay: {
        mode: 'twoCurves',
        minimum: { type: 'ConstantValue', value: 0.1 },
        maximum: { type: 'ConstantValue', value: 0.9 }
      }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    let handle = runtime.spawn('water-impact') as any;
    runtime.update(0);
    for (let index = 0; index < 6; index += 1) {
      runtime.update(0.1);
      expect(handle.instance?.systems[0]?.particleNum).toBe(0);
    }
    runtime.update(0.01);
    expect(handle.instance?.systems[0]?.particleNum).toBeGreaterThan(0);
    handle.release();

    handle = runtime.spawn('water-impact') as any;
    runtime.update(0.2);
    expect(handle.instance?.systems[0]?.particleNum).toBe(0);
    handle.release();
    runtime.dispose();
  });

  it('gates the first emission frame of every exporter loop with start delay', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.looping = true;
    emitter.ps.duration = 0.05;
    emitter.userData.unityParticleQuarks.startDelay = {
      schemaVersion: 'unity_particle_quarks_exporter.start_delay.v1',
      randomSeed: 17,
      delay: { mode: 'constant', value: { type: 'ConstantValue', value: 0.1 } }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.1);
    runtime.update(0.001);
    const system = handle.instance?.systems[0];
    expect(system?.particleNum).toBe(1);

    runtime.update(0.06);
    runtime.update(0.01);
    expect(system?.particleNum).toBe(1);
    runtime.update(0.09);
    runtime.update(0.001);
    expect(system?.particleNum).toBe(2);

    handle.release();
    runtime.dispose();
  });

  it('uses zero emitter speed for lifetime scaling before a motion baseline exists', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.prewarm = false;
    emitter.ps.startLife = { type: 'ConstantValue', value: 1 };
    emitter.userData.unityParticleQuarks.lifetimeByEmitterSpeed = {
      schemaVersion: 'unity_particle_quarks_exporter.lifetime_by_emitter_speed.v1',
      randomSeed: 29,
      range: [0, 8],
      curve: { mode: 'curve', value: linearCurve(0.5, 2) }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0);
    runtime.update(0.01);

    const particle = handle.instance?.systems[0]?.particles[0];
    expect(particle).toBeDefined();
    expect(particle.life).toBeCloseTo(0.5, 6);
    handle.release();
    runtime.dispose();
  });

  it('rejects an exporter simulation speed that could monopolize the update loop', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.simulationSpeed = {
      schemaVersion: 'unity_particle_quarks_exporter.simulation_speed.v1',
      value: Number.MAX_VALUE
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    expect(() => runtime.spawn('water-impact')).toThrow(/simulationSpeed exceeds the runtime maximum/);
    runtime.dispose();
  });

  it('aligns exporter Mesh local forward with current particle velocity without accumulating rotation', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.renderMode = 2;
    emitter.ps.prewarm = false;
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.startRotation = {
      type: 'Euler',
      angleX: { type: 'ConstantValue', value: 0 },
      angleY: { type: 'ConstantValue', value: 0 },
      angleZ: { type: 'ConstantValue', value: 0 },
      eulerOrder: 'XYZ'
    };
    emitter.ps.behaviors = [];
    emitter.userData.unityParticleQuarks.meshVelocityAlignment = {
      schemaVersion: 'unity_particle_quarks_exporter.mesh_velocity_alignment.v1',
      forwardAxis: [0, 0, 1]
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.001);
    const particle = handle.instance?.systems[0]?.particles[0];
    expect(particle).toBeDefined();
    particle.velocity.set(1, 0, 0);

    runtime.update(0.001);
    const expected = new Quaternion().setFromUnitVectors(new Vector3(0, 0, 1), new Vector3(1, 0, 0));
    const first = new Quaternion(
      particle.rotation.x,
      particle.rotation.y,
      particle.rotation.z,
      particle.rotation.w
    );
    expect(Math.abs(first.dot(expected))).toBeCloseTo(1, 6);

    runtime.update(0.001);
    const second = new Quaternion(
      particle.rotation.x,
      particle.rotation.y,
      particle.rotation.z,
      particle.rotation.w
    );
    expect(Math.abs(second.dot(expected))).toBeCloseTo(1, 6);

    for (const behavior of handle.instance.systems[0].behaviors) behavior.reset();
    particle.velocity.set(0, 0, -1);
    runtime.update(0.001);
    const afterLoopReset = new Quaternion(
      particle.rotation.x,
      particle.rotation.y,
      particle.rotation.z,
      particle.rotation.w
    );
    const reversed = new Quaternion().setFromUnitVectors(
      new Vector3(0, 0, 1),
      new Vector3(0, 0, -1)
    );
    expect(Math.abs(afterLoopReset.dot(reversed))).toBeCloseTo(1, 6);

    particle.velocity.set(0, 0, 0);
    runtime.update(0.001);
    const stopped = new Quaternion(
      particle.rotation.x,
      particle.rotation.y,
      particle.rotation.z,
      particle.rotation.w
    );
    expect(Math.abs(stopped.dot(new Quaternion()))).toBeCloseTo(1, 6);

    particle.velocity.set(0, 0, -1);
    runtime.update(0.001);
    const restarted = new Quaternion(
      particle.rotation.x,
      particle.rotation.y,
      particle.rotation.z,
      particle.rotation.w
    );
    expect(Math.abs(restarted.dot(reversed))).toBeCloseTo(1, 6);
    handle.release();
    runtime.dispose();
  });

  it.each(['view', 'facing'] as const)(
    'aligns exporter Mesh camera-facing mode %s without accumulating rotation',
    async (mode) => {
      const source = exporterFixture();
      const emitter = source.object.children[0];
      emitter.ps.renderMode = 2;
      emitter.ps.shape = { type: 'point' };
      emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
      emitter.ps.startRotation = {
        type: 'Euler',
        angleX: { type: 'ConstantValue', value: 0 },
        angleY: { type: 'ConstantValue', value: 0 },
        angleZ: { type: 'ConstantValue', value: 0 },
        eulerOrder: 'XYZ'
      };
      emitter.userData.unityParticleQuarks.meshCameraAlignment = {
        schemaVersion: 'unity_particle_quarks_exporter.mesh_camera_alignment.v1',
        mode,
        forwardAxis: [0, 0, 1],
        upAxis: [0, 1, 0],
        preserveAuthoredRotation: true,
        simulationSpace: 'local'
      };

      const scene = new Scene();
      const camera = new PerspectiveCamera();
      camera.position.set(0, 0, 10);
      camera.lookAt(0, 0, 0);
      const manifest: VfxManifest = {
        schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
        effects: [{ id: 'water-impact', status: 'ready', url: './effect.json' }]
      };
      const runtime = createVfxRuntime({
        scene,
        renderer: {} as WebGLRenderer,
        camera,
        pool: { prewarm: 0, max: 1 },
        fetch: mapFetch(new Map([
          ['http://test/manifest.json', jsonResponse(manifest)],
          ['http://test/effect.json', jsonResponse(source)]
        ]))
      });
      await runtime.loadManifest('http://test/manifest.json');
      await runtime.preload('water-impact');
      const handle = runtime.spawn('water-impact') as any;
      runtime.update(0.001);
      const system = handle.instance?.systems[0];
      const particle = system?.particles[0];
      expect(system?.behaviors.slice(0, 1).map((behavior: { type: string }) => behavior.type)).toEqual([
        'UnityParticleQuarksMeshCameraAlignmentPreparation'
      ]);
      expect(system?.behaviors.at(-1)?.type).toBe('UnityParticleQuarksMeshCameraAlignmentFinalization');
      expect(particle).toBeDefined();

      const first = new Quaternion(particle.rotation.x, particle.rotation.y, particle.rotation.z, particle.rotation.w);
      expect(Math.abs(first.dot(new Quaternion()))).toBeCloseTo(1, 6);

      camera.position.set(10, 0, 0);
      camera.lookAt(0, 0, 0);
      runtime.update(0.001);
      const expectedSide = new Quaternion().setFromUnitVectors(
        new Vector3(0, 0, 1),
        mode === 'view' ? new Vector3(1, 0, 0) : new Vector3(1, 0, 0)
      );
      const second = new Quaternion(particle.rotation.x, particle.rotation.y, particle.rotation.z, particle.rotation.w);
      expect(Math.abs(second.dot(expectedSide))).toBeCloseTo(1, 5);

      runtime.update(0.001);
      const third = new Quaternion(particle.rotation.x, particle.rotation.y, particle.rotation.z, particle.rotation.w);
      expect(Math.abs(third.dot(expectedSide))).toBeCloseTo(1, 5);
      handle.release();
      runtime.dispose();
    }
  );

  it('uses per-particle eye direction for Mesh Facing instead of the unified View direction', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.renderMode = 2;
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.startRotation = {
      type: 'Euler',
      angleX: { type: 'ConstantValue', value: 0.45 },
      angleY: { type: 'ConstantValue', value: -0.2 },
      angleZ: { type: 'ConstantValue', value: 0.3 },
      eulerOrder: 'XYZ'
    };
    emitter.userData.unityParticleQuarks.meshCameraAlignment = {
      schemaVersion: 'unity_particle_quarks_exporter.mesh_camera_alignment.v1',
      mode: 'facing',
      forwardAxis: [0, 0, 1],
      upAxis: [0, 1, 0],
      preserveAuthoredRotation: true,
      simulationSpace: 'local'
    };
    const scene = new Scene();
    const camera = new PerspectiveCamera();
    camera.position.set(10, 0, 10);
    camera.lookAt(0, 0, 0);
    const runtime = createVfxRuntime({
      scene,
      renderer: {} as WebGLRenderer,
      camera,
      pool: { prewarm: 0, max: 1 },
      fetch: mapFetch(new Map([
        ['http://test/manifest.json', jsonResponse({ schemaVersion: 'unity_particle_quarks_runtime.manifest.v1', effects: [{ id: 'water-impact', status: 'ready', url: './effect.json' }] })],
        ['http://test/effect.json', jsonResponse(source)]
      ]))
    });
    await runtime.loadManifest('http://test/manifest.json');
    await runtime.preload('water-impact');
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.001);
    const particle = handle.instance?.systems[0]?.particles[0];
    expect(particle).toBeDefined();
    particle.position.set(2, 0, 0);
    runtime.update(0.001);
    const actual = new Vector3(0, 0, 1).applyQuaternion(new Quaternion(
      particle.rotation.x,
      particle.rotation.y,
      particle.rotation.z,
      particle.rotation.w
    )).normalize();
    const expectedFacing = new Vector3(8, 0, 10).normalize();
    expect(actual.dot(expectedFacing)).toBeGreaterThan(0.999);
    expect(actual.dot(new Vector3(1, 0, 1).normalize())).toBeLessThan(0.999);
    particle.position.set(camera.position.x, camera.position.y, camera.position.z);
    runtime.update(0.001);
    expect([particle.rotation.x, particle.rotation.y, particle.rotation.z, particle.rotation.w].every(Number.isFinite)).toBe(true);
    handle.release();
    runtime.dispose();
  });

  it('faces the post-integration position for moving Mesh Facing particles', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.renderMode = 2;
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 2 };
    emitter.userData.unityParticleQuarks.meshCameraAlignment = {
      schemaVersion: 'unity_particle_quarks_exporter.mesh_camera_alignment.v1',
      mode: 'facing',
      forwardAxis: [0, 0, 1],
      upAxis: [0, 1, 0],
      preserveAuthoredRotation: true,
      simulationSpace: 'local'
    };
    const scene = new Scene();
    const camera = new PerspectiveCamera();
    camera.position.set(0, 0, 10);
    camera.lookAt(0, 0, 0);
    const runtime = createVfxRuntime({
      scene,
      renderer: {} as WebGLRenderer,
      camera,
      pool: { prewarm: 0, max: 1 },
      fetch: mapFetch(new Map([
        ['http://test/manifest.json', jsonResponse({ schemaVersion: 'unity_particle_quarks_runtime.manifest.v1', effects: [{ id: 'water-impact', status: 'ready', url: './effect.json' }] })],
        ['http://test/effect.json', jsonResponse(source)]
      ]))
    });
    await runtime.loadManifest('http://test/manifest.json');
    await runtime.preload('water-impact');
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    expect(particle).toBeDefined();
    const expectedFacing = camera.position.clone().sub(particle.position).normalize();
    const actual = new Vector3(0, 0, 1).applyQuaternion(new Quaternion(
      particle.rotation.x,
      particle.rotation.y,
      particle.rotation.z,
      particle.rotation.w
    )).normalize();
    expect(actual.dot(expectedFacing)).toBeGreaterThan(0.999);
    handle.release();
    runtime.dispose();
  });

  it('preserves authored Mesh rotation when camera alignment is identity', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.renderMode = 2;
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.startRotation = {
      type: 'Euler',
      angleX: { type: 'ConstantValue', value: 0.35 },
      angleY: { type: 'ConstantValue', value: 0.6 },
      angleZ: { type: 'ConstantValue', value: 0.8 },
      eulerOrder: 'XYZ'
    };
    emitter.userData.unityParticleQuarks.meshCameraAlignment = {
      schemaVersion: 'unity_particle_quarks_exporter.mesh_camera_alignment.v1',
      mode: 'view',
      forwardAxis: [0, 0, 1],
      upAxis: [0, 1, 0],
      preserveAuthoredRotation: true,
      simulationSpace: 'local'
    };
    const scene = new Scene();
    const camera = new PerspectiveCamera();
    camera.position.set(0, 0, 10);
    camera.lookAt(0, 0, 0);
    const runtime = createVfxRuntime({
      scene,
      renderer: {} as WebGLRenderer,
      camera,
      pool: { prewarm: 0, max: 1 },
      fetch: mapFetch(new Map([
        ['http://test/manifest.json', jsonResponse({ schemaVersion: 'unity_particle_quarks_runtime.manifest.v1', effects: [{ id: 'water-impact', status: 'ready', url: './effect.json' }] })],
        ['http://test/effect.json', jsonResponse(source)]
      ]))
    });
    await runtime.loadManifest('http://test/manifest.json');
    await runtime.preload('water-impact');
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.001);
    const particle = handle.instance?.systems[0]?.particles[0];
    const expected = new Quaternion().setFromEuler(new Euler(0.35, 0.6, 0.8, 'XYZ'));
    expect(particle).toBeDefined();
    expect(Math.abs(new Quaternion(particle.rotation.x, particle.rotation.y, particle.rotation.z, particle.rotation.w).dot(expected))).toBeCloseTo(1, 5);
    handle.release();
    runtime.dispose();
  });

  it.each([0, -2])(
    'uses the sampled Mesh face normal for offset and birth alignment at startSpeed=%s',
    async (startSpeed) => {
      const source = unityMeshTriangleShapeFixture(startSpeed);
      const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
      const handle = runtime.spawn('water-impact') as any;
      runtime.update(0);
      runtime.update(0.01);

      const particle = handle.instance?.systems[0]?.particles[0];
      expect(particle).toBeDefined();
      expect(particle.position.z).toBeCloseTo(2 + startSpeed * 0.01, 6);
      expect(particle.velocity.z).toBeCloseTo(startSpeed, 6);
      const rotation = new Quaternion(
        particle.rotation.x,
        particle.rotation.y,
        particle.rotation.z,
        particle.rotation.w
      );
      expect(Math.abs(rotation.dot(new Quaternion()))).toBeCloseTo(1, 6);

      handle.release();
      runtime.dispose();
    }
  );

  it('treats exported Mesh rotation-by-speed angular velocity as radians', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.renderMode = 2;
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 1 };
    emitter.ps.startRotation = {
      type: 'Euler',
      angleX: { type: 'ConstantValue', value: 0 },
      angleY: { type: 'ConstantValue', value: 0 },
      angleZ: { type: 'ConstantValue', value: 0 },
      eulerOrder: 'XYZ'
    };
    emitter.userData.unityParticleQuarks.meshRotationBySpeed = {
      schemaVersion: 'unity_particle_quarks_exporter.mesh_rotation_by_speed.v1',
      axisMode: 'fixed',
      axis: [0, 0, 1],
      basisX: [1, 0, 0],
      basisY: [0, 1, 0],
      basisZ: [0, 0, 1],
      speedRange: [0, 1],
      angularVelocity: { mode: 'constant', value: { type: 'ConstantValue', value: 2 } }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.001);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: any) =>
      candidate.type === 'UnityParticleQuarksMeshRotationBySpeed');
    particle.rotation.identity();
    behavior.update(particle, 0.5);

    const angle = 2 * Math.acos(Math.min(1, Math.abs(particle.rotation.w)));
    expect(angle).toBeCloseTo(1, 6);
    handle.release();
    runtime.dispose();
  });

  it('restarts emission-over-time generator memory on every pooled spawn', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.emissionBursts = [];
    emitter.ps.emissionOverTime = { type: 'IntervalValue', a: 100, b: 100 };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);

    let handle = runtime.spawn('water-impact') as any;
    runtime.update(0.05);
    runtime.update(0.05);
    expect(handle.instance?.systems[0]?.particleNum).toBeGreaterThan(0);
    handle.release();

    handle = runtime.spawn('water-impact') as any;
    runtime.update(0.05);
    runtime.update(0.05);
    expect(handle.instance?.systems[0]?.particleNum).toBeGreaterThan(0);
    handle.release();
    runtime.dispose();
  });

  it('applies Unity whole-sheet cycle count and start frame with wrapped tile indices', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.textureSheetAnimation = {
      schemaVersion: 'unity_particle_quarks_exporter.texture_sheet_animation.v1',
      timeMode: 'lifetime',
      tileCount: 20,
      cycleCount: 30,
      frameOverTime: { mode: 'curve', value: linearCurve(0, 1) },
      startFrame: { mode: 'constant', value: { type: 'ConstantValue', value: 0.1 } }
    };
    emitter.ps.uTileCount = 5;
    emitter.ps.vTileCount = 4;
    emitter.ps.blendTiles = false;
    emitter.ps.startLife = { type: 'ConstantValue', value: 10 };
    emitter.ps.behaviors.push({ type: 'FrameOverLife', frame: linearCurve(0, 20) });
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);

    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: any) =>
      candidate.type === 'UnityParticleQuarksTextureSheetAnimation');
    expect(behavior).toBeDefined();
    expect(system?.behaviors.some((candidate: any) => candidate.type === 'FrameOverLife')).toBe(false);

    const tiles = [];
    for (let frame = 0; frame < 75; frame += 1) {
      runtime.update(1 / 60);
      tiles.push(particle.uvTile);
    }
    expect(new Set(tiles).size).toBeGreaterThan(15);
    expect(particle.age).toBeGreaterThan(1);

    particle.age = 1.25;
    particle.life = 10;
    behavior.update(particle, 0);
    expect(particle.uvTile).toBe(17);

    handle.release();
    runtime.dispose();
  });

  it('preserves Unity death position and child emitter basis for InheritNothing subemitters', async () => {
    const source = unityDeathSubemitterFixture();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact', { position: [10, 20, 30] }) as unknown as {
      instance: {
        systems: Array<{
          behaviors: Array<Record<string, unknown>>;
          particles: Array<{ parentMatrix?: { elements: number[] } }>;
        }>;
      } | null;
      release(): void;
    };
    const parent = handle.instance?.systems[0];
    const child = handle.instance?.systems[1];
    const behavior = parent?.behaviors.find((candidate) => candidate.type === 'EmitSubParticleSystem');
    expect(behavior?.particleSystem).toBe(parent);

    runtime.update(0.05);
    runtime.update(0.05);
    runtime.update(0.01);

    const matrix = child?.particles[0]?.parentMatrix?.elements;
    expect(matrix).toBeDefined();
    expect(matrix?.[12]).toBeCloseTo(10, 5);
    expect(matrix?.[13]).toBeCloseTo(20, 5);
    expect(matrix?.[14]).toBeCloseTo(30.2, 5);
    expect(matrix?.[8]).toBeCloseTo(0, 5);
    expect(matrix?.[9]).toBeCloseTo(1, 5);
    expect(matrix?.[10]).toBeCloseTo(0, 5);

    handle.release();
    expect(behavior?.subEmissions).toEqual([]);
    runtime.dispose();
  });

  it('follows Unity Birth subemitters along the live parent trajectory', async () => {
    const source = unityBirthSubemitterFixture();
    const child = source.object.children[1];
    child.ps.emissionBursts = [];
    child.ps.emissionOverTime = { type: 'ConstantValue', value: 10 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact', { position: [2, 3, 4] }) as unknown as {
      instance: {
        systems: Array<{
          behaviors?: Array<{ type: string; subEmissions?: Array<{ matrix?: { elements: number[] } }> }>;
          particles: Array<{ parentMatrix?: { elements: number[] } }>;
        }>;
      } | null;
      release(): void;
    };

    const parent = handle.instance?.systems[0];
    const behavior = parent?.behaviors?.find((candidate) => candidate.type === 'EmitSubParticleSystem');
    runtime.update(0.05);
    runtime.update(0.05);
    const firstMatrix = behavior?.subEmissions?.[0]?.matrix?.elements;
    expect(firstMatrix).toBeDefined();
    const firstZ = firstMatrix?.[14] ?? 0;
    const firstChildZ = handle.instance?.systems[1]?.particles[0]?.parentMatrix?.elements[14];
    expect(firstChildZ).toBeDefined();
    runtime.update(0.2);
    const laterMatrix = behavior?.subEmissions?.[0]?.matrix?.elements;
    expect(laterMatrix?.[14]).toBeGreaterThan(firstZ + 0.1);
    expect(handle.instance?.systems[1]?.particles.length).toBeGreaterThan(0);
    expect(handle.instance?.systems[1]?.particles[0]?.parentMatrix?.elements[14]).toBeCloseTo(firstChildZ ?? 0, 5);

    handle.release();
    runtime.dispose();
  });

  it('uses the active subemission matrix for world-space Shape corrections', async () => {
    const source = unityWorldSpaceShapeSubemitterFixture();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact', { position: [10, 20, 30] }) as unknown as {
      instance: {
        systems: Array<{
          particles: Array<{ position: { x: number; y: number; z: number } }>;
        }>;
      } | null;
      release(): void;
    };

    runtime.update(0.05);
    runtime.update(0.05);
    runtime.update(0.01);
    const child = handle.instance?.systems[1]?.particles[0];
    expect(child?.position.x).toBeCloseTo(10, 5);
    expect(child?.position.y).toBeCloseTo(20, 5);
    expect(child?.position.z).toBeCloseTo(30.2, 5);

    handle.release();
    runtime.dispose();
  });

  it('applies Unity color, scalar size, Z rotation, and remaining lifetime inheritance', async () => {
    const source = unityInheritedBirthSubemitterFixture();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          particles: Array<{
            color: { x: number; y: number; z: number; w: number };
            startColor: { x: number; y: number; z: number; w: number };
            size: { x: number; y: number; z: number };
            startSize: { x: number; y: number; z: number };
            rotation: number | { x: number; y: number; z: number; w: number };
            life: number;
          }>;
        }>;
      } | null;
      release(): void;
    };

    runtime.update(0.01);
    runtime.update(0.01);

    const childParticles = handle.instance?.systems[1]?.particles ?? [];
    expect(childParticles).toHaveLength(2);
    for (const particle of childParticles) {
      const expectedColor = [0.4, 0.15, 0.3, 0.25];
      const startColor = [particle.startColor.x, particle.startColor.y, particle.startColor.z, particle.startColor.w];
      const color = [particle.color.x, particle.color.y, particle.color.z, particle.color.w];
      expectedColor.forEach((value, index) => {
        expect(startColor[index]).toBeCloseTo(value, 6);
        expect(color[index]).toBeCloseTo(value, 6);
      });
      expect([particle.startSize.x, particle.startSize.y, particle.startSize.z]).toEqual([6, 6, 6]);
      expect([particle.size.x, particle.size.y, particle.size.z]).toEqual([6, 6, 6]);
      expect(particle.life).toBe(20);
      expect(typeof particle.rotation).toBe('object');
      if (typeof particle.rotation !== 'number') {
        expect(particle.rotation.x).toBeCloseTo(0, 6);
        expect(particle.rotation.y).toBeCloseTo(0, 6);
        expect(particle.rotation.z).toBeCloseTo(Math.sin(0.7), 6);
        expect(particle.rotation.w).toBeCloseTo(Math.cos(0.7), 6);
      }
    }

    handle.release();
    runtime.dispose();
  });

  it('temporarily evaluates a child subemitter with inherited parent duration', async () => {
    const source = unityInheritedBirthSubemitterFixture();
    const inheritance = source.object.children[0].userData.unityParticleQuarks.subEmitterInheritance[0];
    inheritance.inheritColor = false;
    inheritance.inheritSize = false;
    inheritance.inheritRotation = false;
    inheritance.inheritLifetime = false;
    inheritance.inheritDuration = true;
    source.object.children[1].ps.duration = 2;
    source.object.children[1].ps.emissionOverTime = linearCurve(0, 1);
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    const childSystem = handle.instance?.systems[1];
    const originalGenValue = childSystem.emissionOverTime.genValue.bind(childSystem.emissionOverTime);
    const observedDurations: number[] = [];
    childSystem.emissionOverTime.genValue = (...args: unknown[]) => {
      observedDurations.push(childSystem.duration);
      return originalGenValue(...args);
    };

    runtime.update(0);
    runtime.update(0);
    expect(observedDurations.some((duration) => duration > 4.9 && duration <= 5)).toBe(true);
    expect(childSystem.duration).toBe(2);

    handle.release();
    runtime.dispose();
  });

  it('stops Birth subemitter emission when the parent particle dies', async () => {
    const source = unityInheritedBirthSubemitterFixture();
    const parent = source.object.children[0];
    const child = source.object.children[1];
    parent.ps.startLife = { type: 'ConstantValue', value: 0.1 };
    child.ps.startLife = { type: 'ConstantValue', value: 10 };
    child.ps.emissionOverTime = { type: 'ConstantValue', value: 100 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    for (let index = 0; index < 20; index += 1) runtime.update(0.1);
    const childSystem = handle.instance?.systems[1];
    expect(childSystem?.particleNum).toBeLessThan(40);
    handle.release();
    runtime.dispose();
  });

  it('applies exporter linear Velocity over Lifetime before spatial behaviors with stable curve samples', async () => {
    const source = unityVelocityOverLifetimeFixture();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          behaviors: Array<{ type: string }>;
          particles: Array<{
            age: number;
            position: { x: number; y: number; z: number };
            velocity: { x: number; y: number; z: number };
          }>;
        }>;
      } | null;
      release(): void;
    };
    const system = handle.instance?.systems[0];
    expect(system?.behaviors.map((behavior) => behavior.type).slice(0, 2)).toEqual([
      'UnityParticleQuarksVelocityOverLifetime',
      'ForceOverLife'
    ]);

    runtime.update(0.01);
    const particle = system?.particles[0];
    expect(particle).toBeDefined();
    expect(particle?.velocity.z).toBeCloseTo(3.6, 5);
    const stableRandomVelocity = particle?.velocity.x ?? 0;
    expect(stableRandomVelocity).toBeGreaterThanOrEqual(1);
    expect(stableRandomVelocity).toBeLessThanOrEqual(5);

    for (let index = 0; index < 4; index += 1) runtime.update(0.01);
    expect(particle?.velocity.z).toBeCloseTo(2, 5);
    expect(particle?.velocity.x).toBeCloseTo(stableRandomVelocity, 6);
    for (let index = 0; index < 6; index += 1) runtime.update(0.01);
    expect(particle?.velocity.z).toBeCloseTo(0, 5);
    expect(particle?.position.x).toBeGreaterThan(0);
    expect(particle?.position.z).toBeGreaterThan(0);

    handle.release();
    runtime.dispose();
  });

  it('corrects Unity sphere volume radius at particle birth', async () => {
    const source = unitySphereSemanticsFixture();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          behaviors: Array<{ type: string }>;
          particles: Array<{
            position: { x: number; y: number; z: number };
            velocity: { x: number; y: number; z: number };
          }>;
        }>;
      } | null;
      release(): void;
    };

    runtime.update(0.001);
    const system = handle.instance?.systems[0];
    const particles = system?.particles ?? [];
    expect(system?.behaviors[0]?.type).toBe('UnityParticleQuarksShapeSemantics');
    expect(particles.length).toBeGreaterThanOrEqual(1000);
    const meanRadius = particles.reduce((sum, particle) =>
      sum + Math.hypot(particle.position.x, particle.position.y, particle.position.z), 0) / particles.length;
    expect(meanRadius).toBeCloseTo(1.5, 1);

    handle.release();
    runtime.dispose();
  });

  it('retains stock Shape direction for withdrawn legacy random-direction metadata', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.shapeSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
      randomDirectionAmount: 1
    };
    emitter.ps.shape = {
      type: 'cone', radius: 0, arc: Math.PI * 2, thickness: 0,
      angle: 0, mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 2 };
    const warning = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: { systems: Array<{ particles: Array<{ velocity: { x: number; y: number; z: number } }> }> } | null;
      release(): void;
    };
    runtime.update(0.01);
    const particle = handle.instance?.systems[0]?.particles[0];
    expect(particle?.velocity.x).toBeCloseTo(0, 6);
    expect(particle?.velocity.y).toBeCloseTo(0, 6);
    expect(particle?.velocity.z).toBeCloseTo(2, 6);
    expect(warning).toHaveBeenCalledWith(expect.stringContaining('withdrawn pre-0.1.17 mapping'));
    warning.mockRestore();
    handle.release();
    runtime.dispose();
  });

  it('applies Unity randomDirectionAmount unit-vector lerp at birth', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.shapeSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
      randomDirection: { mode: 'lerpRandomUnit', amount: 0.5 }
    };
    emitter.ps.shape = {
      type: 'circle', radius: 1, arc: Math.PI * 2, thickness: 0,
      mode: 3, spread: 0, speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 2 };

    const random = vi.spyOn(Math, 'random').mockReturnValue(0.25);
    let runtime: VfxRuntime | undefined;
    try {
      runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
      const handle = runtime.spawn('water-impact') as unknown as {
        instance: { systems: Array<{ particles: Array<{ velocity: { x: number; y: number; z: number } }> }> } | null;
        release(): void;
      };
      runtime.update(0.01);
      const particle = handle.instance?.systems[0]?.particles[0];
      const randomX = Math.sqrt(1 - 0.5 * 0.5) * Math.cos(Math.PI / 2);
      const randomY = Math.sqrt(1 - 0.5 * 0.5) * Math.sin(Math.PI / 2);
      const randomZ = -0.5;
      const mixedX = 0.5 + 0.5 * randomX;
      const mixedY = 0.5 * randomY;
      const mixedZ = 0.5 * randomZ;
      const mixedLength = Math.hypot(mixedX, mixedY, mixedZ);
      expect(particle?.velocity.x).toBeCloseTo(2 * mixedX / mixedLength, 6);
      expect(particle?.velocity.y).toBeCloseTo(2 * mixedY / mixedLength, 6);
      expect(particle?.velocity.z).toBeCloseTo(2 * mixedZ / mixedLength, 6);
      expect(Math.hypot(particle?.velocity.x ?? 0, particle?.velocity.y ?? 0, particle?.velocity.z ?? 0))
        .toBeCloseTo(2, 6);
      handle.release();
    } finally {
      runtime?.dispose();
      random.mockRestore();
    }
  });

  it('applies Unity Cone randomDirectionAmount disk formula at birth', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    const coneAngle = Math.PI / 6;
    emitter.userData.unityParticleQuarks.shapeSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
      randomDirection: { mode: 'coneSurface', amount: 1, angle: coneAngle, radius: 1 }
    };
    emitter.ps.shape = {
      type: 'cone', radius: 1, arc: Math.PI * 2, thickness: 0,
      angle: coneAngle, mode: 3, spread: 0, speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 2 };

    const random = vi.spyOn(Math, 'random').mockReturnValue(0.25);
    let runtime: VfxRuntime | undefined;
    try {
      runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
      const handle = runtime.spawn('water-impact') as unknown as {
        instance: { systems: Array<{ particles: Array<{ velocity: { x: number; y: number; z: number } }> }> } | null;
        release(): void;
      };
      runtime.update(0.01);
      const particle = handle.instance?.systems[0]?.particles[0];
      const diskRadius = Math.sqrt(0.001 + 0.25 * (1 - 0.001));
      const y = diskRadius * Math.sin(coneAngle);
      const z = Math.cos(coneAngle);
      const length = Math.hypot(y, z);
      expect(particle?.velocity.x).toBeCloseTo(0, 6);
      expect(particle?.velocity.y).toBeCloseTo(2 * y / length, 6);
      expect(particle?.velocity.z).toBeCloseTo(2 * z / length, 6);
      expect(Math.hypot(particle?.velocity.x ?? 0, particle?.velocity.y ?? 0, particle?.velocity.z ?? 0))
        .toBeCloseTo(2, 6);
      handle.release();
    } finally {
      runtime?.dispose();
      random.mockRestore();
    }
  });

  it('replaces the discrete stock Box fallback with continuous volume samples', async () => {
    const source = unityBoxSemanticsFixture();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          particles: Array<{ position: { x: number; y: number; z: number } }>;
        }>;
      } | null;
      release(): void;
    };

    runtime.update(0.001);
    const particles = handle.instance?.systems[0]?.particles ?? [];
    expect(particles.length).toBeGreaterThanOrEqual(200);
    for (const particle of particles) {
      expect(Math.abs(particle.position.x)).toBeLessThanOrEqual(2);
      expect(Math.abs(particle.position.y)).toBeLessThanOrEqual(3);
      expect(Math.abs(particle.position.z)).toBeLessThanOrEqual(4);
    }
    expect(new Set(particles.map((particle) => particle.position.x.toFixed(6))).size).toBeGreaterThan(100);

    handle.release();
    runtime.dispose();
  });

  it('replaces the stock mean SizeOverLife fallback with a stable TwoCurves sample', async () => {
    const source = unitySizeTwoCurvesFixture();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          behaviors: Array<{ type: string }>;
          particles: Array<{
            age: number;
            size: { x: number; y: number; z: number };
            startSize: { x: number; y: number; z: number };
          }>;
        }>;
      } | null;
      release(): void;
    };

    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    expect(system?.behaviors.some((behavior) => behavior.type === 'SizeOverLife')).toBe(false);
    expect(system?.behaviors.some((behavior) => behavior.type === 'UnityParticleQuarksSizeOverLifetime')).toBe(true);
    expect(particle).toBeDefined();
    const initialSize = particle?.size.x ?? 0;
    expect(initialSize).toBeGreaterThanOrEqual(2);
    expect(initialSize).toBeLessThanOrEqual(6.1);
    for (let index = 0; index < 5; index += 1) runtime.update(0.1);
    expect((particle?.size.x ?? 0) - initialSize).toBeCloseTo(0.82, 1);
    expect(particle?.size.y).toBeCloseTo(particle?.size.x ?? 0, 6);
    expect(particle?.size.z).toBeCloseTo(particle?.size.x ?? 0, 6);

    handle.release();
    runtime.dispose();
  });

  it('remaps stock Mesh scalar rotation curves around a fixed Unity axis and resets on pool reuse', async () => {
    const source = unityMeshScalarRotationFixture('fixed', [0, 1, 0]);
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    let handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          behaviors: Array<{ type: string }>;
          particles: Array<{ rotation: { x: number; y: number; z: number; w: number } }>;
        }>;
      } | null;
      release(): void;
    };
    runtime.update(0.001);
    let system = handle.instance?.systems[0];
    expect(system?.behaviors.map((behavior) => behavior.type)).toEqual([
      'UnityParticleQuarksMeshScalarRotationPreparation',
      'Rotation3DOverLife',
      'UnityParticleQuarksMeshScalarRotationFinalization'
    ]);
    let rotation = system?.particles[0]?.rotation;
    expect(rotation?.x).toBeCloseTo(0, 6);
    expect(rotation?.y).toBeLessThan(0);
    expect(rotation?.z).toBeCloseTo(0, 6);
    const initialAngle = 2 * Math.atan2(rotation?.y ?? 0, rotation?.w ?? 1);

    runtime.update(0.1);
    rotation = system?.particles[0]?.rotation;
    const updatedAngle = 2 * Math.atan2(rotation?.y ?? 0, rotation?.w ?? 1);
    expect(updatedAngle - initialAngle).toBeCloseTo(-0.2, 6);

    handle.release();
    handle = runtime.spawn('water-impact') as unknown as typeof handle;
    runtime.update(0.001);
    system = handle.instance?.systems[0];
    rotation = system?.particles[0]?.rotation;
    const reusedAngle = 2 * Math.atan2(rotation?.y ?? 0, rotation?.w ?? 1);
    expect(reusedAngle).toBeCloseTo(-1.002, 6);

    handle.release();
    runtime.dispose();
  });

  it('derives a stable per-particle Mesh scalar-rotation axis from corrected Shape position', async () => {
    const source = unityMeshScalarRotationFixture('position');
    const emitter = source.object.children[0];
    emitter.ps.shape = {
      type: 'sphere', radius: 2, arc: Math.PI * 2, thickness: 0,
      mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 64 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          particles: Array<{
            position: { x: number; y: number; z: number };
            rotation: { x: number; y: number; z: number; w: number };
          }>;
        }>;
      } | null;
      release(): void;
    };
    runtime.update(0.001);

    const particles = handle.instance?.systems[0]?.particles ?? [];
    expect(particles.length).toBe(64);
    for (const particle of particles) {
      const length = Math.hypot(particle.position.x, particle.position.y);
      if (length <= 1e-8) continue;
      const axisX = particle.position.y / length;
      const axisY = -particle.position.x / length;
      const quaternionAxisLength = Math.hypot(particle.rotation.x, particle.rotation.y, particle.rotation.z);
      expect(particle.rotation.x / quaternionAxisLength).toBeCloseTo(-axisX, 5);
      expect(particle.rotation.y / quaternionAxisLength).toBeCloseTo(-axisY, 5);
      expect(particle.rotation.z).toBeCloseTo(0, 5);
      expect(particle.rotation.w).toBeGreaterThan(0.85);
    }

    handle.release();
    runtime.dispose();
  });

  it('composes a fixed local Mesh scalar-rotation axis with the world-space emitter transform', async () => {
    const source = unityMeshScalarRotationFixture('fixed', [1, 0, 0]);
    const emitter = source.object.children[0];
    const emitterMatrix = new Matrix4().makeRotationY(Math.PI / 2);
    emitterMatrix.setPosition(3, 4, 5);
    emitter.matrix = emitterMatrix.toArray();
    emitter.ps.worldSpace = true;
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          particles: Array<{ rotation: { x: number; y: number; z: number; w: number } }>;
        }>;
      } | null;
      release(): void;
    };
    runtime.update(0.001);

    const rotation = handle.instance?.systems[0]?.particles[0]?.rotation;
    const base = new Quaternion().setFromAxisAngle(new Vector3(0, 1, 0), Math.PI / 2);
    const relative = base.clone().invert().multiply(new Quaternion(
      rotation?.x ?? 0,
      rotation?.y ?? 0,
      rotation?.z ?? 0,
      rotation?.w ?? 1
    ));
    const relativeAxisLength = Math.hypot(relative.x, relative.y, relative.z);
    expect(relative.x / relativeAxisLength).toBeCloseTo(-1, 5);
    expect(relative.y / relativeAxisLength).toBeCloseTo(0, 5);
    expect(relative.z / relativeAxisLength).toBeCloseTo(0, 5);
    expect(relative.w).toBeGreaterThan(0.85);

    handle.release();
    runtime.dispose();
  });

  it('keeps the active world-space emission matrix across a looping behavior reset', async () => {
    const source = unityMeshScalarRotationFixture('fixed', [0, 1, 0]);
    const emitter = source.object.children[0];
    emitter.ps.worldSpace = true;
    emitter.ps.looping = true;
    emitter.ps.duration = 0.001;
    emitter.ps.emissionOverTime = { type: 'ConstantValue', value: 100 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as unknown as {
      instance: {
        systems: Array<{
          particles: Array<{ rotation: { x: number; y: number; z: number; w: number } }>;
        }>;
      } | null;
      release(): void;
    };

    expect(() => {
      runtime.update(0.01);
      runtime.update(0.01);
    }).not.toThrow();
    const rotation = handle.instance?.systems[0]?.particles[0]?.rotation;
    expect(rotation?.x).toBeCloseTo(0, 6);
    expect(rotation?.y).not.toBeCloseTo(0, 3);
    expect(rotation?.z).toBeCloseTo(0, 6);

    handle.release();
    runtime.dispose();
  });

  it('uses separate birth position and direction transforms and resets them on pool reuse', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.shapeSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
      directionMode: 'localZ',
      birthPositionTransform: [2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 0, 1, 2, 3, 1],
      birthDirectionTransform: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1]
    };
    emitter.ps.shape = {
      type: 'rectangle', width: 2, height: 2, thickness: 0,
      mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 2 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    let handle = runtime.spawn('water-impact') as any;
    runtime.update(0.001);
    let particle = handle.instance?.systems[0]?.particles[0];
    expect(particle?.position.x).toBeGreaterThanOrEqual(-1.01);
    expect(particle?.position.x).toBeLessThanOrEqual(3.01);
    expect(particle?.position.y).toBeGreaterThanOrEqual(-1.01);
    expect(particle?.position.y).toBeLessThanOrEqual(5.01);
    expect(particle?.velocity.z).toBeCloseTo(-2, 6);

    handle.release();
    handle = runtime.spawn('water-impact') as any;
    runtime.update(0.001);
    particle = handle.instance?.systems[0]?.particles[0];
    expect(particle?.velocity.z).toBeCloseTo(-2, 6);
    handle.release();
    runtime.dispose();
  });

  it('preserves birth speed after a non-uniform Shape normal transform', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.shapeSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
      birthPositionTransform: [2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 0, 0, 0, 0, 1],
      birthDirectionTransform: [0.5, 0, 0, 0, 0, 1 / 3, 0, 0, 0, 0, 0.25, 0, 0, 0, 0, 1]
    };
    emitter.ps.shape = {
      type: 'cone', radius: 0, arc: Math.PI * 2, thickness: 0,
      angle: 0, mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 3 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.001);

    const velocity = handle.instance?.systems[0]?.particles[0]?.velocity;
    expect(Math.hypot(velocity.x, velocity.y, velocity.z)).toBeCloseTo(3, 6);
    expect(velocity.z).toBeCloseTo(3, 6);
    handle.release();
    runtime.dispose();
  });

  it('applies the full emitter linear matrix to corrected world-space birth velocity', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.matrix = new Matrix4().makeScale(2, 3, 4).toArray();
    emitter.userData.unityParticleQuarks.shapeSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
      directionMode: 'localZ',
      birthDirectionTransform: [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, -1, 0, 0, 0, 0, 1],
      correctWorldSpaceBirthVelocity: true
    };
    emitter.ps.worldSpace = true;
    emitter.ps.shape = {
      type: 'rectangle', width: 1, height: 1, thickness: 0,
      mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 2 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.001);

    const particle = handle.instance?.systems[0]?.particles[0];
    expect(particle?.velocity.x).toBeCloseTo(0, 6);
    expect(particle?.velocity.y).toBeCloseTo(0, 6);
    expect(particle?.velocity.z).toBeCloseTo(-8, 6);
    handle.release();
    runtime.dispose();
  });

  it('keeps the world-space birth matrix across a looping Shape behavior reset', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.matrix = new Matrix4().makeScale(2, 3, 4).toArray();
    emitter.userData.unityParticleQuarks.shapeSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
      directionMode: 'localZ',
      correctWorldSpaceBirthVelocity: true
    };
    emitter.ps.worldSpace = true;
    emitter.ps.looping = true;
    emitter.ps.duration = 0.001;
    emitter.ps.shape = {
      type: 'rectangle', width: 1, height: 1, thickness: 0,
      mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 2 };
    emitter.ps.emissionOverTime = { type: 'ConstantValue', value: 100 };
    emitter.ps.emissionBursts = [];
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;

    expect(() => {
      runtime.update(0.01);
      runtime.update(0.01);
    }).not.toThrow();
    const particles = handle.instance?.systems[0]?.particles ?? [];
    expect(particles.length).toBeGreaterThan(0);
    expect(particles[0]?.velocity.z).toBeCloseTo(8, 6);
    handle.release();
    runtime.dispose();
  });

  it('applies exporter Force and Gravity bases before stock velocity limits', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.forceOverLifetime = {
      schemaVersion: 'unity_particle_quarks_exporter.force_over_lifetime.v1',
      space: 'local',
      basisX: [0, 0, 1], basisY: [1, 0, 0], basisZ: [0, 1, 0],
      x: { mode: 'constant', value: { type: 'ConstantValue', value: 2 } },
      y: { mode: 'constant', value: { type: 'ConstantValue', value: 0 } },
      z: { mode: 'constant', value: { type: 'ConstantValue', value: 0 } }
    };
    emitter.userData.unityParticleQuarks.gravity = {
      schemaVersion: 'unity_particle_quarks_exporter.gravity.v1',
      acceleration: [0, -10, 0],
      modifier: { mode: 'constant', value: { type: 'ConstantValue', value: 1 } }
    };
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.behaviors = [{
      type: 'LimitSpeedOverLife',
      speed: { type: 'ConstantValue', value: 100 },
      dampen: 0
    }];
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    const system = handle.instance?.systems[0];
    expect(system?.behaviors.map((behavior: { type: string }) => behavior.type)).toEqual([
      'UnityParticleQuarksGravity',
      'UnityParticleQuarksForceOverLifetime',
      'LimitSpeedOverLife'
    ]);
    runtime.update(0.1);
    const particle = system?.particles[0];
    expect(particle?.velocity.y).toBeLessThan(0);
    expect(particle?.velocity.z).toBeGreaterThan(0);
    handle.release();
    runtime.dispose();
  });

  it('replaces stock Noise with Unity spatial curl velocity before Limit Velocity', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 2 };
    emitter.userData.unityParticleQuarks.forceOverLifetime = {
      schemaVersion: 'unity_particle_quarks_exporter.force_over_lifetime.v1',
      space: 'local',
      basisX: [1, 0, 0], basisY: [0, 1, 0], basisZ: [0, 0, 1],
      x: { mode: 'constant', value: { type: 'ConstantValue', value: 0 } },
      y: { mode: 'constant', value: { type: 'ConstantValue', value: 0 } },
      z: { mode: 'constant', value: { type: 'ConstantValue', value: 0 } }
    };
    emitter.ps.behaviors = [
      {
        type: 'Noise',
        frequency: { type: 'ConstantValue', value: 0.5 },
        power: { type: 'ConstantValue', value: 0.8 },
        positionAmount: { type: 'ConstantValue', value: 1 },
        rotationAmount: { type: 'ConstantValue', value: 0 }
      },
      {
        type: 'LimitSpeedOverLife',
        speed: { type: 'ConstantValue', value: 100 },
        dampen: 0
      }
    ];
    emitter.userData.unityParticleQuarks.noise = unityNoiseMetadata();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    expect(system?.behaviors.map((behavior: { type: string }) => behavior.type)).toEqual([
      'UnityParticleQuarksNoiseAnimatedVelocityClear',
      'UnityParticleQuarksNoise',
      'UnityParticleQuarksForceOverLifetime',
      'LimitSpeedOverLife'
    ]);
    const particles = system?.particles.slice(0, system.particleNum) ?? [];
    expect(particles).toHaveLength(2);
    expect(Math.hypot(particles[0].velocity.x, particles[0].velocity.z)).toBeGreaterThan(0.001);
    expect(particles[0].velocity.y).toBeCloseTo(0, 10);
    expect(particles[1].velocity.y).toBeCloseTo(0, 10);
    expect(particles[0].velocity.x).toBeCloseTo(particles[1].velocity.x, 10);
    expect(particles[0].velocity.z).toBeCloseTo(particles[1].velocity.z, 10);

    const beforeScroll = [particles[0].velocity.x, particles[0].velocity.z];
    runtime.update(0.1);
    expect(particles[0].velocity.y).toBeCloseTo(0, 10);
    expect(Math.hypot(
      particles[0].velocity.x - beforeScroll[0],
      particles[0].velocity.z - beforeScroll[1]
    )).toBeGreaterThan(0.000001);
    handle.release();
    runtime.dispose();
  });

  it('keeps the authored Noise remap output instead of multiplying it by noise', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.behaviors = [{
      type: 'Noise',
      frequency: { type: 'ConstantValue', value: 1 },
      power: { type: 'ConstantValue', value: 1 },
      positionAmount: { type: 'ConstantValue', value: 1 },
      rotationAmount: { type: 'ConstantValue', value: 0 }
    }];
    emitter.userData.unityParticleQuarks.noise = unityNoiseRemapMetadata(0.5);
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);

    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    expect(particle.velocity.x).toBeCloseTo(0.5, 6);
    expect(particle.velocity.y).toBeCloseTo(0.5, 6);
    expect(particle.velocity.z).toBeCloseTo(0.5, 6);

    const behavior = system.behaviors.find((candidate: any) => candidate.type === 'UnityParticleQuarksNoise');
    const before = particle.velocity.clone();
    particle.position.set(0, 0, 0);
    particle.age = particle.life * 0.75;
    behavior.update(particle, 0);
    expect(particle.velocity.x).toBeCloseTo(before.x, 6);
    expect(particle.velocity.y).toBeCloseTo(before.y, 6);
    expect(particle.velocity.z).toBeCloseTo(before.z, 6);

    handle.release();
    runtime.dispose();
  });

  it('uses normalized noise rather than particle lifetime as the Noise remap curve input', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.behaviors = [{
      type: 'Noise',
      frequency: { type: 'ConstantValue', value: 1 },
      power: { type: 'ConstantValue', value: 1 },
      positionAmount: { type: 'ConstantValue', value: 1 },
      rotationAmount: { type: 'ConstantValue', value: 0 }
    }];
    const metadata = unityNoiseRemapMetadata(0.5) as Record<string, any>;
    const identity = { mode: 'curve', value: linearCurve(0, 1) };
    metadata.remapX = identity;
    metadata.remapY = identity;
    metadata.remapZ = identity;
    emitter.userData.unityParticleQuarks.noise = metadata;
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0);

    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: any) => candidate.type === 'UnityParticleQuarksNoise');
    const before = particle.velocity.clone();
    particle.position.set(0, 0, 0);
    particle.age = particle.life * 0.75;
    behavior.update(particle, 0);
    expect(particle.velocity.x).toBeCloseTo(before.x, 6);
    expect(particle.velocity.y).toBeCloseTo(before.y, 6);
    expect(particle.velocity.z).toBeCloseTo(before.z, 6);

    handle.release();
    runtime.dispose();
  });

  it('replaces stock Limit Velocity with stable TwoCurves runtime evaluation', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 10 };
    emitter.ps.behaviors = [{
      type: 'LimitSpeedOverLife',
      speed: { type: 'ConstantValue', value: 4 },
      dampen: 1
    }];
    emitter.userData.unityParticleQuarks.limitVelocityOverLifetime = {
      schemaVersion: 'unity_particle_quarks_exporter.limit_velocity_over_lifetime.v1',
      limit: {
        mode: 'twoCurves',
        minimum: { type: 'ConstantValue', value: 2 },
        maximum: { type: 'ConstantValue', value: 6 }
      },
      dampen: 1
    };
    const random = vi.spyOn(Math, 'random').mockReturnValue(0.25);
    try {
      const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
      const handle = runtime.spawn('water-impact') as any;
      const system = handle.instance?.systems[0];
      expect(system?.behaviors.map((behavior: { type: string }) => behavior.type)).toEqual([
        'UnityParticleQuarksLimitVelocityOverLifetime'
      ]);

      runtime.update(0.01);
      const particle = system?.particles[0];
      const behavior = system?.behaviors[0];
      particle?.velocity.set(10, 0, 0);
      random.mockReturnValue(0.75);
      behavior?.update(particle, 0.05);
      expect(particle?.velocity.length()).toBeCloseTo(3, 5);

      particle?.velocity.set(10, 0, 0);
      behavior?.update(particle, 0.05);
      expect(particle?.velocity.length()).toBeCloseTo(3, 5);
      handle.release();
      runtime.dispose();
    } finally {
      random.mockRestore();
    }
  });

  it.each([
    { worldSpace: true, emitterScale: 2, expectedStoredVelocity: 100 },
    { worldSpace: false, emitterScale: 2, expectedStoredVelocity: 50 }
  ])('applies Initial Inherit Velocity in $worldSpace storage', async ({
    worldSpace,
    emitterScale,
    expectedStoredVelocity
  }) => {
    const source = unityInheritVelocityFixture(worldSpace, emitterScale);
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    const system = handle.instance?.systems[0];
    runtime.update(0.01);
    handle.setTransform({ position: [1, 0, 0] });
    runtime.update(0.01);

    expect(system?.behaviors[0]?.type).toBe('UnityParticleQuarksInheritVelocityInitial');
    const activeParticles = system?.particles.slice(0, system.particleNum) ?? [];
    const inherited = activeParticles.find((particle: any) =>
      Math.abs(particle.velocity.x - expectedStoredVelocity) < 1e-4);
    expect(inherited).toBeDefined();
    expect(inherited?.velocity.y).toBeCloseTo(0, 6);
    expect(inherited?.velocity.z).toBeCloseTo(0, 6);
    handle.release();
    runtime.dispose();
  });

  it('converts Initial Inherit Velocity through emitter rotation for local storage', async () => {
    const source = unityInheritVelocityFixture(false, 1);
    source.object.children[0].matrix = new Matrix4().makeRotationZ(Math.PI / 2).toArray();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    handle.setTransform({ position: [1, 0, 0] });
    runtime.update(0.01);

    const system = handle.instance?.systems[0];
    const activeParticles = system?.particles.slice(0, system.particleNum) ?? [];
    const inherited = activeParticles.find((particle: any) =>
      Math.abs(particle.velocity.y + 100) < 1e-4);
    expect(inherited).toBeDefined();
    expect(inherited?.velocity.x).toBeCloseTo(0, 6);
    expect(inherited?.velocity.z).toBeCloseTo(0, 6);
    handle.release();
    runtime.dispose();
  });

  it('preserves Initial Inherit Velocity motion baselines across emitter loops', async () => {
    const source = unityInheritVelocityFixture(true, 1);
    source.object.children[0].ps.duration = 0.015;
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    handle.setTransform({ position: [1, 0, 0] });
    runtime.update(0.01);
    handle.setTransform({ position: [2, 0, 0] });
    runtime.update(0.01);

    const system = handle.instance?.systems[0];
    const activeParticles = system?.particles.slice(0, system.particleNum) ?? [];
    expect(activeParticles.length).toBeGreaterThanOrEqual(2);
    expect(activeParticles.filter((particle: any) =>
      Math.abs(particle.velocity.x - 100) < 1e-4).length).toBeGreaterThanOrEqual(2);
    handle.release();
    runtime.dispose();
  });

  it('clears Initial Inherit Velocity motion baselines across pooled restart', async () => {
    const source = unityInheritVelocityFixture(true, 1);
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    let handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    handle.setTransform({ position: [1, 0, 0] });
    runtime.update(0.01);
    let system = handle.instance?.systems[0];
    let activeParticles = system?.particles.slice(0, system.particleNum) ?? [];
    expect(activeParticles.some((particle: any) => Math.abs(particle.velocity.x - 100) < 1e-4)).toBe(true);
    handle.release();

    handle = runtime.spawn('water-impact', { position: [1000, 0, 0] }) as any;
    runtime.update(0.01);
    runtime.update(0.01);
    system = handle.instance?.systems[0];
    activeParticles = system?.particles.slice(0, system.particleNum) ?? [];
    expect(activeParticles.length).toBeGreaterThan(0);
    expect(activeParticles.every((particle: any) => Math.abs(particle.velocity.x) < 1e-6)).toBe(true);
    handle.release();
    runtime.dispose();
  });

  it('uses Unity exponential Limit Velocity damping for partial dampen values', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.behaviors = [{
      type: 'LimitSpeedOverLife',
      speed: { type: 'ConstantValue', value: 2 },
      dampen: 0.5
    }];
    emitter.userData.unityParticleQuarks.limitVelocityOverLifetime = {
      schemaVersion: 'unity_particle_quarks_exporter.limit_velocity_over_lifetime.v1',
      limit: { mode: 'constant', value: { type: 'ConstantValue', value: 2 } },
      dampen: 0.5
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: { type: string }) =>
      candidate.type === 'UnityParticleQuarksLimitVelocityOverLifetime') as any;
    particle.velocity.set(10, 0, 0);
    behavior.update(particle, 1 / 60);
    const firstFactor = 1 - Math.pow(0.5, (1 / 60) * 30);
    expect(particle.velocity.length()).toBeCloseTo(10 + (2 - 10) * firstFactor, 6);
    particle.velocity.set(10, 0, 0);
    behavior.update(particle, 0.05);
    const factor = 1 - Math.pow(0.5, 0.05 * 30);
    expect(particle.velocity.length()).toBeCloseTo(10 + (2 - 10) * factor, 6);
    handle.release();
    runtime.dispose();
  });

  it('converts Current Inherit Velocity into local storage without accumulating it', async () => {
    const source = unityCurrentInheritVelocityFixture(false);
    source.object.children[0].matrix = new Matrix4().makeRotationZ(Math.PI / 2).toArray();
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    handle.setTransform({ position: [1, 0, 0] });
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    expect(particle.velocity.x).toBeCloseTo(0, 6);
    expect(particle.velocity.y).toBeCloseTo(-100, 6);

    handle.setTransform({ position: [2, 0, 0] });
    runtime.update(0.01);
    expect(particle.velocity.x).toBeCloseTo(0, 6);
    expect(particle.velocity.y).toBeCloseTo(-100, 6);

    handle.release();
    runtime.dispose();
  });

  it('preserves Current Inherit Velocity motion history across emitter loops', async () => {
    const source = unityCurrentInheritVelocityFixture(true);
    source.object.children[0].ps.duration = 0.015;
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    handle.setTransform({ position: [1, 0, 0] });
    runtime.update(0.01);
    handle.setTransform({ position: [2, 0, 0] });
    runtime.update(0.01);

    const system = handle.instance?.systems[0];
    const activeParticles = system?.particles.slice(0, system.particleNum) ?? [];
    expect(activeParticles.length).toBeGreaterThanOrEqual(2);
    expect(activeParticles.every((particle: any) =>
      Math.abs(particle.velocity.x - 100) < 1e-4)).toBe(true);

    handle.release();
    runtime.dispose();
  });

  it('clears Current Inherit Velocity motion history on pooled restart', async () => {
    const source = unityCurrentInheritVelocityFixture(true);
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    let handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    handle.setTransform({ position: [1, 0, 0] });
    runtime.update(0.01);
    expect(handle.instance?.systems[0]?.particles[0]?.velocity.x).toBeCloseTo(100, 6);
    handle.release();

    handle = runtime.spawn('water-impact', { position: [1000, 0, 0] }) as any;
    runtime.update(0.01);
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const activeParticles = system?.particles.slice(0, system.particleNum) ?? [];
    expect(activeParticles.length).toBeGreaterThan(0);
    expect(activeParticles.every((particle: any) => Math.abs(particle.velocity.x) < 1e-6)).toBe(true);

    handle.release();
    runtime.dispose();
  });

  it('restores raw HDR Gamma material color after Three JSON color conversion', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.colorSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.gamma_color.v1',
      materialColor: { r: 1.5, g: 0.5, b: 0.2, a: 1.6 }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    const material = handle.instance?.systems[0]?.rendererSettings.material as {
      color: { r: number; g: number; b: number };
      opacity: number;
    };
    expect(material.color.r).toBeCloseTo(1.5, 6);
    expect(material.color.g).toBeCloseTo(0.5, 6);
    expect(material.color.b).toBeCloseTo(0.2, 6);
    expect(material.opacity).toBeCloseTo(1.6, 6);
    handle.release();
    runtime.dispose();
  });

  it('restores Linear material color without sRGB re-decoding and patches Quarks output once', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.colorSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.color.v2',
      sourceProjectColorSpace: 'linear',
      outputColorSpace: 'srgb',
      materialColor: { r: 0.18, g: 0.42, b: 1.25, a: 0.8 }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    const material = handle.instance?.systems[0]?.rendererSettings.material as {
      color: { r: number; g: number; b: number };
      opacity: number;
    };
    expect(material.color.r).toBeCloseTo(0.18, 6);
    expect(material.color.g).toBeCloseTo(0.42, 6);
    expect(material.color.b).toBeCloseTo(1.25, 6);
    expect(material.opacity).toBeCloseTo(0.8, 6);
    const batches = (runtime as any).batchRenderer.batches as Array<{
      material: { fragmentShader: string; userData: Record<string, unknown> };
    }>;
    expect(batches).toHaveLength(1);
    expect(fragmentOccurrences(batches[0]?.material.fragmentShader ?? '', '#include <colorspace_pars_fragment>')).toBe(1);
    expect(fragmentOccurrences(batches[0]?.material.fragmentShader ?? '', '#include <colorspace_fragment>')).toBe(1);
    expect(batches[0]?.material.userData.unityParticleQuarksLinearColorSpace).toBe(true);
    handle.release();
    runtime.dispose();
  });

  it('applies separate-axis Limit Velocity curves in paired runtime', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.behaviors = [{ type: 'LimitSpeedOverLife', speed: { type: 'ConstantValue', value: 100 }, dampen: 1 }];
    emitter.userData.unityParticleQuarks.limitVelocityOverLifetime = {
      schemaVersion: 'unity_particle_quarks_exporter.limit_velocity_over_lifetime.v3',
      separateAxes: true,
      limitX: { mode: 'constant', value: { type: 'ConstantValue', value: 2 } },
      limitY: { mode: 'constant', value: { type: 'ConstantValue', value: 3 } },
      limitZ: { mode: 'constant', value: { type: 'ConstantValue', value: 4 } },
      dampen: 1
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    const system = handle.instance?.systems[0];
    runtime.update(0.01);
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: { type: string }) => candidate.type === 'UnityParticleQuarksLimitVelocityOverLifetime');
    particle?.velocity.set(10, -10, 10);
    behavior?.update(particle, 0.05);
    expect(particle?.velocity.x).toBeCloseTo(2, 5);
    expect(particle?.velocity.y).toBeCloseTo(-3, 5);
    expect(particle?.velocity.z).toBeCloseTo(4, 5);
    handle.release();
    runtime.dispose();
  });

  it('preserves stock exporter PBR material types so Quarks builds a lit mesh batch', async () => {
    const source = exporterFixture();
    source.materials[0].type = 'MeshStandardMaterial';
    source.materials[0].roughness = 1;
    source.materials[0].metalness = 0;
    const emitter = source.object.children[0];
    emitter.ps.renderMode = 2;
    emitter.userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock'
    };

    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const batches = (runtime as any).batchRenderer.batches as Array<{
      settings: { material: { type: string } };
      material: { isMeshStandardMaterial?: boolean; isShaderMaterial?: boolean };
    }>;
    expect(batches).toHaveLength(1);
    expect(batches[0]?.settings.material.type).toBe('MeshStandardMaterial');
    expect(batches[0]?.material.isMeshStandardMaterial).toBe(true);
    expect(batches[0]?.material.isShaderMaterial).not.toBe(true);
    runtime.dispose();
  });

  it('injects every exporter fragment profile once while batching identical profiles', async () => {
    const profiles = [
      ['legacySoftAdditive', 'diffuseColor.rgb *= diffuseColor.a;'],
      ['hovlAdditivePremultiply', 'diffuseColor.rgb *= diffuseColor.a;'],
      ['invisibleFallback', 'diffuseColor = vec4(0.0);'],
      ['legacyAlphaPremultiply', 'diffuseColor *= vColor.a;'],
      ['legacyMultiply', 'diffuseColor = mix(vec4(1.0), diffuseColor, diffuseColor.a);'],
      ['legacyMultiplyDouble', 'vec4 unityParticleQuarksSourceColor = diffuseColor;']
    ] as const;

    for (const [mode, marker] of profiles) {
      const source = unityMaterialSemanticsFixture(mode);
      const runtime = await readyRuntime({ prewarm: 2, max: 2 }, 'drop-newest', undefined, source);
      const batches = (runtime as any).batchRenderer.batches as Array<{
        material: { fragmentShader: string; userData: Record<string, unknown> };
      }>;
      expect(batches).toHaveLength(1);
      expect(fragmentOccurrences(batches[0]?.material.fragmentShader ?? '', marker)).toBe(1);
      expect(batches[0]?.material.userData.unityParticleQuarksFragmentColorMode).toBe(mode);
      if (mode === 'invisibleFallback') {
        expect((batches[0]?.material as any).transparent).toBe(true);
        expect((batches[0]?.material as any).depthWrite).toBe(false);
        expect((batches[0]?.material as any).opacity).toBe(0);
      }
      runtime.dispose();
    }
  });

  it('keeps incompatible exporter fragment profiles in separate Quarks batches', async () => {
    const source = unityMaterialSemanticsFixture('legacySoftAdditive');
    const second = structuredClone(source.object.children[0]);
    second.uuid = '00000000-0000-4000-8000-000000000002';
    second.name = 'Different material formula';
    second.userData.unityParticleQuarks.materialSemantics.fragmentColorMode = 'legacyMultiply';
    source.object.children.push(second);

    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const batches = (runtime as any).batchRenderer.batches as Array<{
      material: { fragmentShader: string; userData: Record<string, unknown> };
    }>;
    expect(batches).toHaveLength(2);
    expect(batches.map((batch) => batch.material.userData.unityParticleQuarksFragmentColorMode).sort()).toEqual([
      'legacyMultiply',
      'legacySoftAdditive'
    ]);
    expect(batches.filter((batch) => batch.material.fragmentShader.includes('diffuseColor.rgb *= diffuseColor.a;'))).toHaveLength(1);
    expect(batches.filter((batch) => batch.material.fragmentShader.includes('mix(vec4(1.0), diffuseColor'))).toHaveLength(1);
    runtime.dispose();
  });

  it('normalizes Start Color Gradient time and samples Random Color from the full gradient', async () => {
    const gradientSource = unityStartColorFixture('gradient');
    let runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, gradientSource);
    let handle = runtime.spawn('water-impact') as any;
    let generator = handle.instance?.systems[0]?.startColor as any;
    let memory: unknown[] = [];
    let color = new QuarksVector4();
    generator.startGen(memory);
    generator.genColor(memory, color, 5);
    expect(color.x).toBeCloseTo(0.5, 6);
    expect(color.y).toBeCloseTo(0.5, 6);
    expect(color.z).toBeCloseTo(0.5, 6);
    handle.release();
    runtime.dispose();

    const random = vi.spyOn(Math, 'random').mockReturnValue(0.5);
    try {
      const randomSource = unityStartColorFixture('randomColor');
      runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, randomSource);
      handle = runtime.spawn('water-impact') as any;
      generator = handle.instance?.systems[0]?.startColor as any;
      memory = [];
      color = new QuarksVector4();
      generator.startGen(memory);
      generator.genColor(memory, color);
      expect(color.x).toBeCloseTo(0.5, 6);
      expect(color.y).toBeCloseTo(0.5, 6);
      expect(color.z).toBeCloseTo(0.5, 6);
      handle.release();
      runtime.dispose();
    } finally {
      random.mockRestore();
    }
  });

  it('keeps versioned exporter material profile metadata explicit', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'legacyMultiply',
      profileId: 'builtin.particleMultiply',
      profileMetadataKey: 'unity_particle_quarks_exporter.material.builtin.particleMultiply.v1'
    };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const batches = (runtime as any).batchRenderer.batches as Array<{
      settings: { material: { type: string } };
    }>;
    expect(batches[0]?.settings.material.type).toContain('builtin.particleMultiply');
    runtime.dispose();
  });

  it('applies exporter alpha, blend, and depth metadata to the Quarks material', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock'
    };
    source.object.children[0].userData.unityParticleQuarks.materialAlpha = {
      schemaVersion: 'unity_particle_quarks_exporter.material.alpha.v1',
      base: { property: '_MainTex', channel: 'r' },
      clip: { enabled: true, threshold: 0.35 }
    };
    source.object.children[0].userData.unityParticleQuarks.materialTextureUv = {
      schemaVersion: 'unity_particle_quarks_exporter.material_texture_uv.v1',
      main: {
        property: '_MainTex',
        scale: [1, 0.25],
        offset: [0.1, 0.2],
        panning: [0.3, 0.4]
      }
    };
    source.object.children[0].userData.unityParticleQuarks.materialBlend = {
      schemaVersion: 'unity_particle_quarks_exporter.material.blend.v1',
      mode: 'custom',
      src: 204,
      dst: 205,
      equation: 100,
      srcAlpha: 201,
      dstAlpha: 205,
      equationAlpha: 100,
      customAlpha: true,
      premultiplied: false,
      zWrite: false
    };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const material = (runtime as any).batchRenderer.batches[0]?.material as any;
    expect(material.alphaTest).toBeCloseTo(0.35, 6);
    expect(material.blendSrc).toBe(204);
    expect(material.blendDst).toBe(205);
    expect(material.blendSrcAlpha).toBe(201);
    expect(material.blendDstAlpha).toBe(205);
    expect(material.depthWrite).toBe(false);
    expect(material.transparent).toBe(true);
    expect(material.fragmentShader).toContain('diffuseColor.rgb *= texelColor.rgb;');
    expect(material.fragmentShader).toContain('diffuseColor.a *= texelColor.r;');
    expect(material.fragmentShader).not.toContain('diffuseColor.a = diffuseColor.r;');
    expect(material.fragmentShader).toContain('unityParticleQuarksMainUvTransform');
    expect(material.fragmentShader).toContain('vUv * unityParticleQuarksMainUvTransform.xy');
    expect(material.uniforms.unityParticleQuarksMainUvTransform.value.toArray()).toEqual([1, 0.25, 0.1, 0.2]);
    expect(material.uniforms.unityParticleQuarksMainUvPanning.value.toArray()).toEqual([0.3, 0.4]);
    runtime.update(0.1);
    expect(material.uniforms.unityParticleQuarksTime.value).toBeCloseTo(0.1, 6);
    runtime.dispose();
  });

  it('scopes low-speed stretched billboard tolerance to vehicle profiles', async () => {
    const vehicle = exporterFixture();
    const vehicleEmitter = vehicle.object.children[0];
    vehicleEmitter.ps.renderMode = 1;
    vehicleEmitter.ps.rendererEmitterSettings = { speedFactor: 0, lengthFactor: 8 };
    vehicleEmitter.userData.unityParticleQuarks.materialProfile = {
      schemaVersion: 'unity_particle_quarks_exporter.material.profile.v1',
      profileId: 'custom.vehicle.effect',
      profileVersion: 'v1',
      sourceShader: 'Effect/Add_Blend_UPR',
      runtimeTier: 'paired',
      fidelity: 'exact'
    };
    vehicleEmitter.userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock',
      profileId: 'custom.vehicle.effect',
      profileMetadataKey: 'unity_particle_quarks_exporter.material.custom.vehicle.effect.v1'
    };
    let runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, vehicle);
    let vehicleShader = (runtime as any).batchRenderer.batches[0]?.material?.vertexShader as string;
    expect(vehicleShader).toContain('if (vlength > 0.000000001)');
    runtime.dispose();

    const generic = exporterFixture();
    const genericEmitter = generic.object.children[0];
    genericEmitter.ps.renderMode = 1;
    genericEmitter.ps.rendererEmitterSettings = { speedFactor: 0, lengthFactor: 8 };
    genericEmitter.userData.unityParticleQuarks.materialProfile = {
      schemaVersion: 'unity_particle_quarks_exporter.material.profile.v1',
      profileId: 'custom.hovl.particles',
      profileVersion: 'v1',
      sourceShader: 'Hovl/Particles/Add_CenterGlow',
      runtimeTier: 'paired',
      fidelity: 'exact'
    };
    genericEmitter.userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock',
      profileId: 'custom.hovl.particles',
      profileMetadataKey: 'unity_particle_quarks_exporter.material.custom.hovl.particles.v1'
    };
    runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, generic);
    const genericShader = (runtime as any).batchRenderer.batches[0]?.material?.vertexShader as string;
    expect(genericShader).toContain('if (vlength > 0.00001)');
    expect(genericShader).not.toContain('if (vlength > 0.000000001)');
    runtime.dispose();
  });

  it('applies ShaderGraph particle red as the base-color mask', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock',
      profileId: 'custom.shadergraph.particle',
      profileMetadataKey: 'unity_particle_quarks_exporter.material.custom.shadergraph.particle.v1',
      baseColorChannel: 'r'
    };
    source.object.children[0].userData.unityParticleQuarks.materialAlpha = {
      schemaVersion: 'unity_particle_quarks_exporter.material.alpha.v1',
      base: { property: 'Texture2D_F593E37E', channel: 'a' },
      clip: { enabled: false, threshold: 0 }
    };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const material = (runtime as any).batchRenderer.batches[0]?.material as any;
    expect(material.fragmentShader).toContain('diffuseColor.rgb *= vec3(texelColor.r);');
    expect(material.fragmentShader).toContain('diffuseColor.a *= texelColor.a;');
    runtime.dispose();
  });

  it('preserves authored transparency for normal Unity alpha materials with depth write', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock'
    };
    emitter.userData.unityParticleQuarks.materialBlend = {
      schemaVersion: 'unity_particle_quarks_exporter.material.blend.v1',
      mode: 'normal',
      src: 201,
      dst: 0,
      equation: 100,
      srcAlpha: 201,
      dstAlpha: 0,
      equationAlpha: 100,
      customAlpha: false,
      premultiplied: false,
      zWrite: true
    };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const material = (runtime as any).batchRenderer.batches[0]?.material as any;
    expect(material.transparent).toBe(true);
    runtime.dispose();
  });

  it('enables alpha blending for stretched particles that consume particle color alpha', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    const material = source.materials[0];
    material.transparent = false;
    material.depthWrite = true;
    emitter.ps.renderMode = 1;
    emitter.ps.rendererEmitterSettings = { speedFactor: 0, lengthFactor: 1 };
    emitter.userData.unityParticleQuarks.materialAlpha = {
      schemaVersion: 'unity_particle_quarks_exporter.material.alpha.v1',
      materialColorAlpha: true,
      particleColorAlpha: true,
      base: { source: 'constant' },
      clip: { enabled: false, threshold: 0 }
    };
    emitter.userData.unityParticleQuarks.materialBlend = {
      schemaVersion: 'unity_particle_quarks_exporter.material.blend.v1',
      mode: 'normal',
      src: 201,
      dst: 0,
      equation: 100,
      srcAlpha: 201,
      dstAlpha: 0,
      equationAlpha: 100,
      customAlpha: false,
      premultiplied: false,
      zWrite: true
    };

    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const batchMaterial = (runtime as any).batchRenderer.batches[0]?.material as any;
    expect(batchMaterial.transparent).toBe(true);
    runtime.dispose();
  });

  it('applies Unity renderer pivot in the batch vertex shader', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.rendererPivot = {
      schemaVersion: 'unity_particle_quarks_exporter.renderer_pivot.v1',
      sourceRenderMode: 'Billboard',
      value: [0.1, -0.48, 0.2],
      geometryOffset: [0.1, -0.48, -0.2]
    };

    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const material = (runtime as any).batchRenderer.batches[0]?.material as any;
    expect(material.vertexShader).toContain(
      '(position.xy + unityParticleQuarksRendererPivot.xy) * size.xy'
    );
    expect(material.uniforms.unityParticleQuarksRendererPivot.value.toArray()).toEqual([0.1, -0.48, -0.2]);
    expect(material.userData.unityParticleQuarksRendererPivot).toEqual([0.1, -0.48, 0.2]);
    expect(material.userData.unityParticleQuarksRendererPivotGeometryOffset).toEqual([0.1, -0.48, -0.2]);
    runtime.dispose();
  });

  it('evaluates the RockDissolve ShaderGraph with Unity Custom1 and Custom2 streams', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    const constant = (value: number) => ({
      mode: 'constant',
      value: { type: 'ConstantValue', value }
    });
    emitter.userData.unityParticleQuarks.materialProfile = {
      schemaVersion: 'unity_particle_quarks_exporter.material.profile.v1',
      profileId: 'custom.shadergraph.rockDissolve',
      profileVersion: 'v1',
      sourceShader: 'Shader Graphs/Fx_RockDissolve',
      runtimeTier: 'paired',
      fidelity: 'exact'
    };
    emitter.userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock',
      profileId: 'custom.shadergraph.rockDissolve',
      profileMetadataKey: 'unity_particle_quarks_exporter.material.custom.shadergraph.rockDissolve.v1'
    };
    emitter.userData.unityParticleQuarks.materialAlpha = {
      schemaVersion: 'unity_particle_quarks_exporter.material.alpha.v1',
      base: { property: '_MainTex', channel: 'a' },
      clip: { enabled: false, threshold: 0 }
    };
    emitter.userData.unityParticleQuarks.materialShaderParameters = {
      schemaVersion: 'unity_particle_quarks_exporter.material.shader_parameters.v1',
      profile: 'custom.shadergraph.rockDissolve',
      colorOperation: 'rockDissolveVertexCustomDataLerp',
      alphaOperation: 'rockDissolveClip'
    };
    emitter.userData.unityParticleQuarks.customData = {
      schemaVersion: 'unity_particle_quarks_exporter.custom_data.v1',
      custom1: {
        mode: 'vector',
        components: [constant(0.25), constant(0.5), constant(0.75), constant(1)]
      },
      custom2: {
        mode: 'color',
        value: {
          type: 'ConstantColor',
          color: { r: 0.17, g: 0.23, b: 0.31, a: 0.8 }
        }
      }
    };

    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    runtime.update(0.01);
    const system = handle.instance?.systems[0] as any;
    const particle = system?.particles[0] as any;
    expect(system.behaviors.some((behavior: any) => behavior.type === 'UnityParticleQuarksCustomData')).toBe(true);
    expect(particle.__unityParticleQuarksCustom1.toArray()).toEqual([0.25, 0.5, 0.75, 1]);
    expect(particle.__unityParticleQuarksCustom2.toArray()).toEqual([0.17, 0.23, 0.31, 0.8]);

    const batch = (runtime as any).batchRenderer.batches[0] as any;
    expect(batch.material.vertexShader).toContain('attribute vec4 unityParticleQuarksCustom1;');
    expect(batch.material.fragmentShader).toContain(
      'texelColor.a - clamp(unityParticleQuarksCustom1Varying.x - texelColor.g'
    );
    expect(batch.material.fragmentShader).toContain(
      'mix(vColor.rgb * texelColor.r, unityParticleQuarksCustom2Varying.rgb * texelColor.r'
    );
    expect(batch.geometry.getAttribute('unityParticleQuarksCustom1').array.slice(0, 4)).toEqual(
      new Float32Array([0.25, 0.5, 0.75, 1])
    );
    expect(batch.geometry.getAttribute('unityParticleQuarksCustom2').array.slice(0, 4)).toEqual(
      new Float32Array([0.17, 0.23, 0.31, 0.8])
    );
    handle.release();
    runtime.dispose();
  });

  it('rejects unknown exporter material profile metadata', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock',
      profileId: 'builtin.particleMultiplyCopy'
    };
    await expect(readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source))
      .rejects.toThrow('Unsupported exporter material profile');
  });

  it('accepts the built-in Sprite profile used by Sprites/Default particle materials', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialProfile = {
      schemaVersion: 'unity_particle_quarks_exporter.material.profile.v1',
      profileId: 'builtin.sprite',
      profileVersion: 'v1',
      sourceShader: 'Sprites/Default',
      runtimeTier: 'stock',
      fidelity: 'approx'
    };
    source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock'
    };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    expect((runtime as any).batchRenderer.batches[0]?.material?.type).toBe('ShaderMaterial');
    runtime.dispose();
  });

  it('accepts the built-in Unlit/Color profile emitted by the Unity exporter', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialProfile = {
      schemaVersion: 'unity_particle_quarks_exporter.material.profile.v1',
      profileId: 'builtin.unlitNoVertexColor',
      profileVersion: 'v1',
      sourceShader: 'Unlit/Color',
      runtimeTier: 'stock',
      fidelity: 'approx'
    };
    source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock'
    };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    expect((runtime as any).batchRenderer.batches[0]?.material).toBeDefined();
    runtime.dispose();
  });

  it('accepts the built-in Standard metallic profile used by lit particle materials', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialProfile = {
      schemaVersion: 'unity_particle_quarks_exporter.material.profile.v1',
      profileId: 'builtin.standardMetallic',
      profileVersion: 'v1',
      sourceShader: 'Standard',
      runtimeTier: 'stock',
      fidelity: 'exact'
    };
    source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock'
    };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    expect((runtime as any).batchRenderer.batches[0]?.material).toBeDefined();
    runtime.dispose();
  });

  it('rejects unknown top-level exporter material profile metadata', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialProfile = {
      schemaVersion: 'unity_particle_quarks_exporter.material.profile.v1',
      profileId: 'urp.particleUnlitCopy',
      profileVersion: 'v1',
      sourceShader: 'Universal Render Pipeline/Particles/Unlit',
      runtimeTier: 'stock',
      fidelity: 'approx'
    };
    await expect(readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source))
      .rejects.toThrow('Malformed exporter material profile metadata');
  });

  it('multiplies ParticleSystem Color over Lifetime into Unity trail color', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    const gradient = (alphaStart: number, alphaEnd: number) => ({
      type: 'Gradient',
      color: {
        type: 'CLinearFunction', subType: 'Color',
        keys: [
          { value: { r: 1, g: 1, b: 1 }, pos: 0 },
          { value: { r: 1, g: 1, b: 1 }, pos: 1 }
        ]
      },
      alpha: {
        type: 'CLinearFunction', subType: 'Number',
        keys: [{ value: alphaStart, pos: 0 }, { value: alphaEnd, pos: 1 }]
      }
    });
    emitter.ps.renderMode = 3;
    emitter.ps.rendererEmitterSettings = {
      startLength: { type: 'ConstantValue', value: 60 },
      followLocalOrigin: false
    };
    emitter.ps.startColor = {
      type: 'ConstantColor', color: { r: 1, g: 1, b: 1, a: 0.5 }
    };
    emitter.ps.behaviors = [{
      type: 'ColorOverLife',
      color: { type: 'ConstantColor', color: { r: 1, g: 1, b: 1, a: 0.8 } }
    }];
    emitter.userData.unityParticleQuarks.trailInheritParticleColor = {
      schemaVersion: 'unity_particle_quarks_exporter.trail_inherit_particle_color.v1',
      particleColorOverLifetime: gradient(0, 1)
    };

    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: any) =>
      candidate.type === 'UnityParticleQuarksTrailInheritParticleColor');
    expect(behavior).toBeDefined();
    expect(system?.behaviors.some((candidate: any) => candidate.type === 'ColorOverLife')).toBe(false);
    particle.age = 0.5;
    particle.life = 1;
    particle.startColor.set(1, 1, 1, 0.5);
    behavior.update(particle, 0);
    expect(particle.color.w).toBeCloseTo(0.2, 6);
    handle.release();
    runtime.dispose();
  });

  it('renders a particle head companion from the authoritative trail particle state', async () => {
    const source = unityTrailFixture();
    const emitter = source.object.children[0];
    const geometry = source.geometries[0].uuid;
    const material = source.materials[0].uuid;
    emitter.userData.unityParticleQuarks.particleHead = {
      schemaVersion: 'unity_particle_quarks_exporter.particle_head.v1',
      geometry,
      material,
      renderMode: 0,
      renderOrder: 7,
      layers: 1,
      uTileCount: 1,
      vTileCount: 1,
      blendTiles: false,
      softParticles: false,
      softFarFade: 1,
      softNearFade: 0,
      worldSpace: false,
      rotation: { alignment: 'billboard', preserveAuthored: true }
    };
    emitter.userData.unityParticleQuarks.particleHead.materialColor = { r: 1, g: 1, b: 1, a: 0 };
    emitter.userData.unityParticleQuarks.particleHead.restoreMaterialColor = false;
    emitter.userData.unityParticleQuarks.rendererPivot = {
      schemaVersion: 'unity_particle_quarks_exporter.renderer_pivot.v1',
      sourceRenderMode: 'Billboard',
      value: [0, -0.48, 0],
      geometryOffset: [0, -0.48, 0]
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const instance = handle.instance as any;
    const system = instance.systems[0];
    expect(instance.companionHeadBatches).toHaveLength(1);
    expect(instance.systems).toHaveLength(1);
    expect(instance.companionHeadBatches[0].batch.systems.has(system)).toBe(true);
    expect(instance.companionHeadBatches[0].batch.settings.softParticles).toBe(false);
    expect(instance.companionHeadBatches[0].batch.material.fragmentShader).toContain(
      'diffuseColor.a *= unityParticleQuarksHeadMaterialAlpha;'
    );
    expect(instance.companionHeadBatches[0].batch.material.uniforms.unityParticleQuarksHeadMaterialAlpha.value).toBe(0);
    expect(instance.companionHeadBatches[0].batch.geometry.instanceCount).toBe(system.particleNum);
    expect(instance.companionHeadBatches[0].batch.material.vertexShader).toContain(
      '(position.xy + unityParticleQuarksRendererPivot.xy) * size.xy'
    );
    expect(instance.companionHeadBatches[0].batch.material.userData.unityParticleQuarksRendererPivot).toEqual([0, -0.48, 0]);
    expect((runtime as any).batchRenderer.batches[0].material.userData.unityParticleQuarksRendererPivot).toBeUndefined();
    expect(system.particles[0].color.w).toBeGreaterThan(0);
    handle.release();
    const reused = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    expect((reused.instance as any).companionHeadBatches).toHaveLength(1);
    reused.release();
    runtime.dispose();
  });

  it('renders Unity Local Billboard particles through an authored-orientation Mesh batch', async () => {
    const source = unityTrailFixture();
    const emitter = source.object.children[0];
    emitter.ps.renderMode = 0;
    emitter.userData.unityParticleQuarks.rendererAlignment = {
      schemaVersion: 'unity_particle_quarks_exporter.renderer_alignment.v1',
      mode: 'local',
      preserveAuthored: true,
      simulationSpace: 'local'
    };
    emitter.userData.unityParticleQuarks.rendererPivot = {
      schemaVersion: 'unity_particle_quarks_exporter.renderer_pivot.v1',
      sourceRenderMode: 'Billboard',
      value: [0, -0.48, 0],
      geometryOffset: [0, -0.48, 0]
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const instance = handle.instance as any;
    const system = instance.systems[0];
    expect(system.renderMode).toBe(2);
    expect(system.particles[0].rotation).toBeInstanceOf(QuarksQuaternion);
    expect((runtime as any).batchRenderer.batches[0].material.vertexShader).toContain(
      'matrix * vec4( position + unityParticleQuarksRendererPivot, 1.0 )'
    );
    handle.release();
    runtime.dispose();
  });

  it('uses independent stretched head settings without mutating the authoritative trail renderer', async () => {
    const source = unityTrailFixture();
    const emitter = source.object.children[0];
    const geometry = source.geometries[0].uuid;
    const material = source.materials[0].uuid;
    emitter.userData.unityParticleQuarks.particleHead = {
      schemaVersion: 'unity_particle_quarks_exporter.particle_head.v1',
      geometry,
      material,
      renderMode: 1,
      renderOrder: 7,
      layers: 1,
      uTileCount: 1,
      vTileCount: 1,
      blendTiles: false,
      softParticles: false,
      softFarFade: 1,
      softNearFade: 0,
      worldSpace: false,
      rendererEmitterSettings: { speedFactor: 0.25, lengthFactor: 2 },
      rotation: { alignment: 'velocity', preserveAuthored: true }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    const instance = handle.instance as any;
    const system = instance.systems[0];
    const trailSettings = system.rendererEmitterSettings;
    runtime.update(0.01);

    expect(instance.companionHeadBatches[0].batch.settings.renderMode).toBe(1);
    expect(instance.companionHeadBatches[0].rendererEmitterSettings).toEqual({ speedFactor: 0.25, lengthFactor: 2 });
    expect(system.rendererEmitterSettings).toBe(trailSettings);
    expect(instance.companionHeadBatches[0].batch.geometry.instanceCount).toBe(system.particleNum);

    handle.release();
    runtime.dispose();
  });

  it('initializes mesh-head rotation on the same trail particles', async () => {
    const source = unityTrailFixture();
    const emitter = source.object.children[0];
    emitter.ps.startRotation = {
      type: 'Euler',
      angleX: { type: 'ConstantValue', value: 0.1 },
      angleY: { type: 'ConstantValue', value: 0.2 },
      angleZ: { type: 'ConstantValue', value: 0.3 },
      eulerOrder: 'XYZ'
    };
    emitter.userData.unityParticleQuarks.particleHead = {
      schemaVersion: 'unity_particle_quarks_exporter.particle_head.v1',
      geometry: source.geometries[0].uuid,
      material: source.materials[0].uuid,
      renderMode: 2,
      renderOrder: 3,
      layers: 1,
      uTileCount: 1,
      vTileCount: 1,
      blendTiles: false,
      softParticles: false,
      softFarFade: 1,
      softNearFade: 0,
      worldSpace: false,
      rotation: { alignment: 'local', preserveAuthored: true }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const instance = handle.instance as any;
    const system = instance.systems[0];
    const particle = system.particles[0];
    expect(instance.companionHeadBatches[0].batch.settings.renderMode).toBe(2);
    expect(particle.rotation).toBeInstanceOf(QuarksQuaternion);
    expect(instance.companionHeadBatches[0].batch.geometry.instanceCount).toBe(1);
    handle.release();
    runtime.dispose();
  });

  it('applies Unity Limit Velocity drag using the squared-speed formula', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.behaviors = [{
      type: 'LimitSpeedOverLife',
      speed: { type: 'ConstantValue', value: 100 },
      dampen: 0
    }];
    emitter.userData.unityParticleQuarks.limitVelocityOverLifetime = {
      schemaVersion: 'unity_particle_quarks_exporter.limit_velocity_over_lifetime.v2',
      limit: null,
      dampen: 0,
      drag: { mode: 'constant', value: { type: 'ConstantValue', value: 0.01 } },
      multiplyDragByParticleSize: false,
      multiplyDragByParticleVelocity: true
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: any) =>
      candidate.type === 'UnityParticleQuarksLimitVelocityOverLifetime');
    expect(behavior).toBeDefined();
    particle.velocity.set(10, 0, 0);
    behavior.update(particle, 0.5);
    expect(particle.velocity.x).toBeCloseTo(9.5, 5);
    expect(particle.velocity.y).toBeCloseTo(0, 5);
    handle.release();
    runtime.dispose();
  });

  it('syncs Unity particle Point Lights with color, size, alpha, scale, and maxLights', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.startSize = {
      type: 'Vector3Function',
      x: { type: 'ConstantValue', value: 2 },
      y: { type: 'ConstantValue', value: 8 },
      z: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startColor = {
      type: 'ConstantColor',
      color: { r: 0.4, g: 0.2, b: 0.6, a: 0.5 }
    };
    emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 3 };
    emitter.userData.unityParticleQuarks.lights = unityLightsMetadata({
      maxLights: 1,
      uses3DSize: true,
      meshSize: false,
      particleColorMultiplier: { r: 2, g: 0.5, b: 1, a: 2 },
      shadowMode: 'soft'
    });

    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact', { scale: 2 }) as any;
    runtime.update(0.01);
    const lights: PointLight[] = [];
    handle.instance?.root.traverse((object: any) => {
      if (object.isPointLight) lights.push(object as PointLight);
    });

    expect(lights).toHaveLength(1);
    expect(lights[0]?.visible).toBe(true);
    expect(lights[0]?.distance).toBeCloseTo(80, 6);
    expect(lights[0]?.intensity).toBeCloseTo(0.5, 6);
    expect(lights[0]?.color.toArray()).toEqual([0.2, 0.4, 0.6]);
    expect(lights[0]?.castShadow).toBe(true);
    expect(lights[0]?.layers.mask).toBe(5);

    handle.release();
    expect(lights[0]?.visible).toBe(false);
    expect(lights[0]?.intensity).toBe(0);
    runtime.dispose();
  });

  it('applies Unity orbital velocity around the exported module origin', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    const constant = (value: number) => ({
      mode: 'constant',
      value: { type: 'ConstantValue', value }
    });
    emitter.userData.unityParticleQuarks.velocityOverLifetime = {
      schemaVersion: 'unity_particle_quarks_exporter.velocity_over_lifetime.v2',
      space: 'local',
      basisX: [1, 0, 0],
      basisY: [0, 1, 0],
      basisZ: [0, 0, 1],
      origin: [0, 0, 0],
      x: constant(0), y: constant(0), z: constant(0),
      orbitalX: constant(0), orbitalY: constant(90), orbitalZ: constant(0),
      orbitalOffsetX: constant(0), orbitalOffsetY: constant(0), orbitalOffsetZ: constant(0),
      radial: constant(0), speedModifier: constant(1)
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: any) =>
      candidate.type === 'UnityParticleQuarksVelocityOverLifetime');
    expect(behavior).toBeDefined();
    particle.position.set(1, 0, 0);
    particle.age = 0.5;
    behavior.update(particle, 1);
    expect(Math.hypot(particle.position.x, particle.position.y, particle.position.z)).toBeCloseTo(1, 5);
    expect(Math.abs(particle.position.z)).toBeGreaterThan(0.9);
    handle.release();
    runtime.dispose();
  });

  it('applies v2 SingleRow frame selection and FPS timing', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.uTileCount = 4;
    emitter.ps.vTileCount = 3;
    emitter.userData.unityParticleQuarks.textureSheetAnimation = {
      schemaVersion: 'unity_particle_quarks_exporter.texture_sheet_animation.v2',
      mode: 'grid',
      animation: 'singleRow',
      timeMode: 'fps',
      frameCount: 4,
      tileCountX: 4,
      tileCountY: 3,
      cycleCount: 1,
      fps: 8,
      speedRange: [0, 1],
      rowMode: 'custom',
      rowIndex: 2,
      frameOverTime: { mode: 'curve', value: linearCurve(0, 1) },
      startFrame: { mode: 'constant', value: { type: 'ConstantValue', value: 0 } },
      sprites: []
    };
    emitter.ps.behaviors.push({ type: 'FrameOverLife', frame: linearCurve(0, 4) });
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    const behavior = system?.behaviors.find((candidate: any) =>
      candidate.type === 'UnityParticleQuarksTextureSheetAnimation');
    expect(behavior).toBeDefined();
    particle.age = 0.75;
    behavior.update(particle);
    expect(particle.uvTile).toBe(10);
    handle.release();
    runtime.dispose();
  });

  it('applies sprite-list frame metadata to Quarks shader uniforms and geometry', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.userData.unityParticleQuarks.textureSheetAnimation = {
      schemaVersion: 'unity_particle_quarks_exporter.texture_sheet_animation.v2',
      mode: 'sprites',
      animation: 'sprites',
      timeMode: 'lifetime',
      frameCount: 2,
      tileCountX: 1,
      tileCountY: 1,
      cycleCount: 1,
      fps: 0,
      speedRange: [0, 1],
      rowMode: 'custom',
      rowIndex: 0,
      frameOverTime: { mode: 'curve', value: linearCurve(0, 1) },
      startFrame: { mode: 'constant', value: { type: 'ConstantValue', value: 0 } },
      sprites: [
        { rect: [0, 0, 0.5, 1], sizeMul: [1, 1], pivot: [0, 0] },
        { rect: [0.5, 0, 0.5, 1], sizeMul: [1.25, 0.75], pivot: [-0.25, 0.1] }
      ]
    };
    emitter.ps.behaviors.push({ type: 'FrameOverLife', frame: linearCurve(0, 2) });
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const batches = (runtime as any).batchRenderer.batches as Array<{
      material: {
        vertexShader: string;
        uniforms: Record<string, { value: unknown }>;
        userData: Record<string, unknown>;
      };
    }>;
    expect(batches).toHaveLength(1);
    const spriteBatch = batches[0]!;
    expect(spriteBatch.material.userData.unityParticleQuarksSpriteFrames).toBe(2);
    expect((spriteBatch.material.uniforms.unityParticleQuarksSpriteRects!.value as unknown[])).toHaveLength(2);
    expect((spriteBatch.material.uniforms.unityParticleQuarksSpriteGeometry!.value as unknown[])).toHaveLength(2);
    expect(spriteBatch.material.vertexShader).toContain('unityParticleQuarksSpriteTileTransform');
    expect(spriteBatch.material.vertexShader).toContain('unityParticleQuarksFrameGeometry');
    const compiledShader = {
      vertexShader: [
        'mat3 tileTransform = makeTileTransform(floor(uvTile));',
        'mat3 nextTileTransform = makeTileTransform(ceil(uvTile));'
      ].join('\n'),
      fragmentShader: '',
      uniforms: {}
    };
    (spriteBatch.material as any).onBeforeCompile(compiledShader, {});
    expect(compiledShader.vertexShader).toContain('unityParticleQuarksSpriteTileTransform(floor(uvTile))');
    expect(compiledShader.vertexShader).toContain('unityParticleQuarksSpriteTileTransform(ceil(uvTile))');
    runtime.dispose();
  });

  it('applies exporter camera fade metadata to the particle shader', async () => {
    const source = exporterFixture();
    source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
      schemaVersion: 'unity_particle_quarks_exporter.material.v1',
      fragmentColorMode: 'stock',
      cameraFade: { near: 2, far: 8, smoothness: 1.5 }
    };
    const runtime = await readyRuntime({ prewarm: 1, max: 1 }, 'drop-newest', undefined, source);
    const batches = (runtime as any).batchRenderer.batches as Array<{
      material: {
        vertexShader: string;
        fragmentShader: string;
        uniforms: Record<string, { value: unknown }>;
        userData: Record<string, unknown>;
      };
    }>;
    expect(batches).toHaveLength(1);
    const cameraBatch = batches[0]!;
    expect(cameraBatch.material.userData.unityParticleQuarksCameraFade).toEqual([2, 8, 1.5]);
    expect(cameraBatch.material.uniforms.unityParticleQuarksCameraFade!.value).toBeDefined();
    expect(cameraBatch.material.vertexShader).toContain('unityParticleQuarksCameraDistance');
    expect(cameraBatch.material.fragmentShader).toContain('unityParticleQuarksCameraFadeFactor');
    runtime.dispose();
  });

  it('keeps particle Point Lights at world-space particle positions', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.matrix = new Matrix4().compose(
      new Vector3(3, 4, 5),
      new Quaternion(),
      new Vector3(2, 2, 2)
    ).toArray();
    emitter.ps.worldSpace = true;
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.startSize = { type: 'ConstantValue', value: 2 };
    emitter.userData.unityParticleQuarks.lights = unityLightsMetadata();

    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const particle = handle.instance?.systems[0]?.particles[0];
    let light: PointLight | undefined;
    handle.instance?.root.traverse((object: any) => {
      if (object.isPointLight) light = object as PointLight;
    });
    const worldPosition = light?.getWorldPosition(new Vector3());

    expect(light?.visible).toBe(true);
    expect(worldPosition?.x).toBeCloseTo(particle?.position.x ?? 0, 6);
    expect(worldPosition?.y).toBeCloseTo(particle?.position.y ?? 0, 6);
    expect(worldPosition?.z).toBeCloseTo(particle?.position.z ?? 0, 6);
    expect(light?.distance).toBeCloseTo(40, 6);

    handle.release();
    runtime.dispose();
  });

  it('multiplies Trail ConstantColor with each history record base color', async () => {
    const source = unityTrailFixture({
      colorOverTrail: {
        type: 'ConstantColor',
        color: { r: 0.5, g: 0.25, b: 1, a: 0.5 }
      }
    });
    const emitter = source.object.children[0];
    emitter.ps.startColor = {
      type: 'ConstantColor',
      color: { r: 0.8, g: 0.6, b: 0.4, a: 0.5 }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    runtime.update(0.01);

    const particle = handle.instance?.systems[0]?.particles[0];
    const records = Array.from(particle.previous.values()) as any[];
    expect(records[0].color.x).toBeCloseTo(0.4, 6);
    expect(records[0].color.y).toBeCloseTo(0.15, 6);
    expect(records[0].color.z).toBeCloseTo(0.4, 6);
    expect(records[0].color.w).toBeCloseTo(0.25, 6);

    handle.release();
    runtime.dispose();
  });

  it('multiplies Trail Gradient endpoints along existing history records', async () => {
    const source = unityTrailFixture({ colorOverTrail: redBlueGradient() });
    const emitter = source.object.children[0];
    emitter.ps.startColor = {
      type: 'ConstantColor',
      color: { r: 0.5, g: 0.25, b: 0.75, a: 0.8 }
    };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    runtime.update(0.01);
    runtime.update(0.01);

    const particle = handle.instance?.systems[0]?.particles[0];
    const records = Array.from(particle.previous.values()) as any[];
    // Linked-list history is tail -> head; Unity's gradient is head -> tail.
    expect([records[0].color.x, records[0].color.y, records[0].color.z, records[0].color.w])
      .toEqual([0, 0, 0.75, 0.8]);
    const lastProcessed = records[records.length - 2];
    expect([lastProcessed.color.x, lastProcessed.color.y, lastProcessed.color.z, lastProcessed.color.w])
      .toEqual([0.5, 0, 0, 0.8]);

    handle.release();
    runtime.dispose();
  });

  it('multiplies Trail WidthOverLength by the particle size captured for each history point', async () => {
    const source = unityTrailFixture({ sizeAffectsWidth: true });
    const emitter = source.object.children[0];
    emitter.ps.startSize = { type: 'ConstantValue', value: 0.1 };
    emitter.ps.behaviors = [{
      type: 'WidthOverLength',
      width: { type: 'ConstantValue', value: 0.75 }
    }];
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    runtime.update(0.01);

    const particle = handle.instance?.systems[0]?.particles[0];
    const records = Array.from(particle.previous.values()) as any[];
    expect(records[0].size).toBeCloseTo(0.075, 6);
    // Quarks appends the newest record after behaviors; it is corrected next frame.
    expect(records[records.length - 1].size).toBeCloseTo(0.1, 6);

    handle.release();
    runtime.dispose();
  });

  it('filters Trail history using Unity minVertexDistance after simulation', async () => {
    const source = unityTrailFixture({ minVertexDistance: 0.15 });
    const emitter = source.object.children[0];
    emitter.ps.shape = {
      type: 'cone',
      radius: 0,
      arc: Math.PI * 2,
      thickness: 0,
      angle: 0,
      mode: 0,
      spread: 0,
      speed: { type: 'ConstantValue', value: 1 }
    };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 10 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    runtime.update(0.01);
    runtime.update(0.01);
    const particle = handle.instance?.systems[0]?.particles[0];
    const records = Array.from(particle.previous.values()) as any[];
    expect(records).toHaveLength(2);
    expect(records[0].position.distanceTo(records[1].position)).toBeGreaterThanOrEqual(0.15);
    handle.release();
    runtime.dispose();
  });

  it('stores only Trail history in world space while particle simulation remains local', async () => {
    const source = unityTrailFixture({ worldSpace: true });
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact', { position: [10, 0, 0] }) as any;
    runtime.update(0.01);
    const system = handle.instance?.systems[0];
    const particle = system?.particles[0];
    let records = Array.from(particle.previous.values()) as any[];
    expect(system.worldSpace).toBe(false);
    expect(records[0].position.x).toBeCloseTo(10, 6);

    handle.setTransform({ position: [20, 0, 0] });
    runtime.update(0.01);
    records = Array.from(particle.previous.values()) as any[];
    expect(system.worldSpace).toBe(false);
    expect(records[0].position.x).toBeCloseTo(10, 6);
    expect(records.slice(0, -1).every((record) => Math.abs(record.position.x - 10) < 1e-6)).toBe(true);
    expect(records[records.length - 1].position.x).toBeCloseTo(20, 6);

    handle.release();
    runtime.dispose();
  });

  it('clears Trail history in the frame its particle dies', async () => {
    const source = unityTrailFixture({ dieWithParticles: true });
    source.object.children[0].ps.startLife = { type: 'ConstantValue', value: 0.05 };
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    runtime.update(0.01);
    runtime.update(0.01);
    const particle = handle.instance?.systems[0]?.particles[0];
    expect(particle.previous.length).toBeGreaterThan(1);

    runtime.update(0.03);
    expect(particle.previous.length).toBe(0);

    handle.release();
    runtime.dispose();
  });

  it('rejects an unknown exporter Trail color generator instead of using white fallback', async () => {
    const source = unityTrailFixture({ colorOverTrail: { type: 'UnknownColor' } });
    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    expect(() => runtime.spawn('water-impact')).toThrow(/Unsupported exporter Trail color generator/);
    runtime.dispose();
  });

  it('uses Unity regular light distribution and reuses lights across pool restart', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 4 };
    emitter.userData.unityParticleQuarks.lights = unityLightsMetadata({
      ratio: 0.5,
      randomDistribution: false,
      maxLights: 20
    });

    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    let handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const root = handle.instance?.root;
    let lights: PointLight[] = [];
    root?.traverse((object: any) => {
      if (object.isPointLight) lights.push(object as PointLight);
    });
    expect(lights.filter((light) => light.visible)).toHaveLength(2);
    handle.release();

    handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    lights = [];
    root?.traverse((object: any) => {
      if (object.isPointLight) lights.push(object as PointLight);
    });
    expect(lights).toHaveLength(2);
    expect(lights.filter((light) => light.visible)).toHaveLength(2);
    handle.release();
    runtime.dispose();
  });

  it('uses Unity xorshift128 for seeded random light distribution', async () => {
    const source = exporterFixture();
    const emitter = source.object.children[0];
    emitter.ps.shape = { type: 'point' };
    emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
    emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 4 };
    emitter.userData.unityParticleQuarks.lights = unityLightsMetadata({
      randomSeed: 1,
      ratio: 0.5,
      randomDistribution: true
    });

    const runtime = await readyRuntime({ prewarm: 0, max: 1 }, 'drop-newest', undefined, source);
    const handle = runtime.spawn('water-impact') as any;
    runtime.update(0.01);
    const lights: PointLight[] = [];
    handle.instance?.root.traverse((object: any) => {
      if (object.isPointLight) lights.push(object as PointLight);
    });
    expect(lights.filter((light) => light.visible)).toHaveLength(1);

    handle.release();
    runtime.dispose();
  });
});

function exporterFixture(): Record<string, any> {
  const source = structuredClone(fixtureJson) as Record<string, any>;
  source.metadata.generator = 'UnityParticleQuarksExporter';
  const emitter = source.object.children[0];
  emitter.userData = {
    unityParticleQuarks: {
      schemaVersion: 'unity_particle_quarks_exporter.user_data.v1',
      subEmitterInheritance: []
    }
  };
  emitter.ps.looping = false;
  emitter.ps.duration = 1;
  emitter.ps.worldSpace = false;
  emitter.ps.emissionOverTime = { type: 'ConstantValue', value: 0 };
  emitter.ps.emissionOverDistance = { type: 'ConstantValue', value: 0 };
  emitter.ps.startLife = { type: 'ConstantValue', value: 10 };
  emitter.ps.startSize = { type: 'ConstantValue', value: 1 };
  emitter.ps.behaviors = [];
  emitter.ps.emissionBursts = [{
    time: 0,
    count: { type: 'ConstantValue', value: 1 },
    cycle: 1,
    interval: 0.01,
    probability: 1
  }];
  return source;
}

function unitySphereSemanticsFixture(): Record<string, any> {
  const source = exporterFixture();
  const emitter = source.object.children[0];
  emitter.userData.unityParticleQuarks.shapeSemantics = {
    schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
    distribution: { type: 'sphereVolume', radius: 2, thickness: 1 }
  };
  emitter.ps.shape = {
    type: 'sphere', radius: 2, arc: Math.PI * 2, thickness: 1,
    mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
  };
  emitter.ps.startSpeed = { type: 'ConstantValue', value: 2 };
  emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 1000 };
  return source;
}

function unityBoxSemanticsFixture(): Record<string, any> {
  const source = exporterFixture();
  const emitter = source.object.children[0];
  emitter.userData.unityParticleQuarks.shapeSemantics = {
    schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
    distribution: { type: 'boxVolume', size: [4, 6, 8] }
  };
  emitter.ps.shape = { type: 'point' };
  emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 200 };
  return source;
}

function unityMeshTriangleShapeFixture(startSpeed: number): Record<string, any> {
  const source = exporterFixture();
  const emitter = source.object.children[0];
  emitter.ps.renderMode = 2;
  emitter.ps.shape = { type: 'mesh_surface', geometry: source.geometries[0].uuid };
  emitter.ps.startSpeed = { type: 'ConstantValue', value: startSpeed };
  emitter.ps.startRotation = {
    type: 'Euler',
    angleX: { type: 'ConstantValue', value: 0 },
    angleY: { type: 'ConstantValue', value: 0 },
    angleZ: { type: 'ConstantValue', value: 0 },
    eulerOrder: 'XYZ'
  };
  emitter.userData.unityParticleQuarks.shapeSemantics = {
    schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
    meshNormalOffset: 2,
    alignToDirection: true
  };
  return source;
}

function unityTrailFixture(overrides: Record<string, unknown> = {}): Record<string, any> {
  const source = exporterFixture();
  const emitter = source.object.children[0];
  emitter.ps.renderMode = 3;
  emitter.ps.rendererEmitterSettings = {
    startLength: { type: 'ConstantValue', value: 20 },
    followLocalOrigin: false
  };
  emitter.ps.shape = { type: 'point' };
  emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  emitter.userData.unityParticleQuarks.trailSemantics = {
    schemaVersion: 'unity_particle_quarks_exporter.trail_semantics.v1',
    worldSpace: false,
    dieWithParticles: false,
    sizeAffectsWidth: false,
    ...overrides
  };
  return source;
}

function redBlueGradient(): Record<string, unknown> {
  return {
    type: 'Gradient',
    color: {
      type: 'CLinearFunction',
      subType: 'Color',
      keys: [
        { value: { r: 1, g: 0, b: 0 }, pos: 0 },
        { value: { r: 0, g: 0, b: 1 }, pos: 1 }
      ]
    },
    alpha: {
      type: 'CLinearFunction',
      subType: 'Number',
      keys: [{ value: 1, pos: 0 }, { value: 1, pos: 1 }]
    }
  };
}

function unitySizeTwoCurvesFixture(): Record<string, any> {
  const source = exporterFixture();
  const emitter = source.object.children[0];
  emitter.userData.unityParticleQuarks.sizeOverLifetime = {
    schemaVersion: 'unity_particle_quarks_exporter.size_over_lifetime.v1',
    separateAxes: false,
    size: {
      mode: 'twoCurves',
      minimum: linearCurve(1, 2),
      maximum: linearCurve(3, 4)
    }
  };
  emitter.ps.shape = { type: 'point' };
  emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  emitter.ps.startLife = { type: 'ConstantValue', value: 1 };
  emitter.ps.startSize = { type: 'ConstantValue', value: 2 };
  emitter.ps.behaviors = [{ type: 'SizeOverLife', size: linearCurve(2, 3) }];
  return source;
}

function unityStartColorFixture(mode: 'gradient' | 'randomColor'): Record<string, any> {
  const source = exporterFixture();
  const emitter = source.object.children[0];
  const gradient = {
    type: 'Gradient',
    color: {
      type: 'CLinearFunction', subType: 'Color',
      keys: [
        { value: { r: 0, g: 0, b: 0 }, pos: 0 },
        { value: { r: 1, g: 1, b: 1 }, pos: 1 }
      ]
    },
    alpha: {
      type: 'CLinearFunction', subType: 'Number',
      keys: [{ value: 1, pos: 0 }, { value: 1, pos: 1 }]
    }
  };
  emitter.ps.duration = 10;
  emitter.ps.startColor = gradient;
  emitter.userData.unityParticleQuarks.startColorSemantics = mode === 'randomColor'
    ? { schemaVersion: 'unity_particle_quarks_exporter.start_color.v1', mode, gradient }
    : { schemaVersion: 'unity_particle_quarks_exporter.start_color.v1', mode };
  return source;
}

function unityMaterialSemanticsFixture(
  mode: 'legacySoftAdditive' | 'hovlAdditivePremultiply' | 'invisibleFallback' | 'legacyAlphaPremultiply' | 'legacyMultiply' | 'legacyMultiplyDouble'
): Record<string, any> {
  const source = exporterFixture();
  source.object.children[0].userData.unityParticleQuarks.materialSemantics = {
    schemaVersion: 'unity_particle_quarks_exporter.material.v1',
    fragmentColorMode: mode
  };
  return source;
}

function fragmentOccurrences(value: string, marker: string): number {
  let count = 0;
  let offset = 0;
  while ((offset = value.indexOf(marker, offset)) >= 0) {
    count += 1;
    offset += marker.length;
  }
  return count;
}

function unityMeshScalarRotationFixture(
  axisMode: 'fixed' | 'position' | 'velocity' | 'uniformXY',
  axis?: [number, number, number]
): Record<string, any> {
  const source = exporterFixture();
  const emitter = source.object.children[0];
  emitter.userData.unityParticleQuarks.meshScalarRotation = {
    schemaVersion: 'unity_particle_quarks_exporter.mesh_scalar_rotation.v1',
    axisMode,
    ...(axisMode === 'fixed' ? { axis } : {})
  };
  emitter.ps.renderMode = 2;
  emitter.ps.shape = { type: 'point' };
  emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  emitter.ps.startRotation = {
    type: 'Euler',
    angleX: { type: 'ConstantValue', value: 0 },
    angleY: { type: 'ConstantValue', value: 0 },
    angleZ: { type: 'ConstantValue', value: -1 },
    eulerOrder: 'XYZ'
  };
  emitter.ps.behaviors = [{
    type: 'Rotation3DOverLife',
    angularVelocity: {
      type: 'Euler',
      angleX: { type: 'ConstantValue', value: 0 },
      angleY: { type: 'ConstantValue', value: 0 },
      angleZ: { type: 'ConstantValue', value: -2 },
      eulerOrder: 'XYZ'
    }
  }];
  return source;
}

function linearCurve(start: number, end: number): Record<string, unknown> {
  const delta = end - start;
  return {
    type: 'PiecewiseBezier',
    functions: [{
      function: {
        p0: start,
        p1: start + delta / 3,
        p2: start + delta * 2 / 3,
        p3: end
      },
      start: 0
    }]
  };
}

function unityVelocityOverLifetimeFixture(): Record<string, any> {
  const source = structuredClone(fixtureJson) as Record<string, any>;
  source.metadata.generator = 'UnityParticleQuarksExporter';
  const emitter = source.object.children[0];
  emitter.userData = {
    unityParticleQuarks: {
      schemaVersion: 'unity_particle_quarks_exporter.user_data.v1',
      subEmitterInheritance: [],
      velocityOverLifetime: {
        schemaVersion: 'unity_particle_quarks_exporter.velocity_over_lifetime.v1',
        space: 'local',
        basisX: [0, 0, 1],
        basisY: [1, 0, 0],
        basisZ: [0, 1, 0],
        x: {
          mode: 'curve',
          value: {
            type: 'PiecewiseBezier',
            functions: [
              { function: { p0: 4, p1: 2.6666666667, p2: 1.3333333333, p3: 0 }, start: 0 },
              { function: { p0: 0, p1: 0, p2: 0, p3: 0 }, start: 0.1 }
            ]
          }
        },
        y: {
          mode: 'twoCurves',
          minimum: { type: 'ConstantValue', value: 1 },
          maximum: { type: 'ConstantValue', value: 5 }
        },
        z: { mode: 'constant', value: { type: 'ConstantValue', value: 0 } }
      }
    }
  };
  emitter.ps.looping = false;
  emitter.ps.duration = 1;
  emitter.ps.worldSpace = false;
  emitter.ps.shape = { type: 'point' };
  emitter.ps.startLife = { type: 'ConstantValue', value: 1 };
  emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  emitter.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 1 };
  emitter.ps.behaviors = [{
    type: 'ForceOverLife',
    x: { type: 'ConstantValue', value: 0 },
    y: { type: 'ConstantValue', value: 0 },
    z: { type: 'ConstantValue', value: 0 }
  }];
  return source;
}

function unityInheritVelocityFixture(worldSpace: boolean, emitterScale: number): Record<string, any> {
  const source = exporterFixture();
  const emitter = source.object.children[0];
  emitter.matrix = new Matrix4().makeScale(emitterScale, 1, 1).toArray();
  emitter.ps.worldSpace = worldSpace;
  emitter.ps.looping = true;
  emitter.ps.duration = 10;
  emitter.ps.shape = { type: 'point' };
  emitter.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  emitter.ps.emissionBursts = [];
  emitter.ps.emissionOverTime = { type: 'ConstantValue', value: 100 };
  emitter.userData.unityParticleQuarks.inheritVelocity = {
    schemaVersion: 'unity_particle_quarks_exporter.inherit_velocity.v1',
    mode: 'initial',
    curve: { mode: 'constant', value: { type: 'ConstantValue', value: 1 } }
  };
  return source;
}

function unityCurrentInheritVelocityFixture(worldSpace: boolean): Record<string, any> {
  const source = unityInheritVelocityFixture(worldSpace, 1);
  source.object.children[0].userData.unityParticleQuarks.inheritVelocity = {
    schemaVersion: 'unity_particle_quarks_exporter.inherit_velocity.v2',
    mode: 'current',
    curve: { mode: 'constant', value: { type: 'ConstantValue', value: 1 } }
  };
  return source;
}

function unityNoiseMetadata(): Record<string, unknown> {
  const constant = (value: number) => ({ mode: 'constant', value: { type: 'ConstantValue', value } });
  return {
    schemaVersion: 'unity_particle_quarks_exporter.noise.v1',
    simulationSpace: 'local',
    particleToNoiseBasisX: [-1, 0, 0],
    particleToNoiseBasisY: [0, 1, 0],
    particleToNoiseBasisZ: [0, 0, 1],
    noiseToParticleBasisX: [-1, 0, 0],
    noiseToParticleBasisY: [0, 1, 0],
    noiseToParticleBasisZ: [0, 0, 1],
    randomSeed: 1,
    separateAxes: true,
    frequency: 0.5,
    damping: true,
    qualityDimensions: 3,
    octaveCount: 1,
    octaveMultiplier: 0.5,
    octaveScale: 2,
    strengthX: constant(0.4),
    strengthY: constant(0),
    strengthZ: constant(0.4),
    positionAmount: constant(1),
    scrollSpeed: constant(0.1)
  };
}

function unityNoiseRemapMetadata(remappedValue: number): Record<string, unknown> {
  const constant = (value: number) => ({ mode: 'constant', value: { type: 'ConstantValue', value } });
  return {
    schemaVersion: 'unity_particle_quarks_exporter.noise.v1',
    simulationSpace: 'local',
    particleToNoiseBasisX: [1, 0, 0],
    particleToNoiseBasisY: [0, 1, 0],
    particleToNoiseBasisZ: [0, 0, 1],
    noiseToParticleBasisX: [1, 0, 0],
    noiseToParticleBasisY: [0, 1, 0],
    noiseToParticleBasisZ: [0, 0, 1],
    randomSeed: 1,
    separateAxes: true,
    frequency: 1,
    damping: false,
    qualityDimensions: 3,
    octaveCount: 1,
    octaveMultiplier: 0.5,
    octaveScale: 2,
    strengthX: constant(1),
    strengthY: constant(1),
    strengthZ: constant(1),
    positionAmount: constant(1),
    scrollSpeed: constant(0),
    remapEnabled: true,
    remapX: constant(remappedValue),
    remapY: constant(remappedValue),
    remapZ: constant(remappedValue)
  };
}

function unityLightsMetadata(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const { shadowMode = 'none', ...rootOverrides } = overrides;
  return {
    schemaVersion: 'unity_particle_quarks_exporter.lights.v1',
    randomSeed: 1,
    ratio: 1,
    randomDistribution: true,
    useParticleColor: true,
    sizeAffectsRange: true,
    alphaAffectsIntensity: true,
    maxLights: 20,
    uses3DSize: false,
    meshSize: false,
    renderScaleMode: 'hierarchy',
    sourceRenderScale: { x: 1, y: 1, z: 1 },
    particleColorMultiplier: { r: 1, g: 1, b: 1, a: 1 },
    range: { mode: 'constant', value: { type: 'ConstantValue', value: 2 } },
    intensity: { mode: 'constant', value: { type: 'ConstantValue', value: 0.5 } },
    ...rootOverrides,
    light: {
      type: 'point',
      color: { r: 1, g: 1, b: 1, a: 1 },
      intensity: 4,
      range: 5,
      cullingMask: 5,
      shadowMode
    }
  };
}

function unityDeathSubemitterFixture(): Record<string, any> {
  const source = structuredClone(subemitterFixtureJson) as Record<string, any>;
  source.metadata.generator = 'UnityParticleQuarksExporter';
  const parent = source.object.children[0];
  const child = source.object.children[1];
  delete parent.userData;
  delete child.userData;
  parent.matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 2, 3, 4, 1];
  parent.ps.looping = false;
  parent.ps.duration = 1;
  parent.ps.worldSpace = false;
  parent.ps.shape = {
    type: 'cone', radius: 0, arc: Math.PI * 2, thickness: 0,
    angle: 0, mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
  };
  parent.ps.startLife = { type: 'ConstantValue', value: 0.1 };
  parent.ps.startSpeed = { type: 'ConstantValue', value: 2 };
  parent.ps.startRotation = { type: 'ConstantValue', value: Math.PI / 2 };
  parent.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 1 };
  parent.ps.behaviors[0].mode = 0;
  parent.ps.behaviors[0].useVelocityAsBasis = false;

  child.matrix = [1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1];
  child.ps.worldSpace = false;
  child.ps.shape = {
    type: 'cone', radius: 0, arc: Math.PI * 2, thickness: 0,
    angle: 0, mode: 0, spread: 0, speed: { type: 'ConstantValue', value: 1 }
  };
  child.ps.startSpeed = { type: 'ConstantValue', value: 1 };
  child.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 1 };
  return source;
}

function unityBirthSubemitterFixture(): Record<string, any> {
  const source = unityDeathSubemitterFixture();
  const parent = source.object.children[0];
  parent.ps.startLife = { type: 'ConstantValue', value: 1 };
  parent.ps.startSpeed = { type: 'ConstantValue', value: 2 };
  parent.ps.behaviors[0].mode = 1;
  parent.ps.duration = 1;
  return source;
}

function unityWorldSpaceShapeSubemitterFixture(): Record<string, any> {
  const source = unityDeathSubemitterFixture();
  const child = source.object.children[1];
  child.userData = {
    unityParticleQuarks: {
      schemaVersion: 'unity_particle_quarks_exporter.user_data.v1',
      subEmitterInheritance: [],
      shapeSemantics: {
        schemaVersion: 'unity_particle_quarks_exporter.shape_semantics.v1',
        distribution: { type: 'boxVolume', size: [0, 0, 0] }
      }
    }
  };
  child.ps.worldSpace = true;
  child.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  return source;
}

function unityInheritedBirthSubemitterFixture(): Record<string, any> {
  const source = structuredClone(subemitterFixtureJson) as Record<string, any>;
  source.metadata.generator = 'UnityParticleQuarksExporter';
  const parent = source.object.children[0];
  const child = source.object.children[1];
  parent.userData = {
    unityParticleQuarks: {
      schemaVersion: 'unity_particle_quarks_exporter.user_data.v1',
      subEmitterInheritance: [{
        index: 0,
        subParticleSystem: child.uuid,
        mode: 1,
        inheritColor: true,
        inheritSize: true,
        inheritRotation: true,
        inheritLifetime: true,
        inheritDuration: false
      }]
    }
  };
  child.userData = {
    unityParticleQuarks: {
      schemaVersion: 'unity_particle_quarks_exporter.user_data.v1',
      subEmitterInheritance: []
    }
  };

  parent.ps.looping = false;
  parent.ps.duration = 1;
  parent.ps.worldSpace = false;
  parent.ps.renderMode = 2;
  parent.ps.shape = { type: 'point' };
  parent.ps.startLife = { type: 'ConstantValue', value: 5 };
  parent.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  parent.ps.startRotation = {
    type: 'Euler',
    angleX: { type: 'ConstantValue', value: 0.2 },
    angleY: { type: 'ConstantValue', value: 0.3 },
    angleZ: { type: 'ConstantValue', value: 1 },
    eulerOrder: 'XYZ'
  };
  parent.ps.startSize = { type: 'ConstantValue', value: 3 };
  parent.ps.startColor = {
    type: 'ConstantColor',
    color: { r: 0.5, g: 0.25, b: 0.75, a: 0.5 }
  };
  parent.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 2 };
  parent.ps.behaviors[0].mode = 1;
  parent.ps.behaviors[0].useVelocityAsBasis = false;

  child.ps.worldSpace = false;
  child.ps.renderMode = 2;
  child.ps.startLife = { type: 'ConstantValue', value: 4 };
  child.ps.startSpeed = { type: 'ConstantValue', value: 0 };
  child.ps.startSize = {
    type: 'Vector3Function',
    x: { type: 'ConstantValue', value: 2 },
    y: { type: 'ConstantValue', value: 3 },
    z: { type: 'ConstantValue', value: 4 }
  };
  child.ps.startRotation = {
    type: 'Euler',
    angleX: { type: 'ConstantValue', value: 0.2 },
    angleY: { type: 'ConstantValue', value: 0.3 },
    angleZ: { type: 'ConstantValue', value: 0.4 },
    eulerOrder: 'XYZ'
  };
  child.ps.startColor = {
    type: 'ConstantColor',
    color: { r: 0.8, g: 0.6, b: 0.4, a: 0.5 }
  };
  child.ps.emissionBursts[0].count = { type: 'ConstantValue', value: 1 };
  child.ps.behaviors = [];
  return source;
}

async function readyRuntime(
  pool: { prewarm: number; max: number },
  overflow: 'drop-newest' | 'reuse-oldest' = 'drop-newest',
  variant?: VfxVariant,
  sourceJson: unknown = fixtureJson
): Promise<VfxRuntime> {
  const manifest: VfxManifest = {
    schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
    effects: [{ id: 'water-impact', status: 'ready', url: './effect.json' }]
  };
  const fetcher = mapFetch(new Map([
    ['http://test/manifest.json', jsonResponse(manifest)],
    ['http://test/effect.json', jsonResponse(sourceJson)]
  ]));
  const runtime = createVfxRuntime({
    scene: new Scene(),
    renderer: {} as WebGLRenderer,
    camera: new PerspectiveCamera(),
    pool,
    overflow,
    fetch: fetcher
  });
  await runtime.loadManifest('http://test/manifest.json');
  await runtime.preload('water-impact', variant);
  return runtime;
}

function mapFetch(responses: Map<string, Response>): typeof globalThis.fetch {
  return (async (input: RequestInfo | URL) => {
    const key = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const response = responses.get(key);
    return response ? response.clone() : new Response('not found', { status: 404 });
  }) as typeof globalThis.fetch;
}

function jsonResponse(value: unknown): Response {
  return new Response(JSON.stringify(value), { status: 200, headers: { 'content-type': 'application/json' } });
}
