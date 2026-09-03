import { execFile } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const rawSpec = process.argv[2];
if (!rawSpec) {
  throw new Error(
    'Usage: node scripts/verify-package-install.mjs <package-name@version|package.tgz> [three-package-spec]'
  );
}
const packageSpec = rawSpec.endsWith('.tgz') ? resolve(rawSpec) : rawSpec;
const threeSpec = process.argv[3] ?? 'three@0.185.1';
const projectRoot = await mkdtemp(join(tmpdir(), 'unity-particle-quarks-consumer-'));
const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';

try {
  await writeFile(join(projectRoot, 'package.json'), JSON.stringify({
    name: 'unity-particle-quarks-runtime-smoke',
    private: true,
    type: 'module'
  }, null, 2));
  await exec(npm, [
    'install',
    '--ignore-scripts',
    '--no-package-lock',
    '--registry=https://registry.npmjs.org/',
    packageSpec,
    threeSpec
  ], {
    cwd: projectRoot,
    shell: process.platform === 'win32'
  });

  const smokePath = join(projectRoot, 'smoke.mjs');
  await writeFile(smokePath, `
import { createVfxRuntime, validateVfxManifest } from 'unity-particle-quarks-runtime';

if (typeof createVfxRuntime !== 'function' || typeof validateVfxManifest !== 'function') {
  throw new Error('Installed package is missing its public runtime exports.');
}
const manifest = validateVfxManifest({
  schemaVersion: 'unity_particle_quarks_runtime.manifest.v1',
  effects: [{ id: 'install-smoke', status: 'ready', url: './effect.quarks.json' }]
});
if (manifest.effects[0]?.id !== 'install-smoke') {
  throw new Error('Installed manifest validator returned invalid data.');
}
`);
  await exec(process.execPath, [smokePath], { cwd: projectRoot });

  console.log(JSON.stringify({ status: 'passed', packageSpec: rawSpec, threeSpec }, null, 2));
} finally {
  await rm(projectRoot, { recursive: true, force: true });
}
