import { createRequire } from 'node:module';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const relative = path.relative(path.join(repository, 'metroidvania-studio/.local'), path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.'));
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state')).json();
async function command(action, values = {}) {
  const current = await state();
  const response = await fetch(base + '/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'layer-activation-test', commandId: crypto.randomUUID(),
      expectedRevision: current.revision, expectedInstanceId: current.instanceId }) });
  const next = await response.json(); assert.ok(response.ok, JSON.stringify(next)); return next;
}
const initial = await state();
const room = { ...initial.document.rooms[0], id: 'layer-test', name: 'Layer test', x: 0, y: 0, width: 16, height: 10,
  visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
const fixture = { ...initial.document, rooms: [room], layerGroups: [], stylegrounds: [], properties: [] };
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'en-US' });
page.setDefaultTimeout(10000);
const errors = [], checks = [];
page.on('pageerror', error => errors.push(error.message));
await page.route('**/app.js', async route => {
  const response = await route.fetch();
  await route.fulfill({ response, body: await response.text() + '\nwindow.__map = map; window.__api = api;' });
});
async function settle() {
  await page.evaluate(async () => { await window.__map.settled(); await window.__api.settled(); await window.__api.refresh(false); });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
}
async function options(values) { await command('options', values); await settle(); }
const geometry = value => value.document.rooms.map(({ id, x, y, width, height }) => ({ id, x, y, width, height }));
async function stroke() {
  const points = await page.evaluate(() => {
    const m = window.__map, room = m.state.document.rooms.find(r => r.id === m.state.selection.roomId);
    const rect = m.canvas.getBoundingClientRect();
    return [3.5, 6.5].map(x => { const p = m.toScreen({ x: room.x + x, y: room.y + 4.5 }); return { x: rect.x + p.x, y: rect.y + p.y }; });
  });
  await page.mouse.move(points[0].x, points[0].y); await page.mouse.down();
  try {
    await page.mouse.move(points[1].x, points[1].y, { steps: 8 });
    assert.equal(await page.evaluate(() => window.__map.gesture?.kind), 'paint', 'Room interiors must paint after a layer is selected');
  } finally { await page.mouse.up(); }
  await settle();
}
try {
  await command('import', { document: fixture, discard: true });
  await command('options', { layer: 0, tool: 0, material: 'terrain', brushSize: 1, shape: 0, hiddenLayers: [], lockedLayers: [] });
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await settle();
  await page.locator('#add-room').click();
  await page.locator('#room-add-name').fill('New room');
  await page.locator('#room-add-x').fill('32'); await page.locator('#room-add-y').fill('0');
  await page.locator('dialog .dialog-footer .accent').click(); await page.locator('dialog').waitFor({ state: 'detached' });
  await settle();
  const created = await state(); assert.equal(created.document.rooms.length, 2);
  await page.locator('#layers .layer-select').nth(0).click(); await settle();
  assert.equal((await state()).selection.tool, 3, 'Selecting the tile layer after creating a room must leave the Rooms tool');
  await stroke();
  const painted = await state(); assert.deepEqual(geometry(painted), geometry(created));
  assert.ok(painted.document.rooms.find(r => r.id === painted.selection.roomId).foreground.length >= 4);
  checks.push('Creating a room, clicking its current tile layer and dragging paints tiles without moving the room');

  await command('import', { document: fixture, discard: true }); await settle();
  await page.locator('#room-list button').first().click(); await settle();
  for (const mode of ['rooms', 'selection', 'camera']) for (const layer of [0, 1, 2, 3, 4, 5, 6]) {
    await options({ layer: 0, tool: mode === 'rooms' ? 0 : 2, hiddenLayers: [], lockedLayers: [] });
    if (mode === 'selection') { await command('selectArea', { x: 2, y: 2, width: 2, height: 2 }); await settle(); }
    if (mode === 'camera') { await page.locator('#game-camera-tool').click(); await settle(); }
    const before = await state(), center = await page.evaluate(() => window.__map.gameCamera.center);
    await page.locator('#layers .layer-select').nth(layer).click(); await settle();
    const next = await state(), expected = layer < 2 ? 3 : layer === 6 ? 2 : 1;
    assert.equal(next.selection.layer, layer); assert.equal(next.selection.tool, expected, mode + ' -> layer ' + layer);
    assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'false');
    assert.equal(await page.locator('#tools .tool-button.active').getAttribute('data-tool'), String(expected));
    assert.deepEqual(next.document, before.document, 'Layer selection must not alter map data');
    if (layer !== 6) assert.equal(next.selection.area, null);
    if (layer < 2 || layer === 2 || layer === 3) {
      if (layer === 2) await options({ objectDefinition: 'Portal' });
      await stroke();
      const edited = await state(); assert.deepEqual(geometry(edited), geometry(before));
      assert.deepEqual(await page.evaluate(() => window.__map.gameCamera.center), center);
      const active = edited.document.rooms[0];
      if (layer < 2) assert.ok(active[layer === 0 ? 'foreground' : 'background'].length >= 4);
      else assert.ok(active.objects.some(o => o.layer === layer));
      await command('undo'); await settle(); assert.deepEqual((await state()).document, before.document);
    }
  }
  checks.push('All seven layers exit room/selection/camera tools; editable layers paint or place, All stays selectable, Undo preserves geometry');

  for (const tool of [3, 4, 5, 6, 7, 8]) {
    await options({ layer: 0, tool });
    await page.locator('#layers .layer-select').nth(1).click(); await settle();
    assert.equal((await state()).selection.tool, tool, 'Keep the selected tile drawing tool');
  }
  await options({ layer: 0, tool: 0 });
  await page.locator('#layers .layer-visibility').first().click(); await settle();
  assert.equal((await state()).selection.tool, 0, 'Visibility toggles must not select a drawing tool');
  await page.locator('#layers .layer-visibility').first().click(); await settle();
  await page.locator('#game-camera-tool').click(); await settle();
  await page.locator('#layers .layer-row').first().locator('button').last().click(); await settle();
  assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'true', 'Layer locks must not exit camera mode');
  checks.push('Existing drawing tools remain selected across tile layers; visibility and lock controls do not switch tools');
  assert.deepEqual(errors, []); console.log(JSON.stringify({ checks, errors }));
} finally { await browser.close(); await command('import', { document: initial.document, discard: true }); }
