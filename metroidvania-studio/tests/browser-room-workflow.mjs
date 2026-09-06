import { createRequire } from 'node:module';
import { writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { verifyRoomFocus } from './browser-room-focus.mjs';

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
    body: JSON.stringify({ action, ...values, clientId: 'room-workflow', commandId: crypto.randomUUID(),
      expectedRevision: current.revision, expectedInstanceId: current.instanceId }) });
  const result = await response.json(); assert.equal(response.ok, true, JSON.stringify(result)); return result;
}
const template = initial.document.rooms[0];
const cell = (x, y) => ({ x, y, shape: 0, material: 'terrain', groupId: '' });
const makeRoom = (id, x) => ({ ...template, id, name: id, x, y: 0, width: 10, height: 8,
  visible: true, locked: false, foreground: [cell(0, 0), cell(1, 4), cell(2, 4)], background: [], objects: [], properties: [] });
const document = { ...initial.document, name: 'Room workflow', rooms: [makeRoom('A', 0), makeRoom('B', 10)],
  stylegrounds: [], layerGroups: [], properties: [] };
await mkdir(path.join(root, 'Maps'), { recursive: true });
await writeFile(path.join(root, 'Maps/RoomWorkflow.json'), JSON.stringify(document));
await command('open', { path: 'RoomWorkflow.json', discard: true });
await command('selectRoom', { id: 'A' });
await command('options', { tool: 3, layer: 0, brushSize: 1, shape: 0, material: 'terrain', groupId: '', hiddenLayers: [], lockedLayers: [] });
const browser = await chromium.launch({ channel: 'msedge', headless: true });
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
  for (let i = 0; i < 80; i++) { const current = await state(); if (predicate(current)) { await clientAt(current.revision); return current; } await new Promise(resolve => setTimeout(resolve, 75)); }
  throw new Error('Timed out waiting for room operation.');
}
let center = { x: 5, y: 4 };
async function point(x, y, scale = 32) {
  const rect = await page.locator('#map-canvas').boundingBox();
  return { x: rect.x + rect.width / 2 + (x - center.x) * scale, y: rect.y + rect.height / 2 - (y - center.y) * scale };
}
async function click(x, y) { const p = await point(x, y); await page.mouse.click(p.x, p.y); }
async function pixel(p) {
  return page.locator('#map-canvas').evaluate((canvas, p) => {
    const r = canvas.getBoundingClientRect();
    return [...canvas.getContext('2d').getImageData(Math.floor((p.x - r.x) * canvas.width / r.width), Math.floor((p.y - r.y) * canvas.height / r.height), 1, 1).data];
  }, p);
}
async function selectA() {
  await page.locator('#room-list button').filter({ hasText: /^A/ }).click();
  await until(s => s.selection.roomId === 'A'); center = { x: 5, y: 4 }; await paint();
}
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await clientAt((await state()).revision);
  await verifyRoomFocus(page);
  checks.push('inactive terrain, entities, triggers, decals and cached LOD dim; focus restores colors and MiniMap walls remain uniform');
  // The shortcut must also escape an object/All layer where tile Brush cannot paint.
  await command('options', { layer: 3, tool: 1 }); await clientAt((await state()).revision);
  await page.locator('#map-canvas').focus(); await page.keyboard.press('b');
  await until(s => s.selection.tool === 3 && s.selection.layer === 0);
  await command('options', { tool: 0 }); await clientAt((await state()).revision);
  await page.locator('#room-search').pressSequentially('b'); await paint();
  assert.equal((await state()).selection.tool, 0); await page.locator('#room-search').fill('');
  await page.locator('#map-canvas').focus(); await page.keyboard.press('b'); await until(s => s.selection.tool === 3);
  checks.push('B selects a usable tile brush, and typing in inputs is protected');

  const beforeActivation = JSON.stringify((await state()).document);
  const b = await point(13.5, 3.5), bend = await point(17.5, 3.5); requests.length = 0;
  await page.mouse.move(b.x, b.y); await page.mouse.down(); await page.mouse.move(bend.x, bend.y, { steps: 8 }); await page.mouse.up();
  await until(s => s.selection.roomId === 'B');
  assert.equal(JSON.stringify((await state()).document), beforeActivation);
  assert.deepEqual(requests.map(r => r.action), ['selectRoom']);
  await click(13.5, 3.5); await until(s => s.document.rooms[1].foreground.some(c => c.x === 3 && c.y === 3));
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === beforeActivation);
  checks.push('first click/drag only activates another room; the next click paints with one Undo');

  // Room-list and canvas room selection must both support deletion with Undo.
  await selectA(); await click(13.5, 3.5); await until(s => s.selection.roomId === 'B');
  await page.keyboard.press('Delete'); await until(s => s.document.rooms.length === 1);
  assert.equal((await state()).document.rooms[0].id, 'A');
  await page.keyboard.press('Control+z'); await until(s => s.document.rooms.length === 2);
  await page.locator('#room-list button').filter({ hasText: /^B/ }).click(); await until(s => s.selection.roomId === 'B');
  await page.keyboard.press('Delete'); await until(s => s.document.rooms.length === 1);
  await page.keyboard.press('Control+z'); await until(s => s.document.rooms.length === 2);
  checks.push('Delete removes only the clicked room from canvas/list and Undo restores it');

  await selectA(); await page.keyboard.press('Control+a'); await until(s => s.selection.area !== null);
  const selectedTiles = JSON.stringify((await state()).document);
  await page.keyboard.press('Delete'); await until(s => s.document.rooms[0].foreground.length === 0);
  assert.equal((await state()).document.rooms.length, 2, 'Selecting tiles replaces the room deletion target.');
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === selectedTiles);
  checks.push('Delete acts on a tile selection after Ctrl+A without deleting its room');

  await command('roomProperties', { id: 'B', locked: true }); await clientAt((await state()).revision);
  await page.locator('#room-list button').filter({ hasText: /B/ }).click(); await clientAt((await state()).revision);
  const locked = JSON.stringify((await state()).document); await page.keyboard.press('Delete');
  await page.locator('#toast:not([hidden])').waitFor();
  assert.equal(JSON.stringify((await state()).document), locked);
  await command('roomProperties', { id: 'B', locked: false }); await clientAt((await state()).revision);
  checks.push('locked rooms cannot be deleted');

  await selectA();
  const original = JSON.stringify((await state()).document);
  for (const [x, y] of [[-1,-1], [0,-1], [1,-1], [-1,0], [1,0], [-1,1], [0,1], [1,1]]) {
    const handle = await point((x + 1) * 5, (y + 1) * 4); handle.x += x * 9; handle.y -= y * 9;
    await page.mouse.move(handle.x, handle.y); await page.mouse.down();
    await page.mouse.move(handle.x + x * 32, handle.y - y * 32, { steps: 5 }); await page.mouse.up();
    const resized = await until(s => s.document.rooms[0].width === 10 + Math.abs(x) && s.document.rooms[0].height === 8 + Math.abs(y));
    assert.equal(resized.selection.tool, 3);
    const resizedRoom = resized.document.rooms[0];
    assert.deepEqual(resizedRoom.foreground.map(c => ({ ...c, x: c.x + resizedRoom.x, y: c.y + resizedRoom.y })),
      document.rooms[0].foreground, 'Resize preserves existing tiles in world space.');
    if (x === 1) assert.equal(resized.document.rooms[1].x, 11, 'Adjacent room must propagate when expanding the right edge.');
    await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  }
  await click(.5, 4.5); await until(s => s.document.rooms[0].foreground.some(c => c.x === 0 && c.y === 4));
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  checks.push('all eight room handles resize in Brush mode; edge cells remain paintable and neighbors propagate');

  await selectA(); await page.mouse.move(0, 0); await paint();
  await page.screenshot({ path: path.join(repository, '.local/logs/RoomWorkflow-Edit.png') });
  const neighboringTile = await pixel(await point(11.5, 4.5));
  assert.notDeepEqual(neighboringTile, [25,25,25,255]);
  await page.locator('#camera-preview').click(); await paint();
  const cameraScale = Number(await page.locator('#map-canvas').getAttribute('data-camera-scale')) * 16;
  assert.deepEqual(await pixel(await point(11.5, 4.5, cameraScale)), [25,25,25,255], 'Neighbor room must disappear inside the camera frame.');
  assert.notDeepEqual(await pixel(await point(1.5, 4.5, cameraScale)), [25,25,25,255], 'Active room tiles remain visible.');
  await page.screenshot({ path: path.join(repository, '.local/logs/RoomWorkflow-Camera.png') });
  await page.keyboard.press('Escape'); await paint();
  assert.deepEqual(await pixel(await point(11.5, 4.5)), neighboringTile);
  checks.push('Game view hides neighboring room content while edit view restores it');

  // Exercise the production renderer with uniquely identified sprite slots.
  const masks = await page.evaluate(async () => {
    const { MapCanvas } = await import('./map-canvas.js');
    const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;width:320px;height:240px;left:0;top:0'; document.body.append(canvas);
    const map = new MapCanvas(canvas, async () => { throw new Error('Render probe must be read-only'); }, () => {}, () => {});
    const room = { id: 'bounds', x: 0, y: 0, width: 3, height: 3 };
    const index = { rows: new Map() }, result = [];
    map.materials.set('terrain', { solidMasks: new Map(Array.from({ length:256 }, (_,mask) => [mask, { mask }])), fallbackShapes: new Map() });
    let chosen; map.sprite = sprite => { chosen = sprite.mask; return true; };
    for (const layer of [0,1]) for (let y = 0; y < 3; y++) for (let x = 0; x < 3; x++) {
      map.drawTileCell(room, layer, index, { x, y, shape: 0, material: 'terrain' }); result.push(chosen);
    }
    map.dispose(); canvas.remove(); return result;
  });
  assert.deepEqual(masks, [...[112,16,28,64,0,4,193,1,7], ...[112,16,28,64,0,4,193,1,7]]);
  checks.push('foreground/background border masks continue outside all four edges and corners; interior gaps remain open');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ passed: checks.length, checks, errors }, null, 2));
} catch (error) {
  await page.screenshot({ path: path.join(repository, '.local/logs/RoomWorkflow-Failure.png') });
  console.error(JSON.stringify({ checks, errors, requests: requests.slice(-5), selection: (await state()).selection,
    rooms: (await state()).document.rooms.map(r => ({ id: r.id, x:r.x,y:r.y,width:r.width,height:r.height,tiles:r.foreground })),
    status: await page.locator('.statusbar').innerText().catch(() => '') }));
  throw error;
} finally { await browser.close(); await command('open', { path: initial.file, discard: true }); }
