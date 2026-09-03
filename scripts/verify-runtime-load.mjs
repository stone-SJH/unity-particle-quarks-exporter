import { readFile, stat } from 'node:fs/promises';
import { dirname, resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { PerspectiveCamera, Scene } from 'three';
import { createVfxRuntime, validateVfxManifest } from 'unity-particle-quarks-runtime';

const options = parseArguments(process.argv.slice(2));
const manifestPath = resolve(options.manifestPath);
const manifestUrl = pathToFileURL(manifestPath).href;
const manifest = validateVfxManifest(JSON.parse(await readFile(manifestPath, 'utf8')));
await verifyReferencedImages(dirname(manifestPath), manifest.effects);
installNodeImageStub();
const runtime = createVfxRuntime({
  scene: new Scene(),
  renderer: {},
  camera: new PerspectiveCamera(),
  runtimeProfile: options.profile,
  allowPartial: options.allowPartial,
  pool: { prewarm: 0, max: Math.max(1, manifest.effects.length) },
  fetch: fileFetch
});

try {
  await runtime.loadManifest(manifestUrl);
  let spawned = 0;
  for (const effect of manifest.effects) {
    if (effect.status === 'partial' && !options.allowPartial) {
      throw new Error(`Effect ${effect.id} is partial; rerun with --allow-partial to verify it.`);
    }
    await runtime.preload(effect.id);
    const handle = runtime.spawn(effect.id);
    if (handle.dropped) throw new Error(`Runtime dropped ${effect.id} during the smoke test.`);
    runtime.update(1 / 60);
    handle.release();
    spawned += 1;
  }

  const telemetry = runtime.getTelemetry();
  if (telemetry.effectsLoaded !== manifest.effects.length || telemetry.loadFailures !== 0) {
    throw new Error(
      `Runtime telemetry mismatch: loaded=${telemetry.effectsLoaded}, failures=${telemetry.loadFailures}.`
    );
  }
  console.log(JSON.stringify({
    status: 'passed',
    manifest: options.manifestPath.replaceAll('\\', '/'),
    profile: options.profile,
    effectsLoaded: telemetry.effectsLoaded,
    spawned
  }, null, 2));
} finally {
  runtime.dispose();
}

function parseArguments(args) {
  let manifestPath;
  let profile = 'extended';
  let allowPartial = false;
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    if (argument === '--profile') {
      profile = args[++index];
    } else if (argument === '--allow-partial') {
      allowPartial = true;
    } else if (argument.startsWith('-')) {
      throw new Error(`Unknown option ${argument}.`);
    } else if (manifestPath === undefined) {
      manifestPath = argument;
    } else {
      throw new Error(`Unexpected argument ${argument}.`);
    }
  }
  if (!manifestPath || (profile !== 'stock' && profile !== 'extended')) {
    throw new Error(
      'Usage: node scripts/verify-runtime-load.mjs <runtime-manifest.json> [--profile stock|extended] [--allow-partial]'
    );
  }
  return { manifestPath, profile, allowPartial };
}

async function fileFetch(input) {
  const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  const url = new URL(raw);
  if (url.protocol !== 'file:') return new Response('unsupported protocol', { status: 400 });
  try {
    return new Response(await readFile(fileURLToPath(url)), { status: 200 });
  } catch {
    return new Response('not found', { status: 404 });
  }
}

async function verifyReferencedImages(root, effects) {
  for (const effect of effects) {
    if (!effect.url) continue;
    const effectPath = resolve(root, ...decodeURIComponent(effect.url.replaceAll('\\', '/')).split('/'));
    if (!effectPath.startsWith(`${root}${sep}`)) throw new Error(`Effect ${effect.id} escapes the manifest root.`);
    const json = JSON.parse(await readFile(effectPath, 'utf8'));
    for (const entry of json.images ?? []) {
      if (typeof entry.url !== 'string' || entry.url.startsWith('data:')) continue;
      const imagePath = resolve(dirname(effectPath), ...decodeURIComponent(entry.url.replaceAll('\\', '/')).split('/'));
      if (!imagePath.startsWith(`${root}${sep}`)) throw new Error(`Image for ${effect.id} escapes the manifest root.`);
      if (!(await stat(imagePath)).isFile()) throw new Error(`Image for ${effect.id} is missing: ${entry.url}`);
    }
  }
}

function installNodeImageStub() {
  if (globalThis.document) return;
  class NodeImage {
    constructor() {
      this.complete = false;
      this.width = 1;
      this.height = 1;
      this.currentSrc = '';
      this.onload = null;
      this.onerror = null;
      this.listeners = new Map();
    }

    addEventListener(type, listener) { this.listeners.set(type, listener); }
    removeEventListener(type) { this.listeners.delete(type); }
    setAttribute() {}
    getAttribute() { return null; }
  }
  globalThis.HTMLImageElement = NodeImage;
  globalThis.document = {
    createElementNS() {
      const image = new NodeImage();
      Object.defineProperty(image, 'src', {
        set(value) {
          image.currentSrc = value;
          image.complete = true;
          queueMicrotask(() => {
            image.listeners.get('load')?.call(image);
            image.onload?.call(image);
          });
        }
      });
      return image;
    }
  };
}
