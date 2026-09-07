import { createRequire } from 'node:module';
import assert from 'node:assert/strict';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL || 'http://127.0.0.1:18765';
const apiTimeoutMs = 12000;
const apiFetch = (resource, options = {}) => fetch(base + resource, { ...options, signal: AbortSignal.timeout(apiTimeoutMs) });
const seedResponse = await apiFetch('/api/state');
assert.equal(seedResponse.ok, true, 'The performance fixture needs a reachable editor server.');
const seed = await seedResponse.json();
const material = seed.catalog.materials[0]?.id || 'terrain';
const caseDefinition = seed.catalog.objects.find(definition => /[A-Z]/.test(definition.id));
if (caseDefinition) caseDefinition.name = 'Case-sensitive catalog label';
const rooms = [];

for (let row = 0; row < 8; row++) for (let column = 0; column < 8; column++) {
  const width = 128, height = 72, foreground = [];
  for (let x = 0; x < width; x++) {
    foreground.push({ x, y: 0, shape: 0, material, groupId: '' });
    foreground.push({ x, y: height - 1, shape: 0, material, groupId: '' });
  }
  for (let y = 1; y < height - 1; y++) {
    foreground.push({ x: 0, y, shape: 0, material, groupId: '' });
    foreground.push({ x: width - 1, y, shape: 0, material, groupId: '' });
  }
  rooms.push({
    id: `stress_${row}_${column}`, name: `stress_${row}_${column}`,
    x: column * width, y: row * height, width, height,
    visible: true, locked: false, foreground, background: [], objects: [], properties: []
  });
}
if (caseDefinition) rooms[0].objects.push({
  id: 'case_insensitive_definition', definition: caseDefinition.id.toLowerCase(), layer: caseDefinition.layer,
  groupId: '', x: 4, y: 4, width: caseDefinition.width || 1, height: caseDefinition.height || 1,
  rotation: 0, scaleX: 1, scaleY: 1, nodes: [], properties: []
});

const state = structuredClone(seed);
state.revision = 1;
state.documentRevision = 1;
state.catalogRevision = 1;
state.document = { ...state.document, name: 'Render stress', rooms, layerGroups: [] };
state.selection = {
  ...state.selection, roomId: rooms[0].id, tool: 3, layer: 0, shape: 0, material,
  brushSize: 16, groupId: '', hiddenLayers: [], lockedLayers: [], objects: caseDefinition ? ['case_insensitive_definition'] : [], area: null, nodes: []
};
state.connections = [];

function stats(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const at = quantile => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * quantile))] || 0;
  return { count: sorted.length, median: +at(.5).toFixed(2), p95: +at(.95).toFixed(2), max: +(sorted.at(-1) || 0).toFixed(2) };
}

const budgets = { drawCallbackP95Ms: 20, drawCallbackMaxMs: 50, inputFrameP95Ms: 40, longTaskMaxMs: 50 };
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'ko-KR', deviceScaleFactor: 1 });
const page = await context.newPage();
let fullStates = 0, incrementalStates = 0, deliverIncremental = false, deliverCatalogIncremental = false;
const tileGestureRequests = [];
const retryProbeAttempts = [], retryProbeCommandIds = new Set();
const responseLossAttempts = [], responseLossResponses = new Map();
let compactNoopRequests = 0;
const assetRequests = [];
let retryProbeExecutions = 0, responseLossExecutions = 0;
page.on('request', request => { if (request.url().includes('/api/asset?')) assetRequests.push(request.url()); });

await page.route('**/api/state*', async route => {
  const conditional = new URL(route.request().url()).searchParams.has('since');
  if (!conditional) {
    fullStates++;
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
    return;
  }
  incrementalStates++;
  if (!deliverIncremental && !deliverCatalogIncremental) { await route.fulfill({ status: 204 }); return; }
  const catalogChanged = deliverCatalogIncremental;
  deliverIncremental = false; deliverCatalogIncremental = false;
  state.revision++;
  if (catalogChanged) state.catalogRevision++;
  // Force a visible selection change: revision parity can select the already
  // active room after a compact commit, which correctly needs no map redraw.
  if (!catalogChanged) state.selection = { ...state.selection, roomId: rooms[state.selection.roomId === rooms[0].id ? 1 : 0].id };
  await route.fulfill({
    status: 200, contentType: 'application/json',
    body: JSON.stringify({
      instanceId: state.instanceId, revision: state.revision,
      documentRevision: state.documentRevision, catalogRevision: state.catalogRevision,
      file: state.file, dirty: state.dirty, canUndo: state.canUndo, canRedo: state.canRedo,
      selection: state.selection, notice: state.notice,
      ...(catalogChanged ? { catalog: state.catalog } : {})
    })
  });
});
await page.route('**/api/command', async route => {
  const payload = route.request().postDataJSON();
  if (payload.action === '__response_parse_retry_probe__') {
    retryProbeAttempts.push(payload);
    if (!retryProbeCommandIds.has(payload.commandId)) {
      retryProbeCommandIds.add(payload.commandId);
      retryProbeExecutions++;
    }
    if (retryProbeAttempts.length === 1) {
      await route.fulfill({ status: 200, contentType: 'application/json', body: '{' });
      return;
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
    return;
  }
  if (payload.action === 'tileGesture') {
    if (payload.roomId === 'compact_noop_room') {
      compactNoopRequests++;
      const { document: _document, catalog: _catalog, connections: _connections, ...compact } = state;
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        ...compact, revision: payload.expectedRevision + 1, documentRevision: state.documentRevision
      }) });
      return;
    }
    if (payload.roomId === 'response_loss_room') {
      responseLossAttempts.push(payload);
      let full = responseLossResponses.get(payload.commandId);
      if (!full) {
        responseLossExecutions++;
        const authoritativeRoom = {
          id: payload.roomId, name: payload.roomId, x: 0, y: 0, width: 8, height: 8,
          visible: true, locked: false,
          foreground: [
            { x: 1, y: 1, shape: 4, material, groupId: '' },
            { x: 2, y: 2, shape: 3, material, groupId: '' }
          ],
          background: [], objects: [], properties: []
        };
        full = { ...state, revision: state.revision + 2, documentRevision: state.documentRevision + 2, dirty: true,
          document: { ...structuredClone(state.document), rooms: [authoritativeRoom], layerGroups: [] },
          selection: { ...structuredClone(state.selection), roomId: payload.roomId, tool: 3, layer: 0, shape: 0,
            material, brushSize: 1, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] },
          connections: [] };
        responseLossResponses.set(payload.commandId, full);
        await route.fulfill({ status: 200, contentType: 'application/json', body: '{' });
        return;
      }
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(full) });
      return;
    }
    tileGestureRequests.push(payload); state.revision++; state.documentRevision++; state.dirty = true;
    const { document: _document, catalog: _catalog, connections: _connections, ...compact } = state;
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(compact) });
    return;
  }
  await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(state) });
});
await page.route('**/__api-lifecycle-probe.js', route => route.fulfill({
  status: 200,
  contentType: 'text/javascript',
  body: 'import { EditorApi, localJson } from "/api.js"; window.__apiLifecycleExports = { EditorApi, localJson }; parent.postMessage("api-lifecycle-ready", "*");'
}));
await page.addInitScript(() => {
  window.__renderBench = { callbacks: [], inputFrames: [], paintFrames: [], longTasks: [], maxLodRasterPixels: 0,
    paintReleaseAt: 0, fullFrames: { map: 0, mini: 0 } };
  const nativeRaf = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = callback => nativeRaf(timestamp => {
    const started = performance.now();
    callback(timestamp);
    window.__renderBench.callbacks.push(performance.now() - started);
  });
  const nativeFillRect = CanvasRenderingContext2D.prototype.fillRect;
  const nativeCreateImageData = CanvasRenderingContext2D.prototype.createImageData;
  CanvasRenderingContext2D.prototype.createImageData = function(width, height, ...rest) {
    if (!this.canvas?.id) window.__renderBench.maxLodRasterPixels = Math.max(window.__renderBench.maxLodRasterPixels, width * height);
    return nativeCreateImageData.call(this, width, height, ...rest);
  };
  CanvasRenderingContext2D.prototype.fillRect = function(x, y, width, height) {
    if (x === 0 && y === 0 && Math.abs(width - this.canvas.clientWidth) < 1 && Math.abs(height - this.canvas.clientHeight) < 1) {
      if (this.canvas.id === 'map-canvas') window.__renderBench.fullFrames.map++;
      if (this.canvas.id === 'mini-canvas') window.__renderBench.fullFrames.mini++;
    }
    return nativeFillRect.call(this, x, y, width, height);
  };
  try {
    new PerformanceObserver(list => {
      for (const item of list.getEntries()) window.__renderBench.longTasks.push(item.duration);
    }).observe({ type: 'longtask', buffered: true });
  } catch { /* Long-task entries are unavailable in a few browser builds. */ }
});

try {
  await page.goto(base);
  await page.locator('#room-list button').first().waitFor();
  await page.evaluate(async () => {
    const { EditorApi } = await import('/api.js');
    const retryClient = new EditorApi();
    try {
      await retryClient.refresh();
      await retryClient.command('__response_parse_retry_probe__', { marker: 'parsed-response-lost' });
    } finally { retryClient.stop(); }
  });
  assert.equal(retryProbeAttempts.length, 2, 'A response parse failure must cause exactly one retry.');
  assert.equal(retryProbeExecutions, 1, 'A retried command ID must represent one logical server mutation.');
  assert.equal(retryProbeAttempts[0].commandId, retryProbeAttempts[1].commandId,
    'The response retry must reuse the original command ID.');
  assert.equal(retryProbeAttempts[0].clientId, retryProbeAttempts[1].clientId,
    'The response retry must stay on the original client ID.');
  assert.ok(typeof retryProbeAttempts[0].clientId === 'string' && retryProbeAttempts[0].clientId.length > 0,
    'EditorApi command envelopes need a nonempty clientId.');
  assert.ok(typeof retryProbeAttempts[0].commandId === 'string' && retryProbeAttempts[0].commandId.length > 0,
    'EditorApi command envelopes need a nonempty commandId.');
  assert.equal(retryProbeAttempts[0].expectedInstanceId, retryProbeAttempts[1].expectedInstanceId);
  assert.equal(retryProbeAttempts[0].expectedRevision, retryProbeAttempts[1].expectedRevision);
  const requestLifecycle = await page.evaluate(async source => {
    const frame = document.createElement('iframe');
    const ready = new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error('The API lifecycle probe did not load.')), 3000);
      addEventListener('message', function onMessage(event) {
        if (event.source !== frame.contentWindow || event.data !== 'api-lifecycle-ready') return;
        removeEventListener('message', onMessage); clearTimeout(timeout); resolve();
      });
    });
    frame.srcdoc = '<!doctype html><script type="module" src="/__api-lifecycle-probe.js"></script>';
    document.body.append(frame); await ready;
    const realm = frame.contentWindow;
    const { EditorApi, localJson } = realm.__apiLifecycleExports;
    let bodyAbortObserved = false;
    realm.fetch = (_resource, init = {}) => Promise.resolve(new realm.Response(new realm.ReadableStream({
      start(controller) {
        controller.enqueue(new realm.TextEncoder().encode('{"instanceId":"partial"'));
        init.signal?.addEventListener('abort', () => {
          bodyAbortObserved = true; controller.error(new realm.DOMException('Aborted', 'AbortError'));
        }, { once: true });
      }
    }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    const aborting = new EditorApi(), stalled = aborting.refresh().then(() => 'resolved', () => 'rejected');
    await new Promise(resolve => realm.setTimeout(resolve, 0)); aborting.stop();
    const abortOutcome = await Promise.race([stalled, new Promise(resolve => realm.setTimeout(() => resolve('timeout'), 300))]);

    let releaseFetch;
    const fetchStarted = new Promise(resolve => {
      realm.fetch = () => { resolve(); return new Promise(done => { releaseFetch = done; }); };
    });
    const preserving = new EditorApi(), pending = preserving.refresh(); await fetchStarted;
    preserving.stop(false);
    releaseFetch(new realm.Response(JSON.stringify(source), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    const preserved = await Promise.race([pending.then(() => true, () => false), new Promise(resolve => realm.setTimeout(() => resolve(false), 300))]);
    let auxiliaryAbortObserved = false;
    realm.fetch = (_resource, init = {}) => Promise.resolve(new realm.Response(new realm.ReadableStream({
      start(controller) {
        controller.enqueue(new realm.TextEncoder().encode('["partial"'));
        init.signal?.addEventListener('abort', () => {
          auxiliaryAbortObserved = true; controller.error(new realm.DOMException('Aborted', 'AbortError'));
        }, { once: true });
      }
    }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    const auxiliaryStarted = realm.performance.now();
    const auxiliaryError = await Promise.race([
      localJson('/api/files', {}, 25).then(() => '', error => error.message),
      new Promise(resolve => realm.setTimeout(() => resolve('probe timeout'), 300))
    ]);
    const auxiliaryElapsed = realm.performance.now() - auxiliaryStarted;
    frame.remove(); return { abortOutcome, bodyAbortObserved, preserved, auxiliaryAbortObserved, auxiliaryError, auxiliaryElapsed };
  }, seed);
  assert.equal(requestLifecycle.abortOutcome, 'rejected');
  assert.equal(requestLifecycle.bodyAbortObserved, true);
  assert.equal(requestLifecycle.preserved, true);
  assert.equal(requestLifecycle.auxiliaryAbortObserved, true);
  assert.match(requestLifecycle.auxiliaryError, /local editor server did not respond within/i);
  assert.ok(requestLifecycle.auxiliaryElapsed < 300,
    `An auxiliary local response body exceeded its timeout (${requestLifecycle.auxiliaryElapsed.toFixed(1)}ms).`);
  const responseLossContract = await page.evaluate(async material => {
    const [{ EditorApi }, { MapCanvas }] = await Promise.all([import('/api.js'), import('/map-canvas.js')]);
    const source = await (await fetch('/api/state')).json();
    const room = { id: 'response_loss_room', name: 'response_loss_room', x: 0, y: 0, width: 8, height: 8,
      visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
    const base = { ...source,
      document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] },
      selection: { ...structuredClone(source.selection), roomId: room.id, tool: 3, layer: 0, shape: 0,
        material, brushSize: 1, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] },
      connections: [] };
    const api = new EditorApi(); await api.refresh();
    const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;width:64px;height:64px;z-index:-1';
    document.body.append(canvas); let refreshes = 0;
    const editor = new MapCanvas(canvas, api.command, () => {}, () => {}, () => {}, () => false,
      async () => { refreshes++; });
    api.addEventListener('state', () => { if (api.state) editor.setState(api.state); });
    editor.setState(base);
    const gesture = { kind: 'paint', pointer: 917, start: { x: 1, y: 1 }, last: { x: 1, y: 1 },
      screen: { x: 0, y: 0 }, center: { x: 0, y: 0 }, room,
      expectation: { instanceId: base.instanceId, revision: base.revision }, tail: Promise.resolve(), failed: false };
    editor.beginTileGesture(gesture, { x: 1, y: 1 }, false, base.selection);
    editor.gesture = gesture; editor.extendTileGesture = () => {};
    editor.up(new PointerEvent('pointerup', { bubbles: true, cancelable: true, pointerId: gesture.pointer }));
    await editor.settled();
    const authoritative = editor.state.document.rooms[0];
    const indexed = editor.occupancy.get(room.id + ':0');
    const result = {
      documentShape: authoritative.foreground.find(cell => cell.x === 1 && cell.y === 1)?.shape,
      indexedShape: indexed?.rows.get(1)?.get(1)?.shape,
      interleavedShape: authoritative.foreground.find(cell => cell.x === 2 && cell.y === 2)?.shape,
      refreshes,
      overlayReleased: editor.tileOverlay === null
    };
    api.stop(); editor.dispose(); canvas.remove(); return result;
  }, material);
  assert.equal(responseLossAttempts.length, 2, 'A lost tileGesture response must retry exactly once.');
  assert.equal(responseLossExecutions, 1, 'The duplicate tileGesture command ID must represent one server execution.');
  assert.equal(responseLossAttempts[0].commandId, responseLossAttempts[1].commandId,
    'The lost-response retry must reuse its command ID.');
  assert.deepEqual(responseLossContract,
    { documentShape: 4, indexedShape: 4, interleavedShape: 3, refreshes: 0, overlayReleased: true },
    'A full duplicate response must remain authoritative over the stale optimistic overlay and retain interleaved edits.');
  const fullStatesBeforeNoop = fullStates;
  const compactNoopContract = await page.evaluate(async material => {
    const [{ EditorApi }, { MapCanvas }] = await Promise.all([import('/api.js'), import('/map-canvas.js')]);
    const source = await (await fetch('/api/state')).json();
    const painted = { x: 1, y: 1, shape: 0, material, groupId: '' };
    const room = { id: 'compact_noop_room', name: 'compact_noop_room', x: 0, y: 0, width: 8, height: 8,
      visible: true, locked: false, foreground: [painted], background: [], objects: [], properties: [] };
    const base = { ...source,
      document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] },
      selection: { ...structuredClone(source.selection), roomId: room.id, tool: 3, layer: 0, shape: 0,
        material, brushSize: 1, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] },
      connections: [] };
    const api = new EditorApi(); api.state = base;
    const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;width:64px;height:64px;z-index:-1';
    document.body.append(canvas); let refreshes = 0;
    const editor = new MapCanvas(canvas, api.command, () => {}, () => {}, () => {}, () => false,
      async () => { refreshes++; await api.refresh(false); });
    api.addEventListener('state', () => { if (api.state) editor.setState(api.state); }); editor.setState(base);
    const gesture = { kind: 'paint', pointer: 919, start: { x: 1, y: 1 }, last: { x: 1, y: 1 },
      screen: { x: 0, y: 0 }, center: { x: 0, y: 0 }, room,
      expectation: { instanceId: base.instanceId, revision: base.revision }, tail: Promise.resolve(), failed: false };
    editor.beginTileGesture(gesture, { x: 1, y: 1 }, false, base.selection);
    editor.gesture = gesture; editor.extendTileGesture = () => {};
    editor.up(new PointerEvent('pointerup', { bubbles: true, cancelable: true, pointerId: gesture.pointer }));
    await editor.settled(); await new Promise(resolve => setTimeout(resolve, 50));
    const result = { refreshes, documentRevision: api.state.documentRevision, overlayReleased: editor.tileOverlay === null };
    api.stop(); editor.dispose(); canvas.remove(); return result;
  }, material);
  assert.deepEqual(compactNoopContract,
    { refreshes: 0, documentRevision: state.documentRevision, overlayReleased: true },
    'A successful same-cell compact no-op must settle without a fallback full-state refresh.');
  assert.equal(compactNoopRequests, 1, 'A compact same-cell no-op must send exactly one tile gesture.');
  assert.equal(fullStates, fullStatesBeforeNoop + 1,
    'The no-op contract may fetch its initial state once but must not request a fallback full document.');
  const initialThumbnail = assetRequests.map(url => new URL(url)).find(url => url.searchParams.get('v') === `${state.instanceId}:1`);
  assert.ok(initialThumbnail, 'Palette thumbnail URLs must carry their initial catalog version.');
  deliverCatalogIncremental = true;
  const refreshedThumbnailDeadline = Date.now() + 3000;
  let refreshedThumbnail;
  while (Date.now() < refreshedThumbnailDeadline && !refreshedThumbnail) {
    refreshedThumbnail = assetRequests.map(url => new URL(url)).find(url => url.searchParams.get('path') === initialThumbnail.searchParams.get('path')
      && url.searchParams.get('v') === `${state.instanceId}:2`);
    if (!refreshedThumbnail) await new Promise(resolve => setTimeout(resolve, 50));
  }
  assert.ok(refreshedThumbnail, 'A catalog revision must request a fresh version of the same palette sprite.');
  if (caseDefinition) assert.equal(await page.locator('#inspector .object-chip').innerText(), caseDefinition.name,
    'Imported object definition IDs must resolve without case sensitivity.');
  const compactIndexContract = await page.evaluate(async material => {
    const { MapCanvas } = await import('/map-canvas.js');
    const response = await fetch('/api/state'), source = await response.json(), canvas = document.createElement('canvas');
    canvas.style.width = '320px'; canvas.style.height = '180px';
    const room = { ...structuredClone(source.document.rooms[0]), id: 'compact_contract', x: 0, y: 0, width: 8, height: 8,
      foreground: [], background: [], objects: [], properties: [], visible: true, locked: false };
    const initial = { ...source, revision: 1, documentRevision: 1,
      document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] },
      selection: { ...structuredClone(source.selection), roomId: room.id, layer: 0, tool: 3, material,
        shape: 0, brushSize: 1, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] } };
    const editor = new MapCanvas(canvas, async () => initial, () => {}, () => {}); editor.setState(initial);
    let rebuilds = 0; const rebuild = editor.rebuildDocument.bind(editor);
    editor.rebuildDocument = value => { rebuilds++; return rebuild(value); };
    const painted = { x: 2, y: 3, shape: 0, material, groupId: '' };
    const tile = { roomId: room.id, layer: 0, erase: false, live: true, material, shape: 0, groupId: '', brushSize: 1,
      tool: 3, baseRevision: 1, baseInstanceId: initial.instanceId, baseDocumentRevision: 1, baseDocument: initial.document,
      points: [{ x: 2, y: 3 }], rasterLength: 1,
      cells: new Map([['2,3', painted]]), overflow: false };
    editor.tileOverlay = tile; editor.tileCommitPending = true;
    const compact = { ...initial, revision: 2, documentRevision: 2 };
    editor.setState(compact); const deferredRebuilds = rebuilds;
    const applied = editor.applyCommittedTileGesture(tile, compact);
    const paintedCell = room.foreground.find(cell => cell.x === 2 && cell.y === 3);
    const indexedCell = editor.occupancy.get(room.id + ':0')?.rows.get(3)?.get(2);
    editor.tileOverlay = null; editor.tileCommitPending = false; editor.setState(compact);
    const rebuildsAfterApply = rebuilds;
    editor.setState({ ...compact, document: structuredClone(compact.document) });
    const fullRefreshRebuilds = rebuilds - rebuildsAfterApply; editor.dispose();
    return { applied, deferredRebuilds, fullRefreshRebuilds, paintedCell: paintedCell === painted, indexedCell: indexedCell === painted };
  }, material);
  assert.deepEqual(compactIndexContract,
    { applied: true, deferredRebuilds: 0, fullRefreshRebuilds: 1, paintedCell: true, indexedCell: true },
    'Compact responses must patch existing indexes while a same-revision full refresh must rebuild them.');
  const cameraAndThemeContract = await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js');
    const source = await (await fetch('/api/state')).json(), canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:0;top:0;width:640px;height:360px;z-index:-1'; document.body.append(canvas);
    const room = { id: 'camera_theme', name: 'camera_theme', x: 0, y: 0, width: 40, height: 24,
      visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
    const material = { id: 'blank-theme', name: 'Blank theme', color: '#123456', themeId: '   ', sprites: [] };
    const first = { ...source, revision: 1, documentRevision: 1, catalogRevision: 1,
      camera: { ppu: 16, referenceWidth: 320, referenceHeight: 180, orthographicSize: 5.625 },
      catalog: { ...source.catalog, materials: [material], camera: { ppu: 16, referenceWidth: 320, referenceHeight: 180, orthographicSize: 5.625 } },
      document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] },
      selection: { ...structuredClone(source.selection), roomId: room.id, layer: 0, tool: 3, material: material.id,
        shape: 0, brushSize: 1, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] } };
    const editor = new MapCanvas(canvas, async () => first, () => {}, () => {}); editor.setState(first); editor.gameView(true);
    await new Promise(resolve => requestAnimationFrame(resolve)); const initialScale = editor.pixelScale;
    const second = { ...first, revision: 2, camera: { ppu: 16, referenceWidth: 640, referenceHeight: 360, orthographicSize: 11.25 }, catalog: { ...first.catalog,
      camera: { ppu: 16, referenceWidth: 640, referenceHeight: 360, orthographicSize: 11.25 } } };
    editor.setState(second); await new Promise(resolve => requestAnimationFrame(resolve)); const updatedScale = editor.pixelScale;
    const tile = { roomId: room.id, layer: 0, erase: false, live: true, material: material.id, shape: 0, groupId: '', brushSize: 1,
      tool: 3, baseRevision: 2, baseInstanceId: second.instanceId, baseDocumentRevision: 1, baseDocument: second.document,
      points: [{ x: 1, y: 1 }],
      rasterLength: 1, cells: new Map([['1,1', { x: 1, y: 1, shape: 0, material: material.id, groupId: '' }]]), overflow: false };
    editor.tileOverlay = tile;
    const committed = { ...second, revision: 3, documentRevision: 2 };
    const applied = editor.applyCommittedTileGesture(tile, committed);
    const properties = room.properties.map(item => item.key);
    const paintedCell = room.foreground.some(cell => cell.x === 1 && cell.y === 1 && cell.material === material.id);
    editor.gameView(false); editor.writerPending = () => true;
    editor.setState({ ...committed, selection: { ...committed.selection, tool: 0 } });
    canvas.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true, button: 0, pointerId: 917, clientX: 320, clientY: 180 }));
    const busyRoomGestureBlocked = !editor.interacting;
    editor.dispose(); canvas.remove();
    return { initialScale, updatedScale, expectedScale: 1, applied, paintedCell, properties, busyRoomGestureBlocked };
  });
  assert.deepEqual(cameraAndThemeContract,
    { initialScale: 2, updatedScale: 1, expectedScale: 1, applied: true, paintedCell: true, properties: [], busyRoomGestureBlocked: true },
    'Live camera changes must refit Game view, blank themes stay inert, and busy room gestures must not become stale drags.');
  const selectedPathContract = await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js');
    const response = await fetch('/api/state'), source = await response.json(), definition = source.catalog.objects[0];
    if (!definition) return null;
    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:0;top:0;width:320px;height:180px;z-index:-1'; document.body.append(canvas);
    const context = canvas.getContext('2d'); let nodeFills = 0, nodeLabels = 0, pathSegments = 0;
    const fillRect = context.fillRect.bind(context), fillText = context.fillText.bind(context), lineTo = context.lineTo.bind(context);
    context.fillRect = (...args) => { if (context.fillStyle === '#ffdc83') nodeFills++; return fillRect(...args); };
    context.fillText = (value, ...args) => { if (/^4096$/.test(String(value))) nodeLabels++; return fillText(value, ...args); };
    context.lineTo = (...args) => { if (context.strokeStyle === '#ffd982') pathSegments++; return lineTo(...args); };
    const nodes = Array.from({ length: 4095 }, (_, index) => ({ x: 1 + index % 2, y: 12 })); nodes.push({ x: 148, y: 12 });
    const object = { id: 'offscreen_path', definition: definition.id, layer: 2, groupId: '', x: 0, y: 11,
      width: 1, height: 1, rotation: 0, scaleX: 1, scaleY: 1, nodes, properties: [] };
    const room = { id: 'path_room', name: 'path_room', x: -128, y: 0, width: 256, height: 24,
      visible: true, locked: false, foreground: [], background: [], objects: [object], properties: [] };
    const contractState = { ...source, revision: 1, documentRevision: 1,
      document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] },
      selection: { ...structuredClone(source.selection), roomId: room.id, tool: 2, layer: 2, objects: [object.id],
        nodes: [], hiddenLayers: [], lockedLayers: [], area: null } };
    const editor = new MapCanvas(canvas, async () => contractState, () => {}, () => {});
    editor.pixelScale = 2; editor.showGrid = false; editor.showNames = false; editor.setState(contractState);
    editor.center = { x: 20, y: 12 }; editor.requestDraw();
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const marker = editor.toScreen({ x: 20, y: 12 });
    const pixel = [...context.getImageData(Math.floor(marker.x), Math.floor(marker.y), 1, 1).data];
    const hit = editor.nodeAt(marker, room); editor.dispose(); canvas.remove();
    return { nodeFills, nodeLabels, pathSegments, pixel, hit, marker, canvasSize: { width: canvas.width, height: canvas.height },
      cssSize: { width: canvas.clientWidth, height: canvas.clientHeight } };
  });
  assert.ok(selectedPathContract, 'The selected-path fixture needs an object definition.');
  assert.deepEqual(selectedPathContract.hit, { id: 'offscreen_path', index: 4095 },
    'An onscreen node must remain hittable when its object body is offscreen.');
  assert.deepEqual(selectedPathContract.pixel, [255, 220, 131, 255],
    `An onscreen selected node must render even when its object body is culled: ${JSON.stringify(selectedPathContract)}`);
  assert.equal(selectedPathContract.nodeFills, 1, 'Path rendering must cull 4,095 offscreen node markers.');
  assert.equal(selectedPathContract.nodeLabels, 1, 'Path rendering must cull offscreen node labels.');
  assert.equal(selectedPathContract.pathSegments, 1, 'Path rendering must submit only the visible path segment.');

  const emptyEraseContract = await page.evaluate(async material => {
    const { MapCanvas } = await import('/map-canvas.js');
    const response = await fetch('/api/state'), source = await response.json(), canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:0;top:0;width:320px;height:180px;z-index:-1'; document.body.append(canvas);
    const room = { id: 'empty_erase', name: 'empty_erase', x: 0, y: 0, width: 128, height: 72,
      visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
    const selection = { ...structuredClone(source.selection), roomId: room.id, tool: 3, layer: 0, material,
      brushSize: 16, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] };
    const contractState = { ...source, revision: 1, documentRevision: 1,
      document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] }, selection };
    const editor = new MapCanvas(canvas, async () => contractState, () => {}, () => {}); editor.setState(contractState);
    const gesture = { kind: 'paint', pointer: 1, start: { x: 8, y: 8 }, last: { x: 8, y: 8 }, screen: { x: 0, y: 0 },
      center: { x: 0, y: 0 }, room, expectation: { instanceId: source.instanceId, revision: 1 }, tail: Promise.resolve(), failed: false };
    editor.beginTileGesture(gesture, { x: 8, y: 8 }, true, selection); editor.gesture = gesture;
    editor.extendTileGesture(gesture, { x: 100, y: 60 });
    const result = { overlayCells: editor.tileOverlay.cells.size, rasterLength: editor.tileOverlay.rasterLength,
      overflow: editor.tileOverlay.overflow };
    editor.cancel(); editor.dispose(); canvas.remove(); return result;
  }, material);
  assert.equal(emptyEraseContract.overlayCells, 0, 'Erasing empty cells must not allocate null overlay entries.');
  assert.equal(emptyEraseContract.overflow, false, 'The bounded empty erase fixture must remain a valid gesture.');
  const overlayReleaseContract = await page.evaluate(async material => {
    const { MapCanvas } = await import('/map-canvas.js');
    const source = await (await fetch('/api/state')).json();
    const room = { id: 'overlay_release', name: 'overlay_release', x: 0, y: 0, width: 8, height: 8,
      visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
    const contractState = { ...source, revision: 1, documentRevision: 1,
      document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] },
      selection: { ...structuredClone(source.selection), roomId: room.id, tool: 3, layer: 0, material,
        brushSize: 1, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] } };
    const makeTile = overrides => ({ roomId: room.id, layer: 0, erase: false, live: false, material,
      shape: 0, groupId: '', brushSize: 1, tool: 3, baseRevision: 1, baseInstanceId: source.instanceId,
      baseDocumentRevision: 1, baseDocument: contractState.document, points: [{ x: 1, y: 1 }], rasterLength: 1,
      cells: new Map([['1,1', { x: 1, y: 1, shape: 0, material, groupId: '' }]]), overflow: false, ...overrides });
    const makeEditor = failing => {
      const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;width:64px;height:64px;z-index:-1';
      document.body.append(canvas);
      const command = async () => {
        if (failing) throw new Error('expected command failure');
        return { ...contractState, revision: 2, documentRevision: 2 };
      };
      const editor = new MapCanvas(canvas, command, () => {}, () => {}); editor.setState(contractState);
      editor.extendTileGesture = () => {};
      return { editor, canvas };
    };
    const gesture = tile => ({ kind: 'paint', pointer: 711, start: { x: 1, y: 1 }, last: { x: 1, y: 1 },
      screen: { x: 0, y: 0 }, center: { x: 0, y: 0 }, room, tile,
      expectation: { instanceId: source.instanceId, revision: 1 }, tail: Promise.resolve(), failed: false });
    const prime = (editor, tile, retainedSource = tile) => {
      editor.tileOverlay = tile;
      editor.overlayIndex = { source: retainedSource, size: 1,
        rows: new Map([[1, new Map([[1, tile.cells.get('1,1') || null]])]]),
        chunks: new Map([['0,0', [{ x: 1, y: 1 }]]]) };
      editor.overlayLod = { source: retainedSource, base: editor.occupancy.get(room.id + ':0'),
        chunks: new Map([['0,0', document.createElement('canvas')]]) };
    };
    const snapshot = editor => ({ tile: editor.tileOverlay === null, index: editor.overlayIndex.source === null,
      rows: editor.overlayIndex.rows.size, chunks: editor.overlayIndex.chunks.size, lod: editor.overlayLod === null });
    const finish = async (kind, tile, failing = false) => {
      const { editor, canvas } = makeEditor(failing), active = gesture(tile); prime(editor, tile); editor.gesture = active;
      if (kind === 'cancel') editor.cancel();
      else editor.up(new PointerEvent('pointerup', { bubbles: true, cancelable: true, pointerId: active.pointer }));
      await editor.settled(); const result = snapshot(editor); editor.dispose(); canvas.remove(); return result;
    };
    const complete = await finish('up', makeTile({}));
    const failed = await finish('up', makeTile({}), true);
    const emptyErase = await finish('up', makeTile({ erase: true, cells: new Map() }));
    const overflow = await finish('up', makeTile({ overflow: true }));
    const cancelled = await finish('cancel', makeTile({}));
    const { editor: otherEditor, canvas: otherCanvas } = makeEditor(false);
    const active = makeTile({}), other = makeTile({ points: [{ x: 2, y: 2 }] });
    prime(otherEditor, active, other); otherEditor.gesture = gesture(active); otherEditor.cancel();
    const preservesOther = otherEditor.tileOverlay === null && otherEditor.overlayIndex.source === other
      && otherEditor.overlayLod?.source === other;
    otherEditor.dispose(); otherCanvas.remove();
    return { complete, failed, emptyErase, overflow, cancelled, preservesOther };
  }, material);
  const releasedOverlay = { tile: true, index: true, rows: 0, chunks: 0, lod: true };
  for (const path of ['complete', 'failed', 'emptyErase', 'overflow', 'cancelled'])
    assert.deepEqual(overlayReleaseContract[path], releasedOverlay, `${path} must immediately release its tile overlay index and LOD cache.`);
  assert.equal(overlayReleaseContract.preservesOther, true, 'Releasing one gesture must not clear an overlay cache owned by another source.');

  const canvasSpriteVersionContract = await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js');
    const source = await (await fetch('/api/state')).json();
    const sprite = source.catalog.materials.flatMap(item => item.sprites || [])[0]
      || source.catalog.objects.map(item => item.sprite).find(Boolean);
    if (!sprite) return null;
    const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;width:64px;height:64px;z-index:-1';
    document.body.append(canvas);
    const editor = new MapCanvas(canvas, async () => source, () => {}, () => {});
    const first = { ...source, revision: 1, catalogRevision: 41 }; editor.setState(first);
    editor.sprite(sprite, { x: 0, y: 0, width: 16, height: 16 });
    const loaded = async () => {
      const deadline = performance.now() + 5000;
      while (editor.images.loading && performance.now() < deadline) await new Promise(resolve => setTimeout(resolve, 20));
      if (editor.images.loading) throw new Error('Timed out decoding atlas');
      return new URL(editor.images.get(sprite.asset).src);
    };
    const firstUrl = await loaded();
    const second = { ...first, revision: 2, catalogRevision: 42 }; editor.setState(second);
    editor.sprite(sprite, { x: 0, y: 0, width: 16, height: 16 });
    const secondUrl = await loaded();
    editor.dispose(); canvas.remove();
    return { firstVersion: firstUrl.searchParams.get('v'), secondVersion: secondUrl.searchParams.get('v'),
      samePath: firstUrl.searchParams.get('path') === secondUrl.searchParams.get('path') };
  });
  assert.ok(canvasSpriteVersionContract, 'The main-canvas cache contract needs at least one catalog sprite.');
  assert.deepEqual(canvasSpriteVersionContract,
    { firstVersion: `${state.instanceId}:41`, secondVersion: `${state.instanceId}:42`, samePath: true },
    'Main-canvas sprite URLs must change with the catalog revision even when the asset path stays the same.');
  const nodePreviewContract = await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js');
    const response = await fetch('/api/state'), source = await response.json();
    const definition = source.catalog.objects.find(item => item.placement === 2 && item.minimumNodes >= 2);
    if (!definition) return null;
    const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;left:0;top:0;width:320px;height:180px;z-index:-1'; document.body.append(canvas);
    const room = { id: 'node_preview', name: 'node_preview', x: 0, y: 0, width: 64, height: 32,
      visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
    const selection = { ...structuredClone(source.selection), roomId: room.id, tool: 1, layer: definition.layer,
      objectDefinition: definition.id, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] };
    const contractState = { ...source, document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] }, selection };
    const editor = new MapCanvas(canvas, async () => contractState, () => {}, () => {}); editor.center = { x: 16, y: 12 }; editor.setState(contractState);
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    let previewNodes = [];
    const drawPath = editor.drawSelectedObjectPath.bind(editor);
    editor.drawSelectedObjectPath = (targetRoom, object, view) => { previewNodes = structuredClone(object.nodes); drawPath(targetRoom, object, view); };
    const endpoint = { x: 18.3, y: 14.4 };
    editor.drawObjectPlacementPreview({ roomId: room.id, erase: false, baseRevision: 1, baseInstanceId: source.instanceId,
      points: [{ x: 10.2, y: 10.1 }, endpoint], hidden: new Set(), definition: definition.id,
      layer: definition.layer, groupId: '', tool: 1, rebaseAfterPending: false, overflow: false });
    editor.dispose(); canvas.remove(); return { definition: definition.id, minimumNodes: definition.minimumNodes, previewNodes,
      snappedStart: { x: Math.round(10.2 * 16) / 16, y: Math.round(10.1 * 16) / 16 },
      snappedEndpoint: { x: Math.round(endpoint.x * 16) / 16, y: Math.round(endpoint.y * 16) / 16 } };
  });
  assert.ok(nodePreviewContract, 'The node-placement preview fixture needs the Path definition.');
  assert.equal(nodePreviewContract.previewNodes.length, nodePreviewContract.minimumNodes,
    'Node placement preview must show the definition minimum node count.');
  assert.deepEqual(nodePreviewContract.previewNodes[0],
    { x: nodePreviewContract.snappedStart.x + 1, y: nodePreviewContract.snappedStart.y },
    'Node preview must retain generated intermediate nodes from the snapped placement start.');
  assert.deepEqual(nodePreviewContract.previewNodes.at(-1), nodePreviewContract.snappedEndpoint,
    'Node placement preview must use the dragged endpoint that core placement receives.');
  const maximumExpandedSegment = await page.evaluate(async ({ material, maximumWork }) => {
    const { MapCanvas } = await import('/map-canvas.js');
    const source = await (await fetch('/api/state')).json(), brushSize = 16;
    const rasterLength = Math.floor((maximumWork - 3 * brushSize * brushSize) / (2 * brushSize - 1)) + 1;
    const segmentLength = rasterLength - 1, roomSize = segmentLength + 32;
    const swept = new Map(), add = (x, y) => swept.set(`${x},${y}`, { x, y, shape: 0, material, groupId: '' });
    for (let y = 1; y <= brushSize; y++) for (let x = 1; x <= brushSize; x++) add(x, y);
    for (let step = 1; step <= segmentLength; step++) {
      const center = 8 + step, right = center + 8, top = center + 8;
      for (let y = center - 7; y <= center + 8; y++) add(right, y);
      for (let x = center - 7; x <= center + 7; x++) add(x, top);
    }
    window.__renderBench.longTasks.length = 0;
    const run = async erase => {
      const room = { id: `maximum_expanded_${erase ? 'erase' : 'paint'}`, name: 'maximum_expanded', x: 0, y: 0,
        width: roomSize, height: roomSize, visible: true, locked: false,
        foreground: erase ? [...swept.values()] : [], background: [], objects: [], properties: [] };
      const contractState = { ...source,
        document: { ...structuredClone(source.document), rooms: [room], layerGroups: [] }, connections: [],
        selection: { ...structuredClone(source.selection), roomId: room.id, tool: 3, layer: 0, shape: 0,
          material, brushSize, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], area: null, nodes: [] } };
      const samples = [];
      for (let trial = 0; trial < 5; trial++) {
        const canvas = document.createElement('canvas');
        canvas.style.cssText = 'position:fixed;left:0;top:0;width:320px;height:180px;z-index:-1'; document.body.append(canvas);
        canvas.setPointerCapture = () => {}; canvas.releasePointerCapture = () => {}; canvas.hasPointerCapture = () => false;
        const editor = new MapCanvas(canvas, async () => contractState, () => {}, () => {});
        editor.setState(contractState); editor.center = { x: 8, y: 8 };
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        const rect = canvas.getBoundingClientRect(), start = editor.toScreen({ x: 8, y: 8 });
        const pointer = { bubbles: true, cancelable: true, pointerId: 1201 + trial + (erase ? 10 : 0), pointerType: 'mouse', isPrimary: true };
        canvas.dispatchEvent(new PointerEvent('pointerdown', { ...pointer, button: erase ? 2 : 0, buttons: erase ? 2 : 1,
          clientX: rect.left + start.x, clientY: rect.top + start.y }));
        const target = editor.toScreen({ x: 8 + segmentLength, y: 8 + segmentLength }), started = performance.now();
        canvas.dispatchEvent(new PointerEvent('pointermove', { ...pointer, button: -1, buttons: erase ? 2 : 1,
          clientX: rect.left + target.x, clientY: rect.top + target.y }));
        samples.push({ dispatchMs: performance.now() - started, overlayCells: editor.tileOverlay?.cells.size || 0,
          actualRasterLength: editor.tileOverlay?.rasterLength || 0, overflow: editor.tileOverlay?.overflow ?? true });
        canvas.dispatchEvent(new PointerEvent('pointercancel', { ...pointer, button: erase ? 2 : 0, buttons: 0,
          clientX: rect.left + target.x, clientY: rect.top + target.y }));
        editor.dispose(); canvas.remove();
        await new Promise(resolve => setTimeout(resolve, 0));
      }
      return samples;
    };
    const paint = await run(false), erase = await run(true);
    const longTaskMaxMs = Math.max(0, ...window.__renderBench.longTasks);
    return { dispatchMs: Math.max(...paint.map(sample => sample.dispatchMs)),
      dispatchSamplesMs: paint.map(sample => sample.dispatchMs),
      eraseDispatchMs: Math.max(...erase.map(sample => sample.dispatchMs)),
      eraseDispatchSamplesMs: erase.map(sample => sample.dispatchMs), longTaskMaxMs, rasterLength,
      actualRasterLengths: paint.map(sample => sample.actualRasterLength),
      eraseRasterLengths: erase.map(sample => sample.actualRasterLength),
      overlayCellCounts: paint.map(sample => sample.overlayCells),
      eraseOverlayCellCounts: erase.map(sample => sample.overlayCells),
      expectedCells: brushSize * brushSize + segmentLength * (2 * brushSize - 1),
      sourceCells: swept.size, overflow: [...paint, ...erase].some(sample => sample.overflow) };
  }, { material, maximumWork: 65536 });
  assert.ok(maximumExpandedSegment.actualRasterLengths.every(length => length === maximumExpandedSegment.rasterLength),
    'Every maximum brush-16 trial must traverse its complete bounded raster path.');
  assert.ok(maximumExpandedSegment.eraseRasterLengths.every(length => length === maximumExpandedSegment.rasterLength),
    'Every maximum brush-16 erase trial must traverse its complete bounded raster path.');
  assert.ok(maximumExpandedSegment.overlayCellCounts.every(count => count === maximumExpandedSegment.expectedCells),
    'Every maximum brush-16 trial must preview every expanded cell.');
  assert.equal(maximumExpandedSegment.sourceCells, maximumExpandedSegment.expectedCells,
    'The maximum erase fixture must populate every swept source cell.');
  assert.ok(maximumExpandedSegment.eraseOverlayCellCounts.every(count => count === maximumExpandedSegment.expectedCells),
    'Every maximum brush-16 erase trial must preview every removed cell.');
  assert.equal(maximumExpandedSegment.overflow, false, 'The maximum in-budget brush-16 segment must remain valid.');
  assert.ok(maximumExpandedSegment.dispatchMs <= budgets.longTaskMaxMs,
    `Maximum brush-16 pointer segment blocked input for ${maximumExpandedSegment.dispatchMs.toFixed(1)}ms.`);
  assert.ok(maximumExpandedSegment.eraseDispatchMs <= budgets.longTaskMaxMs,
    `Maximum brush-16 erase segment blocked input for ${maximumExpandedSegment.eraseDispatchMs.toFixed(1)}ms.`);
  assert.ok(maximumExpandedSegment.longTaskMaxMs <= budgets.longTaskMaxMs,
    `Maximum brush-16 pointer segment produced a ${maximumExpandedSegment.longTaskMaxMs.toFixed(1)}ms long task.`);

  const maximumMiniMap = await page.evaluate(async () => {
    const { MiniMap } = await import('/minimap.js');
    const source = await (await fetch('/api/state')).json(), rooms = [];
    for (let row = 0; row < 32; row++) for (let column = 0; column < 32; column++) rooms.push({
      id: `mini_${row}_${column}`, name: `mini_${row}_${column}`, x: column * 10, y: row * 8,
      width: 8, height: 6, visible: true, locked: false, foreground: [], background: [], objects: [], properties: []
    });
    const connections = Array.from({ length: 16384 }, (_, index) => {
      const room = rooms[index % rooms.length], other = rooms[(index + 1) % rooms.length], vertical = index % 2 === 0;
      return { roomAId: room.id, roomBId: other.id, vertical,
        coordinate: vertical ? room.x + room.width : room.y + room.height,
        start: (vertical ? room.y : room.x) + index % 3, end: (vertical ? room.y : room.x) + index % 3 + 2 };
    });
    const contractState = { ...source,
      document: { ...structuredClone(source.document), rooms, layerGroups: [] }, connections,
      selection: { ...structuredClone(source.selection), roomId: rooms[0].id } };
    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:0;top:0;width:1280px;height:720px;z-index:-1'; document.body.append(canvas);
    canvas.setPointerCapture = () => {}; canvas.releasePointerCapture = () => {}; canvas.hasPointerCapture = () => false;
    const mini = new MiniMap(canvas, () => {}), durations = [];
    const draw = mini.draw.bind(mini); mini.draw = () => { const started = performance.now(); draw(); durations.push(performance.now() - started); };
    mini.setState(contractState); mini.setActive(true); await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    durations.length = 0; window.__renderBench.longTasks.length = 0;
    const rect = canvas.getBoundingClientRect(), pointer = { bubbles: true, cancelable: true, pointerId: 1202, pointerType: 'mouse', isPrimary: true };
    canvas.dispatchEvent(new PointerEvent('pointerdown', { ...pointer, button: 1, buttons: 4,
      clientX: rect.left + rect.width / 2, clientY: rect.top + rect.height / 2 }));
    for (let index = 0; index < 20; index++) {
      canvas.dispatchEvent(new PointerEvent('pointermove', { ...pointer, button: -1, buttons: 4,
        clientX: rect.left + rect.width / 2 + index * 2, clientY: rect.top + rect.height / 2 + index }));
      await new Promise(resolve => requestAnimationFrame(resolve));
    }
    canvas.dispatchEvent(new PointerEvent('pointerup', { ...pointer, button: 1, buttons: 0,
      clientX: rect.left + rect.width / 2 + 40, clientY: rect.top + rect.height / 2 + 20 }));
    await new Promise(resolve => setTimeout(resolve, 0));
    const result = { frames: durations, longTaskMaxMs: Math.max(0, ...window.__renderBench.longTasks),
      rooms: rooms.length, connections: connections.length };
    mini.dispose(); canvas.remove(); return result;
  });
  const maximumMiniMapStats = stats(maximumMiniMap.frames);
  assert.equal(maximumMiniMap.rooms, 1024); assert.equal(maximumMiniMap.connections, 16384);
  assert.ok(maximumMiniMapStats.count >= 18, 'The maximum MiniMap pan did not produce enough measured redraws.');
  assert.ok(maximumMiniMapStats.p95 <= budgets.drawCallbackMaxMs,
    `Maximum MiniMap pan redraw p95 ${maximumMiniMapStats.p95}ms exceeded ${budgets.drawCallbackMaxMs}ms.`);
  assert.ok(maximumMiniMap.longTaskMaxMs <= budgets.longTaskMaxMs,
    `Maximum MiniMap pan produced a ${maximumMiniMap.longTaskMaxMs.toFixed(1)}ms long task.`);
  const fullStatesBeforePolling = fullStates;
  await page.locator('#view-toolbar button').first().click();
  await page.waitForTimeout(350);
  await page.evaluate(() => {
    window.__renderBench.callbacks.length = 0;
    window.__renderBench.inputFrames.length = 0;
    window.__renderBench.longTasks.length = 0;
    window.__renderBench.fullFrames.map = 0;
    window.__renderBench.fullFrames.mini = 0;
  });

  const inputFrames = await page.locator('#map-canvas').evaluate(async canvas => {
    const rect = canvas.getBoundingClientRect(), samples = [];
    for (let index = 0; index < 30; index++) {
      const started = performance.now();
      canvas.dispatchEvent(new PointerEvent('pointermove', {
        bubbles: true, cancelable: true, pointerId: 41, pointerType: 'mouse', isPrimary: true,
        button: -1, buttons: 0, clientX: rect.left + 80 + index * 35, clientY: rect.top + 120 + index % 3 * 35
      }));
      await new Promise(resolve => requestAnimationFrame(resolve));
      samples.push(performance.now() - started);
    }
    return samples;
  });
  const renderTrace = await page.evaluate(() => structuredClone(window.__renderBench));
  const callbackStats = stats(renderTrace.callbacks), inputStats = stats(inputFrames);
  assert.ok(callbackStats.count >= 30, 'The stress test did not observe enough animation-frame callbacks.');
  assert.ok(callbackStats.p95 <= budgets.drawCallbackP95Ms, `Large-map draw callback p95 ${callbackStats.p95}ms exceeded ${budgets.drawCallbackP95Ms}ms.`);
  assert.ok(callbackStats.max <= budgets.drawCallbackMaxMs, `Large-map draw callback max ${callbackStats.max}ms exceeded ${budgets.drawCallbackMaxMs}ms.`);
  assert.ok(inputStats.p95 <= budgets.inputFrameP95Ms, `Large-map input-to-frame p95 ${inputStats.p95}ms exceeded ${budgets.inputFrameP95Ms}ms.`);
  assert.ok(Math.max(0, ...renderTrace.longTasks) <= budgets.longTaskMaxMs, `Large-map interaction produced a ${Math.max(...renderTrace.longTasks).toFixed(1)}ms long task.`);
  assert.ok(renderTrace.maxLodRasterPixels <= 32 * 32, `LOD allocated a ${renderTrace.maxLodRasterPixels}-pixel room-sized raster instead of bounded chunks.`);

  const paintSetup = await page.locator('#map-canvas').evaluate(canvas => {
    const rect = canvas.getBoundingClientRect(), width = 8 * 128, height = 8 * 72;
    const scale = Math.min((rect.width - 50) / width, (rect.height - 50) / height);
    const origin = { x: Math.round(rect.width / 2 - width / 2 * scale), y: Math.round(rect.height / 2 + height / 2 * scale) };
    const screen = point => ({ x: rect.left + origin.x + point.x * scale, y: rect.top + origin.y - point.y * scale });
    const points = [{ x: 16, y: 16 }, { x: 32, y: 24 }, { x: 48, y: 32 }, { x: 64, y: 24 },
      { x: 80, y: 32 }, { x: 96, y: 24 }, { x: 112, y: 32 }].map(screen);
    const sample = points.at(-1), localX = Math.round(sample.x - rect.left), localY = Math.round(sample.y - rect.top);
    const context = canvas.getContext('2d'), before = [...context.getImageData(localX - 5, localY - 5, 11, 11).data];
    window.__renderBench.paintFrames.length = 0; window.__renderBench.longTasks.length = 0;
    canvas.addEventListener('pointermove', event => {
      if (event.buttons !== 1) return;
      const started = performance.now(); requestAnimationFrame(() => window.__renderBench.paintFrames.push(performance.now() - started));
    });
    canvas.addEventListener('pointerup', () => { window.__renderBench.paintReleaseAt = performance.now(); });
    return { points, before, localX, localY, revision: document.querySelector('#status-revision').textContent };
  });
  await page.mouse.move(paintSetup.points[0].x, paintSetup.points[0].y); await page.mouse.down({ button: 'left' });
  for (const point of paintSetup.points.slice(1)) {
    await page.mouse.move(point.x, point.y); await page.evaluate(() => new Promise(resolve => requestAnimationFrame(resolve)));
  }
  const heldPreviewChanged = await page.locator('#map-canvas').evaluate((canvas, setup) => {
    const after = canvas.getContext('2d').getImageData(setup.localX - 5, setup.localY - 5, 11, 11).data;
    return after.some((value, index) => value !== setup.before[index]);
  }, paintSetup);
  await page.mouse.up({ button: 'left' });
  await page.waitForFunction(previous => document.querySelector('#status-revision').textContent !== previous, paintSetup.revision, { timeout: 1000 });
  const paintTrace = await page.evaluate(() => ({ frames: [...window.__renderBench.paintFrames],
    longTasks: [...window.__renderBench.longTasks], commitTailMs: performance.now() - window.__renderBench.paintReleaseAt }));
  const paintFrameStats = stats(paintTrace.frames);
  assert.equal(heldPreviewChanged, true, 'A held brush stroke must render its optimistic overlay before pointerup.');
  assert.equal(tileGestureRequests.length, 1, 'The stress stroke must commit as one tileGesture request.');
  assert.equal(tileGestureRequests[0].compactDocument, true, 'A live brush stroke must request the compact response.');
  assert.ok(tileGestureRequests[0].points.length <= paintSetup.points.length,
    `The stress stroke sent ${tileGestureRequests[0].points.length} path points instead of compact pointer samples.`);
  assert.ok(paintFrameStats.count >= paintSetup.points.length - 1, 'The held stress stroke did not produce enough measured preview frames.');
  assert.ok(paintFrameStats.p95 <= budgets.inputFrameP95Ms,
    `Brush-16 optimistic frame p95 ${paintFrameStats.p95}ms exceeded ${budgets.inputFrameP95Ms}ms.`);
  assert.ok(Math.max(0, ...paintTrace.longTasks) <= budgets.longTaskMaxMs,
    `Brush-16 optimistic paint produced a ${Math.max(0, ...paintTrace.longTasks).toFixed(1)}ms long task.`);
  assert.ok(paintTrace.commitTailMs <= 500, `Brush-16 compact commit tail ${paintTrace.commitTailMs.toFixed(1)}ms exceeded 500ms.`);

  await page.evaluate(() => { window.__renderBench.fullFrames.map = 0; window.__renderBench.fullFrames.mini = 0; });
  deliverIncremental = true;
  await page.waitForFunction(() => window.__renderBench.fullFrames.map > 0, null, { timeout: 3000 });
  let activeFrames = await page.evaluate(() => structuredClone(window.__renderBench.fullFrames));
  assert.equal(activeFrames.mini, 0, 'The hidden MiniMap canvas must not redraw on state updates.');

  await page.locator('#tabs').getByRole('button', { name: '미니맵', exact: true }).click();
  await page.locator('#mini-canvas').waitFor({ state: 'visible' });
  await page.waitForTimeout(100);
  await page.evaluate(() => { window.__renderBench.fullFrames.map = 0; window.__renderBench.fullFrames.mini = 0; });
  deliverIncremental = true;
  await page.waitForFunction(() => window.__renderBench.fullFrames.mini > 0, null, { timeout: 3000 });
  activeFrames = await page.evaluate(() => structuredClone(window.__renderBench.fullFrames));
  assert.equal(activeFrames.map, 0, 'The hidden map canvas must not redraw on state updates.');

  await page.waitForTimeout(4300);
  assert.equal(fullStates, fullStatesBeforePolling, 'Polling must not force-download the full document and catalog every four seconds.');
  assert.ok(incrementalStates >= 8, 'The performance test did not observe the expected conditional polling window.');

  console.log(JSON.stringify({
    passed: caseDefinition ? 20 : 19,
    fixture: {
      rooms: rooms.length,
      roomArea: rooms.reduce((sum, room) => sum + room.width * room.height, 0),
      tiles: rooms.reduce((sum, room) => sum + room.foreground.length, 0)
    },
    budgets,
    measurements: {
      drawCallbackMs: callbackStats, inputFrameMs: inputStats,
      longTaskMaxMs: +Math.max(0, ...renderTrace.longTasks).toFixed(2),
      maxLodRasterPixels: renderTrace.maxLodRasterPixels,
      responseRetry: { attempts: retryProbeAttempts.length, logicalExecutions: retryProbeExecutions,
        reusedCommandId: retryProbeAttempts[0].commandId === retryProbeAttempts[1].commandId },
      tileResponseLoss: { attempts: responseLossAttempts.length, logicalExecutions: responseLossExecutions,
        authoritativeShape: responseLossContract.documentShape, indexedShape: responseLossContract.indexedShape,
        interleavedShape: responseLossContract.interleavedShape },
      auxiliaryTimeout: { elapsedMs: +requestLifecycle.auxiliaryElapsed.toFixed(2), bodyAbortObserved: requestLifecycle.auxiliaryAbortObserved },
      thumbnailRefresh: { initialVersion: initialThumbnail.searchParams.get('v'), refreshedVersion: refreshedThumbnail.searchParams.get('v') },
      compactIndexContract,
      compactNoopContract,
      selectedPathContract,
      emptyEraseContract,
      overlayReleaseContract,
      canvasSpriteVersionContract,
      nodePreviewContract,
      maximumExpandedSegment: { ...maximumExpandedSegment,
        dispatchMs: +maximumExpandedSegment.dispatchMs.toFixed(2), longTaskMaxMs: +maximumExpandedSegment.longTaskMaxMs.toFixed(2) },
      maximumMiniMap: { frames: maximumMiniMapStats, longTaskMaxMs: +maximumMiniMap.longTaskMaxMs.toFixed(2),
        rooms: maximumMiniMap.rooms, connections: maximumMiniMap.connections },
      brush16Paint: { frames: paintFrameStats, heldPreviewChanged, requests: tileGestureRequests.length,
        points: tileGestureRequests[0].points.length, commitTailMs: +paintTrace.commitTailMs.toFixed(2) },
      activeCanvasFrames: activeFrames, polling: { fullStates, incrementalStates }
    }
  }, null, 2));
} finally {
  await browser.close();
}
