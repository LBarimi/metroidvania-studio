import { createRequire } from 'node:module';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const getState = async () => (await fetch(base + '/api/state')).json();
const initial = await getState();
async function command(action, values = {}) {
  const state = await getState();
  const response = await fetch(base + '/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ action, ...values, clientId: 'camera-test', commandId: crypto.randomUUID(), expectedRevision: state.revision, expectedInstanceId: state.instanceId }) });
  assert.equal(response.ok, true, await response.clone().text()); return response.json();
}
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: 'en-US' });
page.setDefaultTimeout(12000);
const errors = [], checks = []; page.on('pageerror', error => errors.push(error.message));
const profile = state => [state.camera.ppu, state.camera.referenceWidth, state.camera.referenceHeight];
async function applied(ppu, width, height) {
  const expected = [ppu, width, height];
  let received;
  for (let attempt = 0; attempt < 120; attempt++) {
    received = profile(await getState());
    if (received.every((value, index) => value === expected[index])) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  assert.deepEqual(received, expected);
  await page.waitForFunction(([ppu, width, height]) => {
    return document.querySelector('#camera-ppu')?.value === String(ppu)
      && document.querySelector('#camera-resolution')?.value === `${width}x${height}`
      && !document.querySelector('#status-state')?.classList.contains('working');
  }, [ppu, width, height]);
}
async function settings(ppu, width, height) {
  if (await page.locator('#camera-ppu').inputValue() !== String(ppu)) await page.locator('#camera-ppu').selectOption(String(ppu));
  if (await page.locator('#camera-resolution').inputValue() !== `${width}x${height}`) await page.locator('#camera-resolution').selectOption(`${width}x${height}`);
  await applied(ppu, width, height);
}
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await page.locator('#language').selectOption('EN');
  assert.deepEqual(profile(initial), [16, 320, 180]); await applied(16, 320, 180);
  assert.equal(await page.locator('#camera-preview + .camera-controls').count(), 1);
  assert.deepEqual(await page.locator('#camera-ppu option').evaluateAll(options => options.map(o => Number(o.value))), Array.from({ length: 14 }, (_, index) => 2 ** index));
  assert.deepEqual(await page.locator('#camera-resolution optgroup').evaluateAll(groups => groups.map(g => g.label)), ['16:9', '4:3', '16:10', '3:2']);
  await page.locator('#edit-menu-button').click();
  assert.equal(await page.locator('#edit-menu [role="menuitem"]').count(), 2);
  assert.equal(await page.locator('#camera-settings-action').count(), 0); await page.keyboard.press('Escape');
  checks.push('PPU and resolution presets sit beside Game view; Edit only contains Undo/Redo');

  // Delay the first response so a second selection really arrives while the writer is busy.
  let release, started; const gate = new Promise(resolve => release = resolve), ready = new Promise(resolve => started = resolve);
  let delayed = false;
  const delay = async route => {
    if (!delayed && route.request().postDataJSON()?.action === 'cameraSettings') { delayed = true; started(); await gate; }
    await route.continue();
  };
  await page.route('**/api/command', delay);
  await page.locator('#camera-ppu').selectOption('32'); await ready;
  await page.locator('#camera-resolution').selectOption('512x288');
  await page.locator('#camera-ppu').selectOption('64');
  assert.equal(await page.locator('#camera-ppu').inputValue(), '64'); release();
  await applied(64, 512, 288); await page.unroute('**/api/command', delay);
  await page.locator('#edit-menu-button').click(); await page.locator('#undo').click(); await applied(32, 512, 288);
  await page.locator('#edit-menu-button').click(); await page.locator('#undo').click(); await applied(32, 320, 180);
  await page.locator('#edit-menu-button').click(); await page.locator('#redo').click(); await applied(32, 512, 288);
  assert.deepEqual((await getState()).document.rooms, initial.document.rooms);
  checks.push('Rapid mixed selections preserve both fields and Undo/Redo updates the dropdowns without touching rooms');

  await page.locator('#camera-preview').click();
  await page.waitForFunction(() => document.querySelector('#map-canvas')?.dataset.cameraResolution === '512x288');
  await page.waitForTimeout(100);
  const before = await page.locator('#map-canvas').evaluate(canvas => canvas.toDataURL());
  await settings(64, 512, 288); await page.waitForTimeout(100);
  assert.equal(before, await page.locator('#map-canvas').evaluate(canvas => canvas.toDataURL()), 'PPU changes units without cropping fixed 16px source tiles.');
  assert.equal((await getState()).camera.orthographicSize, 2.25);
  await settings(64, 320, 240);
  await page.waitForFunction(() => document.querySelector('#map-canvas')?.dataset.cameraResolution === '320x240');
  const frame = await page.locator('#map-canvas').evaluate(canvas => {
    const scale = Number(canvas.dataset.cameraScale), left = Math.floor((canvas.width - 320 * scale) / 2), top = Math.floor((canvas.height - 240 * scale) / 2);
    const ctx = canvas.getContext('2d'); return { scale, outside: [...ctx.getImageData(left - 1, top - 1, 1, 1).data], inside: [...ctx.getImageData(left + 1, top + 1, 1, 1).data] };
  });
  assert.ok(Number.isInteger(frame.scale) && frame.scale >= 1); assert.deepEqual(frame.outside, [11, 11, 11, 255]); assert.notDeepEqual(frame.inside, frame.outside);
  checks.push('Game view updates resolution and aspect ratio at an integer pixel scale; changing PPU preserves pixel framing');

  await command('save', { path: initial.file }); await page.reload(); await page.locator('#room-list button').first().waitFor(); await applied(64, 320, 240);
  const saved = JSON.parse(await readFile(path.join(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT, 'Maps', initial.file), 'utf8'));
  assert.equal(saved.properties.find(p => p.key === 'metroidvaniaStudio.camera.ppu').value, '64');
  assert.equal(saved.properties.find(p => p.key === 'metroidvaniaStudio.camera.height').value, '240');

  // Import a legacy map with non-preset values, then change one dropdown at a time.
  const custom = structuredClone(initial.document);
  custom.properties = custom.properties.filter(p => !p.key.startsWith('metroidvaniaStudio.camera.'));
  custom.properties.push(...Object.entries({ ppu: 24, width: 427, height: 239 }).map(([key, value]) => ({ key: 'metroidvaniaStudio.camera.' + key, value: String(value) })));
  await page.locator('#file-menu-button').click();
  const chooser = page.waitForEvent('filechooser'); await page.getByRole('menuitem', { name: 'Import map JSON', exact: true }).click();
  await (await chooser).setFiles({ name: 'custom-camera.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(custom)) });
  await applied(24, 427, 239);
  assert.equal(await page.locator('#camera-resolution option[data-custom]').count(), 1);
  assert.deepEqual((await getState()).document, custom);
  await settings(32, 427, 239); await command('undo'); await applied(24, 427, 239);
  await settings(24, 640, 360); await command('undo'); await applied(24, 427, 239);
  checks.push('Map JSON persists camera values; importing non-preset values preserves both the document and untouched settings');

  for (const [language, label] of [['KR', '해상도'], ['EN', 'Resolution'], ['JA', '解像度'], ['ZH_CN', '分辨率'], ['ZH_TW', '解析度'], ['RU', 'Разрешение']]) {
    await page.locator('#language').selectOption(language); assert.equal(await page.locator('#camera-resolution').getAttribute('aria-label'), label);
    for (const width of [1440, 1024]) {
      await page.setViewportSize({ width, height: 900 });
      const fits = await page.locator('#view-toolbar').evaluate(toolbar => {
        const area = toolbar.getBoundingClientRect();
        return [...toolbar.querySelectorAll('.camera-controls select')].every(select => { const box = select.getBoundingClientRect(); return box.left >= area.left && box.right <= area.right && box.bottom <= area.bottom; });
      });
      assert.ok(fits, `${language} camera dropdowns fit at ${width}px`);
    }
  }
  await page.locator('#language').selectOption('EN'); await page.setViewportSize({ width: 1440, height: 900 });
  await page.locator('#tabs > button').nth(1).click();
  assert.equal(await page.locator('#camera-ppu, #camera-resolution').count(), 0);
  assert.equal(await page.locator('#minimap-outline-width, #minimap-entrance-length').count(), 2);
  await page.locator('#tabs > button').first().click(); await applied(24, 427, 239);
  const selection = (await getState()).selection;
  await page.locator('#camera-ppu').focus(); await page.keyboard.press('b'); await page.keyboard.press('Delete');
  assert.deepEqual((await getState()).selection, selection);
  assert.deepEqual((await getState()).document.rooms, initial.document.rooms);
  checks.push('All six translations fit the toolbar, minimap controls stay separate and dropdown focus does not trigger editing shortcuts');
  assert.deepEqual(errors, []); console.log(JSON.stringify({ checks, errors }));
} finally {
  await browser.close();
  // This suite uses an isolated workspace. Restore the original map for the following tests.
  await command('open', { path: initial.file, discard: true });
  await command('documentProperties', { properties: initial.document.properties });
  await command('save', { path: initial.file });
}
