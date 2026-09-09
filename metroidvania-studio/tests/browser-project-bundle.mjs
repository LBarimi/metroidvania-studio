import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { readZip } from '../../tools/release/archives.mjs';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const workspace = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.');
const relative = path.relative(repository, workspace);
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state', { signal: AbortSignal.timeout(10000) })).json();
const post = (data, headers = {}) => fetch(base + '/api/project-bundle', { method: 'POST',
  headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(data), signal: AbortSignal.timeout(10000) });
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'ko-KR', acceptDownloads: true });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  const open = async () => { await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', { name: '프로젝트 묶음 내보내기…', exact: true }).click(); };
  await page.locator('#language').selectOption('KR');
  await open();
  const dialog = page.locator('#project-bundle-dialog');
  await dialog.getByRole('button', { name: '취소', exact: true }).click(); await dialog.waitFor({ state: 'detached' });
  const before = await state(); await open();
  const downloaded = page.waitForEvent('download', { timeout: 20000 });
  await page.locator('#project-bundle-download').click();
  const download = await downloaded; assert.equal(download.suggestedFilename(), 'project.zip'); assert.equal(await download.failure(), null);
  const packed = readZip(await readFile(await download.path()));
  assert.deepEqual(JSON.parse(packed.get('Maps/world.map.json')), before.document);
  const catalog = JSON.parse(packed.get('.studio/catalog.json'));
  assert.equal(catalog.projectPath, '.'); assert.deepEqual(catalog.materials, before.catalog.materials);
  assert.deepEqual(catalog.objects, before.catalog.objects);
  assert.ok(packed.get('readme.txt').includes(Buffer.from('--project')));
  const images = [...packed.keys()].filter(name => name.startsWith('Textures/')); assert.ok(images.length > 0);
  for (const name of images) {
    const response = await fetch(base + '/api/asset?path=' + encodeURIComponent(name)); assert.equal(response.status, 200);
    assert.deepEqual(packed.get(name), Buffer.from(await response.arrayBuffer()), name);
  }
  assert.ok([...packed.keys()].every(name => name === 'Maps/world.map.json' || name === '.studio/catalog.json' || name === 'readme.txt' || name.startsWith('Textures/')));
  assert.equal(packed.get('Maps/world.map.json').includes(10), false, 'Compact map JSON.');
  const after = await state(); assert.equal(after.documentRevision, before.documentRevision); assert.deepEqual(after.document, before.document); assert.equal(after.dirty, before.dirty);
  await dialog.getByText('ZIP 다운로드를 시작했습니다.', { exact: true }).waitFor();
  await dialog.getByRole('button', { name: '닫기', exact: true }).click();

  const payload = { instanceId: after.instanceId, documentRevision: after.documentRevision, catalogRevision: after.catalogRevision };
  assert.equal((await post({ ...payload, documentRevision: -1 })).status, 409);
  assert.equal((await post(payload, { Origin: 'https://invalid.example' })).status, 403);
  await page.route('**/api/project-bundle', route => route.fulfill({ status: 400, contentType: 'application/json', body: JSON.stringify({ error: '@bundleAssetUnavailable:Textures/missing.png' }) }));
  await open(); await page.locator('#project-bundle-download').click();
  await dialog.locator('.modal-error').getByText('이미지를 읽을 수 없습니다. 저장이 완료됐는지 확인하세요: Textures/missing.png', { exact: true }).waitFor();
  assert.equal(await page.locator('#project-bundle-download').isEnabled(), true);
  await dialog.getByRole('button', { name: '취소', exact: true }).click(); await page.unroute('**/api/project-bundle');

  let received; const requested = new Promise(resolve => received = resolve);
  let release; const pending = new Promise(resolve => release = resolve);
  await page.route('**/api/project-bundle', async route => { received(); await pending; try { await route.abort(); } catch {} });
  await open(); await page.locator('#project-bundle-download').click(); await requested;
  await dialog.getByRole('button', { name: '취소', exact: true }).click(); release(); await dialog.waitFor({ state: 'detached' });
  await page.unroute('**/api/project-bundle');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ passed: true, images: images.length, checks: ['menu', 'portable ZIP download', 'unchanged edits', 'stale revision', 'origin guard', 'localized error', 'cancel'] }));
} finally { await browser.close(); }
