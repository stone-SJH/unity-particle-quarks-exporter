import {
  Euler,
  DoubleSide,
  DynamicDrawUsage,
  Group,
  InstancedBufferAttribute,
  Layers,
  LoadingManager,
  Matrix4,
  Object3D,
  PointLight,
  Quaternion,
  ShaderChunk,
  TextureLoader,
  Vector2,
  Vector3,
  Vector4,
  type Camera,
  type Material,
  type BufferGeometry,
  type WebGLRenderer
} from 'three';
import {
  ColorGeneratorFromJSON,
  Euler as QuarksEuler,
  Gradient as QuarksGradient,
  Quaternion as QuarksQuaternion,
  Vector3 as QuarksVector3,
  Vector4 as QuarksVector4,
  type Behavior,
  type ColorGenerator,
  type FunctionColorGenerator,
  type GeneratorMemory,
  type FunctionJSON,
  type Particle
} from 'quarks.core';
import { BatchedRenderer, ParticleSystem, QuarksLoader, RenderMode, SpriteBatch } from 'three.quarks';
import {
  extensionKey,
  resolveManifestAsset,
  UNITY_PARTICLE_PAIRED_SEMANTICS_EXTENSION,
  validateVfxManifest
} from './manifest.js';
import { sampleUnityCurlNoise, unityRandom3, type UnityNoiseQuality } from './unity-noise-field.js';
import { applyVfxVariant, variantHash } from './variant.js';
import type {
  CreateVfxRuntimeOptions,
  VfxEffectTelemetry,
  VfxEvent,
  VfxHandle,
  VfxManifest,
  VfxManifestEffect,
  VfxRuntime,
  VfxSpawnOptions,
  VfxTelemetry,
  VfxTransform,
  VfxVariant,
  VfxRuntimeProfile
} from './types.js';

const OWNER_KEY = '__unityParticleQuarksRuntime';
const LEGACY_OWNER_KEY = '__presentationGameVfxRuntime';
const UP = new Vector3(0, 1, 0);
const FORWARD = new Vector3(0, 0, 1);
const UNITY_SHAPE_RANDOM_DIRECTION_MIN_RADIUS = 0.001;
// Keep malformed exporter values from turning one render tick into an
// unbounded synchronous substep loop.
const MAX_UNITY_SIMULATION_SPEED = 32;
// Quarks grows its particle array directly from burst counts. This cap keeps
// malformed stock or exporter JSON from monopolizing a render tick or heap.
const MAX_RUNTIME_PARTICLES_PER_SYSTEM = 4096;

type InstanceState = 'idle' | 'active' | 'draining';

interface LoadedEffect {
  key: string;
  effectId: string;
  hash: string;
  template: Object3D;
  sourceJson: unknown;
  source: VfxEffectTelemetry['source'];
  url: string;
  applyUnityExporterExtension: boolean;
  headResources: Map<string, CompanionHeadResources>;
}

interface UnityParticleHeadMetadata {
  geometry: string;
  material: string;
  materialColor: [number, number, number, number] | undefined;
  restoreMaterialColor: boolean | undefined;
  materialProjectColorSpace: UnityProjectColorSpace | undefined;
  renderMode: 0 | 1 | 2 | 4 | 5;
  renderOrder: number;
  layers: number;
  uTileCount: number;
  vTileCount: number;
  blendTiles: boolean;
  softParticles: boolean;
  softFarFade: number;
  softNearFade: number;
  worldSpace: boolean;
  rendererEmitterSettings: UnityParticleHeadRendererSettings | undefined;
  rotation: {
    alignment: 'local' | 'velocity' | 'view' | 'facing' | 'billboard';
    preserveAuthored: boolean;
  };
}

interface UnityParticleHeadRendererSettings {
  speedFactor: number;
  lengthFactor: number;
}

interface CompanionHeadResources {
  geometry: BufferGeometry;
  material: Material;
}

interface VfxInstance {
  root: Group;
  systems: ParticleSystem[];
  companionHeadBatches: Array<{
    batch: SpriteBatch;
    system: ParticleSystem;
    worldSpace: boolean;
    rendererEmitterSettings: UnityParticleHeadRendererSettings | undefined;
  }>;
  state: InstanceState;
  sequence: number;
  handle: RuntimeVfxHandle | null;
  scaleApproximationReported: boolean;
}

interface EffectPool {
  effect: LoadedEffect;
  instances: VfxInstance[];
}

export class VfxNotPreloadedError extends Error {
  constructor(effectId: string) {
    super(`VFX effect ${effectId} and its requested variant must be preloaded before spawn.`);
    this.name = 'VfxNotPreloadedError';
  }
}

class RuntimeVfxHandle implements VfxHandle {
  private isReleased = false;

  constructor(
    readonly id: number,
    readonly effectId: string,
    readonly dropped: boolean,
    private readonly runtime: VfxRuntimeImpl,
    readonly instance: VfxInstance | null
  ) {}

  get released(): boolean {
    return this.isReleased;
  }

  markReleased(): void {
    this.isReleased = true;
  }

  play(): void {
    if (!this.isReleased && this.instance) this.runtime.playInstance(this.instance);
  }

  stop(): void {
    if (!this.isReleased && this.instance) this.runtime.stopInstance(this.instance);
  }

  endEmit(): void {
    if (!this.isReleased && this.instance) this.runtime.endInstanceEmission(this.instance);
  }

  setTransform(transform: VfxTransform): void {
    if (!this.isReleased && this.instance) this.runtime.setInstanceTransform(this.instance, transform);
  }

  release(): void {
    this.runtime.release(this);
  }
}

export class VfxRuntimeImpl implements VfxRuntime {
  private readonly batchRenderer = new BatchedRenderer();
  private readonly definitions = new Map<string, VfxManifestEffect>();
  private readonly definitionManifestUrls = new Map<string, string>();
  private readonly loaded = new Map<string, LoadedEffect>();
  private readonly pools = new Map<string, EffectPool>();
  private readonly fetcher: typeof globalThis.fetch;
  private readonly prewarmCount: number;
  private readonly maxCount: number;
  private readonly overflow: 'drop-newest' | 'reuse-oldest';
  private readonly allowPartial: boolean;
  private readonly runtimeProfile: VfxRuntimeProfile;
  private readonly supportedExtensions = new Set<string>();
  private manifestUrl = '';
  private disposed = false;
  private nextHandleId = 1;
  private nextSequence = 1;
  private materialTime = 0;
  private telemetry: VfxTelemetry = {
    manifestLoaded: false,
    runtimeProfile: 'extended',
    enabledExtensions: [],
    effectsLoaded: 0,
    loadFailures: 0,
    fallbackLoads: 0,
    spawned: 0,
    released: 0,
    dropped: 0,
    reused: 0,
    variantSkippedFieldCount: 0,
    softParticleDepthMode: 'disabled-no-depth',
    softParticleSystemsDisabled: 0,
    worldSpaceScaleApproximations: 0,
    activeInstances: 0,
    idleInstances: 0,
    allocatedInstances: 0,
    particleCount: 0,
    activeSystemCount: 0,
    batchCount: 0,
    effects: {}
  };

  constructor(private readonly options: CreateVfxRuntimeOptions) {
    if (options.scene.userData[OWNER_KEY] || options.scene.userData[LEGACY_OWNER_KEY]) {
      throw new Error('A scene may contain only one Unity-to-Quarks VFX runtime and shared BatchedRenderer.');
    }
    this.prewarmCount = integerInRange(options.pool?.prewarm ?? 4, 0, 1024, 'pool.prewarm');
    this.maxCount = integerInRange(options.pool?.max ?? 32, 1, 1024, 'pool.max');
    if (this.prewarmCount > this.maxCount) throw new Error('VFX pool.prewarm cannot exceed pool.max.');
    this.overflow = options.overflow ?? 'drop-newest';
    this.allowPartial = options.allowPartial ?? false;
    this.runtimeProfile = options.runtimeProfile ?? 'extended';
    if (this.runtimeProfile !== 'stock' && this.runtimeProfile !== 'extended') {
      throw new Error('VFX runtimeProfile must be stock or extended.');
    }
    if (this.runtimeProfile === 'extended') {
      this.supportedExtensions.add(extensionKey(UNITY_PARTICLE_PAIRED_SEMANTICS_EXTENSION));
    }
    this.telemetry.runtimeProfile = this.runtimeProfile;
    this.telemetry.enabledExtensions = [...this.supportedExtensions].sort();
    const fetcher = options.fetch ?? globalThis.fetch?.bind(globalThis);
    if (typeof fetcher !== 'function') throw new Error('VFX runtime requires fetch.');
    this.fetcher = fetcher;
    options.scene.userData[OWNER_KEY] = this;
    options.scene.userData[LEGACY_OWNER_KEY] = this;
    this.batchRenderer.name = 'Unity-to-Quarks VFX shared BatchedRenderer';
    if (options.depthTexture) {
      this.batchRenderer.setDepthTexture(options.depthTexture);
      this.telemetry.softParticleDepthMode = 'provided-depth';
    }
    options.scene.add(this.batchRenderer);
    void options.renderer;
    void options.camera;
  }

  async loadManifest(url: string): Promise<void> {
    this.assertActive();
    const response = await this.fetcher(url);
    if (!response.ok) throw new Error(`Failed to load VFX manifest ${url}: HTTP ${response.status}.`);
    const manifest = validateVfxManifest(await response.json()) as VfxManifest;
    const resolvedManifestUrl = response.url || new URL(url, globalThis.location?.href ?? 'http://localhost/').href;
    const definitions = new Map<string, VfxManifestEffect>();
    for (const effect of manifest.effects) {
      for (const extension of effect.extensionsRequired ?? []) {
        const key = extensionKey(extension);
        if (!this.supportedExtensions.has(key)) {
          throw new Error(
            `VFX effect ${effect.id} requires unsupported extension ${key} under ${this.runtimeProfile} runtime profile.`
          );
        }
      }
      definitions.set(effect.id, effect);
    }
    // Manifests are additive. This lets a local sample manifest provide only
    // weather fallbacks when a licensed manifest is present but incomplete.
    // A successfully preloaded effect always wins, so later manifests cannot
    // invalidate live vehicle pools.
    if (!this.telemetry.manifestLoaded) this.manifestUrl = resolvedManifestUrl;
    for (const [effectId, effect] of definitions) {
      const alreadyLoaded = [...this.loaded.values()].some((loaded) => loaded.effectId === effectId);
      if (!this.definitions.has(effectId) || !alreadyLoaded) {
        this.definitions.set(effectId, effect);
        this.definitionManifestUrls.set(effectId, resolvedManifestUrl);
      }
    }
    this.telemetry.manifestLoaded = true;
  }

  async preload(effectId: string, variant?: VfxVariant): Promise<void> {
    this.assertActive();
    if (!this.telemetry.manifestLoaded) throw new Error('Load the VFX manifest before preloading effects.');
    const hash = variantHash(variant);
    const key = poolKey(effectId, hash);
    if (this.loaded.has(key)) return;
    const definition = this.definitions.get(effectId);
    if (!definition) throw new Error(`VFX manifest does not define effect ${effectId}.`);

    const appliedSources: Array<{ url: string; source: VfxEffectTelemetry['source'] }> = [];
    const primaryAllowed = definition.status === 'ready' || (definition.status === 'partial' && this.allowPartial);
    const definitionManifestUrl = this.definitionManifestUrls.get(effectId) ?? this.manifestUrl;
    if (primaryAllowed && definition.url) {
      appliedSources.push({ url: resolveManifestAsset(definitionManifestUrl, definition.url, `${effectId}.url`), source: 'converted' });
    }
    if (definition.fallbackUrl) {
      appliedSources.push({ url: resolveManifestAsset(definitionManifestUrl, definition.fallbackUrl, `${effectId}.fallbackUrl`), source: 'synthetic-fallback' });
    }
    if (appliedSources.length === 0) throw new Error(`VFX effect ${effectId} has no source allowed by runtime policy.`);

    const failures: string[] = [];
    for (const candidate of appliedSources) {
      try {
        const sourceJson = await this.fetchJson(candidate.url);
        const applied = applyVfxVariant(sourceJson, variant);
        this.telemetry.variantSkippedFieldCount += applied.skippedFields;
        this.telemetry.softParticleSystemsDisabled += configureSoftParticles(applied.json, Boolean(this.options.depthTexture));
        absolutizeImageUrls(applied.json, candidate.url);
        const convertedByUnityExporter = isUnityExporterJson(applied.json);
        const declaresUnityExporterExtension = (definition.extensionsUsed ?? []).some((extension) =>
          extensionKey(extension) === extensionKey(UNITY_PARTICLE_PAIRED_SEMANTICS_EXTENSION));
        const applyUnityExporterExtension = convertedByUnityExporter &&
          this.runtimeProfile === 'extended' && definition.runtimeProfile === 'extended' &&
          declaresUnityExporterExtension;
        const template = await parseQuarksJson(applied.json);
        if (collectSystems(template).length === 0) throw new Error('stock Quarks JSON contains no ParticleEmitter.');
        const headResources = applyUnityExporterExtension
          ? await loadCompanionHeadResources(applied.json)
          : new Map<string, CompanionHeadResources>();
        const loaded: LoadedEffect = {
          key,
          effectId,
          hash,
          template,
          sourceJson: applied.json,
          source: candidate.source,
          url: candidate.url,
          applyUnityExporterExtension,
          headResources
        };
        this.loaded.set(key, loaded);
        const pool: EffectPool = { effect: loaded, instances: [] };
        this.pools.set(key, pool);
        for (let index = 0; index < this.prewarmCount; index += 1) pool.instances.push(this.createInstance(loaded));
        this.telemetry.effectsLoaded += 1;
        if (candidate.source === 'synthetic-fallback') this.telemetry.fallbackLoads += 1;
        this.telemetry.effects[key] = { source: candidate.source, status: definition.status, url: candidate.url };
        this.refreshCounts();
        return;
      } catch (error) {
        this.telemetry.loadFailures += 1;
        failures.push(`${candidate.url}: ${errorMessage(error)}`);
      }
    }
    throw new Error(`VFX effect ${effectId} failed all sources: ${failures.join(' | ')}`);
  }

  spawn(effectId: string, options: VfxSpawnOptions = {}): VfxHandle {
    this.assertActive();
    const key = poolKey(effectId, variantHash(options.variant));
    const pool = this.pools.get(key);
    if (!pool) throw new VfxNotPreloadedError(effectId);

    let instance = pool.instances.find((candidate) => candidate.state === 'idle');
    if (!instance && pool.instances.length < this.maxCount) {
      instance = this.createInstance(pool.effect);
      pool.instances.push(instance);
    }
    if (!instance && this.overflow === 'reuse-oldest') {
      instance = pool.instances.filter((candidate) => candidate.state !== 'idle').sort((a, b) => a.sequence - b.sequence)[0];
      if (instance) {
        this.releaseInstance(instance, false);
        this.telemetry.reused += 1;
      }
    }
    if (!instance) {
      const handle = new RuntimeVfxHandle(this.nextHandleId++, effectId, true, this, null);
      handle.markReleased();
      this.telemetry.dropped += 1;
      this.refreshCounts();
      return handle;
    }

    const handle = new RuntimeVfxHandle(this.nextHandleId++, effectId, false, this, instance);
    instance.handle = handle;
    instance.state = 'active';
    instance.sequence = this.nextSequence++;
    this.setInstanceTransform(instance, options);
    for (const companion of instance.companionHeadBatches) companion.batch.visible = true;
    // Unity only starts root ParticleSystems. Systems marked onlyUsedByOther
    // are advanced by EmitSubParticleSystem and must not be treated as a
    // second independent emitter when a pooled instance is spawned.
    for (const system of instance.systems) restartParticleSystem(system);
    for (const system of instance.systems) {
      if (!system.onlyUsedByOther) system.play();
    }
    this.telemetry.spawned += 1;
    this.refreshCounts();
    return handle;
  }

  emit(event: VfxEvent): VfxHandle {
    return this.spawn(event.effectId, event);
  }

  update(delta: number): void {
    if (this.disposed) return;
    const safeDelta = Math.min(0.1, Math.max(0, Number.isFinite(delta) ? delta : 0));
    this.materialTime += safeDelta;
    this.updateBatches(safeDelta);
    this.updateUnityMaterialTimeUniforms();
    for (const pool of this.pools.values()) {
      for (const instance of pool.instances) {
        if (instance.state === 'idle') continue;
        const allParticlesGone = instance.systems.every((system) => system.particleNum === 0);
        const rootsEnded = instance.systems.filter((system) => !system.onlyUsedByOther)
          .every((system) => Boolean((system as unknown as { emitEnded?: boolean }).emitEnded));
        if (allParticlesGone && (instance.state === 'draining' || rootsEnded)) this.releaseInstance(instance, true);
      }
    }
    this.refreshCounts();
  }

  private updateBatches(delta: number): void {
    const worldTrailSystems = Array.from(this.batchRenderer.systemToBatchIndex.keys())
      .filter((system) => (system as unknown as UnityTrailRuntimeSystem)
        .__unityParticleQuarksTrailRecordsWorldSpace === true);
    if (worldTrailSystems.length === 0) {
      this.batchRenderer.update(delta);
      this.updateCompanionHeads();
      return;
    }

    this.batchRenderer.systemToBatchIndex.forEach((_batchIndex, system) => {
      (system as unknown as { update(delta: number): void }).update(delta);
    });
    const originalSpaces = worldTrailSystems.map((system) => system.worldSpace);
    try {
      worldTrailSystems.forEach((system) => { system.worldSpace = true; });
      this.batchRenderer.batches.forEach((batch) => batch.update());
    } finally {
      worldTrailSystems.forEach((system, index) => {
        system.worldSpace = originalSpaces[index] ?? false;
      });
    }
    this.updateCompanionHeads();
  }

  private updateUnityMaterialTimeUniforms(): void {
    for (const batch of this.batchRenderer.batches) {
      const material = (batch as unknown as {
        material?: { uniforms?: Record<string, { value: unknown }> };
      }).material;
      const uniform = material?.uniforms?.unityParticleQuarksTime;
      if (uniform) uniform.value = this.materialTime;
    }
  }

  private updateCompanionHeads(): void {
    for (const pool of this.pools.values()) {
      for (const instance of pool.instances) {
        if (instance.state === 'idle') continue;
        for (const companion of instance.companionHeadBatches) {
          const originalSpace = companion.system.worldSpace;
          const originalRendererSettings = companion.system.rendererEmitterSettings;
          companion.system.worldSpace = companion.worldSpace;
          if (companion.rendererEmitterSettings) {
            companion.system.rendererEmitterSettings = companion.rendererEmitterSettings;
          }
          try {
            companion.batch.update();
          } finally {
            companion.system.worldSpace = originalSpace;
            companion.system.rendererEmitterSettings = originalRendererSettings;
          }
        }
      }
    }
  }

  release(handle: VfxHandle): void {
    if (!(handle instanceof RuntimeVfxHandle) || handle.released) return;
    if (handle.instance) this.releaseInstance(handle.instance, true);
    else handle.markReleased();
    this.refreshCounts();
  }

  getTelemetry(): VfxTelemetry {
    this.refreshCounts();
    return JSON.parse(JSON.stringify(this.telemetry)) as VfxTelemetry;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const pool of this.pools.values()) {
      for (const instance of pool.instances) {
        instance.handle?.markReleased();
        for (const system of instance.systems) {
          system.stop();
          this.batchRenderer.deleteSystem(system);
        }
        for (const companion of instance.companionHeadBatches) {
          companion.batch.removeSystem(companion.system);
          companion.batch.removeFromParent();
          companion.batch.dispose();
        }
        instance.companionHeadBatches.length = 0;
        instance.root.removeFromParent();
      }
      disposeTemplateResources(pool.effect.template);
      disposeCompanionHeadResources(pool.effect.headResources);
    }
    this.pools.clear();
    this.loaded.clear();
    this.definitions.clear();
    this.batchRenderer.removeFromParent();
    if (this.options.scene.userData[OWNER_KEY] === this) delete this.options.scene.userData[OWNER_KEY];
    if (this.options.scene.userData[LEGACY_OWNER_KEY] === this) delete this.options.scene.userData[LEGACY_OWNER_KEY];
    this.refreshCounts();
  }

  playInstance(instance: VfxInstance): void {
    if (instance.state === 'idle') return;
    for (const system of instance.systems) {
      if (!system.onlyUsedByOther) system.play();
    }
    instance.state = 'active';
  }

  stopInstance(instance: VfxInstance): void {
    if (instance.state === 'idle') return;
    for (const system of instance.systems) system.stop();
  }

  endInstanceEmission(instance: VfxInstance): void {
    if (instance.state === 'idle') return;
    for (const system of instance.systems) system.endEmit();
    instance.state = 'draining';
  }

  setInstanceTransform(instance: VfxInstance, transform: VfxTransform): void {
    const parent = transform.parent ?? this.options.scene;
    if (instance.root.parent !== parent) parent.add(instance.root);
    if (transform.position) instance.root.position.fromArray(transform.position);
    else instance.root.position.set(0, 0, 0);
    instance.root.quaternion.identity();
    if (transform.normal) {
      const normal = new Vector3().fromArray(transform.normal);
      if (normal.lengthSq() > 1e-12) instance.root.quaternion.copy(new Quaternion().setFromUnitVectors(UP, normal.normalize()));
    }
    if (Array.isArray(transform.scale)) {
      instance.root.scale.fromArray(transform.scale);
      instance.root.scale.set(Math.abs(instance.root.scale.x), Math.abs(instance.root.scale.y), Math.abs(instance.root.scale.z));
    }
    else instance.root.scale.setScalar(Math.abs(transform.scale ?? 1));
    if (!instance.scaleApproximationReported && hasNonUnitScale(instance.root.scale) &&
        requiresWorldSpaceScaleApproximation(instance.systems)) {
      instance.scaleApproximationReported = true;
      this.telemetry.worldSpaceScaleApproximations += 1;
    }
    instance.root.updateMatrixWorld(true);
  }

  private createInstance(effect: LoadedEffect): VfxInstance {
    const content = effect.template.clone(true);
    relinkSubEmitters(effect.template, content);
    const root = new Group();
    root.name = `Unity VFX ${effect.effectId} instance`;
    root.add(content);
    const templateSystems = collectSystems(effect.template);
    const systems = collectSystems(content);
    if (effect.applyUnityExporterExtension) {
      repairUnitySubEmitterSemantics(content);
      installUnityExporterBehaviors(content, this.options.camera);
    }
    const companionHeadBatches: VfxInstance['companionHeadBatches'] = [];
    for (let index = 0; index < systems.length; index += 1) {
      const system = systems[index];
      const templateSystem = templateSystems[index];
      if (!system) continue;
      // Quarks 0.17.1 ParticleSystem.clone() omits prewarm from its constructor parameters.
      if (templateSystem) system.prewarm = templateSystem.prewarm;
      system.stop();
      if (effect.applyUnityExporterExtension) {
        prepareUnityMaterialBatch(system);
        repairUnityAlphaMeshCulling(system);
      }
      this.batchRenderer.addSystem(system);
      if (effect.applyUnityExporterExtension) configureUnityMaterialBatch(this.batchRenderer, system);
      installParticleSpawnBudget(system);
      if (effect.applyUnityExporterExtension) {
        const headMetadata = readUnityParticleHeadMetadata(system.emitter as unknown as Object3D);
        if (headMetadata) {
          const resourceKey = `${headMetadata.geometry}:${headMetadata.material}`;
          const resources = effect.headResources.get(resourceKey);
          if (!resources) throw new Error(`Missing companion Particle head resources for ${system.emitter.uuid}.`);
          const headMaterial = resources.material.clone();
          applyUnityParticleHeadMaterialSemantics(headMaterial, headMetadata);
          const layers = new Layers();
          layers.mask = headMetadata.layers;
          const batch = new SpriteBatch({
            instancingGeometry: resources.geometry,
            material: headMaterial,
            uTileCount: headMetadata.uTileCount,
            vTileCount: headMetadata.vTileCount,
            blendTiles: headMetadata.blendTiles,
            // Companion heads are created outside the parsed JSON tree, so
            // apply the same no-depth fallback as configureSoftParticles.
            // Sampling a null depth texture makes the head fragment vanish.
            softParticles: headMetadata.softParticles && Boolean(this.batchRenderer.depthTexture),
            softNearFade: headMetadata.softNearFade,
            softFarFade: headMetadata.softFarFade,
            renderMode: headMetadata.renderMode,
            renderOrder: headMetadata.renderOrder,
            layers
          });
          applyUnityParticleHeadBatchSemantics(batch, headMetadata);
          const rendererPivot = unityRendererPivotMetadata.get(system);
          if (rendererPivot) {
            configureUnityRendererPivotShader(
              (batch as unknown as {
                material: Parameters<typeof configureUnityRendererPivotShader>[0];
              }).material,
              rendererPivot
            );
          }
          if (this.batchRenderer.depthTexture) batch.applyDepthTexture(this.batchRenderer.depthTexture);
          batch.name = `Unity VFX companion head ${system.emitter.name}`;
          batch.addSystem(system);
          this.batchRenderer.add(batch);
          companionHeadBatches.push({
            batch,
            system,
            worldSpace: headMetadata.worldSpace,
            rendererEmitterSettings: headMetadata.rendererEmitterSettings
          });
        }
      }
    }
    return { root, systems, companionHeadBatches, state: 'idle', sequence: 0, handle: null, scaleApproximationReported: false };
  }

  private releaseInstance(instance: VfxInstance, countTelemetry: boolean): void {
    if (instance.state === 'idle') return;
    for (const system of instance.systems) system.stop();
    for (const companion of instance.companionHeadBatches) {
      companion.batch.visible = false;
      companion.batch.geometry.instanceCount = 0;
    }
    instance.root.removeFromParent();
    instance.root.position.set(0, 0, 0);
    instance.root.quaternion.identity();
    instance.root.scale.set(1, 1, 1);
    instance.scaleApproximationReported = false;
    instance.state = 'idle';
    instance.sequence = 0;
    if (instance.handle) {
      instance.handle.markReleased();
      instance.handle = null;
    }
    if (countTelemetry) this.telemetry.released += 1;
  }

  private async fetchJson(url: string): Promise<unknown> {
    const response = await this.fetcher(url);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return response.json();
  }

  private refreshCounts(): void {
    const instances = [...this.pools.values()].flatMap((pool) => pool.instances);
    this.telemetry.activeInstances = instances.filter((instance) => instance.state !== 'idle').length;
    this.telemetry.idleInstances = instances.filter((instance) => instance.state === 'idle').length;
    this.telemetry.allocatedInstances = instances.length;
    this.telemetry.particleCount = instances.reduce((sum, instance) =>
      sum + instance.systems.reduce((systemSum, system) => systemSum + system.particleNum, 0), 0);
    this.telemetry.activeSystemCount = instances.reduce((sum, instance) => {
      if (instance.state === 'idle') return sum;
      return sum + instance.systems.filter((system) =>
        system.particleNum > 0 || !(system as unknown as { emitEnded?: boolean }).emitEnded).length;
    }, 0);
    this.telemetry.batchCount = this.disposed ? 0 : this.batchRenderer.batches.length;
  }

  private assertActive(): void {
    if (this.disposed) throw new Error('VFX runtime is disposed.');
  }
}

function restartParticleSystem(system: ParticleSystem): void {
  system.restart();
  const quarksInternals = system as unknown as {
    memory: number[];
    emissionOverTime: { startGen(memory: number[]): void };
  };
  quarksInternals.emissionOverTime.startGen(quarksInternals.memory);
}

export function createVfxRuntime(options: CreateVfxRuntimeOptions): VfxRuntime {
  return new VfxRuntimeImpl(options);
}

async function parseQuarksJson(json: unknown): Promise<Object3D> {
  return new Promise<Object3D>((resolve, reject) => {
    const manager = new LoadingManager();
    manager.onError = (url) => reject(new Error(`Quarks resource failed to load: ${url}`));
    const loader = new QuarksLoader(manager);
    let parsed: Object3D;
    let settled = false;
    const finish = async (object: Object3D): Promise<void> => {
      if (settled) return;
      try {
        await resolveExporterAlphaMapTextures(object, json, manager);
        settled = true;
        resolve(object);
      } catch (error) {
        settled = true;
        reject(error);
      }
    };
    try {
      parsed = loader.parse<Object3D>(json, (object) => { void finish(object); });
      const images = isRecord(json) && Array.isArray(json.images) ? json.images : [];
      if (images.length === 0) void finish(parsed);
    } catch (error) {
      reject(error);
    }
  });
}

async function resolveExporterAlphaMapTextures(
  root: Object3D,
  json: unknown,
  manager: LoadingManager
): Promise<void> {
  if (!isRecord(json) || !Array.isArray(json.textures) || !Array.isArray(json.images)) return;
  const textureByUuid = new Map<string, Record<string, unknown>>();
  for (const value of json.textures) {
    if (isRecord(value) && typeof value.uuid === 'string') textureByUuid.set(value.uuid, value);
  }
  const imageByUuid = new Map<string, Record<string, unknown>>();
  for (const value of json.images) {
    if (isRecord(value) && typeof value.uuid === 'string') imageByUuid.set(value.uuid, value);
  }
  const loading = new Map<string, Promise<unknown>>();
  const loader = new TextureLoader(manager);
  const systems = collectSystems(root);
  for (const system of systems) {
    const material = system.rendererSettings.material as Material & {
      userData?: { unityParticleQuarksAlphaMaps?: Array<{ texture?: unknown }> };
    };
    for (const entry of material.userData?.unityParticleQuarksAlphaMaps ?? []) {
      if (!entry || typeof entry.texture !== 'string') continue;
      const textureUuid = entry.texture;
      const textureJson = textureByUuid.get(textureUuid);
      const imageUuid = textureJson && typeof textureJson.image === 'string' ? textureJson.image : undefined;
      const imageJson = imageUuid === undefined ? undefined : imageByUuid.get(imageUuid);
      const url = imageJson && typeof imageJson.url === 'string' ? imageJson.url : undefined;
      if (!url) continue;
      let pending = loading.get(textureUuid);
      if (!pending) {
        pending = loader.loadAsync(url);
        loading.set(textureUuid, pending);
      }
      entry.texture = await pending;
    }
  }
}

function collectSystems(root: Object3D): ParticleSystem[] {
  const systems: ParticleSystem[] = [];
  root.traverse((child) => {
    const possible = child as Object3D & { system?: ParticleSystem };
    if (child.type === 'ParticleEmitter' && possible.system) systems.push(possible.system);
  });
  return systems;
}

function relinkSubEmitters(source: Object3D, clone: Object3D): void {
  type EmitterObject = Object3D & { system?: ParticleSystem; __unityParticleQuarksSourceUuid?: string };
  const sourceEmitters: EmitterObject[] = [];
  const cloneEmitters: EmitterObject[] = [];
  source.traverse((child) => { if (child.type === 'ParticleEmitter') sourceEmitters.push(child as typeof sourceEmitters[number]); });
  clone.traverse((child) => { if (child.type === 'ParticleEmitter') cloneEmitters.push(child as typeof cloneEmitters[number]); });
  const mapping = new Map<Object3D, Object3D>();
  for (let index = 0; index < Math.min(sourceEmitters.length, cloneEmitters.length); index += 1) {
    const sourceEmitter = sourceEmitters[index];
    const cloneEmitter = cloneEmitters[index];
    if (sourceEmitter && cloneEmitter) {
      mapping.set(sourceEmitter, cloneEmitter);
      cloneEmitter.__unityParticleQuarksSourceUuid = sourceEmitter.uuid;
    }
  }
  for (const emitter of cloneEmitters) {
    for (const behavior of emitter.system?.behaviors ?? []) {
      const sub = behavior as unknown as {
        type?: string;
        particleSystem?: ParticleSystem;
        subParticleSystem?: Object3D;
      };
      if (sub.type === 'EmitSubParticleSystem' && emitter.system) sub.particleSystem = emitter.system;
      if (sub.type === 'EmitSubParticleSystem' && sub.subParticleSystem && mapping.has(sub.subParticleSystem)) {
        const mapped = mapping.get(sub.subParticleSystem);
        if (mapped) sub.subParticleSystem = mapped;
      }
    }
  }
}

interface QuarksMatrixLike {
  elements: number[];
  fromArray(array: number[]): unknown;
}

interface UnityCurveSample {
  evaluate(t: number): number;
}

interface UnityVelocityParticleState {
  x: UnityCurveSample;
  y: UnityCurveSample;
  z: UnityCurveSample;
  orbitalX: UnityCurveSample;
  orbitalY: UnityCurveSample;
  orbitalZ: UnityCurveSample;
  orbitalOffsetX: UnityCurveSample;
  orbitalOffsetY: UnityCurveSample;
  orbitalOffsetZ: UnityCurveSample;
  radial: UnityCurveSample;
  speedModifier: UnityCurveSample;
  previousX: number;
  previousY: number;
  previousZ: number;
}

interface UnityVelocityMetadata {
  basisX: [number, number, number];
  basisY: [number, number, number];
  basisZ: [number, number, number];
  origin: [number, number, number];
  x: Record<string, unknown>;
  y: Record<string, unknown>;
  z: Record<string, unknown>;
  orbitalX: Record<string, unknown>;
  orbitalY: Record<string, unknown>;
  orbitalZ: Record<string, unknown>;
  orbitalOffsetX: Record<string, unknown>;
  orbitalOffsetY: Record<string, unknown>;
  orbitalOffsetZ: Record<string, unknown>;
  radial: Record<string, unknown>;
  speedModifier: Record<string, unknown>;
}

type UnityCurveFactory = () => UnityCurveSample;

class UnityVelocityOverLifetimeBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksVelocityOverLifetime';
  private readonly xFactory: UnityCurveFactory;
  private readonly yFactory: UnityCurveFactory;
  private readonly zFactory: UnityCurveFactory;
  private readonly orbitalXFactory: UnityCurveFactory;
  private readonly orbitalYFactory: UnityCurveFactory;
  private readonly orbitalZFactory: UnityCurveFactory;
  private readonly orbitalOffsetXFactory: UnityCurveFactory;
  private readonly orbitalOffsetYFactory: UnityCurveFactory;
  private readonly orbitalOffsetZFactory: UnityCurveFactory;
  private readonly radialFactory: UnityCurveFactory;
  private readonly speedModifierFactory: UnityCurveFactory;
  private readonly moduleToStorage = new Matrix4();
  private readonly storageToModule = new Matrix4();
  private readonly modulePosition = new Vector3();
  private readonly oldOrbitalPosition = new Vector3();
  private readonly orbitalRotation = new Quaternion();
  private readonly orbitalEuler = new Euler();
  private states = new WeakMap<Particle, UnityVelocityParticleState>();

  constructor(private readonly metadata: UnityVelocityMetadata) {
    this.xFactory = compileUnityCurve(metadata.x, 'velocity.x');
    this.yFactory = compileUnityCurve(metadata.y, 'velocity.y');
    this.zFactory = compileUnityCurve(metadata.z, 'velocity.z');
    this.orbitalXFactory = compileUnityCurve(metadata.orbitalX, 'velocity.orbitalX');
    this.orbitalYFactory = compileUnityCurve(metadata.orbitalY, 'velocity.orbitalY');
    this.orbitalZFactory = compileUnityCurve(metadata.orbitalZ, 'velocity.orbitalZ');
    this.orbitalOffsetXFactory = compileUnityCurve(metadata.orbitalOffsetX, 'velocity.orbitalOffsetX');
    this.orbitalOffsetYFactory = compileUnityCurve(metadata.orbitalOffsetY, 'velocity.orbitalOffsetY');
    this.orbitalOffsetZFactory = compileUnityCurve(metadata.orbitalOffsetZ, 'velocity.orbitalOffsetZ');
    this.radialFactory = compileUnityCurve(metadata.radial, 'velocity.radial');
    this.speedModifierFactory = compileUnityCurve(metadata.speedModifier, 'velocity.speedModifier');
    const x = metadata.basisX;
    const y = metadata.basisY;
    const z = metadata.basisZ;
    this.moduleToStorage.set(
      x[0], y[0], z[0], 0,
      x[1], y[1], z[1], 0,
      x[2], y[2], z[2], 0,
      0, 0, 0, 1
    );
    this.storageToModule.copy(this.moduleToStorage).invert();
  }

  initialize(particle: Particle): void {
    const state: UnityVelocityParticleState = {
      x: this.xFactory(),
      y: this.yFactory(),
      z: this.zFactory(),
      orbitalX: this.orbitalXFactory(),
      orbitalY: this.orbitalYFactory(),
      orbitalZ: this.orbitalZFactory(),
      orbitalOffsetX: this.orbitalOffsetXFactory(),
      orbitalOffsetY: this.orbitalOffsetYFactory(),
      orbitalOffsetZ: this.orbitalOffsetZFactory(),
      radial: this.radialFactory(),
      speedModifier: this.speedModifierFactory(),
      previousX: 0,
      previousY: 0,
      previousZ: 0
    };
    const velocity = this.evaluate(state, 0);
    particle.velocity.x += velocity[0];
    particle.velocity.y += velocity[1];
    particle.velocity.z += velocity[2];
    state.previousX = velocity[0];
    state.previousY = velocity[1];
    state.previousZ = velocity[2];
    this.states.set(particle, state);
  }

  update(particle: Particle, delta: number): void {
    const state = this.states.get(particle);
    if (!state) return;
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    const velocity = this.evaluate(state, t);
    particle.velocity.x += velocity[0] - state.previousX;
    particle.velocity.y += velocity[1] - state.previousY;
    particle.velocity.z += velocity[2] - state.previousZ;
    state.previousX = velocity[0];
    state.previousY = velocity[1];
    state.previousZ = velocity[2];
    this.applyOrbitalPositionDelta(particle, state, t, delta);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityVelocityOverLifetimeBehavior(this.metadata);
  }

  reset(): void {
    // Quarks calls reset at every emitter loop boundary while particles from
    // the previous loop are still alive. Reinitialization overwrites state
    // when a particle object is reused by a restarted pool instance.
  }

  private evaluate(state: UnityVelocityParticleState, t: number): [number, number, number] {
    const x = state.x.evaluate(t);
    const y = state.y.evaluate(t);
    const z = state.z.evaluate(t);
    return [
      x * this.metadata.basisX[0] + y * this.metadata.basisY[0] + z * this.metadata.basisZ[0],
      x * this.metadata.basisX[1] + y * this.metadata.basisY[1] + z * this.metadata.basisZ[1],
      x * this.metadata.basisX[2] + y * this.metadata.basisY[2] + z * this.metadata.basisZ[2]
    ];
  }

  private applyOrbitalPositionDelta(
    particle: Particle,
    state: UnityVelocityParticleState,
    t: number,
    delta: number
  ): void {
    if (delta <= 0) return;
    const orbitalX = state.orbitalX.evaluate(t);
    const orbitalY = state.orbitalY.evaluate(t);
    const orbitalZ = state.orbitalZ.evaluate(t);
    const radial = state.radial.evaluate(t);
    if (Math.abs(orbitalX) + Math.abs(orbitalY) + Math.abs(orbitalZ) + Math.abs(radial) <= 1e-12) return;

    // Mirrors Unity Modules/ParticleSystem/Modules/VelocityModule.cpp:
    // worldToLocal(position) - offset, Euler rotation, then radial displacement.
    this.modulePosition.set(
      particle.position.x - this.metadata.origin[0],
      particle.position.y - this.metadata.origin[1],
      particle.position.z - this.metadata.origin[2]
    ).applyMatrix4(this.storageToModule);
    this.modulePosition.x -= state.orbitalOffsetX.evaluate(t);
    this.modulePosition.y -= state.orbitalOffsetY.evaluate(t);
    this.modulePosition.z -= state.orbitalOffsetZ.evaluate(t);
    this.oldOrbitalPosition.copy(this.modulePosition);

    const scaledDelta = delta * state.speedModifier.evaluate(t);
    const degreesToRadians = Math.PI / 180;
    this.orbitalEuler.set(
      orbitalX * scaledDelta * degreesToRadians,
      orbitalY * scaledDelta * degreesToRadians,
      orbitalZ * scaledDelta * degreesToRadians,
      'ZXY'
    );
    this.orbitalRotation.setFromEuler(this.orbitalEuler);
    this.modulePosition.applyQuaternion(this.orbitalRotation);
    if (Math.abs(radial) > 1e-12 && this.modulePosition.lengthSq() > 1e-24) {
      const radialDistance = radial * scaledDelta;
      const inverseLength = radialDistance / Math.sqrt(this.modulePosition.lengthSq());
      this.modulePosition.x += this.modulePosition.x * inverseLength;
      this.modulePosition.y += this.modulePosition.y * inverseLength;
      this.modulePosition.z += this.modulePosition.z * inverseLength;
    }
    this.modulePosition.sub(this.oldOrbitalPosition);
    const storageDelta = transformBasis(
      [this.modulePosition.x, this.modulePosition.y, this.modulePosition.z],
      this.metadata.basisX,
      this.metadata.basisY,
      this.metadata.basisZ
    );
    particle.position.x += storageDelta[0];
    particle.position.y += storageDelta[1];
    particle.position.z += storageDelta[2];
  }
}

interface UnityForceMetadata {
  basisX: [number, number, number];
  basisY: [number, number, number];
  basisZ: [number, number, number];
  x: Record<string, unknown>;
  y: Record<string, unknown>;
  z: Record<string, unknown>;
}

interface UnityForceParticleState {
  x: UnityCurveSample;
  y: UnityCurveSample;
  z: UnityCurveSample;
}

class UnityForceOverLifetimeBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksForceOverLifetime';
  private readonly xFactory: UnityCurveFactory;
  private readonly yFactory: UnityCurveFactory;
  private readonly zFactory: UnityCurveFactory;
  private states = new WeakMap<Particle, UnityForceParticleState>();

  constructor(private readonly metadata: UnityForceMetadata) {
    this.xFactory = compileUnityCurve(metadata.x, 'force.x');
    this.yFactory = compileUnityCurve(metadata.y, 'force.y');
    this.zFactory = compileUnityCurve(metadata.z, 'force.z');
  }

  initialize(particle: Particle): void {
    this.states.set(particle, {
      x: this.xFactory(),
      y: this.yFactory(),
      z: this.zFactory()
    });
  }

  update(particle: Particle, delta: number): void {
    const state = this.states.get(particle);
    if (!state) return;
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    const x = state.x.evaluate(t);
    const y = state.y.evaluate(t);
    const z = state.z.evaluate(t);
    particle.velocity.x += delta * (x * this.metadata.basisX[0] + y * this.metadata.basisY[0] + z * this.metadata.basisZ[0]);
    particle.velocity.y += delta * (x * this.metadata.basisX[1] + y * this.metadata.basisY[1] + z * this.metadata.basisZ[1]);
    particle.velocity.z += delta * (x * this.metadata.basisX[2] + y * this.metadata.basisY[2] + z * this.metadata.basisZ[2]);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityForceOverLifetimeBehavior(this.metadata);
  }

  reset(): void {
    // Preserve state for particles that outlive the emitter duration.
  }
}

interface UnityGravityMetadata {
  acceleration: [number, number, number];
  modifier: Record<string, unknown>;
}

class UnityGravityBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksGravity';
  private readonly modifierFactory: UnityCurveFactory;
  private states = new WeakMap<Particle, UnityCurveSample>();

  constructor(private readonly metadata: UnityGravityMetadata) {
    this.modifierFactory = compileUnityCurve(metadata.modifier, 'gravity.modifier');
  }

  initialize(particle: Particle): void {
    this.states.set(particle, this.modifierFactory());
  }

  update(particle: Particle, delta: number): void {
    const modifier = this.states.get(particle);
    if (!modifier) return;
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    const value = modifier.evaluate(t) * delta;
    particle.velocity.x += this.metadata.acceleration[0] * value;
    particle.velocity.y += this.metadata.acceleration[1] * value;
    particle.velocity.z += this.metadata.acceleration[2] * value;
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityGravityBehavior(this.metadata);
  }

  reset(): void {
    // Preserve state for particles that outlive the emitter duration.
  }
}

interface UnityNoiseMetadata {
  particleToNoiseBasisX: [number, number, number];
  particleToNoiseBasisY: [number, number, number];
  particleToNoiseBasisZ: [number, number, number];
  noiseToParticleBasisX: [number, number, number];
  noiseToParticleBasisY: [number, number, number];
  noiseToParticleBasisZ: [number, number, number];
  randomSeed: number;
  separateAxes: boolean;
  frequency: number;
  damping: boolean;
  quality: UnityNoiseQuality;
  octaveCount: number;
  octaveMultiplier: number;
  octaveScale: number;
  strengthX: Record<string, unknown>;
  strengthY: Record<string, unknown>;
  strengthZ: Record<string, unknown>;
  positionAmount: Record<string, unknown>;
  scrollSpeed: Record<string, unknown>;
  remapEnabled: boolean;
  remapX: Record<string, unknown>;
  remapY: Record<string, unknown>;
  remapZ: Record<string, unknown>;
}

async function loadCompanionHeadResources(json: unknown): Promise<Map<string, CompanionHeadResources>> {
  const resources = new Map<string, CompanionHeadResources>();
  if (!isRecord(json) || !isRecord(json.object)) return resources;
  const heads: UnityParticleHeadMetadata[] = [];
  const visit = (value: unknown): void => {
    if (!isRecord(value)) return;
    const userData = isRecord(value.userData) ? value.userData : null;
    const exporterData = userData && (isRecord(userData.unityParticleQuarks)
      ? userData.unityParticleQuarks
      : isRecord(userData.unityParticleQuarks) ? userData.unityParticleQuarks : null);
    if (exporterData && isRecord(exporterData.particleHead)) {
      const parsed = parseRawParticleHeadMetadata(exporterData.particleHead);
      if (parsed) heads.push(parsed);
    }
    if (Array.isArray(value.children)) for (const child of value.children) visit(child);
  };
  visit(json.object);
  const unique = new Map<string, UnityParticleHeadMetadata>();
  for (const head of heads) unique.set(`${head.geometry}:${head.material}`, head);
  for (const [key, head] of unique) {
    const manager = new LoadingManager();
    const loader = new QuarksLoader(manager);
    const resourceJson = {
      metadata: isRecord(json.metadata) ? json.metadata : { version: 4.7, type: 'Object' },
      geometries: Array.isArray(json.geometries) ? json.geometries : [],
      materials: Array.isArray(json.materials) ? json.materials : [],
      textures: Array.isArray(json.textures) ? json.textures : [],
      images: Array.isArray(json.images) ? json.images : [],
      object: {
        uuid: `unity-particle-head-${key.replace(/[^a-zA-Z0-9_-]/g, '_')}`,
        type: 'Mesh',
        geometry: head.geometry,
        material: head.material
      }
    };
    const mesh = await loader.parseAsync(resourceJson) as Object3D & {
      geometry?: BufferGeometry;
      material?: Material | Material[];
    };
    const material = Array.isArray(mesh.material) ? mesh.material[0] : mesh.material;
    if (!mesh.geometry || !material) throw new Error(`Companion Particle head resource ${key} is incomplete.`);
    resources.set(key, { geometry: mesh.geometry, material });
  }
  return resources;
}

function parseRawParticleHeadMetadata(value: Record<string, unknown>): UnityParticleHeadMetadata | null {
  if (value.schemaVersion !== 'unity_particle_quarks_exporter.particle_head.v1') return null;
  if (typeof value.geometry !== 'string' || typeof value.material !== 'string' ||
      !Number.isInteger(value.renderMode) || ![0, 1, 2, 4, 5].includes(value.renderMode as number) ||
      !Number.isFinite(value.renderOrder) || !Number.isInteger(value.layers) ||
      !Number.isInteger(value.uTileCount) || !Number.isInteger(value.vTileCount) ||
      typeof value.blendTiles !== 'boolean' || typeof value.softParticles !== 'boolean' ||
      !Number.isFinite(value.softFarFade) || !Number.isFinite(value.softNearFade) ||
      typeof value.worldSpace !== 'boolean' || !isRecord(value.rotation) ||
      (value.rendererEmitterSettings !== undefined &&
        (!isRecord(value.rendererEmitterSettings) ||
          !Number.isFinite(value.rendererEmitterSettings.speedFactor) ||
          !Number.isFinite(value.rendererEmitterSettings.lengthFactor))) ||
      (value.restoreMaterialColor !== undefined && typeof value.restoreMaterialColor !== 'boolean') ||
      (value.materialProjectColorSpace !== undefined && value.materialProjectColorSpace !== 'gamma' && value.materialProjectColorSpace !== 'linear') ||
      !['local', 'velocity', 'view', 'facing', 'billboard'].includes(String(value.rotation.alignment)) ||
      value.rotation.preserveAuthored !== true) return null;
  if (value.renderMode === 1 && value.rendererEmitterSettings === undefined) return null;
  const rendererEmitterSettings = value.rendererEmitterSettings === undefined
    ? undefined
    : value.rendererEmitterSettings as Record<string, unknown>;
  return {
    geometry: value.geometry,
    material: value.material,
    materialColor: readOptionalHeadMaterialColor(value),
    restoreMaterialColor: value.restoreMaterialColor === undefined ? undefined : value.restoreMaterialColor as boolean,
    materialProjectColorSpace: value.materialProjectColorSpace === undefined
      ? undefined
      : value.materialProjectColorSpace as UnityProjectColorSpace,
    renderMode: value.renderMode as UnityParticleHeadMetadata['renderMode'],
    renderOrder: value.renderOrder as number,
    layers: value.layers as number,
    uTileCount: value.uTileCount as number,
    vTileCount: value.vTileCount as number,
    blendTiles: value.blendTiles as boolean,
    softParticles: value.softParticles as boolean,
    softFarFade: value.softFarFade as number,
    softNearFade: value.softNearFade as number,
    worldSpace: value.worldSpace as boolean,
    rendererEmitterSettings: rendererEmitterSettings === undefined
      ? undefined
      : {
        speedFactor: rendererEmitterSettings.speedFactor as number,
        lengthFactor: rendererEmitterSettings.lengthFactor as number
      },
    rotation: {
      alignment: value.rotation.alignment as UnityParticleHeadMetadata['rotation']['alignment'],
      preserveAuthored: true
    }
  };
}

interface UnityNoiseParticleState {
  strengthX: UnityCurveSample;
  strengthY: UnityCurveSample;
  strengthZ: UnityCurveSample;
  positionAmount: UnityCurveSample;
  remapX: UnityCurveSample;
  remapY: UnityCurveSample;
  remapZ: UnityCurveSample;
}

interface UnityParticleLightsMetadata {
  randomSeed: number;
  ratio: number;
  randomDistribution: boolean;
  useParticleColor: boolean;
  sizeAffectsRange: boolean;
  alphaAffectsIntensity: boolean;
  maxLights: number;
  uses3DSize: boolean;
  meshSize: boolean;
  renderScaleMode: 'hierarchy' | 'local' | 'shape';
  sourceRenderScale: [number, number, number];
  particleColorMultiplier: [number, number, number, number];
  range: Record<string, unknown>;
  intensity: Record<string, unknown>;
  light: {
    color: [number, number, number];
    intensity: number;
    range: number;
    cullingMask: number;
    shadowMode: 'none' | 'hard' | 'soft';
  };
}

interface UnityParticleLightState {
  hasLight: boolean;
  range: UnityCurveSample | null;
  intensity: UnityCurveSample | null;
  birthWorldSizeScale: [number, number, number] | null;
}

class UnityXorshift128 {
  private x = 0;
  private y = 0;
  private z = 0;
  private w = 0;

  constructor(seed: number) {
    this.setSeed(seed);
  }

  setSeed(seed: number): void {
    this.x = seed >>> 0;
    this.y = (Math.imul(this.x, 1812433253) + 1) >>> 0;
    this.z = (Math.imul(this.y, 1812433253) + 1) >>> 0;
    this.w = (Math.imul(this.z, 1812433253) + 1) >>> 0;
  }

  nextFloat(): number {
    const t = (this.x ^ (this.x << 11)) >>> 0;
    this.x = this.y;
    this.y = this.z;
    this.z = this.w;
    this.w = ((this.w ^ (this.w >>> 19)) ^ (t ^ (t >>> 8))) >>> 0;
    return (this.w & 0x007fffff) / 8388607;
  }
}

class UnityParticleLightsAdapter {
  private readonly assignmentRandom: UnityXorshift128;
  private readonly rangeRandom: UnityXorshift128;
  private readonly intensityRandom: UnityXorshift128;
  private readonly rangeFactory: UnityCurveFactory;
  private readonly intensityFactory: UnityCurveFactory;
  private states = new WeakMap<Particle, UnityParticleLightState>();
  private readonly activeLights = new Map<Particle, PointLight>();
  private readonly freeLights: PointLight[] = [];
  private readonly activeParticles = new Set<Particle>();
  private readonly point = new Vector3();
  private readonly worldScale = new Vector3();
  private readonly parentMatrix = new Matrix4();
  private regularEmissionCounter = 0;

  constructor(
    private readonly emitter: Object3D,
    private readonly system: ParticleSystem,
    private readonly metadata: UnityParticleLightsMetadata
  ) {
    this.assignmentRandom = new UnityXorshift128(metadata.randomSeed);
    this.rangeRandom = new UnityXorshift128((metadata.randomSeed + 0xaaa0982a) >>> 0);
    this.intensityRandom = new UnityXorshift128((metadata.randomSeed + 0x5b5b290a) >>> 0);
    this.rangeFactory = compileUnityCurve(metadata.range, 'lights.range', () => this.rangeRandom.nextFloat());
    this.intensityFactory = compileUnityCurve(metadata.intensity, 'lights.intensity', () => this.intensityRandom.nextFloat());
  }

  assignSpawnedParticles(fromIndex: number, emissionMatrix: { elements: ArrayLike<number> }): void {
    const elements = emissionMatrix.elements;
    const birthWorldSizeScale: [number, number, number] | null = this.system.worldSpace
      ? [
          Math.hypot(elements[0] ?? 0, elements[1] ?? 0, elements[2] ?? 0),
          Math.hypot(elements[4] ?? 0, elements[5] ?? 0, elements[6] ?? 0),
          Math.hypot(elements[8] ?? 0, elements[9] ?? 0, elements[10] ?? 0)
        ]
      : null;
    for (let index = fromIndex; index < this.system.particleNum; index += 1) {
      const particle = this.system.particles[index];
      if (!particle) continue;
      const hasLight = this.selectLight();
      this.states.set(particle, {
        hasLight,
        range: hasLight ? this.rangeFactory() : null,
        intensity: hasLight ? this.intensityFactory() : null,
        birthWorldSizeScale
      });
    }
  }

  sync(): void {
    this.activeParticles.clear();
    this.emitter.updateWorldMatrix(true, false);
    this.emitter.getWorldScale(this.worldScale);
    const renderScale = this.renderScale();
    let selectedCount = 0;

    for (let index = 0; index < this.system.particleNum; index += 1) {
      const particle = this.system.particles[index];
      if (!particle) continue;
      const state = this.states.get(particle);
      if (!state?.hasLight) continue;
      if (selectedCount >= this.metadata.maxLights) break;
      selectedCount += 1;

      const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
      const rangeMultiplier = state.range?.evaluate(t) ?? 0;
      const intensityMultiplier = state.intensity?.evaluate(t) ?? 0;
      const size = this.metadata.sizeAffectsRange ? this.particleSize(particle, state) : 1;
      const range = Math.max(0, this.metadata.light.range * rangeMultiplier * renderScale * size);
      const alpha = this.metadata.alphaAffectsIntensity
        ? this.sourceParticleColor(particle.color.w, 3)
        : 1;
      const intensity = Math.max(0, this.metadata.light.intensity * intensityMultiplier * alpha);
      if (range <= Number.EPSILON || intensity <= Number.EPSILON) continue;

      const light = this.activeLights.get(particle) ?? this.acquireLight(particle);
      this.activeParticles.add(particle);
      light.visible = true;
      light.distance = range;
      light.intensity = intensity;
      if (this.metadata.useParticleColor) {
        light.color.setRGB(
          this.sourceParticleColor(particle.color.x, 0),
          this.sourceParticleColor(particle.color.y, 1),
          this.sourceParticleColor(particle.color.z, 2)
        );
      } else {
        light.color.setRGB(...this.metadata.light.color);
      }
      this.updatePosition(light, particle);
    }

    for (const [particle, light] of this.activeLights) {
      if (this.activeParticles.has(particle)) continue;
      this.activeLights.delete(particle);
      this.releaseLight(light);
    }
  }

  restart(): void {
    this.assignmentRandom.setSeed(this.metadata.randomSeed);
    this.rangeRandom.setSeed((this.metadata.randomSeed + 0xaaa0982a) >>> 0);
    this.intensityRandom.setSeed((this.metadata.randomSeed + 0x5b5b290a) >>> 0);
    this.regularEmissionCounter = 0;
    this.states = new WeakMap<Particle, UnityParticleLightState>();
    for (const light of this.activeLights.values()) this.releaseLight(light);
    this.activeLights.clear();
    this.activeParticles.clear();
  }

  private selectLight(): boolean {
    if (this.metadata.randomDistribution) {
      return this.assignmentRandom.nextFloat() <= this.metadata.ratio;
    }
    this.regularEmissionCounter += this.metadata.ratio;
    if (this.regularEmissionCounter < 1) return false;
    this.regularEmissionCounter -= 1;
    return true;
  }

  private acquireLight(particle: Particle): PointLight {
    const light = this.freeLights.pop() ?? this.createLight();
    this.activeLights.set(particle, light);
    return light;
  }

  private createLight(): PointLight {
    const light = new PointLight(0xffffff, 0, 0, 2);
    light.name = `Unity particle light: ${this.emitter.name}`;
    light.visible = false;
    light.castShadow = this.metadata.light.shadowMode !== 'none';
    light.layers.mask = this.metadata.light.cullingMask >>> 0;
    this.emitter.add(light);
    return light;
  }

  private releaseLight(light: PointLight): void {
    light.visible = false;
    light.intensity = 0;
    light.distance = 0;
    this.freeLights.push(light);
  }

  private renderScale(): number {
    if (this.metadata.renderScaleMode === 'shape') return 1;
    const scale: [number, number, number] = this.metadata.renderScaleMode === 'hierarchy'
      ? [this.worldScale.x, this.worldScale.y, this.worldScale.z]
      : this.metadata.sourceRenderScale;
    return Math.max(Number.EPSILON, Math.cbrt(Math.abs(scale[0] * scale[1] * scale[2])));
  }

  private particleSize(particle: Particle, state: UnityParticleLightState): number {
    const birthScale = state.birthWorldSizeScale;
    const sizeX = particle.size.x / Math.max(Number.EPSILON, birthScale?.[0] ?? 1);
    const sizeY = particle.size.y / Math.max(Number.EPSILON, birthScale?.[1] ?? 1);
    const sizeZ = particle.size.z / Math.max(Number.EPSILON, birthScale?.[2] ?? 1);
    if (!this.metadata.uses3DSize) return Math.max(0, sizeX);
    if (this.metadata.meshSize) {
      return Math.max(0, Math.cbrt(Math.max(0, sizeX * sizeY * sizeZ)));
    }
    return Math.max(0, Math.sqrt(Math.max(0, sizeX * sizeY)));
  }

  private sourceParticleColor(value: number, channel: 0 | 1 | 2 | 3): number {
    const multiplier = this.metadata.particleColorMultiplier[channel];
    const restored = Math.abs(multiplier) <= 1e-6 ? value : value / multiplier;
    return Math.min(1, Math.max(0, restored));
  }

  private updatePosition(light: PointLight, particle: Particle): void {
    this.point.set(particle.position.x, particle.position.y, particle.position.z);
    if (this.system.worldSpace) {
      this.emitter.worldToLocal(this.point);
    } else if (particle.parentMatrix) {
      this.parentMatrix.fromArray(particle.parentMatrix.elements);
      this.point.applyMatrix4(this.parentMatrix);
      this.emitter.worldToLocal(this.point);
    }
    light.position.copy(this.point);
  }
}

type UnityNoiseParticle = Particle & {
  __unityParticleQuarksNoiseAnimatedVelocity?: [number, number, number];
};

function clearUnityNoiseAnimatedVelocity(particle: Particle): void {
  const state = particle as UnityNoiseParticle;
  const contribution = state.__unityParticleQuarksNoiseAnimatedVelocity;
  if (!contribution) return;
  particle.velocity.x -= contribution[0];
  particle.velocity.y -= contribution[1];
  particle.velocity.z -= contribution[2];
  contribution[0] = 0;
  contribution[1] = 0;
  contribution[2] = 0;
}

function replaceUnityNoiseAnimatedVelocity(
  particle: Particle,
  velocity: [number, number, number]
): void {
  const state = particle as UnityNoiseParticle;
  clearUnityNoiseAnimatedVelocity(particle);
  particle.velocity.x += velocity[0];
  particle.velocity.y += velocity[1];
  particle.velocity.z += velocity[2];
  state.__unityParticleQuarksNoiseAnimatedVelocity = [velocity[0], velocity[1], velocity[2]];
}

class UnityNoiseAnimatedVelocityClearBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksNoiseAnimatedVelocityClear';

  initialize(particle: Particle): void {
    const state = particle as UnityNoiseParticle;
    state.__unityParticleQuarksNoiseAnimatedVelocity = [0, 0, 0];
  }

  update(particle: Particle): void {
    clearUnityNoiseAnimatedVelocity(particle);
  }

  frameUpdate(): void {}
  toJSON(): Record<string, unknown> { return { type: this.type }; }
  clone(): Behavior { return new UnityNoiseAnimatedVelocityClearBehavior(); }
  reset(): void {}
}

class UnityNoiseBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksNoise';
  private readonly strengthXFactory: UnityCurveFactory;
  private readonly strengthYFactory: UnityCurveFactory;
  private readonly strengthZFactory: UnityCurveFactory;
  private readonly positionAmountFactory: UnityCurveFactory;
  private readonly remapXFactory: UnityCurveFactory;
  private readonly remapYFactory: UnityCurveFactory;
  private readonly remapZFactory: UnityCurveFactory;
  private readonly scrollSpeed: UnityCurveSample;
  private readonly fieldOffset: [number, number, number];
  private states = new WeakMap<Particle, UnityNoiseParticleState>();
  private scrollOffset = 0;
  private emitterTime = 0;

  constructor(private readonly metadata: UnityNoiseMetadata, private readonly duration: number) {
    this.strengthXFactory = compileUnityCurve(metadata.strengthX, 'noise.strengthX');
    this.strengthYFactory = compileUnityCurve(metadata.strengthY, 'noise.strengthY');
    this.strengthZFactory = compileUnityCurve(metadata.strengthZ, 'noise.strengthZ');
    this.positionAmountFactory = compileUnityCurve(metadata.positionAmount, 'noise.positionAmount');
    this.remapXFactory = compileUnityCurve(metadata.remapX, 'noise.remapX');
    this.remapYFactory = compileUnityCurve(metadata.remapY, 'noise.remapY');
    this.remapZFactory = compileUnityCurve(metadata.remapZ, 'noise.remapZ');
    this.scrollSpeed = compileUnityCurve(metadata.scrollSpeed, 'noise.scrollSpeed')();
    const offset = unityRandom3(metadata.randomSeed);
    this.fieldOffset = [offset[0] * 100, offset[1] * 100, offset[2] * 100];
  }

  initialize(particle: Particle): void {
    const strengthX = this.strengthXFactory();
    const state: UnityNoiseParticleState = {
      strengthX,
      strengthY: this.metadata.separateAxes ? this.strengthYFactory() : strengthX,
      strengthZ: this.metadata.separateAxes ? this.strengthZFactory() : strengthX,
      positionAmount: this.positionAmountFactory(),
      remapX: this.remapXFactory(),
      remapY: this.remapYFactory(),
      remapZ: this.remapZFactory()
    };
    replaceUnityNoiseAnimatedVelocity(particle, this.evaluate(particle, state));
    this.states.set(particle, state);
  }

  update(particle: Particle): void {
    const state = this.states.get(particle);
    if (!state) return;
    replaceUnityNoiseAnimatedVelocity(particle, this.evaluate(particle, state));
  }

  frameUpdate(delta: number): void {
    const normalizedTime = this.duration <= 0 ? 0 : (this.emitterTime % this.duration) / this.duration;
    this.scrollOffset += this.scrollSpeed.evaluate(normalizedTime) * delta;
    this.emitterTime += delta;
  }

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityNoiseBehavior(this.metadata, this.duration);
  }

  reset(): void {
    // Quarks also calls reset at a loop boundary while live particles retain the field phase.
  }

  restart(): void {
    this.scrollOffset = 0;
    this.emitterTime = 0;
  }

  private evaluate(particle: Particle, state: UnityNoiseParticleState): [number, number, number] {
    const sourcePosition = transformBasis(
      [particle.position.x, particle.position.y, particle.position.z],
      this.metadata.particleToNoiseBasisX,
      this.metadata.particleToNoiseBasisY,
      this.metadata.particleToNoiseBasisZ
    );
    sourcePosition[0] += this.fieldOffset[0];
    sourcePosition[1] += this.fieldOffset[1];
    sourcePosition[2] += this.fieldOffset[2];
    const curl = sampleUnityCurlNoise(sourcePosition, {
      quality: this.metadata.quality,
      frequency: this.metadata.frequency,
      octaveCount: this.metadata.octaveCount,
      octaveMultiplier: this.metadata.octaveMultiplier,
      octaveScale: this.metadata.octaveScale,
      scrollOffset: this.scrollOffset
    });
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    const damping = this.metadata.damping ? 1 / this.metadata.frequency : 1;
    const positionAmount = state.positionAmount.evaluate(t);
    if (this.metadata.remapEnabled) {
      curl[0] = state.remapX.evaluate(Math.min(1, Math.max(0, curl[0] * 0.5 + 0.5)));
      curl[1] = state.remapY.evaluate(Math.min(1, Math.max(0, curl[1] * 0.5 + 0.5)));
      curl[2] = state.remapZ.evaluate(Math.min(1, Math.max(0, curl[2] * 0.5 + 0.5)));
    }
    curl[0] *= state.strengthX.evaluate(t) * damping * positionAmount;
    curl[1] *= state.strengthY.evaluate(t) * damping * positionAmount;
    curl[2] *= state.strengthZ.evaluate(t) * damping * positionAmount;
    return transformBasis(
      curl,
      this.metadata.noiseToParticleBasisX,
      this.metadata.noiseToParticleBasisY,
      this.metadata.noiseToParticleBasisZ
    );
  }
}

function transformBasis(
  value: readonly [number, number, number],
  basisX: readonly [number, number, number],
  basisY: readonly [number, number, number],
  basisZ: readonly [number, number, number]
): [number, number, number] {
  return [
    value[0] * basisX[0] + value[1] * basisY[0] + value[2] * basisZ[0],
    value[0] * basisX[1] + value[1] * basisY[1] + value[2] * basisZ[1],
    value[0] * basisX[2] + value[1] * basisY[2] + value[2] * basisZ[2]
  ];
}

interface UnityLimitVelocityMetadata {
  limit: Record<string, unknown> | undefined;
  separateAxes: boolean;
  limitX: Record<string, unknown> | undefined;
  limitY: Record<string, unknown> | undefined;
  limitZ: Record<string, unknown> | undefined;
  dampen: number;
  drag: Record<string, unknown> | undefined;
  multiplyDragByParticleSize: boolean;
  multiplyDragByParticleVelocity: boolean;
}

interface UnityLimitVelocityParticleState {
  limit: UnityCurveSample | undefined;
  limitX: UnityCurveSample | undefined;
  limitY: UnityCurveSample | undefined;
  limitZ: UnityCurveSample | undefined;
  drag: UnityCurveSample | undefined;
}

class UnityLimitVelocityOverLifetimeBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksLimitVelocityOverLifetime';
  private readonly limitFactory: UnityCurveFactory | undefined;
  private readonly limitXFactory: UnityCurveFactory | undefined;
  private readonly limitYFactory: UnityCurveFactory | undefined;
  private readonly limitZFactory: UnityCurveFactory | undefined;
  private readonly dragFactory: UnityCurveFactory | undefined;
  private states = new WeakMap<Particle, UnityLimitVelocityParticleState>();

  constructor(private readonly metadata: UnityLimitVelocityMetadata) {
    this.limitFactory = metadata.limit
      ? compileUnityCurve(metadata.limit, 'limitVelocityOverLifetime.limit')
      : undefined;
    this.limitXFactory = metadata.limitX
      ? compileUnityCurve(metadata.limitX, 'limitVelocityOverLifetime.limitX')
      : undefined;
    this.limitYFactory = metadata.limitY
      ? compileUnityCurve(metadata.limitY, 'limitVelocityOverLifetime.limitY')
      : undefined;
    this.limitZFactory = metadata.limitZ
      ? compileUnityCurve(metadata.limitZ, 'limitVelocityOverLifetime.limitZ')
      : undefined;
    this.dragFactory = metadata.drag
      ? compileUnityCurve(metadata.drag, 'limitVelocityOverLifetime.drag')
      : undefined;
  }

  initialize(particle: Particle): void {
    this.states.set(particle, {
      limit: this.limitFactory?.(),
      limitX: this.limitXFactory?.(),
      limitY: this.limitYFactory?.(),
      limitZ: this.limitZFactory?.(),
      drag: this.dragFactory?.()
    });
  }

  update(particle: Particle, delta: number): void {
    const state = this.states.get(particle);
    if (!state) return;
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    let speed = particle.velocity.length();
    const dampenFactor = 1 - Math.pow(
      1 - Math.min(1, Math.max(0, this.metadata.dampen)),
      Math.abs(delta) * 30
    );
    if (this.metadata.separateAxes && (state.limitX || state.limitY || state.limitZ)) {
      const samples: Array<UnityCurveSample | undefined> = [state.limitX, state.limitY, state.limitZ];
      const components: [number, number, number] = [particle.velocity.x, particle.velocity.y, particle.velocity.z];
      for (let index = 0; index < 3; index += 1) {
        const sample = samples[index];
        if (!sample) continue;
        const maximum = Math.max(0, sample.evaluate(t));
        const component = index === 0 ? components[0] : index === 1 ? components[1] : components[2];
        if (Math.abs(component) > maximum) {
          const target = Math.sign(component) * maximum;
          const next = component + (target - component) * dampenFactor;
          if (index === 0) components[0] = next;
          else if (index === 1) components[1] = next;
          else components[2] = next;
        }
      }
      particle.velocity.set(components[0], components[1], components[2]);
      speed = particle.velocity.length();
    } else if (state.limit) {
      const maximum = Math.max(0, state.limit.evaluate(t));
      if (speed > maximum && speed > 1e-12) {
        const nextSpeed = speed + (maximum - speed) * dampenFactor;
        particle.velocity.multiplyScalar(nextSpeed / speed);
        speed = particle.velocity.length();
      }
    }
    if (state.drag && speed > 1e-12 && delta > 0) {
      // Mirrors Unity Modules/ParticleSystem/Modules/ClampVelocityModule.cpp.
      let drag = Math.max(0, state.drag.evaluate(t));
      if (this.metadata.multiplyDragByParticleSize) {
        const maximumDimension = Math.max(
          Math.abs(particle.size.x),
          Math.abs(particle.size.y),
          Math.abs(particle.size.z)
        );
        const radius = maximumDimension * 0.5;
        drag *= Math.PI * radius * radius;
      }
      if (this.metadata.multiplyDragByParticleVelocity) drag *= speed * speed;
      const nextSpeed = Math.max(0, speed - drag * delta);
      particle.velocity.multiplyScalar(nextSpeed / speed);
    }
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityLimitVelocityOverLifetimeBehavior(this.metadata);
  }

  reset(): void {
    // Preserve samples for particles that outlive the emitter duration.
  }
}

interface UnityInheritVelocityMetadata {
  mode: 'initial' | 'current';
  curve: Record<string, unknown>;
}

class UnityInheritVelocityInitialContext {
  private baselines = new WeakMap<object, Vector3>();
  private readonly activeVelocity = new Vector3();
  private readonly currentPosition = new Vector3();
  private readonly inverseEmitterMatrix = new Matrix4();
  private hasActiveVelocity = false;

  runWithEmitterVelocity(
    delta: number,
    emissionState: unknown,
    emitterMatrix: QuarksMatrixLike,
    worldSpace: boolean,
    emit: () => void
  ): void {
    this.activeVelocity.set(0, 0, 0);
    this.hasActiveVelocity = true;

    if (typeof emissionState === 'object' && emissionState !== null) {
      this.currentPosition.set(
        emitterMatrix.elements[12] ?? 0,
        emitterMatrix.elements[13] ?? 0,
        emitterMatrix.elements[14] ?? 0
      );
      const baseline = this.baselines.get(emissionState);
      if (baseline && Number.isFinite(delta) && delta > 0) {
        this.activeVelocity.copy(this.currentPosition).sub(baseline).multiplyScalar(1 / delta);
        if (!worldSpace) {
          this.inverseEmitterMatrix.fromArray(emitterMatrix.elements).invert();
          applyLinearMatrix(this.activeVelocity, this.inverseEmitterMatrix);
        }
      }
      if (baseline) baseline.copy(this.currentPosition);
      else this.baselines.set(emissionState, this.currentPosition.clone());
    }

    try {
      emit();
    } finally {
      this.activeVelocity.set(0, 0, 0);
      this.hasActiveVelocity = false;
    }
  }

  applyInitialVelocity(particle: Particle, multiplier: number): void {
    if (!this.hasActiveVelocity || multiplier === 0) return;
    particle.velocity.x += this.activeVelocity.x * multiplier;
    particle.velocity.y += this.activeVelocity.y * multiplier;
    particle.velocity.z += this.activeVelocity.z * multiplier;
  }

  clearMotionBaseline(): void {
    this.baselines = new WeakMap<object, Vector3>();
    this.activeVelocity.set(0, 0, 0);
    this.hasActiveVelocity = false;
  }
}

class UnityInheritVelocityInitialBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksInheritVelocityInitial';
  private readonly curveFactory: UnityCurveFactory;

  constructor(
    private readonly metadata: UnityInheritVelocityMetadata,
    private readonly context: UnityInheritVelocityInitialContext
  ) {
    this.curveFactory = compileUnityCurve(metadata.curve, 'inheritVelocity.curve');
  }

  initialize(particle: Particle): void {
    this.context.applyInitialVelocity(particle, this.curveFactory().evaluate(0));
  }

  update(): void {}
  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityInheritVelocityInitialBehavior(this.metadata, this.context);
  }

  reset(): void {
    // Emitter loop resets must not erase the frame-to-frame motion baseline.
  }
}

type UnityShapeDistribution =
  | { type: 'sphereVolume' | 'hemisphereVolume'; radius: number; thickness: number }
  | { type: 'boxVolume'; size: [number, number, number] }
  | { type: 'singleSidedEdge'; radius: number; mode: number; spread: number };

type UnityShapeRandomDirection =
  | { mode: 'lerpRandomUnit'; amount: number }
  | { mode: 'coneSurface'; amount: number; angle: number; radius: number };

interface UnityShapeRandomPosition {
  amount: number;
  sphericalAmount: number;
  mode: 'box' | 'radial';
}

interface UnityShapeMetadata {
  distribution?: UnityShapeDistribution;
  directionMode?: 'localY' | 'localZ';
  randomDirection?: UnityShapeRandomDirection;
  randomPosition?: UnityShapeRandomPosition;
  alignToDirection?: true;
  birthPositionTransform?: number[];
  birthDirectionTransform?: number[];
  /** 0.1.14 compatibility: the same matrix was used for both channels. */
  birthTransform?: number[];
  correctWorldSpaceBirthVelocity?: true;
  meshNormalOffset?: number;
}

interface UnityStartDelayMetadata {
  randomSeed: number;
  delay: Record<string, unknown>;
}

interface UnityLifetimeByEmitterSpeedMetadata {
  randomSeed: number;
  range: [number, number];
  curve: Record<string, unknown>;
}

interface UnityMeshRotationBySpeedMetadata {
  axisMode: 'fixed' | 'position' | 'velocity' | 'uniformXY';
  axis?: [number, number, number];
  basisX: [number, number, number];
  basisY: [number, number, number];
  basisZ: [number, number, number];
  speedRange: [number, number];
  angularVelocity: Record<string, unknown>;
}

interface UnityTrailSemanticsMetadata {
  worldSpace: boolean;
  dieWithParticles: boolean;
  sizeAffectsWidth: boolean;
  minVertexDistance: number;
  colorOverTrail?: Record<string, unknown>;
}

type UnityProjectColorSpace = 'gamma' | 'linear';

interface UnityColorSemanticsMetadata {
  projectColorSpace: UnityProjectColorSpace;
  materialColor: [number, number, number, number];
}

type UnityFragmentColorMode =
  | 'stock'
  | 'legacySoftAdditive'
  | 'legacyAlphaPremultiply'
  | 'legacyMultiply'
  | 'legacyMultiplyDouble'
  | 'hovlAdditivePremultiply'
  | 'invisibleFallback';

type UnityMaterialBaseColorChannel = 'rgb' | 'r' | 'g' | 'b' | 'a';

interface UnityMaterialAlphaMetadata {
  baseChannel: 'r' | 'g' | 'b' | 'a';
  factorChannel: 'r' | 'g' | 'b' | 'a';
  particleColorAlpha?: boolean;
  baseWeights?: [number, number, number, number];
  colorScale?: [number, number, number, number];
  factorWeights?: [number, number, number, number];
  clipEnabled: boolean;
  clipThreshold: number;
}

interface UnityUberFxsgShaderParameters {
  schemaVersion: 'unity_particle_quarks_exporter.material.shader_parameters.v1' |
    'unity_particle_quarks_exporter.material.shader_parameters.v2';
  profile: 'custom.piloto.uberfxsg';
  useColorRamp: boolean;
  useFresnel: boolean;
  useAlphaOverride: boolean;
  useSoftAlpha: boolean;
  emissionMode: 'baseColorAdditive' | 'none';
  emissionScale: number;
  colorOperation: 'channelPickerSaturation' | 'legacyScalar';
  alphaOperation: 'channelPickerAdd' | 'legacyChannel';
  mainTextureChannel: [number, number, number, number];
  mainAlphaChannel: [number, number, number, number];
  alphaOverrideChannel: [number, number, number, number];
  lastColor: [number, number, number, number];
  midColor: [number, number, number, number];
  whiteColor: [number, number, number, number];
  fresnelColor: [number, number, number, number];
  fresnelScale: number;
  fresnelPower: number;
  desaturate: number;
  middlePointPos: number;
  middlePointPos1: number;
  fresnelBlend: number;
}

interface UnityRockDissolveShaderParameters {
  schemaVersion: 'unity_particle_quarks_exporter.material.shader_parameters.v1';
  profile: 'custom.shadergraph.rockDissolve';
  colorOperation: 'rockDissolveVertexCustomDataLerp';
  alphaOperation: 'rockDissolveClip';
}

type UnityMaterialShaderParameters = UnityUberFxsgShaderParameters | UnityRockDissolveShaderParameters;

interface UnityCustomDataMetadata {
  custom1: [Record<string, unknown>, Record<string, unknown>, Record<string, unknown>, Record<string, unknown>];
  custom2: Record<string, unknown>;
}

interface UnityRendererPivotMetadata {
  value: [number, number, number];
  geometryOffset: [number, number, number];
  sourceRenderMode?: string;
}

interface UnityMaterialBlendMetadata {
  mode: string;
  src: number;
  dst: number;
  equation: number;
  srcAlpha: number;
  dstAlpha: number;
  equationAlpha: number;
  customAlpha: boolean;
  premultiplied: boolean;
  zWrite: boolean;
}

interface UnityMaterialTextureUvEntry {
  property: string;
  scale: [number, number];
  offset: [number, number];
  panning: [number, number];
}

interface UnityMaterialTextureUvMetadata {
  main?: UnityMaterialTextureUvEntry;
  alpha?: UnityMaterialTextureUvEntry;
}

const supportedUnityMaterialProfiles = new Set([
  'builtin.sprite',
  'builtin.unlitNoVertexColor',
  'builtin.standardMetallic',
  'builtin.particleAlphaBlended',
  'builtin.particleAdditive',
  'builtin.particleMultiply',
  'builtin.particleAnimAlphaBlended',
  'builtin.particleAdditiveSoft',
  'builtin.particleAlphaBlendedPremultiply',
  'builtin.particleMultiplyDouble',
  'builtin.mobileParticleAlphaBlended',
  'builtin.mobileParticleAdditive',
  'builtin.mobileParticleMultiply',
  'builtin.mobileParticleVertexLit',
  'builtin.particlesStandardUnlit',
  'builtin.particlesStandardSurface',
  'urp.particleUnlit',
  'urp.particleSimpleLit',
  'urp.particleLit',
  'urp.unlit',
  'urp.simpleLit',
  'urp.lit',
  'custom.hovl.particles',
  'custom.piloto.uberfxsg',
  'custom.vehicle.effect',
  'custom.shadergraph.rockDissolve',
  'custom.shadergraph.particle'
]);

interface UnityMaterialMetadata {
  fragmentColorMode: UnityFragmentColorMode;
  baseColorChannel?: UnityMaterialBaseColorChannel;
  cameraFade: UnityCameraFadeMetadata | undefined;
  profileId?: string;
  profileMetadataKey?: string;
  alpha?: UnityMaterialAlphaMetadata;
  blend?: UnityMaterialBlendMetadata;
  textureUv?: UnityMaterialTextureUvMetadata;
  shaderParameters?: UnityMaterialShaderParameters;
}

interface UnityCameraFadeMetadata {
  near: number;
  far: number;
  smoothness: number;
}

const unityMaterialMetadata = new WeakMap<ParticleSystem, UnityMaterialMetadata>();
const unityColorSemanticsMetadata = new WeakMap<ParticleSystem, UnityColorSemanticsMetadata>();
const unityCustomDataMetadata = new WeakMap<ParticleSystem, UnityCustomDataMetadata>();
const unityRendererPivotMetadata = new WeakMap<ParticleSystem, UnityRendererPivotMetadata>();

type UnityStartColorMetadata =
  | { mode: 'gradient' | 'twoGradients' }
  | { mode: 'randomColor'; gradient: Record<string, unknown> };

interface UnityTrailInheritParticleColorMetadata {
  particleColorOverLifetime: Record<string, unknown>;
}

class UnityNormalizedStartColor implements FunctionColorGenerator {
  readonly type = 'function' as const;

  constructor(
    private readonly source: FunctionColorGenerator,
    private readonly duration: number
  ) {}

  startGen(memory: GeneratorMemory): void {
    this.source.startGen(memory);
  }

  genColor(memory: GeneratorMemory, color: Parameters<FunctionColorGenerator['genColor']>[1], time: number) {
    return this.source.genColor(memory, color, this.duration <= 0 ? 0 : time / this.duration);
  }

  toJSON(): FunctionJSON {
    return this.source.toJSON();
  }

  clone(): FunctionColorGenerator {
    return new UnityNormalizedStartColor(this.source.clone(), this.duration);
  }
}

class UnityRandomGradientStartColor implements ColorGenerator {
  readonly type = 'value' as const;
  private memoryIndex = 0;

  constructor(private readonly gradient: QuarksGradient) {}

  startGen(memory: GeneratorMemory): void {
    this.memoryIndex = memory.length;
    memory.push(Math.random());
  }

  genColor(memory: GeneratorMemory, color: Parameters<ColorGenerator['genColor']>[1]) {
    return this.gradient.genColor(memory, color, memory[this.memoryIndex] ?? Math.random());
  }

  toJSON(): FunctionJSON {
    return this.gradient.toJSON();
  }

  clone(): ColorGenerator {
    return new UnityRandomGradientStartColor(this.gradient.clone() as QuarksGradient);
  }
}

class UnityInheritVelocityCurrentBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksInheritVelocityCurrent';
  private readonly curveFactory: UnityCurveFactory;
  private readonly previousPosition = new Vector3();
  private readonly currentPosition = new Vector3();
  private readonly deltaVelocity = new Vector3();
  private readonly inverseEmitterMatrix = new Matrix4();
  private hasPreviousPosition = false;
  private states = new WeakMap<Particle, {
    curve: UnityCurveSample;
    previousX: number;
    previousY: number;
    previousZ: number;
  }>();

  constructor(
    private readonly metadata: UnityInheritVelocityMetadata,
    private readonly emitter: Object3D,
    private readonly worldSpace: boolean
  ) {
    this.curveFactory = compileUnityCurve(metadata.curve, 'inheritVelocity.curve');
  }

  initialize(particle: Particle): void {
    this.states.set(particle, {
      curve: this.curveFactory(),
      previousX: 0,
      previousY: 0,
      previousZ: 0
    });
  }

  update(particle: Particle): void {
    const state = this.states.get(particle);
    if (!state) return;
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    const scale = state.curve.evaluate(t);
    const currentX = this.deltaVelocity.x * scale;
    const currentY = this.deltaVelocity.y * scale;
    const currentZ = this.deltaVelocity.z * scale;
    particle.velocity.x += currentX - state.previousX;
    particle.velocity.y += currentY - state.previousY;
    particle.velocity.z += currentZ - state.previousZ;
    state.previousX = currentX;
    state.previousY = currentY;
    state.previousZ = currentZ;
  }

  frameUpdate(delta: number): void {
    this.emitter.updateWorldMatrix(true, false);
    this.currentPosition.setFromMatrixPosition(this.emitter.matrixWorld);
    if (!this.hasPreviousPosition || delta <= 0) {
      this.previousPosition.copy(this.currentPosition);
      this.deltaVelocity.set(0, 0, 0);
      this.hasPreviousPosition = true;
      return;
    }
    this.deltaVelocity.copy(this.currentPosition).sub(this.previousPosition).multiplyScalar(1 / delta);
    if (!this.worldSpace) {
      this.inverseEmitterMatrix.copy(this.emitter.matrixWorld).invert();
      applyLinearMatrix(this.deltaVelocity, this.inverseEmitterMatrix);
    }
    this.previousPosition.copy(this.currentPosition);
  }

  toJSON(): Record<string, unknown> { return { type: this.type }; }
  clone(): Behavior { return new UnityInheritVelocityCurrentBehavior(this.metadata, this.emitter, this.worldSpace); }
  reset(): void {
    // Quarks calls reset at loop boundaries; live particles must retain their
    // inherited component and emitter motion history across that boundary.
  }

  restart(): void {
    this.hasPreviousPosition = false;
    this.deltaVelocity.set(0, 0, 0);
  }
}

class UnityMeshRotationBySpeedBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksMeshRotationBySpeed';
  private readonly angularVelocityFactory: UnityCurveFactory;
  private readonly states = new WeakMap<Particle, { angularVelocity: UnityCurveSample; axis: QuarksVector3 }>();
  private readonly deltaRotation = new QuarksQuaternion();
  private readonly sourceAxis = new Vector3();
  private readonly basis = new Matrix4();

  constructor(private readonly metadata: UnityMeshRotationBySpeedMetadata) {
    this.angularVelocityFactory = compileUnityCurve(metadata.angularVelocity, 'rotationBySpeed.angularVelocity');
    const x = metadata.basisX;
    const y = metadata.basisY;
    const z = metadata.basisZ;
    this.basis.set(
      x[0], y[0], z[0], 0,
      x[1], y[1], z[1], 0,
      x[2], y[2], z[2], 0,
      0, 0, 0, 1
    );
  }

  initialize(particle: Particle): void {
    this.states.set(particle, {
      angularVelocity: this.angularVelocityFactory(),
      axis: this.resolveAxis(particle)
    });
  }

  update(particle: Particle, delta: number): void {
    const state = this.states.get(particle);
    if (!state || !(particle.rotation instanceof QuarksQuaternion) || !Number.isFinite(delta)) return;
    const speed = Math.hypot(particle.velocity.x, particle.velocity.y, particle.velocity.z);
    const low = this.metadata.speedRange[0];
    const high = this.metadata.speedRange[1];
    const normalized = high <= low
      ? 0
      : Math.min(1, Math.max(0, (speed - low) / (high - low)));
    const radians = state.angularVelocity.evaluate(normalized) * delta;
    if (Math.abs(radians) <= 1e-12 || state.axis.lengthSq() <= 1e-24) return;
    this.deltaRotation.setFromAxisAngle(state.axis, radians);
    particle.rotation.multiply(this.deltaRotation);
  }

  frameUpdate(): void {}
  toJSON(): Record<string, unknown> { return { type: this.type }; }
  clone(): Behavior { return new UnityMeshRotationBySpeedBehavior(this.metadata); }
  reset(): void {}

  private resolveAxis(particle: Particle): QuarksVector3 {
    if (this.metadata.axisMode === 'fixed' && this.metadata.axis) {
      return new QuarksVector3(...this.metadata.axis);
    }
    if (this.metadata.axisMode === 'uniformXY') {
      const angle = Math.random() * Math.PI * 2;
      return new QuarksVector3(Math.cos(angle), Math.sin(angle), 0).normalize();
    }
    this.sourceAxis.set(
      this.metadata.axisMode === 'position' ? particle.position.x : particle.velocity.x,
      this.metadata.axisMode === 'position' ? particle.position.y : particle.velocity.y,
      this.metadata.axisMode === 'position' ? particle.position.z : particle.velocity.z
    );
    if (this.sourceAxis.lengthSq() <= 1e-24) this.sourceAxis.set(0, 0, 1);
    applyLinearMatrix(this.sourceAxis, this.basis);
    this.sourceAxis.normalize();
    return new QuarksVector3(this.sourceAxis.x, this.sourceAxis.y, this.sourceAxis.z);
  }
}

class UnityTrailSemanticsBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksTrailSemantics';
  private readonly sample = new QuarksVector4();
  private readonly color: ColorGenerator | FunctionColorGenerator | null;
  private readonly baseColors = new WeakMap<object, QuarksVector4>();
  private readonly baseWidths = new WeakMap<object, number>();

  constructor(private readonly metadata: UnityTrailSemanticsMetadata) {
    this.color = metadata.colorOverTrail
      ? ColorGeneratorFromJSON(metadata.colorOverTrail as FunctionJSON)
      : null;
  }

  initialize(particle: Particle): void {
    this.color?.startGen(particle.memory);
  }

  captureHistoryBaseWidths(particle: Particle): void {
    if (!this.metadata.sizeAffectsWidth) return;
    const trail = particle as Particle & {
      previous?: { values(): IterableIterator<{ size: number }> };
    };
    if (!trail.previous) return;
    for (const record of trail.previous.values()) {
      if (!this.baseWidths.has(record)) this.baseWidths.set(record, record.size);
    }
  }

  filterHistoryByDistance(particle: Particle): void {
    if (this.metadata.minVertexDistance <= 0) return;
    const trail = particle as Particle & {
      previous?: {
        length: number;
        clear(): void;
        push(record: { position: QuarksVector3; color: QuarksVector4; size: number }): void;
        values(): IterableIterator<{ position: QuarksVector3; color: QuarksVector4; size: number }>;
      };
    };
    if (!trail.previous || trail.previous.length <= 1) return;
    const minimumDistanceSquared = this.metadata.minVertexDistance * this.metadata.minVertexDistance;
    const accepted: Array<{ position: QuarksVector3; color: QuarksVector4; size: number }> = [];
    for (const record of trail.previous.values()) {
      const previous = accepted[accepted.length - 1];
      if (!previous || previous.position.distanceToSquared(record.position) >= minimumDistanceSquared) {
        accepted.push(record);
      }
    }
    if (accepted.length === trail.previous.length) return;
    trail.previous.clear();
    for (const record of accepted) trail.previous.push(record);
  }

  update(particle: Particle): void {
    const trail = particle as Particle & {
      previous?: {
        length: number;
        clear(): void;
        values(): IterableIterator<{ color: QuarksVector4; size: number }>;
      };
    };
    if (!trail.previous) return;
    if (this.metadata.dieWithParticles && particle.age >= particle.life) {
      trail.previous.clear();
      return;
    }
    if (trail.previous.length <= 0) return;
    const records = Array.from(trail.previous.values());
    if (this.metadata.sizeAffectsWidth) {
      for (const record of records) {
        const baseWidth = this.baseWidths.get(record);
        if (baseWidth !== undefined) record.size *= baseWidth;
      }
    }
    if (!this.color) return;
    const denominator = Math.max(1, records.length - 1);
    records.forEach((record, index) => {
      let baseColor = this.baseColors.get(record);
      if (!baseColor) {
        baseColor = record.color.clone();
        this.baseColors.set(record, baseColor);
      }
      // Quarks stores history from the oldest point (tail) to the newest
      // point (particle head). Unity ColorOverTrail is authored from head to
      // tail, so reverse the normalized history coordinate before sampling.
      const unityTrailT = 1 - index / denominator;
      if (this.color!.type === 'function') {
        this.color!.genColor(particle.memory, this.sample, unityTrailT);
      } else {
        this.color!.genColor(particle.memory, this.sample);
      }
      record.color.set(
        baseColor.x * this.sample.x,
        baseColor.y * this.sample.y,
        baseColor.z * this.sample.z,
        baseColor.w * this.sample.w
      );
    });
  }

  frameUpdate(): void {}
  toJSON(): Record<string, unknown> { return { type: this.type }; }
  clone(): Behavior { return new UnityTrailSemanticsBehavior(this.metadata); }
  reset(): void {}
}

class UnityTrailInheritParticleColorBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksTrailInheritParticleColor';
  private readonly trailSample = new QuarksVector4();
  private readonly particleSample = new QuarksVector4();

  constructor(
    private readonly trailColor: ColorGenerator | FunctionColorGenerator,
    private readonly particleColor: FunctionColorGenerator
  ) {}

  initialize(particle: Particle): void {
    this.trailColor.startGen(particle.memory);
    this.particleColor.startGen(particle.memory);
  }

  update(particle: Particle): void {
    const time = particle.life <= 0 ? 1 : particle.age / particle.life;
    if (this.trailColor.type === 'function') {
      this.trailColor.genColor(particle.memory, this.trailSample, time);
    } else {
      this.trailColor.genColor(particle.memory, this.trailSample);
    }
    this.particleColor.genColor(particle.memory, this.particleSample, time);
    particle.color.set(
      this.trailSample.x * this.particleSample.x * particle.startColor.x,
      this.trailSample.y * this.particleSample.y * particle.startColor.y,
      this.trailSample.z * this.particleSample.z * particle.startColor.z,
      this.trailSample.w * this.particleSample.w * particle.startColor.w
    );
  }

  frameUpdate(): void {}

  toJSON() {
    return {
      type: this.type,
      trailColor: this.trailColor.toJSON(),
      particleColor: this.particleColor.toJSON()
    };
  }

  clone(): Behavior {
    return new UnityTrailInheritParticleColorBehavior(
      this.trailColor.clone(),
      this.particleColor.clone()
    );
  }

  reset(): void {}
}

type UnityMeshScalarRotationMetadata = {
  basisX: [number, number, number];
  basisY: [number, number, number];
  basisZ: [number, number, number];
  shapeOrigin: [number, number, number];
  shapeBasisX: [number, number, number];
  shapeBasisY: [number, number, number];
  shapeBasisZ: [number, number, number];
} & (
  | { axisMode: 'fixed'; axis: [number, number, number] }
  | { axisMode: 'position' | 'velocity' | 'uniformXY' }
);

interface UnitySimulationSpeedMetadata {
  value: number;
}

interface UnityMeshVelocityAlignmentMetadata {
  forwardAxis: [number, number, number];
}

interface UnityRendererAlignmentMetadata {
  mode: 'local' | 'world' | 'view' | 'facing' | 'velocity';
  preserveAuthored: true;
  simulationSpace: 'local' | 'world';
}

/**
 * Quarks SpriteBatch always faces the camera for RenderMode.BillBoard. Unity
 * Local Billboard is a camera-independent quad, so use the same exported
 * geometry through the Mesh batch while retaining the authored scalar rotation.
 */
function installUnityRendererAlignment(
  system: ParticleSystem,
  metadata: UnityRendererAlignmentMetadata
): void {
  if (metadata.mode !== 'local' || system.renderMode !== RenderMode.BillBoard) return;
  const authoredStartRotation = system.startRotation;
  system.renderMode = RenderMode.Mesh;
  system.startRotation = authoredStartRotation;
  system.neededToUpdateRender = true;
}

interface UnityMeshCameraAlignmentMetadata {
  mode: 'view' | 'facing';
  forwardAxis: [number, number, number];
  upAxis: [number, number, number];
  preserveAuthoredRotation: true;
  simulationSpace: 'local';
}

interface UnityMeshVelocityAlignmentParticleState {
  alignment: QuarksQuaternion;
  aligned: boolean;
}

class UnityMeshVelocityAlignmentContext {
  private readonly forward = new Vector3();
  private readonly direction = new Vector3();
  private readonly threeAlignment = new Quaternion();
  private readonly inverseAlignment = new QuarksQuaternion();
  private readonly authoredRotation = new QuarksQuaternion();
  private readonly startRotations = new WeakMap<Particle, QuarksQuaternion>();
  private readonly states = new WeakMap<Particle, UnityMeshVelocityAlignmentParticleState>();

  constructor(metadata: UnityMeshVelocityAlignmentMetadata) {
    this.forward.set(...metadata.forwardAxis).normalize();
  }

  captureStartRotation(particle: Particle): void {
    if (!(particle.rotation instanceof QuarksQuaternion)) {
      throw new Error('Exporter Mesh Velocity alignment requires quaternion Mesh particles.');
    }
    this.startRotations.set(particle, particle.rotation.clone());
  }

  initialize(particle: Particle): void {
    if (!(particle.rotation instanceof QuarksQuaternion)) {
      throw new Error('Exporter Mesh Velocity alignment requires quaternion Mesh particles.');
    }
    const startRotation = this.startRotations.get(particle);
    if (!startRotation) {
      throw new Error('Exporter Mesh Velocity alignment did not capture authored start rotation.');
    }
    particle.rotation.copy(startRotation);
    this.states.set(particle, {
      alignment: new QuarksQuaternion(),
      aligned: false
    });
  }

  prepare(particle: Particle): void {
    const state = this.states.get(particle);
    if (!state?.aligned || !(particle.rotation instanceof QuarksQuaternion)) return;
    this.inverseAlignment.copy(state.alignment).invert();
    this.authoredRotation.multiplyQuaternions(this.inverseAlignment, particle.rotation);
    particle.rotation.copy(this.authoredRotation);
  }

  finalize(particle: Particle, delta = 0): void {
    const state = this.states.get(particle);
    if (!state || !(particle.rotation instanceof QuarksQuaternion)) return;
    this.direction.set(particle.velocity.x, particle.velocity.y, particle.velocity.z);
    if (this.direction.lengthSq() <= 1e-24) {
      state.alignment.set(0, 0, 0, 1);
      state.aligned = false;
      return;
    }
    this.threeAlignment.setFromUnitVectors(this.forward, this.direction.normalize());
    state.alignment.set(
      this.threeAlignment.x,
      this.threeAlignment.y,
      this.threeAlignment.z,
      this.threeAlignment.w
    );
    this.authoredRotation.copy(particle.rotation);
    particle.rotation.copy(state.alignment).multiply(this.authoredRotation);
    state.aligned = true;
  }

}

class UnityMeshVelocityAlignmentPreparationBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksMeshVelocityAlignmentPreparation';

  constructor(private readonly context: UnityMeshVelocityAlignmentContext) {}

  initialize(particle: Particle): void {
    this.context.initialize(particle);
  }

  update(particle: Particle): void {
    this.context.prepare(particle);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityMeshVelocityAlignmentPreparationBehavior(this.context);
  }

  reset(): void {}
}

class UnityMeshVelocityAlignmentFinalizationBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksMeshVelocityAlignmentFinalization';

  constructor(private readonly context: UnityMeshVelocityAlignmentContext) {}

  initialize(particle: Particle): void {
    this.context.finalize(particle);
  }

  update(particle: Particle): void {
    this.context.finalize(particle);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityMeshVelocityAlignmentFinalizationBehavior(this.context);
  }

  reset(): void {}
}

interface UnityMeshCameraAlignmentParticleState {
  alignment: QuarksQuaternion;
  aligned: boolean;
}

class UnityMeshCameraAlignmentContext {
  private readonly forward = new Vector3();
  private readonly upAxis = new Vector3();
  private readonly directionWorld = new Vector3();
  private readonly directionLocal = new Vector3();
  private readonly upReferenceLocal = new Vector3();
  private readonly resolvedUpLocal = new Vector3();
  private readonly fallbackUpLocal = new Vector3();
  private readonly right = new Vector3();
  private readonly localRight = new Vector3();
  private readonly worldUp = new Vector3();
  private readonly cameraPosition = new Vector3();
  private readonly particleWorldPosition = new Vector3();
  private readonly viewDirectionLocal = new Vector3();
  private readonly worldUpLocal = new Vector3();
  private readonly authoredForwardLocal = new Vector3();
  private readonly localBasis = new Matrix4();
  private readonly desiredBasis = new Matrix4();
  private readonly inverseEmitter = new Matrix4();
  private readonly alignmentThree = new Quaternion();
  private readonly inverseAlignment = new QuarksQuaternion();
  private readonly authoredRotation = new QuarksQuaternion();
  private readonly startRotations = new WeakMap<Particle, QuarksQuaternion>();
  private readonly states = new WeakMap<Particle, UnityMeshCameraAlignmentParticleState>();
  private frameReady = false;

  constructor(
    private readonly metadata: UnityMeshCameraAlignmentMetadata,
    private readonly emitter: Object3D,
    private readonly camera: Camera
  ) {
    this.forward.set(...metadata.forwardAxis).normalize();
    this.upAxis.set(...metadata.upAxis).normalize();
  }

  captureStartRotation(particle: Particle): void {
    if (!(particle.rotation instanceof QuarksQuaternion)) {
      throw new Error('Exporter Mesh camera alignment requires quaternion Mesh particles.');
    }
    this.startRotations.set(particle, particle.rotation.clone());
  }

  initialize(particle: Particle): void {
    if (!(particle.rotation instanceof QuarksQuaternion)) {
      throw new Error('Exporter Mesh camera alignment requires quaternion Mesh particles.');
    }
    const startRotation = this.startRotations.get(particle);
    if (!startRotation) {
      throw new Error('Exporter Mesh camera alignment did not capture authored start rotation.');
    }
    particle.rotation.copy(startRotation);
    this.states.set(particle, { alignment: new QuarksQuaternion(), aligned: false });
  }

  prepare(particle: Particle): void {
    const state = this.states.get(particle);
    if (!state?.aligned || !(particle.rotation instanceof QuarksQuaternion)) return;
    this.inverseAlignment.copy(state.alignment).invert();
    this.authoredRotation.multiplyQuaternions(this.inverseAlignment, particle.rotation);
    particle.rotation.copy(this.authoredRotation);
  }

  refreshFrame(): void {
    this.camera.updateMatrixWorld(true);
    this.emitter.updateMatrixWorld(true);
    this.inverseEmitter.copy(this.emitter.matrixWorld).invert();
    this.camera.getWorldPosition(this.cameraPosition);
    this.worldUp.set(0, 1, 0).transformDirection(this.camera.matrixWorld).normalize();
    this.worldUpLocal.copy(this.worldUp).transformDirection(this.inverseEmitter);
    this.viewDirectionLocal
      .set(0, 0, 1)
      .transformDirection(this.camera.matrixWorld)
      .transformDirection(this.inverseEmitter)
      .normalize();
    this.frameReady = true;
  }

  finalize(particle: Particle, delta = 0): void {
    const state = this.states.get(particle);
    if (!state || !(particle.rotation instanceof QuarksQuaternion)) return;
    if (!this.frameReady) this.refreshFrame();
    if (this.metadata.mode === 'view') {
      // Three cameras look down -Z, so the camera-facing side points along
      // camera local +Z (the camera's backward vector).
      this.directionLocal.copy(this.viewDirectionLocal);
      this.upReferenceLocal.copy(this.worldUpLocal);
    } else {
      // Quarks evaluates behaviors before its per-particle position integration.
      // Face the position that will be rendered at the end of this update, not
      // the stale pre-integration position.
      this.particleWorldPosition.copy(particle.position).addScaledVector(
        particle.velocity,
        delta * ((particle as Particle & { speedModifier?: number }).speedModifier ?? 1)
      );
      this.emitter.localToWorld(this.particleWorldPosition);
      this.directionWorld.copy(this.cameraPosition).sub(this.particleWorldPosition);
      if (this.directionWorld.lengthSq() <= 1e-24) {
        state.alignment.set(0, 0, 0, 1);
        state.aligned = false;
        return;
      }
      this.directionLocal.copy(this.directionWorld).transformDirection(this.inverseEmitter);
      this.upReferenceLocal.copy(this.worldUpLocal);
    }
    if (this.directionLocal.lengthSq() <= 1e-24) {
      state.alignment.set(0, 0, 0, 1);
      state.aligned = false;
      return;
    }
    this.directionLocal.normalize();
    this.authoredRotation.copy(particle.rotation);
    if (this.metadata.mode === 'facing') {
      // Facing is applied after Unity Mesh rotation semantics (including
      // scalar/velocity rotation). Map that completed authored forward axis
      // to the per-particle eye direction so those rotations cannot tilt the
      // final Mesh away from the camera. The shortest arc preserves roll.
      this.authoredForwardLocal.copy(this.forward)
        .applyQuaternion(this.authoredRotation)
        .normalize();
      this.alignmentThree.setFromUnitVectors(this.authoredForwardLocal, this.directionLocal);
    } else {
    this.resolvedUpLocal.copy(this.upReferenceLocal).addScaledVector(
      this.directionLocal,
      -this.upReferenceLocal.dot(this.directionLocal)
    );
    if (this.resolvedUpLocal.lengthSq() <= 1e-12) {
      this.fallbackUpLocal.set(0, 1, 0).transformDirection(this.inverseEmitter);
      this.resolvedUpLocal.copy(this.fallbackUpLocal).addScaledVector(
        this.directionLocal,
        -this.fallbackUpLocal.dot(this.directionLocal)
      );
    }
    if (this.resolvedUpLocal.lengthSq() <= 1e-12) this.resolvedUpLocal.set(0, 1, 0);
    this.resolvedUpLocal.normalize();
    this.right.crossVectors(this.resolvedUpLocal, this.directionLocal).normalize();
    this.resolvedUpLocal.crossVectors(this.directionLocal, this.right).normalize();
    this.localRight.crossVectors(this.upAxis, this.forward).normalize();
    this.localBasis.makeBasis(this.localRight, this.upAxis, this.forward).invert();
    this.desiredBasis.makeBasis(this.right, this.resolvedUpLocal, this.directionLocal).multiply(this.localBasis);
    this.alignmentThree.setFromRotationMatrix(this.desiredBasis);
    }
    state.alignment.set(
      this.alignmentThree.x,
      this.alignmentThree.y,
      this.alignmentThree.z,
      this.alignmentThree.w
    );
    particle.rotation.copy(state.alignment).multiply(this.authoredRotation);
    state.aligned = true;
  }

}

class UnityMeshCameraAlignmentPreparationBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksMeshCameraAlignmentPreparation';
  constructor(private readonly context: UnityMeshCameraAlignmentContext) {}
  initialize(particle: Particle): void { this.context.initialize(particle); }
  update(particle: Particle): void { this.context.prepare(particle); }
  frameUpdate(): void { this.context.refreshFrame(); }
  toJSON(): Record<string, unknown> { return { type: this.type }; }
  clone(): Behavior { return new UnityMeshCameraAlignmentPreparationBehavior(this.context); }
  reset(): void {}
}

class UnityMeshCameraAlignmentFinalizationBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksMeshCameraAlignmentFinalization';
  constructor(private readonly context: UnityMeshCameraAlignmentContext) {}
  initialize(particle: Particle): void { this.context.finalize(particle); }
  update(particle: Particle, delta: number): void { this.context.finalize(particle, delta); }
  frameUpdate(): void {}
  toJSON(): Record<string, unknown> { return { type: this.type }; }
  clone(): Behavior { return new UnityMeshCameraAlignmentFinalizationBehavior(this.context); }
  reset(): void {}
}

interface UnityMeshScalarRotationParticleState {
  axis: QuarksVector3;
  baseRotation: QuarksQuaternion;
  scalarAngle: number;
}

class UnityMeshScalarRotationContext {
  private readonly emissionMatrix = new Matrix4();
  private readonly inverseEmissionMatrix = new Matrix4();
  private readonly localBasis = new Matrix4();
  private readonly inverseLocalBasis = new Matrix4();
  private readonly shapeBasis = new Matrix4();
  private readonly inverseShapeBasis = new Matrix4();
  private readonly matrixPosition = new Vector3();
  private readonly matrixScale = new Vector3();
  private readonly matrixRotation = new Quaternion();
  private readonly localDirection = new Vector3();
  private readonly zRotation = new QuarksQuaternion();
  private readonly axisRotation = new QuarksQuaternion();
  private readonly relativeRotation = new QuarksQuaternion();
  private readonly inverseBaseRotation = new QuarksQuaternion();
  private readonly zAxis = new QuarksVector3(0, 0, 1);
  private startAngles = new WeakMap<Particle, number>();
  private states = new WeakMap<Particle, UnityMeshScalarRotationParticleState>();
  private hasEmissionMatrix = false;

  constructor(private readonly metadata: UnityMeshScalarRotationMetadata) {
    const x = metadata.basisX;
    const y = metadata.basisY;
    const z = metadata.basisZ;
    this.localBasis.set(
      x[0], y[0], z[0], 0,
      x[1], y[1], z[1], 0,
      x[2], y[2], z[2], 0,
      0, 0, 0, 1
    );
    this.inverseLocalBasis.copy(this.localBasis).invert();
    const shapeX = metadata.shapeBasisX;
    const shapeY = metadata.shapeBasisY;
    const shapeZ = metadata.shapeBasisZ;
    this.shapeBasis.set(
      shapeX[0], shapeY[0], shapeZ[0], 0,
      shapeX[1], shapeY[1], shapeZ[1], 0,
      shapeX[2], shapeY[2], shapeZ[2], 0,
      0, 0, 0, 1
    );
    this.inverseShapeBasis.copy(this.shapeBasis).invert();
  }

  captureStartRotation(particle: Particle): void {
    if (!(particle.rotation instanceof QuarksQuaternion)) {
      throw new Error('Exporter Mesh scalar-rotation metadata requires quaternion Mesh particles.');
    }
    const rotation = particle.rotation;
    if (Math.abs(rotation.x) > 1e-5 || Math.abs(rotation.y) > 1e-5) {
      throw new Error('Exporter Mesh scalar-rotation stock fallback is not a local-Z quaternion.');
    }
    this.startAngles.set(particle, 2 * Math.atan2(rotation.z, rotation.w));
  }

  initialize(particle: Particle, system: UnityParticleSystemLike): void {
    if (!(particle.rotation instanceof QuarksQuaternion)) {
      throw new Error('Exporter Mesh scalar-rotation metadata requires quaternion Mesh particles.');
    }
    const scalarAngle = this.startAngles.get(particle);
    if (scalarAngle === undefined) {
      throw new Error('Exporter Mesh scalar-rotation start angle was not captured before Shape initialization.');
    }

    const baseRotation = new QuarksQuaternion();
    if (system.worldSpace) {
      if (!this.hasEmissionMatrix) {
        throw new Error('Exporter world-space Mesh scalar rotation requires the active emission matrix.');
      }
      this.emissionMatrix.decompose(this.matrixPosition, this.matrixRotation, this.matrixScale);
      baseRotation.set(
        this.matrixRotation.x,
        this.matrixRotation.y,
        this.matrixRotation.z,
        this.matrixRotation.w
      );
    }

    const state: UnityMeshScalarRotationParticleState = {
      axis: this.resolveAxis(particle, system),
      baseRotation,
      scalarAngle
    };
    this.states.set(particle, state);
    this.writeStockZRotation(particle, state);
  }

  prepare(particle: Particle): void {
    const state = this.states.get(particle);
    if (state) this.writeStockZRotation(particle, state);
  }

  finalize(particle: Particle): void {
    const state = this.states.get(particle);
    if (!state || !(particle.rotation instanceof QuarksQuaternion)) return;
    this.inverseBaseRotation.copy(state.baseRotation).invert();
    this.relativeRotation.multiplyQuaternions(this.inverseBaseRotation, particle.rotation);
    state.scalarAngle = 2 * Math.atan2(this.relativeRotation.z, this.relativeRotation.w);
    this.axisRotation.setFromAxisAngle(state.axis, state.scalarAngle);
    particle.rotation.copy(state.baseRotation).multiply(this.axisRotation);
  }

  setEmissionMatrix(matrix: { elements: ArrayLike<number> }): void {
    this.emissionMatrix.fromArray(matrix.elements);
    this.inverseEmissionMatrix.copy(this.emissionMatrix).invert();
    this.hasEmissionMatrix = true;
  }

  clearEmissionMatrix(): void {
    this.hasEmissionMatrix = false;
  }

  reset(): void {
    // Quarks also calls reset at an emitter loop boundary while particles live.
    // Reused particle objects overwrite both maps during their next initialization.
  }

  private resolveAxis(particle: Particle, system: UnityParticleSystemLike): QuarksVector3 {
    if (this.metadata.axisMode === 'fixed') {
      return new QuarksVector3(...this.metadata.axis);
    }
    if (this.metadata.axisMode === 'uniformXY') {
      const angle = Math.random() * Math.PI * 2;
      this.localDirection.set(Math.cos(angle), Math.sin(angle), 0);
      applyLinearMatrix(this.localDirection, this.localBasis).normalize();
      return new QuarksVector3(this.localDirection.x, this.localDirection.y, this.localDirection.z);
    }

    const source = this.metadata.axisMode === 'position' ? particle.position : particle.velocity;
    this.localDirection.set(source.x, source.y, source.z);
    if (system.worldSpace) {
      if (this.metadata.axisMode === 'position') this.localDirection.applyMatrix4(this.inverseEmissionMatrix);
      else applyLinearMatrix(this.localDirection, this.inverseEmissionMatrix);
    }
    applyLinearMatrix(this.localDirection, this.inverseLocalBasis);
    if (this.metadata.axisMode === 'position') {
      this.localDirection.sub(new Vector3(...this.metadata.shapeOrigin));
    }
    applyLinearMatrix(this.localDirection, this.inverseShapeBasis);
    this.localDirection.set(-this.localDirection.y, this.localDirection.x, 0);
    if (this.localDirection.lengthSq() <= 1e-24) this.localDirection.set(0, 1, 0);
    applyLinearMatrix(this.localDirection, this.localBasis).normalize();
    return new QuarksVector3(this.localDirection.x, this.localDirection.y, this.localDirection.z);
  }

  private writeStockZRotation(particle: Particle, state: UnityMeshScalarRotationParticleState): void {
    if (!(particle.rotation instanceof QuarksQuaternion)) return;
    this.zRotation.setFromAxisAngle(this.zAxis, state.scalarAngle);
    particle.rotation.copy(state.baseRotation).multiply(this.zRotation);
  }
}

class UnityMeshScalarRotationPreparationBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksMeshScalarRotationPreparation';

  constructor(private readonly context: UnityMeshScalarRotationContext) {}

  initialize(particle: Particle, rawSystem: unknown): void {
    this.context.initialize(particle, rawSystem as UnityParticleSystemLike);
  }

  update(particle: Particle): void {
    this.context.prepare(particle);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityMeshScalarRotationPreparationBehavior(this.context);
  }

  reset(): void {
    this.context.reset();
  }
}

class UnityMeshScalarRotationFinalizationBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksMeshScalarRotationFinalization';

  constructor(private readonly context: UnityMeshScalarRotationContext) {}

  initialize(particle: Particle): void {
    this.context.finalize(particle);
  }

  update(particle: Particle): void {
    this.context.finalize(particle);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityMeshScalarRotationFinalizationBehavior(this.context);
  }

  reset(): void {}
}

interface UnityParticleSystemLike {
  worldSpace: boolean;
  emitter: Object3D;
}

function applyLinearMatrix(vector: Vector3, matrix: Matrix4): Vector3 {
  const { x, y, z } = vector;
  const elements = matrix.elements;
  vector.set(
    elements[0]! * x + elements[4]! * y + elements[8]! * z,
    elements[1]! * x + elements[5]! * y + elements[9]! * z,
    elements[2]! * x + elements[6]! * y + elements[10]! * z
  );
  return vector;
}

function randomUnitVector(target: Vector3): Vector3 {
  const angle = Math.random() * Math.PI * 2;
  const z = Math.random() * 2 - 1;
  const radius = Math.sqrt(Math.max(0, 1 - z * z));
  target.set(radius * Math.cos(angle), radius * Math.sin(angle), z);
  return target;
}

class UnityShapeSemanticsBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksShapeSemantics';
  private readonly worldMatrix = new Matrix4();
  private readonly birthPositionMatrix = new Matrix4();
  private readonly birthDirectionMatrix = new Matrix4();
  private readonly position = new Vector3();
  private readonly velocity = new Vector3();
  private readonly randomDirection = new Vector3();
  private readonly randomPosition = new Vector3();
  private readonly sampledDirection = new Vector3();
  private readonly directionRotation = new Quaternion();
  private localBirthVelocities = new WeakMap<Particle, [number, number, number]>();
  private hasEmissionMatrix = false;

  constructor(private readonly metadata: UnityShapeMetadata) {
    const legacyTransform = metadata.birthTransform;
    if (metadata.birthPositionTransform ?? legacyTransform) {
      this.birthPositionMatrix.fromArray(metadata.birthPositionTransform ?? legacyTransform!);
    }
    if (metadata.birthDirectionTransform ?? legacyTransform) {
      this.birthDirectionMatrix.fromArray(metadata.birthDirectionTransform ?? legacyTransform!);
    }
  }

  transformBirth(
    particle: Particle,
    sampledNormal?: readonly [number, number, number],
    emissionState?: unknown,
    shapeCurrentValue = 0
  ): void {
    const alignmentDirection = sampledNormal
      ? this.sampledDirection.set(...sampledNormal)
      : null;
    if (this.metadata.distribution) {
      this.resamplePosition(particle, emissionState, shapeCurrentValue);
    }
    if (this.metadata.directionMode === 'localY') {
      particle.velocity.x = 0;
      particle.velocity.y = particle.startSpeed;
      particle.velocity.z = 0;
    } else if (this.metadata.directionMode === 'localZ') {
      particle.velocity.x = 0;
      particle.velocity.y = 0;
      particle.velocity.z = particle.startSpeed;
    }
    if (this.metadata.randomDirection) this.applyRandomDirection(particle);
    if (this.metadata.randomPosition) this.applyRandomPosition(particle);
    if (this.metadata.meshNormalOffset !== undefined) {
      const normalX = sampledNormal?.[0] ?? particle.velocity.x;
      const normalY = sampledNormal?.[1] ?? particle.velocity.y;
      const normalZ = sampledNormal?.[2] ?? particle.velocity.z;
      const length = Math.hypot(normalX, normalY, normalZ);
      if (length > 1e-12) {
        const offset = this.metadata.meshNormalOffset;
        particle.position.x += normalX / length * offset;
        particle.position.y += normalY / length * offset;
        particle.position.z += normalZ / length * offset;
      }
    }
    if (this.metadata.birthPositionTransform ?? this.metadata.birthTransform) {
      this.position.set(particle.position.x, particle.position.y, particle.position.z)
        .applyMatrix4(this.birthPositionMatrix);
      particle.position.x = this.position.x;
      particle.position.y = this.position.y;
      particle.position.z = this.position.z;
    }
    if (this.metadata.birthDirectionTransform ?? this.metadata.birthTransform) {
      this.velocity.set(particle.velocity.x, particle.velocity.y, particle.velocity.z);
      const sourceSpeed = this.velocity.length();
      applyLinearMatrix(this.velocity, this.birthDirectionMatrix);
      const transformedSpeed = this.velocity.length();
      if (sourceSpeed > 1e-12 && transformedSpeed > 1e-12) {
        this.velocity.multiplyScalar(sourceSpeed / transformedSpeed);
      }
      particle.velocity.x = this.velocity.x;
      particle.velocity.y = this.velocity.y;
      particle.velocity.z = this.velocity.z;
      if (alignmentDirection) {
        applyLinearMatrix(alignmentDirection, this.birthDirectionMatrix).normalize();
      }
    }
    if (this.metadata.alignToDirection && particle.rotation instanceof QuarksQuaternion) {
      if (alignmentDirection) {
        this.velocity.copy(alignmentDirection);
      } else {
        this.velocity.set(particle.velocity.x, particle.velocity.y, particle.velocity.z);
      }
      if (this.velocity.lengthSq() > 1e-12) {
        this.directionRotation.setFromUnitVectors(FORWARD, this.velocity.normalize());
        const authored = new QuarksQuaternion(
          particle.rotation.x, particle.rotation.y, particle.rotation.z, particle.rotation.w
        );
        particle.rotation.set(
          this.directionRotation.x * authored.w + this.directionRotation.w * authored.x + this.directionRotation.y * authored.z - this.directionRotation.z * authored.y,
          this.directionRotation.y * authored.w + this.directionRotation.w * authored.y + this.directionRotation.z * authored.x - this.directionRotation.x * authored.z,
          this.directionRotation.z * authored.w + this.directionRotation.w * authored.z + this.directionRotation.x * authored.y - this.directionRotation.y * authored.x,
          this.directionRotation.w * authored.w - this.directionRotation.x * authored.x - this.directionRotation.y * authored.y - this.directionRotation.z * authored.z
        );
      }
    }
    this.localBirthVelocities.set(particle, [particle.velocity.x, particle.velocity.y, particle.velocity.z]);
  }

  initialize(particle: Particle, rawSystem: unknown): void {
    const system = rawSystem as UnityParticleSystemLike;
    if (!system.worldSpace || this.metadata.correctWorldSpaceBirthVelocity !== true) return;
    const localVelocity = this.localBirthVelocities.get(particle);
    if (!localVelocity || !this.hasEmissionMatrix) {
      throw new Error('Exporter world-space birth velocity requires captured local velocity and emission matrix.');
    }
    this.velocity.set(...localVelocity);
    applyLinearMatrix(this.velocity, this.worldMatrix);
    particle.velocity.x = this.velocity.x;
    particle.velocity.y = this.velocity.y;
    particle.velocity.z = this.velocity.z;
  }

  update(): void {}

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityShapeSemanticsBehavior(this.metadata);
  }

  reset(): void {
    // Quarks also resets behaviors inside emit() at a loop boundary. Existing
    // particles can still need their birth state; reuse overwrites the entry.
  }

  setEmissionMatrix(matrix: { elements: ArrayLike<number> }): void {
    this.worldMatrix.fromArray(matrix.elements);
    this.hasEmissionMatrix = true;
  }

  clearEmissionMatrix(): void {
    this.hasEmissionMatrix = false;
  }

  private resamplePosition(particle: Particle, emissionState?: unknown, shapeCurrentValue = 0): void {
    const distribution = this.metadata.distribution;
    if (!distribution) return;
    if (distribution.type === 'singleSidedEdge') {
      const state = isRecord(emissionState) ? emissionState : {};
      const mode = distribution.mode;
      let u = mode === 0 ? Math.random() : shapeCurrentValue;
      if (mode === 3 && state.isBursting === true &&
          typeof state.burstParticleIndex === 'number' &&
          typeof state.burstParticleCount === 'number') {
        u = state.burstParticleIndex / Math.max(1, state.burstParticleCount);
      }
      if (distribution.spread > 0) {
        u = Math.floor(u / distribution.spread) * distribution.spread;
      }
      if (mode === 1) {
        u = ((u % 1) + 1) % 1;
      } else if (mode === 2) {
        u = Math.abs(((u % 2) + 2) % 2 - 1);
      }
      this.position.set((u * 2 - 1) * distribution.radius, 0, 0);
    } else if (distribution.type === 'boxVolume') {
      this.position.set(
        (Math.random() - 0.5) * distribution.size[0],
        (Math.random() - 0.5) * distribution.size[1],
        (Math.random() - 0.5) * distribution.size[2]
      );
    } else {
      this.position.set(particle.position.x, particle.position.y, particle.position.z);
      const length = this.position.length();
      if (length <= 1e-12) {
        this.position.set(0, 0, 1);
      } else {
        this.position.multiplyScalar(1 / length);
      }
      const innerRadius = distribution.radius * (1 - distribution.thickness);
      const innerCube = innerRadius * innerRadius * innerRadius;
      const outerCube = distribution.radius * distribution.radius * distribution.radius;
      const radius = Math.cbrt(innerCube + Math.random() * (outerCube - innerCube));
      this.position.multiplyScalar(radius);
    }
    particle.position.x = this.position.x;
    particle.position.y = this.position.y;
    particle.position.z = this.position.z;
  }

  private applyRandomDirection(particle: Particle): void {
    const randomDirection = this.metadata.randomDirection;
    if (!randomDirection) return;
    this.velocity.set(particle.velocity.x, particle.velocity.y, particle.velocity.z);
    const sourceSpeed = this.velocity.length();
    if (sourceSpeed <= 1e-12) return;

    if (randomDirection.mode === 'coneSurface') {
      this.applyConeSurfaceRandomDirection(particle, randomDirection, sourceSpeed);
      return;
    }

    this.velocity.multiplyScalar(1 / sourceSpeed);
    randomUnitVector(this.randomDirection);
    this.velocity
      .multiplyScalar(1 - randomDirection.amount)
      .addScaledVector(this.randomDirection, randomDirection.amount);
    this.writeNormalizedVelocity(particle, sourceSpeed);
  }

  private applyRandomPosition(particle: Particle): void {
    const randomPosition = this.metadata.randomPosition;
    if (!randomPosition) return;
    const amount = Math.min(1, Math.max(0, randomPosition.amount));
    const sphericalAmount = Math.min(1, Math.max(0, randomPosition.sphericalAmount));
    if (randomPosition.mode === 'box') {
      this.randomPosition.set(Math.random() - 0.5, Math.random() - 0.5, Math.random() - 0.5);
      if (this.metadata.distribution?.type === 'boxVolume') {
        this.randomPosition.x *= this.metadata.distribution.size[0];
        this.randomPosition.y *= this.metadata.distribution.size[1];
        this.randomPosition.z *= this.metadata.distribution.size[2];
      }
      particle.position.x += (this.randomPosition.x - particle.position.x) * amount;
      particle.position.y += (this.randomPosition.y - particle.position.y) * amount;
      particle.position.z += (this.randomPosition.z - particle.position.z) * amount;
    } else {
      this.position.set(particle.position.x, particle.position.y, particle.position.z);
      const radius = this.position.length();
      randomUnitVector(this.randomPosition).multiplyScalar(radius);
      this.position.lerp(this.randomPosition, amount);
      particle.position.x = this.position.x;
      particle.position.y = this.position.y;
      particle.position.z = this.position.z;
    }
    if (sphericalAmount > 0) {
      this.velocity.set(particle.velocity.x, particle.velocity.y, particle.velocity.z);
      const speed = this.velocity.length();
      if (speed > 1e-12) {
        randomUnitVector(this.randomDirection);
        this.velocity.normalize().lerp(this.randomDirection, sphericalAmount).normalize().multiplyScalar(speed);
        particle.velocity.x = this.velocity.x;
        particle.velocity.y = this.velocity.y;
        particle.velocity.z = this.velocity.z;
      }
    }
  }

  private applyConeSurfaceRandomDirection(
    particle: Particle,
    randomDirection: Extract<UnityShapeRandomDirection, { mode: 'coneSurface' }>,
    sourceSpeed: number
  ): void {
    let posX = 0;
    let posY = 0;
    if (randomDirection.radius > 1e-12) {
      posX = particle.position.x / randomDirection.radius;
      posY = particle.position.y / randomDirection.radius;
    } else {
      const xyLength = Math.hypot(this.velocity.x, this.velocity.y);
      if (randomDirection.angle > 1e-12 && xyLength > 1e-12) {
        const z = Math.max(-1, Math.min(1, this.velocity.z / sourceSpeed));
        const radial = Math.max(0, Math.min(1, Math.acos(z) / randomDirection.angle));
        posX = (this.velocity.x / xyLength) * radial;
        posY = (this.velocity.y / xyLength) * radial;
      }
    }

    const angle = Math.random() * Math.PI * 2;
    const radius = Math.sqrt(
      UNITY_SHAPE_RANDOM_DIRECTION_MIN_RADIUS +
      Math.random() * (1 - UNITY_SHAPE_RANDOM_DIRECTION_MIN_RADIUS)
    );
    const amount = randomDirection.amount;
    const randomX = Math.cos(angle) * radius;
    const randomY = Math.sin(angle) * radius;
    const sinCone = Math.sin(randomDirection.angle);
    this.velocity.set(
      ((1 - amount) * posX + amount * randomX) * sinCone,
      ((1 - amount) * posY + amount * randomY) * sinCone,
      Math.cos(randomDirection.angle)
    );
    this.writeNormalizedVelocity(particle, sourceSpeed);
  }

  private writeNormalizedVelocity(particle: Particle, sourceSpeed: number): void {
    const directionLength = this.velocity.length();
    if (directionLength <= 1e-12) {
      this.velocity.set(0, 0, sourceSpeed);
    } else {
      this.velocity.multiplyScalar(sourceSpeed / directionLength);
    }
    particle.velocity.x = this.velocity.x;
    particle.velocity.y = this.velocity.y;
    particle.velocity.z = this.velocity.z;
  }

}

type UnitySizeParticleState =
  | { separateAxes: false; size: UnityCurveSample }
  | { separateAxes: true; x: UnityCurveSample; y: UnityCurveSample; z: UnityCurveSample };

type UnitySizeMetadata =
  | { separateAxes: false; size: Record<string, unknown> }
  | { separateAxes: true; x: Record<string, unknown>; y: Record<string, unknown>; z: Record<string, unknown> };

class UnitySizeOverLifetimeBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksSizeOverLifetime';
  private readonly sizeFactory?: UnityCurveFactory;
  private readonly xFactory?: UnityCurveFactory;
  private readonly yFactory?: UnityCurveFactory;
  private readonly zFactory?: UnityCurveFactory;
  private states = new WeakMap<Particle, UnitySizeParticleState>();

  constructor(private readonly metadata: UnitySizeMetadata) {
    if (metadata.separateAxes) {
      this.xFactory = compileUnityCurve(metadata.x, 'sizeOverLifetime.x');
      this.yFactory = compileUnityCurve(metadata.y, 'sizeOverLifetime.y');
      this.zFactory = compileUnityCurve(metadata.z, 'sizeOverLifetime.z');
    } else {
      this.sizeFactory = compileUnityCurve(metadata.size, 'sizeOverLifetime.size');
    }
  }

  initialize(particle: Particle): void {
    const state: UnitySizeParticleState = this.metadata.separateAxes
      ? {
          separateAxes: true,
          x: this.xFactory!(),
          y: this.yFactory!(),
          z: this.zFactory!()
        }
      : { separateAxes: false, size: this.sizeFactory!() };
    this.states.set(particle, state);
    this.apply(particle, state, 0);
  }

  update(particle: Particle): void {
    const state = this.states.get(particle);
    if (!state) return;
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    this.apply(particle, state, t);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnitySizeOverLifetimeBehavior(this.metadata);
  }

  reset(): void {
    // Preserve state for particles that outlive the emitter duration.
  }

  private apply(particle: Particle, state: UnitySizeParticleState, t: number): void {
    if (state.separateAxes) {
      particle.size.x = particle.startSize.x * state.x.evaluate(t);
      particle.size.y = particle.startSize.y * state.y.evaluate(t);
      particle.size.z = particle.startSize.z * state.z.evaluate(t);
      return;
    }
    const size = state.size.evaluate(t);
    particle.size.x = particle.startSize.x * size;
    particle.size.y = particle.startSize.y * size;
    particle.size.z = particle.startSize.z * size;
  }
}

type UnityCustomDataParticle = Particle & {
  __unityParticleQuarksCustom1?: QuarksVector4;
  __unityParticleQuarksCustom2?: QuarksVector4;
};

interface UnityCustomDataParticleState {
  custom1: [UnityCurveSample, UnityCurveSample, UnityCurveSample, UnityCurveSample];
  custom2: QuarksVector4;
}

class UnityCustomDataBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksCustomData';
  private readonly custom1Factories: [UnityCurveFactory, UnityCurveFactory, UnityCurveFactory, UnityCurveFactory];
  private readonly custom2: ColorGenerator | FunctionColorGenerator;
  private states = new WeakMap<Particle, UnityCustomDataParticleState>();

  constructor(private readonly metadata: UnityCustomDataMetadata) {
    this.custom1Factories = metadata.custom1.map((component, index) =>
      compileUnityCurve(component, `customData.custom1.${index}`)
    ) as [UnityCurveFactory, UnityCurveFactory, UnityCurveFactory, UnityCurveFactory];
    this.custom2 = ColorGeneratorFromJSON(metadata.custom2 as FunctionJSON);
  }

  initialize(particle: Particle): void {
    const state: UnityCustomDataParticleState = {
      custom1: this.custom1Factories.map((factory) => factory()) as [
        UnityCurveSample,
        UnityCurveSample,
        UnityCurveSample,
        UnityCurveSample
      ],
      custom2: new QuarksVector4()
    };
    this.custom2.startGen(particle.memory);
    this.states.set(particle, state);
    this.apply(particle as UnityCustomDataParticle, state, 0);
  }

  update(particle: Particle): void {
    const state = this.states.get(particle);
    if (!state) return;
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    this.apply(particle as UnityCustomDataParticle, state, t);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityCustomDataBehavior(this.metadata);
  }

  reset(): void {
    // Preserve custom data for particles that outlive emitter duration.
  }

  private apply(particle: UnityCustomDataParticle, state: UnityCustomDataParticleState, t: number): void {
    const custom1 = particle.__unityParticleQuarksCustom1 ??= new QuarksVector4();
    custom1.set(
      state.custom1[0].evaluate(t),
      state.custom1[1].evaluate(t),
      state.custom1[2].evaluate(t),
      state.custom1[3].evaluate(t)
    );
    if (this.custom2.type === 'function') {
      this.custom2.genColor(particle.memory, state.custom2, t);
    } else {
      this.custom2.genColor(particle.memory, state.custom2);
    }
    const custom2 = particle.__unityParticleQuarksCustom2 ??= new QuarksVector4();
    custom2.copy(state.custom2);
  }
}

interface SubEmitterParticleLike {
  age: number;
  life: number;
  position: { x: number; y: number; z: number };
  velocity: { x: number; y: number; z: number };
  color: { x: number; y: number; z: number; w: number };
  startColor: { x: number; y: number; z: number; w: number };
  size: { x: number; y: number; z: number; set(x: number, y: number, z: number): unknown };
  startSize: { x: number; y: number; z: number; set(x: number, y: number, z: number): unknown };
  rotation?: number | { x: number; y: number; z: number; w: number; set(x: number, y: number, z: number, w: number): unknown };
  speedModifier?: number;
  parentMatrix?: QuarksMatrixLike;
}

interface SubEmitterInheritanceMetadata {
  index: number;
  subParticleSystem: string;
  mode: number;
  inheritColor: boolean;
  inheritSize: boolean;
  inheritRotation: boolean;
  inheritLifetime: boolean;
  inheritDuration: boolean;
}

interface SubEmitterParentSnapshot {
  color: [number, number, number, number];
  size: number;
  rotation: number;
  remainingLifetime: number;
}

interface SubEmitterBehaviorLike {
  type?: string;
  particleSystem?: ParticleSystem;
  subParticleSystem?: Object3D & { system?: ParticleSystem };
  useVelocityAsBasis?: boolean;
  mode?: number;
  emit?: (particle: SubEmitterParticleLike, delta: number) => void;
  frameUpdate?: (delta: number) => void;
  setMatrixFromParticle?: (matrix: QuarksMatrixLike, particle: SubEmitterParticleLike) => void;
  reset?: () => void;
  subEmissions?: object[];
  __unityParticleQuarksSemanticsPatched?: boolean;
}

interface UnityParticleCapacityMetadata {
  maxParticles: number;
}

interface UnityTextureSheetAnimationMetadata {
  mode: 'grid' | 'sprites';
  animation: 'wholeSheet' | 'singleRow' | 'sprites';
  timeMode: 'lifetime' | 'fps' | 'speed';
  frameCount: number;
  tileCountX: number;
  tileCountY: number;
  cycleCount: number;
  fps: number;
  speedRange: [number, number];
  rowMode: 'custom' | 'random' | 'meshIndex';
  rowIndex: number;
  frameOverTime: Record<string, unknown>;
  startFrame: Record<string, unknown>;
  sprites: UnityTextureSheetSpriteFrame[];
}

interface UnityTextureSheetSpriteFrame {
  rect: [number, number, number, number];
  sizeMul: [number, number];
  pivot: [number, number];
}

const unityTextureSheetMetadata = new WeakMap<ParticleSystem, UnityTextureSheetAnimationMetadata>();

interface UnityTextureSheetParticleState {
  frameOverTime: UnityCurveSample;
  startFrame: UnityCurveSample;
  emitterTime: number;
  row: number;
}

class UnityTextureSheetAnimationBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksTextureSheetAnimation';
  private readonly frameFactory: UnityCurveFactory;
  private readonly startFrameFactory: UnityCurveFactory;
  private states = new WeakMap<Particle, UnityTextureSheetParticleState>();
  private emitterTime = 0;

  constructor(private readonly metadata: UnityTextureSheetAnimationMetadata) {
    this.frameFactory = compileUnityCurve(metadata.frameOverTime, 'textureSheetAnimation.frameOverTime');
    this.startFrameFactory = compileUnityCurve(metadata.startFrame, 'textureSheetAnimation.startFrame');
  }

  setEmitterTime(value: number): void {
    this.emitterTime = Math.min(1, Math.max(0, value));
  }

  initialize(particle: Particle): void {
    const state = {
      frameOverTime: this.frameFactory(),
      startFrame: this.startFrameFactory(),
      emitterTime: this.emitterTime,
      row: this.metadata.rowMode === 'random'
        ? Math.floor(Math.random() * this.metadata.tileCountY)
        : this.metadata.rowMode === 'meshIndex' ? 0 : this.metadata.rowIndex
    };
    this.states.set(particle, state);
    this.apply(particle, state, 0);
  }

  update(particle: Particle): void {
    const state = this.states.get(particle);
    if (!state) return;
    const t = particle.life <= 0 ? 1 : Math.min(1, Math.max(0, particle.age / particle.life));
    this.apply(particle, state, t);
  }

  frameUpdate(): void {}

  toJSON(): Record<string, unknown> {
    return { type: this.type };
  }

  clone(): Behavior {
    return new UnityTextureSheetAnimationBehavior(this.metadata);
  }

  reset(): void {
    // Preserve state for particles that outlive the emitter duration.
  }

  private apply(particle: Particle, state: UnityTextureSheetParticleState, t: number): void {
    const start = state.startFrame.evaluate(state.emitterTime);
    let phase: number;
    if (this.metadata.timeMode === 'fps') {
      phase = start + particle.age * this.metadata.fps / this.metadata.frameCount;
    } else if (this.metadata.timeMode === 'speed') {
      const range = this.metadata.speedRange;
      const width = range[1] - range[0];
      const speedT = width <= 1e-12
        ? 0
        : Math.min(1, Math.max(0, (particle.velocity.length() - range[0]) / width));
      phase = start + state.frameOverTime.evaluate(speedT) * this.metadata.cycleCount;
    } else {
      phase = start + state.frameOverTime.evaluate(t) * this.metadata.cycleCount;
    }
    const wrapped = phase - Math.floor(phase);
    const frame = Math.min(
      this.metadata.frameCount - 1,
      Math.max(0, Math.floor(wrapped * this.metadata.frameCount))
    );
    particle.uvTile = this.metadata.mode === 'grid' && this.metadata.animation === 'singleRow'
      ? Math.min(
          this.metadata.tileCountX * this.metadata.tileCountY - 1,
          Math.max(0, state.row) * this.metadata.tileCountX + frame
        )
      : frame;
  }
}

class UnityParticleHeadRotationBehavior implements Behavior {
  readonly type = 'UnityParticleQuarksParticleHeadRotation';

  constructor(
    private readonly system: ParticleSystem,
    private readonly metadata: UnityParticleHeadMetadata
  ) {}

  initialize(particle: Particle): void {
    initializeUnityParticleHeadRotation(this.system, this.metadata, particle);
  }

  update(): void {}
  frameUpdate(): void {}
  reset(): void {}
  toJSON(): Record<string, never> { return {}; }
  clone(): Behavior { return new UnityParticleHeadRotationBehavior(this.system, this.metadata); }
}

function initializeUnityParticleHeadRotation(
  system: ParticleSystem,
  metadata: UnityParticleHeadMetadata,
  particle: Particle,
  force = false
): void {
  const state = particle as Particle & { __unityParticleQuarksHeadRotationInitialized?: boolean };
  if (!force && state.__unityParticleQuarksHeadRotationInitialized === true) return;
  const generator = system.startRotation as unknown as {
    type?: string;
    startGen?: (memory: unknown) => void;
    genValue?: (...args: any[]) => any;
  };
  if (!generator || typeof generator.startGen !== 'function' || typeof generator.genValue !== 'function') return;
  // TrailParticle does not normally consume startRotation memory. Keep the
  // companion's one-shot sample in an isolated slot so adding a head does
  // not shift random slots used by the authoritative trail behaviors.
  const rotationMemory: unknown[] = [];
  const t = system.duration <= 0
    ? 0
    : Math.max(0, Math.min(1, system.emissionState.time / system.duration));
  generator.startGen(rotationMemory);
  if (metadata.renderMode === 2) {
    const rotation = new QuarksQuaternion();
    if (generator.type === 'rotation') {
      generator.genValue(rotationMemory, rotation, 1, t);
    } else {
      const angle = Number(generator.genValue(rotationMemory, t));
      rotation.setFromAxisAngle(new QuarksVector3(0, 1, 0), Number.isFinite(angle) ? angle : 0);
    }
    (particle as Particle & { rotation: QuarksQuaternion }).rotation = rotation;
    state.__unityParticleQuarksHeadRotationInitialized = true;
    return;
  }
  if (generator.type === 'rotation') {
    const rotation = new QuarksQuaternion();
    generator.genValue(rotationMemory, rotation, 1, t);
    const euler = new QuarksEuler().setFromQuaternion(rotation, 'XYZ');
    particle.rotation = euler.z;
  } else {
    const angle = Number(generator.genValue(rotationMemory, t));
    particle.rotation = Number.isFinite(angle) ? angle : 0;
  }
  state.__unityParticleQuarksHeadRotationInitialized = true;
}

function installUnityParticleHeadRotation(system: ParticleSystem, metadata: UnityParticleHeadMetadata): void {
  if (system.behaviors.some((behavior) => behavior.type === 'UnityParticleQuarksParticleHeadRotation')) return;
  const shape = system.emitterShape as unknown as {
    initialize(particle: Particle, emissionState: unknown): void;
  };
  const originalShapeInitialize = shape.initialize.bind(shape);
  shape.initialize = (particle, emissionState) => {
    const state = particle as Particle & { __unityParticleQuarksHeadRotationInitialized?: boolean };
    state.__unityParticleQuarksHeadRotationInitialized = false;
    initializeUnityParticleHeadRotation(system, metadata, particle, true);
    originalShapeInitialize(particle, emissionState);
  };
  system.behaviors.unshift(new UnityParticleHeadRotationBehavior(system, metadata));
}

function installUnityExporterBehaviors(effectRoot: Object3D, camera: Camera): void {
  const velocitySensitiveBehaviors = new Set([
    'UnityParticleQuarksVelocityOverLifetime',
    'UnityParticleQuarksForceOverLifetime',
    'SpeedOverLife',
    'ForceOverLife',
    'GravityForce',
    'LimitSpeedOverLife',
    'Noise',
    'OrbitOverLife'
  ]);
  effectRoot.traverse((object) => {
    const emitter = object as Object3D & { system?: ParticleSystem };
    if (object.type !== 'ParticleEmitter' || !emitter.system) return;
    const rendererAlignmentMetadata = readUnityRendererAlignmentMetadata(emitter);
    if (rendererAlignmentMetadata) installUnityRendererAlignment(emitter.system, rendererAlignmentMetadata);
    const rendererPivotMetadata = readUnityRendererPivotMetadata(emitter);
    if (rendererPivotMetadata) unityRendererPivotMetadata.set(emitter.system, rendererPivotMetadata);
    const customDataMetadata = readUnityCustomDataMetadata(emitter);
    if (customDataMetadata) {
      unityCustomDataMetadata.set(emitter.system, customDataMetadata);
      emitter.system.behaviors.push(new UnityCustomDataBehavior(customDataMetadata));
    }
    const particleHeadMetadata = readUnityParticleHeadMetadata(emitter);
    if (particleHeadMetadata) installUnityParticleHeadRotation(emitter.system, particleHeadMetadata);
    const colorMetadata = readUnityColorSemanticsMetadata(emitter);
    if (colorMetadata) {
      unityColorSemanticsMetadata.set(emitter.system, colorMetadata);
      applyUnityColorSemantics(emitter.system, colorMetadata);
    }
    validateUnityMaterialProfileMetadata(emitter);
    const materialMetadata = readUnityMaterialMetadata(emitter);
    if (materialMetadata) {
      unityMaterialMetadata.set(emitter.system, materialMetadata);
      applyUnityMaterialState(emitter.system, materialMetadata);
    }
    const startColorMetadata = readUnityStartColorMetadata(emitter);
    if (startColorMetadata) applyUnityStartColorSemantics(emitter.system, startColorMetadata);
    const trailInheritParticleColorMetadata = readUnityTrailInheritParticleColorMetadata(emitter);
    if (trailInheritParticleColorMetadata) {
      const fallbackIndex = emitter.system.behaviors.findIndex((candidate) => candidate.type === 'ColorOverLife');
      if (fallbackIndex < 0) {
        throw new Error(`Exporter Trail inheritParticleColor metadata has no stock fallback on ${emitter.uuid}.`);
      }
      const fallback = emitter.system.behaviors[fallbackIndex] as Behavior & {
        color?: ColorGenerator | FunctionColorGenerator;
      };
      if (!fallback.color || (fallback.color.type !== 'value' && fallback.color.type !== 'function')) {
        throw new Error(`Exporter Trail inheritParticleColor fallback is not a color generator on ${emitter.uuid}.`);
      }
      emitter.system.behaviors.splice(
        fallbackIndex,
        1,
        new UnityTrailInheritParticleColorBehavior(
          fallback.color,
          QuarksGradient.fromJSON(trailInheritParticleColorMetadata.particleColorOverLifetime)
        )
      );
    }
    const capacityMetadata = readUnityParticleCapacityMetadata(emitter);
    installParticleSpawnBudget(emitter.system, capacityMetadata?.maxParticles);
    const simulationSpeedMetadata = readUnitySimulationSpeedMetadata(emitter);
    if (simulationSpeedMetadata) installUnitySimulationSpeed(emitter.system, simulationSpeedMetadata);
    const startDelayMetadata = readUnityStartDelayMetadata(emitter);
    if (startDelayMetadata) installUnityStartDelay(emitter.system, startDelayMetadata);
    const lifetimeByEmitterSpeedMetadata = readUnityLifetimeByEmitterSpeedMetadata(emitter);
    if (lifetimeByEmitterSpeedMetadata) {
      installUnityLifetimeByEmitterSpeed(emitter.system, lifetimeByEmitterSpeedMetadata);
    }
    const textureSheetMetadata = readUnityTextureSheetAnimationMetadata(emitter);
    if (textureSheetMetadata) {
      unityTextureSheetMetadata.set(emitter.system, textureSheetMetadata);
      const fallbackIndex = emitter.system.behaviors.findIndex((candidate) => candidate.type === 'FrameOverLife');
      if (fallbackIndex < 0) {
        throw new Error(`Exporter Texture Sheet Animation metadata has no stock fallback on ${emitter.uuid}.`);
      }
      const behavior = new UnityTextureSheetAnimationBehavior(textureSheetMetadata);
      emitter.system.behaviors.splice(fallbackIndex, 1, behavior);
      const textureSystem = emitter.system as unknown as {
        spawn(count: number, emissionState: unknown, matrix: unknown): void;
        duration: number;
      };
      const originalSpawn = textureSystem.spawn.bind(textureSystem);
      textureSystem.spawn = (count, emissionState, matrix) => {
        const time = isRecord(emissionState) && typeof emissionState.time === 'number'
          ? emissionState.time
          : 0;
        behavior.setEmitterTime(textureSystem.duration <= 0 ? 0 : time / textureSystem.duration);
        originalSpawn(count, emissionState, matrix);
      };
    }
    const shapeMetadata = readUnityShapeMetadata(emitter);
    if (shapeMetadata) {
      const behavior = new UnityShapeSemanticsBehavior(shapeMetadata);
      const emitterShape = emitter.system.emitterShape as unknown as {
        currentValue?: number;
        initialize(particle: Particle, emissionState: unknown): void;
      };
      const originalShapeInitialize = emitterShape.initialize.bind(emitterShape);
      emitterShape.initialize = (particle, emissionState) => {
        const captureMeshNormal = shapeMetadata.meshNormalOffset !== undefined ||
          shapeMetadata.alignToDirection === true;
        if (captureMeshNormal) {
          const authoredStartSpeed = particle.startSpeed;
          particle.startSpeed = 1;
          try {
            originalShapeInitialize(particle, emissionState);
          } finally {
            particle.startSpeed = authoredStartSpeed;
          }
          const length = Math.hypot(particle.velocity.x, particle.velocity.y, particle.velocity.z);
          const sampledNormal: [number, number, number] = length > 1e-12
            ? [particle.velocity.x / length, particle.velocity.y / length, particle.velocity.z / length]
            : [0, 0, 1];
          particle.velocity.set(...sampledNormal).multiplyScalar(authoredStartSpeed);
          behavior.transformBirth(
            particle,
            sampledNormal,
            emissionState,
            emitterShape.currentValue ?? 0
          );
          return;
        }
        originalShapeInitialize(particle, emissionState);
        behavior.transformBirth(
          particle,
          undefined,
          emissionState,
          emitterShape.currentValue ?? 0
        );
      };
      const originalEmit = emitter.system.emit.bind(emitter.system);
      emitter.system.emit = (delta, emissionState, emitterMatrix) => {
        behavior.setEmissionMatrix(emitterMatrix);
        try {
          originalEmit(delta, emissionState, emitterMatrix);
        } finally {
          behavior.clearEmissionMatrix();
        }
      };
      emitter.system.behaviors.unshift(behavior);
    }

    const sizeMetadata = readUnitySizeMetadata(emitter);
    if (sizeMetadata) {
      const fallbackIndex = emitter.system.behaviors.findIndex((candidate) => candidate.type === 'SizeOverLife');
      if (fallbackIndex < 0) {
        throw new Error(`Exporter Size over Lifetime metadata has no stock fallback on ${emitter.uuid}.`);
      }
      emitter.system.behaviors.splice(fallbackIndex, 1, new UnitySizeOverLifetimeBehavior(sizeMetadata));
    }

    const inheritVelocityMetadata = readUnityInheritVelocityMetadata(emitter);
    if (inheritVelocityMetadata) {
      const insertionIndex = emitter.system.behaviors.findIndex((candidate) =>
        velocitySensitiveBehaviors.has(candidate.type));
      if (inheritVelocityMetadata.mode === 'current') {
        const behavior = new UnityInheritVelocityCurrentBehavior(
          inheritVelocityMetadata,
          emitter,
          emitter.system.worldSpace
        );
        emitter.system.behaviors.splice(
          insertionIndex < 0 ? emitter.system.behaviors.length : insertionIndex,
          0,
          behavior
        );
        const currentSystem = emitter.system as ParticleSystem & { restart(): void };
        const originalRestart = currentSystem.restart.bind(currentSystem);
        currentSystem.restart = () => {
          behavior.restart();
          originalRestart();
        };
      } else {
        const context = new UnityInheritVelocityInitialContext();
        const behavior = new UnityInheritVelocityInitialBehavior(inheritVelocityMetadata, context);
        emitter.system.behaviors.splice(
          insertionIndex < 0 ? emitter.system.behaviors.length : insertionIndex,
          0,
          behavior
        );

        const inheritSystem = emitter.system as unknown as {
          worldSpace: boolean;
          emit(delta: number, emissionState: unknown, emitterMatrix: QuarksMatrixLike): void;
          restart(): void;
        };
        const originalEmit = inheritSystem.emit.bind(inheritSystem);
        inheritSystem.emit = (delta, emissionState, emitterMatrix) => {
          context.runWithEmitterVelocity(
            delta,
            emissionState,
            emitterMatrix,
            inheritSystem.worldSpace,
            () => originalEmit(delta, emissionState, emitterMatrix)
          );
        };
        const originalRestart = inheritSystem.restart.bind(inheritSystem);
        inheritSystem.restart = () => {
          context.clearMotionBaseline();
          originalRestart();
        };
      }
    }

    const velocityMetadata = readUnityVelocityMetadata(emitter);
    if (velocityMetadata) {
      const behavior = new UnityVelocityOverLifetimeBehavior(velocityMetadata);
      const insertionIndex = emitter.system.behaviors.findIndex((candidate) =>
        velocitySensitiveBehaviors.has(candidate.type));
      emitter.system.behaviors.splice(
        insertionIndex < 0 ? emitter.system.behaviors.length : insertionIndex,
        0,
        behavior
      );
    }

    const forceMetadata = readUnityForceMetadata(emitter);
    if (forceMetadata) {
      const insertionIndex = emitter.system.behaviors.findIndex((candidate) =>
        candidate.type === 'LimitSpeedOverLife' || candidate.type === 'UnityParticleQuarksLimitVelocityOverLifetime');
      emitter.system.behaviors.splice(
        insertionIndex < 0 ? emitter.system.behaviors.length : insertionIndex,
        0,
        new UnityForceOverLifetimeBehavior(forceMetadata)
      );
    }

    const gravityMetadata = readUnityGravityMetadata(emitter);
    if (gravityMetadata) {
      const insertionIndex = emitter.system.behaviors.findIndex((candidate) =>
        velocitySensitiveBehaviors.has(candidate.type));
      emitter.system.behaviors.splice(
        insertionIndex < 0 ? emitter.system.behaviors.length : insertionIndex,
        0,
        new UnityGravityBehavior(gravityMetadata)
      );
    }

    const noiseMetadata = readUnityNoiseMetadata(emitter);
    if (noiseMetadata) {
      const fallbackIndex = emitter.system.behaviors.findIndex((candidate) => candidate.type === 'Noise');
      if (fallbackIndex < 0) {
        throw new Error(`Exporter Noise metadata has no stock fallback on ${emitter.uuid}.`);
      }
      emitter.system.behaviors.splice(fallbackIndex, 1);
      if (!emitter.system.behaviors.some((candidate) =>
        candidate.type === 'UnityParticleQuarksNoiseAnimatedVelocityClear')) {
        emitter.system.behaviors.unshift(new UnityNoiseAnimatedVelocityClearBehavior());
      }
      const behavior = new UnityNoiseBehavior(noiseMetadata, emitter.system.duration);
      const insertionIndex = emitter.system.behaviors.findIndex((candidate) =>
        candidate.type === 'UnityParticleQuarksInheritVelocityInitial' ||
        candidate.type === 'UnityParticleQuarksForceOverLifetime' || candidate.type === 'ForceOverLife' ||
        candidate.type === 'LimitSpeedOverLife' || candidate.type === 'UnityParticleQuarksLimitVelocityOverLifetime');
      emitter.system.behaviors.splice(
        insertionIndex < 0 ? emitter.system.behaviors.length : insertionIndex,
        0,
        behavior
      );
      const noiseSystem = emitter.system as unknown as { restart(): void };
      const originalRestart = noiseSystem.restart.bind(noiseSystem);
      noiseSystem.restart = () => {
        originalRestart();
        behavior.restart();
      };
    }

    const lightsMetadata = readUnityParticleLightsMetadata(emitter);
    if (lightsMetadata) installUnityParticleLights(emitter, emitter.system, lightsMetadata);

    const limitVelocityMetadata = readUnityLimitVelocityMetadata(emitter);
    if (limitVelocityMetadata) {
      const fallbackIndex = emitter.system.behaviors.findIndex((candidate) =>
        candidate.type === 'LimitSpeedOverLife');
      if (fallbackIndex < 0) {
        throw new Error(`Exporter Limit Velocity metadata has no stock fallback on ${emitter.uuid}.`);
      }
      emitter.system.behaviors.splice(
        fallbackIndex,
        1,
        new UnityLimitVelocityOverLifetimeBehavior(limitVelocityMetadata)
      );
    }

    const meshScalarRotationMetadata = readUnityMeshScalarRotationMetadata(emitter);
    if (meshScalarRotationMetadata) {
      const context = new UnityMeshScalarRotationContext(meshScalarRotationMetadata);
      const emitterShape = emitter.system.emitterShape as unknown as {
        initialize(particle: Particle, emissionState: unknown): void;
      };
      const originalShapeInitialize = emitterShape.initialize.bind(emitterShape);
      emitterShape.initialize = (particle, emissionState) => {
        context.captureStartRotation(particle);
        originalShapeInitialize(particle, emissionState);
      };

      const originalEmit = emitter.system.emit.bind(emitter.system);
      emitter.system.emit = (delta, emissionState, emitterMatrix) => {
        context.setEmissionMatrix(emitterMatrix);
        try {
          originalEmit(delta, emissionState, emitterMatrix);
        } finally {
          context.clearEmissionMatrix();
        }
      };

      const shapeBehaviorIndex = emitter.system.behaviors.findIndex((candidate) =>
        candidate.type === 'UnityParticleQuarksShapeSemantics');
      const preparationIndex = shapeBehaviorIndex < 0 ? 0 : shapeBehaviorIndex + 1;
      emitter.system.behaviors.splice(
        preparationIndex,
        0,
        new UnityMeshScalarRotationPreparationBehavior(context)
      );
      let finalizationIndex = -1;
      for (let index = 0; index < emitter.system.behaviors.length; index += 1) {
        if (emitter.system.behaviors[index]?.type === 'Rotation3DOverLife') finalizationIndex = index + 1;
      }
      if (finalizationIndex < 0) finalizationIndex = preparationIndex + 1;
      emitter.system.behaviors.splice(
        finalizationIndex,
        0,
        new UnityMeshScalarRotationFinalizationBehavior(context)
      );
    }

    const meshVelocityAlignmentMetadata = readUnityMeshVelocityAlignmentMetadata(emitter);
    if (meshVelocityAlignmentMetadata) {
      const context = new UnityMeshVelocityAlignmentContext(meshVelocityAlignmentMetadata);
      const emitterShape = emitter.system.emitterShape as unknown as {
        initialize(particle: Particle, emissionState: unknown): void;
      };
      const originalShapeInitialize = emitterShape.initialize.bind(emitterShape);
      emitterShape.initialize = (particle, emissionState) => {
        context.captureStartRotation(particle);
        originalShapeInitialize(particle, emissionState);
      };
      const scalarPreparationIndex = emitter.system.behaviors.findIndex((candidate) =>
        candidate.type === 'UnityParticleQuarksMeshScalarRotationPreparation');
      const shapeBehaviorIndex = emitter.system.behaviors.findIndex((candidate) =>
        candidate.type === 'UnityParticleQuarksShapeSemantics');
      const preparationIndex = scalarPreparationIndex >= 0
        ? scalarPreparationIndex
        : shapeBehaviorIndex < 0 ? 0 : shapeBehaviorIndex + 1;
      emitter.system.behaviors.splice(
        preparationIndex,
        0,
        new UnityMeshVelocityAlignmentPreparationBehavior(context)
      );
      emitter.system.behaviors.push(new UnityMeshVelocityAlignmentFinalizationBehavior(context));
    }
    const meshCameraAlignmentMetadata = readUnityMeshCameraAlignmentMetadata(emitter);
    if (meshCameraAlignmentMetadata) {
      const context = new UnityMeshCameraAlignmentContext(
        meshCameraAlignmentMetadata,
        emitter,
        camera
      );
      const emitterShape = emitter.system.emitterShape as unknown as {
        initialize(particle: Particle, emissionState: unknown): void;
      };
      const originalShapeInitialize = emitterShape.initialize.bind(emitterShape);
      emitterShape.initialize = (particle, emissionState) => {
        context.captureStartRotation(particle);
        originalShapeInitialize(particle, emissionState);
      };
      // Remove the previous frame's camera quaternion before authored rotation,
      // scalar rotation, and other velocity-sensitive behaviors run.
      emitter.system.behaviors.unshift(new UnityMeshCameraAlignmentPreparationBehavior(context));
      // Camera alignment must be the last Mesh rotation operation. The
      // rotation-by-speed adapter is inserted immediately before this marker.
      emitter.system.behaviors.push(new UnityMeshCameraAlignmentFinalizationBehavior(context));
    }
    const meshRotationBySpeedMetadata = readUnityMeshRotationBySpeedMetadata(emitter);
    if (meshRotationBySpeedMetadata) {
      const behavior = new UnityMeshRotationBySpeedBehavior(meshRotationBySpeedMetadata);
      const alignmentFinalizationIndex = emitter.system.behaviors.findIndex((candidate) =>
        candidate.type === 'UnityParticleQuarksMeshVelocityAlignmentFinalization' ||
        candidate.type === 'UnityParticleQuarksMeshCameraAlignmentFinalization');
      emitter.system.behaviors.splice(
        alignmentFinalizationIndex < 0 ? emitter.system.behaviors.length : alignmentFinalizationIndex,
        0,
        behavior
      );
    }
    const trailSemanticsMetadata = readUnityTrailSemanticsMetadata(emitter);
    if (trailSemanticsMetadata) {
      installUnityTrailSemantics(emitter, emitter.system, trailSemanticsMetadata);
    }
  });
}

function readUnityShapeMetadata(emitter: Object3D): UnityShapeMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.shapeSemantics === undefined) return null;
  const value = exporterData.shapeSemantics;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.shape_semantics.v1') {
    throw new Error(`Malformed exporter Shape metadata on ${emitter.uuid}.`);
  }

  let distribution: UnityShapeDistribution | undefined;
  if (value.distribution !== undefined) {
    if (!isRecord(value.distribution) || typeof value.distribution.type !== 'string') {
      throw new Error(`Malformed exporter Shape distribution on ${emitter.uuid}.`);
    }
    if (value.distribution.type === 'sphereVolume' || value.distribution.type === 'hemisphereVolume') {
      const radius = finiteUnityNumber(value.distribution.radius, `${emitter.uuid}.shape.radius`);
      const thickness = finiteUnityNumber(value.distribution.thickness, `${emitter.uuid}.shape.thickness`);
      if (radius < 0 || thickness <= 0 || thickness > 1) {
        throw new Error(`Exporter radial Shape metadata is out of range on ${emitter.uuid}.`);
      }
      distribution = { type: value.distribution.type, radius, thickness };
    } else if (value.distribution.type === 'boxVolume') {
      const size = readFiniteTuple3(value.distribution.size, `${emitter.uuid}.shape.size`);
      if (size.some((axis) => axis < 0)) {
        throw new Error(`Exporter Box Shape metadata is out of range on ${emitter.uuid}.`);
      }
      distribution = { type: 'boxVolume', size };
    } else if (value.distribution.type === 'singleSidedEdge') {
      const radius = finiteUnityNumber(value.distribution.radius, `${emitter.uuid}.shape.radius`);
      const mode = finiteUnityNumber(value.distribution.mode, `${emitter.uuid}.shape.mode`);
      const spread = finiteUnityNumber(value.distribution.spread, `${emitter.uuid}.shape.spread`);
      if (radius < 0 || !Number.isInteger(mode) || mode < 0 || mode > 3 || spread < 0 || spread > 1) {
        throw new Error(`Exporter Single Sided Edge metadata is out of range on ${emitter.uuid}.`);
      }
      distribution = { type: 'singleSidedEdge', radius, mode, spread };
    } else {
      throw new Error(`Unsupported exporter Shape distribution on ${emitter.uuid}.`);
    }
  }

  const directionMode = value.directionMode;
  if (directionMode !== undefined && directionMode !== 'localY' && directionMode !== 'localZ') {
    throw new Error(`Unsupported exporter Shape direction mode on ${emitter.uuid}.`);
  }

  let randomDirection: UnityShapeRandomDirection | undefined;
  if (value.randomDirection !== undefined) {
    if (!isRecord(value.randomDirection) || typeof value.randomDirection.mode !== 'string') {
      throw new Error(`Malformed exporter Shape random direction on ${emitter.uuid}.`);
    }
    const amount = finiteUnityNumber(value.randomDirection.amount, `${emitter.uuid}.shape.randomDirection.amount`);
    if (amount <= 0 || amount > 1) {
      throw new Error(`Exporter Shape random direction amount is out of range on ${emitter.uuid}.`);
    }
    if (value.randomDirection.mode === 'lerpRandomUnit') {
      randomDirection = { mode: 'lerpRandomUnit', amount };
    } else if (value.randomDirection.mode === 'coneSurface') {
      const angle = finiteUnityNumber(value.randomDirection.angle, `${emitter.uuid}.shape.randomDirection.angle`);
      const radius = finiteUnityNumber(value.randomDirection.radius, `${emitter.uuid}.shape.randomDirection.radius`);
      if (angle < 0 || angle > Math.PI / 2 + 1e-6 || radius < 0) {
        throw new Error(`Exporter Cone Shape random direction metadata is out of range on ${emitter.uuid}.`);
      }
      randomDirection = { mode: 'coneSurface', amount, angle, radius };
    } else {
      throw new Error(`Unsupported exporter Shape random direction mode on ${emitter.uuid}.`);
    }
  }

  let randomPosition: UnityShapeRandomPosition | undefined;
  if (value.randomPosition !== undefined) {
    if (!isRecord(value.randomPosition) ||
        (value.randomPosition.mode !== 'box' && value.randomPosition.mode !== 'radial')) {
      throw new Error(`Malformed exporter Shape random position on ${emitter.uuid}.`);
    }
    const amount = finiteUnityNumber(value.randomPosition.amount, `${emitter.uuid}.shape.randomPosition.amount`);
    const sphericalAmount = finiteUnityNumber(value.randomPosition.sphericalAmount, `${emitter.uuid}.shape.randomPosition.sphericalAmount`);
    if (amount < 0 || amount > 1 || sphericalAmount < 0 || sphericalAmount > 1) {
      throw new Error(`Exporter Shape random position amount is out of range on ${emitter.uuid}.`);
    }
    randomPosition = { mode: value.randomPosition.mode, amount, sphericalAmount };
  }
  const alignToDirection = value.alignToDirection;
  if (alignToDirection !== undefined && alignToDirection !== true) {
    throw new Error(`Exporter Shape alignToDirection flag must be true on ${emitter.uuid}.`);
  }
  const meshNormalOffset = value.meshNormalOffset === undefined
    ? undefined
    : finiteUnityNumber(value.meshNormalOffset, `${emitter.uuid}.shape.meshNormalOffset`);

  const legacyRandomDirectionAmount = value.randomDirectionAmount;
  if (legacyRandomDirectionAmount !== undefined && legacyRandomDirectionAmount !== 1) {
    throw new Error(`Exporter randomDirectionAmount metadata must be exactly 1 on ${emitter.uuid}.`);
  }
  if (legacyRandomDirectionAmount === 1) {
    console.warn(`Exporter randomDirectionAmount metadata on ${emitter.uuid} uses the withdrawn pre-0.1.17 mapping; retaining the stock Quarks Shape direction. Re-export this effect with exporter 0.1.17 or newer.`);
  }
  const readTransform = (raw: unknown, field: string): number[] | undefined => {
    if (raw === undefined) return undefined;
    if (!Array.isArray(raw) || raw.length !== 16 ||
        !raw.every((entry) => typeof entry === 'number' && Number.isFinite(entry))) {
      throw new Error(`Malformed exporter Shape ${field} on ${emitter.uuid}.`);
    }
    return raw.map(Number);
  };
  const birthTransform = readTransform(value.birthTransform, 'birth transform');
  const birthPositionTransform = readTransform(value.birthPositionTransform, 'birth position transform');
  const birthDirectionTransform = readTransform(value.birthDirectionTransform, 'birth direction transform');
  if (birthTransform && (birthPositionTransform || birthDirectionTransform)) {
    throw new Error(`Exporter Shape metadata mixes legacy and split birth transforms on ${emitter.uuid}.`);
  }
  const correctWorldSpaceBirthVelocity = value.correctWorldSpaceBirthVelocity;
  if (correctWorldSpaceBirthVelocity !== undefined && correctWorldSpaceBirthVelocity !== true) {
    throw new Error(`Exporter world-space birth velocity flag must be true on ${emitter.uuid}.`);
  }
  if (!distribution && directionMode !== 'localZ' && !birthTransform && !birthPositionTransform &&
      !birthDirectionTransform && !randomDirection && !randomPosition && alignToDirection !== true &&
      meshNormalOffset === undefined &&
      correctWorldSpaceBirthVelocity !== true) {
    if (legacyRandomDirectionAmount === 1) return null;
    throw new Error(`Exporter Shape metadata is empty on ${emitter.uuid}.`);
  }
  const metadata: UnityShapeMetadata = {};
  if (distribution) metadata.distribution = distribution;
  if (directionMode === 'localZ') metadata.directionMode = directionMode;
  if (randomDirection) metadata.randomDirection = randomDirection;
  if (randomPosition) metadata.randomPosition = randomPosition;
  if (alignToDirection === true) metadata.alignToDirection = true;
  if (meshNormalOffset !== undefined) metadata.meshNormalOffset = meshNormalOffset;
  if (birthTransform) metadata.birthTransform = birthTransform;
  if (birthPositionTransform) metadata.birthPositionTransform = birthPositionTransform;
  if (birthDirectionTransform) metadata.birthDirectionTransform = birthDirectionTransform;
  if (correctWorldSpaceBirthVelocity === true) metadata.correctWorldSpaceBirthVelocity = true;
  return metadata;
}

function readUnityColorSemanticsMetadata(emitter: Object3D): UnityColorSemanticsMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.colorSemantics === undefined) return null;
  const value = exporterData.colorSemantics;
  if (!isRecord(value) ||
      (value.schemaVersion !== 'unity_particle_quarks_exporter.gamma_color.v1' &&
       value.schemaVersion !== 'unity_particle_quarks_exporter.color.v2')) {
    throw new Error(`Malformed exporter color metadata on ${emitter.uuid}.`);
  }
  const projectColorSpace = value.schemaVersion === 'unity_particle_quarks_exporter.gamma_color.v1'
    ? 'gamma'
    : value.sourceProjectColorSpace;
  if (projectColorSpace !== 'gamma' && projectColorSpace !== 'linear') {
    throw new Error(`Malformed exporter project color space on ${emitter.uuid}.`);
  }
  if (value.schemaVersion === 'unity_particle_quarks_exporter.color.v2' && value.outputColorSpace !== 'srgb') {
    throw new Error(`Malformed exporter output color space on ${emitter.uuid}.`);
  }
  const materialColor = isRecord(value.materialColor)
    ? [
        finiteUnityNumber(value.materialColor.r, `${emitter.uuid}.color.materialColor.r`),
        finiteUnityNumber(value.materialColor.g, `${emitter.uuid}.color.materialColor.g`),
        finiteUnityNumber(value.materialColor.b, `${emitter.uuid}.color.materialColor.b`),
        finiteUnityNumber(value.materialColor.a, `${emitter.uuid}.color.materialColor.a`)
      ] as [number, number, number, number]
    : readFiniteTuple4(value.materialColor, `${emitter.uuid}.color.materialColor`);
  return { projectColorSpace, materialColor };
}

function applyUnityColorSemantics(system: ParticleSystem, metadata: UnityColorSemanticsMetadata): void {
  const material = system.rendererSettings.material as Material & {
    color?: { setRGB(r: number, g: number, b: number): unknown };
    opacity?: number;
  };
  if (!material?.color || typeof material.color.setRGB !== 'function') {
    throw new Error('Exporter color metadata requires a color-capable Quarks material.');
  }
  // Three Color.setRGB writes working-space values. This intentionally avoids
  // ObjectLoader's integer/setHex sRGB decode, including for Linear Unity
  // material colors that are already in the shader working space.
  material.color.setRGB(metadata.materialColor[0], metadata.materialColor[1], metadata.materialColor[2]);
  material.opacity = metadata.materialColor[3];
  material.needsUpdate = true;
}

function readUnityMaterialMetadata(emitter: Object3D): UnityMaterialMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData) return null;
  const value = exporterData.materialSemantics;
  if (value !== undefined && (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.material.v1' ||
      (value.fragmentColorMode !== 'stock' &&
       value.fragmentColorMode !== 'legacySoftAdditive' &&
       value.fragmentColorMode !== 'legacyAlphaPremultiply' &&
       value.fragmentColorMode !== 'legacyMultiply' &&
       value.fragmentColorMode !== 'legacyMultiplyDouble' &&
       value.fragmentColorMode !== 'hovlAdditivePremultiply' &&
       value.fragmentColorMode !== 'invisibleFallback'))) {
    throw new Error(`Malformed exporter material metadata on ${emitter.uuid}.`);
  }

  const profileId = value === undefined || value.profileId === undefined ? undefined : String(value.profileId);
  const profileMetadataKey = value === undefined || value.profileMetadataKey === undefined ? undefined : String(value.profileMetadataKey);
  if (profileId !== undefined && !supportedUnityMaterialProfiles.has(profileId)) {
    throw new Error(`Unsupported exporter material profile ${profileId} on ${emitter.uuid}.`);
  }
  if (profileMetadataKey !== undefined &&
      !/^unity_particle_quarks_exporter\.material\.(builtin|urp|custom)\.[A-Za-z0-9.]+\.v(?:1|2)$/.test(profileMetadataKey)) {
    throw new Error(`Malformed exporter material profile metadata key on ${emitter.uuid}.`);
  }
  if (profileMetadataKey !== undefined && profileId === undefined) {
    throw new Error(`Exporter material profile metadata is missing profileId on ${emitter.uuid}.`);
  }
  if (profileMetadataKey !== undefined && profileId !== undefined &&
      profileMetadataKey !== `unity_particle_quarks_exporter.material.${profileId}.${profileId === 'custom.piloto.uberfxsg' ? 'v2' : 'v1'}` &&
      profileMetadataKey !== `unity_particle_quarks_exporter.material.${profileId}.v1`) {
    throw new Error(`Exporter material profile metadata key does not match profileId on ${emitter.uuid}.`);
  }
  let cameraFade: UnityCameraFadeMetadata | undefined;
  if (value !== undefined && value.cameraFade !== undefined) {
    if (!isRecord(value.cameraFade)) {
      throw new Error(`Malformed exporter camera-fade metadata on ${emitter.uuid}.`);
    }
    const near = finiteUnityNumber(value.cameraFade.near, `${emitter.uuid}.material.cameraFade.near`);
    const far = finiteUnityNumber(value.cameraFade.far, `${emitter.uuid}.material.cameraFade.far`);
    const smoothness = finiteUnityNumber(value.cameraFade.smoothness, `${emitter.uuid}.material.cameraFade.smoothness`);
    if (near < 0 || far <= near || smoothness <= 0) {
      throw new Error(`Exporter camera-fade range is invalid on ${emitter.uuid}.`);
    }
    cameraFade = { near, far, smoothness };
  }
  const alpha = readUnityMaterialAlphaMetadata(exporterData.materialAlpha, emitter.uuid);
  const blend = readUnityMaterialBlendMetadata(exporterData.materialBlend, emitter.uuid);
  const textureUv = readUnityMaterialTextureUvMetadata(exporterData.materialTextureUv, emitter.uuid);
  const shaderParameters = readUnityMaterialShaderParameters(exporterData.materialShaderParameters, emitter.uuid);
  if (value === undefined && alpha === undefined && blend === undefined && textureUv === undefined && shaderParameters === undefined) return null;
  return {
    fragmentColorMode: value === undefined ? 'stock' : value.fragmentColorMode,
    ...(value?.baseColorChannel === undefined ? {} : { baseColorChannel: readUnityMaterialBaseColorChannel(value.baseColorChannel, emitter.uuid) }),
    cameraFade,
    ...(profileId === undefined ? {} : { profileId }),
    ...(profileMetadataKey === undefined ? {} : { profileMetadataKey }),
    ...(alpha === undefined ? {} : { alpha }),
    ...(blend === undefined ? {} : { blend }),
    ...(textureUv === undefined ? {} : { textureUv }),
    ...(shaderParameters === undefined ? {} : { shaderParameters })
  };
}

function readUnityMaterialBaseColorChannel(value: unknown, owner: string): UnityMaterialBaseColorChannel {
  if (value === undefined) return 'rgb';
  if (value !== 'rgb' && value !== 'r' && value !== 'g' && value !== 'b' && value !== 'a') {
    throw new Error(`Unsupported exporter material base color channel ${String(value)} on ${owner}.`);
  }
  return value;
}

function readUnityMaterialAlphaMetadata(value: unknown, owner: string): UnityMaterialAlphaMetadata | undefined {
  if (value === undefined) return undefined;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.material.alpha.v1' || !isRecord(value.clip)) {
    throw new Error(`Malformed exporter material alpha metadata on ${owner}.`);
  }
  const baseChannel = isRecord(value.base) && typeof value.base.channel === 'string' ? value.base.channel : 'a';
  if (baseChannel !== 'r' && baseChannel !== 'g' && baseChannel !== 'b' && baseChannel !== 'a') {
    throw new Error(`Unsupported exporter material alpha channel ${String(baseChannel)} on ${owner}.`);
  }
  const factorChannel = typeof value.factorChannel === 'string' ? value.factorChannel : 'r';
  if (factorChannel !== 'r' && factorChannel !== 'g' && factorChannel !== 'b' && factorChannel !== 'a') {
    throw new Error(`Unsupported exporter material alpha factor channel ${String(factorChannel)} on ${owner}.`);
  }
  const clipEnabled = value.clip.enabled === true;
  const clipThreshold = finiteUnityNumber(value.clip.threshold, `${owner}.materialAlpha.clip.threshold`);
  if (clipThreshold < 0 || clipThreshold > 1) {
    throw new Error(`Exporter material alpha clip threshold is outside [0,1] on ${owner}.`);
  }
  const readOptionalColor = (entry: unknown, field: string): [number, number, number, number] | undefined => {
    if (!isRecord(entry) || entry[field] === undefined) return undefined;
    const color = entry[field];
    if (!isRecord(color) || !Number.isFinite(color.r) || !Number.isFinite(color.g) ||
        !Number.isFinite(color.b) || !Number.isFinite(color.a)) {
      throw new Error(`Malformed exporter material alpha ${field} metadata on ${owner}.`);
    }
    return [color.r, color.g, color.b, color.a];
  };
  const baseEntry = isRecord(value.base) ? value.base : undefined;
  const baseWeights = readOptionalColor(baseEntry, 'weights');
  const colorScale = readOptionalColor(baseEntry, 'colorScale');
  const factorWeights = readOptionalColor(value, 'factorWeights');
  return {
    baseChannel,
    factorChannel,
    ...(value.particleColorAlpha === true ? { particleColorAlpha: true } : {}),
    ...(baseWeights === undefined ? {} : { baseWeights }),
    ...(colorScale === undefined ? {} : { colorScale }),
    ...(factorWeights === undefined ? {} : { factorWeights }),
    clipEnabled,
    clipThreshold
  };
}

function readUnityMaterialShaderParameters(value: unknown, owner: string): UnityMaterialShaderParameters | undefined {
  if (value === undefined) return undefined;
  if (isRecord(value) &&
      value.schemaVersion === 'unity_particle_quarks_exporter.material.shader_parameters.v1' &&
      value.profile === 'custom.shadergraph.rockDissolve') {
    if (value.colorOperation !== 'rockDissolveVertexCustomDataLerp' ||
        value.alphaOperation !== 'rockDissolveClip') {
      throw new Error(`Malformed exporter RockDissolve shader parameters on ${owner}.`);
    }
    return {
      schemaVersion: 'unity_particle_quarks_exporter.material.shader_parameters.v1',
      profile: 'custom.shadergraph.rockDissolve',
      colorOperation: 'rockDissolveVertexCustomDataLerp',
      alphaOperation: 'rockDissolveClip'
    };
  }
  if (!isRecord(value) ||
      (value.schemaVersion !== 'unity_particle_quarks_exporter.material.shader_parameters.v1' &&
       value.schemaVersion !== 'unity_particle_quarks_exporter.material.shader_parameters.v2') ||
      value.profile !== 'custom.piloto.uberfxsg') {
    throw new Error(`Malformed exporter material shader parameters on ${owner}.`);
  }
  const color = (entry: unknown, field: string, fallback: [number, number, number, number]): [number, number, number, number] => {
    if (entry === undefined) return fallback;
    if (!isRecord(entry) || !Number.isFinite(entry.r) || !Number.isFinite(entry.g) ||
        !Number.isFinite(entry.b) || !Number.isFinite(entry.a)) {
      throw new Error(`Malformed exporter UberFXSG ${field} metadata on ${owner}.`);
    }
    return [entry.r, entry.g, entry.b, entry.a];
  };
  const number = (entry: unknown, field: string, fallback = 0): number => {
    if (entry === undefined) return fallback;
    return finiteUnityNumber(entry, `${owner}.shaderParameters.${field}`);
  };
  return {
    schemaVersion: value.schemaVersion,
    profile: 'custom.piloto.uberfxsg',
    useColorRamp: value.useColorRamp === true,
    useFresnel: value.useFresnel === true,
    useAlphaOverride: value.useAlphaOverride === true,
    useSoftAlpha: value.useSoftAlpha === true,
    emissionMode: value.emissionMode === 'baseColorAdditive' ? 'baseColorAdditive' : 'none',
    emissionScale: number(value.emissionScale, 'EmissionScale', 1),
    colorOperation: value.colorOperation === 'channelPickerSaturation' ? 'channelPickerSaturation' : 'legacyScalar',
    alphaOperation: value.alphaOperation === 'channelPickerAdd' ? 'channelPickerAdd' : 'legacyChannel',
    mainTextureChannel: color(value.MainTextureChannel, 'MainTextureChannel', [1, 1, 1, 0]),
    mainAlphaChannel: color(value.MainAlphaChannel, 'MainAlphaChannel', [0, 0, 0, 1]),
    alphaOverrideChannel: color(value.AlphaOverrideChannel, 'AlphaOverrideChannel', [0, 0, 0, 1]),
    lastColor: color(value.LastColor, 'LastColor', [0, 0, 0, 0]),
    midColor: color(value.MidColor, 'MidColor', [0.5, 0.5, 0.5, 0]),
    whiteColor: color(value.WhiteColor, 'WhiteColor', [1, 1, 1, 0]),
    fresnelColor: color(value.FresnelColor, 'FresnelColor', [1, 1, 1, 0]),
    fresnelScale: number(value.FresnelScale, 'FresnelScale', 1),
    fresnelPower: number(value.FresnelPower, 'FresnelPower', 1),
    desaturate: number(value.Desaturate, 'Desaturate', 0),
    middlePointPos: number(value.MiddlePointPos, 'MiddlePointPos', 0.5),
    middlePointPos1: number(value.MiddlePointPos1, 'MiddlePointPos1', 0.5),
    fresnelBlend: number(value.FresnelBlend, 'FresnelBlend', 0)
  };
}

function readUnityMaterialBlendMetadata(value: unknown, owner: string): UnityMaterialBlendMetadata | undefined {
  if (value === undefined) return undefined;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.material.blend.v1') {
    throw new Error(`Malformed exporter material blend metadata on ${owner}.`);
  }
  const number = (field: string) => finiteUnityNumber(value[field], `${owner}.materialBlend.${field}`);
  return {
    mode: typeof value.mode === 'string' ? value.mode : 'normal',
    src: number('src'),
    dst: number('dst'),
    equation: number('equation'),
    srcAlpha: number('srcAlpha'),
    dstAlpha: number('dstAlpha'),
    equationAlpha: number('equationAlpha'),
    customAlpha: value.customAlpha === true,
    premultiplied: value.premultiplied === true,
    zWrite: value.zWrite !== false
  };
}

function readUnityMaterialTextureUvMetadata(value: unknown, owner: string): UnityMaterialTextureUvMetadata | undefined {
  if (value === undefined) return undefined;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.material_texture_uv.v1') {
    throw new Error(`Malformed exporter material texture UV metadata on ${owner}.`);
  }

  const readEntry = (entry: unknown, role: string): UnityMaterialTextureUvEntry | undefined => {
    if (entry === undefined) return undefined;
    if (!isRecord(entry) || typeof entry.property !== 'string' || entry.property.length === 0) {
      throw new Error(`Malformed exporter ${role} texture UV metadata on ${owner}.`);
    }
    const scale = readFiniteTuple2(entry.scale, `${owner}.materialTextureUv.${role}.scale`);
    const offset = readFiniteTuple2(entry.offset, `${owner}.materialTextureUv.${role}.offset`);
    const panning = readFiniteTuple2(entry.panning, `${owner}.materialTextureUv.${role}.panning`);
    return { property: entry.property, scale, offset, panning };
  };
  const main = readEntry(value.main, 'main');
  const alpha = readEntry(value.alpha, 'alpha');
  if (main === undefined && alpha === undefined) return undefined;
  return {
    ...(main === undefined ? {} : { main }),
    ...(alpha === undefined ? {} : { alpha })
  };
}

function validateUnityMaterialProfileMetadata(emitter: Object3D): void {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.materialProfile === undefined) return;
  const value = exporterData.materialProfile;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.material.profile.v1' ||
      typeof value.profileId !== 'string' || !supportedUnityMaterialProfiles.has(value.profileId) ||
      (value.profileVersion !== 'v1' && value.profileVersion !== 'v2') ||
      (value.runtimeTier !== 'stock' && value.runtimeTier !== 'paired') ||
      (value.fidelity !== 'exact' && value.fidelity !== 'approx')) {
    throw new Error(`Malformed exporter material profile metadata on ${emitter.uuid}.`);
  }
  if (typeof value.sourceShader !== 'string' || value.sourceShader.length === 0) {
    throw new Error(`Exporter material profile metadata is missing sourceShader on ${emitter.uuid}.`);
  }
}

function applyUnityMaterialState(system: ParticleSystem, metadata: UnityMaterialMetadata): void {
  const material = system.rendererSettings.material as Material & {
    alphaTest?: number;
    transparent?: boolean;
    depthWrite?: boolean;
    blending?: number;
    blendSrc?: number;
    blendDst?: number;
    blendEquation?: number;
    blendSrcAlpha?: number | null;
    blendDstAlpha?: number | null;
    blendEquationAlpha?: number | null;
    premultipliedAlpha?: boolean;
  };
  if (!material) throw new Error('Exporter material metadata requires a Quarks material.');
  applyUnityMaterialStateToMaterial(material as unknown as Material, metadata);
  // Unity stretched billboards can keep an opaque source material while the
  // ParticleSystem color still drives alpha. Three's opaque batch path writes
  // those fragments as solid pixels, so opt this narrow paired path into
  // alpha blending while preserving the authored depth-write setting.
  if (metadata.alpha?.particleColorAlpha === true &&
      system.renderMode === RenderMode.StretchedBillBoard) {
    material.transparent = true;
    material.needsUpdate = true;
  }
}

function applyUnityMaterialStateToMaterial(material: Material, metadata?: UnityMaterialMetadata): void {
  if (!metadata) return;
  const state = material as unknown as Record<string, unknown>;
  if (metadata.alpha) {
    if (metadata.alpha.clipEnabled) state.alphaTest = metadata.alpha.clipThreshold;
  }
  if (metadata.blend) {
    state.blendSrc = metadata.blend.src;
    state.blendDst = metadata.blend.dst;
    state.blendEquation = metadata.blend.equation;
    if (metadata.blend.customAlpha) {
      state.blendSrcAlpha = metadata.blend.srcAlpha;
      state.blendDstAlpha = metadata.blend.dstAlpha;
      state.blendEquationAlpha = metadata.blend.equationAlpha;
    }
    state.premultipliedAlpha = metadata.blend.premultiplied;
    state.depthWrite = metadata.blend.zWrite;
    // Keep transparency authored by Three's ObjectLoader for normal Unity
    // alpha materials, including the valid normal + depth-write combination.
    state.transparent = Boolean(state.transparent) ||
      metadata.blend.mode !== 'normal' || !metadata.blend.zWrite;
  }
  if (metadata.fragmentColorMode === 'invisibleFallback') {
    state.opacity = 0;
    state.transparent = true;
    state.depthWrite = false;
  }
  material.needsUpdate = true;
}

function unityMainBatchRendererPivot(system: ParticleSystem): UnityRendererPivotMetadata | undefined {
  const metadata = unityRendererPivotMetadata.get(system);
  if (!metadata) return undefined;
  const hasCompanionHead = readUnityParticleHeadMetadata(system.emitter as unknown as Object3D) !== null;
  return hasCompanionHead && system.renderMode === RenderMode.Trail ? undefined : metadata;
}

function configureUnityMaterialBatch(renderer: BatchedRenderer, system: ParticleSystem): void {
  const metadata = unityMaterialMetadata.get(system);
  const colorMetadata = unityColorSemanticsMetadata.get(system);
  const textureSheet = unityTextureSheetMetadata.get(system);
  const spriteSheet = textureSheet?.mode === 'sprites' ? textureSheet : undefined;
  const linearColorPatch = requiresUnityLinearColorShader(system, colorMetadata);
  const alphaMaps = unityMaterialAlphaMaps(system, metadata);
  const rendererPivot = unityMainBatchRendererPivot(system);
  const customData = unityCustomDataMetadata.get(system);
  const stretchedBillboard = system.renderMode === RenderMode.StretchedBillBoard;
  if (!requiresUnityMaterialBatchCustomization(
    metadata,
    spriteSheet,
    linearColorPatch,
    alphaMaps.length > 0,
    rendererPivot,
    customData
  ) && !stretchedBillboard) return;
  const batchIndex = renderer.systemToBatchIndex.get(system);
  const batch = batchIndex === undefined ? undefined : renderer.batches[batchIndex] as unknown as {
    settings?: { material?: Material & { type: string } };
    material?: Material & {
      vertexShader?: string;
      fragmentShader?: string;
      uniforms?: Record<string, { value: unknown }>;
      userData: Record<string, unknown>;
    };
  };
  const material = batch?.material;
  if (!material || typeof material.vertexShader !== 'string' || typeof material.fragmentShader !== 'string') {
    throw new Error('Exporter material or sprite metadata requires a Quarks shader batch.');
  }
  patchUnityStretchedBillboardShader(material, metadata?.profileId === 'custom.vehicle.effect');
  applyUnityMaterialStateToMaterial(material as unknown as Material, metadata);
  const signature = unityMaterialBatchType(
    metadata,
    spriteSheet,
    linearColorPatch,
    alphaMaps,
    rendererPivot,
    customData
  );
  if (batch.settings?.material) batch.settings.material.type = signature;
  const existing = material.userData.unityParticleQuarksBatchSignature;
  if (existing !== undefined) {
    if (existing !== signature) {
      throw new Error(`Quarks batch mixes incompatible exporter material profiles ${String(existing)} and ${signature}.`);
    }
    return;
  }

  if (rendererPivot) configureUnityRendererPivotShader(material, rendererPivot);
  if (customData) configureUnityCustomDataBatch(batch, material);
  if (spriteSheet) configureUnitySpriteSheetShader(material, spriteSheet);
  if (linearColorPatch) configureUnityLinearColorShader(material);
  if (alphaMaps.length > 0) configureUnityAlphaMapShader(material, alphaMaps, metadata?.textureUv?.alpha);
  if (metadata?.cameraFade) configureUnityCameraFadeShader(material, metadata.cameraFade);
  if (metadata?.textureUv?.main || (metadata?.alpha && metadata.alpha.baseChannel !== 'a') ||
      metadata?.baseColorChannel !== undefined && metadata.baseColorChannel !== 'rgb' ||
      metadata?.shaderParameters) {
    configureUnityBaseTextureShader(
      material,
      metadata?.alpha?.baseChannel ?? 'a',
      metadata?.textureUv?.main,
      metadata?.alpha?.baseWeights,
      metadata?.alpha?.colorScale,
      metadata?.shaderParameters,
      metadata?.baseColorChannel
    );
  }
  if (metadata && metadata.fragmentColorMode !== 'stock') {
    const transform = unityFragmentColorTransform(metadata.fragmentColorMode);
    const stockMarker = '    #include <alphatest_fragment>';
    const trailMarker = '    if( diffuseColor.a < alphaTest ) discard;';
    if (material.fragmentShader.includes(stockMarker)) {
      material.fragmentShader = material.fragmentShader.replace(stockMarker, `${transform}\n${stockMarker}`);
    } else if (material.fragmentShader.includes(trailMarker)) {
      material.fragmentShader = material.fragmentShader.replace(trailMarker, `${transform}\n${trailMarker}`);
    } else {
      throw new Error(`Quarks shader batch has no supported fragment-color insertion point for ${metadata.fragmentColorMode}.`);
    }
    material.userData.unityParticleQuarksFragmentColorMode = metadata.fragmentColorMode;
  }
  material.userData.unityParticleQuarksBatchSignature = signature;
  material.needsUpdate = true;
}

function configureUnityBaseTextureShader(
  material: {
    fragmentShader?: string;
    uniforms?: Record<string, { value: unknown }>;
    userData: Record<string, unknown>;
    needsUpdate?: boolean;
  },
  channel: UnityMaterialAlphaMetadata['baseChannel'],
  uvMetadata?: UnityMaterialTextureUvEntry,
  alphaWeights?: [number, number, number, number],
  colorScale?: [number, number, number, number],
  shaderParameters?: UnityMaterialShaderParameters,
  baseColorChannel: UnityMaterialBaseColorChannel = 'rgb'
): void {
  if (typeof material.fragmentShader !== 'string' || !material.uniforms) {
    throw new Error('Exporter base texture semantics require a Quarks shader batch.');
  }
  const sourceUv = material.fragmentShader.includes('#include <map_fragment>') ? 'vMapUv' : 'vUv';
  const uv = configureUnityTextureUvUniforms(material, 'Main', uvMetadata, sourceUv);
  const tileMarker = '#include <tile_fragment>';
  const mapMarker = '#include <map_fragment>';
  const rockDissolve = shaderParameters?.profile === 'custom.shadergraph.rockDissolve';
  if (material.fragmentShader.includes(tileMarker)) {
    const tileSample = [
      '#ifdef USE_MAP',
      `    vec4 texelColor = texture2D( map, ${uv} );`,
      '    #ifdef TILE_BLEND',
      `        texelColor = mix( texelColor, texture2D( map, ${uv.replace(sourceUv, sourceUv === 'vUv' ? 'vUvNext' : 'vMapUvNext')} ), vUvBlend );`,
      '    #endif',
      ...(rockDissolve
        ? unityRockDissolveFragmentLines('texelColor')
        : [
            `    diffuseColor.rgb *= ${unityUberFxsgColorExpression('texelColor', colorScale, shaderParameters, baseColorChannel)};`,
            `    diffuseColor.a *= ${unityUberFxsgAlphaExpression('texelColor', channel, alphaWeights, shaderParameters)};`
          ]),
      '#endif'
    ].join('\n');
    material.fragmentShader = material.fragmentShader.replace(tileMarker, tileSample);
  } else if (material.fragmentShader.includes(mapMarker)) {
    const mapSample = [
      '#ifdef USE_MAP',
      `    vec4 sampledDiffuseColor = texture2D( map, ${uv} );`,
      '    #ifdef DECODE_VIDEO_TEXTURE',
      '        sampledDiffuseColor = sRGBTransferEOTF( sampledDiffuseColor );',
      '    #endif',
      ...(rockDissolve
        ? unityRockDissolveFragmentLines('sampledDiffuseColor')
        : [
            `    diffuseColor.rgb *= ${unityUberFxsgColorExpression('sampledDiffuseColor', colorScale, shaderParameters, baseColorChannel)};`,
            `    diffuseColor.a *= ${unityUberFxsgAlphaExpression('sampledDiffuseColor', channel, alphaWeights, shaderParameters)};`
          ]),
      '#endif'
    ].join('\n');
    material.fragmentShader = material.fragmentShader.replace(mapMarker, mapSample);
  } else {
    throw new Error(`Quarks shader batch has no supported base-texture insertion point for alpha channel ${channel}.`);
  }
  if (channel !== 'a') material.userData.unityParticleQuarksAlphaChannel = channel;
  if (rockDissolve) material.userData.unityParticleQuarksRockDissolve = true;
  material.needsUpdate = true;
}

function unityRockDissolveFragmentLines(sampleName: string): string[] {
  return [
    `    float unityParticleQuarksRockAlpha = clamp(${sampleName}.a - clamp(unityParticleQuarksCustom1Varying.x - ${sampleName}.g, 0.0, 1.0), 0.0, 1.0);`,
    '    if (unityParticleQuarksRockAlpha < clamp(vColor.a, 0.0, 1.0)) discard;',
    `    float unityParticleQuarksRockColorBlend = clamp(unityParticleQuarksCustom2Varying.a * ${sampleName}.b, 0.0, 1.0);`,
    `    diffuseColor.rgb = mix(vColor.rgb * ${sampleName}.r, unityParticleQuarksCustom2Varying.rgb * ${sampleName}.r, unityParticleQuarksRockColorBlend);`,
    '    diffuseColor.a = unityParticleQuarksRockAlpha;'
  ];
}

function configureUnityRendererPivotShader(
  material: {
    vertexShader?: string;
    uniforms?: Record<string, { value: unknown }>;
    userData: Record<string, unknown>;
    needsUpdate?: boolean;
  },
  metadata: UnityRendererPivotMetadata
): void {
  if (typeof material.vertexShader !== 'string' || !material.uniforms) {
    throw new Error('Exporter renderer pivot requires a Quarks shader batch.');
  }
  if (material.userData.unityParticleQuarksRendererPivot !== undefined) return;
  const declaration = 'uniform vec3 unityParticleQuarksRendererPivot;';
  let shader = material.vertexShader;
  if (!shader.includes('void main() {')) {
    throw new Error('Quarks shader has no vertex main insertion point for renderer pivot.');
  }
  shader = shader.replace('void main() {', `${declaration}\nvoid main() {`);
  const before = shader;
  shader = shader.replace(
    'vec2 alignedPosition = position.xy * size.xy;',
    'vec2 alignedPosition = (position.xy + unityParticleQuarksRendererPivot.xy) * size.xy;'
  );
  shader = shader.replace(
    /matrix \* vec4\(\s*position\s*,\s*1\.0\s*\)/g,
    'matrix * vec4( position + unityParticleQuarksRendererPivot, 1.0 )'
  );
  shader = shader.replace(
    '#include <begin_vertex>',
    '#include <begin_vertex>\n\ttransformed += unityParticleQuarksRendererPivot;'
  );
  shader = shader.replace(
    'vec3 scaledPos = vec3(position.xy * size.xy, position.z);',
    'vec3 scaledPos = vec3((position.xy + unityParticleQuarksRendererPivot.xy) * size.xy, position.z + unityParticleQuarksRendererPivot.z);'
  );
  shader = shader.replace(/position\.y \* normalize/g, '(position.y + unityParticleQuarksRendererPivot.y) * normalize');
  shader = shader.replace(/\(position\.x \+ 0\.5\) \* viewVelocity/g, '(position.x + unityParticleQuarksRendererPivot.x + 0.5) * viewVelocity');
  shader = shader.replace(
    'vec3(position.x * avgSize, position.y * avgSize, 0.0)',
    'vec3((position.x + unityParticleQuarksRendererPivot.x) * avgSize, (position.y + unityParticleQuarksRendererPivot.y) * avgSize, unityParticleQuarksRendererPivot.z)'
  );
  if (shader === before) {
    throw new Error('Quarks shader batch has no supported renderer-pivot transform point.');
  }
  material.vertexShader = shader;
  material.uniforms.unityParticleQuarksRendererPivot = {
    value: new Vector3(...metadata.geometryOffset)
  };
  material.userData.unityParticleQuarksRendererPivot = [...metadata.value];
  material.userData.unityParticleQuarksRendererPivotGeometryOffset = [...metadata.geometryOffset];
  material.needsUpdate = true;
}

function configureUnityCustomDataBatch(
  batchValue: unknown,
  material: {
    vertexShader?: string;
    fragmentShader?: string;
    userData: Record<string, unknown>;
    needsUpdate?: boolean;
  }
): void {
  const batch = batchValue as {
    geometry: {
      getAttribute(name: string): { count: number } | undefined;
      setAttribute(name: string, value: InstancedBufferAttribute): unknown;
    };
    getVisibleSystems(): ParticleSystem[];
    update(): void;
    __unityParticleQuarksCustomDataUpdateInstalled?: boolean;
  };
  if (!batch?.geometry || typeof batch.getVisibleSystems !== 'function' || typeof batch.update !== 'function' ||
      typeof material.vertexShader !== 'string' || typeof material.fragmentShader !== 'string') {
    throw new Error('Exporter Custom Data requires a Quarks SpriteBatch.');
  }
  if (batch.__unityParticleQuarksCustomDataUpdateInstalled) return;

  const varyingDeclarations = [
    'attribute vec4 unityParticleQuarksCustom1;',
    'attribute vec4 unityParticleQuarksCustom2;',
    'varying vec4 unityParticleQuarksCustom1Varying;',
    'varying vec4 unityParticleQuarksCustom2Varying;'
  ].join('\n');
  material.vertexShader = material.vertexShader.replace(
    'void main() {',
    `${varyingDeclarations}\nvoid main() {\n    unityParticleQuarksCustom1Varying = unityParticleQuarksCustom1;\n    unityParticleQuarksCustom2Varying = unityParticleQuarksCustom2;`
  );
  material.fragmentShader = material.fragmentShader.replace(
    'void main() {',
    'varying vec4 unityParticleQuarksCustom1Varying;\nvarying vec4 unityParticleQuarksCustom2Varying;\nvoid main() {'
  );

  const originalUpdate = batch.update.bind(batch);
  batch.update = () => {
    originalUpdate();
    const capacity = Math.max(1, batch.geometry.getAttribute('offset')?.count ?? 1);
    const ensureAttribute = (name: string): InstancedBufferAttribute => {
      const current = batch.geometry.getAttribute(name) as InstancedBufferAttribute | undefined;
      if (current && current.count >= capacity) return current;
      const attribute = new InstancedBufferAttribute(new Float32Array(capacity * 4), 4);
      attribute.setUsage(DynamicDrawUsage);
      batch.geometry.setAttribute(name, attribute);
      return attribute;
    };
    const custom1Attribute = ensureAttribute('unityParticleQuarksCustom1');
    const custom2Attribute = ensureAttribute('unityParticleQuarksCustom2');
    let index = 0;
    for (const system of batch.getVisibleSystems()) {
      for (let particleIndex = 0; particleIndex < system.particleNum; particleIndex += 1, index += 1) {
        const particle = system.particles[particleIndex] as UnityCustomDataParticle | undefined;
        const custom1 = particle?.__unityParticleQuarksCustom1;
        const custom2 = particle?.__unityParticleQuarksCustom2;
        custom1Attribute.setXYZW(index, custom1?.x ?? 0, custom1?.y ?? 0, custom1?.z ?? 0, custom1?.w ?? 0);
        custom2Attribute.setXYZW(index, custom2?.x ?? 0, custom2?.y ?? 0, custom2?.z ?? 0, custom2?.w ?? 0);
      }
    }
    for (const attribute of [custom1Attribute, custom2Attribute]) {
      attribute.clearUpdateRanges();
      if (index > 0) attribute.addUpdateRange(0, index * 4);
      attribute.needsUpdate = index > 0;
    }
  };
  batch.__unityParticleQuarksCustomDataUpdateInstalled = true;
  material.userData.unityParticleQuarksCustomData = true;
  material.needsUpdate = true;
}

function configureUnityTextureUvUniforms(
  material: {
    fragmentShader?: string;
    uniforms?: Record<string, { value: unknown }>;
  },
  role: 'Main' | 'Alpha',
  metadata: UnityMaterialTextureUvEntry | undefined,
  sourceUv: string
): string {
  if (!metadata) return sourceUv;
  if (!material.fragmentShader || !material.uniforms) {
    throw new Error(`Exporter ${role.toLowerCase()} texture UV semantics require a Quarks shader batch.`);
  }
  const transformName = `unityParticleQuarks${role}UvTransform`;
  const panningName = `unityParticleQuarks${role}UvPanning`;
  const timeName = 'unityParticleQuarksTime';
  material.uniforms[transformName] = {
    value: new Vector4(metadata.scale[0], metadata.scale[1], metadata.offset[0], metadata.offset[1])
  };
  material.uniforms[panningName] = { value: new Vector2(metadata.panning[0], metadata.panning[1]) };
  material.uniforms[timeName] ??= { value: 0 };
  const declarations = [
    `uniform vec4 ${transformName};`,
    `uniform vec2 ${panningName};`,
    `uniform float ${timeName};`
  ].filter((declaration) => !material.fragmentShader!.includes(declaration)).join('\n');
  if (declarations.length > 0) {
    if (!material.fragmentShader.includes('void main() {')) {
      throw new Error(`Quarks shader has no fragment main insertion point for ${role.toLowerCase()} texture UV semantics.`);
    }
    material.fragmentShader = material.fragmentShader.replace('void main() {', `${declarations}\nvoid main() {`);
  }
  return `( ${sourceUv} * ${transformName}.xy + ${transformName}.zw + ${panningName} * ${timeName} )`;
}

function prepareUnityMaterialBatch(system: ParticleSystem): void {
  const metadata = unityMaterialMetadata.get(system);
  const colorMetadata = unityColorSemanticsMetadata.get(system);
  const textureSheet = unityTextureSheetMetadata.get(system);
  const spriteSheet = textureSheet?.mode === 'sprites' ? textureSheet : undefined;
  const linearColorPatch = requiresUnityLinearColorShader(system, colorMetadata);
  const alphaMaps = unityMaterialAlphaMaps(system, metadata);
  const rendererPivot = unityMainBatchRendererPivot(system);
  const customData = unityCustomDataMetadata.get(system);
  if (!requiresUnityMaterialBatchCustomization(
    metadata,
    spriteSheet,
    linearColorPatch,
    alphaMaps.length > 0,
    rendererPivot,
    customData
  )) return;
  const source = system.rendererSettings.material;
  const material = source.clone();
  (material as Material & { type: string }).type = unityMaterialBatchType(
    metadata,
    spriteSheet,
    linearColorPatch,
    alphaMaps,
    rendererPivot,
    customData
  );
  system.rendererSettings.material = material;
}

function repairUnityAlphaMeshCulling(system: ParticleSystem): void {
  const rendererSettings = (system as unknown as {
    rendererSettings?: {
      renderMode?: number;
      material?: Material & { alphaTest?: number; map?: unknown; side?: number };
    };
  }).rendererSettings;
  const material = rendererSettings?.material;
  // Unity alpha-clipped Mesh particle planes can arrive with a handedness
  // transform that reverses their winding. Keep the texture and cutoff exact
  // while making this narrow paired path visible from either side.
  if (rendererSettings?.renderMode !== 2 || !material ||
      !(material.alphaTest && material.alphaTest > 0) || !material.map ||
      material.side === DoubleSide) return;
  material.side = DoubleSide;
  material.needsUpdate = true;
}

function requiresUnityMaterialBatchCustomization(
  metadata?: UnityMaterialMetadata,
  spriteSheet?: UnityTextureSheetAnimationMetadata,
  linearColorPatch = false,
  alphaMap = false,
  rendererPivot?: UnityRendererPivotMetadata,
  customData?: UnityCustomDataMetadata
): boolean {
  return Boolean(
    spriteSheet ||
    rendererPivot ||
    customData ||
    metadata?.cameraFade ||
    linearColorPatch ||
    alphaMap ||
    (metadata && metadata.fragmentColorMode !== 'stock') ||
    metadata?.blend?.customAlpha ||
    metadata?.alpha?.baseChannel !== undefined && metadata.alpha.baseChannel !== 'a' ||
    metadata?.baseColorChannel !== undefined && metadata.baseColorChannel !== 'rgb' ||
    metadata?.textureUv !== undefined ||
    metadata?.shaderParameters !== undefined
    || metadata?.profileId === 'custom.vehicle.effect'
  );
}

function unityUberFxsgColorExpression(
  sampleName: string,
  colorScale?: [number, number, number, number],
  shaderParameters?: UnityMaterialShaderParameters,
  baseColorChannel: UnityMaterialBaseColorChannel = 'rgb'
): string {
  if (shaderParameters?.profile === 'custom.shadergraph.rockDissolve') {
    return `${sampleName}.rgb`;
  }
  if (!shaderParameters && baseColorChannel !== 'rgb') {
    const channel = baseColorChannel === 'a' ? 'a' : baseColorChannel;
    const sampled = `vec3(${sampleName}.${channel})`;
    const values = (colorScale ?? [1, 1, 1, 0]).slice(0, 3);
    return values.every((value) => value === 1) ? sampled : `${sampled} * vec3(${values.join(', ')})`;
  }
  const scale = (colorScale ?? [1, 1, 1, 0]).slice(0, 3).join(', ');
  if (!shaderParameters) {
    const values = (colorScale ?? [1, 1, 1, 0]).slice(0, 3);
    return values.every((value) => value === 1) ? `${sampleName}.rgb` : `${sampleName}.rgb * vec3(${scale})`;
  }
  if (shaderParameters.colorOperation !== 'channelPickerSaturation') {
    return `${sampleName}.rgb * vec3(${scale})`;
  }
  const channel = shaderParameters.mainTextureChannel.slice(0, 3).join(', ');
  const selected = `( ${sampleName}.rgb * vec3(${channel}) )`;
  const desaturate = Math.min(1, Math.max(0, shaderParameters.desaturate));
  const saturation = `mix( vec3(dot(${selected}, vec3(0.2126, 0.7152, 0.0722))), ${selected}, ${(1 - desaturate).toFixed(6)} )`;
  let base = saturation;
  if (shaderParameters.useColorRamp) {
    const rampValue = `clamp(max(max(${base}.r, ${base}.g), ${base}.b), 0.0, 1.0)`;
    const low = Number.isFinite(shaderParameters.middlePointPos1) ? shaderParameters.middlePointPos1 : 0.5;
    const high = Number.isFinite(shaderParameters.middlePointPos) ? shaderParameters.middlePointPos : 0.5;
    const lowSpan = Math.max(0.0001, high - low);
    const highSpan = Math.max(0.0001, 1 - high);
    const lowT = `clamp((${rampValue} - ${low.toFixed(6)}) / ${lowSpan.toFixed(6)}, 0.0, 1.0)`;
    const highT = `clamp((${rampValue} - ${high.toFixed(6)}) / ${highSpan.toFixed(6)}, 0.0, 1.0)`;
    const last = shaderParameters.lastColor.slice(0, 3).join(', ');
    const mid = shaderParameters.midColor.slice(0, 3).join(', ');
    const white = shaderParameters.whiteColor.slice(0, 3).join(', ');
    base = `( ${rampValue} < ${high.toFixed(6)} ? mix( vec3(${last}), vec3(${mid}), ${lowT} ) : mix( vec3(${mid}), vec3(${white}), ${highT} ) )`;
  }
  if (shaderParameters.useFresnel) {
    const fresnelColor = shaderParameters.fresnelColor.slice(0, 3).join(', ');
    const power = Math.max(0.0001, shaderParameters.fresnelPower);
    const edge = `pow(clamp(max(abs(vUv.x * 2.0 - 1.0), abs(vUv.y * 2.0 - 1.0)), 0.0, 1.0), ${power.toFixed(6)})`;
    base = `mix( ${base}, vec3(${fresnelColor}), clamp(${edge} * ${Math.max(0, shaderParameters.fresnelScale).toFixed(6)}, 0.0, 1.0) )`;
  }
  const emissionScale = shaderParameters.emissionMode === 'baseColorAdditive'
    ? 1 + Math.max(0, shaderParameters.emissionScale)
    : 1;
  return `( ${base} * ${emissionScale.toFixed(6)} )`;
}

function unityUberFxsgAlphaExpression(
  sampleName: string,
  channel: UnityMaterialAlphaMetadata['baseChannel'],
  alphaWeights?: [number, number, number, number],
  shaderParameters?: UnityMaterialShaderParameters
): string {
  if (shaderParameters?.profile === 'custom.shadergraph.rockDissolve') {
    return `${sampleName}.b`;
  }
  if (shaderParameters?.alphaOperation === 'channelPickerAdd') {
    // The exporter resolves the effective base alpha channel separately from
    // the raw graph picker. This matters when _AlphaOverride samples the same
    // texture: Unity's material alpha semantic already points at that channel
    // while MainAlphaChannel may still describe the graph's unmodified input.
    const weights = (alphaWeights ?? shaderParameters.mainAlphaChannel).join(', ');
    return `clamp(dot(${sampleName}, vec4(${weights})), 0.0, 1.0)`;
  }
  return alphaWeights
    ? `clamp(dot(${sampleName}, vec4(${alphaWeights.join(', ')})), 0.0, 1.0)`
    : `${sampleName}.${channel}`;
}

function requiresUnityLinearColorShader(
  system: ParticleSystem,
  metadata?: UnityColorSemanticsMetadata
): boolean {
  if (metadata?.projectColorSpace !== 'linear') return false;
  const rendererSettings = system.rendererSettings as unknown as {
    renderMode?: number;
    material?: Material & { type?: string };
  };
  const renderMode = rendererSettings?.renderMode;
  const materialType = rendererSettings?.material?.type;
  // Quarks' MeshStandard/MeshPhysical particle shaders already carry
  // colorspace_fragment. Billboard, trail, MeshBasic, and stretched paths do
  // not, so they need the paired patch below.
  return !(renderMode === 2 &&
    (materialType === 'MeshStandardMaterial' || materialType === 'MeshPhysicalMaterial'));
}

function configureUnityLinearColorShader(material: Material & {
  fragmentShader?: string;
  userData: Record<string, unknown>;
}): void {
  if (typeof material.fragmentShader !== 'string') {
    throw new Error('Linear exporter color semantics require a Quarks shader batch.');
  }
  if (!material.fragmentShader.includes('#include <colorspace_pars_fragment>')) {
    const commonMarker = '#include <common>';
    if (!material.fragmentShader.includes(commonMarker)) {
      throw new Error('Quarks shader has no colorspace declaration insertion point.');
    }
    material.fragmentShader = material.fragmentShader.replace(
      commonMarker,
      `${commonMarker}\n#include <colorspace_pars_fragment>`
    );
  }
  if (!material.fragmentShader.includes('#include <colorspace_fragment>')) {
    const toneMappingMarker = '#include <tonemapping_fragment>';
    if (!material.fragmentShader.includes(toneMappingMarker)) {
      throw new Error('Quarks shader has no output color-space insertion point.');
    }
    material.fragmentShader = material.fragmentShader.replace(
      toneMappingMarker,
      `${toneMappingMarker}\n#include <colorspace_fragment>`
    );
  }
  material.userData.unityParticleQuarksLinearColorSpace = true;
}

function patchUnityStretchedBillboardShader(material: {
  vertexShader?: string;
  userData: Record<string, unknown>;
  needsUpdate?: boolean;
}, allowVehicleLowSpeedStretch = false): void {
  if (typeof material.vertexShader !== 'string' ||
      !material.vertexShader.includes('attribute vec4 velocity') ||
      material.userData.unityParticleQuarksSafeStretchedBillboard === true) return;
  let shader = material.vertexShader;
  // Quarks writes a tiny compatibility velocity when Unity velocityScale is
  // zero. Vehicle Add_Blend effects still need that vector's direction to
  // retain Unity's non-zero lengthScale base; other profiles must treat it as
  // zero so small noise velocities cannot become random light columns.
  const velocityThreshold = allowVehicleLowSpeedStretch ? '0.000000001' : '0.00001';
  shader = shader.replace(/if \(vlength > 0\.000000001\)/g, `if (vlength > ${velocityThreshold})`);
  shader = shader.replace(/if \(vlength > 0\.00001\)/g, `if (vlength > ${velocityThreshold})`);
  shader = shader.replace(
    /\s+float vlength = length\(viewVelocity\);\s+vec3 projVelocity =\s+dot\(scaledPos, viewVelocity\) \* viewVelocity \/ vlength;\s+mvPosition\.xyz \+= scaledPos \+ projVelocity \* \(speedFactor \/ avgSize \+ lengthFactor \/ vlength\);/,
    `    float vlength = length(viewVelocity);
    if (vlength > ${velocityThreshold}) {
        vec3 projVelocity = dot(scaledPos, viewVelocity) * viewVelocity / vlength;
        mvPosition.xyz += scaledPos + projVelocity * (speedFactor / max(avgSize, 0.00001) + lengthFactor / vlength);
    } else {
        // A zero birth velocity is valid in Unity. Keep a stable billboard.
        mvPosition.xyz += scaledPos;
    }`
  );
  shader = shader.replace(
    /\s+float vlength = length\(viewVelocity\);\s+mvPosition\.xyz \+= position\.y \* normalize\(cross\(mvPosition\.xyz, viewVelocity\)\) \* avgSize;[^\n]*\n\s+mvPosition\.xyz -= \(position\.x \+ 0\.5\) \* viewVelocity \* \(1\.0 \+ lengthFactor \/ vlength\) \* avgSize;[^\n]*;/,
    `    float vlength = length(viewVelocity);
    if (vlength > ${velocityThreshold}) {
        mvPosition.xyz += position.y * normalize(cross(mvPosition.xyz, viewVelocity)) * avgSize;
        mvPosition.xyz -= (position.x + 0.5) * viewVelocity * (1.0 + lengthFactor / vlength) * avgSize;
    } else {
        // Avoid normalize/divide-by-zero producing random giant columns.
        mvPosition.xyz += vec3(position.x * avgSize, position.y * avgSize, 0.0);
    }`
  );
  if (shader === material.vertexShader) return;
  material.vertexShader = shader;
  material.userData.unityParticleQuarksSafeStretchedBillboard = true;
  material.needsUpdate = true;
}

function unityMaterialBatchType(
  metadata?: UnityMaterialMetadata,
  spriteSheet?: UnityTextureSheetAnimationMetadata,
  linearColorPatch = false,
  alphaMaps: Array<{ texture: unknown; uuid?: string; channel?: 'r' | 'g' | 'b' | 'a' }> = [],
  rendererPivot?: UnityRendererPivotMetadata,
  customData?: UnityCustomDataMetadata
): string {
  const camera = metadata?.cameraFade
    ? `${metadata.cameraFade.near},${metadata.cameraFade.far},${metadata.cameraFade.smoothness}`
    : 'none';
  const sprites = spriteSheet
    ? JSON.stringify(spriteSheet.sprites)
    : 'none';
  const alpha = alphaMaps.length === 0
    ? 'none'
    : alphaMaps.map((alphaMap) => `${alphaMap.uuid ?? 'none'}:${alphaMap.channel ?? 'r'}`).join('|');
  const baseAlpha = metadata?.alpha?.baseChannel ?? 'a';
  const baseWeights = metadata?.alpha?.baseWeights ?? [];
  const colorScale = metadata?.alpha?.colorScale ?? [];
  const textureUv = metadata?.textureUv === undefined ? 'none' : JSON.stringify(metadata.textureUv);
  const shaderParameters = metadata?.shaderParameters === undefined ? 'none' : JSON.stringify(metadata.shaderParameters);
  const baseColorChannel = metadata?.baseColorChannel ?? 'rgb';
  const pivot = rendererPivot === undefined
    ? 'none'
    : `${rendererPivot.value.join(',')}@${rendererPivot.geometryOffset.join(',')}`;
  const custom = customData === undefined ? 'none' : JSON.stringify(customData);
  return `UnityParticleQuarksExporterMaterial:${metadata?.profileId ?? 'none'}:${metadata?.fragmentColorMode ?? 'stock'}:${camera}:${sprites}:linear=${linearColorPatch}:baseAlpha=${baseAlpha}:baseColor=${baseColorChannel}:${baseWeights.join(',')}:${colorScale.join(',')}:uv=${textureUv}:alpha=${alpha}:shader=${shaderParameters}:pivot=${pivot}:custom=${custom}`;
}

function unityMaterialAlphaMaps(
  system: ParticleSystem,
  metadata?: UnityMaterialMetadata
): Array<{ texture: unknown; uuid?: string; channel?: 'r' | 'g' | 'b' | 'a'; weights?: [number, number, number, number] }> {
  const material = system.rendererSettings.material as Material & {
    alphaMap?: { uuid?: string };
    userData?: { unityParticleQuarksAlphaMaps?: Array<{ texture?: unknown; property?: string; channel?: 'r' | 'g' | 'b' | 'a' }> };
  };
  if (!material.alphaMap) return [];
  const maps: Array<{ texture: unknown; uuid?: string; channel?: 'r' | 'g' | 'b' | 'a'; weights?: [number, number, number, number] }> = [{
    texture: material.alphaMap,
    ...(material.alphaMap.uuid === undefined ? {} : { uuid: material.alphaMap.uuid }),
    channel: metadata?.alpha?.factorChannel ?? 'r',
    ...(metadata?.alpha?.factorWeights === undefined ? {} : { weights: metadata.alpha.factorWeights })
  }];
  for (const entry of material.userData?.unityParticleQuarksAlphaMaps ?? []) {
    if (!entry || !entry.texture || typeof entry.texture !== 'object') continue;
    const texture = entry.texture as { uuid?: string };
    maps.push({
      texture: entry.texture,
      ...(texture.uuid === undefined ? {} : { uuid: texture.uuid }),
      channel: entry.channel ?? 'r'
    });
  }
  return maps;
}

function configureUnityAlphaMapShader(
  material: {
    fragmentShader?: string;
    uniforms?: Record<string, { value: unknown }>;
    userData: Record<string, unknown>;
    needsUpdate?: boolean;
  },
  alphaMaps: Array<{ texture: unknown; uuid?: string; channel?: 'r' | 'g' | 'b' | 'a'; weights?: [number, number, number, number] }>,
  uvMetadata?: UnityMaterialTextureUvEntry
): void {
  if (typeof material.fragmentShader !== 'string' || !material.uniforms) {
    throw new Error('Exporter alphaMap requires a Quarks shader batch.');
  }
  const declarations = alphaMaps.map((_, index) =>
    `uniform sampler2D unityParticleQuarksAlphaMap${index === 0 ? '' : index};`
  ).filter((declaration) => !material.fragmentShader!.includes(declaration));
  alphaMaps.forEach((alphaMap, index) => {
    const name = `unityParticleQuarksAlphaMap${index === 0 ? '' : index}`;
    material.uniforms![name] = { value: alphaMap.texture };
  });
  if (declarations.length > 0) {
    if (!material.fragmentShader.includes('void main() {')) {
      throw new Error('Quarks shader has no fragment main insertion point for alphaMap.');
    }
    material.fragmentShader = material.fragmentShader.replace(
      'void main() {',
      `${declarations.join('\n')}\nvoid main() {`
    );
  }
  const sourceUv = material.fragmentShader.includes('#include <map_fragment>') ? 'vMapUv' : 'vUv';
  const uv = configureUnityTextureUvUniforms(material, 'Alpha', uvMetadata, sourceUv);
  const samples = alphaMaps.map((alphaMap, index) => {
    const name = `unityParticleQuarksAlphaMap${index === 0 ? '' : index}`;
    const channel = alphaMap.channel ?? 'r';
    return alphaMap.weights
      ? `    diffuseColor.a *= clamp(dot(texture2D( ${name}, ${uv} ), vec4(${alphaMap.weights.join(', ')})), 0.0, 1.0);`
      : `    diffuseColor.a *= texture2D( ${name}, ${uv} ).${channel};`;
  }).join('\n');
  if (!material.fragmentShader.includes(samples)) {
    const stockMarker = '    #include <alphatest_fragment>';
    const trailMarker = '    if( diffuseColor.a < alphaTest ) discard;';
    if (material.fragmentShader.includes(stockMarker)) {
      material.fragmentShader = material.fragmentShader.replace(stockMarker, `${samples}\n${stockMarker}`);
    } else if (material.fragmentShader.includes(trailMarker)) {
      material.fragmentShader = material.fragmentShader.replace(trailMarker, `${samples}\n${trailMarker}`);
    } else {
      throw new Error('Quarks shader has no alpha-test insertion point for alphaMap.');
    }
  }
  material.userData.unityParticleQuarksAlphaMap = alphaMaps.length === 1
    ? alphaMaps[0]?.uuid ?? true
    : alphaMaps.map((alphaMap) => alphaMap.uuid ?? true);
  material.needsUpdate = true;
}

function configureUnitySpriteSheetShader(
  material: Material & {
    vertexShader?: string;
    uniforms?: Record<string, { value: unknown }>;
    userData: Record<string, unknown>;
  },
  metadata: UnityTextureSheetAnimationMetadata
): void {
  if (typeof material.vertexShader !== 'string' || !material.uniforms || metadata.sprites.length === 0) {
    throw new Error('Exporter sprite-list animation requires a ShaderMaterial with sprite frames.');
  }
  const frameCount = metadata.sprites.length;
  const declarations = [
    `uniform vec4 unityParticleQuarksSpriteRects[${frameCount}];`,
    `uniform vec4 unityParticleQuarksSpriteGeometry[${frameCount}];`,
    'int unityParticleQuarksSpriteFrameIndex(float tile) {',
    `  return int(clamp(floor(tile), 0.0, ${Math.max(0, frameCount - 1)}.0));`,
    '}',
    'mat3 unityParticleQuarksSpriteTileTransform(float tile) {',
    '  vec4 rect = unityParticleQuarksSpriteRects[unityParticleQuarksSpriteFrameIndex(tile)];',
    '  return mat3(rect.z, 0.0, 0.0, 0.0, rect.w, 0.0, rect.x, rect.y, 1.0);',
    '}'
  ].join('\n');
  if (!material.vertexShader.includes('void main() {')) {
    throw new Error('Quarks sprite shader has no main insertion point.');
  }
  // Sprite-list animation still writes a per-particle uvTile even when its
  // authored atlas reports a 1x1 tile grid. Quarks normally omits UV_TILE in
  // that case, so opt this custom shader into the attribute path explicitly.
  material.defines = { ...(material.defines ?? {}), UV_TILE: '' };
  material.uniforms.tileCount ??= { value: new Vector2(metadata.tileCountX, metadata.tileCountY) };
  material.vertexShader = material.vertexShader.replace('void main() {', `${declarations}\nvoid main() {`);
  const replaceTileTransforms = (shaderSource: string): string => {
    const floorMarker = 'mat3 tileTransform = makeTileTransform(floor(uvTile));';
    const ceilMarker = 'mat3 nextTileTransform = makeTileTransform(ceil(uvTile));';
    if (shaderSource.includes('unityParticleQuarksSpriteTileTransform(floor(uvTile))')) return shaderSource;
    if (!shaderSource.includes(floorMarker)) {
      throw new Error('Quarks sprite shader has no UV tile insertion point.');
    }
    return shaderSource
      .replace(floorMarker, 'mat3 tileTransform = unityParticleQuarksSpriteTileTransform(floor(uvTile));')
      .replace(ceilMarker, 'mat3 nextTileTransform = unityParticleQuarksSpriteTileTransform(ceil(uvTile));');
  };
  if (!material.vertexShader.includes('#include <tile_vertex>') &&
      !material.vertexShader.includes('mat3 tileTransform = makeTileTransform(floor(uvTile));')) {
    throw new Error('Quarks sprite shader has no UV tile insertion point.');
  }
  const tileInclude = '#include <tile_vertex>';
  if (material.vertexShader.includes(tileInclude)) {
    const tileChunk = (ShaderChunk as Record<string, string>).tile_vertex;
    if (typeof tileChunk !== 'string') {
      throw new Error('Quarks sprite shader has no registered tile vertex chunk.');
    }
    material.vertexShader = material.vertexShader.replace(tileInclude, replaceTileTransforms(tileChunk));
  } else if (material.vertexShader.includes('mat3 tileTransform = makeTileTransform(floor(uvTile));')) {
    material.vertexShader = replaceTileTransforms(material.vertexShader);
  }
  const previousOnBeforeCompile = material.onBeforeCompile;
  material.onBeforeCompile = (shader, renderer: WebGLRenderer) => {
    previousOnBeforeCompile.call(material, shader, renderer);
    shader.vertexShader = replaceTileTransforms(shader.vertexShader);
  };

  const billboardMarker = '    vec2 alignedPosition = position.xy * size.xy;';
  const stretchedMarker = '    float avgSize = (size.x + size.y) * 0.5;';
  if (material.vertexShader.includes(billboardMarker)) {
    material.vertexShader = material.vertexShader.replace(billboardMarker, [
      '    vec4 unityParticleQuarksFrameGeometry = unityParticleQuarksSpriteGeometry[unityParticleQuarksSpriteFrameIndex(uvTile)];',
      '    vec2 unityParticleQuarksFrameSize = size.xy * unityParticleQuarksFrameGeometry.xy;',
      '    vec2 alignedPosition = position.xy * unityParticleQuarksFrameSize + unityParticleQuarksFrameSize * unityParticleQuarksFrameGeometry.zw;'
    ].join('\n'));
  } else if (material.vertexShader.includes(stretchedMarker)) {
    material.vertexShader = material.vertexShader.replace(stretchedMarker, [
      '    vec4 unityParticleQuarksFrameGeometry = unityParticleQuarksSpriteGeometry[unityParticleQuarksSpriteFrameIndex(uvTile)];',
      '    vec2 unityParticleQuarksFrameSize = size.xy * unityParticleQuarksFrameGeometry.xy;',
      '    vec2 unityParticleQuarksFramePosition = position.xy * unityParticleQuarksFrameSize + unityParticleQuarksFrameSize * unityParticleQuarksFrameGeometry.zw;',
      '    float avgSize = (unityParticleQuarksFrameSize.x + unityParticleQuarksFrameSize.y) * 0.5;'
    ].join('\n'));
    material.vertexShader = material.vertexShader.replace(
      'vec3 scaledPos = vec3(position.xy * size.xy, position.z);',
      'vec3 scaledPos = vec3(unityParticleQuarksFramePosition, position.z);'
    );
    material.vertexShader = material.vertexShader.replaceAll('position.y *', 'unityParticleQuarksFramePosition.y *');
    material.vertexShader = material.vertexShader.replaceAll('(position.x + 0.5) *', '(unityParticleQuarksFramePosition.x + 0.5) *');
  } else {
    throw new Error('Quarks sprite shader has no supported billboard geometry insertion point.');
  }

  material.uniforms.unityParticleQuarksSpriteRects = {
    value: metadata.sprites.map((frame) => new Vector4(...frame.rect))
  };
  material.uniforms.unityParticleQuarksSpriteGeometry = {
    value: metadata.sprites.map((frame) => new Vector4(
      frame.sizeMul[0],
      frame.sizeMul[1],
      frame.pivot[0],
      frame.pivot[1]
    ))
  };
  material.userData.unityParticleQuarksSpriteFrames = frameCount;
}

function configureUnityCameraFadeShader(
  material: Material & {
    vertexShader?: string;
    fragmentShader?: string;
    uniforms?: Record<string, { value: unknown }>;
    userData: Record<string, unknown>;
  },
  metadata: UnityCameraFadeMetadata
): void {
  if (typeof material.vertexShader !== 'string' || typeof material.fragmentShader !== 'string' || !material.uniforms) {
    throw new Error('Exporter camera fade requires a Quarks ShaderMaterial.');
  }
  const varying = 'varying float unityParticleQuarksCameraDistance;';
  material.vertexShader = material.vertexShader.replace('void main() {', `${varying}\nvoid main() {`);
  const positionMarker = '\tgl_Position = projectionMatrix * mvPosition;';
  const spacePositionMarker = '    gl_Position = projectionMatrix * mvPosition;';
  if (material.vertexShader.includes(positionMarker)) {
    material.vertexShader = material.vertexShader.replace(
      positionMarker,
      `    unityParticleQuarksCameraDistance = length(mvPosition.xyz);\n${positionMarker}`
    );
  } else if (material.vertexShader.includes(spacePositionMarker)) {
    material.vertexShader = material.vertexShader.replace(
      spacePositionMarker,
      `    unityParticleQuarksCameraDistance = length(mvPosition.xyz);\n${spacePositionMarker}`
    );
  } else {
    throw new Error('Quarks shader has no camera-distance vertex insertion point.');
  }

  const fragmentDeclarations = [
    varying,
    'uniform vec3 unityParticleQuarksCameraFade;',
    'float unityParticleQuarksCameraFadeFactor() {',
    '  float width = max(0.000001, unityParticleQuarksCameraFade.y - unityParticleQuarksCameraFade.x);',
    '  float linearFade = clamp((unityParticleQuarksCameraDistance - unityParticleQuarksCameraFade.x) / width, 0.0, 1.0);',
    '  return pow(linearFade, unityParticleQuarksCameraFade.z);',
    '}'
  ].join('\n');
  material.fragmentShader = material.fragmentShader.replace('void main() {', `${fragmentDeclarations}\nvoid main() {`);
  const stockMarker = '    #include <alphatest_fragment>';
  if (!material.fragmentShader.includes(stockMarker)) {
    throw new Error('Quarks shader has no camera-fade fragment insertion point.');
  }
  material.fragmentShader = material.fragmentShader.replace(
    stockMarker,
    `    diffuseColor.a *= unityParticleQuarksCameraFadeFactor();\n${stockMarker}`
  );
  material.uniforms.unityParticleQuarksCameraFade = {
    value: new Vector3(metadata.near, metadata.far, metadata.smoothness)
  };
  material.userData.unityParticleQuarksCameraFade = [metadata.near, metadata.far, metadata.smoothness];
}

function unityFragmentColorTransform(mode: UnityFragmentColorMode): string {
  switch (mode) {
    case 'stock':
      return '';
    case 'legacySoftAdditive':
      return '    diffuseColor.rgb *= diffuseColor.a;';
    case 'hovlAdditivePremultiply':
      // Hovl Add_CenterGlow uses Blend One[_Blend2] but premultiplies the
      // sampled texture/particle alpha into RGB in its Unity fragment.
      return '    diffuseColor.rgb *= diffuseColor.a;';
    case 'invisibleFallback':
      // GrabPass/screen-space shaders have no faithful stock Quarks path.
      // Clear the final fragment before alpha testing so best-effort review
      // cannot show an opaque placeholder quad.
      return '    diffuseColor = vec4(0.0);';
    case 'legacyAlphaPremultiply':
      return '    diffuseColor *= vColor.a;';
    case 'legacyMultiply':
      return '    diffuseColor = mix(vec4(1.0), diffuseColor, diffuseColor.a);';
    case 'legacyMultiplyDouble':
      return [
        '    vec4 unityParticleQuarksSourceColor = diffuseColor;',
        '    unityParticleQuarksSourceColor.rgb *= 2.0;',
        '    diffuseColor = mix(vec4(0.5), unityParticleQuarksSourceColor, unityParticleQuarksSourceColor.a);'
      ].join('\n');
  }
}

function readUnityStartColorMetadata(emitter: Object3D): UnityStartColorMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.startColorSemantics === undefined) return null;
  const value = exporterData.startColorSemantics;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.start_color.v1' ||
      (value.mode !== 'gradient' && value.mode !== 'twoGradients' && value.mode !== 'randomColor')) {
    throw new Error(`Malformed exporter Start Color metadata on ${emitter.uuid}.`);
  }
  if (value.mode === 'randomColor') {
    if (!isRecord(value.gradient)) {
      throw new Error(`Malformed exporter Random Start Color gradient on ${emitter.uuid}.`);
    }
    return { mode: 'randomColor', gradient: value.gradient };
  }
  return { mode: value.mode };
}

function applyUnityStartColorSemantics(system: ParticleSystem, metadata: UnityStartColorMetadata): void {
  if (metadata.mode === 'randomColor') {
    system.startColor = new UnityRandomGradientStartColor(QuarksGradient.fromJSON(metadata.gradient));
    return;
  }
  const source = system.startColor;
  if (source.type !== 'function') {
    // Unlit exporter profiles can legally keep a constant Quarks color because
    // the source shader does not consume particle color. Preserve that stock
    // value when paired normalization has no function generator to wrap.
    return;
  }
  system.startColor = new UnityNormalizedStartColor(source, system.duration);
}

function readUnityTrailInheritParticleColorMetadata(
  emitter: Object3D
): UnityTrailInheritParticleColorMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.trailInheritParticleColor === undefined) return null;
  const value = exporterData.trailInheritParticleColor;
  if (!isRecord(value) ||
      value.schemaVersion !== 'unity_particle_quarks_exporter.trail_inherit_particle_color.v1' ||
      !isRecord(value.particleColorOverLifetime)) {
    throw new Error(`Malformed exporter Trail inheritParticleColor metadata on ${emitter.uuid}.`);
  }
  return { particleColorOverLifetime: value.particleColorOverLifetime };
}

function readUnitySizeMetadata(emitter: Object3D): UnitySizeMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.sizeOverLifetime === undefined) return null;
  const value = exporterData.sizeOverLifetime;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.size_over_lifetime.v1' ||
      typeof value.separateAxes !== 'boolean') {
    throw new Error(`Malformed exporter Size over Lifetime metadata on ${emitter.uuid}.`);
  }
  if (value.separateAxes) {
    if (!isRecord(value.x) || !isRecord(value.y) || !isRecord(value.z)) {
      throw new Error(`Malformed exporter separate-axis Size over Lifetime metadata on ${emitter.uuid}.`);
    }
    return { separateAxes: true, x: value.x, y: value.y, z: value.z };
  }
  if (!isRecord(value.size)) {
    throw new Error(`Malformed exporter scalar Size over Lifetime metadata on ${emitter.uuid}.`);
  }
  return { separateAxes: false, size: value.size };
}

function readUnityMeshScalarRotationMetadata(emitter: Object3D): UnityMeshScalarRotationMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.meshScalarRotation === undefined) return null;
  const value = exporterData.meshScalarRotation;
  if (!isRecord(value) ||
      (value.schemaVersion !== 'unity_particle_quarks_exporter.mesh_scalar_rotation.v1' &&
       value.schemaVersion !== 'unity_particle_quarks_exporter.mesh_scalar_rotation.v2') ||
      (value.axisMode !== 'fixed' && value.axisMode !== 'position' &&
       value.axisMode !== 'velocity' && value.axisMode !== 'uniformXY')) {
    throw new Error(`Malformed exporter Mesh scalar-rotation metadata on ${emitter.uuid}.`);
  }
  const basisX = value.basisX === undefined
    ? [-1, 0, 0] as [number, number, number]
    : readFiniteTuple3(value.basisX, `${emitter.uuid}.meshScalarRotation.basisX`);
  const basisY = value.basisY === undefined
    ? [0, 1, 0] as [number, number, number]
    : readFiniteTuple3(value.basisY, `${emitter.uuid}.meshScalarRotation.basisY`);
  const basisZ = value.basisZ === undefined
    ? [0, 0, 1] as [number, number, number]
    : readFiniteTuple3(value.basisZ, `${emitter.uuid}.meshScalarRotation.basisZ`);
  const shapeOrigin = value.shapeOrigin === undefined
    ? [0, 0, 0] as [number, number, number]
    : readFiniteTuple3(value.shapeOrigin, `${emitter.uuid}.meshScalarRotation.shapeOrigin`);
  const shapeBasisX = value.shapeBasisX === undefined
    ? [1, 0, 0] as [number, number, number]
    : readFiniteTuple3(value.shapeBasisX, `${emitter.uuid}.meshScalarRotation.shapeBasisX`);
  const shapeBasisY = value.shapeBasisY === undefined
    ? [0, 1, 0] as [number, number, number]
    : readFiniteTuple3(value.shapeBasisY, `${emitter.uuid}.meshScalarRotation.shapeBasisY`);
  const shapeBasisZ = value.shapeBasisZ === undefined
    ? [0, 0, 1] as [number, number, number]
    : readFiniteTuple3(value.shapeBasisZ, `${emitter.uuid}.meshScalarRotation.shapeBasisZ`);
  if (value.axisMode === 'fixed') {
    const axis = readFiniteTuple3(value.axis, `${emitter.uuid}.meshScalarRotation.axis`);
    const length = Math.hypot(...axis);
    if (Math.abs(length - 1) > 1e-3) {
      throw new Error(`Exporter fixed Mesh scalar-rotation axis is not normalized on ${emitter.uuid}.`);
    }
    return { axisMode: 'fixed', axis, basisX, basisY, basisZ, shapeOrigin, shapeBasisX, shapeBasisY, shapeBasisZ };
  }
  if (value.axis !== undefined) {
    throw new Error(`Exporter derived Mesh scalar-rotation metadata has an unexpected fixed axis on ${emitter.uuid}.`);
  }
  return { axisMode: value.axisMode, basisX, basisY, basisZ, shapeOrigin, shapeBasisX, shapeBasisY, shapeBasisZ };
}

function readUnitySimulationSpeedMetadata(emitter: Object3D): UnitySimulationSpeedMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.simulationSpeed === undefined) return null;
  const value = exporterData.simulationSpeed;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.simulation_speed.v1') {
    throw new Error(`Malformed exporter simulationSpeed metadata on ${emitter.uuid}.`);
  }
  const speed = finiteUnityNumber(value.value, `${emitter.uuid}.simulationSpeed.value`);
  if (speed < 0) throw new Error(`Exporter simulationSpeed is negative on ${emitter.uuid}.`);
  if (speed > MAX_UNITY_SIMULATION_SPEED) {
    throw new Error(`Exporter simulationSpeed exceeds the runtime maximum of ${MAX_UNITY_SIMULATION_SPEED} on ${emitter.uuid}.`);
  }
  return { value: speed };
}

function readUnityStartDelayMetadata(emitter: Object3D): UnityStartDelayMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.startDelay === undefined) return null;
  const value = exporterData.startDelay;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.start_delay.v1' ||
      !Number.isInteger(value.randomSeed) || (value.randomSeed as number) < 0 ||
      (value.randomSeed as number) > 0xffffffff || !isRecord(value.delay)) {
    throw new Error(`Malformed exporter Start Delay metadata on ${emitter.uuid}.`);
  }
  return { randomSeed: value.randomSeed as number, delay: value.delay };
}

function readUnityLifetimeByEmitterSpeedMetadata(
  emitter: Object3D
): UnityLifetimeByEmitterSpeedMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.lifetimeByEmitterSpeed === undefined) return null;
  const value = exporterData.lifetimeByEmitterSpeed;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.lifetime_by_emitter_speed.v1' ||
      !Number.isInteger(value.randomSeed) || (value.randomSeed as number) < 0 ||
      (value.randomSeed as number) > 0xffffffff || !isRecord(value.curve)) {
    throw new Error(`Malformed exporter lifetimeByEmitterSpeed metadata on ${emitter.uuid}.`);
  }
  const range = readFiniteTuple2(value.range, `${emitter.uuid}.lifetimeByEmitterSpeed.range`);
  if (range[1] < range[0]) {
    throw new Error(`Exporter lifetimeByEmitterSpeed range is descending on ${emitter.uuid}.`);
  }
  return { randomSeed: value.randomSeed as number, range, curve: value.curve };
}

function readUnityMeshRotationBySpeedMetadata(
  emitter: Object3D
): UnityMeshRotationBySpeedMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.meshRotationBySpeed === undefined) return null;
  const value = exporterData.meshRotationBySpeed;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.mesh_rotation_by_speed.v1' ||
      (value.axisMode !== 'fixed' && value.axisMode !== 'position' &&
       value.axisMode !== 'velocity' && value.axisMode !== 'uniformXY') ||
      !isRecord(value.angularVelocity)) {
    throw new Error(`Malformed exporter Mesh rotation-by-speed metadata on ${emitter.uuid}.`);
  }
  const speedRange = readFiniteTuple2(value.speedRange, `${emitter.uuid}.meshRotationBySpeed.speedRange`);
  if (speedRange[1] < speedRange[0]) {
    throw new Error(`Exporter Mesh rotation-by-speed range is descending on ${emitter.uuid}.`);
  }
  const basisX = readFiniteTuple3(value.basisX, `${emitter.uuid}.meshRotationBySpeed.basisX`);
  const basisY = readFiniteTuple3(value.basisY, `${emitter.uuid}.meshRotationBySpeed.basisY`);
  const basisZ = readFiniteTuple3(value.basisZ, `${emitter.uuid}.meshRotationBySpeed.basisZ`);
  if (value.axisMode === 'fixed') {
    const axis = readFiniteTuple3(value.axis, `${emitter.uuid}.meshRotationBySpeed.axis`);
    const length = Math.hypot(...axis);
    if (length <= 1e-8) throw new Error(`Exporter fixed Mesh rotation-by-speed axis is zero on ${emitter.uuid}.`);
    return {
      axisMode: 'fixed', axis: [axis[0] / length, axis[1] / length, axis[2] / length],
      basisX, basisY, basisZ, speedRange, angularVelocity: value.angularVelocity
    };
  }
  if (value.axis !== undefined) {
    throw new Error(`Exporter derived Mesh rotation-by-speed metadata has an unexpected fixed axis on ${emitter.uuid}.`);
  }
  return { axisMode: value.axisMode, basisX, basisY, basisZ, speedRange, angularVelocity: value.angularVelocity };
}

function readUnityTrailSemanticsMetadata(emitter: Object3D): UnityTrailSemanticsMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.trailSemantics === undefined) return null;
  const value = exporterData.trailSemantics;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.trail_semantics.v1' ||
      typeof value.worldSpace !== 'boolean' || typeof value.dieWithParticles !== 'boolean' ||
      (value.sizeAffectsWidth !== undefined && typeof value.sizeAffectsWidth !== 'boolean') ||
      (value.minVertexDistance !== undefined &&
        (typeof value.minVertexDistance !== 'number' || !Number.isFinite(value.minVertexDistance) ||
          value.minVertexDistance < 0)) ||
      (value.colorOverTrail !== undefined && !isRecord(value.colorOverTrail))) {
    throw new Error(`Malformed exporter Trail semantics metadata on ${emitter.uuid}.`);
  }
  const colorTypes = new Set([
    'ConstantColor',
    'ColorRange',
    'RandomColor',
    'Gradient',
    'RandomColorBetweenGradient'
  ]);
  if (isRecord(value.colorOverTrail) && !colorTypes.has(value.colorOverTrail.type as string)) {
    throw new Error(`Unsupported exporter Trail color generator on ${emitter.uuid}.`);
  }
  const metadata: UnityTrailSemanticsMetadata = {
    worldSpace: value.worldSpace,
    dieWithParticles: value.dieWithParticles,
    sizeAffectsWidth: value.sizeAffectsWidth === true,
    minVertexDistance: value.minVertexDistance === undefined ? 0 : value.minVertexDistance
  };
  if (isRecord(value.colorOverTrail)) metadata.colorOverTrail = value.colorOverTrail;
  return metadata;
}

function readUnityMeshVelocityAlignmentMetadata(
  emitter: Object3D
): UnityMeshVelocityAlignmentMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.meshVelocityAlignment === undefined) return null;
  const value = exporterData.meshVelocityAlignment;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.mesh_velocity_alignment.v1') {
    throw new Error(`Malformed exporter Mesh Velocity alignment metadata on ${emitter.uuid}.`);
  }
  const forwardAxis = readFiniteTuple3(value.forwardAxis, `${emitter.uuid}.meshVelocityAlignment.forwardAxis`);
  const length = Math.hypot(...forwardAxis);
  if (Math.abs(length - 1) > 1e-3) {
    throw new Error(`Exporter Mesh Velocity alignment forward axis is not normalized on ${emitter.uuid}.`);
  }
  return { forwardAxis };
}

function readUnityRendererAlignmentMetadata(
  emitter: Object3D
): UnityRendererAlignmentMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.rendererAlignment === undefined) return null;
  const value = exporterData.rendererAlignment;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.renderer_alignment.v1' ||
      (value.mode !== 'local' && value.mode !== 'world' && value.mode !== 'view' &&
       value.mode !== 'facing' && value.mode !== 'velocity') ||
      value.preserveAuthored !== true ||
      (value.simulationSpace !== 'local' && value.simulationSpace !== 'world')) {
    throw new Error(`Malformed exporter renderer alignment metadata on ${emitter.uuid}.`);
  }
  return {
    mode: value.mode,
    preserveAuthored: true,
    simulationSpace: value.simulationSpace
  };
}

function readUnityRendererPivotMetadata(emitter: Object3D): UnityRendererPivotMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.rendererPivot === undefined) return null;
  const value = exporterData.rendererPivot;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.renderer_pivot.v1') {
    throw new Error(`Malformed exporter renderer pivot metadata on ${emitter.uuid}.`);
  }
  const pivot = readFiniteTuple3(value.value, `${emitter.uuid}.rendererPivot.value`);
  const geometryOffset = value.geometryOffset === undefined
    ? [pivot[0], pivot[1], -pivot[2]] as [number, number, number]
    : readFiniteTuple3(value.geometryOffset, `${emitter.uuid}.rendererPivot.geometryOffset`);
  return {
    value: pivot,
    geometryOffset,
    ...(typeof value.sourceRenderMode === 'string' ? { sourceRenderMode: value.sourceRenderMode } : {})
  };
}

function readUnityCustomDataMetadata(emitter: Object3D): UnityCustomDataMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.customData === undefined) return null;
  const value = exporterData.customData;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.custom_data.v1' ||
      !isRecord(value.custom1) || value.custom1.mode !== 'vector' ||
      !Array.isArray(value.custom1.components) || value.custom1.components.length !== 4 ||
      value.custom1.components.some((component) => !isRecord(component)) ||
      !isRecord(value.custom2) || value.custom2.mode !== 'color' || !isRecord(value.custom2.value)) {
    throw new Error(`Malformed exporter Custom Data metadata on ${emitter.uuid}.`);
  }
  return {
    custom1: value.custom1.components as UnityCustomDataMetadata['custom1'],
    custom2: value.custom2.value
  };
}

function readUnityMeshCameraAlignmentMetadata(
  emitter: Object3D
): UnityMeshCameraAlignmentMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.meshCameraAlignment === undefined) return null;
  const value = exporterData.meshCameraAlignment;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.mesh_camera_alignment.v1') {
    throw new Error(`Malformed exporter Mesh camera alignment metadata on ${emitter.uuid}.`);
  }
  if (value.mode !== 'view' && value.mode !== 'facing') {
    throw new Error(`Unsupported exporter Mesh camera alignment mode on ${emitter.uuid}.`);
  }
  const forwardAxis = readFiniteTuple3(
    value.forwardAxis,
    `${emitter.uuid}.meshCameraAlignment.forwardAxis`
  );
  const upAxis = readFiniteTuple3(
    value.upAxis,
    `${emitter.uuid}.meshCameraAlignment.upAxis`
  );
  if (Math.abs(Math.hypot(...forwardAxis) - 1) > 1e-3 ||
      Math.abs(Math.hypot(...upAxis) - 1) > 1e-3) {
    throw new Error(`Exporter Mesh camera alignment basis is not normalized on ${emitter.uuid}.`);
  }
  if (value.preserveAuthoredRotation !== true || value.simulationSpace !== 'local') {
    throw new Error(`Unsupported exporter Mesh camera alignment contract on ${emitter.uuid}.`);
  }
  return {
    mode: value.mode,
    forwardAxis,
    upAxis,
    preserveAuthoredRotation: true,
    simulationSpace: 'local'
  };
}

function readExporterUserData(emitter: Object3D): Record<string, unknown> | null {
  const exporterData = isRecord(emitter.userData?.unityParticleQuarks)
    ? emitter.userData.unityParticleQuarks
    : isRecord(emitter.userData?.unityParticleQuarks)
      ? emitter.userData.unityParticleQuarks
      : null;
  if (!exporterData) return null;
  if (exporterData.schemaVersion !== 'unity_particle_quarks_exporter.user_data.v1') {
    throw new Error(`Malformed exporter userData on ${emitter.uuid}.`);
  }
  return exporterData;
}

function readUnityParticleHeadMetadata(emitter: Object3D): UnityParticleHeadMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.particleHead === undefined) return null;
  const value = exporterData.particleHead;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.particle_head.v1' ||
      typeof value.geometry !== 'string' || typeof value.material !== 'string' ||
      !Number.isInteger(value.renderMode) || ![0, 1, 2, 4, 5].includes(value.renderMode as number) ||
      !Number.isFinite(value.renderOrder) || !Number.isInteger(value.layers) ||
      !Number.isInteger(value.uTileCount) || (value.uTileCount as number) < 1 ||
      !Number.isInteger(value.vTileCount) || (value.vTileCount as number) < 1 ||
      typeof value.blendTiles !== 'boolean' || typeof value.softParticles !== 'boolean' ||
      !Number.isFinite(value.softFarFade) || !Number.isFinite(value.softNearFade) ||
      typeof value.worldSpace !== 'boolean' || !isRecord(value.rotation) ||
      (value.rendererEmitterSettings !== undefined &&
        (!isRecord(value.rendererEmitterSettings) ||
          !Number.isFinite(value.rendererEmitterSettings.speedFactor) ||
          !Number.isFinite(value.rendererEmitterSettings.lengthFactor))) ||
      (value.restoreMaterialColor !== undefined && typeof value.restoreMaterialColor !== 'boolean') ||
      (value.materialProjectColorSpace !== undefined && value.materialProjectColorSpace !== 'gamma' && value.materialProjectColorSpace !== 'linear') ||
      !['local', 'velocity', 'view', 'facing', 'billboard'].includes(String(value.rotation.alignment)) ||
      value.rotation.preserveAuthored !== true) {
    throw new Error(`Malformed exporter Particle head metadata on ${emitter.uuid}.`);
  }
  if (value.renderMode === 1 && value.rendererEmitterSettings === undefined) {
    throw new Error(`Malformed exporter Stretch Particle head metadata on ${emitter.uuid}.`);
  }
  const rendererEmitterSettings = value.rendererEmitterSettings === undefined
    ? undefined
    : value.rendererEmitterSettings as Record<string, unknown>;
  return {
    geometry: value.geometry as string,
    material: value.material as string,
    materialColor: readOptionalHeadMaterialColor(value),
    restoreMaterialColor: value.restoreMaterialColor === undefined ? undefined : value.restoreMaterialColor as boolean,
    materialProjectColorSpace: value.materialProjectColorSpace === undefined
      ? undefined
      : value.materialProjectColorSpace as UnityProjectColorSpace,
    renderMode: value.renderMode as UnityParticleHeadMetadata['renderMode'],
    renderOrder: value.renderOrder as number,
    layers: value.layers as number,
    uTileCount: value.uTileCount as number,
    vTileCount: value.vTileCount as number,
    blendTiles: value.blendTiles as boolean,
    softParticles: value.softParticles as boolean,
    softFarFade: value.softFarFade as number,
    softNearFade: value.softNearFade as number,
    worldSpace: value.worldSpace as boolean,
    rendererEmitterSettings: rendererEmitterSettings === undefined
      ? undefined
      : {
        speedFactor: rendererEmitterSettings.speedFactor as number,
        lengthFactor: rendererEmitterSettings.lengthFactor as number
      },
    rotation: {
      alignment: value.rotation.alignment as UnityParticleHeadMetadata['rotation']['alignment'],
      preserveAuthored: true
    }
  };
}

function readOptionalHeadMaterialColor(value: Record<string, unknown>): [number, number, number, number] | undefined {
  if (value.materialColor === undefined) return undefined;
  if (!isRecord(value.materialColor) ||
      !Number.isFinite(value.materialColor.r) || !Number.isFinite(value.materialColor.g) ||
      !Number.isFinite(value.materialColor.b) || !Number.isFinite(value.materialColor.a)) {
    throw new Error('Malformed exporter Particle head material color metadata.');
  }
  return [value.materialColor.r, value.materialColor.g, value.materialColor.b, value.materialColor.a] as [number, number, number, number];
}

function applyUnityParticleHeadMaterialSemantics(material: Material, metadata: UnityParticleHeadMetadata): void {
  if (!metadata.materialColor) return;
  const colorMaterial = material as Material & {
    color?: { setRGB(r: number, g: number, b: number): unknown };
    opacity?: number;
    transparent?: boolean;
  };
  if (metadata.restoreMaterialColor && colorMaterial.color && typeof colorMaterial.color.setRGB === 'function') {
    colorMaterial.color.setRGB(
      metadata.materialColor[0],
      metadata.materialColor[1],
      metadata.materialColor[2]
    );
  }
  // Unlit companion heads intentionally keep particle-driven RGB, but their
  // source material alpha still controls visibility (for example alpha = 0).
  colorMaterial.opacity = metadata.materialColor[3];
  if (metadata.materialColor[3] < 1) colorMaterial.transparent = true;
  colorMaterial.needsUpdate = true;
}

function applyUnityParticleHeadBatchSemantics(batch: SpriteBatch, metadata: UnityParticleHeadMetadata): void {
  const alpha = metadata.materialColor?.[3];
  if (alpha === undefined) return;
  const shaderMaterial = batch.material as Material & {
    fragmentShader?: string;
    uniforms?: Record<string, { value: number }>;
  };
  if (typeof shaderMaterial.fragmentShader !== 'string') return;
  const uniformName = 'unityParticleQuarksHeadMaterialAlpha';
  const declaration = `uniform float ${uniformName};`;
  if (!shaderMaterial.fragmentShader.includes(declaration)) {
    shaderMaterial.fragmentShader = `${declaration}\n${shaderMaterial.fragmentShader}`;
    shaderMaterial.fragmentShader = shaderMaterial.fragmentShader.replace(
      'vec4 diffuseColor = vColor;',
      `vec4 diffuseColor = vColor;\n    diffuseColor.a *= ${uniformName};`
    );
  }
  shaderMaterial.uniforms = shaderMaterial.uniforms ?? {};
  shaderMaterial.uniforms[uniformName] = { value: alpha };
  shaderMaterial.needsUpdate = true;
}

function readUnityParticleCapacityMetadata(emitter: Object3D): UnityParticleCapacityMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.particleCapacity === undefined) return null;
  const value = exporterData.particleCapacity;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.particle_capacity.v1' ||
      !Number.isInteger(value.maxParticles) || (value.maxParticles as number) < 0) {
    throw new Error(`Malformed exporter particle capacity metadata on ${emitter.uuid}.`);
  }
  return { maxParticles: value.maxParticles as number };
}

function readUnityTextureSheetAnimationMetadata(
  emitter: Object3D
): UnityTextureSheetAnimationMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.textureSheetAnimation === undefined) return null;
  const value = exporterData.textureSheetAnimation;
  if (!isRecord(value) ||
      (value.schemaVersion !== 'unity_particle_quarks_exporter.texture_sheet_animation.v1' &&
       value.schemaVersion !== 'unity_particle_quarks_exporter.texture_sheet_animation.v2') ||
      !isRecord(value.frameOverTime) || !isRecord(value.startFrame)) {
    throw new Error(`Malformed exporter Texture Sheet Animation metadata on ${emitter.uuid}.`);
  }
  if (value.schemaVersion === 'unity_particle_quarks_exporter.texture_sheet_animation.v1') {
    if (value.timeMode !== 'lifetime' ||
        !Number.isInteger(value.tileCount) || (value.tileCount as number) < 1 ||
        !Number.isInteger(value.cycleCount) || (value.cycleCount as number) < 1) {
      throw new Error(`Malformed legacy exporter Texture Sheet Animation metadata on ${emitter.uuid}.`);
    }
    return {
      mode: 'grid',
      animation: 'wholeSheet',
      timeMode: 'lifetime',
      frameCount: value.tileCount as number,
      tileCountX: value.tileCount as number,
      tileCountY: 1,
      cycleCount: value.cycleCount as number,
      fps: 0,
      speedRange: [0, 1],
      rowMode: 'custom',
      rowIndex: 0,
      frameOverTime: value.frameOverTime,
      startFrame: value.startFrame,
      sprites: []
    };
  }
  if ((value.mode !== 'grid' && value.mode !== 'sprites') ||
      (value.animation !== 'wholeSheet' && value.animation !== 'singleRow' && value.animation !== 'sprites') ||
      (value.timeMode !== 'lifetime' && value.timeMode !== 'fps' && value.timeMode !== 'speed') ||
      (value.rowMode !== 'custom' && value.rowMode !== 'random' && value.rowMode !== 'meshIndex') ||
      !Number.isInteger(value.frameCount) || (value.frameCount as number) < 1 ||
      !Number.isInteger(value.tileCountX) || (value.tileCountX as number) < 1 ||
      !Number.isInteger(value.tileCountY) || (value.tileCountY as number) < 1 ||
      !Number.isInteger(value.cycleCount) || (value.cycleCount as number) < 1 ||
      !Number.isInteger(value.rowIndex) || (value.rowIndex as number) < 0) {
    throw new Error(`Malformed exporter Texture Sheet Animation v2 metadata on ${emitter.uuid}.`);
  }
  const fps = finiteUnityNumber(value.fps, `${emitter.uuid}.textureSheetAnimation.fps`);
  const speedRange = readFiniteTuple2(value.speedRange, `${emitter.uuid}.textureSheetAnimation.speedRange`);
  if (fps < 0 || speedRange[1] < speedRange[0]) {
    throw new Error(`Exporter Texture Sheet Animation timing range is invalid on ${emitter.uuid}.`);
  }
  const sprites = value.mode === 'sprites'
    ? readUnityTextureSheetSpriteFrames(value.sprites, emitter.uuid)
    : [];
  if (value.mode === 'sprites' && sprites.length !== value.frameCount) {
    throw new Error(`Exporter Texture Sheet Animation sprite frame count is inconsistent on ${emitter.uuid}.`);
  }
  return {
    mode: value.mode,
    animation: value.animation,
    timeMode: value.timeMode,
    frameCount: value.frameCount as number,
    tileCountX: value.tileCountX as number,
    tileCountY: value.tileCountY as number,
    cycleCount: value.cycleCount as number,
    fps,
    speedRange,
    rowMode: value.rowMode,
    rowIndex: value.rowIndex as number,
    frameOverTime: value.frameOverTime,
    startFrame: value.startFrame,
    sprites
  };
}

function readUnityTextureSheetSpriteFrames(value: unknown, emitterId: string): UnityTextureSheetSpriteFrame[] {
  if (!Array.isArray(value) || value.length === 0) {
    throw new Error(`Exporter Texture Sheet Animation has no sprite frames on ${emitterId}.`);
  }
  return value.map((entry, index) => {
    if (!isRecord(entry)) {
      throw new Error(`Malformed exporter sprite frame ${index} on ${emitterId}.`);
    }
    return {
      rect: readFiniteTuple4(entry.rect, `${emitterId}.textureSheetAnimation.sprites[${index}].rect`),
      sizeMul: readFiniteTuple2(entry.sizeMul, `${emitterId}.textureSheetAnimation.sprites[${index}].sizeMul`),
      pivot: readFiniteTuple2(entry.pivot, `${emitterId}.textureSheetAnimation.sprites[${index}].pivot`)
    };
  });
}

function installParticleSpawnBudget(
  system: ParticleSystem,
  requestedCapacity = MAX_RUNTIME_PARTICLES_PER_SYSTEM
): void {
  const capacitySystem = system as unknown as {
    spawn(count: number, emissionState: unknown, matrix: unknown): void;
    particleNum: number;
    __unityParticleQuarksSpawnBudget?: number;
  };
  const capacity = Math.min(MAX_RUNTIME_PARTICLES_PER_SYSTEM,
    Math.max(0, Math.trunc(Number.isFinite(requestedCapacity) ? requestedCapacity : 0)));
  if (capacitySystem.__unityParticleQuarksSpawnBudget !== undefined) {
    capacitySystem.__unityParticleQuarksSpawnBudget = Math.min(capacitySystem.__unityParticleQuarksSpawnBudget, capacity);
    return;
  }
  const originalSpawn = capacitySystem.spawn.bind(capacitySystem);
  capacitySystem.spawn = (count, emissionState, matrix) => {
    const available = Math.max(0, (capacitySystem.__unityParticleQuarksSpawnBudget ?? capacity) - capacitySystem.particleNum);
    const safeCount = Number.isFinite(count) ? Math.max(0, Math.floor(count)) : 0;
    if (available <= 0 || safeCount <= 0) return;
    originalSpawn(Math.min(safeCount, available), emissionState, matrix);
  };
  capacitySystem.__unityParticleQuarksSpawnBudget = capacity;
}

function installUnitySimulationSpeed(
  system: ParticleSystem,
  metadata: UnitySimulationSpeedMetadata
): void {
  const simulationSystem = system as unknown as {
    update(delta: number): void;
    __unityParticleQuarksSimulationSpeedPatched?: boolean;
  };
  if (simulationSystem.__unityParticleQuarksSimulationSpeedPatched) return;
  const originalUpdate = simulationSystem.update.bind(simulationSystem);
  let updating = false;
  simulationSystem.update = (delta) => {
    if (updating) {
      originalUpdate(delta);
      return;
    }
    updating = true;
    try {
      const safeDelta = Number.isFinite(delta) ? Math.min(0.1, Math.max(0, delta)) : 0;
      let remaining = safeDelta * metadata.value;
      if (remaining === 0) {
        originalUpdate(0);
        return;
      }
      while (remaining > 1e-12) {
        const step = Math.min(0.1, remaining);
        originalUpdate(step);
        remaining -= step;
      }
    } finally {
      updating = false;
    }
  };
  simulationSystem.__unityParticleQuarksSimulationSpeedPatched = true;
}

function installUnityStartDelay(system: ParticleSystem, metadata: UnityStartDelayMetadata): void {
  const delayedSystem = system as unknown as {
    emit(delta: number, state: object, matrix: QuarksMatrixLike): void;
    restart(): void;
    __unityParticleQuarksStartDelayPatched?: boolean;
  };
  if (delayedSystem.__unityParticleQuarksStartDelayPatched) return;
  const rng = new UnityXorshift128(metadata.randomSeed);
  const delayFactory = compileUnityCurve(metadata.delay, 'main.startDelay', () => rng.nextFloat());
  let states = new WeakMap<object, { remaining: number; delayingLoop: boolean }>();
  const createState = () => ({
    remaining: Math.max(0, delayFactory().evaluate(0)),
    delayingLoop: false
  });
  const originalEmit = delayedSystem.emit.bind(delayedSystem);
  delayedSystem.emit = (delta, state, matrix) => {
    let delayState = states.get(state);
    if (!delayState) {
      delayState = createState();
      states.set(state, delayState);
    }
    const time = isRecord(state) && typeof state.time === 'number' ? state.time : undefined;
    if (!delayState.delayingLoop && time !== undefined && time > system.duration && system.looping) {
      delayState.remaining = Math.max(0, delayFactory().evaluate(0));
      delayState.delayingLoop = true;
    }
    const safeDelta = Number.isFinite(delta) ? Math.max(0, delta) : 0;
    if (delayState.remaining > 0) {
      const consumed = Math.min(delayState.remaining, safeDelta);
      delayState.remaining -= consumed;
      if (consumed >= safeDelta - 1e-12) return;
      delta = safeDelta - consumed;
    }
    originalEmit(delta, state, matrix);
    delayState.delayingLoop = false;
  };
  const originalRestart = delayedSystem.restart.bind(delayedSystem);
  delayedSystem.restart = () => {
    rng.setSeed(metadata.randomSeed);
    states = new WeakMap<object, { remaining: number; delayingLoop: boolean }>();
    originalRestart();
  };
  delayedSystem.__unityParticleQuarksStartDelayPatched = true;
}

function installUnityLifetimeByEmitterSpeed(
  system: ParticleSystem,
  metadata: UnityLifetimeByEmitterSpeedMetadata
): void {
  const speedSystem = system as unknown as {
    emit(delta: number, state: object, matrix: QuarksMatrixLike): void;
    spawn(count: number, state: object, matrix: QuarksMatrixLike): void;
    restart(): void;
    particleNum: number;
    particles: Particle[];
    __unityParticleQuarksLifetimeByEmitterSpeedPatched?: boolean;
  };
  if (speedSystem.__unityParticleQuarksLifetimeByEmitterSpeedPatched) return;
  let currentSpeed = 0;
  let baselines = new WeakMap<object, [number, number, number]>();
  const rng = new UnityXorshift128(metadata.randomSeed);
  const multiplierFactory = compileUnityCurve(metadata.curve, 'main.lifetimeByEmitterSpeed', () => rng.nextFloat());
  const originalEmit = speedSystem.emit.bind(speedSystem);
  speedSystem.emit = (delta, state, matrix) => {
    const position: [number, number, number] = [matrix.elements[12] ?? 0, matrix.elements[13] ?? 0, matrix.elements[14] ?? 0];
    const previous = baselines.get(state);
    currentSpeed = previous && delta > 1e-8
      ? Math.max(0, Math.hypot(position[0] - previous[0], position[1] - previous[1], position[2] - previous[2]) / delta)
      : 0;
    baselines.set(state, position);
    originalEmit(delta, state, matrix);
  };
  const originalSpawn = speedSystem.spawn.bind(speedSystem);
  speedSystem.spawn = (count, state, matrix) => {
    const first = speedSystem.particleNum;
    originalSpawn(count, state, matrix);
    const normalizedSpeed = metadata.range[1] <= metadata.range[0]
      ? 0
      : Math.min(1, Math.max(0, (currentSpeed - metadata.range[0]) /
        (metadata.range[1] - metadata.range[0])));
    for (let index = first; index < speedSystem.particleNum; index += 1) {
      const particle = speedSystem.particles[index];
      if (particle) particle.life *= Math.max(0, multiplierFactory().evaluate(normalizedSpeed));
    }
  };
  const originalRestart = speedSystem.restart.bind(speedSystem);
  speedSystem.restart = () => {
    baselines = new WeakMap<object, [number, number, number]>();
    currentSpeed = 0;
    rng.setSeed(metadata.randomSeed);
    originalRestart();
  };
  speedSystem.__unityParticleQuarksLifetimeByEmitterSpeedPatched = true;
}

function installUnityTrailSemantics(
  emitter: Object3D,
  system: ParticleSystem,
  metadata: UnityTrailSemanticsMetadata
): void {
  const behavior = new UnityTrailSemanticsBehavior(metadata);
  system.behaviors.push(behavior);
  if (!metadata.worldSpace && !metadata.dieWithParticles && !metadata.sizeAffectsWidth &&
      metadata.minVertexDistance <= 0) return;
  const trailSystem = system as unknown as UnityTrailRuntimeSystem;
  if (metadata.worldSpace && !system.worldSpace) {
    trailSystem.__unityParticleQuarksTrailRecordsWorldSpace = true;
  }
  if (trailSystem.__unityParticleQuarksTrailSemanticsPatched) return;
  const convertedRecords = new WeakSet<object>();
  const recordMatrix = new Matrix4();
  const point = new Vector3();
  const originalUpdate = trailSystem.update.bind(trailSystem);
  trailSystem.update = (delta) => {
    originalUpdate(delta);
    if (metadata.worldSpace && !system.worldSpace) emitter.updateWorldMatrix(true, false);
    for (const particle of trailSystem.particles) {
      if (!particle) continue;
      if (metadata.dieWithParticles && particle.age >= particle.life) {
        particle.previous?.clear();
        continue;
      }
      behavior.captureHistoryBaseWidths(particle);
      if (metadata.worldSpace && !system.worldSpace && particle.previous) {
        const matrix = particle.parentMatrix
          ? recordMatrix.fromArray(particle.parentMatrix.elements)
          : emitter.matrixWorld;
        for (const record of particle.previous.values()) {
          if (convertedRecords.has(record)) continue;
          point.set(record.position.x, record.position.y, record.position.z).applyMatrix4(matrix);
          record.position.set(point.x, point.y, point.z);
          convertedRecords.add(record);
        }
      }
      behavior.filterHistoryByDistance(particle);
    }
  };
  trailSystem.__unityParticleQuarksTrailSemanticsPatched = true;
}

interface UnityTrailRuntimeSystem {
    update(delta: number): void;
    particles: Array<Particle & {
      previous?: {
        clear(): void;
        push(record: {
          position: QuarksVector3;
          color: QuarksVector4;
          size: number;
        }): void;
        values(): IterableIterator<{
          position: QuarksVector3;
          color: QuarksVector4;
          size: number;
        }>;
      };
    }>;
    __unityParticleQuarksTrailRecordsWorldSpace?: boolean;
    __unityParticleQuarksTrailSemanticsPatched?: boolean;
}

function readFiniteTuple3(value: unknown, field: string): [number, number, number] {
  if (!Array.isArray(value) || value.length !== 3 || !value.every((item) =>
    typeof item === 'number' && Number.isFinite(item))) {
    throw new Error(`Malformed exporter vector ${field}.`);
  }
  return [value[0] as number, value[1] as number, value[2] as number];
}

function readFiniteTuple2(value: unknown, field: string): [number, number] {
  if (!Array.isArray(value) || value.length !== 2 || !value.every((item) =>
    typeof item === 'number' && Number.isFinite(item))) {
    throw new Error(`Malformed exporter vector ${field}.`);
  }
  return [value[0] as number, value[1] as number];
}

function readFiniteTuple4(value: unknown, field: string): [number, number, number, number] {
  if (!Array.isArray(value) || value.length !== 4 || !value.every((item) =>
    typeof item === 'number' && Number.isFinite(item))) {
    throw new Error(`Malformed exporter color ${field}.`);
  }
  return [value[0] as number, value[1] as number, value[2] as number, value[3] as number];
}

function readUnityVelocityMetadata(emitter: Object3D): UnityVelocityMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.velocityOverLifetime === undefined) return null;
  const value = exporterData.velocityOverLifetime;
  if (!isRecord(value) ||
      (value.schemaVersion !== 'unity_particle_quarks_exporter.velocity_over_lifetime.v1' &&
       value.schemaVersion !== 'unity_particle_quarks_exporter.velocity_over_lifetime.v2') ||
      (value.space !== 'local' && value.space !== 'world') ||
      !isRecord(value.x) || !isRecord(value.y) || !isRecord(value.z)) {
    throw new Error(`Malformed exporter Velocity over Lifetime metadata on ${emitter.uuid}.`);
  }
  const zero = unityConstantCurveMetadata(0);
  const one = unityConstantCurveMetadata(1);
  return {
    basisX: readVelocityBasis(value.basisX, `${emitter.uuid}.basisX`),
    basisY: readVelocityBasis(value.basisY, `${emitter.uuid}.basisY`),
    basisZ: readVelocityBasis(value.basisZ, `${emitter.uuid}.basisZ`),
    origin: value.origin === undefined
      ? [0, 0, 0]
      : readFiniteTuple3(value.origin, `${emitter.uuid}.velocity.origin`),
    x: value.x,
    y: value.y,
    z: value.z,
    orbitalX: isRecord(value.orbitalX) ? value.orbitalX : zero,
    orbitalY: isRecord(value.orbitalY) ? value.orbitalY : zero,
    orbitalZ: isRecord(value.orbitalZ) ? value.orbitalZ : zero,
    orbitalOffsetX: isRecord(value.orbitalOffsetX) ? value.orbitalOffsetX : zero,
    orbitalOffsetY: isRecord(value.orbitalOffsetY) ? value.orbitalOffsetY : zero,
    orbitalOffsetZ: isRecord(value.orbitalOffsetZ) ? value.orbitalOffsetZ : zero,
    radial: isRecord(value.radial) ? value.radial : zero,
    speedModifier: isRecord(value.speedModifier) ? value.speedModifier : one
  };
}

function unityConstantCurveMetadata(value: number): Record<string, unknown> {
  return { mode: 'constant', value: { type: 'ConstantValue', value } };
}

function readUnityForceMetadata(emitter: Object3D): UnityForceMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.forceOverLifetime === undefined) return null;
  const value = exporterData.forceOverLifetime;
  if (!isRecord(value) ||
      value.schemaVersion !== 'unity_particle_quarks_exporter.force_over_lifetime.v1' ||
      (value.space !== 'local' && value.space !== 'world' && value.space !== 'custom') ||
      (value.space === 'custom' &&
       (typeof value.customTransformName !== 'string' || value.customTransformName.length === 0)) ||
      !isRecord(value.x) || !isRecord(value.y) || !isRecord(value.z)) {
    throw new Error(`Malformed exporter Force over Lifetime metadata on ${emitter.uuid}.`);
  }
  return {
    basisX: readVelocityBasis(value.basisX, `${emitter.uuid}.force.basisX`),
    basisY: readVelocityBasis(value.basisY, `${emitter.uuid}.force.basisY`),
    basisZ: readVelocityBasis(value.basisZ, `${emitter.uuid}.force.basisZ`),
    x: value.x,
    y: value.y,
    z: value.z
  };
}

function readUnityNoiseMetadata(emitter: Object3D): UnityNoiseMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.noise === undefined) return null;
  const value = exporterData.noise;
  if (!isRecord(value) ||
      value.schemaVersion !== 'unity_particle_quarks_exporter.noise.v1' ||
      (value.simulationSpace !== 'local' && value.simulationSpace !== 'world') ||
      typeof value.separateAxes !== 'boolean' ||
      typeof value.damping !== 'boolean' ||
      !Number.isInteger(value.randomSeed) || (value.randomSeed as number) < 0 ||
      (value.randomSeed as number) > 0xffffffff ||
      !Number.isInteger(value.qualityDimensions) || (value.qualityDimensions as number) < 1 ||
      (value.qualityDimensions as number) > 3 ||
      !Number.isInteger(value.octaveCount) || (value.octaveCount as number) < 1 ||
      !isRecord(value.strengthX) || !isRecord(value.strengthY) || !isRecord(value.strengthZ) ||
      !isRecord(value.positionAmount) || !isRecord(value.scrollSpeed) ||
      (value.remapEnabled !== undefined && typeof value.remapEnabled !== 'boolean') ||
      (value.remapEnabled === true &&
       (!isRecord(value.remapX) || !isRecord(value.remapY) || !isRecord(value.remapZ)))) {
    throw new Error(`Malformed exporter Noise metadata on ${emitter.uuid}.`);
  }
  const frequency = finiteUnityNumber(value.frequency, `${emitter.uuid}.noise.frequency`);
  const octaveMultiplier = finiteUnityNumber(value.octaveMultiplier, `${emitter.uuid}.noise.octaveMultiplier`);
  const octaveScale = finiteUnityNumber(value.octaveScale, `${emitter.uuid}.noise.octaveScale`);
  if (frequency <= 0 || octaveMultiplier < 0 || octaveScale <= 0) {
    throw new Error(`Exporter Noise frequency or octave metadata is out of range on ${emitter.uuid}.`);
  }
  const remapEnabled = value.remapEnabled === true;
  const remap = unityConstantCurveMetadata(1);
  return {
    particleToNoiseBasisX: readVelocityBasis(value.particleToNoiseBasisX, `${emitter.uuid}.noise.particleBasisX`),
    particleToNoiseBasisY: readVelocityBasis(value.particleToNoiseBasisY, `${emitter.uuid}.noise.particleBasisY`),
    particleToNoiseBasisZ: readVelocityBasis(value.particleToNoiseBasisZ, `${emitter.uuid}.noise.particleBasisZ`),
    noiseToParticleBasisX: readVelocityBasis(value.noiseToParticleBasisX, `${emitter.uuid}.noise.velocityBasisX`),
    noiseToParticleBasisY: readVelocityBasis(value.noiseToParticleBasisY, `${emitter.uuid}.noise.velocityBasisY`),
    noiseToParticleBasisZ: readVelocityBasis(value.noiseToParticleBasisZ, `${emitter.uuid}.noise.velocityBasisZ`),
    randomSeed: value.randomSeed as number,
    separateAxes: value.separateAxes,
    frequency,
    damping: value.damping,
    quality: value.qualityDimensions as UnityNoiseQuality,
    octaveCount: value.octaveCount as number,
    octaveMultiplier,
    octaveScale,
    strengthX: value.strengthX,
    strengthY: value.strengthY,
    strengthZ: value.strengthZ,
    positionAmount: value.positionAmount,
    scrollSpeed: value.scrollSpeed,
    remapEnabled,
    remapX: isRecord(value.remapX) ? value.remapX : remap,
    remapY: isRecord(value.remapY) ? value.remapY : remap,
    remapZ: isRecord(value.remapZ) ? value.remapZ : remap
  };
}

function readUnityParticleLightsMetadata(emitter: Object3D): UnityParticleLightsMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.lights === undefined) return null;
  const value = exporterData.lights;
  if (!isRecord(value) ||
      value.schemaVersion !== 'unity_particle_quarks_exporter.lights.v1' ||
      !Number.isInteger(value.randomSeed) || (value.randomSeed as number) < 0 ||
      (value.randomSeed as number) > 0xffffffff ||
      typeof value.randomDistribution !== 'boolean' ||
      typeof value.useParticleColor !== 'boolean' ||
      typeof value.sizeAffectsRange !== 'boolean' ||
      typeof value.alphaAffectsIntensity !== 'boolean' ||
      typeof value.uses3DSize !== 'boolean' ||
      typeof value.meshSize !== 'boolean' ||
      (value.renderScaleMode !== 'hierarchy' && value.renderScaleMode !== 'local' &&
       value.renderScaleMode !== 'shape') ||
      !Number.isInteger(value.maxLights) || (value.maxLights as number) < 1 ||
      !isRecord(value.range) || !isRecord(value.intensity) ||
      !isRecord(value.sourceRenderScale) ||
      !isRecord(value.particleColorMultiplier) ||
      !isRecord(value.light) || value.light.type !== 'point' ||
      !isRecord(value.light.color) ||
      !Number.isInteger(value.light.cullingMask) ||
      (value.light.shadowMode !== 'none' && value.light.shadowMode !== 'hard' && value.light.shadowMode !== 'soft')) {
    throw new Error(`Malformed exporter Lights metadata on ${emitter.uuid}.`);
  }
  const ratio = finiteUnityNumber(value.ratio, `${emitter.uuid}.lights.ratio`);
  const baseIntensity = finiteUnityNumber(value.light.intensity, `${emitter.uuid}.lights.light.intensity`);
  const baseRange = finiteUnityNumber(value.light.range, `${emitter.uuid}.lights.light.range`);
  const color = value.light.color;
  const particleColorMultiplier = value.particleColorMultiplier;
  const sourceRenderScale = value.sourceRenderScale;
  const red = finiteUnityNumber(color.r, `${emitter.uuid}.lights.light.color.r`);
  const green = finiteUnityNumber(color.g, `${emitter.uuid}.lights.light.color.g`);
  const blue = finiteUnityNumber(color.b, `${emitter.uuid}.lights.light.color.b`);
  const multiplierRed = finiteUnityNumber(particleColorMultiplier.r, `${emitter.uuid}.lights.particleColorMultiplier.r`);
  const multiplierGreen = finiteUnityNumber(particleColorMultiplier.g, `${emitter.uuid}.lights.particleColorMultiplier.g`);
  const multiplierBlue = finiteUnityNumber(particleColorMultiplier.b, `${emitter.uuid}.lights.particleColorMultiplier.b`);
  const multiplierAlpha = finiteUnityNumber(particleColorMultiplier.a, `${emitter.uuid}.lights.particleColorMultiplier.a`);
  const renderScaleX = finiteUnityNumber(sourceRenderScale.x, `${emitter.uuid}.lights.sourceRenderScale.x`);
  const renderScaleY = finiteUnityNumber(sourceRenderScale.y, `${emitter.uuid}.lights.sourceRenderScale.y`);
  const renderScaleZ = finiteUnityNumber(sourceRenderScale.z, `${emitter.uuid}.lights.sourceRenderScale.z`);
  if (ratio <= 0 || ratio > 1 || baseIntensity < 0 || baseRange < 0 ||
      red < 0 || green < 0 || blue < 0 ||
      renderScaleX <= 0 || renderScaleY <= 0 || renderScaleZ <= 0) {
    throw new Error(`Exporter Lights scalar metadata is out of range on ${emitter.uuid}.`);
  }
  return {
    randomSeed: value.randomSeed as number,
    ratio,
    randomDistribution: value.randomDistribution,
    useParticleColor: value.useParticleColor,
    sizeAffectsRange: value.sizeAffectsRange,
    alphaAffectsIntensity: value.alphaAffectsIntensity,
    maxLights: value.maxLights as number,
    uses3DSize: value.uses3DSize,
    meshSize: value.meshSize,
    renderScaleMode: value.renderScaleMode,
    sourceRenderScale: [renderScaleX, renderScaleY, renderScaleZ],
    particleColorMultiplier: [multiplierRed, multiplierGreen, multiplierBlue, multiplierAlpha],
    range: value.range,
    intensity: value.intensity,
    light: {
      color: [red, green, blue],
      intensity: baseIntensity,
      range: baseRange,
      cullingMask: value.light.cullingMask as number,
      shadowMode: value.light.shadowMode
    }
  };
}

function installUnityParticleLights(
  emitter: Object3D,
  system: ParticleSystem,
  metadata: UnityParticleLightsMetadata
): void {
  const adapter = new UnityParticleLightsAdapter(emitter, system, metadata);
  const lightSystem = system as unknown as {
    particleNum: number;
    spawn(count: number, emissionState: unknown, matrix: unknown): void;
    update(delta: number): void;
    restart(): void;
    __unityParticleQuarksLightsPatched?: boolean;
  };
  if (lightSystem.__unityParticleQuarksLightsPatched) return;

  const originalSpawn = lightSystem.spawn.bind(lightSystem);
  lightSystem.spawn = (count, emissionState, matrix) => {
    const fromIndex = lightSystem.particleNum;
    originalSpawn(count, emissionState, matrix);
    adapter.assignSpawnedParticles(fromIndex, matrix as { elements: ArrayLike<number> });
  };
  const originalUpdate = lightSystem.update.bind(lightSystem);
  lightSystem.update = (delta) => {
    originalUpdate(delta);
    adapter.sync();
  };
  const originalRestart = lightSystem.restart.bind(lightSystem);
  lightSystem.restart = () => {
    originalRestart();
    adapter.restart();
  };
  lightSystem.__unityParticleQuarksLightsPatched = true;
}

function readUnityGravityMetadata(emitter: Object3D): UnityGravityMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.gravity === undefined) return null;
  const value = exporterData.gravity;
  if (!isRecord(value) || value.schemaVersion !== 'unity_particle_quarks_exporter.gravity.v1' ||
      !isRecord(value.modifier)) {
    throw new Error(`Malformed exporter Gravity metadata on ${emitter.uuid}.`);
  }
  return {
    acceleration: readFiniteTuple3(value.acceleration, `${emitter.uuid}.gravity.acceleration`),
    modifier: value.modifier
  };
}

function readUnityLimitVelocityMetadata(emitter: Object3D): UnityLimitVelocityMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.limitVelocityOverLifetime === undefined) return null;
  const value = exporterData.limitVelocityOverLifetime;
  if (!isRecord(value) ||
      (value.schemaVersion !== 'unity_particle_quarks_exporter.limit_velocity_over_lifetime.v1' &&
       value.schemaVersion !== 'unity_particle_quarks_exporter.limit_velocity_over_lifetime.v2' &&
       value.schemaVersion !== 'unity_particle_quarks_exporter.limit_velocity_over_lifetime.v3') ||
      (value.limit !== null && value.limit !== undefined && !isRecord(value.limit)) ||
      (value.limitX !== null && value.limitX !== undefined && !isRecord(value.limitX)) ||
      (value.limitY !== null && value.limitY !== undefined && !isRecord(value.limitY)) ||
      (value.limitZ !== null && value.limitZ !== undefined && !isRecord(value.limitZ)) ||
      (value.drag !== null && value.drag !== undefined && !isRecord(value.drag))) {
    throw new Error(`Malformed exporter Limit Velocity metadata on ${emitter.uuid}.`);
  }
  const dampen = finiteUnityNumber(value.dampen, `${emitter.uuid}.limitVelocityOverLifetime.dampen`);
  if (dampen < 0 || dampen > 1) {
    throw new Error(`Exporter Limit Velocity dampen is out of range on ${emitter.uuid}.`);
  }
  const limit = isRecord(value.limit) ? value.limit : undefined;
  const limitX = isRecord(value.limitX) ? value.limitX : undefined;
  const limitY = isRecord(value.limitY) ? value.limitY : undefined;
  const limitZ = isRecord(value.limitZ) ? value.limitZ : undefined;
  const drag = isRecord(value.drag) ? value.drag : undefined;
  if (!limit && !limitX && !limitY && !limitZ && !drag) {
    throw new Error(`Exporter Limit Velocity metadata has neither limit nor drag on ${emitter.uuid}.`);
  }
  return {
    limit,
    separateAxes: value.separateAxes === true,
    limitX,
    limitY,
    limitZ,
    dampen,
    drag,
    multiplyDragByParticleSize: value.multiplyDragByParticleSize === true,
    multiplyDragByParticleVelocity: value.multiplyDragByParticleVelocity === true
  };
}

function readUnityInheritVelocityMetadata(emitter: Object3D): UnityInheritVelocityMetadata | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData || exporterData.inheritVelocity === undefined) return null;
  const value = exporterData.inheritVelocity;
  if (!isRecord(value) ||
      (value.schemaVersion !== 'unity_particle_quarks_exporter.inherit_velocity.v1' &&
       value.schemaVersion !== 'unity_particle_quarks_exporter.inherit_velocity.v2') ||
      (value.mode !== 'initial' && value.mode !== 'current') ||
      !isRecord(value.curve)) {
    throw new Error(`Malformed exporter Inherit Velocity metadata on ${emitter.uuid}.`);
  }
  return { mode: value.mode, curve: value.curve };
}

function readVelocityBasis(value: unknown, field: string): [number, number, number] {
  return readFiniteTuple3(value, field);
}

function compileUnityCurve(
  metadata: Record<string, unknown>,
  field: string,
  random: () => number = Math.random
): UnityCurveFactory {
  switch (metadata.mode) {
    case 'constant':
    case 'twoConstants':
    case 'curve':
      return compileUnityGenerator(metadata.value, `${field}.value`, random);
    case 'twoCurves': {
      const minimumFactory = compileUnityGenerator(metadata.minimum, `${field}.minimum`, random);
      const maximumFactory = compileUnityGenerator(metadata.maximum, `${field}.maximum`, random);
      return () => {
        const minimum = minimumFactory();
        const maximum = maximumFactory();
        const blend = random();
        return {
          evaluate: (t) => minimum.evaluate(t) + (maximum.evaluate(t) - minimum.evaluate(t)) * blend
        };
      };
    }
    default:
      throw new Error(`Unsupported exporter Unity curve mode at ${field}.`);
  }
}

function compileUnityGenerator(
  value: unknown,
  field: string,
  random: () => number = Math.random
): UnityCurveFactory {
  if (!isRecord(value) || typeof value.type !== 'string') {
    throw new Error(`Malformed exporter Unity generator at ${field}.`);
  }
  if (value.type === 'ConstantValue') {
    const constant = finiteUnityNumber(value.value, `${field}.value`);
    return () => ({ evaluate: () => constant });
  }
  if (value.type === 'IntervalValue') {
    const minimum = finiteUnityNumber(value.a, `${field}.a`);
    const maximum = finiteUnityNumber(value.b, `${field}.b`);
    return () => {
      const sampled = minimum + (maximum - minimum) * random();
      return { evaluate: () => sampled };
    };
  }
  if (value.type === 'PiecewiseBezier') {
    if (!Array.isArray(value.functions) || value.functions.length === 0) {
      throw new Error(`Malformed exporter Unity PiecewiseBezier at ${field}.`);
    }
    const pieces = value.functions.map((entry, index) => {
      if (!isRecord(entry) || !isRecord(entry.function)) {
        throw new Error(`Malformed exporter Unity Bezier ${field}[${index}].`);
      }
      return {
        start: finiteUnityNumber(entry.start, `${field}[${index}].start`),
        p0: finiteUnityNumber(entry.function.p0, `${field}[${index}].p0`),
        p1: finiteUnityNumber(entry.function.p1, `${field}[${index}].p1`),
        p2: finiteUnityNumber(entry.function.p2, `${field}[${index}].p2`),
        p3: finiteUnityNumber(entry.function.p3, `${field}[${index}].p3`)
      };
    });
    if (Math.abs(pieces[0]?.start ?? 1) > 1e-6 ||
        pieces.some((piece, index) => piece.start < 0 || piece.start > 1 ||
          (index > 0 && piece.start <= (pieces[index - 1]?.start ?? -1)))) {
      throw new Error(`Exporter Unity PiecewiseBezier starts are invalid at ${field}.`);
    }
    return () => ({
      evaluate: (rawT) => {
        const t = Math.min(1, Math.max(0, rawT));
        let index = pieces.length - 1;
        for (let candidate = 0; candidate + 1 < pieces.length; candidate += 1) {
          if (t < (pieces[candidate + 1]?.start ?? 1)) {
            index = candidate;
            break;
          }
        }
        const piece = pieces[index];
        if (!piece) return 0;
        const end = pieces[index + 1]?.start ?? 1;
        const u = end <= piece.start ? 0 : (t - piece.start) / (end - piece.start);
        const inverse = 1 - u;
        return inverse * inverse * inverse * piece.p0 +
          3 * inverse * inverse * u * piece.p1 +
          3 * inverse * u * u * piece.p2 +
          u * u * u * piece.p3;
      }
    });
  }
  throw new Error(`Unsupported exporter Unity generator ${value.type} at ${field}.`);
}

function finiteUnityNumber(value: unknown, field: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new Error(`Malformed exporter Unity number at ${field}.`);
  }
  return value;
}

function repairUnitySubEmitterSemantics(effectRoot: Object3D): void {
  effectRoot.traverse((object) => {
    const parentEmitter = object as Object3D & { system?: ParticleSystem };
    const parentSystem = parentEmitter.system;
    if (object.type !== 'ParticleEmitter' || !parentSystem) return;

    const subBehaviors = parentSystem.behaviors
      .filter((behavior) => behavior.type === 'EmitSubParticleSystem') as unknown as SubEmitterBehaviorLike[];
    const inheritanceEntries = readSubEmitterInheritance(parentEmitter);
    if (inheritanceEntries && inheritanceEntries.length !== subBehaviors.length) {
      throw new Error(`Exporter subemitter metadata count does not match ${parentEmitter.uuid} behaviors.`);
    }

    for (let behaviorIndex = 0; behaviorIndex < subBehaviors.length; behaviorIndex += 1) {
      const sub = subBehaviors[behaviorIndex];
      if (!sub) continue;
      const childEmitter = sub.subParticleSystem;
      if (sub.type !== 'EmitSubParticleSystem' || !childEmitter || !sub.emit || sub.__unityParticleQuarksSemanticsPatched) continue;
      const inheritance = inheritanceEntries?.[behaviorIndex];
      const hasInheritance = inheritance !== undefined && hasParticleInheritance(inheritance);
      const childSourceUuid = (childEmitter as Object3D & { __unityParticleQuarksSourceUuid?: string })
        .__unityParticleQuarksSourceUuid ?? childEmitter.uuid;
      if (inheritance &&
          (inheritance.subParticleSystem !== childSourceUuid || inheritance.mode !== sub.mode)) {
        throw new Error(`Exporter subemitter metadata does not match behavior ${behaviorIndex} on ${parentEmitter.uuid}.`);
      }

      const originalEmit = sub.emit.bind(sub);
      const originalFrameUpdate = sub.frameUpdate?.bind(sub);
      const originalReset = sub.reset?.bind(sub);
      const childSystem = childEmitter.system as ParticleSystem & {
        emit(delta: number, state: object, matrix: QuarksMatrixLike): void;
        particles: SubEmitterParticleLike[];
        duration: number;
      };
      const snapshots = new WeakMap<object, SubEmitterParentSnapshot>();
      let parentParticles = new WeakMap<object, SubEmitterParticleLike>();
      const rootInverse = new Matrix4();
      const parentRelative = new Matrix4();
      const childRelative = new Matrix4();
      const parentRelativeInverse = new Matrix4();
      const childFromParentBasis = new Matrix4();
      const triggerMatrix = new Matrix4();
      const localTranslation = new Matrix4();
      const inheritedRotation = new Matrix4();
      const inheritedQuaternion = new Quaternion();
      let deathAdvance = 0;

      sub.emit = (particle, delta) => {
        deathAdvance = sub.mode === 0
          ? Math.max(0, Math.min(delta, particle.life - particle.age))
          : 0;
        const previousEmissionCount = sub.subEmissions?.length ?? 0;
        try {
          originalEmit(particle, delta);
          if (sub.subEmissions) {
            for (let index = previousEmissionCount; index < sub.subEmissions.length; index += 1) {
              const state = sub.subEmissions[index];
              if (state) parentParticles.set(state, particle);
            }
          }
          if (inheritance && hasParticleInheritance(inheritance) && sub.subEmissions) {
            const snapshot = captureSubEmitterParent(particle, deathAdvance);
            for (let index = previousEmissionCount; index < sub.subEmissions.length; index += 1) {
              const state = sub.subEmissions[index];
              if (state) snapshots.set(state, snapshot);
            }
          }
        } finally {
          deathAdvance = 0;
        }
      };

      if (originalFrameUpdate && (sub.mode === 1 || hasInheritance)) {
        sub.frameUpdate = (delta) => {
          const originalChildEmit = childSystem.emit;
          childSystem.emit = (childDelta, state, matrix) => {
            // Unity Birth sub-emitters stop producing new child particles
            // when the triggering parent dies. Existing child particles still
            // age normally on the child system.
            const parent = parentParticles.get(state);
            if (sub.mode === 1 && parent && parent.age >= parent.life - 1e-8) return;
            const firstNewParticle = childSystem.particleNum;
            const previousDuration = childSystem.duration;
            if (inheritance?.inheritDuration) {
              childSystem.duration = Math.max(0, snapshots.get(state)?.remainingLifetime ?? previousDuration);
            }
            try {
              originalChildEmit.call(childSystem, childDelta, state, matrix);
            } finally {
              childSystem.duration = previousDuration;
            }
            // Birth follows the live parent matrix while producing children,
            // but each child keeps the transform from its spawn frame.
            // Quarks otherwise stores the mutable subemission matrix by
            // reference on every child and drags the whole trail to the
            // latest parent position.
            if (sub.mode === 1 && !childSystem.worldSpace) {
              for (let index = firstNewParticle; index < childSystem.particleNum; index += 1) {
                const particle = childSystem.particles[index] as SubEmitterParticleLike | undefined;
                if (particle) particle.parentMatrix = new Matrix4().fromArray(matrix.elements);
              }
            }
            const snapshot = snapshots.get(state);
            if (!snapshot || !inheritance) return;
            for (let index = firstNewParticle; index < childSystem.particleNum; index += 1) {
              const particle = childSystem.particles[index];
              if (particle) applySubEmitterInheritance(particle, snapshot, inheritance);
            }
          };
          try {
            originalFrameUpdate(delta);
          } finally {
            childSystem.emit = originalChildEmit;
          }
        };
      }

      sub.setMatrixFromParticle = (target, particle) => {
        effectRoot.updateWorldMatrix(true, true);
        rootInverse.copy(effectRoot.matrixWorld).invert();
        parentRelative.multiplyMatrices(rootInverse, parentEmitter.matrixWorld);
        childRelative.multiplyMatrices(rootInverse, childEmitter.matrixWorld);

        parentRelativeInverse.copy(parentRelative).invert();
        childFromParentBasis.multiplyMatrices(parentRelativeInverse, childRelative);

        const speedScale = particle.speedModifier ?? 1;
        const x = particle.position.x + particle.velocity.x * deathAdvance * speedScale;
        const y = particle.position.y + particle.velocity.y * deathAdvance * speedScale;
        const z = particle.position.z + particle.velocity.z * deathAdvance * speedScale;
        if (parentSystem.worldSpace) {
          triggerMatrix.copy(parentEmitter.matrixWorld);
          triggerMatrix.elements[12] = x;
          triggerMatrix.elements[13] = y;
          triggerMatrix.elements[14] = z;
        } else {
          localTranslation.makeTranslation(x, y, z);
          triggerMatrix.multiplyMatrices(parentEmitter.matrixWorld, localTranslation);
        }

        // Exporter 0.1.8 used this stock boolean for InheritRotation. New metadata
        // applies rotation to child particles without rotating the child Shape.
        if (!inheritance && sub.useVelocityAsBasis && particle.rotation !== undefined) {
          if (typeof particle.rotation === 'number') {
            inheritedQuaternion.setFromAxisAngle(FORWARD, particle.rotation);
          } else {
            inheritedQuaternion.set(
              particle.rotation.x,
              particle.rotation.y,
              particle.rotation.z,
              particle.rotation.w
            );
          }
          inheritedRotation.makeRotationFromQuaternion(inheritedQuaternion);
          triggerMatrix.multiply(inheritedRotation);
        }

        triggerMatrix.multiply(childFromParentBasis);
        target.fromArray(triggerMatrix.elements);
      };

      sub.reset = () => {
        originalReset?.();
        if (sub.subEmissions) sub.subEmissions.length = 0;
        parentParticles = new WeakMap<object, SubEmitterParticleLike>();
      };
      sub.__unityParticleQuarksSemanticsPatched = true;
    }
  });
}

function readSubEmitterInheritance(emitter: Object3D): SubEmitterInheritanceMetadata[] | null {
  const exporterData = readExporterUserData(emitter);
  if (!exporterData) return null;
  if (exporterData.schemaVersion !== 'unity_particle_quarks_exporter.user_data.v1' ||
      !Array.isArray(exporterData.subEmitterInheritance)) {
    throw new Error(`Unsupported exporter userData on ParticleEmitter ${emitter.uuid}.`);
  }
  return exporterData.subEmitterInheritance.map((value, index) => {
    if (!isRecord(value) || !Number.isInteger(value.index) ||
        typeof value.subParticleSystem !== 'string' || !Number.isInteger(value.mode) ||
        typeof value.inheritColor !== 'boolean' || typeof value.inheritSize !== 'boolean' ||
        typeof value.inheritRotation !== 'boolean' || typeof value.inheritLifetime !== 'boolean' ||
        typeof value.inheritDuration !== 'boolean') {
      throw new Error(`Malformed exporter subemitter metadata ${index} on ${emitter.uuid}.`);
    }
    return value as unknown as SubEmitterInheritanceMetadata;
  });
}

function hasParticleInheritance(metadata: SubEmitterInheritanceMetadata): boolean {
  return metadata.inheritColor || metadata.inheritSize || metadata.inheritRotation ||
    metadata.inheritLifetime || metadata.inheritDuration;
}

function captureSubEmitterParent(
  particle: SubEmitterParticleLike,
  deathAdvance: number
): SubEmitterParentSnapshot {
  return {
    color: [particle.color.x, particle.color.y, particle.color.z, particle.color.w],
    size: particle.size.x,
    rotation: subEmitterRotationZ(particle.rotation),
    remainingLifetime: Math.max(0, particle.life - particle.age - deathAdvance)
  };
}

function applySubEmitterInheritance(
  particle: SubEmitterParticleLike,
  parent: SubEmitterParentSnapshot,
  metadata: SubEmitterInheritanceMetadata
): void {
  if (metadata.inheritColor) {
    particle.startColor.x *= parent.color[0];
    particle.startColor.y *= parent.color[1];
    particle.startColor.z *= parent.color[2];
    particle.startColor.w *= parent.color[3];
    particle.color.x *= parent.color[0];
    particle.color.y *= parent.color[1];
    particle.color.z *= parent.color[2];
    particle.color.w *= parent.color[3];
  }
  if (metadata.inheritSize) {
    const startSize = particle.startSize.x * parent.size;
    const currentSize = particle.size.x * parent.size;
    particle.startSize.set(startSize, startSize, startSize);
    particle.size.set(currentSize, currentSize, currentSize);
  }
  if (metadata.inheritRotation && particle.rotation !== undefined) {
    if (typeof particle.rotation === 'number') {
      particle.rotation += parent.rotation;
    } else {
      const rotation = subEmitterRotationZ(particle.rotation) + parent.rotation;
      const halfRotation = rotation * 0.5;
      particle.rotation.set(0, 0, Math.sin(halfRotation), Math.cos(halfRotation));
    }
  }
  if (metadata.inheritLifetime) particle.life *= parent.remainingLifetime;
}

function subEmitterRotationZ(
  rotation: number | { x: number; y: number; z: number; w: number } | undefined
): number {
  if (rotation === undefined) return 0;
  if (typeof rotation === 'number') return rotation;
  const quaternion = new QuarksQuaternion(rotation.x, rotation.y, rotation.z, rotation.w);
  return new QuarksEuler().setFromQuaternion(quaternion, 'XYZ').z;
}

function isUnityExporterJson(json: unknown): boolean {
  if (!isRecord(json)) return false;
  if (isRecord(json.metadata) &&
      (json.metadata.generator === 'UnityParticleQuarksExporter' ||
       json.metadata.generator === 'UnityParticleQuarksExporter')) return true;
  return containsExporterUserData(json.object);
}

function containsExporterUserData(value: unknown): boolean {
  if (!isRecord(value)) return false;
  if (isRecord(value.userData) &&
      (isRecord(value.userData.unityParticleQuarks) || isRecord(value.userData.unityParticleQuarks))) {
    return true;
  }
  return Array.isArray(value.children) && value.children.some(containsExporterUserData);
}

function disposeTemplateResources(template: Object3D): void {
  const geometries = new Set<BufferGeometry>();
  const materials = new Set<Material>();
  for (const system of collectSystems(template)) {
    geometries.add(system.instancingGeometry);
    materials.add(system.material);
  }
  for (const material of materials) {
    const mapped = material as Material & { map?: { dispose(): void } | null };
    mapped.map?.dispose();
    material.dispose();
  }
  for (const geometry of geometries) geometry.dispose();
}

function disposeCompanionHeadResources(resources: Map<string, CompanionHeadResources>): void {
  const geometries = new Set<BufferGeometry>();
  const materials = new Set<Material>();
  for (const resource of resources.values()) {
    geometries.add(resource.geometry);
    materials.add(resource.material);
  }
  for (const geometry of geometries) geometry.dispose();
  for (const material of materials) {
    (material as Material & { map?: { dispose(): void } | null }).map?.dispose();
    material.dispose();
  }
}

function absolutizeImageUrls(json: unknown, sourceUrl: string): void {
  if (!isRecord(json) || !Array.isArray(json.images)) return;
  for (const image of json.images) {
    if (isRecord(image) && typeof image.url === 'string') image.url = new URL(image.url, sourceUrl).href;
  }
}

function configureSoftParticles(json: unknown, hasDepthTexture: boolean): number {
  if (hasDepthTexture) return 0;
  let disabled = 0;
  const visit = (value: unknown): void => {
    if (!isRecord(value)) return;
    if (isRecord(value.ps) && value.ps.softParticles === true) {
      value.ps.softParticles = false;
      disabled += 1;
    }
    if (isRecord(value.object)) visit(value.object);
    if (Array.isArray(value.children)) for (const child of value.children) visit(child);
  };
  visit(json);
  return disabled;
}

function hasNonUnitScale(scale: Vector3): boolean {
  return Math.abs(scale.x - 1) > 1e-6 || Math.abs(scale.y - 1) > 1e-6 || Math.abs(scale.z - 1) > 1e-6;
}

function requiresWorldSpaceScaleApproximation(systems: ParticleSystem[]): boolean {
  const spatialBehaviors = new Set([
    'ForceOverLife',
    'GravityForce',
    'UnityParticleQuarksVelocityOverLifetime',
    'LimitSpeedOverLife',
    'UnityParticleQuarksLimitVelocityOverLifetime',
    'Noise',
    'UnityParticleQuarksNoise',
    'OrbitOverLife'
  ]);
  return systems.some((system) => system.worldSpace &&
    system.behaviors.some((behavior) => spatialBehaviors.has(behavior.type)));
}

function poolKey(effectId: string, hash: string): string {
  return `${effectId}@${hash}`;
}

function integerInRange(value: number, min: number, max: number, field: string): number {
  if (!Number.isInteger(value) || value < min || value > max) throw new Error(`VFX ${field} must be an integer in [${min}, ${max}].`);
  return value;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
