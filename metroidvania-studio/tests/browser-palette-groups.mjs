import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state?full=true')).json();
async function command(action, values = {}) {
  const s = await state();
  const r = await fetch(base + '/api/command', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({
    action, ...values, clientId: 'palette-group-test', commandId: crypto.randomUUID(), expectedInstanceId: s.instanceId, expectedRevision: s.revision
  }) });
  assert.ok(r.ok, await r.text());
}
await command('options', { layer: 0, groupId: '' });
const initial = await state(), ids = initial.paletteGroups.find(g => g.id === 'default').materials.slice(0, 3);
assert.equal(ids.length, 3);
const bytes = async () => Promise.all([...new Set(initial.catalog.materials.flatMap(m => m.sprites.map(s => s.asset)).filter(Boolean))].map(async asset =>
  Buffer.from(await (await fetch(base + '/api/asset?path=' + encodeURIComponent(asset))).arrayBuffer()).toString('base64')));
const originalBytes = await bytes();
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'ko-KR' }); page.setDefaultTimeout(15000);
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  const actions = []; page.on('request', r => { if (r.url().endsWith('/api/command')) actions.push(r.postDataJSON().action); });
  await page.goto(base); await page.locator('#add-palette-group').waitFor(); await page.locator('#language').selectOption('KR');
  const group = id => page.locator(`.palette-group[data-group-id="${id}"]`);
  const entry = id => page.locator(`.palette-entry[data-material-id="${id}"]`);
  assert.equal(await group('default').locator('.palette-group-name').innerText(), '디폴트 색상');
  async function changed(action, work) {
    const reply = page.waitForResponse(r => r.url().endsWith('/api/command') && r.request().postDataJSON()?.action === action);
    await work(); const r = await reply; assert.ok(r.ok(), await r.text());
    const s = await r.json();
    await page.waitForFunction(groups => [...document.querySelectorAll('.palette-group')].map(g => g.dataset.groupId).join() === groups.map(g => g.id).join(), s.paletteGroups);
    return s;
  }
  async function add(name) {
    await page.locator('#add-palette-group').click(); await page.locator('#palette-group-name').fill(name);
    const s = await changed('paletteGroupAdd', () => page.locator('#palette-group-dialog .accent').click());
    await page.locator('#palette-group-dialog').waitFor({ state: 'detached' }); return s.paletteGroups.find(g => g.name === name).id;
  }
  const caves = await add('Caves'), tower = await add('Tower');
  await page.locator('#add-palette-group').click(); await page.locator('#palette-group-name').fill('caves');
  await page.locator('#palette-group-dialog .accent').click(); await page.waitForFunction(() => document.querySelector('#palette-group-dialog .modal-error')?.textContent);
  assert.match(await page.locator('#palette-group-dialog .modal-error').innerText(), /같은 이름/); await page.keyboard.press('Escape');
  await group('default').locator('.palette-group-toggle').click(); assert.equal(await entry(ids[0]).isVisible(), false);
  await page.locator('#palette-search').fill(initial.catalog.materials.find(m => m.id === ids[0]).name);
  // Built-in names are localized; searching by the group name also reveals collapsed contents.
  await page.locator('#palette-search').fill('디폴트 색상'); await entry(ids[0]).waitFor();
  await page.locator('#palette-search').fill(''); assert.equal(await entry(ids[0]).isVisible(), false);
  await group('default').locator('.palette-group-toggle').click();
  await page.locator('.sidebar').evaluate(el => { el.scrollTop = el.scrollHeight; });
  await changed('paletteMove', () => entry(ids[0]).locator('.palette-drag-handle').dragTo(group(caves).locator('.palette-group-empty')));
  await changed('paletteMove', () => entry(ids[1]).locator('.palette-drag-handle').dragTo(entry(ids[0]), { targetPosition: { x: 25, y: 2 } }));
  assert.deepEqual((await state()).paletteGroups.find(g => g.id === caves).materials, [ids[1], ids[0]]);
  await changed('paletteMove', () => entry(ids[1]).locator('.palette-drag-handle').dragTo(group(caves).locator('.palette-group-header')));
  assert.deepEqual((await state()).paletteGroups.find(g => g.id === caves).materials, [ids[0], ids[1]]);
  await changed('paletteGroupMove', () => group(caves).locator('.palette-group-header > .palette-drag-handle').dragTo(group('default').locator('.palette-group-header'), { targetPosition: { x: 40, y: 2 } }));
  assert.equal((await state()).paletteGroups[0].id, caves);
  const open = async id => { await entry(id).locator('.palette-settings').click(); await page.waitForFunction(() => !document.querySelector('#tileset-apply')?.disabled); };
  await open(ids[1]); await page.locator('#tileset-group').selectOption(tower); await page.keyboard.press('Escape');
  assert.deepEqual((await state()).paletteGroups.find(g => g.id === caves).materials, [ids[0], ids[1]]);
  await open(ids[1]); await page.locator('#tileset-up').click();
  const actionCount = actions.length;
  await changed('paletteMove', () => page.locator('#tileset-apply').click()); await page.locator('#tileset-dialog').waitFor({ state: 'detached' });
  assert.deepEqual(actions.slice(actionCount), ['paletteMove'], 'Changing order must not upload/rebuild a tileset.');
  assert.deepEqual((await state()).paletteGroups.find(g => g.id === caves).materials, [ids[1], ids[0]]);
  await open(ids[1]); await page.locator('#tileset-group').selectOption(tower);
  await changed('paletteMove', () => page.locator('#tileset-apply').click()); await page.locator('#tileset-dialog').waitFor({ state: 'detached' });
  await group(tower).locator('.palette-group-settings').click(); await page.locator('#palette-group-name').fill('High tower');
  await page.locator('#palette-group-up').click();
  await changed('paletteGroupMove', () => page.locator('#palette-group-dialog .accent').click()); await page.locator('#palette-group-dialog').waitFor({ state: 'detached' });
  await page.locator('#palette-search').fill('High tower'); assert.equal(await page.locator('.palette-group').count(), 1); await entry(ids[1]).waitFor();
  await page.locator('#palette-search').fill('');
  await open(ids[0]); await page.locator('#tileset-group').selectOption(tower);
  await command('paletteGroupAdd', { name: 'External group' });
  const conflict = page.waitForResponse(r => r.url().endsWith('/api/command') && r.request().postDataJSON()?.action === 'paletteMove');
  await page.locator('#tileset-apply').click(); assert.equal((await conflict).status(), 409);
  await page.waitForFunction(() => !!document.querySelector('#tileset-error')?.textContent);
  assert.ok(await page.locator('#tileset-error').innerText()); await page.keyboard.press('Escape');
  assert.deepEqual((await state()).paletteGroups.find(g => g.id === caves).materials, [ids[0]]);
  const saved = await state(); await page.reload(); await entry(ids[0]).waitFor();
  assert.deepEqual(await page.locator('.palette-group').evaluateAll(gs => gs.map(g => g.dataset.groupId)), saved.paletteGroups.map(g => g.id));
  for (const g of saved.paletteGroups) assert.deepEqual(await group(g.id).locator('.palette-entry').evaluateAll(es => es.map(e => e.dataset.materialId)), g.materials);
  assert.deepEqual(saved.document, initial.document); assert.equal(saved.documentRevision, initial.documentRevision);
  assert.deepEqual(saved.catalog.materials, initial.catalog.materials); assert.deepEqual(await bytes(), originalBytes);
  if (process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT) await page.screenshot({ path: process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT });
  await group(caves).locator('.palette-group-toggle').click();
  await page.locator('#add-palette').click(); await page.locator('#palette-name').fill('Grouped palette'); await page.locator('#palette-new-group').selectOption(caves);
  const added = await changed('paletteAdd', () => page.locator('dialog .accent').click()); await page.locator('dialog').waitFor({ state: 'detached' });
  assert.equal(added.paletteGroups.find(g => g.id === caves).materials.at(-1), added.selection.material);
  assert.equal(await entry(added.selection.material).isVisible(), true, 'A newly created palette must be revealed even when its group was collapsed.');
  assert.deepEqual(errors, []);
  console.log('PASS: palette group creation, collapse/search, real drag order and moves, settings, cancellation, stale edits, persistence and unchanged maps/textures.');
} finally { await browser.close(); }
