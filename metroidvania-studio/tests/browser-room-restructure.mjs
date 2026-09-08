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
    body: JSON.stringify({ action, ...values, clientId: 'room-restructure-test', commandId: crypto.randomUUID(), expectedRevision: current.revision, expectedInstanceId: current.instanceId }),
    signal: AbortSignal.timeout(10000) });
  const result = await response.json(); assert.ok(response.ok, JSON.stringify(result)); return result;
}
const initial = await state(), template = initial.document.rooms[0];
const document = { ...initial.document, name: 'Room editing', properties: [], stylegrounds: [], layerGroups: [],
  rooms: ['A', 'B'].map((id, i) => ({ ...template, id, name: id, x: i * 8, y: 0, width: 8, height: 8, visible: true, locked: false, properties: [],
    foreground: [{ x: 1, y: 1, shape: 0, material: 'terrain', groupId: '' }],
    background: [{ x: 5, y: 4, shape: 1, material: 'terrain', groupId: '' }],
    objects: [{ id: String(100 + i), definition: 'Portal', layer: 2, groupId: '', x: 2, y: 2, width: 1, height: 2,
      rotation: 0, scaleX: 1, scaleY: 1, nodes: [], properties: [{ key: 'desc', value: id + ' portal' }] }] })) };
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, locale: 'ko-KR' });
const errors = []; page.on('pageerror', error => errors.push(error.message));
async function until(predicate) {
  for (let i = 0; i < 100; i++) { const current = await state(); if (predicate(current)) return current; await page.waitForTimeout(50); }
  throw new Error('Room restructuring state did not settle.');
}
async function screen(x, y) {
  return page.evaluate(([x, y]) => {
    const map = window.__roomTestMap, rect = document.querySelector('#map-canvas').getBoundingClientRect();
    return { x: rect.x + rect.width / 2 + (x - map.center.x) * map.scale, y: rect.y + rect.height / 2 - (y - map.center.y) * map.scale };
  }, [x, y]);
}
async function applyDialog() { await page.locator('#room-restructure-dialog .accent').click(); await page.locator('#room-restructure-dialog').waitFor({ state: 'detached' }); }
try {
  await command('import', { document, discard: true }); await command('selectRoom', { id: 'A' }); await command('options', { tool: 3, layer: 0 });
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js'), original = MapCanvas.prototype.requestDraw;
    MapCanvas.prototype.requestDraw = function (...args) { window.__roomTestMap = this; return original.apply(this, args); };
  });
  await page.locator('#room-list button').filter({ hasText: /^A/ }).click();
  await page.waitForFunction(() => !!window.__roomTestMap);
  const before = JSON.stringify((await state()).document);
  assert.ok(await page.locator('#room-merge-action').isDisabled());
  await page.keyboard.down('Control'); await page.locator('#room-list button').filter({ hasText: /^B/ }).click(); await page.keyboard.up('Control');
  await until(s => s.selection.roomIds.length === 2);
  await page.locator('#room-merge-selection').waitFor();
  await page.locator('#room-merge-selection').click(); await page.locator('#room-restructure-dialog').waitFor();
  await page.locator('#room-restructure-dialog .dialog-footer button').first().click();
  assert.equal(JSON.stringify((await state()).document), before, 'Cancelling merge changes nothing');
  await page.locator('#edit-menu-button').click(); await page.locator('#room-merge-action').click(); await applyDialog();
  const merged = await until(s => s.document.rooms.length === 1);
  assert.equal(merged.document.rooms[0].width, 16); assert.equal(merged.selection.roomIds.length, 1);
  assert.deepEqual(merged.document.rooms[0].objects.map(item => item.id).sort(), ['100', '101']);

  await command('options', { tool: 2, layer: 1 });
  await page.waitForFunction(() => window.__roomTestMap.state?.selection.tool === 2);
  const start = await screen(0.5, 0.5), end = await screen(7.5, 7.5);
  await page.mouse.move(start.x, start.y); await page.mouse.down(); await page.mouse.move(end.x, end.y, { steps: 8 }); await page.mouse.up();
  await until(s => s.selection.area?.width === 8 && s.selection.area?.height === 8);
  await page.locator('#room-split-selection').click(); await applyDialog();
  const split = await until(s => s.document.rooms.length === 2);
  assert.equal(split.selection.area, null); assert.ok(split.selection.roomId !== 'B');
  assert.equal(split.document.rooms.reduce((n, room) => n + room.foreground.length, 0), 2);
  assert.equal(split.document.rooms.reduce((n, room) => n + room.background.length, 0), 2);
  assert.deepEqual(split.document.rooms.flatMap(room => room.objects.map(item => item.id)).sort(), ['100', '101']);
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Control+z'); await until(s => s.document.rooms.length === 1);
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === before);
  await page.keyboard.press('Control+y'); await until(s => s.document.rooms.length === 1);
  await page.keyboard.press('Control+y'); await until(s => JSON.stringify(s.document) === JSON.stringify(split.document));

  await command('selectRoom', { id: split.selection.roomId }); await command('options', { tool: 2, layer: 0 });
  await command('selectArea', { x: 0, y: 0, width: 3, height: 3 });
  await page.locator('#room-split-selection').click(); await page.locator('#room-restructure-dialog .accent').click();
  await page.locator('#room-restructure-dialog .modal-error').filter({ hasText: '분할 경계' }).waitFor();
  assert.equal(JSON.stringify((await state()).document), JSON.stringify(split.document), 'Crossing a portal rejects without losing content');
  await page.locator('#room-restructure-dialog .dialog-footer button').first().click();
  assert.deepEqual(errors, []);
  console.log('PASS browser Ctrl-selection, merge menu and inspector, cancellation, drag-selection split, both layers, IDs, Undo/Redo and localized crossing-object rejection');
} finally { await browser.close(); }
