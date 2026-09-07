import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL || 'http://127.0.0.1:18765';
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const browser = await chromium.launch({ channel: process.env.METROIDVANIA_STUDIO_BROWSER_CHANNEL || (process.platform === 'win32' ? 'msedge' : undefined), headless: true });
try {
 for (const deviceScaleFactor of [1, 2]) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor });
  await page.goto(base);
  await page.locator('#room-list button').first().waitFor();
  const result = await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js');
    const source = await (await fetch('/api/state')).json();
    const material = source.catalog.materials[0].id;
    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:0;top:0;width:1280px;height:720px;z-index:1000';
    document.body.append(canvas);
    // Synthetic input uses production listeners, with capture isolated from the
    // user's physical mouse. This fixture never submits a project command.
    canvas.setPointerCapture = () => {}; canvas.hasPointerCapture = () => false;
    const room = { id: 'latency', name: 'latency', x: 0, y: 0, width: 160, height: 90,
      visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
    for (let y = 0; y < room.height; y++) for (let x = 0; x < room.width; x++)
      if (y !== 44) room.foreground.push({ x, y, shape: 0, material, groupId: '' });
    room.objects.push({ id: 'outline-probe', definition: '__fallback_probe__', layer: 3, groupId: '',
      x: 63, y: 51, width: 1, height: 1, rotation: 25, scaleX: 6, scaleY: 2, nodes: [], properties: [] });
    const state = { ...source, document: { ...source.document, rooms: [room], layerGroups: [] },
      selection: { ...source.selection, roomId: room.id, tool: 3, layer: 0, material, shape: 0,
        brushSize: 1, groupId: '', hiddenLayers: [], lockedLayers: [], objects: ['outline-probe'], nodes: [], area: null } };
    const editor = new MapCanvas(canvas, () => { throw new Error('Latency fixture must not submit commands.'); }, () => {}, () => {});
    editor.setState(state); editor.pixelScale = .5 * devicePixelRatio; editor.showGrid = true; editor.frameRoom();
    await new Promise(resolve => requestAnimationFrame(resolve));
    const deadline = performance.now() + 5000;
    while (editor.images.loading && performance.now() < deadline)
      await new Promise(resolve => setTimeout(resolve, 20));
    editor.draw();
    const referenceFullMs = [];
    for (let sample = 0; sample < 12; sample++) {
      await new Promise(resolve => requestAnimationFrame(resolve));
      const start = performance.now(); editor.draw(); referenceFullMs.push(performance.now() - start);
    }
    const nativeRaf = window.requestAnimationFrame, nativeCancel = window.cancelAnimationFrame;
    const queued = new Map(); let nextId = 100000, clock = performance.now(), fullDraws = 0, partialDraws = 0;
    const nativeDraw = editor.draw.bind(editor), durations = [];
    editor.draw = damage => { const start = performance.now(); nativeDraw(damage); durations.push(performance.now() - start); if (damage) partialDraws++; else fullDraws++; };
    window.requestAnimationFrame = callback => { const id = ++nextId; queued.set(id, callback); return id; };
    window.cancelAnimationFrame = id => { queued.delete(id); nativeCancel(id); };
    function input(type, x, y, button = 0, samples) {
      const point = editor.toScreen({ x, y });
      const event = new PointerEvent(type, { bubbles: true, cancelable: true, pointerId: 71,
        button, buttons: button === 2 ? 2 : 1, clientX: point.x, clientY: point.y });
      Object.defineProperty(event, 'timeStamp', { value: ++clock });
      if (samples) Object.defineProperty(event, 'getCoalescedEvents', { value: () => samples });
      canvas.dispatchEvent(event); return event;
    }
    const frame = () => editor.ctx.getImageData(0, 0, canvas.width, canvas.height).data;
    function matchesFull() {
      const incremental = frame(); nativeDraw(); const full = frame();
      for (let i = 0; i < full.length; i++) if (full[i] !== incremental[i]) return { equal: false, byte: i, expected: full[i], actual: incremental[i] };
      return { equal: true };
    }
    const checks = [];
    try {
      input('pointermove', 2.5, 44.5);
      input('pointerdown', 2.5, 44.5);
      // Hold RAF completely: the first stamp and all following samples must
      // already be drawn, even while unrelated UI rendering is delayed.
      const immediateStart = editor.tileOverlay.cells.size > 0 && durations.length > 0;
      durations.length = 0; fullDraws = 0; partialDraws = 0;
      for (let x = 3; x < 135; x++) {
        input('pointerrawupdate', x + .5, 44.5);
        const rawTime = clock;
        const repeated = input('pointermove', x + .5, 44.5);
        // Repeated coalesced history must not retrace or consume stroke budget.
        const duplicate = new PointerEvent('pointermove', { pointerId: 71, clientX: repeated.clientX, clientY: repeated.clientY });
        Object.defineProperty(duplicate, 'timeStamp', { value: rawTime }); canvas.dispatchEvent(duplicate);
      }
      const rasterLength = editor.gesture.tile.rasterLength;
      const dense = { durations: [...durations], fullDraws, partialDraws, stamps: editor.tileOverlay.cells.size, rasterLength };
      checks.push({ name: 'dense raw brush pixels match a complete frame', ...matchesFull() });
      const rasterBeforeDuplicate = editor.gesture.tile.rasterLength;
      const rawA = input('pointerrawupdate', 132.5, 42.5), rawB = input('pointerrawupdate', 130.5, 40.5);
      input('pointermove', 130.5, 40.5, 0, [rawA, rawB]);
      const duplicateRaster = editor.gesture.tile.rasterLength - rasterBeforeDuplicate;
      checks.push({ name: 'direction reversal and repeated raw/coalesced input', ...matchesFull() });
      editor.cancel(); nativeDraw();
      // Clear deferred full invalidations before exercising regional painting.
      if (editor.raf) { window.cancelAnimationFrame(editor.raf); editor.raf = 0; }
      input('pointerdown', 60.5, 48.5, 2);
      input('pointermove', 64.5, 52.5, 2);
      input('pointermove', 58.5, 52.5, 2);
      checks.push({ name: 'diagonal erasing preserves autotile masks and rotated object outlines', ...matchesFull() });
      editor.cancel();
      editor.pixelScale = 2; editor.center = { x: 8, y: 45 }; nativeDraw();
      if (editor.raf) { window.cancelAnimationFrame(editor.raf); editor.raf = 0; }
      input('pointermove', .5, 44.5); input('pointerdown', .5, 44.5);
      input('pointermove', -.5, 45.5); input('pointermove', 1.5, 46.5);
      checks.push({ name: 'room border, grid and cursor leave no clipped residue', ...matchesFull() });
      editor.cancel();
      if (devicePixelRatio === 2) {
        // At 8 physical pixels/tile, this viewport can cross the 32,768-cell
        // exact-render limit. A tiny dirty region must retain full-view LOD.
        room.width = 320; room.height = 180; room.foreground = [];
        for (let i = 0; i < 32767; i++) room.foreground.push({ x: i % 320, y: Math.floor(i / 320), shape: 0, material, groupId: '' });
        editor.setState({ ...state, documentRevision: state.documentRevision + 1 });
        editor.pixelScale = .5; editor.frameRoom(); nativeDraw();
        if (editor.raf) { window.cancelAnimationFrame(editor.raf); editor.raf = 0; }
        input('pointermove', 127.5, 102.5); input('pointerdown', 127.5, 102.5);
        input('pointerrawupdate', 128.5, 102.5);
        checks.push({ name: 'crossing the viewport LOD threshold invalidates the whole frame', ...matchesFull() });
        input('pointerrawupdate', 131.5, 103.5);
        checks.push({ name: 'regional painting keeps the viewport LOD mode', ...matchesFull() });
        editor.cancel();
      }
      return { immediateStart, dense, duplicateRaster, checks, referenceFullMs, context: editor.ctx.getContextAttributes?.() };
    } finally {
      window.requestAnimationFrame = nativeRaf; window.cancelAnimationFrame = nativeCancel;
      editor.dispose(); canvas.remove();
    }
  });
  const sorted = result.dense.durations.sort((a, b) => a - b);
  result.referenceFullMs.sort((a, b) => a - b);
  const summary = { ...result, deviceScaleFactor, referenceFullMs: undefined,
    fullFrameP95Ms: result.referenceFullMs[Math.floor(result.referenceFullMs.length * .95)], dense: { ...result.dense, durations: undefined,
    p50Ms: sorted[Math.floor(sorted.length * .5)] || 0, p95Ms: sorted[Math.floor(sorted.length * .95)] || 0,
    maxMs: sorted.at(-1) || 0 } };
  console.log(JSON.stringify(summary, null, 2));
  if (process.env.METROIDVANIA_STUDIO_LATENCY_BASELINE !== '1') {
    assert.equal(result.immediateStart, true, 'Pointer-down must draw before any RAF callback.');
    assert.equal(result.dense.partialDraws, 132, 'Every new raw cell must draw immediately, with no duplicate pointermove draw.');
    assert.equal(result.dense.fullDraws, 0, 'Held brush input must not redraw the entire viewport.');
    assert.equal(result.dense.stamps, 133, 'No cells may disappear while RAF is delayed.');
    assert.equal(result.dense.rasterLength, 133, 'Raw samples and pointermove must not duplicate the path.');
    assert.equal(result.duplicateRaster, 4, 'Coalesced raw history must not be replayed backwards.');
    for (const check of result.checks) assert.equal(check.equal, true, JSON.stringify(check));
    assert.ok(summary.dense.p95Ms < 8, `Dense brush drawing exceeded the 8ms input budget: ${summary.dense.p95Ms}ms.`);
    console.log(`PASS: immediate brush input, dense damage rendering, raw deduplication and ${result.checks.length} pixel equivalence checks.`);
  }
  await page.close();
 }
} finally { await browser.close(); }
