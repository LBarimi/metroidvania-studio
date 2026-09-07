import { createRequire } from 'node:module';
import { writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const root = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.');
const relative = path.relative(path.join(repository, 'metroidvania-studio/.local'), root);
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const fetchApi = (resource, options = {}) => fetch(base + resource, { ...options, signal: AbortSignal.timeout(10000) });
const state = async () => (await fetchApi('/api/state')).json();
const initial = await state();
async function command(action, values = {}) {
  const current = await state();
  const response = await fetchApi('/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'room-context-menu', commandId: crypto.randomUUID(),
      expectedRevision: current.revision, expectedInstanceId: current.instanceId }) });
  const result = await response.json(); assert.equal(response.ok, true, JSON.stringify(result)); return result;
}
const cell = (x, y) => ({ x, y, shape: 0, material: 'terrain', groupId: '' });
const makeRoom = (id, x) => ({ ...initial.document.rooms[0], id, name: id, x, y: 0, width: 10, height: 8,
  visible: true, locked: false, foreground: [cell(2, 3), cell(3, 3)], background: [], objects: [], properties: [] });
const fixture = { ...initial.document, name: 'Room context menu', rooms: [makeRoom('A', 0), makeRoom('B', 18)],
  stylegrounds: [], layerGroups: [], properties: [] };
await mkdir(path.join(root, 'Maps'), { recursive: true });
await writeFile(path.join(root, 'Maps/RoomContextMenu.json'), JSON.stringify(fixture));
await command('open', { path: 'RoomContextMenu.json', discard: true });
await command('selectRoom', { id: 'A' });
await command('options', { tool: 3, layer: 0, brushSize: 1, shape: 0, material: 'terrain', groupId: '', hiddenLayers: [], lockedLayers: [] });
const browser = await chromium.launch({ channel: 'msedge', headless: true, ignoreDefaultArgs: ['--hide-scrollbars'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: 'ko-KR' });
const errors = [], checks = [], requests = [];
page.on('pageerror', error => errors.push(error.message));
page.on('request', request => { if (request.url().endsWith('/api/command')) requests.push(JSON.parse(request.postData())); });
const paint = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
async function clientAt(revision) {
  await page.waitForFunction(revision => Number(document.querySelector('#status-revision')?.textContent.slice(1)) >= revision
    && !document.querySelector('#status-state')?.classList.contains('working'), revision);
  await paint();
}
async function until(predicate) {
  for (let i = 0; i < 80; i++) {
    const current = await state(); if (predicate(current)) { await clientAt(current.revision); return current; }
    await new Promise(resolve => setTimeout(resolve, 75));
  }
  throw new Error('Timed out waiting for room context operation.');
}
const menu = page.locator('#room-context-menu');
let center = { x: 5, y: 4 };
async function point(x, y) {
  const rect = await page.locator('#map-canvas').boundingBox();
  return { x: rect.x + rect.width / 2 + (x - center.x) * 32, y: rect.y + rect.height / 2 - (y - center.y) * 32 };
}
async function rightAt(x, y) { const p = await point(x, y); await page.mouse.click(p.x, p.y, { button: 'right' }); await menu.waitFor(); return p; }
async function openAt(x, y) {
  await rightAt(x, y);
  assert.equal(await menu.innerText(), '여기에 방 생성');
  await menu.getByRole('menuitem').click(); await page.locator('dialog[open]').waitFor();
}
async function undoAndFrame(before) {
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Control+z');
  await until(s => JSON.stringify(s.document) === before);
  await page.locator('#room-list button').filter({ hasText: /^A/ }).click();
  await until(s => s.selection.roomId === 'A'); center = { x: 5, y: 4 }; await paint();
}
function overlaps(a, b) { return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y; }
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await clientAt((await state()).revision);
  const before = JSON.stringify((await state()).document);
  await openAt(-2.25, -1.25);
  assert.equal(await page.locator('#room-add-x').inputValue(), '-3');
  assert.equal(await page.locator('#room-add-y').inputValue(), '-2');
  assert.equal(await page.locator('#room-add-width').inputValue(), '16');
  assert.equal(await page.locator('#room-add-height').inputValue(), '10');
  await page.locator('#room-add-name').fill('New room'); requests.length = 0;
  await page.locator('dialog .accent').click();
  const created = await until(s => s.document.rooms.length === 3);
  const newRoom = created.document.rooms.find(r => r.name === 'New room');
  assert.equal(created.selection.roomId, newRoom.id);
  assert.deepEqual(created.document.rooms.slice(0, 2), fixture.rooms);
  assert.ok(created.document.rooms.filter(r => r.id !== newRoom.id).every(r => !overlaps(r, newRoom)));
  assert.ok(fixture.rooms.some(r => newRoom.x + newRoom.width === r.x || newRoom.x === r.x + r.width || newRoom.y + newRoom.height === r.y || newRoom.y === r.y + r.height));
  assert.deepEqual(requests.map(r => r.action), ['roomAdd']);
  await undoAndFrame(before);
  checks.push('empty-space menu opens a 16x10 dialog at floored world coordinates; only the new room moves to avoid overlap; one Undo');

  await openAt(-5.2, 9.3);
  await page.locator('#room-add-width').fill('6'); await page.locator('#room-add-height').fill('4');
  await page.locator('dialog .accent').click();
  const free = await until(s => s.document.rooms.length === 3);
  const freeRoom = free.document.rooms.find(r => r.id === free.selection.roomId);
  assert.deepEqual([freeRoom.x, freeRoom.y, freeRoom.width, freeRoom.height], [-6, 9, 6, 4]);
  await undoAndFrame(before);
  checks.push('custom dimensions preserve a free clicked location, including negative coordinates');

  requests.length = 0;
  await rightAt(-2.25, -1.25); await page.keyboard.press('Escape'); assert.equal(await menu.count(), 0);
  await rightAt(-2.25, -1.25); const inside = await point(2.5, 3.5); await page.mouse.click(inside.x, inside.y);
  await paint(); assert.equal(await menu.count(), 0);
  assert.deepEqual(requests, []); assert.equal(JSON.stringify((await state()).document), before);
  await openAt(-2.25, -1.25); await page.keyboard.press('Escape'); await paint();
  assert.equal(await page.locator('dialog[open]').count(), 0);
  assert.equal(JSON.stringify((await state()).document), before);
  checks.push('Escape, dialog cancellation and a dismissing canvas click never modify the map');

  const outside = await point(-2.25, -1.25);
  requests.length = 0;
  await page.mouse.move(outside.x, outside.y); await page.mouse.down({ button: 'right' });
  await page.mouse.move(outside.x + 64, outside.y - 32, { steps: 6 });
  assert.equal(await menu.count(), 0);
  assert.equal(await page.locator('#map-canvas').evaluate(canvas => getComputedStyle(canvas).cursor), 'grabbing');
  await page.mouse.up({ button: 'right' }); await paint();
  center = { x: 3, y: 3 };
  await page.mouse.move(outside.x, outside.y); await paint();
  const movedHover = (await page.locator('#status-coordinates').innerText()).match(/-?\d+/g).map(Number).slice(0, 2);
  assert.deepEqual(movedHover, [-5, -3], 'The view must follow the right drag by its full distance.');
  assert.equal(await menu.count(), 0); assert.deepEqual(requests, []);
  assert.equal(JSON.stringify((await state()).document), before);
  await page.locator('#map-canvas').focus(); await page.keyboard.press('f'); center = { x: 5, y: 4 }; await paint();
  // Minor click jitter still opens the menu, while a completed drag remains a pan even after returning to its start.
  await page.mouse.move(outside.x, outside.y); await page.mouse.down({ button: 'right' });
  await page.mouse.move(outside.x + 2, outside.y + 1); await page.mouse.up({ button: 'right' });
  await menu.waitFor(); await page.keyboard.press('Escape');
  checks.push('empty-space right drag pans immediately without commands or a menu; small click jitter still opens the menu');
  requests.length = 0;
  await page.mouse.move(outside.x, outside.y); await page.mouse.down({ button: 'right' });
  await page.mouse.move(inside.x, inside.y, { steps: 6 }); await page.mouse.move(outside.x, outside.y, { steps: 6 });
  await page.mouse.up({ button: 'right' }); await paint();
  assert.equal(await menu.count(), 0); assert.deepEqual(requests, []);
  assert.equal(JSON.stringify((await state()).document), before);
  await page.mouse.move(inside.x, inside.y); await page.mouse.down({ button: 'right' });
  await page.mouse.move(outside.x, outside.y, { steps: 6 }); await page.mouse.up({ button: 'right' });
  await until(s => !s.document.rooms[0].foreground.some(c => c.x === 2 && c.y === 3));
  assert.equal(await menu.count(), 0); assert.deepEqual(requests.map(r => r.action), ['tileGesture']);
  await undoAndFrame(before);
  checks.push('a right pan returning to its start never opens the menu; erase drags beginning inside remain erasers even when released outside');

  await rightAt(-2.25, -1.25); await page.keyboard.press('Enter'); await page.locator('dialog[open]').waitFor();
  await page.locator('#room-add-width').fill('0'); await page.locator('dialog .accent').click();
  await page.waitForFunction(() => document.querySelector('dialog .modal-error')?.textContent.length > 0);
  assert.equal(JSON.stringify((await state()).document), before); await page.keyboard.press('Escape');
  await page.locator('#add-room').click(); await page.locator('dialog[open]').waitFor();
  assert.equal(await page.locator('#room-add-width').inputValue(), '20');
  assert.equal(await page.locator('#room-add-height').inputValue(), '12'); await page.keyboard.press('Escape');
  checks.push('keyboard menu activation works; invalid size preserves the document; ordinary Add retains its defaults');

  // Measure the actual hovered tile after panning and zooming, without relying on a fixed camera scale.
  await page.mouse.move(outside.x, outside.y); await page.mouse.down({ button: 'middle' });
  await page.mouse.move(outside.x + 64, outside.y - 32, { steps: 4 }); await page.mouse.up({ button: 'middle' }); await paint();
  center = { x: 3, y: 3 }; await rightAt(-2.25, -1.25);
  await menu.getByRole('menuitem').click(); await page.locator('dialog[open]').waitFor();
  assert.equal(await page.locator('#room-add-x').inputValue(), '-3');
  assert.equal(await page.locator('#room-add-y').inputValue(), '-2'); await page.keyboard.press('Escape');
  const p = await point(-2.25, -1.25); await page.mouse.move(p.x, p.y); await page.mouse.wheel(0, 120); await paint();
  await page.mouse.move(p.x + 1, p.y + 1); await paint();
  const hovered = (await page.locator('#status-coordinates').innerText()).match(/-?\d+/g).map(Number).slice(0, 2);
  await page.mouse.click(p.x + 1, p.y + 1, { button: 'right' }); await menu.waitFor();
  await menu.getByRole('menuitem').click(); await page.locator('dialog[open]').waitFor();
  assert.deepEqual([Number(await page.locator('#room-add-x').inputValue()), Number(await page.locator('#room-add-y').inputValue())], hovered);
  await page.keyboard.press('Escape');
  assert.equal(JSON.stringify((await state()).document), before);
  checks.push('middle-button panning and wheel zoom preserve the clicked world coordinate for room creation');

  await page.locator('#language').selectOption('EN');
  const rect = await page.locator('#map-canvas').boundingBox();
  await page.mouse.click(rect.x + rect.width - 3, rect.y + rect.height - 3, { button: 'right' }); await menu.waitFor();
  assert.equal(await menu.innerText(), 'Create room here');
  const bounds = await menu.boundingBox();
  assert.ok(bounds.x >= 0 && bounds.y >= 0 && bounds.x + bounds.width <= 1440 && bounds.y + bounds.height <= 900);
  await page.keyboard.press('Escape');
  await page.locator('#camera-preview').click(); await paint();
  const camera = await page.locator('#map-canvas').boundingBox();
  await page.mouse.click(camera.x + 8, camera.y + 8, { button: 'right' }); await paint();
  assert.equal(await menu.count(), 0); assert.equal(JSON.stringify((await state()).document), before);
  checks.push('English translation, viewport-edge menu clamping and read-only camera preview remain correct');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ passed: checks.length, checks, errors }, null, 2));
} catch (error) {
  await page.screenshot({ path: path.join(root, 'room-context-failure.png') });
  console.error(JSON.stringify({ checks, errors, requests: requests.slice(-5), status: await page.locator('.statusbar').innerText().catch(() => '') }));
  throw error;
} finally { await browser.close(); await command('open', { path: initial.file, discard: true }); }
