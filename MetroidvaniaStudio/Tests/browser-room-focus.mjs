import assert from 'node:assert/strict';

// Read-only render probes inside the isolated browser workflow; no production map edits.
export async function verifyRoomFocus(page) {
  const result = await page.evaluate(async () => {
    const { MapCanvas } = await import('/map-canvas.js');
    const { MiniMap } = await import('/minimap.js');
    const source = await (await fetch('/api/state')).json();
    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:0;top:0;width:680px;height:400px';
    document.body.append(canvas);
    const miniCanvas = canvas.cloneNode(); document.body.append(miniCanvas);
    const map = new MapCanvas(canvas, async () => { throw new Error('Render probe cannot edit.'); }, () => {}, () => {});
    const mini = new MiniMap(miniCanvas, () => {});
    mini.setActive(true);
    const swatch = document.createElement('canvas'); swatch.width = swatch.height = 16;
    swatch.getContext('2d').fillStyle = '#ffffff'; swatch.getContext('2d').fillRect(0, 0, 16, 16);
    const image = new Image(); image.src = swatch.toDataURL(); await image.decode();
    const sprite = { asset: 'local-probe', x: 0, y: 0, width: 16, height: 16, shape: 0 };
    const cell = (x, y) => ({ x, y, shape: 0, material: 'probe', groupId: '' });
    const object = (id, layer, definition, x, y) => ({ id, layer, definition, x, y, width: 1, height: 1,
      groupId: '', rotation: 0, scaleX: 1, scaleY: 1, properties: [], nodes: [] });
    const rooms = ['A', 'B'].map((id, i) => ({ id, name: id, x: i * 10, y: 0, width: 8, height: 8,
      visible: true, locked: false, foreground: [cell(1, 1)], background: [cell(2, 1)], properties: [],
      objects: [object(id + '-sprite', 2, 'sprite', 1, 3), object(id + '-trigger', 3, 'fallback', 3, 3),
        object(id + '-entity', 2, 'fallback', 5, 3), object(id + '-front', 4, 'sprite', 1, 5),
        object(id + '-back', 5, 'sprite', 3, 5)] }));
    const mapDocument = { ...source.document, rooms, layerGroups: [], stylegrounds: [] };
    const original = JSON.stringify(mapDocument);
    const state = { ...source, document: mapDocument, documentRevision: 100, connections: [],
      selection: { ...source.selection, roomId: 'A', tool: 0, objects: [], area: null, nodes: [], hiddenLayers: [], lockedLayers: [] },
      catalogRevision: 100, catalog: { ...source.catalog, materials: [{ id: 'probe', color: '#e0b45c', sprites: [sprite] }],
        objects: [{ id: 'sprite', color: '#ffffff', sprite }, { id: 'fallback', color: '#50d8ff', sprite: null }] } };
    const samples = [[1.5, 1.5], [2.5, 1.5], [1.5, 3.5], [3.5, 3.5], [5.5, 3.5], [1.5, 5.5], [3.5, 5.5]];
    const pixel = (target, p) => [...target.getContext('2d').getImageData(Math.floor(p.x), Math.floor(p.y), 1, 1).data];
    const capture = () => rooms.map(room => samples.map(([x, y]) => pixel(canvas, map.toScreen({ x: room.x + x, y }))));
    function select(id) {
      const next = { ...state, selection: { ...state.selection, roomId: id } };
      map.setState(next); map.center = { x: 9, y: 4 }; map.draw();
      mini.setState(next); mini.center = { x: 9, y: 4 }; mini.scale = 16; mini.draw();
    }
    try {
      map.showGrid = map.showNames = false; map.setState(state); map.images.set('local-probe', image); map.pixelScale = 2;
      select('A'); const first = capture();
      const inactiveRect = mini.rect(rooms[1]);
      const miniCorner = pixel(miniCanvas, { x: inactiveRect.x - 2, y: inactiveRect.y - 2 });
      const miniWall = pixel(miniCanvas, { x: inactiveRect.x + 30, y: inactiveRect.y - 2 });
      const miniFill = pixel(miniCanvas, { x: inactiveRect.x + 20, y: inactiveRect.y + 20 });
      const alphaAfterDraw = [map.ctx.globalAlpha, mini.ctx.globalAlpha];
      select('B'); const switched = capture();
      select(null); const noSelection = capture();
      const unchanged = original === JSON.stringify(mapDocument);
      // Exercise cached, downsampled tile chunks as well as the exact sprite path above.
      for (const room of rooms) room.foreground = Array.from({ length: 64 }, (_, i) => cell(i % 8, Math.floor(i / 8)));
      state.documentRevision++; map.pixelScale = .25; select('A');
      const lod = rooms.map(room => pixel(canvas, map.toScreen({ x: room.x + 1.5, y: 1.5 })));
      const cachedChunks = [...map.occupancy.values()].reduce((n, index) => n + index.lod.size, 0);
      return { first, switched, noSelection, miniCorner, miniWall, miniFill, alphaAfterDraw, unchanged, lod, cachedChunks };
    } finally { map.dispose(); mini.dispose(); canvas.remove(); miniCanvas.remove(); }
  });
  const brighter = (active, inactive) => active.slice(0, 3).reduce((a, b) => a + b, 0) > inactive.slice(0, 3).reduce((a, b) => a + b, 0) + 15;
  // Room fill and translucent object fill each introduce up to one level of GPU alpha rounding.
  const sameColor = (left, right) => left.every((channel, j) => Math.abs(channel - right[j]) <= 2);
  for (let i = 0; i < result.first[0].length; i++) {
    assert.ok(brighter(result.first[0][i], result.first[1][i]), `Inactive room sample ${i} must dim.`);
    assert.ok(sameColor(result.first[0][i], result.switched[1][i]), `Newly active sample ${i} must recover its original color.`);
    assert.ok(sameColor(result.first[1][i], result.switched[0][i]),
      `Inactive sample ${i}: ${JSON.stringify(result.first[1][i])} vs ${JSON.stringify(result.switched[0][i])}`);
    assert.ok(sameColor(result.noSelection[0][i], result.noSelection[1][i]));
  }
  assert.deepEqual(result.miniCorner, [153, 153, 153, 255], 'MiniMap corners must not brighten from overlapping strokes.');
  assert.deepEqual(result.miniWall, result.miniCorner);
  assert.deepEqual(result.miniFill, [119, 26, 32, 255]);
  assert.deepEqual(result.alphaAfterDraw, [1, 1]);
  assert.equal(result.unchanged, true, 'Room focus must not change serialized map data.');
  assert.ok(result.cachedChunks >= 2);
  assert.ok(brighter(result.lod[0], result.lod[1]), 'Cached LOD terrain must dim too.');
}
