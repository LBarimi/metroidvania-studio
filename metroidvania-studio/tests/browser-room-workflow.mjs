import { createRequire } from 'node:module';
import { writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { verifyRoomFocus } from './browser-room-focus.mjs';
import { verifyMiniMap } from './browser-minimap.mjs';

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
  await clientAt((await state()).revision);
  await page.locator('#room-list button').filter({ hasText: /^A/ }).click();
  await until(s => s.selection.roomId === 'A'); center = { x: 5, y: 4 }; await paint();
}
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await clientAt((await state()).revision);
  await verifyRoomFocus(page);
  await verifyMiniMap(page);
  checks.push('MiniMap walls and entrances scale together with zoom, remain uniform and do not change map data');
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

  const activationFixture = JSON.parse(beforeActivation);
  activationFixture.rooms[1].background = [cell(1, 4), cell(2, 4)];
  activationFixture.rooms[1].objects = [2, 3].map(layer => ({ id: 'activation-' + layer, layer,
    definition: initial.catalog.objects.find(object => object.layer === layer)?.id || '',
    x: 1, y: 4, width: 1, height: 1, rotation: 0, scaleX: 1, scaleY: 1, groupId: '', properties: [], nodes: [] }));
  for (const layer of [0, 1, 2, 3]) {
    await command('import', { document: activationFixture, discard: true }); await selectA();
    await command('options', { layer, tool: layer < 2 ? 3 : 1 }); await clientAt((await state()).revision);
    for (const drag of [false, true]) {
      await selectA(); const before = await state(); requests.length = 0;
      const start = await point(11.5, 4.5), end = await point(12.5, 4.5);
      await page.mouse.move(start.x, start.y); await page.mouse.down({ button: 'right' });
      await until(s => s.selection.roomId === 'B');
      // Keep holding after the room switch completes: this must not turn into erasing.
      if (drag) await page.mouse.move(end.x, end.y, { steps: 8 });
      await page.mouse.up({ button: 'right' }); await paint();
      const activated = await state();
      assert.deepEqual(activated.document, before.document, 'Right activation must preserve tiles and objects on every layer.');
      assert.deepEqual([activated.documentRevision, activated.canUndo, activated.canRedo],
        [before.documentRevision, before.canUndo, before.canRedo]);
      assert.deepEqual(requests.map(r => r.action), ['selectRoom'], 'An activating right click/drag must send only room selection.');
      assert.equal(await page.locator('#room-context-menu').count(), 0);
    }
    const beforeErase = JSON.stringify((await state()).document), erase = await point(11.5, 4.5);
    await page.mouse.click(erase.x, erase.y, { button: 'right' });
    const after = await until(s => layer < 2
      ? !s.document.rooms[1][layer === 0 ? 'foreground' : 'background'].some(c => c.x === 1 && c.y === 4)
      : !s.document.rooms[1].objects.some(object => object.layer === layer));
    assert.deepEqual(after.document.rooms[0], activationFixture.rooms[0], 'Erasing stays in the active room.');
    await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === beforeErase);
  }
  await command('import', { document: JSON.parse(beforeActivation), discard: true }); await selectA();
  await command('options', { layer: 0, tool: 3 }); await clientAt((await state()).revision);
  checks.push('right click/held drag activates rooms without edits or history changes on both tile layers, entities and triggers; the next right click erases with Undo');

  await selectA();
  const beforeDeselect = JSON.stringify((await state()).document);
  const historyBeforeDeselect = await state();
  async function selectTileArea() {
    await page.locator('#tools button[data-tool="2"]').click(); await until(s => s.selection.tool === 2);
    const start = await point(1.2, 2.2), end = await point(4.2, 5.2);
    await page.mouse.move(start.x, start.y); await page.mouse.down();
    await page.mouse.move(end.x, end.y, { steps: 5 }); await page.mouse.up();
    await until(s => s.selection.area !== null); await page.locator('#selection-summary').waitFor();
  }
  for (const tool of [0, 3, 4, 5, 6, 7, 8]) {
    await selectTileArea();
    await page.locator(`#tools button[data-tool="${tool}"]`).click();
    await until(s => s.selection.tool === tool && s.selection.area === null);
    await page.locator('#selection-summary').waitFor({ state: 'detached' });
  }
  await selectTileArea(); await page.locator('#map-canvas').focus(); await page.keyboard.press('b');
  await until(s => s.selection.tool === 3 && s.selection.area === null);
  await selectTileArea();
  // Escape used to close a dialog must not also dismiss the underlying selection.
  await page.locator('#add-room').click(); await page.locator('dialog[open]').waitFor(); await page.keyboard.press('Escape');
  await page.locator('dialog[open]').waitFor({ state: 'detached' }); assert.ok((await state()).selection.area);
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Escape');
  await until(s => s.selection.tool === 2 && s.selection.area === null);
  await page.locator('#selection-summary').waitFor({ state: 'detached' });
  await page.keyboard.press('Delete'); await paint();
  assert.equal(JSON.stringify((await state()).document), beforeDeselect, 'Delete after deselection must not remove tiles or the active room.');
  await selectTileArea();
  const selectionStart = await point(2.2, 3.2), selectionEnd = await point(3.2, 4.2);
  await page.mouse.move(selectionStart.x, selectionStart.y); await page.mouse.down();
  await page.mouse.move(selectionEnd.x, selectionEnd.y, { steps: 4 }); await page.keyboard.press('Escape'); await page.mouse.up();
  const dismissed = await until(s => s.selection.area === null);
  assert.equal(JSON.stringify(dismissed.document), beforeDeselect);
  assert.deepEqual([dismissed.canUndo, dismissed.canRedo, dismissed.documentRevision],
    [historyBeforeDeselect.canUndo, historyBeforeDeselect.canRedo, historyBeforeDeselect.documentRevision]);
  await page.keyboard.press('b'); await until(s => s.selection.tool === 3);
  checks.push('switching tools by button or shortcut clears the tile area; Escape clears completed and dragging selections without editing data/history or leaking through dialogs');

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

  for (const layer of [0, 1, 2, 3, 4, 5, 6]) {
    await command('options', { layer, tool: layer < 2 ? 3 : 1 }); await clientAt((await state()).revision);
    const handle = await point(0, 4); handle.x -= 9;
    await page.mouse.move(handle.x, handle.y); await page.mouse.down();
    await page.mouse.move(handle.x - 32, handle.y, { steps: 5 }); await page.mouse.up();
    await until(s => s.document.rooms[0].width === 11);
    assert.equal((await state()).selection.layer, layer);
    await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
    const title = await point(0, 8); title.x += 32; title.y -= 26;
    await page.mouse.move(title.x, title.y); await page.mouse.down();
    await page.mouse.move(title.x - 32, title.y, { steps: 5 }); await page.mouse.up();
    await until(s => s.document.rooms[0].x === -1);
    assert.equal((await state()).selection.layer, layer);
    await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  }
  checks.push('room title movement and resize handles work on all seven layer choices without placing content');
  await command('options', { tool: 3, layer: 0 }); await clientAt((await state()).revision);
  const separated = JSON.parse(original);
  separated.rooms[1].x = 15; separated.rooms[1].locked = true;
  await command('import', { document: separated, discard: true }); await selectA();
  const beforeSeparatedResize = JSON.stringify((await state()).document);
  for (const delta of [1, -1]) {
    const handle = await point(10, 4); handle.x += 9;
    await page.mouse.move(handle.x, handle.y); await page.mouse.down();
    await page.mouse.move(handle.x + delta * 32, handle.y, { steps: 5 }); await page.mouse.up();
    const resized = await until(s => s.document.rooms[0].width === 10 + delta);
    assert.deepEqual(resized.document.rooms[1], separated.rooms[1], 'A detached locked room must stay unchanged when expanding or shrinking by a handle.');
    await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === beforeSeparatedResize);
  }
  const blockedHandle = await point(10, 4); blockedHandle.x += 9;
  await page.mouse.move(blockedHandle.x, blockedHandle.y); await page.mouse.down();
  await page.mouse.move(blockedHandle.x + 7 * 32, blockedHandle.y, { steps: 6 });
  const blockedResponse = page.waitForResponse(response => response.url().endsWith('/api/command')
    && response.request().postDataJSON()?.action === 'roomResize');
  await page.mouse.up(); assert.equal((await blockedResponse).ok(), false);
  await page.locator('#toast:not([hidden])').waitFor();
  assert.equal(JSON.stringify((await state()).document), beforeSeparatedResize, 'Overlapping a detached room must reject the gesture without moving either room.');
  checks.push('resize handles leave detached locked rooms fixed and reject overlap without partial changes');

  const inset = JSON.parse(original);
  inset.rooms[0].foreground = [cell(2, 2), cell(4, 4)];
  inset.rooms[0].background = [cell(6, 6)];
  await command('import', { document: inset, discard: true }); await selectA();
  const beforeShrink = JSON.stringify((await state()).document);
  for (const [x, y] of [[-1,-1], [0,-1], [1,-1], [-1,0], [1,0], [-1,1], [0,1], [1,1]]) {
    const handle = await point((x + 1) * 5, (y + 1) * 4); handle.x += x * 9; handle.y -= y * 9;
    requests.length = 0;
    await page.mouse.move(handle.x, handle.y); await page.mouse.down();
    await page.mouse.move(handle.x - x * 32 * 8, handle.y + y * 32 * 8, { steps: 8 }); await page.mouse.up();
    const expected = { x: x < 0 ? 2 : 0, y: y < 0 ? 2 : 0,
      width: x < 0 ? 8 : x > 0 ? 7 : 10, height: y < 0 ? 6 : y > 0 ? 7 : 8 };
    const resized = await until(s => Object.entries(expected).every(([key, value]) => s.document.rooms[0][key] === value));
    const sent = requests.find(r => r.action === 'roomResize');
    assert.ok(sent, 'A handle drag must reach the resize command.');
    for (const [key, value] of Object.entries(expected)) assert.equal(sent[key], value, 'Preview and submitted edges must already stop at terrain.');
    const room = resized.document.rooms[0];
    for (const layer of ['foreground', 'background']) assert.deepEqual(room[layer].map(c => ({ ...c, x: c.x + room.x, y: c.y + room.y })), inset.rooms[0][layer]);
    assert.equal(resized.document.rooms[1].x, x > 0 ? 7 : 10, 'Neighbors follow the actual clamped edge.');
    await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === beforeShrink);
  }
  checks.push('all eight shrink handles stop tightly on foreground/background tiles, preserve world contents and undo with neighbors');
  await command('import', { document: JSON.parse(original), discard: true }); await selectA();


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
