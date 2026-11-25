import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(['localhost', '127.0.0.1'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state?full=true')).json();
async function command(action, values = {}) {
  const s = await state(), response = await fetch(base + '/api/command', { method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'sample-test', commandId: crypto.randomUUID(), expectedInstanceId: s.instanceId, expectedRevision: s.revision }) });
  assert.ok(response.ok, await response.text());
}

async function verifyExistingRoomGrid(page, sample) {
  await page.locator('#minimap-drawing-tab').click();
  const canvas = page.locator('#minimap-drawing-canvas'), box = await canvas.boundingBox();
  const scale = Math.min((box.width - 90) / (102 + 64), (box.height - 90) / (38 + 64));
  const point = (x, y) => ({ x: box.x + box.width / 2 + (x - 51) * scale, y: box.y + box.height / 2 - (y - 19) * scale });
  const move = (x, y) => { const p = point(x, y); return page.mouse.move(p.x, p.y); };
  const frame = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const ink = (x, y) => canvas.evaluate((canvas, p) => {
    const r = canvas.getBoundingClientRect(), px = Math.round((p.x - r.x) * devicePixelRatio), py = Math.round((p.y - r.y) * devicePixelRatio);
    return canvas.getContext('2d').getImageData(px, py, 1, 1).data[0];
  }, point(x, y));
  const write = async (action, interact) => {
    const response = page.waitForResponse(r => r.url().endsWith('/api/command') && r.request().postDataJSON()?.action === action);
    await interact();
    const result = await response; assert.ok(result.ok(), await result.text());
    const current = await state();
    await page.waitForFunction(rev => Number(document.querySelector('#status-revision').textContent.slice(1)) >= rev, current.revision);
    await frame(); return current;
  };
  await frame();
  // Sample in the empty margin to see the same grid lines that pass through each wall.
  for (const r of sample.document.rooms) {
    for (const x of [r.x, r.x + r.width]) assert.ok(await ink(x, -3), 'Missing vertical room edge at ' + x);
    for (const y of [r.y, r.y + r.height]) assert.ok(await ink(-3, y), 'Missing horizontal room edge at ' + y);
  }
  assert.equal(await ink(71, -3), 0);
  assert.deepEqual((await state()).document, sample.document);
  await page.locator('[data-chip="8x8"]').click();
  await move(102.7, 9); await frame();
  const pixels = () => canvas.evaluate(c => c.toDataURL());
  const preview = await pixels();
  const added = await write('roomAdd', async () => { await page.mouse.down(); await page.mouse.up(); });
  const chip = added.document.rooms.find(r => r.id === added.selection.roomId);
  assert.deepEqual([chip.x, chip.y, chip.width, chip.height], [102, 8, 8, 8]);
  assert.ok(added.connections.some(c => c.vertical && c.coordinate === 102 && c.start === 8 && c.end === 16
    && [c.roomAId, c.roomBId].includes('foundry') && [c.roomAId, c.roomBId].includes(chip.id)));
  assert.ok(preview === await pixels(), 'Preview matches placement against an existing room edge.');
  const undo = () => write('undo', () => page.keyboard.press('Control+z'));
  assert.deepEqual((await undo()).document, sample.document);
  const moved = await write('roomMove', async () => {
    await move(80, 17); await page.mouse.down(); await move(80, 19); await page.mouse.up();
  });
  const foundry = moved.document.rooms.find(r => r.id === 'foundry');
  assert.deepEqual([foundry.x, foundry.y, foundry.width, foundry.height], [70, 10, 32, 22]);
  assert.deepEqual((await undo()).document, sample.document);

  const grids = await page.evaluate(async () => {
    const { roomGrid } = await import('/minimap-drawing.js');
    const a = { x: -5, y: -9, width: 16, height: 8 }, b = { x: 11, y: -1, width: 8, height: 8 };
    return [roomGrid([a, b]), roomGrid([b, a]), roomGrid([{ ...a, width: 13 }]), roomGrid([])];
  });
  assert.deepEqual(grids, [{ x: 3, y: 7, step: 8 }, { x: 3, y: 7, step: 8 }, { x: 0, y: 0, step: 1 }, { x: 0, y: 0, step: 8 }]);
}

const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'en-US' }); page.setDefaultTimeout(12000);
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await page.locator('#language').selectOption('EN');
  const open = async () => { await page.locator('#help-menu-button').click(); await page.getByRole('menuitem', { name: 'Open sample world', exact: true }).click(); };
  const sync = async () => { const s = await state(); await page.waitForFunction(revision => Number(document.querySelector('#status-revision').textContent.slice(1)) >= revision, s.revision); };
  await command('documentProperties', { name: 'Keep my draft' }); await sync();
  await open(); await page.locator('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
  assert.equal((await state()).document.name, 'Keep my draft');
  await open(); await page.locator('dialog .accent').click();
  await page.waitForFunction(async () => (await (await fetch('/api/state?full=true')).json()).document.name === 'Starter World'); await sync();
  await page.waitForFunction(() => document.querySelectorAll('#room-list button').length === 6);
  const sample = await state(); assert.equal(sample.document.rooms.length, 6); assert.equal(sample.connections.length, 6);
  assert.equal(sample.file, null); assert.equal(sample.dirty, true);
  assert.equal(sample.document.rooms.filter(room => room.background.length).length, 6);
  assert.ok(sample.document.rooms.some(room => room.foreground.some(tile => tile.shape > 0)));
  await page.waitForTimeout(1000);
  const output = process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT_DIR;
  if (output) { await mkdir(output, { recursive: true }); await page.screenshot({ path: path.join(output, 'starter-world.png') }); }
  await verifyExistingRoomGrid(page, sample);
  if (output) await page.locator('#minimap-drawing-canvas').screenshot({ path: path.join(output, 'starter-minimap-grid.png') });
  await page.locator('#minimap-tab').click(); await page.locator('#mini-canvas').waitFor({ state: 'visible' }); await page.waitForTimeout(200);
  if (output) await page.screenshot({ path: path.join(output, 'starter-minimap.png') });
  const box = await page.locator('#mini-canvas').boundingBox();
  const scale = Math.min((box.width - 90) / 102, (box.height - 90) / 38);
  await page.mouse.dblclick(box.x + box.width / 2 + (86 - 51) * scale, box.y + box.height / 2);
  await page.locator('#map-canvas').waitFor({ state: 'visible' });
  await page.waitForFunction(async () => (await (await fetch('/api/state')).json()).selection.roomId === 'foundry');
  await page.locator('#room-list button').filter({ hasText: '04 Sky Bridge' }).click(); await sync();
  await page.keyboard.down('Control'); await page.locator('#room-list button').filter({ hasText: '03 Lift Shaft' }).click(); await page.keyboard.up('Control');
  await page.waitForFunction(async () => (await (await fetch('/api/state')).json()).selection.roomIds.length === 2);
  assert.deepEqual((await state()).selection.roomIds.sort(), ['bridge', 'shaft']);
  const background = page.locator('#layers .layer-row').nth(1).locator('.layer-visibility');
  await background.click(); await page.waitForFunction(async () => (await (await fetch('/api/state')).json()).selection.hiddenLayers.includes(1));
  await background.click(); await page.waitForFunction(async () => !(await (await fetch('/api/state')).json()).selection.hiddenLayers.includes(1));
  const doc = await fetch(base + '/docs/sample-world.html'); assert.ok(doc.ok);
  assert.deepEqual(errors, []);
  console.log('PASS: sample opening/cancel, six-room grid alignment, adjacent chip placement, moving and undo, minimap navigation, multiple selection and background visibility.');
} finally { await browser.close(); }
