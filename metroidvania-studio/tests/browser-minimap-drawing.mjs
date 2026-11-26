import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { verifyMiniMap } from './browser-minimap.mjs';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
const browser = await chromium.launch({ channel: process.env.METROIDVANIA_STUDIO_BROWSER_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined), headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 950 }, locale: 'en-US' });
const page = await context.newPage();
page.setDefaultTimeout(12000);
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const state = async () => (await fetch(base + '/api/state')).json();
const frame = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));

try {
  await page.goto(base);
  await page.locator('#room-list button').first().waitFor();
  await verifyMiniMap(page);
  const before = await state();
  const edit = page.locator('#minimap-drawing-tab'), view = page.locator('#minimap-tab');
  const canvas = page.locator('#minimap-drawing-canvas');
  assert.equal(await edit.textContent(), 'Edit minimap');
  assert.equal(await view.textContent(), 'View minimap');
  await edit.click(); await frame();
  assert.equal(await page.locator('.sidebar').isVisible(), false);
  const chips = await page.locator('.minimap-chip-list button').evaluateAll(nodes => nodes.map(node => node.dataset.chip));
  assert.equal(new Set(chips).size, 34);
  assert.ok(chips.every(id => id.split('x').every(n => Number(n) > 0 && Number(n) % 8 === 0)));
  assert.ok(await page.locator('#minimap-room-color').isVisible(), 'Existing map rooms are editable on entry.');

  // The editor frames the shared world with space around it for new rooms.
  async function coordinates(rooms) {
    const box = await canvas.boundingBox();
    const left = Math.min(...rooms.map(r => r.x)) - 32, right = Math.max(...rooms.map(r => r.x + r.width)) + 32;
    const bottom = Math.min(...rooms.map(r => r.y)) - 32, top = Math.max(...rooms.map(r => r.y + r.height)) + 32;
    const scale = Math.min(80, (box.width - 90) / (right - left), (box.height - 90) / (top - bottom));
    return (x, y) => ({ x: box.x + box.width / 2 + (x - (left + right) / 2) * scale,
      y: box.y + box.height / 2 - (y - (bottom + top) / 2) * scale });
  }
  const write = async (action, interact) => {
    const response = page.waitForResponse(r => r.url().endsWith('/api/command') && r.request().postDataJSON()?.action === action);
    await interact();
    const r = await response; assert.ok(r.ok(), await r.text());
    const current = await state();
    await page.waitForFunction(rev => Number(document.querySelector('#status-revision')?.textContent.slice(1)) >= rev
      && !document.querySelector('#status-state').classList.contains('working'), current.revision);
    await frame();
    return current;
  };
  let at = await coordinates(before.document.rooms);
  const x = Math.floor(Math.min(...before.document.rooms.map(r => r.x)) / 8) * 8 - 8;
  const y = Math.ceil(Math.max(...before.document.rooms.map(r => r.y + r.height)) / 8) * 8;
  const clickAt = (x, y, button = 'left') => {
    const p = at(x + .35, y + .35); return page.mouse.click(p.x, p.y, { button });
  };
  const pixels = () => canvas.evaluate(c => c.toDataURL());
  const hoverAt = async (x, y) => { const p = at(x, y); await page.mouse.move(p.x, p.y); await frame(); };
  const undo = async () => {
    await page.locator('#edit-menu-button').click();
    return write('undo', () => page.locator('#undo').click());
  };
  const picker = page.locator('#minimap-drawing-color'), presets = page.locator('#minimap-drawing-color-preset');
  const region = before.catalog.materials.find(m => /^#[0-9a-f]{6}$/i.test(m.color)).color.toLowerCase();
  await presets.selectOption(region);
  assert.equal(await picker.inputValue(), region);
  await picker.fill('#3478ab');
  assert.equal(await presets.inputValue(), '');
  await page.locator('.minimap-drawing-panel').getByRole('button', {name:'Save preset'}).first().click();
  const dialog = page.locator('dialog[open]');
  await dialog.getByLabel('Name').fill('Flooded halls');
  await dialog.getByRole('button', { name:'Save preset', exact:true }).click();
  assert.equal(await presets.inputValue(), '#3478ab');
  const freshPage = await context.newPage();
  try {
    await freshPage.goto(base); await freshPage.locator('#minimap-drawing-tab').click();
    await freshPage.locator('#minimap-drawing-color-preset').selectOption({ label: 'Flooded halls' });
    assert.equal(await freshPage.locator('#minimap-drawing-color').inputValue(), '#3478ab');
  } finally { await freshPage.close(); }

  const gridPoint = at(x, y + 4), cellPoint = at(x + 4, y + 4);
  const sample = point => canvas.evaluate((c, p) => {
    const box = c.getBoundingClientRect(), px = Math.round((p.x - box.x) * c.width / box.width);
    const py = Math.round((p.y - box.y) * c.height / box.height);
    return c.getContext('2d').getImageData(px, py, 1, 1).data[0];
  }, point);
  assert.equal(await sample(gridPoint), 0x32);
  assert.equal(await sample(cellPoint), 0);

  // The cursor keeps its world position across zoom, including in a negative grid cell.
  const bottom = Math.floor(Math.min(...before.document.rooms.map(r => r.y)) / 8) * 8 - 16;
  await hoverAt(x + 5, bottom + 3);
  await page.mouse.wheel(0, -120); await frame();
  const zoomPreview = await pixels();
  const zoomRoom = await write('roomAdd', async () => { await page.mouse.down(); await page.mouse.up(); });
  const negativeRoom = zoomRoom.document.rooms.find(room => room.id === zoomRoom.selection.roomId);
  assert.deepEqual([negativeRoom.x, negativeRoom.y], [x, bottom]);
  assert.ok(zoomPreview === await pixels());
  await undo(); await view.click(); await edit.click(); await frame();

  // Clicking anywhere inside the first grid cell places exactly one room even if the mouse is dragged.
  for (const id of chips) {
    const [w, h] = id.split('x').map(Number);
    await page.locator('[data-chip="' + w + 'x' + h + '"]').click();
    await hoverAt(x + 3.4, y + 2.6);
    const preview = await pixels();
    const added = await write('roomAdd', async () => {
      const q = at(x + 7, y + 4);
      await page.mouse.down(); await page.mouse.move(q.x, q.y, { steps: 6 }); await page.mouse.up();
    });
    assert.ok(preview === await pixels(), 'The preview and committed canvas must have identical pixels for ' + w + 'x' + h);
    const room = added.document.rooms.find(r => r.id === added.selection.roomId);
    assert.deepEqual([room.x,room.y,room.width,room.height], [x,y,w,h]);
    assert.equal(added.document.rooms.length, before.document.rooms.length + 1);
    assert.ok(await canvas.isVisible());
    assert.equal(room.properties.find(p => p.key === 'mapMaker.minimapRegionColor').value, '#3478ab');
    assert.deepEqual((await undo()).document, before.document);
  }

  await page.locator('[data-chip="16x8"]').click();
  const first = await write('roomAdd', () => clickAt(x, y));
  const firstId = first.selection.roomId;
  await hoverAt(x + 19, y + 2);
  const connectedPreview = await pixels();
  const second = await write('roomAdd', () => clickAt(x + 19, y + 2));
  assert.ok(connectedPreview === await pixels(), 'Shared walls and doorways match before and after stamping.');
  assert.notEqual(firstId, second.selection.roomId);
  assert.equal(second.document.rooms.length, before.document.rooms.length + 2);
  const deleted = await write('roomDelete', () => clickAt(x + 3, y + 3, 'right'));
  assert.deepEqual(deleted.document.rooms, second.document.rooms.filter(room => room.id !== firstId));
  assert.equal(await page.locator('dialog[open]').count(), 0);
  const restored = await write('undo', () => page.keyboard.press('Control+z'));
  assert.deepEqual(restored.document, second.document);
  const redo = await write('redo', () => page.keyboard.press('Control+y'));
  assert.deepEqual(redo.document, deleted.document);
  await write('undo', () => page.keyboard.press('Control+z'));
  await clickAt(x - 4, y + 3, 'right'); await frame();
  assert.deepEqual((await state()).document, second.document);
  if ((await state()).selection.roomId !== firstId) await write('selectRoom', () => clickAt(x + 3, y + 3));
  await write('roomProperties', () => page.locator('#minimap-room-color-preset').selectOption(region));
  assert.equal(await page.locator('#minimap-room-color').inputValue(), region);
  await write('roomProperties', () => page.locator('#minimap-room-color').fill('#ab5421'));
  let movePreview;
  await write('roomMove', async () => {
    const start = at(x + 3.5, y + 3.5), end = at(x - 4.5, y + 3.5);
    await page.mouse.move(start.x, start.y); await page.mouse.down(); await page.mouse.move(end.x,end.y,{steps:5}); await frame(); movePreview = await pixels(); await page.mouse.up();
  });
  assert.ok(movePreview === await pixels(), 'Moving room preview matches the committed room and its neighbors.');
  await page.locator('#map-tab').click();
  const inspector = page.locator('#inspector');
  assert.equal(await inspector.getByLabel('X', { exact: true }).inputValue(), String(x - 8));
  assert.equal((await page.locator('#room-color-hex').inputValue()).toLowerCase(), '#ab5421');
  await inspector.getByLabel('Name', { exact: true }).fill('Shared chip room');
  await inspector.getByLabel('Width', { exact: true }).fill('18');
  await page.locator('#room-color-picker-preset').selectOption({label: 'Flooded halls'});
  assert.equal((await page.locator('#room-color-hex').inputValue()).toLowerCase(), '#3478ab');
  await page.locator('#room-color-hex').fill('#127354');
  const renamed = await write('roomProperties', () => inspector.getByRole('button', { name: 'Apply', exact: true }).click());
  await edit.click(); await frame();
  assert.ok((await page.locator('#minimap-selected-room').textContent()).includes('Shared chip room'));
  assert.ok((await page.locator('#minimap-selected-room').textContent()).includes('18 × 8'));
  assert.equal(await page.locator('#minimap-room-color').inputValue(), '#127354');
  at = await coordinates(renamed.document.rooms);
  const inside = at(x - 5, y + 4);
  await page.mouse.dblclick(inside.x, inside.y); await page.locator('#map-canvas').waitFor({state:'visible'});
  assert.equal((await state()).selection.roomId, firstId);
  await edit.click();
  for (let n = 0; n < 6; n++) await undo();
  assert.deepEqual((await state()).document, before.document);
  assert.deepEqual(errors, []);
  console.log('34 grid-aligned chip sizes, matching preview pixels and passages, right-click delete/undo/redo, saved region presets and two-way room editing passed.');
} finally { await browser.close(); }
