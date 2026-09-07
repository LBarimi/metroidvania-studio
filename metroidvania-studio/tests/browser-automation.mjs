import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1', 'Run against a disposable automation workspace.');
assert.ok(base && ['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const browser = await chromium.launch({ channel: process.env.METROIDVANIA_STUDIO_BROWSER_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined), headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'en-US' });
page.setDefaultTimeout(15000);
page.setDefaultNavigationTimeout(15000);
const errors = []; page.on('pageerror', error => errors.push(error.message));
const document = async () => (await (await fetch(base + '/api/v1/document')).json());
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  const original = await document();
  await page.locator('#file-menu-button').click(); await page.locator('#scripts-action').click();
  await page.locator('#script-source').fill('studio.rename { name = "Automated web" }\nprint("first batch done")');
  await page.locator('#script-run').click(); await page.waitForFunction(() => document.querySelector('#script-output').textContent.includes('first batch done'));
  assert.equal((await document()).document.name, 'Automated web');
  await page.locator('#script-source').fill('studio.rename { name = "Dry preview" }\nprint("dry preview done")');
  await page.locator('#script-dry-run').check(); await page.locator('#script-run').click();
  await page.waitForFunction(() => document.querySelector('#script-output').textContent.includes('dry preview done'));
  assert.equal((await document()).document.name, 'Automated web');
  await page.locator('#script-dry-run').uncheck();
  await page.locator('#script-source').fill('studio.rename { name = "Must not apply" }\nerror("intentional script failure")');
  await page.locator('#script-run').click(); await page.waitForFunction(() => document.querySelector('#script-output').textContent.includes('intentional script failure'));
  assert.equal((await document()).document.name, 'Automated web');
  await page.locator('#script-source').fill('while true do end');
  await page.locator('#script-run').click();
  await page.locator('#script-source').focus(); await page.keyboard.press('Tab');
  assert.equal(await page.locator('#script-source').inputValue(), 'while true do end', 'Tab must not alter a running script');
  await page.locator('#script-cancel').click();
  await page.waitForFunction(() => !document.querySelector('#script-run').disabled);
  assert.equal((await document()).document.name, 'Automated web');
  await page.keyboard.press('Escape'); await page.locator('#script-dialog').waitFor({ state: 'detached' });
  await page.keyboard.press('Control+z');
  await page.waitForFunction(expected => document.querySelector('#project-name').textContent.includes(expected), original.document.name);
  assert.deepEqual((await document()).document, original.document);
  await page.keyboard.press('Control+y');
  await page.waitForFunction(() => document.querySelector('#project-name').lastChild.textContent === 'Automated web');
  assert.equal((await document()).document.name, 'Automated web');
  // An accepted POST can lose its response. Recover the same job identity and
  // create exactly one edit/history entry instead of submitting another run.
  await page.locator('#file-menu-button').click(); await page.locator('#scripts-action').click();
  const beforeRecovery = await document();
  const submissions = []; const acceptedIds = [];
  let firstAccepted;
  const accepted = new Promise(resolve => { firstAccepted = resolve; });
  await page.route('**/api/v1/jobs', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    submissions.push(route.request().postData());
    const response = await route.fetch();
    assert.equal(response.status(), 202);
    acceptedIds.push((await response.json()).id);
    if (submissions.length === 1) { await route.abort('failed'); firstAccepted(); }
    else await route.fulfill({ response });
  });
  await page.locator('#script-source').fill('studio.rename {name=studio.document().name .. " recovered"}\nprint("response recovered")');
  await page.locator('#script-run').click(); await accepted;
  await page.waitForTimeout(100);
  assert.equal(await page.locator('#script-run').isDisabled(), true, 'Uncertain submission must not enable a fresh run');
  await page.waitForFunction(() => document.querySelector('#script-output').textContent.includes('response recovered'));
  assert.ok(submissions.length >= 2, 'The lost response must be recovered');
  assert.equal(new Set(submissions).size, 1, 'Submission retries must reuse the exact payload');
  assert.equal(new Set(acceptedIds).size, 1, 'Submission retries must recover the original job');
  const recovered = await document();
  assert.equal(recovered.document.name, 'Automated web recovered');
  assert.equal(recovered.documentRevision, beforeRecovery.documentRevision + 1, 'Accepted job applied more than once');
  await page.unroute('**/api/v1/jobs');
  await page.keyboard.press('Escape'); await page.locator('#script-dialog').waitFor({ state: 'detached' });
  await page.keyboard.press('Control+z');
  await page.waitForFunction(() => document.querySelector('#project-name').lastChild.textContent === 'Automated web');
  assert.deepEqual((await document()).document, beforeRecovery.document, 'Recovery must add exactly one undo entry');
  await page.keyboard.press('Control+y');
  await page.waitForFunction(() => document.querySelector('#project-name').textContent.includes('Automated web recovered'));
  assert.deepEqual((await document()).document, recovered.document);

  // Closing an uncertain submission uses lookup/cancel, never a new POST.
  await page.locator('#file-menu-button').click(); await page.locator('#scripts-action').click();
  let closeJob; let closePosts = 0; let closeAccepted;
  const closingAccepted = new Promise(resolve => { closeAccepted = resolve; });
  await page.route('**/api/v1/jobs', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    closePosts++;
    const response = await route.fetch();
    closeJob = await response.json();
    await route.abort('failed'); closeAccepted();
  });
  await page.locator('#script-source').fill('studio.rename {name="must remain cancelled"}; while true do end');
  await page.locator('#script-run').click(); await closingAccepted;
  await page.keyboard.press('Escape'); await page.locator('#script-dialog').waitFor({ state: 'detached' });
  const cancelUntil = Date.now() + 15000; let cancelled;
  do {
    cancelled = await (await fetch(base + '/api/v1/jobs/' + closeJob.id)).json();
    if (cancelled.phase === 'running') await new Promise(resolve => setTimeout(resolve, 50));
  } while (cancelled.phase === 'running' && Date.now() < cancelUntil);
  assert.equal(cancelled.phase, 'cancelled', cancelled.error);
  assert.equal(closePosts, 1, 'Closing must not resubmit an uncertain job');
  assert.deepEqual((await document()).document, recovered.document);
  await page.unroute('**/api/v1/jobs');
  const snapshot = await document();
  const response = await fetch(base + '/api/v1/jobs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    kind: 'batch', batch: { apiVersion: 1, operations: [{ op: 'document.update', name: 'API batch' }] },
    clientId: 'browser-automation', commandId: crypto.randomUUID(), expectedInstanceId: snapshot.instanceId, expectedDocumentRevision: snapshot.documentRevision }) });
  assert.equal(response.status, 202, await response.clone().text());
  const job = await response.json();
  const deadline = Date.now() + 20000; let status;
  do { status = await (await fetch(base + '/api/v1/jobs/' + job.id)).json(); if (status.phase === 'running') await new Promise(resolve => setTimeout(resolve, 50)); } while (status.phase === 'running' && Date.now() < deadline);
  assert.equal(status.phase, 'completed', status.error);
  assert.equal((await document()).document.name, 'API batch');
  assert.deepEqual(errors, []);
  console.log('Web automation: Lua run, dry-run, atomic failure, cancellation, Undo/Redo, lost-response recovery, close cancellation and async batch passed.');
} catch (error) {
  console.error('Scripts panel:', await page.locator('#script-output').textContent().catch(() => 'closed'));
  console.error('Page errors:', errors);
  throw error;
} finally { await browser.close(); }
