import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const workspace = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.');
const relative = path.relative(path.join(repository, 'metroidvania-studio/.local'), workspace);
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state', { signal: AbortSignal.timeout(10000) })).json();
async function command(action, values = {}) {
  const current = await state();
  const response = await fetch(base + '/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'preview-live-paint-test', commandId: crypto.randomUUID(), expectedRevision: current.revision, expectedInstanceId: current.instanceId }), signal: AbortSignal.timeout(10000) });
  const result = await response.json(); assert.ok(response.ok, JSON.stringify(result)); return result;
}
const initial = await state(), template = initial.document.rooms[0];
const room = { ...template, id: 'live', name: 'Live tile preview', x: 0, y: 0, width: 128, height: 80, visible: true, locked: false,
  objects: [], properties: [], background: [], foreground: Array.from({ length: 10240 }, (_, i) => ({ x: i % 128, y: Math.floor(i / 128), material: 'biome-rock', shape: 0, groupId: '' })) };
const document = { ...initial.document, rooms: [room], properties: [], stylegrounds: [], layerGroups: [] };
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
const errors = [], checks = [], measurements = [];
try {
  await command('import', { document, discard: true }); await command('selectRoom', { id: room.id });
  for (const [dpr, resolution] of [[1, 320], [1.25, 320], [2, 320], [1, 1920]]) {
    await command('cameraSettings', { ppu: 16, referenceWidth: resolution, referenceHeight: resolution * 9 / 16 });
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: dpr, locale: 'en-US' });
    page.setDefaultTimeout(10000); page.on('pageerror', error => errors.push(error.message));
    // Repeated pixel readback must not switch the browser's rendering backend
    // partway through comparisons. Input scheduling still uses real pointer events.
    await page.addInitScript(() => {
      const original = HTMLCanvasElement.prototype.getContext;
      HTMLCanvasElement.prototype.getContext = function (kind, options) {
        return original.call(this, kind, this.id === 'game-preview-canvas' && kind === '2d' ? { ...options, willReadFrequently: true } : options);
      };
    });
    await page.route('**/app.js', async route => {
      const response = await route.fetch(); await route.fulfill({ response, body: await response.text() + `
window.__map = map; window.__frames = []; window.__calls = 0;
const originalTile = map.drawTileCell, originalPreview = map.renderGamePreview;
map.drawTileCell = function (...args) { if (this.cameraPreview) window.__calls++; return originalTile.apply(this, args); };
map.renderGamePreview = function (...args) {
  const start = performance.now(); window.__calls = 0;
  const result = originalPreview.apply(this, args);
  window.__frames.push({ ms: performance.now() - start, calls: window.__calls, partial: !!args[2], version: this.previewVersion });
  return result;
};` });
    });
    async function settled() {
      const current = await state(); await page.waitForFunction(r => window.__map?.state.revision >= r && !window.__map.hasPendingWork, current.revision); await page.waitForTimeout(80);
    }
    async function screen(x, y) {
      return page.evaluate(([x,y]) => { const m = window.__map, r = m.canvas.getBoundingClientRect(), p = m.toScreen({ x, y }); return { x: r.x + p.x, y: r.y + p.y }; }, [x,y]);
    }
    async function samePixels(label) {
      const result = await page.evaluate(() => {
        const m = window.__map, canvas = document.querySelector('#game-preview-canvas'), before = canvas.toDataURL();
        const first = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
        m.renderGamePreview(canvas, m.gameCamera.center);
        const last = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
        let count = 0, left = Infinity, top = Infinity, right = 0, bottom = 0; const examples = [];
        for (let i = 0; i < first.length; i += 4) if ([0,1,2,3].some(c => first[i+c] !== last[i+c])) {
          const x = i / 4 % canvas.width, y = Math.floor(i / 4 / canvas.width);
          count++; left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y);
          if (examples.length < 12) examples.push({ x, y, a: [...first.slice(i, i+4)], b: [...last.slice(i, i+4)] });
        }
        return { equal: before === canvas.toDataURL(), count, left, top, right, bottom, examples, frames: window.__frames.slice(-8) };
      });
      assert.ok(result.equal, `Partial preview differs from full render: ${label}, DPI ${dpr}: ${JSON.stringify(result)}`);
    }
    await command('options', { tool: 3, layer: 0, material: 'biome-grassland', brushSize: 1, hiddenLayers: [], lockedLayers: [] });
    await page.goto(base); await page.locator('#room-list button').first().waitFor(); await page.waitForFunction(() => window.__map?.state);
    await page.evaluate(() => { const m = window.__map; m.pixelScale = 1; m.center = { x: 64, y: 40 }; m.moveGameCamera({ x: 64, y: 40 }); m.requestDraw(); });
    await page.waitForTimeout(200);
    for (const [layer, button] of [[0, 'left'], [0, 'right'], [1, 'left']]) {
      await command('options', { tool: 3, layer, material: 'biome-grassland', brushSize: 1 }); await settled();
      const before = (await state()).documentRevision;
      const from = await screen(57.5, 36.5); await page.mouse.move(from.x, from.y); await page.mouse.down({ button });
      await page.evaluate(() => { window.__frames = []; });
      for (let i = 0; i < 15; i++) {
        const p = await screen(58.5 + i, 36.5 + i % 4); await page.mouse.move(p.x, p.y);
        await page.waitForFunction(() => window.__frames.at(-1)?.version === window.__map.previewVersion);
        if (i === 6 || i === 14) await samePixels(`layer ${layer}, ${button}, step ${i}`);
      }
      assert.equal((await state()).documentRevision, before, 'Live rendering must not wait for a commit');
      const frames = await page.evaluate(() => window.__frames.filter(f => f.partial));
      assert.ok(frames.length >= 10, 'Continuous paint must update Preview through the partial render path');
      const full = await page.evaluate(() => {
        const m = window.__map, canvas = document.querySelector('#game-preview-canvas');
        for (let i = 0; i < 5; i++) m.renderGamePreview(canvas, m.gameCamera.center);
        return window.__frames.slice(-5);
      });
      const mean = (values, key) => values.reduce((sum, f) => sum + f[key], 0) / values.length;
      if (layer === 0 && resolution === 320) assert.ok(mean(frames, 'calls') < mean(full, 'calls') * .65, 'Held-stroke preview should avoid drawing the accumulated stroke');
      measurements.push({ dpr, resolution, layer, button, liveMs: +mean(frames, 'ms').toFixed(3), fullMs: +mean(full, 'ms').toFixed(3), liveTiles: +mean(frames, 'calls').toFixed(1), fullTiles: mean(full, 'calls') });
      // Camera movement must discard partial damage, then later samples resume partial updates.
      await page.evaluate(() => window.__map.moveGameCamera({ x: 65, y: 40 })); await page.waitForTimeout(80); await samePixels('camera moved during stroke');
      await page.mouse.up({ button }); await settled(); await samePixels('committed stroke');
      await command('undo'); await settled(); await samePixels('undo');
    }
    // A hidden preview does no rendering, then receives a complete frame when shown.
    await page.locator('#preview-collapse').click(); await page.waitForTimeout(80);
    const count = await page.evaluate(() => window.__frames.length);
    await page.evaluate(() => window.__map.moveGameCamera({ x: 64, y: 40 })); await page.waitForTimeout(80);
    assert.equal(await page.evaluate(() => window.__frames.length), count);
    await page.locator('#preview-collapse').click(); await page.waitForTimeout(80); await samePixels('expanded');
    checks.push(`${resolution}px / DPI ${dpr}: live foreground/background painting and erasing match full renders before commit, after camera changes, Undo and collapsed restore`);
    await page.close();
  }
  assert.deepEqual(errors, []); console.log(JSON.stringify({ checks, measurements, errors }));
} finally { await browser.close(); await command('import', { document: initial.document, discard: true }); }
