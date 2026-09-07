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
  await page.locator('#tabs button').nth(1).click(); await page.locator('#mini-canvas').waitFor({ state: 'visible' }); await page.waitForTimeout(200);
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
  console.log('PASS: sample opening/cancel, six-room layout, minimap navigation, multiple room selection and background visibility.');
} finally { await browser.close(); }
