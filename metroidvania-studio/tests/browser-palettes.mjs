import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const snapshot = async () => (await fetch(base + '/api/state?full=true')).json();
async function command(action, values = {}) {
  const current = await snapshot();
  const response = await fetch(base + '/api/command', { method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'palette-test', commandId: crypto.randomUUID(), expectedInstanceId: current.instanceId, expectedRevision: current.revision }) });
  assert.ok(response.ok, await response.text());
}
const original = await snapshot(), count = original.catalog.materials.length;
await command('import', { discard: true, document: { ...original.document, rooms: [{ id: 'palette-room', name: 'Palette room', x: 0, y: 0,
  width: 20, height: 12, visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] }] } });
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'ko-KR' });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(base); await page.locator('#palette .palette-item').first().waitFor();
  await page.locator('#language').selectOption('KR');
  await page.locator('#add-palette').click();
  assert.equal(await page.locator('.dialog-title').textContent(), '팔레트 추가×');
  await page.locator('#palette-name').fill('새 지역');
  await page.locator('#palette-color-hex').fill('invalid');
  await page.locator('dialog .accent').click();
  assert.ok((await page.locator('.modal-error').innerText()).includes('#RRGGBB'));
  assert.equal((await snapshot()).catalog.materials.length, count);
  await page.locator('#palette-color-picker').evaluate(input => { input.value = '#9655cf'; input.dispatchEvent(new Event('input', { bubbles: true })); });
  assert.equal(await page.locator('#palette-color-hex').inputValue(), '#9655CF');
  if (process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT) await page.screenshot({ path: process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT });
  const addition = page.waitForResponse(response => response.url().endsWith('/api/command') && response.request().postDataJSON()?.action === 'paletteAdd');
  await page.locator('dialog .accent').click();
  const addedResponse = await addition, added = await addedResponse.json();
  assert.ok(added.catalog, 'The command response must include the changed catalog without waiting for polling.');
  await page.locator('dialog').waitFor({ state: 'detached' });
  await page.locator('#palette .palette-item.active').filter({ hasText: '새 지역' }).waitFor();
  assert.equal(await page.locator('#palette .palette-item').count(), count + 1);
  const material = added.catalog.materials.find(m => m.name === '새 지역');
  assert.equal(material.id, added.selection.material);
  assert.equal(material.sprites.length, 51);
  const pixels = await page.evaluate(async material => {
    const image = new Image(); image.src = '/api/asset?path=' + encodeURIComponent(material.sprites[0].asset); await image.decode();
    const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0);
    const read = (shape, mask, x, y) => { const s = material.sprites.find(s => s.shape === shape && s.mask === mask);
      return [...ctx.getImageData(s.x + x, image.height - 1 - s.y - y, 1, 1).data]; };
    return { solid: read(0, 255, 0, 15), outline: read(0, 0, 0, 15), inside: read(0, 0, 8, 8), corner: read(0, 5, 15, 15),
      slopes: [1, 2, 3, 4].map(shape => ({ fill: read(shape, 0, shape === 1 || shape === 3 ? 0 : 15, shape < 3 ? 0 : 15),
        empty: read(shape, 0, shape === 1 || shape === 3 ? 15 : 0, shape < 3 ? 15 : 0), edge: read(shape, 0, 7, shape === 1 || shape === 4 ? 8 : 7) })) };
  }, material);
  const purple = [150, 85, 207, 255], white = [255, 255, 255, 255];
  assert.deepEqual(pixels.solid, purple); assert.deepEqual(pixels.inside, purple);
  assert.deepEqual(pixels.outline, white); assert.deepEqual(pixels.corner, white);
  for (const slope of pixels.slopes) { assert.deepEqual(slope.fill, purple); assert.equal(slope.empty[3], 0); assert.deepEqual(slope.edge, white); }
  const catalogFile = path.join(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT, '.studio/catalog.json');
  const saved = JSON.parse(await readFile(catalogFile, 'utf8'));
  assert.deepEqual(saved.materials.slice(0, count), original.catalog.materials, 'Original palettes remain unchanged.');
  assert.deepEqual(saved.objects, original.catalog.objects);
  const canvas = await page.locator('#map-canvas').boundingBox();
  const at = (x, y) => ({ x: canvas.x + canvas.width / 2 + (x - 10) * 32, y: canvas.y + canvas.height / 2 - (y - 6) * 32 });
  const start = at(3.5, 3.5), end = at(15.5, 3.5);
  await page.mouse.move(start.x, start.y); await page.mouse.down(); await page.mouse.move(end.x, end.y, { steps: 20 }); await page.mouse.up();
  await page.waitForFunction(async id => { const state = await (await fetch('/api/state?full=true')).json(); return state.document.rooms[0].foreground.filter(c => c.material === id).length >= 13; }, material.id);
  const painted = await snapshot();
  assert.ok(painted.document.rooms[0].properties.some(p => p.key === 'mapMaker.minimapColor' && p.value === material.color));
  await page.locator('#palette-search').fill('not-found');
  await page.locator('#add-palette').click(); await page.locator('#palette-name').fill('Another stage');
  await page.locator('dialog .accent').click(); await page.locator('dialog').waitFor({ state: 'detached' });
  await page.locator('#palette .palette-item.active').filter({ hasText: 'Another stage' }).waitFor();
  assert.equal(await page.locator('#palette-search').inputValue(), '');
  await page.locator('#add-palette').click(); await page.locator('#palette-name').fill('Another stage');
  await page.locator('dialog .accent').click(); assert.ok((await page.locator('.modal-error').innerText()).includes('같은 이름'));
  await page.keyboard.press('Escape');
  await page.reload(); await page.locator('#palette .palette-item').filter({ hasText: '새 지역' }).waitFor();
  assert.equal(await page.locator('#palette .palette-item').count(), count + 2);
  await page.locator('.layer-row').nth(2).locator('button').first().click();
  await page.locator('#add-palette').waitFor({ state: 'hidden' });
  assert.deepEqual(errors, []);
  console.log('PASS: palette creation, validation, immediate selection, PNG masks/slopes, painting, minimap theme, persistence and layer visibility.');
} finally { await browser.close(); }
