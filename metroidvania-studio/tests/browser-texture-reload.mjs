import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { writeFile, readFile, rename } from 'node:fs/promises';
import path from 'node:path';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const workspace = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT);
const state = async () => (await fetch(base + '/api/state?full=true')).json();
async function command(action, values = {}) {
  const s = await state();
  const response = await fetch(base + '/api/command', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({
    action, ...values, clientId: 'texture-test', commandId: crypto.randomUUID(), expectedInstanceId: s.instanceId, expectedRevision: s.revision }) });
  assert.ok(response.ok, await response.text());
}
await command('paletteAdd', { name: 'Texture refresh', color: '#112233' });
let seed = await state();
const material = seed.catalog.materials.find(m => m.name === 'Texture refresh'), asset = material.sprites[0].asset;
const file = path.resolve(workspace, asset);
assert.ok(file.startsWith(workspace + path.sep));
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'en-US' });
  page.setDefaultTimeout(15000);
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await page.goto(base); await page.locator('#palette .palette-item.active').waitFor();
  await page.waitForTimeout(1800); seed = await state();
  const source = await page.evaluate(async asset => {
    const image = new Image(); image.src = '/api/asset?path=' + encodeURIComponent(asset); await image.decode();
    const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0);
    const pixels = ctx.getImageData(0, 0, canvas.width, canvas.height);
    for (let i = 0; i < pixels.data.length; i += 4) if (pixels.data[i + 3]) { pixels.data[i] = 17; pixels.data[i + 1] = 115; pixels.data[i + 2] = 221; }
    ctx.putImageData(pixels, 0, 0); return canvas.toDataURL('image/png').split(',')[1];
  }, asset);
  // An ordinary filesystem write simulates an external image editor, with no app command or page reload.
  const catalogBefore = await readFile(path.join(workspace, '.studio/catalog.json'), 'utf8');
  const bytes = Buffer.from(source, 'base64'); await writeFile(file + '.saving', bytes); await rename(file + '.saving', file);
  await page.waitForFunction(async revision => (await (await fetch('/api/state')).json()).catalogRevision > revision, seed.catalogRevision);
  await page.waitForFunction(() => {
    const canvas = document.querySelector('#palette .palette-item.active canvas');
    return canvas && Array.from(canvas.getContext('2d').getImageData(16, 16, 1, 1).data).join(',') === '17,115,221,255';
  });
  const updated = await state();
  assert.equal(updated.documentRevision, seed.documentRevision);
  assert.deepEqual(updated.document, seed.document);
  assert.equal(await readFile(path.join(workspace, '.studio/catalog.json'), 'utf8'), catalogBefore);
  const response = await fetch(base + '/api/asset?path=' + encodeURIComponent(asset));
  assert.equal(response.headers.get('cache-control'), 'no-store');
  assert.deepEqual(Buffer.from(await response.arrayBuffer()), bytes, 'Engine asset downloads must receive the new PNG bytes.');
  // Also exercise the canvas chunk cache, then fail a replacement read and retain its previous pixels.
  const rendering = await page.evaluate(async ({ material, seed }) => {
    const { MapCanvas } = await import('/map-canvas.js');
    const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;left:0;top:0;width:640px;height:400px'; document.body.append(canvas);
    const editor = new MapCanvas(canvas, async () => seed, () => {}, () => {});
    const room = { id: 'texture-room', name: 'Texture room', x: 0, y: 0, width: 20, height: 12, visible: true, locked: false,
      foreground: [{ x: 8, y: 5, shape: 0, material: material.id, groupId: '' }], background: [], objects: [], properties: [] };
    let current = { ...seed, document: { ...seed.document, rooms: [room] }, selection: { ...seed.selection, roomId: room.id, roomIds: [room.id] } };
    editor.setState(current); editor.pixelScale = 2; editor.center = { x: 10, y: 6 };
    const pixel = () => { editor.draw(); const p = editor.toScreen({ x: 8.5, y: 5.5 }); return Array.from(editor.ctx.getImageData(p.x, p.y, 1, 1).data); };
    const until = async predicate => { const deadline = performance.now() + 5000; while (!predicate()) { if (performance.now() > deadline) throw new Error('Texture pixels did not load'); await new Promise(r => requestAnimationFrame(r)); } };
    await until(() => pixel().join(',') === '17,115,221,255');
    window.__textureCanvas = { editor, canvas, current, pixel }; return pixel();
  }, { material, seed: updated });
  assert.deepEqual(rendering, [17, 115, 221, 255]);
  await page.route('**/api/asset?*', route => route.fulfill({ status: 503, body: '' }));
  const retained = await page.evaluate(async () => {
    const t = window.__textureCanvas; t.current = { ...t.current, catalogRevision: t.current.catalogRevision + 1 };
    t.editor.setState(t.current); await new Promise(r => setTimeout(r, 100)); return t.pixel();
  });
  assert.deepEqual(retained, rendering, 'A failed replacement must keep the previous rendered image.');
  await page.unroute('**/api/asset?*');
  const redraw = await page.evaluate(async asset => {
    const image = new Image(); image.src = '/api/asset?path=' + encodeURIComponent(asset); await image.decode();
    const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0); const data = ctx.getImageData(0, 0, image.width, image.height);
    for (let i = 0; i < data.data.length; i += 4) if (data.data[i + 3]) { data.data[i] = 232; data.data[i + 1] = 64; data.data[i + 2] = 19; }
    ctx.putImageData(data, 0, 0); return canvas.toDataURL().split(',')[1];
  }, asset);
  await writeFile(file, Buffer.from(redraw, 'base64'));
  await page.waitForFunction(async revision => (await (await fetch('/api/state')).json()).catalogRevision > revision, updated.catalogRevision);
  await page.evaluate(async () => {
    const t = window.__textureCanvas, latest = await (await fetch('/api/state')).json();
    t.current = { ...t.current, catalogRevision: latest.catalogRevision + 1 }; t.editor.setState(t.current);
  });
  await page.waitForFunction(() => window.__textureCanvas.pixel().join(',') === '232,64,19,255');
  await page.evaluate(() => { window.__textureCanvas.editor.dispose(); window.__textureCanvas.canvas.remove(); });
  assert.deepEqual(errors, []);
  console.log('PASS: External PNG replacement refreshes palette and canvas pixels, preserves edits, downloads to engines and retains images on read failure.');
} finally { await browser.close(); }
