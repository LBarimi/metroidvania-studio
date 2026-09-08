import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const root = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.');
const relative = path.relative(path.join(repository, 'metroidvania-studio/.local'), root);
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state', { signal: AbortSignal.timeout(10000) })).json();
async function command(action, values = {}) {
  const current = await state();
  const response = await fetch(base + '/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'game-preview-test', commandId: crypto.randomUUID(), expectedRevision: current.revision, expectedInstanceId: current.instanceId }), signal: AbortSignal.timeout(10000) });
  const result = await response.json(); assert.ok(response.ok, JSON.stringify(result)); return result;
}
const initial = await state(), template = initial.document.rooms[0];
const cells = (width, height, material) => Array.from({ length: width * height }, (_, i) => ({ x: i % width, y: Math.floor(i / width), shape: 0, material, groupId: '' }));
const document = { ...initial.document, name: 'Preview', properties: [], stylegrounds: [], layerGroups: [], rooms: [
  { ...template, id: 'preview-a', name: 'Room A', x: 0, y: 0, width: 24, height: 14, visible: true, locked: false, objects: [], background: [], properties: [],
    foreground: cells(24, 14, 'biome-rock').filter(t => t.y < 2 || t.y === 13 || t.x === 0 || t.x === 23 || t.y === 7 && t.x >= 9 && t.x <= 16) },
  { ...template, id: 'preview-b', name: 'Room B', x: 24, y: 0, width: 8, height: 14, visible: true, locked: false, objects: [], background: [], properties: [], foreground: cells(8, 14, 'terrain-blue') }
] };
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, locale: 'en-US' });
page.setDefaultTimeout(10000);
const errors = [], checks = []; page.on('pageerror', error => errors.push(error.message));
const canvas = page.locator('#game-preview-canvas');
async function settled() {
  const current = await state();
  await page.waitForFunction(revision => window.__previewMap?.state.revision >= revision, current.revision);
  await page.waitForTimeout(80);
}
const png = async () => { const value = await canvas.evaluate(c => c.toDataURL()); return (await import('node:crypto')).createHash('sha256').update(value).digest('hex'); };
async function screen(x, y) {
  return page.evaluate(([x, y]) => {
    const map = window.__previewMap, r = document.querySelector('#map-canvas').getBoundingClientRect();
    const p = map.toScreen({ x, y }); return { x: r.x + p.x, y: r.y + p.y };
  }, [x, y]);
}
async function snapshot(name) {
  if (process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOTS, name + '.png') });
}
try {
  await command('import', { document, discard: true }); await command('selectRoom', { id: 'preview-a' });
  await command('options', { tool: 3, layer: 0, material: 'biome-rock' });
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  await page.locator('#language').selectOption('EN');
  await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js');
    const render = MapCanvas.prototype.renderGamePreview, index = MapCanvas.prototype.rebuildDocument;
    window.__previewRenders = 0; window.__previewIndexes = 0; window.__previewTimes = [];
    MapCanvas.prototype.renderGamePreview = function (...args) {
      window.__previewMap = this; window.__previewCenter = { ...args[1] }; window.__previewRenders++; const start = performance.now();
      const result = render.apply(this, args); window.__previewTimes.push(performance.now() - start); return result;
    };
    MapCanvas.prototype.rebuildDocument = function (...args) { window.__previewIndexes++; return index.apply(this, args); };
  });
  // The dock has a fixed width; use a real layout change to observe the renderer.
  await page.locator('#preview-maximize').click();
  await page.waitForFunction(() => window.__previewMap);
  await page.locator('#preview-maximize').click();
  await page.waitForTimeout(80);
  assert.equal(await page.locator('#camera-preview').count(), 0);
  assert.equal(await page.locator('#camera-ppu, #camera-resolution').count(), 2);
  assert.equal(await canvas.getAttribute('data-camera-resolution'), '320x180');
  assert.equal(await page.locator('#game-preview .preview-room').textContent(), 'Room A');
  const mainView = await page.evaluate(() => ({ center: { ...window.__previewMap.center }, pixelScale: window.__previewMap.pixelScale, camera: window.__previewMap.cameraPreview }));
  assert.equal(mainView.camera, false);
  await snapshot('preview-window');
  const before = await png();
  const start = await screen(3.5, 5.5), end = await screen(18.5, 5.5);
  await page.mouse.move(start.x, start.y); await page.mouse.down(); await page.mouse.move(end.x, end.y, { steps: 20 });
  await page.waitForTimeout(100);
  assert.notEqual(await png(), before, 'A held stroke reaches Preview before it is committed.');
  assert.equal(await page.evaluate(() => !!window.__previewMap.tileOverlay?.live), true);
  await page.mouse.up(); await page.waitForFunction(() => !window.__previewMap.hasPendingWork); await settled();
  const painted = await state(); assert.ok(painted.document.rooms[0].foreground.some(t => t.y === 5 && t.x === 18));
  checks.push('Old toolbar button removed; selected-room preview reflects live painting before mouse release');

  await page.locator('#preview-collapse').click();
  assert.equal(await canvas.isVisible(), false);
  const renders = await page.evaluate(() => window.__previewRenders);
  await command('selectRoom', { id: 'preview-b' }); await settled();
  assert.equal(await page.evaluate(() => window.__previewRenders), renders);
  assert.equal(await page.locator('#game-preview .preview-room').textContent(), 'Room B');
  await page.locator('#preview-collapse').click(); await page.waitForTimeout(80);
  assert.notEqual(await png(), before);
  // Room A is adjacent and textured, but remains absent in Room B's camera viewport.
  const outside = await canvas.evaluate(c => {
    const scale = Number(c.dataset.cameraScale), x = Math.round(c.width / 2 - 7 * 16 * scale), y = Math.round(c.height / 2);
    return [...c.getContext('2d').getImageData(x, y, 1, 1).data];
  });
  assert.deepEqual(outside, [25, 25, 25, 255]);
  await command('selectRoom', { id: 'preview-a' }); await settled();
  checks.push('Collapsed rendering pauses; room switches recenter the preview and adjacent rooms stay hidden');

  assert.equal(await page.evaluate(() => {
    const map = window.__previewMap, draw = map.drawScene;
    const fields = ['canvas','ctx','width','height','dpr','center','pixelScale','cameraPreview','renderOrigin','tileLodModes','selectedObjects'];
    const before = fields.map(key => map[key]);
    map.drawScene = () => { throw new Error('Preview test interruption'); };
    try { map.renderGamePreview(document.querySelector('#game-preview-canvas'), { x: 99, y: 99 }); }
    catch (error) { if (error.message !== 'Preview test interruption') throw error; }
    finally { map.drawScene = draw; }
    return fields.every((key,i) => map[key] === before[i]);
  }), true, 'An interrupted Preview pass restores all editor render state.');
  const stableView = await page.evaluate(() => ({ center: { ...window.__previewMap.center }, scale: window.__previewMap.pixelScale, width: window.__previewMap.width }));
  const indexes = await page.evaluate(() => window.__previewIndexes);
  await page.locator('#preview-maximize').click(); await page.waitForTimeout(80);
  assert.equal(await page.locator('#preview-maximize').getAttribute('aria-pressed'), 'true');
  const bounds = await page.locator('#game-preview').boundingBox(), area = await page.locator('.canvas-wrap').boundingBox();
  assert.ok(Math.abs(bounds.width - area.width) < 2 && Math.abs(bounds.height - area.height) < 2);
  assert.deepEqual(await page.evaluate(() => ({ center: { ...window.__previewMap.center }, scale: window.__previewMap.pixelScale, width: window.__previewMap.width })), stableView);
  const priorPan = await png(), r = await canvas.boundingBox();
  await page.mouse.move(r.x + r.width / 2, r.y + r.height / 2); await page.mouse.down();
  await page.mouse.move(r.x + r.width / 2 + 64, r.y + r.height / 2); await page.mouse.up(); await page.waitForTimeout(80);
  assert.notEqual(await png(), priorPan);
  const beforeKeys = JSON.stringify((await state()).document);
  await page.keyboard.press('Delete'); await page.keyboard.press('b'); await page.keyboard.press('Control+z');
  assert.equal(JSON.stringify((await state()).document), beforeKeys);
  await page.keyboard.press('f'); await page.waitForTimeout(80); assert.equal(await png(), priorPan, 'Recentering restores exactly the same pixels.');
  await snapshot('preview-maximized');
  await page.keyboard.press('Escape'); assert.equal(await page.locator('#preview-maximize').getAttribute('aria-pressed'), 'false');
  assert.equal(await page.evaluate(() => window.__previewIndexes), indexes);
  assert.deepEqual(await page.evaluate(() => ({ center: { ...window.__previewMap.center }, scale: window.__previewMap.pixelScale, width: window.__previewMap.width })), stableView);
  checks.push('Maximize, pan, center and Escape preserve the editing viewport and indexes; Preview cannot edit or delete content');

  for (const [resolution, ppu] of [['320x240', '32'], ['2560x1440', '64']]) {
    await page.locator('#camera-ppu').selectOption(ppu); await settled();
    await page.locator('#camera-resolution').selectOption(resolution); await settled();
    assert.equal(await canvas.getAttribute('data-camera-resolution'), resolution);
    const fits = await canvas.evaluate(c => {
      const [w, h] = c.dataset.cameraResolution.split('x').map(Number), scale = Number(c.dataset.cameraScale);
      return w * scale <= c.width && h * scale <= c.height && (Number.isInteger(scale) || Math.abs(1 / scale - Math.round(1 / scale)) < 1e-8);
    });
    assert.ok(fits, resolution + ' fits the full camera frame without stretching or cropping');
  }
  await page.locator('#camera-resolution').selectOption('320x180'); await settled();
  const pixels = await png(); await page.locator('#camera-ppu').selectOption('16'); await settled();
  assert.equal(await png(), pixels, 'PPU changes world units rather than source pixels.');
  await page.locator('#tabs > button').nth(1).click(); assert.equal(await page.locator('#game-preview').isVisible(), false);
  const paused = await page.evaluate(() => window.__previewRenders);
  await page.waitForTimeout(150); assert.equal(await page.evaluate(() => window.__previewRenders), paused);
  await page.locator('#tabs > button').first().click(); await page.waitForTimeout(80);
  checks.push('Resolution and PPU synchronize; high resolutions reduce by reciprocal scales; minimap mode pauses Preview');

  for (const language of ['KR', 'EN', 'JA', 'ZH_CN', 'ZH_TW', 'RU']) {
    await page.locator('#language').selectOption(language);
    assert.notEqual(await page.locator('#preview-collapse').getAttribute('aria-label'), 'previewCollapse');
  }
  await page.locator('#language').selectOption('EN');
  const timings = await page.evaluate(() => window.__previewTimes.slice().sort((a,b) => a-b));
  const p95 = timings[Math.floor(timings.length * .95)];
  assert.ok(p95 < 50, 'Preview rendering exceeds the existing 50 ms input-frame budget: ' + p95);
  const idle = await page.evaluate(() => window.__previewRenders);
  const empty = await screen(5, 11);
  await page.mouse.move(empty.x, empty.y); await page.mouse.move(empty.x + 30, empty.y, { steps: 8 }); await page.waitForTimeout(100);
  assert.equal(await page.evaluate(() => window.__previewRenders), idle, 'An idle cursor does not repaint Preview.');
  for (const dpr of [1.25, 2]) {
    const high = await browser.newPage({ viewport: { width: 1100, height: 760 }, deviceScaleFactor: dpr });
    try {
      await high.goto(base); await high.locator('#room-list button').first().waitFor();
      await high.locator('#preview-maximize').click();
      await high.waitForFunction(() => document.querySelector('#game-preview-canvas')?.dataset.cameraScale);
      const result = await high.locator('#game-preview-canvas').evaluate(c => {
        const r = c.getBoundingClientRect();
        const expected = Math.floor(Math.min(c.width / 320, c.height / 180));
        return { expected, scale: Number(c.dataset.cameraScale), width: c.width, cssWidth: r.width, dpr: devicePixelRatio };
      });
      assert.equal(result.scale, result.expected); assert.equal(result.width, Math.round(result.cssWidth * dpr));
      await high.setViewportSize({ width: 820, height: 580 });
      await high.locator('#preview-maximize').click();
      const bounds = await high.locator('#game-preview').boundingBox(), region = await high.locator('.canvas-wrap').boundingBox();
      assert.ok(bounds.x >= region.x && bounds.x + bounds.width <= region.x + region.width + 1);
    } finally { await high.close(); }
  }
  checks.push('Idle cursors do not redraw; fractional and 2x DPI fit physical pixels; warm Preview render p95 ' + p95.toFixed(2) + ' ms');
  await page.locator('#preview-collapse').click(); await page.reload(); await page.locator('#room-list button').first().waitFor();
  assert.equal(await canvas.isVisible(), false); await page.locator('#preview-collapse').click();
  await snapshot('preview-final');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ checks, errors }));
} finally {
  await browser.close();
  await command('import', { document: initial.document, discard: true });
}
