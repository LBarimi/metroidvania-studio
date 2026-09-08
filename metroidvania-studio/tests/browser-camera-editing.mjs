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
    body: JSON.stringify({ action, ...values, clientId: 'camera-editing-test', commandId: crypto.randomUUID(), expectedRevision: current.revision, expectedInstanceId: current.instanceId }), signal: AbortSignal.timeout(10000) });
  const result = await response.json(); assert.ok(response.ok, JSON.stringify(result)); return result;
}
const initial = await state(), template = initial.document.rooms[0];
const room = { ...template, id: 'controls', name: 'Room controls', x: 0, y: 0, width: 24, height: 14,
  visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
const document = { ...initial.document, rooms: [room], properties: [], stylegrounds: [], layerGroups: [] };
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, locale: 'en-US' }); page.setDefaultTimeout(10000);
const errors = [], checks = []; page.on('pageerror', error => errors.push(error.message));
await page.route('**/app.js', async route => {
  const response = await route.fetch(); await route.fulfill({ response, body: await response.text() + '\nwindow.__map = map;' });
});
async function settled() {
  const current = await state();
  await page.waitForFunction(r => window.__map?.state.revision >= r && !window.__map.hasPendingWork, current.revision);
  await page.waitForTimeout(40);
}
async function until(predicate) {
  for (let i = 0; i < 80; i++) { const current = await state(); if (predicate(current)) { await settled(); return current; } await page.waitForTimeout(50); }
  throw new Error('Room gesture did not reach the expected state: ' + JSON.stringify((await state()).document.rooms));
}
async function position(kind) {
  return page.evaluate(kind => {
    const m = window.__map, room = m.state.document.rooms[0], r = m.canvas.getBoundingClientRect();
    const p = kind === 'resize' ? m.roomHandlePoint(room, -1, 0) : kind === 'camera' ? m.toScreen(m.gameCamera.center)
      : (() => { const t = m.roomMoveRect(room); return { x: t.x + 30, y: t.y + 8 }; })();
    return { x: r.x + p.x, y: r.y + p.y, scale: m.scale };
  }, kind);
}
async function drag(kind, dx, dy, gesture) {
  const p = await position(kind); await page.mouse.move(p.x, p.y);
  assert.equal(await page.locator('#map-canvas').evaluate(c => c.style.cursor), kind === 'resize' ? 'ew-resize' : 'grab');
  await page.mouse.down(); await page.mouse.move(p.x + p.scale * dx, p.y - p.scale * dy, { steps: 4 });
  assert.equal(await page.evaluate(() => window.__map.gesture?.kind), gesture);
  await page.mouse.up();
}
try {
  await command('import', { document, discard: true }); await command('selectRoom', { id: room.id });
  await command('cameraSettings', { ppu: 16, referenceWidth: 320, referenceHeight: 180 });
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await page.locator('#language').selectOption('EN');
  await page.waitForFunction(() => window.__map?.state);
  await page.evaluate(() => { const m = window.__map; m.pixelScale = 1; m.center = { x: 12, y: 7 }; m.requestDraw(); });
  await page.locator('#preview-collapse').click();
  const cases = [...[0, 2, 3, 4, 5, 6, 7, 8].map(tool => ({ tool, layer: 0 })), { tool: 1, layer: 2 },
    ...[0, 1, 2, 3, 4, 5, 6].map(layer => ({ tool: 2, layer, camera: true }))];
  for (const entry of cases) {
    await command('options', { tool: entry.tool, layer: entry.layer, hiddenLayers: [], lockedLayers: [] }); await settled();
    await page.evaluate(enabled => window.__map.setGameCameraTool(enabled), !!entry.camera);
    await page.waitForTimeout(40);
    await drag('title', 2, 1, 'room-move'); await until(s => s.document.rooms[0].x === 2 && s.document.rooms[0].y === 1);
    await command('undo'); await settled();
    await drag('resize', -2, 0, 'room-resize'); await until(s => s.document.rooms[0].x === -2 && s.document.rooms[0].width === 26);
    assert.equal(await page.evaluate(() => window.__map.gameCameraTool), !!entry.camera);
    assert.deepEqual((await state()).document.rooms[0].objects, []); assert.deepEqual((await state()).document.rooms[0].foreground, []);
    await command('undo'); await settled();
  }
  checks.push('Every tool and all seven camera-tool layers retain title movement, outer resize handles, cursors and Undo without placing content');

  await page.evaluate(() => { const m = window.__map; m.setGameCameraTool(true); window.__writer = m.writerPending; m.writerPending = () => true; });
  const beforeBusy = (await state()).document;
  for (const kind of ['title', 'resize']) {
    const p = await position(kind); await page.mouse.move(p.x, p.y); await page.mouse.down();
    assert.equal(await page.evaluate(() => window.__map.gesture), null); await page.mouse.up();
  }
  const center = await page.evaluate(() => window.__map.gameCamera.center);
  await drag('camera', 1, 0, 'game-camera');
  assert.ok((await page.evaluate(() => window.__map.gameCamera.center)).x > center.x);
  assert.deepEqual((await state()).document, beforeBusy);
  await page.evaluate(() => { window.__map.writerPending = window.__writer; });
  checks.push('A pending writer blocks room geometry but allows camera movement');

  await command('import', { document: { ...document, rooms: [{ ...room, locked: true }] }, discard: true }); await settled();
  assert.equal(await page.evaluate(() => window.__map.canResizeRoom(window.__map.state.document.rooms[0])), false);
  const locked = (await state()).document;
  const p = await position('title'); await page.mouse.move(p.x, p.y); await page.mouse.down(); await page.mouse.move(p.x + 32, p.y); await page.mouse.up();
  assert.deepEqual((await state()).document, locked);
  checks.push('Locked rooms retain their geometry in camera mode');

  await command('import', { document, discard: true }); await command('options', { tool: 3, layer: 0, material: 'biome-rock' }); await settled();
  await page.evaluate(() => window.__map.setGameCameraTool(false));
  const toggle = page.locator('#always-show-game-camera');
  assert.equal(await page.locator('#view-toolbar > button').nth(2).getAttribute('id'), 'always-show-game-camera');
  await toggle.click(); assert.equal(await toggle.getAttribute('aria-pressed'), 'true');
  await page.waitForTimeout(80);
  const crosshair = await page.evaluate(() => {
    const m = window.__map, p = m.toScreen(m.gameCamera.center);
    return [...m.ctx.getImageData(Math.round(p.x * m.dpr), Math.round(p.y * m.dpr), 1, 1).data];
  });
  assert.deepEqual(crosshair, [255, 255, 255, 255]);
  const camera = await page.evaluate(() => window.__map.gameCamera.center), brush = await position('camera');
  await page.mouse.move(brush.x, brush.y); await page.mouse.down(); await page.mouse.move(brush.x + 32, brush.y, { steps: 4 });
  assert.equal(await page.evaluate(() => window.__map.gesture?.kind), 'paint'); await page.mouse.up();
  await until(s => s.document.rooms[0].foreground.length >= 3);
  assert.deepEqual(await page.evaluate(() => window.__map.gameCamera.center), camera);
  await page.reload(); await page.locator('#room-list button').first().waitFor();
  assert.equal(await toggle.getAttribute('aria-pressed'), 'true');
  if (process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOTS) await page.screenshot({ path: path.join(process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOTS, 'camera-always-visible.png') });
  checks.push('Persistent toolbar toggle draws the frame while brush strokes still paint through it');
  assert.deepEqual(errors, []); console.log(JSON.stringify({ checks, errors }));
} finally { await browser.close(); await command('import', { document: initial.document, discard: true }); }
