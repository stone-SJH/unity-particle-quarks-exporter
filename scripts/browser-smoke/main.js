import { PerspectiveCamera, Scene } from 'three';
import { createVfxRuntime } from 'unity-particle-quarks-runtime';

const status = document.querySelector('#status');
const runtime = createVfxRuntime({
  scene: new Scene(),
  renderer: {},
  camera: new PerspectiveCamera(),
  runtimeProfile: 'extended',
  pool: { prewarm: 0, max: 1 }
});

try {
  await runtime.loadManifest(
    '/packages/particle-quarks-runtime/test/fixtures/exported-interop/runtime-manifest.json'
  );
  await runtime.preload('interop-effect');
  const handle = runtime.spawn('interop-effect');
  runtime.update(1 / 60);
  handle.release();
  const telemetry = runtime.getTelemetry();
  if (handle.dropped || telemetry.effectsLoaded !== 1 || telemetry.loadFailures !== 0) {
    throw new Error(`Unexpected runtime telemetry: ${JSON.stringify(telemetry)}`);
  }
  window.__VFX_SMOKE__ = { status: 'passed', telemetry };
  status.textContent = 'passed';
} catch (error) {
  window.__VFX_SMOKE__ = {
    status: 'failed',
    error: error instanceof Error ? error.stack ?? error.message : String(error)
  };
  status.textContent = 'failed';
} finally {
  runtime.dispose();
}
