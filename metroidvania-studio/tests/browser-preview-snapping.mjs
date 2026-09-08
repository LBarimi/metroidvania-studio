import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const workspace = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.');
const relative = path.relative(path.join(repository, 'metroidvania-studio/.local'), workspace);
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state')).json();
async function command(action, values = {}) {
  const current = await state();
  const response = await fetch(base + '/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'preview-snapping-test', commandId: crypto.randomUUID(), expectedRevision: current.revision, expectedInstanceId: current.instanceId }) });
  const result = await response.json(); assert.ok(response.ok, JSON.stringify(result)); return result;
}
const initial = await state(), template = initial.document.rooms[0];
const room = { ...template, id: 'snapping', name: 'Camera motion', x: 0, y: 0, width: 40, height: 24, visible: true, locked: false, objects: [], background: [], properties: [],
  foreground: Array.from({ length: 960 }, (_, i) => ({ x: i % 40, y: Math.floor(i / 40), shape: 0, material: 'biome-grassland', groupId: '' }))
    .filter(t => t.y < 3 || t.y === 23 || t.x === 0 || t.x === 39 || t.y === 10 && t.x >= 8 && t.x <= 28) };
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
const errors = [], checks = [];
const closeTo = (a, b) => assert.ok(Math.abs(a - b) < .0001, `${a} differs from ${b}`);
const integer = value => Math.abs(value - Math.round(value)) < .0001;
try {
  await command('import', { document: { ...initial.document, rooms: [room], properties: [], layerGroups: [], stylegrounds: [] }, discard: true });
  await command('selectRoom', { id: room.id });
  await command('cameraSettings', { ppu: 16, referenceWidth: 320, referenceHeight: 180 });
  const before = JSON.stringify((await state()).document), revision = (await state()).documentRevision;
  for (const dpr of [1, 1.25, 2]) {
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: dpr, locale: 'en-US' });
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/app.js', async route => {
      const response = await route.fetch();
      await route.fulfill({ response, body: await response.text() + `
window.__snapMap = map;
const originalPreviewScene = map.drawScene;
map.drawScene = function (...args) {
  if (this.cameraPreview) window.__snapRender = { origin: this.computeOrigin(), dpr: this.dpr, center: { ...this.center }, pixelScale: this.pixelScale };
  return originalPreviewScene.apply(this, args);
};` });
    });
    const tick = () => page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
    const center = () => page.evaluate(() => window.__snapMap.gameCamera.center);
    const frame = () => page.evaluate(() => window.__snapMap.gameCamera.frame);
    async function dragFrame(dx) {
      const p = await page.evaluate(() => { const m = window.__snapMap, c = document.querySelector('#map-canvas').getBoundingClientRect(), p = m.toScreen(m.gameCamera.center); return { x: c.x + p.x, y: c.y + p.y, scale: m.scale }; });
      await page.mouse.move(p.x, p.y); await page.mouse.down(); await page.mouse.move(p.x + dx * p.scale, p.y); await page.mouse.up(); await tick();
    }
    try {
      await page.goto(base); await page.locator('#room-list button').first().waitFor(); await page.locator('#language').selectOption('EN');
      const checkbox = page.getByRole('checkbox', { name: 'Pixel Perfect', exact: true });
      assert.equal(await checkbox.isChecked(), true);
      await page.locator('#game-camera-tool').click(); await tick();
      const snapped = await center(); await dragFrame(.01); closeTo((await center()).x, snapped.x);
      await checkbox.uncheck(); await dragFrame(.01);
      closeTo((await center()).x, snapped.x + .01); assert.ok(!integer((await frame()).x * 16));
      const rendered = await page.evaluate(() => window.__snapRender);
      assert.ok(!integer(rendered.origin.x * rendered.dpr), 'Smooth preview must retain subpixel rendering coordinates.');
      const editingOrigin = await page.evaluate(() => { const m = window.__snapMap, p = m.computeOrigin(); return { x: p.x * m.dpr, y: p.y * m.dpr }; });
      assert.ok(integer(editingOrigin.x) && integer(editingOrigin.y), 'Preview mode must not alter editor pixel alignment.');
      const canvas = page.locator('#game-preview-canvas'), r = await canvas.boundingBox(), prior = await center();
      await page.mouse.move(r.x + r.width / 2, r.y + r.height / 2); await page.mouse.down();
      await page.mouse.move(r.x + r.width / 2 + .37, r.y + r.height / 2); await page.mouse.up(); await tick();
      assert.ok((await center()).x < prior.x); assert.ok(!integer((await frame()).x * 16));
      await checkbox.focus(); await page.keyboard.press('Space'); await tick();
      assert.equal(await checkbox.isChecked(), true); assert.ok(integer((await frame()).x * 16) && integer((await frame()).y * 16));
      const perfectRender = await page.evaluate(() => window.__snapRender);
      assert.ok(integer(perfectRender.origin.x * dpr) && integer(perfectRender.origin.y * dpr));
      assert.equal(await page.locator('#game-camera-tool').getAttribute('aria-pressed'), 'true');
      await checkbox.uncheck(); await page.reload(); await page.locator('#room-list button').first().waitFor();
      assert.equal(await checkbox.isChecked(), false); assert.equal(await page.evaluate(() => window.__snapMap.gameCamera.pixelPerfect), false);
      await page.locator('#preview-maximize').click(); await tick(); assert.equal(await checkbox.isVisible(), true);
      await page.locator('#preview-maximize').click(); await page.locator('#preview-collapse').click(); await page.locator('#preview-collapse').click();
      assert.equal(await checkbox.isChecked(), false);
      await page.setViewportSize({ width: 820, height: 580 }); await tick();
      const box = await checkbox.boundingBox(), panel = await page.locator('#game-preview').boundingBox();
      assert.ok(box && panel && box.x >= panel.x && box.x + box.width <= panel.x + panel.width && box.y + box.height <= panel.y + panel.height);
      const aligned = await checkbox.evaluate(input => {
        const text = document.createRange(); text.selectNodeContents(input.parentElement.lastChild);
        const a = input.getBoundingClientRect(), b = text.getBoundingClientRect();
        return Math.abs(a.y + a.height / 2 - b.y - b.height / 2) < 4;
      });
      assert.equal(aligned, true, 'Checkbox and caption stay on the same row.');
      if (dpr === 1 && process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOTS) {
        await page.setViewportSize({ width: 1600, height: 1000 }); await checkbox.check(); await tick();
        await page.screenshot({ path: path.join(process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOTS, 'preview-pixel-perfect.png') });
      }
      checks.push(`DPI ${dpr}: default snapping, fractional camera and rendering motion, both drag surfaces, Space key, persisted preference and window layouts`);
    } finally { await page.close(); }
  }
  const final = await state(); assert.equal(JSON.stringify(final.document), before); assert.equal(final.documentRevision, revision);
  assert.deepEqual(errors, []); console.log(JSON.stringify({ checks, unchangedDocument: true, errors }));
} finally { await browser.close(); await command('import', { document: initial.document, discard: true }); }
