import { chromium } from 'playwright';
import { createServer } from 'vite';

const server = await createServer({
  root: process.cwd(),
  logLevel: 'error',
  server: { host: '127.0.0.1', port: 0, strictPort: false }
});
let browser;

try {
  await server.listen();
  const baseUrl = server.resolvedUrls?.local[0];
  if (!baseUrl) throw new Error('Vite did not expose a local smoke-test URL.');
  const launchOptions = { headless: true };
  if (process.env.PLAYWRIGHT_BROWSER_PATH) {
    launchOptions.executablePath = process.env.PLAYWRIGHT_BROWSER_PATH;
  } else if (process.platform === 'win32') {
    launchOptions.channel = 'chrome';
  }
  browser = await chromium.launch(launchOptions);
  const page = await browser.newPage();
  const pageErrors = [];
  const imageResponses = [];
  page.on('pageerror', (error) => pageErrors.push(error.stack ?? error.message));
  page.on('response', (response) => {
    if (/\.png(?:$|\?)/i.test(response.url())) imageResponses.push({ url: response.url(), status: response.status() });
  });
  const response = await page.goto(new URL('/scripts/browser-smoke/', baseUrl).href);
  if (!response?.ok()) throw new Error(`Browser smoke page returned HTTP ${response?.status() ?? 'unknown'}.`);
  await page.waitForFunction(() => window.__VFX_SMOKE__?.status !== undefined);
  const result = await page.evaluate(() => window.__VFX_SMOKE__);
  if (result.status !== 'passed' || pageErrors.length > 0 ||
      imageResponses.length === 0 || imageResponses.some((response) => response.status !== 200)) {
    throw new Error(`Browser runtime smoke failed: ${JSON.stringify({ result, pageErrors, imageResponses }, null, 2)}`);
  }
  console.log(JSON.stringify({
    status: result.status,
    effectsLoaded: result.telemetry.effectsLoaded,
    spawned: result.telemetry.spawned,
    imagesLoaded: imageResponses.length,
    pageErrors: pageErrors.length
  }, null, 2));
} finally {
  if (browser) await browser.close();
  await server.close();
}
