import { createRequire } from 'node:module';
import { mkdir, readFile, readdir, rm, access, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1', 'Run this destructive browser gate through Run-Browser-Interaction.ps1.');
assert.ok(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT && process.env.METROIDVANIA_STUDIO_BASE_URL,
  'The isolated browser gate requires an explicit scratch project and server URL.');
const root = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT);
const localRoot = path.resolve(repositoryRoot, 'MetroidvaniaStudio/.local');
const localRelative = path.relative(localRoot, root);
assert.ok(localRelative && !localRelative.startsWith('..') && !path.isAbsolute(localRelative),
  'The destructive browser fixture must stay beneath MetroidvaniaStudio/.local.');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(new URL(base).hostname),
  'The destructive browser fixture may target only a loopback server.');
const apiTimeoutMs = 12000;
const maximumTileGesturePoints = 16384;
const maximumTileGestureWork = 65536;
const performanceBudgets = { optimisticP95Ms: 50, longTaskMaxMs: 50, commitTailMs: 500, responseBytes: 64 * 1024, hugeDispatchMs: 50, hugeCancelTailMs: 500 };
const testFolder = path.join(root, 'Maps/__browser_validation__');
assert.equal(path.dirname(testFolder), path.join(root, 'Maps'));
try { await access(testFolder); throw new Error('Validation folder already exists; it will not be overwritten.'); } catch (error) { if (error.code !== 'ENOENT') throw error; }
const apiFetch = (resource, options = {}) => fetch(base + resource, { ...options, signal: AbortSignal.timeout(apiTimeoutMs) });
const initial = await (await apiFetch('/api/state')).json();
assert.equal(initial.dirty, false, 'Run the interaction gate with a saved workspace.');
let lastClient = 'browser-integration';
const directCommandEnvelopes = [];
const state = async () => (await apiFetch('/api/state')).json();
async function command(action, values = {}, clientId = 'browser-integration') {
  const current = await state();
  const envelope = { action, ...values, clientId, commandId: crypto.randomUUID(),
    expectedInstanceId: current.instanceId, expectedRevision: current.revision };
  assert.ok(typeof envelope.clientId === 'string' && envelope.clientId.length > 0, 'Direct browser commands need a nonempty clientId.');
  assert.ok(typeof envelope.commandId === 'string' && envelope.commandId.length > 0, 'Direct browser commands need a nonempty commandId.');
  directCommandEnvelopes.push(envelope);
  const response = await apiFetch('/api/command', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(envelope) });
  const result = await response.json(); if (!response.ok) throw new Error(JSON.stringify(result)); return result;
}
async function eventually(predicate, message) {
  const deadline = Date.now() + 8000;
  while (Date.now() < deadline) { const current = await state(); if (predicate(current)) return current; await new Promise(resolve => setTimeout(resolve, 80)); }
  throw new Error(message);
}
await mkdir(path.join(root, 'Logs'), { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'ko-KR', deviceScaleFactor: 1 });
const page = await context.newPage(), errors = [], checks = [], commandActions = [], commandRequests = [];
let paintPerformance = null, exactBoundaryPerformance = null, hugeJumpPerformance = null;
await page.addInitScript(() => {
  window.__mapDrawOps = { dashedCameraFrames: 0, cameraLabels: [] };
  const strokeRect = CanvasRenderingContext2D.prototype.strokeRect, fillText = CanvasRenderingContext2D.prototype.fillText;
  CanvasRenderingContext2D.prototype.strokeRect = function(...args) {
    const dash = this.getLineDash();
    if (this.canvas?.id === 'map-canvas' && dash.length === 2 && dash[0] === 6 && dash[1] === 4) window.__mapDrawOps.dashedCameraFrames++;
    return strokeRect.apply(this, args);
  };
  CanvasRenderingContext2D.prototype.fillText = function(value, ...args) {
    if (this.canvas?.id === 'map-canvas' && /camera guide|카메라 가이드/.test(String(value))) window.__mapDrawOps.cameraLabels.push(String(value));
    return fillText.call(this, value, ...args);
  };
});
page.on('pageerror', error => errors.push(error.message));
page.on('request', request => {
  if (!request.url().endsWith('/api/command') || !request.postData()) return;
  const body = JSON.parse(request.postData()); lastClient = body.clientId; commandActions.push(body.action);
  const points = Array.isArray(body.points) ? body.points : [];
  commandRequests.push({
    request, action: body.action, pointCount: points.length, firstPoint: points[0], lastPoint: points.at(-1),
    roomId: body.roomId, erase: body.erase, compactDocument: body.compactDocument === true,
    path: body.path, directory: body.directory, overwrite: body.overwrite === true, brushSize: body.brushSize,
    clientId: body.clientId, commandId: body.commandId,
    expectedInstanceId: body.expectedInstanceId, expectedRevision: body.expectedRevision, baseRevision: body.baseRevision,
    started: performance.now(), ended: 0, bytes: 0
  });
});
page.on('response', async response => {
  const record = commandRequests.find(item => item.request === response.request());
  if (!record) return;
  record.status = response.status();
  try {
    const body = await response.body(); record.bytes = body.byteLength;
    const result = JSON.parse(body.toString('utf8')); record.responseRevision = (result.state || result).revision;
  } catch { /* A failing response is reported by the page and test assertions. */ }
  record.ended = performance.now();
});
async function drag(a, b, button = 'left') { await page.mouse.move(a.x, a.y); await page.mouse.down({ button }); await page.mouse.move(b.x, b.y, { steps: 10 }); await page.mouse.up({ button }); }
async function canvasPixelOn(targetPage, point) {
  return targetPage.locator('#map-canvas').evaluate((canvas, point) => {
    const rect = canvas.getBoundingClientRect(), context = canvas.getContext('2d');
    const x = Math.floor((point.x - rect.left) * canvas.width / rect.width), y = Math.floor((point.y - rect.top) * canvas.height / rect.height);
    return [...context.getImageData(x, y, 1, 1).data];
  }, point);
}
async function canvasPixel(point) { return canvasPixelOn(page, point); }
async function nextPaint() { await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); }
async function clientAt(revision) {
  await page.waitForFunction(({ revision, instanceId }) => {
    const marker = document.querySelector('#status-revision'), parsed = /^r(\d+)$/.exec(marker?.textContent || '');
    return marker?.dataset.instanceId === instanceId && Number(parsed?.[1] || -1) >= revision
      && !document.querySelector('#status-state')?.classList.contains('working');
  }, { revision, instanceId: initial.instanceId });
}
async function completedCommand(action) {
  const deadline = Date.now() + 8000;
  while (Date.now() < deadline) {
    const record = commandRequests.find(item => item.action === action);
    if (record?.ended && record.bytes) return record;
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  throw new Error(`Timed out waiting for ${action} response metrics.`);
}
async function completedCommands(action, count) {
  const deadline = Date.now() + 8000;
  while (Date.now() < deadline) {
    const records = commandRequests.filter(item => item.action === action);
    if (records.length >= count && records.slice(0, count).every(item => item.ended)) return records.slice(0, count);
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  throw new Error(`Timed out waiting for ${count} ${action} responses.`);
}
function percentile(values, quantile) {
  if (!values.length) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * quantile))];
}
try {
  const beforeHttpBoundary = await state();
  const malformedJsonResponse = await apiFetch('/api/command', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{'
  });
  const malformedJsonBody = await malformedJsonResponse.json();
  assert.equal(malformedJsonResponse.status, 400, 'Malformed command JSON must be rejected as a client error.');
  assert.equal(typeof malformedJsonBody.error, 'string', 'Malformed command JSON must return a usable JSON error.');
  const nonObjectJsonResponse = await apiFetch('/api/command', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '[]'
  });
  const nonObjectJsonBody = await nonObjectJsonResponse.json();
  assert.equal(nonObjectJsonResponse.status, 400, 'A non-object command body must be rejected as a client error.');
  assert.equal(typeof nonObjectJsonBody.error, 'string', 'A non-object command must return a usable JSON error.');
  const wrongContentTypeResponse = await apiFetch('/api/command', {
    method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: '{}'
  });
  assert.equal(wrongContentTypeResponse.status, 415, 'A command without a JSON content type must be rejected before parsing.');
  const oversizedResponse = await apiFetch('/api/command', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: '{"padding":"' + 'x'.repeat(32 * 1024 * 1024) + '"}'
  });
  const oversizedBody = await oversizedResponse.json();
  assert.equal(oversizedResponse.status, 413, 'A command above the 32 MiB HTTP limit must be rejected before workspace execution.');
  assert.equal(typeof oversizedBody.error, 'string', 'An oversized command must return a usable JSON error.');
  const afterHttpBoundary = await state();
  assert.equal(afterHttpBoundary.revision, beforeHttpBoundary.revision, 'Rejected HTTP bodies must preserve workspace revision.');
  assert.deepEqual(afterHttpBoundary.document, beforeHttpBoundary.document, 'Rejected HTTP bodies must preserve the document.');
  checks.push('HTTP boundary rejects malformed, non-JSON, and oversized command bodies atomically');

  await command('new', { name: 'Browser integration', discard: true });
  await command('save', { path: '__browser_validation__/working.map.json' });
  await command('options', { layer: 0, tool: 3, shape: 0, material: initial.catalog.materials[0]?.id || 'terrain', brushSize: 16 });
  const brush16 = await state();
  assert.equal(brush16.selection.brushSize, 16, 'Brush size 16 must be accepted.');
  const invalidBrushResponse = await apiFetch('/api/command', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ action: 'options', brushSize: 17, clientId: 'browser-invalid-brush', commandId: crypto.randomUUID(),
      expectedInstanceId: brush16.instanceId, expectedRevision: brush16.revision })
  });
  const invalidBrushBody = await invalidBrushResponse.json(), afterInvalidBrush = await state();
  assert.equal(invalidBrushResponse.status, 400, 'Brush size 17 must be rejected.');
  assert.match(invalidBrushBody.error, /between 1 and 16/i, 'Brush size rejection must report the supported range.');
  assert.equal(afterInvalidBrush.revision, brush16.revision, 'Invalid brush size must preserve the revision.');
  assert.equal(afterInvalidBrush.selection.brushSize, 16, 'Invalid brush size must preserve the selection.');
  assert.deepEqual(afterInvalidBrush.document, brush16.document, 'Invalid brush size must preserve the document.');
  await command('options', { brushSize: 1 }); checks.push('brush size 16 boundary is atomic');
  await page.goto(base); await page.locator('#room-list button').first().waitFor(); await page.waitForTimeout(300);
  const malformedMapRelative = '__browser_validation__/malformed.map.json';
  await writeFile(path.join(root, 'Maps', malformedMapRelative), '{', 'utf8');
  const beforeMalformedOpen = await state(); commandActions.length = 0; commandRequests.length = 0;
  await page.locator('#file-menu-button').click(); await page.locator('#file-menu .menu-popup button').nth(2).click();
  await page.locator('dialog .file-list').getByRole('button', { name: malformedMapRelative, exact: true }).click();
  await page.locator('dialog .accent').click();
  await page.locator('dialog .modal-error').filter({ hasText: /.+/ }).waitFor();
  const malformedOpenRequest = await completedCommand('open'), afterMalformedOpen = await state();
  assert.equal(malformedOpenRequest.status, 400, 'Opening malformed map JSON must be a structured client error, not HTTP 500.');
  assert.equal(await page.locator('dialog').count(), 1, 'A failed Open must leave its dialog available for correction.');
  assert.equal(afterMalformedOpen.revision, beforeMalformedOpen.revision, 'A failed Open must preserve workspace revision.');
  assert.deepEqual(afterMalformedOpen.document, beforeMalformedOpen.document, 'A failed Open must preserve the active document.');
  await page.locator('dialog .dialog-title button').click();

  commandActions.length = 0; commandRequests.length = 0;
  await page.locator('#edit-menu-button').click(); await page.locator('#metadata-action').click();
  await page.locator('dialog textarea').fill('{'); await page.locator('dialog .accent').click();
  await page.locator('dialog .modal-error').filter({ hasText: /.+/ }).waitFor();
  assert.equal(commandRequests.length, 0, 'Malformed metadata JSON must be rejected before sending a command.');
  assert.equal(await page.locator('dialog').count(), 1, 'Malformed metadata JSON must leave its dialog available for correction.');
  await page.locator('dialog .dialog-title button').click();

  const roomX = page.locator('#inspector .fields input').first(), originalRoomX = await roomX.inputValue();
  const beforeBlankRoomInput = await state(); commandActions.length = 0; commandRequests.length = 0;
  await roomX.fill(''); await page.locator('#inspector button.accent').click();
  await page.locator('#toast:not([hidden])').waitFor();
  const afterBlankRoomInput = await state();
  assert.equal(commandRequests.length, 0, 'A blank room coordinate must be rejected before sending a command.');
  assert.equal(afterBlankRoomInput.revision, beforeBlankRoomInput.revision, 'A blank room coordinate must not silently become zero.');
  assert.deepEqual(afterBlankRoomInput.document, beforeBlankRoomInput.document, 'A blank room coordinate must preserve the room.');
  await roomX.fill(originalRoomX); await page.locator('#toast').evaluate(node => node.hidden = true);
  checks.push('file, JSON, and blank numeric input errors remain correctable and atomic');

  assert.equal(await page.locator('#brush-options input[type="number"]').getAttribute('max'), '16', 'The browser brush input must expose the server limit.');
  const brushInput = page.locator('#brush-options input[type="number"]');
  const brushWheelCanvas = await page.locator('#map-canvas').boundingBox();
  const brushWheelCamera = await page.locator('#status-camera').innerText();
  const brushWheelViewport = await page.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio }));
  await page.mouse.move(brushWheelCanvas.x + brushWheelCanvas.width / 2, brushWheelCanvas.y + brushWheelCanvas.height / 2);
  await page.keyboard.down('Control'); await page.mouse.wheel(0, -120); await page.keyboard.up('Control');
  const brushWheelState = await eventually(s => s.selection.brushSize === 2, 'Ctrl+wheel did not increase brush size.');
  await clientAt(brushWheelState.revision);
  assert.equal(await brushInput.inputValue(), '2', 'Ctrl+wheel must update the visible brush size.');
  assert.equal(await page.locator('#status-camera').innerText(), brushWheelCamera, 'Ctrl+wheel must not zoom the map.');
  assert.deepEqual(await page.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio })), brushWheelViewport,
    'Ctrl+wheel over the canvas must prevent the browser zoom default.');
  checks.push('Ctrl+wheel adjusts the brush and prevents browser/map zoom');

  let releaseBrushWheel, markBrushWheel;
  const brushWheelGate = new Promise(resolve => { releaseBrushWheel = resolve; });
  const brushWheelIntercepted = new Promise(resolve => { markBrushWheel = resolve; });
  let heldBrushRequest = false;
  const delayBrushWheel = async route => {
    const body = JSON.parse(route.request().postData() || '{}');
    if (body.action === 'options' && !heldBrushRequest) { heldBrushRequest = true; markBrushWheel(); await brushWheelGate; }
    await route.continue();
  };
  await page.route('**/api/command', delayBrushWheel); commandActions.length = 0; commandRequests.length = 0;
  const dispatchBrushWheels = deltas => page.locator('#map-canvas').evaluate((canvas, deltas) => {
    const started = performance.now(); let canceled = true;
    for (const deltaY of deltas) canceled = !canvas.dispatchEvent(new WheelEvent('wheel', { deltaY, ctrlKey: true, cancelable: true })) && canceled;
    return { canceled, value: document.querySelector('#brush-options input[type="number"]').value, elapsed: performance.now() - started };
  }, deltas);
  assert.equal((await dispatchBrushWheels([-120])).value, '3', 'Brush controls must update before the server replies.');
  await Promise.race([brushWheelIntercepted, new Promise((_, reject) => setTimeout(() => reject(new Error('Brush wheel request was not intercepted.')), 3000))]);
  const brushBurst = await dispatchBrushWheels([...Array(200).fill(-120), ...Array(12).fill(120)]);
  assert.equal(brushBurst.canceled, true, 'Every modified wheel event must suppress the browser default.');
  assert.equal(brushBurst.value, '4', 'A fast wheel burst must use the latest local size and clamp at 16.');
  assert.ok(brushBurst.elapsed < 50, `A 212-event brush burst must avoid a long task (${brushBurst.elapsed.toFixed(2)}ms).`);
  assert.equal(commandRequests.length, 1, 'Rapid wheel events must not enqueue obsolete requests behind an in-flight options command.');
  releaseBrushWheel();
  const coalescedBrushState = await eventually(s => s.selection.brushSize === 4, 'The latest coalesced brush target was lost.');
  await clientAt(coalescedBrushState.revision); await completedCommands('options', 2);
  assert.deepEqual(commandRequests.map(record => record.brushSize), [3, 4], 'Only the in-flight and latest requested brush values should be sent.');
  assert.equal(await brushInput.inputValue(), '4', 'An older response must not replace the newer brush preview.');
  await page.unroute('**/api/command', delayBrushWheel);
  checks.push('rapid Ctrl+wheel previews immediately and coalesces slow requests');

  const modifierWheel = await page.locator('#map-canvas').evaluate(canvas => {
    for (const ctrlKey of [true, false, true]) canvas.dispatchEvent(new WheelEvent('wheel', { deltaY: -20, ctrlKey, cancelable: true }));
    return document.querySelector('#brush-options input[type="number"]').value;
  });
  assert.equal(modifierWheel, '4', 'Fine wheel deltas must not leak between brush resizing and zoom.');
  await dispatchBrushWheels([-20]);
  const fineBrushState = await eventually(s => s.selection.brushSize === 5, 'Fine Ctrl+wheel input must accumulate within the same mode.');
  await clientAt(fineBrushState.revision);
  assert.equal(await page.locator('#status-camera').innerText(), brushWheelCamera, 'Substep zoom deltas must not move the camera.');
  assert.equal((await dispatchBrushWheels([12000])).value, '1', 'Wheel down must clamp at the minimum brush size.');
  const minimumBrushState = await eventually(s => s.selection.brushSize === 1, 'Brush minimum was not committed.');
  await clientAt(minimumBrushState.revision);
  checks.push('brush wheel bounds and modifier-specific fine deltas are stable');
  await nextPaint();
  const editCanvas = await page.locator('#map-canvas').boundingBox(), beforeCameraPreview = await state();
  assert.equal(await page.locator('#camera-preview').getAttribute('aria-pressed'), 'false', 'Game view must start as an explicit inactive mode.');
  assert.equal(await page.locator('#map-canvas').getAttribute('data-view-mode'), 'edit', 'Normal editing must identify itself separately from the camera preview.');
  let cameraDrawOps = await page.evaluate(() => window.__mapDrawOps);
  assert.equal(cameraDrawOps.dashedCameraFrames, 0, 'Normal editing must not draw the old dashed camera guide.');
  assert.deepEqual(cameraDrawOps.cameraLabels, [], 'Normal editing must not draw a camera guide label over the map.');
  checks.push('normal editing has no camera guide');

  await page.locator('#camera-preview').click();
  await page.waitForFunction(() => document.querySelector('#workspace')?.classList.contains('camera-preview'));
  await page.waitForFunction(() => { const canvas = document.querySelector('#map-canvas'); return Number(canvas?.dataset.cameraScale) >= 1 && canvas.getBoundingClientRect().width > 1200; });
  await nextPaint();
  const previewCanvas = await page.locator('#map-canvas').boundingBox(), previewBacking = await page.locator('#map-canvas').evaluate(canvas => ({ width: canvas.width, height: canvas.height, resolution: canvas.dataset.cameraResolution, scale: Number(canvas.dataset.cameraScale) }));
  const profile = beforeCameraPreview.catalog.camera;
  const expectedCameraScale = Math.max(1, Math.min(32, Math.floor(Math.min(previewBacking.width / profile.referenceWidth, previewBacking.height / profile.referenceHeight))));
  assert.equal(await page.locator('#camera-preview').getAttribute('aria-pressed'), 'true', 'Game view must expose its active toggle state.');
  assert.equal(await page.locator('#undo').isDisabled(), true, 'Game view must disable authoring history buttons.');
  assert.equal(await page.locator('#redo').isDisabled(), true, 'Game view must disable authoring history buttons.');
  assert.equal(await page.locator('#metadata-action').isDisabled(), true, 'Game view must disable metadata authoring.');
  assert.equal(await page.locator('#inspector-toggle').isDisabled(), true, 'Game view must disable its hidden inspector control.');
  assert.equal(await page.locator('#view-toolbar select').count(), 0, 'Game view must not show MiniMap-only outline controls.');
  assert.ok(previewCanvas.width > editCanvas.width + 300, 'Game view must reclaim the authoring side panels for a larger preview.');
  assert.equal(previewBacking.resolution, `${profile.referenceWidth}x${profile.referenceHeight}`);
  assert.equal(previewBacking.scale, expectedCameraScale, 'Game view must choose the largest fitting integer physical-pixel scale.');
  const cameraStatus = await page.locator('#status-camera').innerText();
  assert.match(cameraStatus, new RegExp(`${profile.referenceWidth}×${profile.referenceHeight}`));
  assert.match(cameraStatus, new RegExp(`×${expectedCameraScale}`));
  assert.match(cameraStatus, new RegExp(profile.orthographicSize.toFixed(3).replace('.', '\\.')));
  assert.match(cameraStatus, new RegExp(`PPU ${profile.ppu}`));
  const gapX = previewCanvas.width - profile.referenceWidth * expectedCameraScale, gapY = previewCanvas.height - profile.referenceHeight * expectedCameraScale;
  assert.ok(gapX > 2 || gapY > 2, 'The camera preview fixture needs a visible letterbox sample.');
  const outside = gapX > 2 ? { x: previewCanvas.x + gapX / 4, y: previewCanvas.y + previewCanvas.height / 2 } : { x: previewCanvas.x + previewCanvas.width / 2, y: previewCanvas.y + gapY / 4 };
  const previewCenter = { x: previewCanvas.x + previewCanvas.width / 2, y: previewCanvas.y + previewCanvas.height / 2 };
  assert.deepEqual(await canvasPixel(outside), [11, 11, 11, 255], 'Pixels outside the exact camera frame must be black letterbox.');
  assert.notDeepEqual(await canvasPixel(previewCenter), [11, 11, 11, 255], 'The camera frame center must render the selected room.');
  await page.locator('#workspace').evaluate(workspace => workspace.classList.add('inspector-hidden'));
  await page.setViewportSize({ width: 1000, height: 900 });
  await page.waitForFunction(() => { const canvas = document.querySelector('#map-canvas'); return canvas.getBoundingClientRect().width > 900 && canvas.dataset.cameraScale === '3'; });
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.waitForFunction(scale => { const canvas = document.querySelector('#map-canvas'); return canvas.getBoundingClientRect().width > 1200 && canvas.dataset.cameraScale === String(scale); }, expectedCameraScale);
  await page.locator('#workspace').evaluate(workspace => workspace.classList.remove('inspector-hidden'));
  const commandsBeforeReadOnlyClick = commandActions.length;
  await page.mouse.click(previewCenter.x, previewCenter.y); await page.waitForTimeout(80);
  for (const shortcut of ['u', 'Delete', 'Backspace', 'Control+z', 'Control+y', 'Control+c', 'Control+x', 'Control+v', 'Control+a'])
    await page.keyboard.press(shortcut);
  await dispatchBrushWheels([-120]);
  assert.equal(await page.locator('#map-canvas').getAttribute('data-camera-scale'), String(expectedCameraScale),
    'Ctrl+wheel must leave the view-only Game view camera scale unchanged.');
  await page.waitForTimeout(100);
  const afterReadOnlyClick = await state();
  assert.equal(afterReadOnlyClick.revision, beforeCameraPreview.revision, 'Game view must not edit the map.');
  assert.deepEqual(afterReadOnlyClick.document, beforeCameraPreview.document, 'Game view must remain read-only.');
  assert.deepEqual(afterReadOnlyClick.selection, beforeCameraPreview.selection, 'Game view must ignore authoring shortcuts.');
  assert.equal(commandActions.length, commandsBeforeReadOnlyClick, 'Game view pointer and every authoring shortcut must send no editing command.');
  cameraDrawOps = await page.evaluate(() => window.__mapDrawOps);
  assert.equal(cameraDrawOps.dashedCameraFrames, 0, 'Game view must use clipping instead of the old dashed frame.');
  assert.deepEqual(cameraDrawOps.cameraLabels, [], 'Game view must not paint a camera label over the preview.');
  await page.locator('#language').selectOption('EN');
  await page.getByRole('button', { name: 'Game view', exact: true }).waitFor();
  assert.match(await page.locator('#status-camera').innerText(), /Game view/);
  assert.match(await page.locator('#canvas-help').innerText(), /current room/i);
  await page.locator('#language').selectOption('KR');
  await page.getByRole('button', { name: '게임 시야', exact: true }).waitFor();
  checks.push('camera preview is large, exact, clipped, labelled and read-only');

  await page.mouse.move(previewCenter.x, previewCenter.y); await page.mouse.wheel(0, -120);
  const zoomedCameraScale = Math.min(32, expectedCameraScale + 1);
  await page.waitForFunction(scale => document.querySelector('#map-canvas')?.dataset.cameraScale === String(scale), zoomedCameraScale);
  assert.equal(await page.locator('#camera-preview').getAttribute('aria-pressed'), 'true', 'Pixel zoom must keep Game view active.');
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => !document.querySelector('#workspace')?.classList.contains('camera-preview'));
  await page.waitForFunction(() => document.querySelector('#map-canvas')?.dataset.viewMode === 'edit');
  assert.equal(await page.locator('#camera-preview').getAttribute('aria-pressed'), 'false', 'Escape must return to editing.');
  const restoredCanvas = await page.locator('#map-canvas').boundingBox(), afterCameraPreview = await state();
  assert.ok(Math.abs(restoredCanvas.width - editCanvas.width) < 1, 'Leaving Game view must restore the authoring layout.');
  assert.equal(afterCameraPreview.revision, beforeCameraPreview.revision);
  assert.deepEqual(afterCameraPreview.document, beforeCameraPreview.document);
  const restoredStatus = await page.locator('#status-camera').innerText();
  assert.match(restoredStatus, /편집 화면/);
  assert.match(restoredStatus, new RegExp(`${(restoredCanvas.width / 32).toFixed(2)}×${(restoredCanvas.height / 32).toFixed(2)}`), 'Restored edit status must use the resized authoring viewport.');

  await page.locator('#camera-preview').click();
  await page.waitForFunction(() => document.querySelector('#workspace')?.classList.contains('camera-preview'));
  await page.locator('#view-toolbar button').first().click();
  await page.waitForFunction(() => !document.querySelector('#workspace')?.classList.contains('camera-preview') && document.querySelector('#map-canvas')?.dataset.viewMode === 'overview');
  await nextPaint();
  const fitCanvas = await page.locator('#map-canvas').boundingBox();
  const visibleRooms = beforeCameraPreview.document.rooms.filter(room => room.visible);
  const left = Math.min(...visibleRooms.map(room => room.x)), right = Math.max(...visibleRooms.map(room => room.x + room.width));
  const bottom = Math.min(...visibleRooms.map(room => room.y)), top = Math.max(...visibleRooms.map(room => room.y + room.height));
  const expectedFitScale = Math.min((fitCanvas.width - 50) / ((right - left) * 16), (fitCanvas.height - 50) / ((top - bottom) * 16));
  assert.match(await page.locator('#status-camera').innerText(), new RegExp(`×${expectedFitScale.toFixed(2)}`), 'Fit map must calculate against the restored authoring width.');
  const fitCenter = { x: fitCanvas.x + fitCanvas.width / 2, y: fitCanvas.y + fitCanvas.height / 2 };
  await page.mouse.move(fitCenter.x, fitCenter.y); await page.mouse.wheel(0, -120);
  const expectedEditScale = Math.max(1, Math.min(32, Math.round(expectedFitScale) + 1));
  await page.waitForFunction(scale => document.querySelector('#map-canvas')?.dataset.viewMode === 'edit' && document.querySelector('#status-camera')?.textContent?.includes(`×${scale}`), expectedEditScale);
  assert.equal(expectedEditScale, 2, 'The interaction fixture must return to its 2× edit scale.');
  checks.push('Fit map exits camera preview using the restored layout');

  await page.locator('#camera-preview').click();
  await page.locator('#tabs button').nth(1).click();
  await page.locator('#tabs button').first().click();
  await page.waitForFunction(() => document.querySelector('#map-canvas')?.dataset.viewMode === 'edit');
  assert.equal(await page.locator('#camera-preview').getAttribute('aria-pressed'), 'false', 'Opening MiniMap must close Game view instead of leaving hidden preview state.');
  checks.push('camera preview pixel zoom and exit restore the edit view');

  const highDpiContext = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'ko-KR', deviceScaleFactor: 2 });
  const highDpiPage = await highDpiContext.newPage();
  highDpiPage.on('pageerror', error => errors.push(error.message));
  highDpiPage.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await highDpiPage.goto(base); await highDpiPage.locator('#room-list button').first().waitFor();
  await highDpiPage.locator('#camera-preview').click();
  await highDpiPage.waitForFunction(() => document.querySelector('#workspace')?.classList.contains('camera-preview') && Number(document.querySelector('#map-canvas')?.dataset.cameraScale) >= 1);
  await highDpiPage.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  const highDpiCanvas = await highDpiPage.locator('#map-canvas').boundingBox();
  const highDpiBacking = await highDpiPage.locator('#map-canvas').evaluate(canvas => ({ width: canvas.width, height: canvas.height, scale: Number(canvas.dataset.cameraScale), dpr: devicePixelRatio }));
  const expectedHighDpiScale = Math.max(1, Math.min(32, Math.floor(Math.min(highDpiBacking.width / profile.referenceWidth, highDpiBacking.height / profile.referenceHeight))));
  assert.equal(highDpiBacking.dpr, 2);
  assert.equal(highDpiBacking.scale, expectedHighDpiScale, 'High-DPI Game view must fit by physical pixels.');
  const highGapX = highDpiCanvas.width - profile.referenceWidth * expectedHighDpiScale / highDpiBacking.dpr;
  const highGapY = highDpiCanvas.height - profile.referenceHeight * expectedHighDpiScale / highDpiBacking.dpr;
  assert.ok(highGapX > 2 || highGapY > 2, 'The high-DPI fixture needs a visible letterbox sample.');
  const highOutside = highGapX > 2 ? { x: highDpiCanvas.x + highGapX / 4, y: highDpiCanvas.y + highDpiCanvas.height / 2 } : { x: highDpiCanvas.x + highDpiCanvas.width / 2, y: highDpiCanvas.y + highGapY / 4 };
  const highCenter = { x: highDpiCanvas.x + highDpiCanvas.width / 2, y: highDpiCanvas.y + highDpiCanvas.height / 2 };
  assert.deepEqual(await canvasPixelOn(highDpiPage, highOutside), [11, 11, 11, 255]);
  assert.notDeepEqual(await canvasPixelOn(highDpiPage, highCenter), [11, 11, 11, 255]);
  await highDpiContext.close();
  checks.push('camera preview remains pixel-perfect at DPR 2');
  const bounds = await page.locator('#map-canvas').boundingBox();
  const point = (x, y) => ({ x: bounds.x + bounds.width / 2 + (x - 20) * 32, y: bounds.y + bounds.height / 2 - (y - 12) * 32 });
  const strokeStart = point(14.5, 12.5), strokeEnd = point(20.5, 12.5), beforeStroke = await state();
  const strokeMid = point(17.5, 12.5), strokeSamples = [strokeStart, strokeMid, strokeEnd];
  const blankPixels = await Promise.all(strokeSamples.map(canvasPixel)), blankPixel = blankPixels[0]; commandActions.length = 0; commandRequests.length = 0;
  await page.evaluate(() => {
    window.__paintFrameTimings = []; window.__paintLongTasks = [];
    window.__paintMoveListener = event => {
      if (event.target?.id !== 'map-canvas' || !(event.buttons & 1)) return;
      const canvas = event.target, context = canvas.getContext('2d'), rect = canvas.getBoundingClientRect(), started = performance.now();
      const coalesced = typeof event.getCoalescedEvents === 'function' ? event.getCoalescedEvents() : [];
      const target = coalesced.length ? coalesced[coalesced.length - 1] : event;
      const x = Math.floor((target.clientX - rect.left) * canvas.width / rect.width), y = Math.floor((target.clientY - rect.top) * canvas.height / rect.height);
      const before = [...context.getImageData(x, y, 1, 1).data];
      queueMicrotask(() => {
        const immediate = [...context.getImageData(x, y, 1, 1).data], immediateLatency = performance.now() - started;
        requestAnimationFrame(() => requestAnimationFrame(() => {
        const after = [...context.getImageData(x, y, 1, 1).data];
        window.__paintFrameTimings.push({ latency: performance.now() - started, immediateLatency,
          immediateChanged: immediate.some((value, index) => value !== before[index]),
          changed: after.some((value, index) => value !== before[index]), x, y, before, after });
        }));
      });
    };
    // Raw painting precedes pointermove. Sample before the earliest input the
    // browser supports, otherwise the "before" pixel is already painted.
    window.__paintMoveEvent = 'onpointerrawupdate' in window ? 'pointerrawupdate' : 'pointermove';
    document.querySelector('#map-canvas').addEventListener(window.__paintMoveEvent, window.__paintMoveListener, { capture: true });
    try {
      window.__paintObserver = new PerformanceObserver(list => { for (const entry of list.getEntries()) window.__paintLongTasks.push(entry.duration); });
      window.__paintObserver.observe({ type: 'longtask', buffered: false });
    } catch { window.__paintObserver = null; }
  });
  await page.mouse.move(strokeStart.x, strokeStart.y); await page.mouse.down({ button: 'left' });
  for (let x = 15.5; x <= 20.5; x++) await page.mouse.move(point(x, 12.5).x, strokeStart.y);
  assert.equal((await dispatchBrushWheels([-120])).value, '1', 'Ctrl+wheel during an active tile stroke must preserve its fixed brush size.');
  await nextPaint();
  const gestureTiming = await page.evaluate(() => {
    document.querySelector('#map-canvas').removeEventListener(window.__paintMoveEvent, window.__paintMoveListener, { capture: true });
    if (window.__paintObserver) {
      for (const entry of window.__paintObserver.takeRecords()) window.__paintLongTasks.push(entry.duration);
      window.__paintObserver.disconnect();
    }
    return { frames: window.__paintFrameTimings, longTasks: window.__paintLongTasks };
  });
  const heldStroke = await state(), optimisticPixels = await Promise.all(strokeSamples.map(canvasPixel));
  assert.equal(heldStroke.revision, beforeStroke.revision, 'A held tile stroke must not contact or mutate the server.');
  assert.equal(heldStroke.document.rooms[0].foreground.length, 0, 'A held tile stroke must remain client-local until pointerup.');
  strokeSamples.forEach((_, index) => assert.notDeepEqual(optimisticPixels[index], blankPixels[index], `The local canvas must preview the held stroke sample ${index + 1}.`));
  assert.deepEqual(commandActions, [], 'A held tile stroke must not issue incremental server commands.');
  const pointerUpAt = performance.now(); await page.mouse.up({ button: 'left' });
  let current = await eventually(s => s.document.rooms[0].foreground.length === 7, 'Continuous left drag did not paint every cell.');
  assert.deepEqual(current.document.rooms[0].foreground.map(c => c.x).sort((a,b) => a-b), [14,15,16,17,18,19,20]); checks.push('left drag paints continuous tiles');
  const strokeRequest = await completedCommand('tileGesture');
  assert.deepEqual(commandActions, ['tileGesture'], 'A complete tile stroke must use one batched tileGesture request.'); checks.push('tile stroke previews locally and commits in one request');
  assert.equal(strokeRequest.expectedInstanceId, beforeStroke.instanceId, 'A stroke must stay bound to the server instance where it began.');
  assert.equal(strokeRequest.expectedRevision, beforeStroke.revision, 'A stroke must keep its pointerdown revision expectation.');
  assert.equal(strokeRequest.baseRevision, beforeStroke.revision, 'A stroke base revision must match its fixed command expectation.');
  assert.equal(strokeRequest.compactDocument, true, 'Live tile strokes must request a compact response instead of downloading the document again.');
  await clientAt(current.revision); await nextPaint();
  const committedPixels = await Promise.all(strokeSamples.map(canvasPixel));
  strokeSamples.forEach((_, index) => assert.notDeepEqual(committedPixels[index], blankPixels[index], `Compact commit must retain painted pixel ${index + 1} after its optimistic overlay is cleared.`));
  const optimisticP95Ms = percentile(gestureTiming.frames.map(sample => sample.latency), .95), longTaskMaxMs = Math.max(0, ...gestureTiming.longTasks), commitTailMs = strokeRequest.ended - pointerUpAt;
  paintPerformance = { requests: commandActions.length, heldPreview: true, moveSamples: gestureTiming.frames.length, optimisticP95Ms: +optimisticP95Ms.toFixed(2), longTasks: gestureTiming.longTasks.length, longTaskMaxMs: +longTaskMaxMs.toFixed(2), commitTailMs: +commitTailMs.toFixed(2), responseBytes: strokeRequest.bytes };
  assert.ok(gestureTiming.frames.length >= 5, 'The optimistic latency sample needs at least five pointer moves.');
  const unchangedFrames = gestureTiming.frames.filter(sample => !sample.changed);
  assert.equal(unchangedFrames.length, 0, `Every sampled pointer endpoint must be painted by its next frame: ${JSON.stringify(unchangedFrames)}`);
  assert.ok(optimisticP95Ms <= performanceBudgets.optimisticP95Ms, `Optimistic paint p95 ${optimisticP95Ms.toFixed(1)}ms exceeded ${performanceBudgets.optimisticP95Ms}ms.`);
  assert.ok(longTaskMaxMs <= performanceBudgets.longTaskMaxMs, `Paint produced a ${longTaskMaxMs.toFixed(1)}ms long task.`);
  assert.ok(commitTailMs <= performanceBudgets.commitTailMs, `Stroke commit tail ${commitTailMs.toFixed(1)}ms exceeded ${performanceBudgets.commitTailMs}ms.`);
  assert.ok(strokeRequest.bytes <= performanceBudgets.responseBytes, `Stroke response ${strokeRequest.bytes}B exceeded ${performanceBudgets.responseBytes}B.`);
  checks.push('optimistic paint latency and commit stay within budget');
  await page.keyboard.press('Control+z'); current = await eventually(s => s.document.rooms[0].foreground.length === 0, 'Undo did not remove the whole stroke.'); await clientAt(current.revision);
  await page.keyboard.press('Control+y'); current = await eventually(s => s.document.rooms[0].foreground.length === 7, 'Redo did not restore the stroke.'); await clientAt(current.revision); checks.push('one stroke is one undo/redo');

  const bentStart = point(10.5, 5.5), bentPath = [point(13.5, 5.5), point(13.5, 8.5), point(16.5, 8.5)];
  const bentSamples = [bentStart, point(12.5, 5.5), point(13.5, 7.5), bentPath[2]], bentBlank = await Promise.all(bentSamples.map(canvasPixel));
  const beforeBent = await state(); commandActions.length = 0; commandRequests.length = 0;
  await page.evaluate(() => {
    window.__browserTestPointerId = 0;
    document.querySelector('#map-canvas').addEventListener('pointerdown', event => { window.__browserTestPointerId = event.pointerId; }, { capture: true, once: true });
  });
  await page.mouse.move(bentStart.x, bentStart.y); await page.mouse.down({ button: 'left' });
  await page.locator('#map-canvas').evaluate((canvas, samples) => {
    const pointer = { bubbles: true, cancelable: true, pointerId: window.__browserTestPointerId, pointerType: 'mouse', isPrimary: true };
    const coalesced = samples.map(sample => new PointerEvent('pointermove', { ...pointer, button: -1, buttons: 1, clientX: sample.x, clientY: sample.y }));
    const move = new PointerEvent('pointermove', { ...pointer, button: -1, buttons: 1, clientX: samples.at(-1).x, clientY: samples.at(-1).y });
    Object.defineProperty(move, 'getCoalescedEvents', { value: () => coalesced });
    canvas.dispatchEvent(move);
  }, bentPath);
  await nextPaint();
  const heldBent = await state(), bentPreview = await Promise.all(bentSamples.map(canvasPixel));
  assert.equal(heldBent.revision, beforeBent.revision, 'A held bent stroke must remain client-local.');
  bentSamples.forEach((_, index) => assert.notDeepEqual(bentPreview[index], bentBlank[index], `The held bent stroke must paint sample ${index + 1}.`));
  assert.deepEqual(commandActions, [], 'A held bent stroke must not stream commands.');
  await page.locator('#map-canvas').evaluate((canvas, target) => {
    canvas.dispatchEvent(new PointerEvent('pointerup', {
      bubbles: true, cancelable: true, pointerId: window.__browserTestPointerId, pointerType: 'mouse', isPrimary: true,
      button: 0, buttons: 0, clientX: target.x, clientY: target.y
    }));
  }, bentPath.at(-1));
  await page.mouse.up({ button: 'left' });
  current = await eventually(s => s.document.rooms[0].foreground.length === 17, 'Bent stroke did not preserve its complete path.'); await clientAt(current.revision);
  const bentActual = new Set(current.document.rooms[0].foreground.filter(cell => cell.y <= 8).map(cell => `${cell.x},${cell.y}`));
  const bentExpected = new Set(['10,5', '11,5', '12,5', '13,5', '13,6', '13,7', '13,8', '14,8', '15,8', '16,8']);
  assert.deepEqual(bentActual, bentExpected, 'Bent stroke must retain its turns instead of shortcutting start to end.');
  assert.deepEqual(commandActions, ['tileGesture'], 'Bent stroke must commit in one tileGesture request.'); checks.push('coalesced bent preview and batch preserve every turn');
  await page.keyboard.press('Control+z'); current = await eventually(s => s.document.rooms[0].foreground.length === 7, 'Bent stroke Undo failed.'); await clientAt(current.revision);

  const paintedPixel = await canvasPixel(strokeStart), beforeErase = await state(); commandActions.length = 0; commandRequests.length = 0;
  await page.mouse.move(strokeStart.x, strokeStart.y); await page.mouse.down({ button: 'right' }); await page.mouse.move(strokeEnd.x, strokeEnd.y, { steps: 10 }); await nextPaint();
  const heldErase = await state(), optimisticErasePixel = await canvasPixel(strokeStart);
  assert.equal(heldErase.revision, beforeErase.revision, 'A held erase stroke must not contact or mutate the server.');
  assert.equal(heldErase.document.rooms[0].foreground.length, 7, 'A held erase stroke must remain client-local until pointerup.');
  assert.notDeepEqual(optimisticErasePixel, paintedPixel, 'The local canvas must hide erased pixels before pointerup.');
  assert.deepEqual(commandActions, [], 'A held erase stroke must not issue incremental server commands.');
  await page.mouse.up({ button: 'right' });
  current = await eventually(s => s.document.rooms[0].foreground.length === 0, 'Right drag did not erase.'); await clientAt(current.revision); checks.push('right drag erases without panning');
  assert.deepEqual(commandActions, ['tileGesture'], 'A complete erase stroke must use one batched tileGesture request.'); checks.push('erase stroke previews locally and commits in one request');
  const beforeEmptyErase = await state(); commandActions.length = 0; commandRequests.length = 0;
  await page.mouse.click(strokeStart.x, strokeStart.y, { button: 'right' }); await page.waitForTimeout(100);
  assert.equal((await state()).revision, beforeEmptyErase.revision, 'Erasing an empty cell must remain a client-side no-op.');
  assert.equal(commandRequests.length, 0, 'Erasing an empty cell must not allocate an overlay or send a command.');
  checks.push('empty right erase allocates and sends nothing');

  const exactStart = point(14.5, 9.5), exactTarget = { x: exactStart.x + (maximumTileGesturePoints - 1) * 32, y: exactStart.y };
  const beforeExactBoundary = await state(); commandActions.length = 0; commandRequests.length = 0;
  await page.evaluate(() => {
    window.__browserTestPointerId = 0;
    document.querySelector('#map-canvas').addEventListener('pointerdown', event => { window.__browserTestPointerId = event.pointerId; }, { capture: true, once: true });
  });
  await page.mouse.move(exactStart.x, exactStart.y); await page.mouse.down({ button: 'left' });
  const exactDispatchStarted = performance.now();
  await page.locator('#map-canvas').evaluate((canvas, target) => {
    canvas.dispatchEvent(new PointerEvent('pointermove', {
      bubbles: true, cancelable: true, pointerId: window.__browserTestPointerId, pointerType: 'mouse', isPrimary: true,
      button: -1, buttons: 1, clientX: target.x, clientY: target.y
    }));
  }, exactTarget);
  const exactDispatchMs = performance.now() - exactDispatchStarted;
  assert.ok(exactDispatchMs < performanceBudgets.hugeDispatchMs, `An exact-limit pointer jump took ${exactDispatchMs.toFixed(1)}ms before pointerup.`);
  assert.equal((await state()).revision, beforeExactBoundary.revision, 'An exact-limit stroke must remain client-local until pointerup.');
  assert.deepEqual(commandActions, [], 'An exact-limit held stroke must not stream commands.');
  const exactPointerUpAt = performance.now();
  await page.locator('#map-canvas').evaluate((canvas, target) => {
    canvas.dispatchEvent(new PointerEvent('pointerup', {
      bubbles: true, cancelable: true, pointerId: window.__browserTestPointerId, pointerType: 'mouse', isPrimary: true,
      button: 0, buttons: 0, clientX: target.x, clientY: target.y
    }));
  }, exactTarget);
  await page.mouse.up({ button: 'left' });
  const expectedExactCells = Math.max(0, beforeExactBoundary.document.rooms[0].width - 14);
  current = await eventually(s => s.document.rooms[0].foreground.filter(cell => cell.y === 9).length === expectedExactCells, 'The exact 16,384-cell stroke was not accepted.'); await clientAt(current.revision);
  const exactRequest = await completedCommand('tileGesture');
  assert.deepEqual(commandActions, ['tileGesture'], 'An exact-limit stroke must commit in one request.');
  assert.equal(exactRequest.pointCount, 2, 'A straight exact-limit stroke must send only its path endpoints.');
  assert.equal(Math.max(Math.abs(exactRequest.lastPoint.x - exactRequest.firstPoint.x), Math.abs(exactRequest.lastPoint.y - exactRequest.firstPoint.y)) + 1,
    maximumTileGesturePoints, 'The compact endpoints must still describe exactly 16,384 raster cells.');
  assert.equal(exactRequest.status, 200, 'The server must accept exactly 16,384 raster cells.');
  exactBoundaryPerformance = { points: exactRequest.pointCount, requests: commandActions.length, dispatchMs: +exactDispatchMs.toFixed(2), commitTailMs: +(exactRequest.ended - exactPointerUpAt).toFixed(2), responseBytes: exactRequest.bytes };
  checks.push('exact 16,384-cell boundary uses compact endpoints and commits once');
  await page.keyboard.press('Control+z'); current = await eventually(s => s.document.rooms[0].foreground.filter(cell => cell.y === 9).length === 0, 'Exact-limit stroke Undo failed.'); await clientAt(current.revision);

  current = await command('options', { brushSize: 16 }); await clientAt(current.revision);
  const brushSize = 16;
  const rejectedRasterLength = Math.floor((maximumTileGestureWork - 3 * brushSize * brushSize) / (2 * brushSize - 1)) + 2;
  const beforeExpandedOverflow = await state(); commandActions.length = 0; commandRequests.length = 0;
  await page.evaluate(() => {
    window.__browserTestPointerId = 0;
    document.querySelector('#map-canvas').addEventListener('pointerdown', event => { window.__browserTestPointerId = event.pointerId; }, { capture: true, once: true });
  });
  await page.mouse.move(strokeStart.x, strokeStart.y); await page.mouse.down({ button: 'left' });
  await page.locator('#map-canvas').evaluate((canvas, target) => {
    canvas.dispatchEvent(new PointerEvent('pointermove', {
      bubbles: true, cancelable: true, pointerId: window.__browserTestPointerId, pointerType: 'mouse', isPrimary: true,
      button: -1, buttons: 1, clientX: target.x, clientY: target.y
    }));
  }, { x: strokeStart.x + (rejectedRasterLength - 1) * 32, y: strokeStart.y });
  await page.mouse.up({ button: 'left' });
  await page.waitForFunction(() => document.querySelector('#toast')?.textContent?.includes('65,536'));
  const afterExpandedOverflow = await state();
  assert.equal(commandRequests.length, 0, 'A brush-expanded overflow must be cancelled before sending a request.');
  assert.equal(afterExpandedOverflow.revision, beforeExpandedOverflow.revision, 'A brush-expanded overflow must preserve server state.');
  assert.deepEqual(afterExpandedOverflow.document, beforeExpandedOverflow.document, 'A brush-expanded overflow must discard its local preview.');
  current = await command('options', { brushSize: 1 }); await clientAt(current.revision);
  checks.push('brush-expanded 65,536-cell limit cancels locally before release');

  const beforeHugeJump = await state(); commandActions.length = 0; commandRequests.length = 0;
  await page.evaluate(() => {
    window.__browserTestPointerId = 0;
    document.querySelector('#map-canvas').addEventListener('pointerdown', event => { window.__browserTestPointerId = event.pointerId; }, { capture: true, once: true });
  });
  await page.mouse.move(strokeStart.x, strokeStart.y); await page.mouse.down({ button: 'left' });
  const jumpStarted = performance.now();
  await page.locator('#map-canvas').evaluate((canvas, target) => {
    canvas.dispatchEvent(new PointerEvent('pointermove', {
      bubbles: true, cancelable: true, pointerId: window.__browserTestPointerId, pointerType: 'mouse', isPrimary: true,
      button: -1, buttons: 1, clientX: target.x, clientY: target.y
    }));
  }, { x: strokeStart.x + 20000 * 32, y: strokeStart.y });
  const jumpDispatchMs = performance.now() - jumpStarted;
  assert.ok(jumpDispatchMs < performanceBudgets.hugeDispatchMs, `A capped overview pointer jump took ${jumpDispatchMs.toFixed(1)}ms before pointerup.`);
  assert.equal((await state()).revision, beforeHugeJump.revision, 'A capped pointer jump must remain client-local until pointerup.');
  assert.deepEqual(commandActions, [], 'A capped pointer jump must not stream incremental commands.');
  const hugePointerUpAt = performance.now(); await page.mouse.up({ button: 'left' });
  await page.locator('#toast:not([hidden])').waitFor({ timeout: 8000 });
  const afterHugeJump = await state(); await nextPaint();
  assert.deepEqual(commandActions, [], 'An overflowing pointer jump must be cancelled without a server command.');
  assert.equal(commandRequests.length, 0, 'An overflowing pointer jump must send no request body.');
  assert.match(await page.locator('#toast').innerText(), /16,?384/, 'The local overflow cancellation must be visible to the user.');
  assert.equal(afterHugeJump.revision, beforeHugeJump.revision, 'A cancelled pointer jump must preserve the server revision.');
  assert.deepEqual(afterHugeJump.document, beforeHugeJump.document, 'A cancelled pointer jump must preserve the document.');
  assert.deepEqual(await canvasPixel(strokeStart), blankPixel, 'A cancelled pointer jump must clear its optimistic overlay.');
  const hugeCancelTailMs = performance.now() - hugePointerUpAt;
  assert.ok(hugeCancelTailMs <= performanceBudgets.hugeCancelTailMs, `Capped pointer jump cancellation took ${hugeCancelTailMs.toFixed(1)}ms.`);
  hugeJumpPerformance = { serverRequests: 0, cancelledLocally: true, dispatchMs: +jumpDispatchMs.toFixed(2), cancelTailMs: +hugeCancelTailMs.toFixed(2), responseBytes: 0 };
  checks.push('huge overview pointer jump is capped and cancelled locally without state loss');
  await page.locator('#toast').evaluate(node => node.hidden = true);

  await page.keyboard.press('Control+z'); current = await eventually(s => s.document.rooms[0].foreground.length === 7, 'Erase undo failed.'); await clientAt(current.revision);

  const delayedMaterial = initial.catalog.materials[1];
  assert.ok(delayedMaterial, 'The writer-rebase test needs a second tile material.');
  let releaseOptions, markOptionsIntercepted;
  const optionsGate = new Promise(resolve => { releaseOptions = resolve; });
  const optionsIntercepted = new Promise(resolve => { markOptionsIntercepted = resolve; });
  let delayedOnce = false;
  const delayOptions = async route => {
    const body = JSON.parse(route.request().postData() || '{}');
    if (!delayedOnce && body.action === 'options' && body.material === delayedMaterial.id) {
      delayedOnce = true; markOptionsIntercepted(); await optionsGate;
    }
    await route.continue();
  };
  await page.route('**/api/command', delayOptions); commandActions.length = 0; commandRequests.length = 0;
  await page.locator('#palette .palette-item').nth(1).evaluate(button => button.click());
  await Promise.race([optionsIntercepted, new Promise((_, reject) => setTimeout(() => reject(new Error('Options request was not intercepted.')), 3000))]);
  const delayedStart = point(14.5, 10.5), delayedEnd = point(20.5, 10.5), beforeDelayedStroke = await state();
  const delayedBlankPixels = await Promise.all([delayedStart, point(17.5, 10.5), delayedEnd].map(canvasPixel));
  await page.mouse.move(delayedStart.x, delayedStart.y); await page.mouse.down({ button: 'left' }); await page.mouse.move(delayedEnd.x, delayedEnd.y, { steps: 10 }); await nextPaint(); await page.mouse.up({ button: 'left' }); await nextPaint();
  const blockedPixels = await Promise.all([delayedStart, point(17.5, 10.5), delayedEnd].map(canvasPixel));
  assert.equal((await state()).revision, beforeDelayedStroke.revision, 'A stroke during a delayed writer must not mutate the server.');
  assert.deepEqual(blockedPixels, delayedBlankPixels, 'A stroke during a delayed writer must not leave an optimistic preview.');
  assert.deepEqual(commandActions, ['options'], 'A stroke during a delayed writer must not enqueue tileGesture.');
  await page.locator('#toast:not([hidden])').waitFor();
  assert.ok((await page.locator('#toast').innerText()).length > 0, 'A blocked stroke must explain that the editor is busy.');
  await page.locator('#map-canvas').focus(); await page.keyboard.press('Control+s');
  await page.waitForTimeout(80);
  assert.deepEqual(commandActions, ['options'], 'Save must wait for an earlier options writer before capturing its revision.');
  releaseOptions();
  current = await eventually(s => s.selection.material === delayedMaterial.id && !s.dirty, 'Save did not drain the delayed options writer.'); await clientAt(current.revision);
  await page.unroute('**/api/command', delayOptions);
  assert.deepEqual(commandActions, ['options', 'save'], 'Save must run once immediately after the delayed options writer.');
  const delayedOptionRequest = await completedCommand('options'), delayedSaveRequest = await completedCommand('save');
  assert.equal(delayedSaveRequest.expectedInstanceId, delayedOptionRequest.expectedInstanceId);
  assert.ok(delayedSaveRequest.expectedRevision >= delayedOptionRequest.responseRevision,
    'Save must capture the completed options response or a newer passive export-status update.');
  checks.push('save drains the ordered writer before capturing file state');
  await page.locator('#toast').evaluate(node => node.hidden = true);
  commandActions.length = 0; commandRequests.length = 0;
  await drag(delayedStart, delayedEnd);
  current = await eventually(s => s.document.rooms[0].foreground.filter(cell => cell.y === 10).length === 7, 'A fresh stroke after the options writer was lost.'); await clientAt(current.revision);
  const delayedCells = current.document.rooms[0].foreground.filter(cell => cell.y === 10);
  assert.deepEqual(delayedCells.map(cell => cell.x).sort((a, b) => a - b), [14, 15, 16, 17, 18, 19, 20]);
  assert.ok(delayedCells.every(cell => cell.material === delayedMaterial.id), 'A fresh stroke must use the newly selected material.');
  assert.deepEqual(commandActions, ['tileGesture'], 'A fresh stroke after a writer must commit once.');
  await nextPaint();
  const activeSwatchColor = await page.locator('#room-list .room-row.active .swatch').evaluate(node => getComputedStyle(node).backgroundColor);
  const expectedSwatchColor = await page.evaluate(color => {
    const probe = document.createElement('span'); probe.style.color = color; document.body.append(probe);
    const normalized = getComputedStyle(probe).color; probe.remove(); return normalized;
  }, delayedMaterial.color);
  assert.equal(activeSwatchColor, expectedSwatchColor,
    'A compact live stroke must repaint room chrome from its locally patched minimap theme without waiting for another state event.');
  checks.push('pending writer blocks stale input and compact commit refreshes the room theme');
  await page.keyboard.press('Control+z'); current = await eventually(s => s.document.rooms[0].foreground.filter(cell => cell.y === 10).length === 0, 'Post-writer stroke Undo failed.'); await clientAt(current.revision);

  await page.locator('#tools [data-tool="4"]').click();
  current = await eventually(s => s.selection.tool === 4, 'Rectangle tool did not activate.'); await clientAt(current.revision);
  commandActions.length = 0; commandRequests.length = 0;
  const rectangleBefore = current.document.rooms[0].foreground.length;
  await drag(point(5.5, 2.5), point(9.5, 5.5));
  current = await eventually(s => s.document.rooms[0].foreground.length > rectangleBefore, 'Rectangle gesture did not commit.'); await clientAt(current.revision);
  const rectangleRequest = await completedCommand('tileGesture');
  assert.equal(rectangleRequest.pointCount, 2, 'A non-live shape gesture must send only its start and latest endpoint.');
  assert.equal(rectangleRequest.compactDocument, false, 'A non-live shape needs the authoritative document response.');
  await page.keyboard.press('Control+z'); current = await eventually(s => s.document.rooms[0].foreground.length === rectangleBefore, 'Rectangle gesture Undo failed.'); await clientAt(current.revision);

  await page.locator('#tools [data-tool="5"]').click();
  current = await eventually(s => s.selection.tool === 5, 'Bucket tool did not activate.'); await clientAt(current.revision);
  commandActions.length = 0; commandRequests.length = 0;
  const bucketBefore = current.revision;
  await drag(strokeStart, strokeEnd);
  current = await eventually(s => s.revision > bucketBefore && s.document.rooms[0].foreground.every(cell => cell.material === delayedMaterial.id), 'Bucket gesture did not commit from its first sample.'); await clientAt(current.revision);
  const bucketRequest = await completedCommand('tileGesture');
  assert.equal(bucketRequest.pointCount, 1, 'A bucket drag must send only its starting cell.');
  await page.keyboard.press('Control+z'); current = await eventually(s => s.document.rooms[0].foreground.every(cell => cell.material !== delayedMaterial.id), 'Bucket gesture Undo failed.'); await clientAt(current.revision);
  checks.push('non-live tile gestures use bounded endpoint payloads');

  const beforePan = await state();
  await drag(point(20, 12), { x: point(20,12).x + 64, y: point(20,12).y }, 'middle');
  await page.mouse.move(point(20,12).x + 1, point(20,12).y);
  assert.equal((await state()).revision, beforePan.revision); checks.push('middle drag pans without editing');
  await page.mouse.wheel(0, -120); await page.waitForTimeout(100);
  assert.match(await page.locator('#status-camera').innerText(), /×3/); checks.push('wheel advances integer pixel scale');
  await page.locator('#layers').getByRole('button', { name: '트리거', exact: true }).click();
  current = await eventually(s => s.selection.layer === 3, 'Trigger layer did not activate.'); await clientAt(current.revision);
  await page.locator('#palette').getByRole('button', { name: '트리거 영역', exact: true }).click();
  current = await eventually(s => s.selection.tool === 1, 'Object placement tool did not activate.'); await clientAt(current.revision);
  await page.locator('#map-canvas').scrollIntoViewIfNeeded();
  const area = await page.locator('#map-canvas').boundingBox();
  const center = { x: area.x + area.width / 2, y: area.y + area.height / 2 };
  await drag(center, { x: center.x + 70, y: center.y + 50 });
  current = await eventually(s => s.document.rooms[0].objects.length === 1, 'Trigger was not placed.'); await clientAt(current.revision);
  assert.equal(current.document.rooms[0].objects[0].layer, 3);
  await page.locator('#tools [data-tool="2"]').click();
  current = await eventually(s => s.selection.tool === 2, 'Object selection tool did not activate.'); await clientAt(current.revision);
  await page.mouse.click(center.x + 20, center.y + 20);
  current = await eventually(s => s.selection.objects.length === 1, 'Placed trigger was not selected.'); await clientAt(current.revision);
  const objectX = page.locator('#inspector .fields input').first(), originalObjectX = await objectX.inputValue();
  const beforeBlankObjectInput = await state(); commandActions.length = 0; commandRequests.length = 0;
  await objectX.fill(''); await page.locator('#inspector .fields + button').click();
  await page.locator('#toast:not([hidden])').waitFor();
  const afterBlankObjectInput = await state();
  assert.equal(commandRequests.length, 0, 'A blank object transform must be rejected before sending a command.');
  assert.equal(afterBlankObjectInput.revision, beforeBlankObjectInput.revision, 'A blank object transform must not silently become zero.');
  assert.deepEqual(afterBlankObjectInput.document, beforeBlankObjectInput.document, 'A blank object transform must preserve the object.');
  await objectX.fill(originalObjectX); await page.locator('#toast').evaluate(node => node.hidden = true);
  await page.mouse.click(center.x + 20, center.y + 20, { button: 'right' });
  current = await eventually(s => s.document.rooms[0].objects.length === 0, 'Right click did not erase the trigger.'); await clientAt(current.revision); checks.push('trigger placement and right erase');
  await page.keyboard.press('Control+z'); current = await eventually(s => s.document.rooms[0].objects.length === 1, 'Trigger erasure undo failed.'); await clientAt(current.revision);
  await page.getByRole('button', { name: '방의 객체 모두 지우기', exact: true }).click();
  await page.locator('dialog .accent').click(); current = await eventually(s => s.document.rooms[0].objects.length === 0, 'Clear room objects did not work.'); await clientAt(current.revision); checks.push('room object clear');
  const beforeMiniView = await state(); commandActions.length = 0; commandRequests.length = 0;
  await page.locator('#tabs').getByRole('button', { name: '미니맵', exact: true }).click();
  await page.locator('#mini-canvas').waitFor({ state: 'visible' }); await page.waitForTimeout(150);
  assert.equal(await page.locator('#undo').isDisabled(), true, 'MiniMap must disable Undo.');
  assert.equal(await page.locator('#redo').isDisabled(), true, 'MiniMap must disable Redo.');
  assert.equal(await page.locator('#metadata-action').isDisabled(), true, 'MiniMap must disable metadata editing.');
  assert.equal(await page.locator('#inspector-toggle').isDisabled(), true, 'MiniMap must disable inspector authoring controls.');
  for (const shortcut of ['u', 'Delete', 'Backspace', 'Control+z', 'Control+y', 'Control+c', 'Control+x', 'Control+v', 'Control+a'])
    await page.keyboard.press(shortcut);
  await page.waitForTimeout(100);
  const afterMiniShortcuts = await state();
  assert.equal(afterMiniShortcuts.revision, beforeMiniView.revision, 'MiniMap authoring shortcuts must not change server state.');
  assert.deepEqual(commandActions, [], 'MiniMap authoring shortcuts must send no command.');
  await page.screenshot({ path: path.join(root, 'Logs/ExternalEditor-MiniMap-Test.png') });
  const miniPage = await context.newPage(); await miniPage.goto(base + '/?view=minimap');
  await miniPage.locator('#room-list button').first().waitFor({ state: 'attached' });
  await miniPage.locator('#mini-canvas').waitFor({ state: 'visible' });
  assert.equal(await miniPage.locator('#undo').isDisabled(), true, 'Standalone MiniMap must disable Undo.');
  assert.equal(await miniPage.locator('#metadata-action').isDisabled(), true, 'Standalone MiniMap must disable metadata editing.');
  const beforeStandaloneShortcuts = await state();
  for (const shortcut of ['Delete', 'Control+z', 'Control+c', 'Control+x', 'Control+v']) await miniPage.keyboard.press(shortcut);
  await miniPage.waitForTimeout(100);
  assert.equal((await state()).revision, beforeStandaloneShortcuts.revision, 'Standalone MiniMap shortcuts must remain read-only.');
  checks.push('embedded and separate MiniMap are read-only');
  await miniPage.close();
  await page.locator('#language').selectOption('EN');
  const editorTab = page.getByRole('button', { name: 'Map editor', exact: true });
  await editorTab.waitFor(); await editorTab.click(); await page.locator('#map-canvas').waitFor({ state: 'visible' }); checks.push('KR/EN switch');
  const beforeSaveAsOption = await state(), saveAsTool = beforeSaveAsOption.selection.tool === 0 ? 2 : 0;
  let releaseSaveAsOption, markSaveAsOption;
  const saveAsOptionGate = new Promise(resolve => { releaseSaveAsOption = resolve; });
  const saveAsOptionIntercepted = new Promise(resolve => { markSaveAsOption = resolve; });
  let delayedSaveAsOnce = false;
  const delaySaveAsOption = async route => {
    const body = JSON.parse(route.request().postData() || '{}');
    if (!delayedSaveAsOnce && body.action === 'options' && body.tool === saveAsTool) {
      delayedSaveAsOnce = true; markSaveAsOption(); await saveAsOptionGate;
    }
    await route.continue();
  };
  commandActions.length = 0; commandRequests.length = 0;
  await page.route('**/api/command', delaySaveAsOption);
  await page.locator(`#tools [data-tool="${saveAsTool}"]`).click();
  await Promise.race([saveAsOptionIntercepted, new Promise((_, reject) => setTimeout(() => reject(new Error('Save As options request was not intercepted.')), 3000))]);
  await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', { name: 'Save map as', exact: true }).click();
  await page.waitForTimeout(80);
  assert.equal(await page.locator('dialog').count(), 0, 'Save As must wait for the preceding options writer before opening its snapshot dialog.');
  releaseSaveAsOption();
  current = await eventually(s => s.selection.tool === saveAsTool, 'The options writer before Save As did not complete.'); await clientAt(current.revision);
  await page.locator('dialog').waitFor();
  await page.unroute('**/api/command', delaySaveAsOption);
  await page.locator('dialog input').fill('__browser_validation__/한글.map.json');
  await page.locator('dialog .accent').click();
  current = await eventually(s => s.file === '__browser_validation__/한글.map.json' && !s.dirty, 'Save As did not publish into workspace Maps.'); await clientAt(current.revision);
  assert.deepEqual(commandActions, ['options', 'save'], 'Save As must run once after the delayed options writer.');
  const saveAsOptionRequest = await completedCommand('options'), drainedSaveAsRequest = await completedCommand('save');
  assert.equal(drainedSaveAsRequest.expectedInstanceId, saveAsOptionRequest.expectedInstanceId);
  assert.ok(drainedSaveAsRequest.expectedRevision >= saveAsOptionRequest.responseRevision,
    'Save As must capture the completed options response or a newer passive export-status update.');
  const saved = await readFile(path.join(testFolder, '한글.map.json'));
  assert.notDeepEqual([...saved.subarray(0, 3)], [239, 187, 191]);
  assert.equal(JSON.parse(saved.toString('utf8')).rooms[0].foreground.length, 7);
  checks.push('Save As drains the ordered writer and writes UTF-8 JSON in workspace Maps');

  const collisionRelative = '__browser_validation__/existing.map.json';
  const collisionPath = path.join(testFolder, 'existing.map.json');
  const collisionDocument = JSON.parse(saved.toString('utf8'));
  collisionDocument.name = 'PRESERVE UNTIL SAVE AS CONFIRMATION';
  const collisionSentinel = Buffer.from(JSON.stringify(collisionDocument, null, 2), 'utf8');
  await writeFile(collisionPath, collisionSentinel);
  const saveConflictExpectation = await state(); commandRequests.length = 0;
  await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', { name: 'Save map as', exact: true }).click();
  await page.locator('dialog input').fill(collisionRelative);
  await page.locator('dialog .accent').click();
  const saveOverwriteDialog = page.locator('dialog').last();
  await saveOverwriteDialog.getByText(/selected destination already contains files/i).waitFor();
  const rejectedSaves = await completedCommands('save', 1);
  assert.equal(rejectedSaves.length, 1, 'Save As must not retry before explicit overwrite confirmation.');
  assert.equal(rejectedSaves[0].status, 409, 'An existing Save As target must be reported as a conflict.');
  assert.deepEqual(await readFile(collisionPath), collisionSentinel, 'Save As must preserve existing bytes before confirmation.');
  await saveOverwriteDialog.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('dialog').length === 0);
  await eventually(s => s.file === collisionRelative && !s.dirty, 'Confirmed Save As overwrite did not complete.');
  const saveAttempts = await completedCommands('save', 2);
  assert.deepEqual(saveAttempts.map(item => [item.path, item.overwrite]),
    [[collisionRelative, false], [collisionRelative, true]], 'Save As must retry the captured path only with overwrite enabled.');
  assert.ok(saveAttempts.every(item => item.clientId && item.commandId), 'Save As command envelopes need client and command IDs.');
  assert.ok(saveAttempts.every(item => item.expectedInstanceId === saveConflictExpectation.instanceId
    && item.expectedRevision === saveConflictExpectation.revision), 'Save As overwrite must preserve its captured server expectation.');
  assert.notEqual(JSON.parse(await readFile(collisionPath, 'utf8')).name, collisionDocument.name,
    'Confirmed Save As overwrite must replace the sentinel map.');
  checks.push('Save As requires explicit overwrite and preserves captured target/revision');

  await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', { name: 'Save all rooms as JSON…', exact: true }).click();
  await page.locator('dialog input').fill('__browser_validation__/Rooms');
  await page.locator('dialog .accent').click();
  await page.locator('dialog').waitFor({ state: 'detached' });
  const roomFiles = (await readdir(path.join(testFolder, 'Rooms'))).filter(name => name.endsWith('.json'));
  assert.equal(roomFiles.length, 1); assert.equal(JSON.parse(await readFile(path.join(testFolder, 'Rooms', roomFiles[0]), 'utf8')).rooms.length, 1);
  checks.push('File menu room JSON export');

  const roomPath = path.join(testFolder, 'Rooms', roomFiles[0]);
  const roomDocument = JSON.parse(await readFile(roomPath, 'utf8'));
  roomDocument.name = 'PRESERVE UNTIL EXPORT CONFIRMATION';
  const roomSentinel = Buffer.from(JSON.stringify(roomDocument, null, 2), 'utf8');
  await writeFile(roomPath, roomSentinel);
  const exportConflictExpectation = await state(); commandRequests.length = 0;
  await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', { name: 'Save all rooms as JSON…', exact: true }).click();
  await page.locator('dialog input').fill('__browser_validation__/Rooms');
  await page.locator('dialog .accent').click();
  const exportOverwriteDialog = page.locator('dialog').last();
  await exportOverwriteDialog.getByText(/selected destination already contains files/i).waitFor();
  const rejectedExports = await completedCommands('exportRooms', 1);
  assert.equal(rejectedExports.length, 1, 'Export must not retry before explicit overwrite confirmation.');
  assert.ok(rejectedExports[0].status === 400 || rejectedExports[0].status === 409,
    'An existing export target must be reported as a rejected command.');
  assert.deepEqual(await readFile(roomPath), roomSentinel, 'Export must preserve existing bytes before confirmation.');
  await exportOverwriteDialog.getByRole('button', { name: 'Continue', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('dialog').length === 0);
  const exportAttempts = await completedCommands('exportRooms', 2);
  assert.deepEqual(exportAttempts.map(item => [item.directory, item.overwrite]),
    [['__browser_validation__/Rooms', false], ['__browser_validation__/Rooms', true]],
    'Export must retry the captured directory only with overwrite enabled.');
  assert.ok(exportAttempts.every(item => item.clientId && item.commandId), 'Export command envelopes need client and command IDs.');
  assert.ok(exportAttempts.every(item => item.expectedInstanceId === exportConflictExpectation.instanceId
    && item.expectedRevision === exportConflictExpectation.revision), 'Export overwrite must preserve its captured server expectation.');
  assert.notEqual(JSON.parse(await readFile(roomPath, 'utf8')).name, roomDocument.name,
    'Confirmed export overwrite must replace the sentinel room JSON.');
  checks.push('room export requires explicit overwrite and preserves captured target/revision');

  await page.getByRole('button', { name: 'Map editor', exact: true }).click();
  await page.locator('#map-canvas').waitFor({ state: 'visible' });
  const normalizedLayerOptions = await command('options', { layer: 0, groupId: '', hiddenLayers: [], lockedLayers: [] });
  await clientAt(normalizedLayerOptions.revision);
  await page.waitForFunction(() => {
    const buttons = document.querySelectorAll('#layers .layer-row:first-child button');
    return buttons[1]?.textContent === '●' && buttons[2]?.textContent === '▫';
  });
  commandActions.length = 0;
  await page.locator('#layers .layer-row').first().evaluate(row => {
    const buttons = row.querySelectorAll('button'); buttons[1].click(); buttons[2].click();
  });
  current = await eventually(s => s.selection.hiddenLayers.includes(0) && s.selection.lockedLayers.includes(0),
    'Rapid visibility and lock clicks lost one layer option update.');
  await clientAt(current.revision);
  assert.deepEqual(commandActions, ['options', 'options'], 'Rapid layer toggles must remain two ordered commands.');

  const rapidGroup = { id: 'rapid-toggle-group', name: 'Rapid toggle', layer: 0, parentId: '', visible: true, locked: false };
  await command('documentProperties', { layerGroups: [rapidGroup] });
  await page.waitForFunction(id => [...document.querySelectorAll('#group-select option')].some(option => option.value === id), rapidGroup.id);
  await page.locator('#group-select').selectOption(rapidGroup.id);
  current = await eventually(s => s.selection.groupId === rapidGroup.id, 'The rapid-toggle group was not selected.'); await clientAt(current.revision);
  commandActions.length = 0;
  await page.locator('#group-actions').evaluate(actions => {
    const buttons = actions.querySelectorAll('button'); buttons[1].click(); buttons[2].click();
  });
  current = await eventually(s => {
    const group = s.document.layerGroups.find(item => item.id === 'rapid-toggle-group');
    return group?.visible === false && group?.locked === true;
  }, 'Rapid group visibility and lock clicks lost one document update.');
  await clientAt(current.revision);
  assert.deepEqual(commandActions, ['documentProperties', 'documentProperties'],
    'Rapid group toggles must derive two ordered updates from current state.');
  checks.push('rapid layer and group toggles preserve every click');

  await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', { name: 'Open map', exact: true }).click();
  await page.locator('dialog').getByRole('button', { name: 'Continue', exact: true }).click();
  await page.locator('dialog .file-list').getByRole('button', { name: initial.file, exact: true }).click();
  await page.locator('dialog .accent').click();
  current = await eventually(s => s.file === initial.file && !s.dirty, 'File menu open did not restore the existing map.');
  await clientAt(current.revision);
  checks.push('File menu opens saved workspace map');
  assert.equal(await page.evaluate(() => window.dispatchEvent(new Event('beforeunload', { cancelable: true }))), true,
    'A clean, idle editor must allow closing without an unsaved-work prompt.');
  const activeBounds = await page.locator('#map-canvas').boundingBox();
  await page.mouse.move(activeBounds.x + activeBounds.width / 2, activeBounds.y + activeBounds.height / 2);
  await page.mouse.down({ button: 'middle' });
  assert.equal(await page.evaluate(() => window.dispatchEvent(new Event('beforeunload', { cancelable: true }))), false,
    'An active canvas gesture must protect the page from closing silently.');
  await page.mouse.up({ button: 'middle' });

  let releasePending, markPending;
  const pendingGate = new Promise(resolve => { releasePending = resolve; });
  const pendingIntercepted = new Promise(resolve => { markPending = resolve; });
  const delayPending = async route => {
    const body = JSON.parse(route.request().postData() || '{}');
    if (body.action === 'options') { markPending(); await pendingGate; }
    await route.continue();
  };
  const cleanState = await state(), nextTool = cleanState.selection.tool === 0 ? 2 : 0;
  await page.route('**/api/command', delayPending);
  await page.locator(`#tools [data-tool="${nextTool}"]`).evaluate(button => button.click());
  await Promise.race([pendingIntercepted, new Promise((_, reject) => setTimeout(() => reject(new Error('Pending close-guard request was not intercepted.')), 3000))]);
  assert.equal(await page.evaluate(() => window.dispatchEvent(new Event('beforeunload', { cancelable: true }))), false,
    'An in-flight command must protect a still-clean document from closing silently.');
  releasePending();
  await eventually(s => s.selection.tool === nextTool, 'The delayed close-guard command did not complete.');
  await page.unroute('**/api/command', delayPending);
  await page.waitForFunction(() => !document.querySelector('#status-state')?.classList.contains('working'));
  assert.equal(await page.evaluate(() => window.dispatchEvent(new Event('beforeunload', { cancelable: true }))), true,
    'The close guard must clear after a non-document command settles.');
  checks.push('active gestures and pending commands guard page close without aborting writes');
  assert.ok(directCommandEnvelopes.every(item => item.clientId && item.commandId),
    'Every direct browser command must carry a nonempty clientId and commandId.');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ passed: checks.length, checks, errors, performanceBudgets, paintPerformance, exactBoundaryPerformance, hugeJumpPerformance }, null, 2));
} catch (error) {
  await page.screenshot({ path: path.join(root, 'Logs/ExternalEditor-InteractionFailure.png') });
  console.error(JSON.stringify({ checks, errors, status: await page.locator('.statusbar').innerText().catch(() => ''),
    requests: commandRequests.slice(-3).map(({ request, ...record }) => record) }));
  throw error;
} finally {
  await command('cancel', {}, lastClient).catch(() => undefined);
  await command('open', { path: initial.file, discard: true }).catch(error => console.error('Restore failed:', error));
  await browser.close();
  // This exact, preflight-absent folder is owned by this validation run.
  assert.equal(path.dirname(testFolder), path.join(root, 'Maps'));
  await rm(testFolder, { recursive: true, force: true });
}
