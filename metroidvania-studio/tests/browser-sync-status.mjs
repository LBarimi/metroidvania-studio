import { createRequire } from 'node:module';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
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
const request = (resource, options = {}) => fetch(base + resource, { ...options, signal: AbortSignal.timeout(10000) });
const state = async () => (await request('/api/state')).json();
const initial = await state();
async function command(action, values = {}) {
  const current = await state();
  const response = await request('/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'sync-test', commandId: crypto.randomUUID(),
      expectedRevision: current.revision, expectedInstanceId: current.instanceId }) });
  const result = await response.json(); assert.equal(response.ok, true, JSON.stringify(result)); return result;
}
async function exported() {
  for (let i = 0; i < 100; i++) {
    const value = await state(); if (value.export.phase === 'saved') return value;
    await new Promise(resolve => setTimeout(resolve, 75));
  }
  throw new Error('Export did not complete.');
}
const document = { ...initial.document, name: 'Sync fixture', rooms: initial.document.rooms.slice(0, 1) };
await writeFile(path.join(root, 'Maps/SyncFixture.json'), JSON.stringify(document));
await command('open', { path: 'SyncFixture.json', discard: true });
const first = document.rooms[0].id;
await command('selectRoom', { id: first });
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: 'ko-KR' });
const checks = [], errors = [];
page.on('pageerror', error => errors.push(error.message));
const json = key => page.waitForFunction(key => document.getElementById('status-export')?.dataset.state === key,
  key, { timeout: 10000 });
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  await exported(); await json('syncExportSaved');
  checks.push('room JSON export completion is visible in an independent studio');
  await command('documentProperties', { name: 'Sync edited' });
  await exported(); await json('syncExportSaved');
  assert.match(await page.locator('#status-state').textContent(), /맵 수정됨/);
  checks.push('room JSON export does not claim the authoring map was saved');
  const output = (await state()).export;
  const exportFile = path.resolve(root, output.path);
  assert.ok(path.relative(root, exportFile).startsWith('Maps' + path.sep + 'AutoExport' + path.sep));
  const previousBytes = await readFile(exportFile);
  await writeFile(exportFile, 'External modification fixture');
  await command('documentProperties', { name: 'Export conflict' });
  await json('syncExportError');
  assert.match(await page.locator('#status-export').getAttribute('title'), /externally changed/i);
  await writeFile(exportFile, previousBytes);
  await exported(); await json('syncExportSaved');
  checks.push('publication conflicts surface persistently and recover through the existing automatic retry');
  await page.waitForFunction(() => document.querySelector('#toast')?.hidden);

  await page.route('**/api/state*', route => route.abort());
  await json('syncServerOffline');
  await page.mouse.move(500, 350); await page.waitForTimeout(100);
  await json('syncServerOffline');
  const recovered204 = page.waitForResponse(response => new URL(response.url()).pathname === '/api/state' && response.status() === 204);
  await page.unroute('**/api/state*'); await recovered204; await json('syncExportSaved');
  checks.push('offline status survives pointer redraws and a 204 response restores connectivity');

  await page.locator('#language').selectOption('EN');
  await page.waitForFunction(() => document.querySelector('#status-export')?.textContent === 'JSON exported');
  assert.equal(await page.locator('#status-export').textContent(), 'JSON exported');
  await page.locator('#language').selectOption('KR');
  const evidence = path.join(repository, '.local/logs/metroidvania-studio-sync'); await mkdir(evidence, { recursive: true });
  await page.screenshot({ path: path.join(evidence, 'web-status.png') });
  await page.setViewportSize({ width: 800, height: 650 });
  const bounds = await page.locator('#status-export').boundingBox(); assert.ok(bounds.x + bounds.width <= 800);
  checks.push('KR/EN labels and the compact status bar remain usable');
  assert.deepEqual(errors, []);
  await writeFile(path.join(evidence, 'browser-report.json'), JSON.stringify({ checks, errors }, null, 2));
  console.log(`Sync browser checks: ${checks.length}/${checks.length} passed.`);
} finally {
  await browser.close();
  if (initial.file) await command('open', { path: initial.file, discard: true });
}
