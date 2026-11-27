import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { installFilePickers } from './browser-file-handles.mjs';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
// Sample actual room paints at exact elapsed times, independent of the browser's frame rate.
function watchRoomPaints(page, canvasId, background, colors) {
  return page.evaluateHandle(({ canvasId, background, colors }) => {
    const ctx = document.getElementById(canvasId).getContext('2d');
    const fill = ctx.fillRect, origin = performance.now();
    let elapsed = 0, rooms = [];
    performance.now = () => origin + elapsed;
    ctx.fillRect = function(x, y, w, h) {
      if (this.fillStyle === background && x === 0 && y === 0) rooms = [];
      if (colors.includes(this.fillStyle)) rooms.push([x, y, w, h]);
      fill.call(this, x, y, w, h);
    };
    return {
      async frame(ms) {
        elapsed = ms;
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        return rooms.slice();
      },
      restore() { delete performance.now; ctx.fillRect = fill; }
    };
  }, { canvasId, background, colors });
}

const browser = await chromium.launch({ channel: process.env.METROIDVANIA_STUDIO_BROWSER_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined), headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 950 }, locale: 'en-US' });
  page.setDefaultTimeout(12000);
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await installFilePickers(page);
  const state = async () => (await fetch(base + '/api/state?full=true')).json();
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  const original = (await state()).document;
  const trigger = page.locator('.canvas-wrap #minimap-generate');
  const dialog = page.locator('dialog[open]');
  const press = name => dialog.getByRole('button', { name, exact: true }).click();
  const skipSave = async () => { await trigger.click(); await press('Continue without saving'); };
  const submit = async () => {
    await press('Generate'); await dialog.waitFor({ state: 'hidden' }); return state();
  };
  const preview = page.locator('#game-preview');
  async function checkButtonAbovePreview() {
    const [button, panel] = await Promise.all([trigger.boundingBox(), preview.boundingBox()]);
    assert.ok(button && panel && button.y + button.height < panel.y);
    assert.ok(Math.abs(button.x + button.width - panel.x - panel.width) < 2);
  }
  await checkButtonAbovePreview();
  await page.locator('#preview-collapse').click(); await checkButtonAbovePreview();
  await page.locator('#preview-collapse').click();
  await page.locator('#preview-maximize').click(); assert.equal(await trigger.isVisible(), false);
  await page.locator('#preview-maximize').click(); await checkButtonAbovePreview();
  await page.setViewportSize({ width: 800, height: 750 }); await checkButtonAbovePreview();
  await page.setViewportSize({ width: 1440, height: 950 });
  await trigger.click(); await press('Cancel');
  await page.locator('#minimap-tab').click(); assert.equal(await trigger.isVisible(), false);
  await page.locator('#minimap-drawing-tab').click();
  const canvas = await page.locator('#minimap-drawing-canvas').boundingBox(), overlay = await trigger.boundingBox();
  assert.ok(Math.abs(canvas.x + canvas.width - overlay.x - overlay.width - 10) < 2 && Math.abs(overlay.y - canvas.y - 10) < 2);
  await trigger.click(); await press('Cancel');
  await skipSave(); await page.keyboard.press('Escape');
  assert.deepEqual((await state()).document, original);

  await trigger.click();
  await page.evaluate(() => { window.__filePickers.cancel = true; });
  await press('Save and continue');
  await page.waitForFunction(() => window.__filePickers.activeCalls.length > 0);
  assert.equal(await dialog.getByLabel('Room count').count(), 0);
  await page.evaluate(() => { window.__filePickers.cancel = false; window.__filePickers.failWrite = true; });
  await press('Save and continue'); await dialog.locator('.modal-error:not(:empty)').waitFor();
  assert.deepEqual((await state()).document, original);
  await page.evaluate(() => { window.__filePickers.failWrite = false; });
  await press('Save and continue');
  await dialog.getByLabel('Room count').fill('19');
  const randomSeed = dialog.getByRole('checkbox', { name: 'Random seed', exact: true });
  const generateTiles = dialog.getByRole('checkbox', { name: 'Generate tilemap', exact: true });
  const [randomBox, tilesBox, formBox] = await Promise.all([randomSeed, generateTiles, dialog.locator('.generation-settings')].map(control => control.boundingBox()));
  assert.ok(Math.abs(randomBox.x - formBox.x) < 1, 'The options start at the left edge of the form.');
  assert.ok(Math.abs(randomBox.y - tilesBox.y) < 1 && tilesBox.x > randomBox.x + randomBox.width,
    'Random seed and Generate tilemap sit side by side.');
  const seed = dialog.getByLabel('Seed', { exact: true });
  assert.ok(await randomSeed.isChecked() && await seed.isDisabled());
  const automaticSeed = await seed.inputValue();
  assert.ok(await generateTiles.isChecked());
  assert.ok(await dialog.getByLabel('Terrain theme').isVisible());
  await generateTiles.uncheck();
  assert.equal(await dialog.getByLabel('Terrain theme').isVisible(), false);
  assert.deepEqual(JSON.parse(await page.evaluate(() => window.__readMapFile('saved.map.json'))), original);
  const auto = await submit();
  assert.equal(auto.document.rooms.length, 19); assert.equal(auto.file, null); assert.equal(auto.dirty, true);
  assert.ok(auto.document.properties.some(p => p.key === 'mapMaker.minimapGenerationSeed' && p.value === automaticSeed));
  assert.ok(auto.document.rooms.every(r => !r.foreground.length && !r.background.length && !r.objects.length));
  assert.ok(new Set(auto.document.rooms.map(r => r.properties.find(p => p.key === 'mapMaker.minimapRegionColor').value)).size > 1);

  await skipSave();
  const count = dialog.getByLabel('Room count'), chips = dialog.getByLabel('Room chips', { exact: true });
  assert.ok(await generateTiles.isChecked());
  await generateTiles.uncheck();
  await randomSeed.uncheck();
  assert.equal(await seed.inputValue(), automaticSeed);
  await seed.fill('2147483648'); await press('Generate');
  assert.match(await dialog.locator('.modal-error').innerText(), /2147483647/);
  await seed.fill(automaticSeed); await count.fill('19');
  assert.deepEqual((await submit()).document, auto.document);
  await skipSave(); await generateTiles.uncheck(); await randomSeed.uncheck(); await seed.fill('-123');
  await count.fill('0'); await press('Generate');
  assert.match(await dialog.locator('.modal-error').innerText(), /1024/);
  await count.fill('1024'); await chips.selectOption('generationInclude'); await press('Generate');
  assert.match(await dialog.locator('.modal-error').innerText(), /at least one room chip/);
  await dialog.locator('.generation-chips input[value="8x96"]').check();
  await dialog.getByLabel('Region color', { exact: true }).selectOption('generationChooseColors');
  await press('Generate'); assert.match(await dialog.locator('.modal-error').innerText(), /region color/);
  await dialog.locator('.generation-colors input').first().check();
  const color = await dialog.locator('.generation-colors input').first().inputValue();
  const dim = '#' + color.slice(1).match(/../g).map(s => Math.round(parseInt(s, 16) * .6).toString(16).padStart(2, '0')).join('');
  const probe = await watchRoomPaints(page, 'minimap-drawing-canvas', '#000000', [color, dim]);
  let manual;
  try {
    manual = await submit();
    const frame = ms => probe.evaluate((p, t) => p.frame(t), ms);
    assert.equal((await frame(0)).length, 0);
    const half = await frame(500);
    assert.equal(half.length, 512);
    const [x, y, w, h] = half[0];
    await page.mouse.click(canvas.x + x + w / 2, canvas.y + y + h / 2, { button: 'right' });
    const almost = await frame(990), full = await frame(1000);
    assert.equal(almost.length, 1014); assert.equal(full.length, 1024);
    assert.deepEqual(half, full.slice(0, 512), 'Placed rooms keep their final positions as the layout grows.');
    assert.deepEqual(await frame(1200), full);
  } finally { await probe.evaluate(p => p.restore()); await probe.dispose(); }
  assert.ok(manual.document.properties.some(p => p.key === 'mapMaker.minimapGenerationSeed' && p.value === '-123'));
  assert.equal(manual.document.rooms.length, 1024);
  assert.ok(manual.document.rooms.every(r => r.width === 8 && r.height === 96 && r.properties.some(p => p.value === color)));

  await skipSave(); await count.fill('5'); await chips.selectOption('generationExclude');
  for (const box of await dialog.locator('.generation-chips input:not([value="24x8"])').all()) await box.check();
  const filtered = await submit();
  assert.equal(filtered.document.rooms.length, 5);
  assert.ok(filtered.document.rooms.every(r => r.width === 24 && r.height === 8));
  await page.locator('#map-tab').click(); await page.locator('#map-canvas').waitFor();
  await page.waitForFunction(() => document.querySelector('#status-camera').textContent.includes('Overview scale'));
  assert.equal(await page.locator('#room-list button').count(), 5);
  assert.deepEqual(JSON.parse(await page.evaluate(() => window.__readMapFile('saved.map.json'))), original);

  await page.locator('#minimap-drawing-tab').click();
  await skipSave();
  const name = 'Generation-' + crypto.randomUUID();
  const edit = await fetch(base + '/api/command', { method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ action: 'documentProperties', name, clientId: 'second-editor', commandId: crypto.randomUUID(),
      expectedInstanceId: filtered.instanceId, expectedRevision: filtered.revision }) });
  assert.ok(edit.ok, await edit.text());
  await press('Generate'); await dialog.locator('.modal-error:not(:empty)').waitFor();
  const concurrent = await state(); assert.equal(concurrent.document.name, name);
  assert.deepEqual(concurrent.document.rooms, filtered.document.rooms);
  await press('Cancel');

  await page.evaluate(() => { window.showSaveFilePicker = undefined; });
  await trigger.click(); await press('Save and continue'); await count.waitFor();
  const saved = await state();
  assert.equal(saved.dirty, false);
  const savedPath = path.join(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT, 'Maps', saved.file);
  assert.deepEqual(JSON.parse(await readFile(savedPath, 'utf8')), concurrent.document);
  await count.fill('1'); assert.equal((await submit()).document.rooms.length, 1);
  await page.locator('#map-tab').click();
  await skipSave();
  assert.ok(await generateTiles.isChecked());
  assert.ok(await dialog.getByLabel('Terrain theme').isVisible());
  await dialog.getByLabel('Terrain theme').selectOption('biome-rock');
  await dialog.getByLabel('Difficulty', { exact: true }).selectOption('hard');
  await randomSeed.uncheck();
  await seed.fill('42');
  await chips.selectOption('generationInclude');
  await dialog.locator('input[value="16x32"]').check();
  await count.fill('8');
  const mapProbe = await watchRoomPaints(page, 'map-canvas', '#191919', ['#242424']);
  let tiled;
  try {
    tiled = await submit();
    const frame = ms => mapProbe.evaluate((p, t) => p.frame(t), ms);
    assert.equal((await frame(0)).length, 0);
    assert.equal((await frame(60)).length, 1);
    const half = await frame(500);
    assert.equal(half.length, 4);
    const colors = await page.locator('#map-canvas').evaluate((canvas, [x, y, w, h]) => {
      const dpr = window.devicePixelRatio;
      const pixels = canvas.getContext('2d').getImageData(Math.ceil(x * dpr) + 2, Math.ceil(y * dpr) + 2,
        Math.floor(w * dpr) - 4, Math.floor(h * dpr) - 4).data;
      return new Set(new Uint32Array(pixels.buffer)).size;
    }, half[0]);
    assert.ok(colors > 2, 'The first room already has visible terrain during the reveal.');
    const full = await frame(1000);
    assert.equal(full.length, 8);
    assert.deepEqual(half, full.slice(0, 4));
    assert.deepEqual(await frame(1200), full);
  } finally { await mapProbe.evaluate(p => p.restore()); await mapProbe.dispose(); }
  assert.ok(tiled.document.rooms.every(room => room.foreground.some(cell => cell.material === 'biome-rock')));
  assert.ok(tiled.document.rooms.every(room => room.objects.some(object => object.definition === 'Portal')));
  assert.ok(tiled.document.rooms.flatMap(room => room.foreground).some(cell => cell.material === 'traversal-ladder'));
  assert.equal(await page.locator('#map-tab').getAttribute('aria-pressed'), 'true');
  await page.waitForFunction(() => document.querySelector('#status-camera').textContent.includes('Overview scale'));
  await page.locator('#map-canvas').waitFor();
  for (const [name, id] of [['Ladder', 'traversal-ladder'], ['Drop-through floor', 'traversal-floor']]) {
    await page.locator('#palette-search').fill(name);
    await page.locator('#palette .palette-item').filter({ hasText: name }).click();
    await page.waitForFunction(material => fetch('/api/state').then(r => r.json()).then(s => s.selection.material === material), id);
  }
  assert.deepEqual(errors, []);
  console.log('Procedural generation passed save, cancel, chip filters, default terrain and both editor animations.');
} finally { await browser.close(); }
