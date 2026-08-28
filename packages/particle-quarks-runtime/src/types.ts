import type { Camera, Object3D, Scene, Texture, WebGLRenderer } from 'three';

export interface VfxVariant {
  emissionRateMultiplier?: number;
  lifetimeMultiplier?: number;
  speedMultiplier?: number;
  sizeMultiplier?: number;
  limitVelocityDragMultiplier?: number;
  limitVelocityMultiplyDragByParticleSize?: boolean;
  limitVelocityMultiplyDragByParticleVelocity?: boolean;
  colorMultiplier?: [number, number, number, number];
  looping?: boolean;
}

export interface VfxTransform {
  position?: [number, number, number];
  normal?: [number, number, number];
  scale?: number | [number, number, number];
  parent?: Object3D;
}

export interface VfxEvent extends VfxTransform {
  effectId: string;
  position: [number, number, number];
  variant?: VfxVariant;
}

export interface VfxSpawnOptions extends VfxTransform {
  variant?: VfxVariant;
}

export type VfxOverflowPolicy = 'drop-newest' | 'reuse-oldest';

export interface VfxPoolOptions {
  prewarm?: number;
  max?: number;
}

export type VfxRuntimeProfile = 'stock' | 'extended';

export interface VfxExtensionDescriptor {
  id: string;
  version: string;
}

export interface CreateVfxRuntimeOptions {
  scene: Scene;
  renderer: WebGLRenderer;
  camera: Camera;
  pool?: VfxPoolOptions;
  overflow?: VfxOverflowPolicy;
  allowPartial?: boolean;
  runtimeProfile?: VfxRuntimeProfile;
  depthTexture?: Texture;
  fetch?: typeof globalThis.fetch;
}

export interface VfxManifestEffect {
  id: string;
  url?: string;
  status: 'ready' | 'partial' | 'failed';
  runtimeProfile?: VfxRuntimeProfile;
  runtimeTier?: 'stock' | 'paired';
  extensionsUsed?: VfxExtensionDescriptor[];
  extensionsRequired?: VfxExtensionDescriptor[];
  conversionReport?: string;
  fallbackUrl?: string;
}

export interface VfxManifest {
  schemaVersion: 'unity_particle_quarks_runtime.manifest.v1';
  effects: VfxManifestEffect[];
}

export interface VfxEffectTelemetry {
  source: 'converted' | 'synthetic-fallback';
  status: VfxManifestEffect['status'];
  url: string;
}

export interface VfxTelemetry {
  manifestLoaded: boolean;
  runtimeProfile: VfxRuntimeProfile;
  enabledExtensions: string[];
  effectsLoaded: number;
  loadFailures: number;
  fallbackLoads: number;
  spawned: number;
  released: number;
  dropped: number;
  reused: number;
  variantSkippedFieldCount: number;
  softParticleDepthMode: 'provided-depth' | 'disabled-no-depth';
  softParticleSystemsDisabled: number;
  worldSpaceScaleApproximations: number;
  activeInstances: number;
  idleInstances: number;
  allocatedInstances: number;
  particleCount: number;
  activeSystemCount: number;
  batchCount: number;
  effects: Record<string, VfxEffectTelemetry>;
}

export interface VfxHandle {
  readonly id: number;
  readonly effectId: string;
  readonly dropped: boolean;
  readonly released: boolean;
  play(): void;
  stop(): void;
  endEmit(): void;
  setTransform(transform: VfxTransform): void;
  release(): void;
}

export interface VfxRuntime {
  loadManifest(url: string): Promise<void>;
  preload(effectId: string, variant?: VfxVariant): Promise<void>;
  spawn(effectId: string, options?: VfxSpawnOptions): VfxHandle;
  emit(event: VfxEvent): VfxHandle;
  update(delta: number): void;
  release(handle: VfxHandle): void;
  getTelemetry(): VfxTelemetry;
  dispose(): void;
}
