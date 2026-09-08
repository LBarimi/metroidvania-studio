import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const root = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.');
const relative = path.relative(path.join(repository, 'metroidvania-studio/.local'), root);
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1'); assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state', { signal: AbortSignal.timeout(10000) })).json();
async function command(action, values = {}) {
  const current = await state();
  const response = await fetch(base + '/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'game-camera-test', commandId: crypto.randomUUID(), expectedRevision: current.revision, expectedInstanceId: current.instanceId }), signal: AbortSignal.timeout(10000) });
  const result = await response.json(); assert.ok(response.ok, JSON.stringify(result)); return result;
}
const initial = await state(), template = initial.document.rooms[0];
const document = { ...initial.document, name: 'Camera views', properties: [], stylegrounds: [], layerGroups: [], rooms: [
  { ...template, id: 'large', name: 'Large room', x: 0, y: 0, width: 40, height: 24, visible: true, locked: false, objects: [], background: [], properties: [],
    foreground: Array.from({ length: 960 }, (_, i) => ({ x: i % 40, y: Math.floor(i / 40), shape: 0, material: 'biome-rock', groupId: '' }))
      .filter(t => t.y < 2 || t.y === 23 || t.x === 0 || t.x === 39 || t.y === 8 && t.x >= 9 && t.x <= 23 || t.y === 16 && t.x >= 25) },
  { ...template, id: 'small', name: 'Small room', x: 45, y: 0, width: 12, height: 8, visible: true, locked: true, objects: [], background: [], properties: [], foreground: [] },
  { ...template, id: 'tall', name: 'Tall room', x: 60, y: 0, width: 12, height: 30, visible: true, locked: false, objects: [], background: [], properties: [], foreground: [] }
] };
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, locale: 'en-US' }); page.setDefaultTimeout(10000);
const errors = [], checks = []; page.on('pageerror', error => errors.push(error.message));
const commands = [];
page.on('request', request => { if (request.url().endsWith('/api/command')) commands.push(JSON.parse(request.postData())); });
const preview = page.locator('#game-preview-canvas');
const pixels = async () => createHash('sha256').update(await preview.evaluate(c => c.toDataURL())).digest('hex');
async function settled() { const current = await state(); await page.waitForFunction(r => window.__cameraMap?.state.revision >= r, current.revision); await page.waitForTimeout(80); }
const position = () => page.evaluate(() => window.__cameraMap.gameCamera.center);
const frame = () => page.evaluate(() => window.__cameraMap.gameCamera.frame);
async function screen(point) {
  return page.evaluate(point => { const map = window.__cameraMap, rect = document.querySelector('#map-canvas').getBoundingClientRect(), p = map.toScreen(point); return { x: rect.x + p.x, y: rect.y + p.y }; }, point);
}
async function drag(from, to, held = false) {
  const a = await screen(from), b = await screen(to); await page.mouse.move(a.x, a.y); await page.mouse.down();
  await page.mouse.move(b.x, b.y, { steps: 12 }); if (!held) await page.mouse.up(); await page.waitForTimeout(100);
}
async function snapshot(name) { if (process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOTS, name + '.png') }); }
try {
  await command('import', { document, discard: true }); await command('selectRoom', { id: 'large' }); await command('options', { tool: 3, layer: 0 });
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await page.locator('#language').selectOption('EN');
  await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js'), original = MapCanvas.prototype.renderGamePreview;
    MapCanvas.prototype.renderGamePreview = function (...args) { window.__cameraMap = this; window.__previewPosition = args[1]; return original.apply(this,args); };
  });
  // The dock has a fixed width; use a real layout change to observe the renderer.
  await page.locator('#preview-maximize').click();
  await page.waitForFunction(() => window.__cameraMap);
  await page.locator('#preview-maximize').click();
  await page.waitForTimeout(80);
  await page.locator('#preview-collapse').click();
  const original = await state(); await page.locator('#game-camera-tool').click();
  assert.equal(await preview.isVisible(), true); assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'true');
  assert.equal(await page.locator('#tools [data-tool].active').count(), 0);
  assert.deepEqual(await frame(), { x: 10, y: 6.375, width: 20, height: 11.25 });
  const cameraCenter = await screen(await position()); await page.mouse.move(cameraCenter.x, cameraCenter.y);
  assert.equal(await page.locator('#map-canvas').evaluate(c => c.style.cursor), 'grab');
  const cross = await page.evaluate(() => {
    const map = window.__cameraMap, p = map.toScreen(map.gameCamera.center), canvas = document.querySelector('#map-canvas');
    return [...canvas.getContext('2d').getImageData(Math.round(p.x * devicePixelRatio), Math.round(p.y * devicePixelRatio), 1, 1).data];
  });
  assert.deepEqual(cross, [255,255,255,255]);
  const beforePixels = await pixels(); await drag({ x: 20, y: 12 }, { x: 24, y: 15 }, true);
  assert.equal(await page.locator('#map-canvas').evaluate(c => c.style.cursor), 'grabbing');
  assert.deepEqual(await position(), { x: 24, y: 15 }); assert.notEqual(await pixels(), beforePixels);
  assert.deepEqual(await page.evaluate(() => window.__previewPosition), await position());
  await page.mouse.up();
  // Export status may advance the general revision without editing the document.
  const afterCameraDrag = await state();
  assert.equal(afterCameraDrag.documentRevision, original.documentRevision);
  assert.deepEqual(afterCameraDrag.document, original.document);
  assert.deepEqual(commands, []);
  await snapshot('game-camera-tool');
  checks.push('Game camera opens Preview, shows a white frame/crosshair and hand cursor, and updates Preview during the held drag without commands');

  const editCenter = await page.evaluate(() => ({ ...window.__cameraMap.center }));
  for (const target of [{ x: 500, y: 500 }, { x: -500, y: -500 }]) {
    await drag(await position(), target); const current = await frame();
    assert.ok(current.x >= 0 && current.y >= 0 && current.x + current.width <= 40 && current.y + current.height <= 24);
  }
  await page.locator('#preview-center').click();
  const r = await preview.boundingBox(), beforeReverse = await position();
  await page.mouse.move(r.x + r.width / 2, r.y + r.height / 2); await page.mouse.down(); await page.mouse.move(r.x + r.width / 2 + 32, r.y + r.height / 2); await page.mouse.up();
  assert.ok((await position()).x < beforeReverse.x); assert.deepEqual(await page.evaluate(() => ({ ...window.__cameraMap.center })), editCenter);
  await page.locator('#map-canvas').focus();
  for (const key of ['Delete', 'Backspace', 'Control+z', 'Control+x', 'Control+v']) await page.keyboard.press(key);
  assert.deepEqual((await state()).document, original.document);
  checks.push('Frame clamps to all room boundaries; Preview dragging moves the same camera; camera keys cannot delete or change map contents');

  for (const layer of [1, 2, 3, 4, 5, 6, 0]) {
    await page.locator('#layers .layer-select').nth(layer).click(); await settled();
    assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'false');
    await page.locator('#game-camera-tool').click();
    assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'true');
    const from = await position(); await drag(from, { x: from.x + .5, y: from.y });
    assert.ok((await position()).x > from.x);
  }
  await page.locator('#map-canvas').focus(); await page.keyboard.press('b'); await settled();
  assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'false');
  assert.equal(await page.locator('#tools [data-tool="3"]').evaluate(b => b.classList.contains('active')), true);
  await page.locator('#game-camera-tool').click(); await page.keyboard.press('Escape');
  assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'false');
  await page.locator('#game-camera-tool').click();
  checks.push('Camera tool stays usable on every layer; B and Escape leave camera mode and restore normal tools');

  await page.locator('#camera-ppu').selectOption('32'); await settled(); assert.equal((await frame()).width, 20);
  await page.locator('#camera-resolution').selectOption('640x360'); await settled();
  assert.equal((await frame()).width, 40); assert.equal((await frame()).height, 22.5); assert.equal((await position()).x, 20);
  await page.locator('#camera-resolution').selectOption('320x180'); await settled();
  await page.locator('#room-list button').filter({ hasText: /Small room/ }).click(); await settled();
  const smallPosition = await position(); await drag(smallPosition, { x: smallPosition.x + 10, y: smallPosition.y + 10 });
  assert.deepEqual(await position(), smallPosition);
  assert.deepEqual(await page.evaluate(() => window.__cameraMap.gameCamera.visibleFrame), { x: 45, y: 0, width: 12, height: 8 });
  assert.equal(await preview.getAttribute('data-camera-resolution'), '320x180');
  await snapshot('game-camera-small-room');
  await page.locator('#room-list button').filter({ hasText: /^Tall room/ }).click(); await settled();
  const tallPosition = await position(); await drag(tallPosition, { x: tallPosition.x + 5, y: tallPosition.y + 5 });
  assert.equal((await position()).x, tallPosition.x); assert.ok((await position()).y > tallPosition.y);
  checks.push('Camera dimensions follow resolution and preserve PPU framing; undersized axes lock independently, including locked rooms');

  await page.locator('#room-list button').filter({ hasText: /^Large room/ }).click(); await settled();
  const center = await position(); await drag(center, { x: center.x + 2, y: center.y + 1 }, true);
  await command('cameraSettings', { ppu: 16, referenceWidth: 640, referenceHeight: 360 }); await settled();
  assert.equal(await page.evaluate(() => window.__cameraMap.interacting), false); await page.mouse.up();
  assert.equal((await position()).x, 20); assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'true');
  const previewBounds = await preview.boundingBox();
  await page.mouse.move(previewBounds.x + previewBounds.width / 2, previewBounds.y + previewBounds.height / 2);
  await page.mouse.down(); await page.mouse.move(previewBounds.x + previewBounds.width / 2 + 5, previewBounds.y + previewBounds.height / 2 + 5);
  await command('cameraSettings', { ppu: 16, referenceWidth: 320, referenceHeight: 180 }); await settled();
  assert.equal(await preview.evaluate(c => c.classList.contains('panning')), false); await page.mouse.up();
  await page.evaluate(() => window.__cameraMap.fit()); await page.waitForTimeout(80);
  const smallCenter = await screen({ x: 51, y: 4 }); await page.mouse.click(smallCenter.x, smallCenter.y); await settled();
  assert.equal((await state()).selection.roomId, 'small');
  assert.deepEqual(await position(), { x: 51, y: 4 });
  checks.push('Resolution changes cancel either drag safely; clicking a different room in the canvas selects it without painting');
  for (const language of ['KR','EN','JA','ZH_CN','ZH_TW','RU']) {
    await page.locator('#language').selectOption(language);
    assert.ok(!(await page.locator('#game-camera-tool').textContent()).includes('gameCameraTool'));
  }
  assert.deepEqual((await state()).document.rooms, original.document.rooms); assert.deepEqual(errors, []);
  console.log(JSON.stringify({ checks, errors }));
} finally { await browser.close(); await command('import', { document: initial.document, discard: true }); }
