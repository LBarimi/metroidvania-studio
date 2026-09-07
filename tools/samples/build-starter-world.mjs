import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

// Hand-arranged rooms: a loop with a side chamber and paired four-tile openings.
const rooms = [];
const property = (key, value) => ({ key, value });
function room(id, name, x, y, width, height, material, color, note) {
  const value = { id, name, x, y, width, height, visible: true, locked: false, foreground: [], background: [], objects: [],
    properties: [property('mapMaker.terrainTheme', material), property('mapMaker.minimapColor', color), property('note', note)] };
  const foreground = new Map(), background = new Map();
  const cell = (x, y, shape = 0, layer = 0, groupId = '') => {
    if (x < 0 || y < 0 || x >= width || y >= height) throw new Error('Tile outside ' + id);
    (layer === 0 ? foreground : background).set(x + ',' + y, { x, y, shape, material, groupId });
  };
  const rect = (x, y, w, h, layer = 0) => { for (let iy = y; iy < y + h; iy++) for (let ix = x; ix < x + w; ix++) cell(ix, iy, 0, layer, layer === 1 ? 'architecture' : ''); };
  const cut = (x, y, w, h) => { for (let iy = y; iy < y + h; iy++) for (let ix = x; ix < x + w; ix++) foreground.delete(ix + ',' + iy); };
  const object = (definition, x, y, w, h, label, layer = 2, nodes = []) => value.objects.push({ id: id + '-object-' + value.objects.length,
    definition, layer, groupId: '', x, y, width: w, height: h, rotation: 0, scaleX: 1, scaleY: 1, nodes,
    properties: [property(definition === 'Area' ? 'event' : definition === 'Spawn' ? 'player' : 'label', label)] });
  rect(0, 0, width, 2); rect(0, height - 1, width, 1); rect(0, 1, 1, height - 2); rect(width - 1, 1, 1, height - 2);
  rooms.push(value);
  return { value, cell, rect, cut, object, finish() {
    const ordered = cells => [...cells.values()].sort((a, b) => a.y - b.y || a.x - b.x);
    value.foreground = ordered(foreground); value.background = ordered(background);
  } };
}

const landing = room('landing', '01 Landing', 0, 0, 24, 18, 'terrain', '#2E9B50', 'Paint or erase the floor. Use the upper platforms to trace the route to Lookout.');
landing.rect(5, 4, 4, 1); landing.rect(12, 7, 5, 1); landing.rect(7, 10, 4, 1); landing.rect(12, 14, 4, 1);
landing.rect(17, 2, 5, 1); landing.cell(16, 2, 2);
landing.rect(2, 2, 3, 7, 1); landing.rect(3, 9, 8, 2, 1); landing.rect(18, 3, 3, 6, 1);
landing.cut(23, 4, 1, 4); landing.cut(8, 17, 4, 1); landing.object('Spawn', 3, 2, 1, 1, 'player'); landing.finish();

const canopy = room('canopy', '02 Canopy', 24, 0, 32, 18, 'terrain', '#2E9B50', 'Try the diagonal brush shapes. Toggle the background layer with its eye icon.');
canopy.rect(4, 5, 6, 1); canopy.cell(3, 5, 2); canopy.cell(10, 5, 1);
canopy.rect(16, 8, 5, 1); canopy.rect(26, 4, 5, 1);
canopy.rect(6, 2, 3, 2); canopy.rect(15, 14, 4, 3); canopy.cell(14, 16, 4); canopy.cell(19, 16, 3);
canopy.rect(4, 2, 2, 11, 1); canopy.rect(5, 12, 7, 2, 1); canopy.rect(20, 2, 3, 13, 1); canopy.rect(23, 12, 5, 2, 1);
canopy.cut(0, 4, 1, 4); canopy.cut(31, 4, 1, 4); canopy.object('Marker', 18, 9, 1, 1, 'Rest'); canopy.finish();

const shaft = room('shaft', '03 Lift Shaft', 56, 0, 14, 38, 'terrain-blue', '#287FC4', 'A vertical route joins the upper bridge and the side chamber.');
shaft.rect(1, 8, 4, 1); shaft.rect(8, 12, 5, 1); shaft.rect(1, 17, 4, 1); shaft.rect(8, 22, 5, 1); shaft.rect(1, 28, 5, 1); shaft.rect(8, 34, 5, 1);
shaft.rect(6, 3, 2, 12, 1); shaft.rect(6, 19, 2, 15, 1); shaft.rect(3, 35, 8, 1, 1);
shaft.cut(0, 4, 1, 4); shaft.cut(0, 29, 1, 4); shaft.cut(13, 12, 1, 4);
shaft.object('Path', 6, 4, 2, 1, 'Lift route', 2, [{ x: 6, y: 4 }, { x: 6, y: 31 }]); shaft.finish();

const bridge = room('bridge', '04 Sky Bridge', 24, 26, 32, 12, 'terrain-yellow', '#E5C341', 'Ctrl-click this room and the shaft, then drag a room title to move them together.');
bridge.rect(6, 2, 5, 1); bridge.cell(5, 2, 2); bridge.cell(11, 2, 1);
bridge.rect(20, 2, 5, 2); bridge.cell(19, 2, 2); bridge.cell(25, 3, 1); bridge.rect(24, 2, 2, 1);
bridge.rect(12, 8, 4, 3); bridge.cell(11, 10, 4); bridge.cell(16, 10, 3);
bridge.rect(2, 3, 27, 1, 1); bridge.rect(3, 4, 2, 4, 1); bridge.rect(14, 4, 2, 2, 1); bridge.rect(27, 4, 2, 4, 1);
bridge.cut(0, 3, 1, 4); bridge.cut(31, 3, 1, 4); bridge.finish();

const lookout = room('lookout', '05 Lookout', 0, 18, 24, 20, 'terrain-dark-gray', '#3B424B', 'Follow the loop back to Landing. Resize or rotate this room and use Undo to restore it.');
lookout.rect(1, 2, 5, 5); lookout.cell(6, 6, 1); lookout.rect(1, 7, 3, 6);
lookout.rect(10, 5, 4, 1); lookout.rect(16, 9, 5, 1); lookout.rect(7, 12, 4, 1); lookout.rect(15, 10, 8, 1);
lookout.rect(1, 16, 8, 3); lookout.cell(9, 18, 3);
lookout.rect(5, 3, 3, 14, 1); lookout.rect(8, 16, 10, 2, 1); lookout.rect(18, 3, 3, 14, 1);
lookout.cut(8, 0, 4, 2); lookout.cut(23, 11, 1, 4); lookout.object('Marker', 19, 11, 1, 1, 'Viewpoint'); lookout.finish();

const foundry = room('foundry', '06 Foundry', 70, 8, 32, 22, 'terrain-orange', '#DB812E', 'A wide side chamber: move the marker and resize the trigger rectangle.');
foundry.rect(1, 2, 4, 2); foundry.cell(5, 3, 1); foundry.rect(5, 2, 1, 1);
foundry.rect(26, 2, 5, 2); foundry.cell(25, 3, 2); foundry.rect(25, 2, 1, 1);
foundry.rect(4, 13, 6, 1); foundry.rect(21, 11, 7, 1); foundry.rect(13, 17, 6, 1);
foundry.rect(8, 2, 3, 14, 1); foundry.rect(11, 15, 10, 3, 1); foundry.rect(21, 2, 3, 14, 1); foundry.rect(13, 3, 6, 4, 1);
foundry.cut(0, 4, 1, 4); foundry.object('Marker', 16, 2, 1, 1, 'Challenge'); foundry.object('Area', 11, 2, 10, 7, 'encounter-preview', 3); foundry.finish();

const world = { formatVersion: 2, tileSize: 16, name: 'Starter World', rooms,
  properties: [property('description', 'Six connected rooms for painting, arranging rooms, editing layers and exploring the minimap.')],
  layerGroups: [{ id: 'architecture', name: 'Background architecture', parentId: '', layer: 1, visible: true, locked: false }], stylegrounds: [] };
writeFileSync(fileURLToPath(new URL('../../samples/maps/starter-world.map.json', import.meta.url)), JSON.stringify(world, null, 2) + '\n');
console.log('Wrote Starter World: ' + rooms.length + ' rooms.');
