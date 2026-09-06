import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL || 'http://127.0.0.1:18765';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2 });
  if (process.env.METROIDVANIA_STUDIO_TEST_WEB_ROOT) await page.route('**/*.js', async route => {
    const name = path.basename(new URL(route.request().url()).pathname);
    await route.fulfill({ contentType: 'text/javascript', body: await readFile(path.join(process.env.METROIDVANIA_STUDIO_TEST_WEB_ROOT, name), 'utf8') });
  });
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  const report = await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js'), seed = await (await fetch('/api/state')).json();
    const canvas = document.createElement('canvas'); canvas.style.cssText = 'position:fixed;left:0;top:0;width:1280px;height:720px;z-index:1000';
    document.body.append(canvas); canvas.setPointerCapture = () => {}; canvas.hasPointerCapture = () => false;
    let state, editor, tileCalls = 0, timestamp = performance.now();
    editor = new MapCanvas(canvas, async () => {
      state = { ...state, revision: state.revision + 1, documentRevision: state.documentRevision + 1 };
      editor.setState(state); return state;
    }, () => {}, () => {});
    const drawCell = editor.drawTileCell.bind(editor);
    editor.drawTileCell = (...args) => { tileCalls++; return drawCell(...args); };
    const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
    const stats = list => { list.sort((a, b) => a - b); return { p50Ms: list[Math.floor(list.length * .5)], p95Ms: list[Math.floor(list.length * .95)] }; };
    const checks = [], measurements = [];
    function comparePixels(name) {
      editor.draw(); const cached = editor.ctx.getImageData(0, 0, canvas.width, canvas.height).data;
      const implementation = editor.exactTileChunk;
      editor.exactTileChunk = () => null; editor.draw(); editor.exactTileChunk = implementation;
      const direct = editor.ctx.getImageData(0, 0, canvas.width, canvas.height).data;
      let maxDelta = 0; for (let i = 0; i < direct.length; i++) maxDelta = Math.max(maxDelta, Math.abs(cached[i] - direct[i]));
      checks.push({ name, maxDelta });
    }
    function input(type, x, y, button = 0) {
      const p = editor.toScreen({ x, y }), event = new PointerEvent(type, { bubbles: true, cancelable: true,
        pointerId: 43, button, buttons: button === 2 ? 2 : 1, clientX: p.x, clientY: p.y });
      Object.defineProperty(event, 'timeStamp', { value: ++timestamp }); canvas.dispatchEvent(event);
    }
    try {
      for (const count of [1024, 8192, 14240, 262144]) {
        const width = count > 14240 ? 1024 : 160;
        const room = { id: 'scaling', name: 'scaling', x: 0, y: 0, width, height: count > 14240 ? 1024 : 90,
          visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
        for (let i = 0; i < count; i++) room.foreground.push({ x: i % width, y: Math.floor(i / width),
          shape: i % 101 === 0 ? 1 : 0, material: seed.catalog.materials[0].id, groupId: '' });
        state = { ...seed, revision: count, documentRevision: count,
          document: { ...seed.document, rooms: [room], layerGroups: [] },
          selection: { ...seed.selection, roomId: room.id, tool: 3, layer: 0, shape: 0, material: seed.catalog.materials[0].id,
            brushSize: 1, groupId: '', hiddenLayers: [], lockedLayers: [], objects: [], nodes: [], area: null } };
        editor.setState(state); editor.pixelScale = 1; editor.center = { x: 80, y: 45 }; await frame();
        const deadline = performance.now() + 5000;
        while ([...editor.images.values()].some(image => !image.complete) && performance.now() < deadline) await new Promise(resolve => setTimeout(resolve, 20));
        editor.draw(); await frame(); editor.draw();
        tileCalls = 0; const times = [];
        for (let sample = 0; sample < 16; sample++) { await frame(); const start = performance.now(); editor.draw(); times.push(performance.now() - start); }
        measurements.push({ tiles: count, ...stats(times), tileCalls, chunks: editor.exactTileChunks?.size || 0 });
        comparePixels(`${count} tiles: cached sprites and slopes match direct drawing`);
      }
      let frames = 0; const draw = editor.draw.bind(editor); editor.draw = (...args) => { frames++; return draw(...args); };
      await frame(); frames = 0;
      for (let i = 0; i < 6; i++) {
        state = { ...state, revision: state.revision + 1, selection: structuredClone(state.selection),
          export: { ...state.export, phase: i % 2 ? 'queued' : 'saved' } };
        editor.setState(state); await frame();
      }
      const passiveFrames = frames; editor.draw = draw;
      const before = structuredClone(state.document);
      const distantChunk = editor.exactTileChunks?.get('scaling:0:4,2')?.canvas;
      input('pointerdown', 31.5, 31.5, 2); input('pointerrawupdate', 32.5, 32.5, 2);
      comparePixels('held erase invalidates neighbours across four chunk corners');
      input('pointerup', 32.5, 32.5, 2); await editor.settled(); await frame();
      comparePixels('compact erase commit refreshes affected cached chunks');
      const distantRetained = !!distantChunk && editor.exactTileChunks.get('scaling:0:4,2')?.canvas === distantChunk;
      input('pointerdown', 31.5, 31.5); input('pointerrawupdate', 32.5, 32.5);
      comparePixels('repaint joins cached terrain across chunk corners');
      editor.cancel(); await frame(); comparePixels('cancel restores cached terrain');
      state = { ...state, document: before, documentRevision: state.documentRevision + 1 }; editor.setState(state); await frame();
      comparePixels('authoritative replacement discards cached geometry');
      const themed = structuredClone(state.document);
      themed.rooms[0].background = themed.rooms[0].foreground.splice(0, 1280);
      themed.layerGroups = [{ id: 'visibility', name: 'Visibility', layer: 0, parentId: '', visible: true, locked: false }];
      for (const tile of themed.rooms[0].foreground) tile.groupId = 'visibility';
      state = { ...state, document: themed, documentRevision: state.documentRevision + 1, selection: { ...state.selection, roomId: '' } };
      editor.setState(state); await frame(); comparePixels('background alpha and inactive-room opacity survive cached composition');
      const hidden = structuredClone(themed); hidden.layerGroups[0].visible = false;
      state = { ...state, document: hidden, documentRevision: state.documentRevision + 1 }; editor.setState(state); await frame();
      comparePixels('hidden layer groups invalidate baked tile images');
      state = { ...state, document: before, documentRevision: state.documentRevision + 1, selection: { ...state.selection, roomId: 'scaling' }, catalogRevision: state.catalogRevision + 1 };
      editor.setState(state); await frame();
      const deadline = performance.now() + 5000;
      while ([...editor.images.values()].some(image => !image.complete) && performance.now() < deadline) await new Promise(resolve => setTimeout(resolve, 20));
      comparePixels('catalog/image refresh rebuilds native sprite caches');
      const limits = [];
      for (let x = 80; x < 960; x += 64) { editor.center.x = x; editor.draw(); limits.push(editor.exactTileChunks?.size || 0); await frame(); }
      comparePixels('panning under cache pressure retains exact pixels');
      editor.center = { x: 80, y: 45 }; editor.pixelScale = .75; editor.draw();
      comparePixels('fractional overview falls back without resampling artefacts');
      editor.pixelScale = .5; editor.draw(); comparePixels('half-scale atlas edge sampling stays unchanged');
      const retained = [...(editor.exactTileChunks?.values() || [])].map(item => item.canvas);
      editor.dispose();
      return { measurements, passiveFrames, checks, distantRetained, maxChunks: Math.max(...limits), released: retained.every(item => item.width === 1 && item.height === 1) };
    } finally { editor.dispose(); canvas.remove(); }
  });
  console.log(JSON.stringify(report, null, 2));
  if (process.env.METROIDVANIA_STUDIO_SCALING_BASELINE !== '1') {
    assert.equal(report.passiveFrames, 0, 'Sync metadata must not redraw a large unchanged map.');
    for (const item of report.measurements) {
      assert.equal(item.tileCalls, 0, `${item.tiles} tiles: warm full frames must reuse cached sprites/masks.`);
      assert.ok(item.p95Ms < 12, `Warm full frame exceeded 12ms: ${JSON.stringify(item)}`);
    }
    for (const check of report.checks) assert.ok(check.maxDelta <= 2, JSON.stringify(check));
    assert.ok(report.maxChunks > 0 && report.maxChunks <= 64, 'All rooms/layers share a bounded cache.');
    assert.equal(report.released, true, 'Disposal must release cached bitmap storage.');
    assert.equal(report.distantRetained, true, 'A compact brush commit must retain untouched chunks.');
    console.log(`PASS: 4 tile-count workloads, passive updates, bounded cache and ${report.checks.length} pixel comparisons.`);
  }
} finally { await browser.close(); }
