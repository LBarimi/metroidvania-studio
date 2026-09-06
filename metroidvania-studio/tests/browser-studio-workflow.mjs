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
  await page.locator('#language').selectOption('EN'); await selectA();
  async function fileMenu(name) { await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', { name, exact: true }).click(); }
  await page.keyboard.press('Alt+h'); await page.getByRole('menuitem', { name: 'About', exact: true }).click();
  await page.locator('.build-version').filter({ hasText: '0.1.0' }).waitFor();
  assert.equal(await page.locator('dialog img').evaluate(img => img.complete && img.naturalWidth > 0), true);
  await page.screenshot({ path: path.join(repository, '.local/logs/Studio-About.png') });
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  checks.push('File/Help menus work with Alt shortcuts; About loads the original icon and actual build version');

  const languages = await page.evaluate(async () => {
    const { Locale, LANGUAGES, detectLanguage } = await import('/locale.js'); const locale = new Locale(); await locale.load();
    const failures = []; const placeholders = value => [...value.matchAll(/\{\d+\}/g)].map(m => m[0]).sort().join(',');
    for (const [key, row] of locale.table) for (const [column] of LANGUAGES) {
      if (!row[column] || placeholders(row[column]) !== placeholders(row.EN)) failures.push(key + ':' + column);
    }
    return { failures, keys: locale.table.size, detected: ['ja-JP','zh-CN','zh-TW','zh-Hant','ru-RU','ko-KR','fr-FR'].map(tag => detectLanguage([tag])) };
  });
  assert.deepEqual(languages.failures, []); assert.ok(languages.keys >= 210);
  assert.deepEqual(languages.detected, ['JA','ZH_CN','ZH_TW','ZH_TW','RU','KR','EN']);
  for (const [language, caption] of [['JA','ファイル'],['ZH_CN','文件'],['ZH_TW','檔案'],['RU','Файл'],['KR','파일'],['EN','File']]) {
    await page.locator('#language').selectOption(language); assert.match(await page.locator('#file-menu-button').innerText(), new RegExp(caption));
    assert.ok((await page.locator('#language').boundingBox()).width >= 140);
    await page.screenshot({ path: path.join(repository, '.local/logs/Studio-' + language + '.png') });
  }
  await page.reload(); await clientAt((await state()).revision); assert.equal(await page.locator('#language').inputValue(), 'EN');
  checks.push('all locale rows and placeholders cover six languages; locale detection, persistence and dropdown width pass');

  await selectA(); const original = JSON.stringify((await state()).document);
  for (const tool of [3,4,5,6,7,8]) {
    await command('options', { tool }); await clientAt((await state()).revision);
    const bar = await point(0, 8); bar.x += 25; bar.y -= 25;
    await page.mouse.move(bar.x, bar.y); await page.mouse.down(); await page.mouse.move(bar.x - 64, bar.y, { steps: 8 }); await page.mouse.up();
    await until(s => s.document.rooms[0].x === -2); assert.equal((await state()).selection.tool, tool);
    await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  }
  // B starts at x=10; moving A one tile right must resolve to x=0, not overlap.
  const bar = await point(0, 8); bar.x += 25; bar.y -= 25;
  await page.mouse.move(bar.x, bar.y); await page.mouse.down(); await page.mouse.move(bar.x + 32, bar.y, { steps: 6 }); await page.mouse.up();
  await clientAt((await state()).revision); assert.equal((await state()).document.rooms[0].x, 0);
  checks.push('all six drawing tools move rooms by their title strip, preserve free gaps and resolve actual collisions');

  await selectA(); await page.locator('[data-scope="room"]').getByRole('button', { name: 'Copy', exact: true }).click(); await clientAt((await state()).revision);
  await page.locator('[data-scope="room"]').getByRole('button', { name: 'Paste', exact: true }).click(); await until(s => s.document.rooms.length === 3);
  const copied = (await state()).document.rooms[2]; assert.notEqual(copied.id, 'A'); assert.equal(copied.foreground.length, 3);
  await page.keyboard.press('Control+z'); await until(s => s.document.rooms.length === 2); await selectA();
  await page.locator('[data-scope="room"]').getByRole('button', { name: 'Flip horizontal', exact: true }).click(); await until(s => s.document.rooms[0].foreground.some(c => c.x === 9 && c.y === 0));
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  await page.locator('[data-scope="room"]').getByRole('button', { name: 'Flip vertical', exact: true }).click(); await until(s => s.document.rooms[0].foreground.some(c => c.x === 0 && c.y === 7));
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  await page.locator('[data-scope="room"]').getByRole('button', { name: '↻ Rotation', exact: true }).click(); await until(s => s.document.rooms[0].width === 8 && s.document.rooms[0].height === 10);
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  await page.locator('[data-scope="room"]').getByRole('button', { name: 'Delete', exact: true }).click(); await until(s => s.document.rooms.length === 1);
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  checks.push('room inspector copy/paste, horizontal/vertical flips, rotation and delete affect whole rooms with Undo');
  assert.deepEqual(await page.locator('.top-actions > .menu > button').allTextContents(), ['File (F)', 'Edit (E)', 'Help (H)']);
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Alt+e');
  assert.equal(await page.locator('#camera-settings-action').isVisible(), true);
  assert.equal(await page.locator('#metadata-action').isVisible(), true);
  await page.locator('#redo').click(); await until(s => s.document.rooms.length === 1);
  await page.locator('#edit-menu-button').click(); await page.locator('#undo').click(); await until(s => JSON.stringify(s.document) === original);
  checks.push('Edit menu contains working Undo/Redo plus camera and map settings; top bar has exactly three menus');


  await selectA(); await page.locator('#tools [data-tool="2"]').click(); await until(s => s.selection.tool === 2);
  const start = await point(.5, 4.5), end = await point(3.5, 5.5); requests.length = 0;
  await page.mouse.move(start.x, start.y); await page.mouse.down();
  const before = await page.locator('#map-canvas').evaluate(c => c.toDataURL());
  await page.mouse.move(end.x, end.y, { steps: 12 }); await paint();
  const during = await page.locator('#map-canvas').evaluate(c => c.toDataURL());
  assert.notEqual(during, before, 'Selection must draw while the pointer is held.');
  assert.equal(requests.length, 0, 'Selection preview must not wait on network commands.');
  await page.mouse.up(); await until(s => s.selection.area?.width === 4 && s.selection.area?.height === 2);
  await page.locator('#selection-summary').waitFor(); assert.match(await page.locator('#canvas-help').innerText(), /select tile area/);
  await page.screenshot({ path: path.join(repository, '.local/logs/Studio-Selection.png') });
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Control+c'); await clientAt((await state()).revision);
  await page.mouse.move((await point(5.5, 2.5)).x, (await point(5.5, 2.5)).y); await page.keyboard.press('Control+v');
  await until(s => s.document.rooms[0].foreground.some(c => c.x === 6 && c.y === 2));
  await page.keyboard.press('Control+z'); await until(s => JSON.stringify(s.document) === original);
  assert.equal(await page.locator('#tools [data-tool="1"]').count(), 0, 'Placement is redundant on tile layers.');
  await page.locator('#layers').getByRole('button', { name: 'Triggers', exact: true }).click(); await until(s => s.selection.layer === 3);
  assert.equal(await page.locator('#tools [data-tool="1"]').count(), 1); assert.equal(await page.locator('#tools [data-tool="4"]').count(), 0);
  await page.locator('#map-canvas').focus(); await page.keyboard.press('l'); await paint(); assert.equal((await state()).selection.tool, 2, 'Hidden drawing tools cannot activate on object layers through shortcuts.');
  checks.push('tile selection is visible before release, sends one command, copies real tiles and shows useful contextual tools');
  const selectionPerformance = await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js'); const source = await (await fetch('/api/state')).json();
    const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;left:0;top:0;width:640px;height:480px'; document.body.append(canvas);
    const map = new MapCanvas(canvas, async () => { throw new Error('Cancelled preview must never submit changes.'); }, () => {}, () => {});
    const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
    try {
      const room = { ...source.document.rooms[0], id: 'dense', x: 0, y: 0, width: 512, height: 512, foreground: Array.from({ length: 262144 }, (_, i) => ({ x:i%512, y:Math.floor(i/512), shape:0, material:'terrain', groupId:'' })), background:[], objects:[] };
      map.setState({ ...source, document:{ ...source.document, rooms:[room] }, documentRevision:9999, selection:{ ...source.selection, roomId:'dense', layer:0, tool:2, area:null, objects:[] } });
      map.center = { x:256, y:256 }; map.pixelScale = 1; await frame();
      let terrainPasses = 0; const drawTiles = map.drawTiles.bind(map); map.drawTiles = (...args) => { terrainPasses++; return drawTiles(...args); };
      const rect = canvas.getBoundingClientRect(); const event = (type, x, y, buttons) => new PointerEvent(type, { pointerId:4, button:0, buttons, clientX:rect.x+x, clientY:rect.y+y, bubbles:true });
      // Programmatic component input cannot own native pointer capture, so keep that browser API out of this render-only probe.
      const capture = canvas.setPointerCapture; canvas.setPointerCapture = () => {};
      map.down(event('pointerdown', 100, 100, 1)); const baseline = terrainPasses; const times = [];
      for (let i=0;i<40;i++) { const start=performance.now(); map.move(event('pointermove', 120+i*8, 180+i*3, 1)); map.draw(); times.push(performance.now()-start); }
      const extraTerrainPasses = terrainPasses-baseline; map.cancel(); canvas.setPointerCapture=capture;
      times.sort((a,b)=>a-b); return { tiles:262144, p95:times[38], max:times[39], extraTerrainPasses };
    } finally { map.dispose(); canvas.remove(); }
  });
  assert.equal(selectionPerformance.extraTerrainPasses, 0, 'Dense selection previews must reuse terrain instead of traversing it.');
  assert.ok(selectionPerformance.p95 < 16.7, JSON.stringify(selectionPerformance));
  checks.push('262144-tile selection preview stays within one frame: ' + JSON.stringify(selectionPerformance));


  await fileMenu('Save all rooms as JSON…'); await page.locator('dialog input').fill('WorkflowExport'); await page.locator('dialog .accent').click(); await page.locator('dialog').waitFor({ state: 'detached' });
  await fileMenu('Save changed rooms as JSON…'); await page.locator('dialog input').fill('WorkflowExport'); await page.locator('dialog .accent').click(); await page.locator('dialog').waitFor({ state: 'detached' });
  const incoming = { ...document, rooms: [{ ...document.rooms[0], id: 'imported-C', name: 'C', x: -30 }] };
  const chooserPromise = page.waitForEvent('filechooser'); await fileMenu('Import room JSON files…'); const chooser = await chooserPromise;
  await chooser.setFiles([{ name: 'C.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(incoming)) }]);
  await until(s => s.document.rooms.length === 3 && s.selection.roomId === 'imported-C');
  await fileMenu('Save selected rooms as JSON…'); await page.locator('dialog input').fill('SelectedRoom'); await page.locator('dialog .accent').click(); await page.locator('dialog').waitFor({ state: 'detached' });
  checks.push('File menu saves all/changed/selected rooms and imports room JSON alongside existing rooms');
  assert.deepEqual(errors, []); console.log(JSON.stringify({ checks, languages, errors }, null, 2));
} catch (error) {
  await page.screenshot({ path: path.join(repository, '.local/logs/Studio-Workflow-Failure.png') });
  console.error(JSON.stringify({ checks, errors, requests: requests.slice(-5), selection: (await state()).selection })); throw error;
} finally { await browser.close(); await command('open', { path: initial.file, discard: true }); }
