
const assert = (ok, message) => { if (!ok) throw new Error(message); };
const brand = document.querySelector('.topbar > .brand');
assert(brand && getComputedStyle(brand).display === 'none', 'Desktop branding should be hidden.');
assert(document.querySelector('#file-menu-button').getBoundingClientRect().left < 16, 'File menu should start at the left edge.');
assert(typeof window.showOpenFilePicker === 'function' && typeof window.showSaveFilePicker === 'function', 'Native map file pickers must be available.');
const { api, map } = window.metroidvaniaDesktop.test;
const material = api.state.catalog.materials[0].id;
const room = { id: 'desktop-room', name: 'Desktop room', x: 0, y: 0, width: 160, height: 90,
  visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] };
for (let y = 0; y < 90; y++) for (let x = 0; x < 160; x++)
  if (y !== 44) room.foreground.push({ x, y, shape: 0, material, groupId: '' });
await api.command('import', { document: { ...api.state.document, name: 'Desktop validation', rooms: [room] }, discard: true });
await api.command('selectRoom', { id: room.id });
await api.command('options', { tool: 3, layer: 0, material, brushSize: 1, shape: 0, groupId: '' });
await new Promise(resolve => requestAnimationFrame(resolve));
map.pixelScale = .25 * devicePixelRatio; map.frameRoom(); map.draw();
await new Promise(resolve => setTimeout(resolve, 300));
const canvas = document.querySelector('#map-canvas');
const capture = canvas.setPointerCapture, hasCapture = canvas.hasPointerCapture;
canvas.setPointerCapture = () => {}; canvas.hasPointerCapture = () => false;
let time = performance.now();
const input = (type, x, y, button = 0) => {
  const point = map.toScreen({ x, y }), rect = canvas.getBoundingClientRect();
  const event = new PointerEvent(type, { bubbles: true, cancelable: true, pointerId: 97,
    button, buttons: type === 'pointerup' ? 0 : button === 2 ? 2 : 1, clientX: point.x + rect.left, clientY: point.y + rect.top });
  Object.defineProperty(event, 'timeStamp', { value: ++time }); canvas.dispatchEvent(event);
};
const samples = [];
input('pointerdown', 3.5, 44.5);
for (let x = 4; x < 135; x++) { const start = performance.now(); input('pointermove', x + .5, 44.5); samples.push(performance.now() - start); }
input('pointerup', 134.5, 44.5);
await map.settled(); await api.settled(); await api.refresh();
assert(api.state.document.rooms[0].foreground.filter(cell => cell.y === 44).length === 132, 'Dragged tiles were lost.');
await api.command('undo');
assert(api.state.document.rooms[0].foreground.every(cell => cell.y !== 44), 'Undo failed.');
await api.command('redo');
assert(api.state.document.rooms[0].foreground.filter(cell => cell.y === 44).length === 132, 'Redo failed.');
input('pointerdown', 50.5, 44.5, 2); input('pointerup', 50.5, 44.5, 2);
await map.settled(); await api.settled(); await api.refresh();
assert(!api.state.document.rooms[0].foreground.some(cell => cell.x === 50 && cell.y === 44), 'Right-button erasing failed.');
const brushBefore = map.brushSize;
canvas.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, ctrlKey: true, deltaY: -120 }));
await map.settled(); await api.settled();
assert(map.brushSize !== brushBefore, 'Ctrl-wheel brush sizing failed.');
const zoomBefore = map.pixelScale;
canvas.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: -120, clientX: 800, clientY: 400 }));
await new Promise(resolve => setTimeout(resolve, 150));
assert(map.pixelScale !== zoomBefore, 'Wheel zoom failed.');
canvas.setPointerCapture = capture; canvas.hasPointerCapture = hasCapture;
await api.command('save', { path: 'DesktopValidation.map.json' });
await api.command('exportRooms', { directory: 'DesktopExport' });
const files = await (await fetch('/api/files')).json();
assert(files.some(file => file.startsWith('DesktopExport/')), 'Room JSON export failed.');
samples.sort((a,b) => a - b);
const p95 = samples[Math.floor(samples.length * .95)];
assert(p95 < 50, 'Brush event processing exceeded 50 ms at the 95th percentile.');
assert(canvas.width > 600 && canvas.height > 400, 'The editor viewport is cropped.');
return { headerStartsWithFile: true, tilesBeforePaint: room.foreground.length, paintedTiles: 132, inputP95Ms: p95, inputMaxMs: samples.at(-1),
  canvas: { width: canvas.width, height: canvas.height }, devicePixelRatio,
  drag: true, undoRedo: true, rightErase: true, ctrlWheel: true, wheelZoom: true, save: true, jsonExport: true };
