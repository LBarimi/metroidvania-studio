import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { installFilePickers } from './browser-file-handles.mjs';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(['localhost', '127.0.0.1'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state?full=true')).json();
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'en-US' });
  page.setDefaultTimeout(12000); await installFilePickers(page);
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  await page.locator('#language').selectOption('EN');
  const oversized = await page.evaluate(async () => {
    const { writeMapFile } = await import('./file-access.js');
    let touched = false;
    const fail = async () => { touched = true; throw new Error('Target file touched'); };
    try { await writeMapFile({ getFile: fail, createWritable: fail }, 'x'.repeat(32 * 1024 * 1024 + 1), ''); }
    catch (error) { return { touched, message: error.message }; }
    throw new Error('Oversized save was accepted');
  });
  assert.deepEqual(oversized, { touched: false, message: '@importTooLarge' }, 'Reject oversized JSON before touching the destination.');
  const menu = async name => { await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', { name, exact: true }).click(); };
  const sync = async () => {
    const s = await state(); await page.waitForFunction(revision => Number(document.querySelector('#status-revision')?.textContent.slice(1)) >= revision, s.revision);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); return s;
  };
  const command = async (action, values = {}) => {
    const s = await state(), response = await fetch(base + '/api/command', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({
      action, ...values, clientId: 'file-test', commandId: crypto.randomUUID(), expectedInstanceId: s.instanceId, expectedRevision: s.revision }) });
    assert.ok(response.ok, await response.text()); await sync();
  };
  await page.locator('#file-menu-button').click();
  const order = await page.locator('#file-menu .menu-popup').evaluate(node => [...node.children].slice(0, 4).map(n => n.tagName === 'HR' ? 'separator' : n.textContent));
  assert.deepEqual(order, ['New map', 'Open map', 'Import map JSON', 'separator']); await page.keyboard.press('Escape');
  const original = await state(), loaded = { ...original.document, name: 'Loaded from file' };
  await page.evaluate(async doc => { await window.__putMapFile('opened.map.json', JSON.stringify(doc)); }, loaded);
  await menu('Open map');
  if (original.dirty) await page.locator('dialog .accent').click();
  await page.waitForFunction(async () => (await (await fetch('/api/state')).json()).file === 'opened.map.json');
  let current = await sync(); assert.equal(current.dirty, false); assert.deepEqual(current.document, loaded);
  const fileId = current.browserFileId;
  await command('documentProperties', { name: 'Saved by shortcut' });
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Control+s');
  await page.waitForFunction(() => window.__filePickers.writes === 1);
  await page.waitForFunction(async () => !(await (await fetch('/api/state')).json()).dirty); await sync();
  assert.equal(await page.evaluate(async () => JSON.parse(await window.__readMapFile('opened.map.json')).name), 'Saved by shortcut');
  assert.equal(await page.evaluate(() => window.__filePickers.saveCalls), 0, 'Ctrl+S reuses the explicitly opened file.');
  assert.equal((await state()).browserFileId, fileId);
  await command('undo'); assert.equal((await state()).dirty, true); await command('redo'); assert.equal((await state()).dirty, false);
  let releaseWriter, startedWriter;
  const gate = new Promise(resolve => releaseWriter = resolve), started = new Promise(resolve => startedWriter = resolve);
  const delay = async route => { if (route.request().postDataJSON()?.action === 'options') { startedWriter(); await gate; } await route.continue(); };
  await page.route('**/api/command', delay);
  await page.locator('#tools [data-tool="0"]').click(); await started;
  const writes = await page.evaluate(() => window.__filePickers.writes);
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Control+s');
  await page.waitForTimeout(100); assert.equal(await page.evaluate(() => window.__filePickers.writes), writes, 'Save must drain earlier input before serializing the file.');
  releaseWriter(); await page.waitForFunction(n => window.__filePickers.writes > n, writes);
  await page.waitForFunction(async () => !(await (await fetch('/api/state')).json()).dirty); await sync();
  await page.unroute('**/api/command', delay);

  await page.evaluate(() => { window.__filePickers.cancel = true; });
  await menu('Save map as'); await sync(); assert.equal((await state()).browserFileId, fileId);
  await page.evaluate(() => { window.__filePickers.cancel = false; window.__filePickers.saveName = 'copy.map.json'; });
  await menu('Save map as'); await page.waitForFunction(async () => (await (await fetch('/api/state')).json()).file === 'copy.map.json'); await sync();
  assert.notEqual((await state()).browserFileId, fileId);
  assert.equal(await page.evaluate(async () => JSON.parse(await window.__readMapFile('copy.map.json')).name), 'Saved by shortcut');
  await command('documentProperties', { name: 'Not saved yet' });
  await page.evaluate(() => { window.__filePickers.failWrite = true; });
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Control+s');
  await page.locator('#toast').filter({ hasText: 'Test write failure' }).waitFor(); assert.equal((await state()).dirty, true);
  assert.equal(await page.evaluate(async () => JSON.parse(await window.__readMapFile('copy.map.json')).name), 'Saved by shortcut');
  await page.evaluate(async () => { window.__filePickers.failWrite = false; await window.__putMapFile('copy.map.json', '{"external":true}'); });
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Control+s');
  await page.locator('#toast').filter({ hasText: 'changed outside' }).waitFor();
  assert.equal(await page.evaluate(() => window.__readMapFile('copy.map.json')), '{"external":true}');
  await page.evaluate(() => { window.__filePickers.saveName = 'raced.map.json'; window.__filePickers.afterWriteName = 'Later edit'; });
  await menu('Save map as');
  await page.waitForFunction(async () => (await (await fetch('/api/state')).json()).document.name === 'Later edit');
  await page.locator('#toast').filter({ hasText: /changed|stale|newer/i }).waitFor();
  assert.equal((await state()).dirty, true, 'A late receipt must not mark a later edit saved.');
  assert.equal(await page.evaluate(async () => JSON.parse(await window.__readMapFile('raced.map.json')).name), 'Not saved yet');
  // Import keeps its ordinary OS file chooser and deliberately drops the source-file binding.
  const chooser = page.waitForEvent('filechooser'); await menu('Import map JSON');
  await (await chooser).setFiles({ name: 'imported.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(loaded)) });
  await page.locator('dialog .accent').click(); await page.waitForFunction(async () => (await (await fetch('/api/state')).json()).browserFileId === null); await sync();
  assert.equal((await state()).dirty, true);
  await page.evaluate(() => { window.showOpenFilePicker = undefined; window.showSaveFilePicker = undefined; });
  const fallback = page.waitForEvent('filechooser'); await menu('Open map');
  await (await fallback).setFiles({ name: 'fallback.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(loaded)) });
  await page.locator('dialog .accent').click(); await page.waitForFunction(async () => (await (await fetch('/api/state')).json()).file === 'fallback.json'); await sync();
  await command('documentProperties', { name: 'Fallback download' });
  const download = page.waitForEvent('download'); await menu('Save map as');
  assert.equal((await download).suggestedFilename(), 'fallback.json'); assert.equal((await state()).dirty, true, 'Starting a browser download is not a confirmed file save.');
  assert.ok((await page.evaluate(() => window.__filePickers.activeCalls)).every(Boolean), 'Native picker calls must retain transient user activation.');
  assert.deepEqual(errors, []);
  console.log('PASS: File order, native open/save, Ctrl+S, Save As, real file writes, cancellation, failures, external edits, stale receipts and browser fallbacks.');
} finally { await browser.close(); }
