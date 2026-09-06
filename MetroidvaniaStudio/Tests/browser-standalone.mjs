import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const root = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.');
const relative = path.relative(path.join(repository, 'MetroidvaniaStudio/.local'), root);
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const request = (resource, options = {}) => fetch(base + resource, { ...options, signal: AbortSignal.timeout(10000) });
const state = async () => (await request('/api/state')).json();
async function command(action, values = {}) {
  const current = await state();
  const response = await request('/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action, ...values, clientId: 'standalone-test', commandId: crypto.randomUUID(),
      expectedRevision: current.revision, expectedInstanceId: current.instanceId }) });
  const result = await response.json(); assert.equal(response.ok, true, JSON.stringify(result)); return result;
}
const initial = await state();
assert.ok(initial.file && !initial.dirty, 'Import UI validation needs a saved fixture.');
const health = await (await request('/api/health')).json();
assert.equal(path.resolve(health.workspacePath), root);
assert.equal(initial.workspace.mapsPath, 'Maps');
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, locale: 'en-US' });
const errors = [], checks = [];
page.on('pageerror', error => errors.push(error.message));
async function choose(name, contents) {
  const [chooser] = await Promise.all([page.waitForEvent('filechooser'), page.getByRole('button', { name: 'Import JSON', exact: true }).click()]);
  await chooser.setFiles({ name, mimeType: 'application/json', buffer: Buffer.from(contents, 'utf8') });
}
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  await page.locator('#language').selectOption('EN');
  await page.waitForFunction(() => document.querySelector('#connection')?.textContent === 'Local studio connected');
  checks.push('independent workspace reports a local connection');
  await page.locator('#language').selectOption('KR');
  await page.locator('#palette').getByRole('button', { name: '초록 테마', exact: true }).waitFor();
  await page.locator('#layers').getByRole('button', { name: '트리거', exact: true }).click();
  await page.locator('#palette').getByRole('button', { name: '트리거 영역', exact: true }).waitFor();
  await page.locator('#language').selectOption('EN');
  await page.locator('#palette').getByRole('button', { name: 'Area', exact: true }).waitFor();
  await page.locator('#layers').getByRole('button', { name: 'Foreground tiles', exact: true }).click();
  await page.locator('#palette').getByRole('button', { name: 'Green theme', exact: true }).waitFor();
  checks.push('default layers, objects and themes follow KR/EN language selection');

  await choose('invalid.json', '{');
  await page.locator('#toast').waitFor({ state: 'visible' });
  assert.deepEqual((await state()).document, initial.document);
  checks.push('malformed file import preserves the open map');
  const imported = structuredClone(initial.document); imported.name = 'Imported room document';
  await choose('import.json', JSON.stringify(imported));
  await page.waitForFunction(() => document.querySelector('#project-name')?.textContent.includes('Imported room document'));
  const changed = await state();
  assert.equal(changed.document.name, imported.name); assert.equal(changed.file, null); assert.equal(changed.dirty, true);
  checks.push('browser file import creates an unsaved authoring document');
  await command('exportRooms', { directory: 'StandaloneExport' });
  assert.ok((await (await request('/api/files')).json()).some(name => name.startsWith('StandaloneExport/')));
  checks.push('imported rooms export through the generic workspace');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ checks, errors }));
} finally {
  await browser.close();
  await command('open', { path: initial.file, discard: true });
}
