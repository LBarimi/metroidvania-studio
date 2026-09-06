import { createRequire } from 'node:module';
import assert from 'node:assert/strict';
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
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: 'en-US' });
const errors = [], checks = []; page.on('pageerror', error => errors.push(error.message));
async function settings(ppu, width, height) {
  await page.locator('#camera-settings-action').click();
  await page.locator('#camera-ppu').fill(String(ppu)); await page.locator('#camera-referenceWidth').fill(String(width)); await page.locator('#camera-referenceHeight').fill(String(height));
  await page.locator('dialog .accent').click(); await page.locator('dialog').waitFor({ state: 'detached' });
  await page.waitForFunction(ppu => document.querySelector('#status-camera')?.textContent.includes(`PPU ${ppu}`), ppu);
}
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await page.locator('#language').selectOption('EN');
  assert.deepEqual([initial.camera.ppu, initial.camera.referenceWidth, initial.camera.referenceHeight], [16, 320, 180]);
  await settings(32, 400, 224); let state = await getState(); assert.equal(state.camera.orthographicSize, 3.5);
  checks.push('camera settings apply through the localized web dialog');
  await page.locator('#camera-preview').click();
  await page.waitForFunction(() => document.querySelector('#map-canvas')?.dataset.cameraResolution === '400x224');
  await page.waitForTimeout(100);
  const before = await page.locator('#map-canvas').evaluate(canvas => canvas.toDataURL());
  await settings(64, 400, 224); await page.waitForTimeout(100);
  const after = await page.locator('#map-canvas').evaluate(canvas => canvas.toDataURL());
  assert.equal(before, after, 'Changing world-unit conversion must not crop fixed 16px source tiles.');
  assert.equal((await getState()).camera.orthographicSize, 1.75);
  checks.push('PPU changes physical units while preserving source-pixel framing');
  await settings(64, 512, 288);
  await page.waitForFunction(() => document.querySelector('#map-canvas')?.dataset.cameraResolution === '512x288');
  const frame = await page.locator('#map-canvas').evaluate(canvas => {
    const scale = Number(canvas.dataset.cameraScale), left = Math.floor((canvas.width - 512 * scale) / 2), top = Math.floor((canvas.height - 288 * scale) / 2);
    const ctx = canvas.getContext('2d'); return { scale, outside: [...ctx.getImageData(left - 1, top - 1, 1, 1).data], inside: [...ctx.getImageData(left + 1, top + 1, 1, 1).data] };
  });
  assert.ok(Number.isInteger(frame.scale) && frame.scale >= 1); assert.deepEqual(frame.outside, [11, 11, 11, 255]); assert.notDeepEqual(frame.inside, frame.outside);
  checks.push('reference resolution changes the exact integer-scaled preview frame');
  await page.locator('#camera-settings-action').click(); await page.locator('#camera-ppu').fill('1.5'); await page.locator('dialog .accent').click();
  await page.locator('.modal-error').filter({ hasText: 'integer' }).waitFor(); assert.equal((await getState()).camera.ppu, 64); await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  checks.push('invalid fractional settings leave the map unchanged');
  await command('save', { path: initial.file }); await page.reload(); await page.locator('#room-list button').first().waitFor();
  await page.locator('#camera-settings-action').click(); assert.equal(await page.locator('#camera-ppu').inputValue(), '64'); assert.equal(await page.locator('#camera-referenceWidth').inputValue(), '512');
  await page.getByRole('button', { name: 'Defaults 16 PPU · 320×180', exact: true }).click(); assert.equal(await page.locator('#camera-ppu').inputValue(), '16');
  await page.locator('dialog .accent').click(); await page.locator('dialog').waitFor({ state: 'detached' });
  await page.locator('#language').selectOption('KR'); await page.getByRole('button', { name: '카메라 설정', exact: true }).click(); await page.getByText('기준 해상도 가로', { exact: true }).waitFor();
  checks.push('save/reload, defaults and Korean labels work');
  assert.deepEqual(errors, []); console.log(JSON.stringify({ checks, errors }));
} finally {
  await browser.close();
  await command('documentProperties', { properties: initial.document.properties });
  await command('save', { path: initial.file });
}
