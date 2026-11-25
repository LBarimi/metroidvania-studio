import type { Point, Room } from './types.js';

export const minimapShapeProperty = 'mapMaker.minimapShape';
export const minimapRegionColorProperty = 'mapMaker.minimapRegionColor';
export const minimapColorProperty = 'mapMaker.minimapColor';
export { DEFAULT_MINIMAP_COLOR as defaultMinimapColor } from './contracts.generated.js';
export const minimapGridSize = 1024;

export interface MinimapEdge { vertical: boolean; coordinate: number; start: number; end: number }
export interface MinimapShape {
  rows: string[];
  width: number;
  height: number;
  fill: Path2D;
  edges: MinimapEdge[];
  anchor: Point;
}

export function readMinimapShape(room: Room): MinimapShape | null {
  const value = room.properties.find(p => p.key === minimapShapeProperty)?.value;
  if (!value || value.length > minimapGridSize * (minimapGridSize + 1) - 1) return null;
  const rows = value.split('/'), width = rows[0].length, height = rows.length;
  if (width < 1 || width > minimapGridSize || height > minimapGridSize
    || rows.some(row => row.length !== width || /[^01]/.test(row)) || !value.includes('1')) return null;

  const fill = new Path2D(), edges: MinimapEdge[] = [];
  let distance = Infinity, anchor = { x: width / 2, y: height / 2 };
  for (let y = 0; y < height; y++) for (const match of rows[y].matchAll(/1+/g)) {
    const start = match.index!, length = match[0].length;
    fill.rect(start, y, length, 1);
    const x = Math.max(start, Math.min(start + length - 1, Math.floor(width / 2))) + .5;
    const next = (x - width / 2) ** 2 + (y + .5 - height / 2) ** 2;
    if (next < distance) { distance = next; anchor = { x, y: y + .5 }; }
  }
  for (const vertical of [false, true]) {
    const lines = vertical ? width : height, length = vertical ? height : width;
    for (let coordinate = 0; coordinate <= lines; coordinate++) {
      let start = -1;
      for (let along = 0; along <= length; along++) {
        const boundary = along < length && (vertical
          ? (rows[along][coordinate] === '1') !== (rows[along][coordinate - 1] === '1')
          : (rows[coordinate]?.[along] === '1') !== (rows[coordinate - 1]?.[along] === '1'));
        if (boundary && start < 0) start = along;
        if (!boundary && start >= 0) { edges.push({ vertical, coordinate, start, end: along }); start = -1; }
      }
    }
  }
  return { rows, width, height, fill, edges, anchor };
}

export function minimapWorldEdge(room: Room, shape: MinimapShape, edge: MinimapEdge): MinimapEdge {
  const across = edge.vertical ? room.width / shape.width : room.height / shape.height;
  const along = edge.vertical ? room.height / shape.height : room.width / shape.width;
  const offset = edge.vertical ? room.y : room.x;
  return { vertical: edge.vertical, coordinate: (edge.vertical ? room.x : room.y) + edge.coordinate * across,
    start: offset + edge.start * along, end: offset + edge.end * along };
}
