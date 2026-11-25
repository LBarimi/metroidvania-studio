import type { Connection, Rect, Room, State } from './types.js';

// Chip dimensions are multiples of eight map tiles. Existing rooms can use any tile bounds.
export const chipUnit = 8;
export interface MapChip { width: number; height: number }
type ChipGroup = { label: string; chips: MapChip[] };

function group(label: string, sizes: [number, number][], rotate = false): ChipGroup {
  return { label, chips: sizes.map(([w, h]) => ({ width: (rotate ? h : w) * chipUnit, height: (rotate ? w : h) * chipUnit })) };
}
const horizontal: [number, number][] = [[2, 1], [3, 1], [4, 2], [6, 3]];
const wide: [number, number][] = [[3, 2], [4, 3], [5, 3], [6, 4], [8, 4]];
const long: [number, number][] = [[4, 1], [6, 1], [8, 1], [12, 1], [8, 2]];
export const chipGroups: ChipGroup[] = [
  group('chipHorizontal', horizontal), group('chipVertical', horizontal, true),
  group('chipSquare', [[1, 1], [2, 2], [3, 3], [4, 4], [6, 6], [8, 8]]),
  group('chipWideHorizontal', wide), group('chipWideVertical', wide, true),
  group('chipLongHorizontal', long), group('chipLongVertical', long, true)
];

export function roomGrid(rooms: readonly Rect[]) {
  const origin = rooms[0] ?? { x: 0, y: 0 };
  let step = chipUnit;
  for (const room of rooms) {
    while ((room.x - origin.x) % step || (room.y - origin.y) % step || room.width % step || room.height % step) step /= 2;
    if (step === 1) break;
  }
  // Canonical offsets keep the same grid when rooms are reordered or lie at negative coordinates.
  return { step, x: ((origin.x % step) + step) % step, y: ((origin.y % step) + step) % step };
}

export interface MinimapEditTools {
  chip: MapChip;
  color: string;
  place: (bounds: Rect, state: State) => void;
  move: (id: string, dx: number, dy: number, state: State) => void;
  remove: (id: string, state: State) => void;
}

// Matches MiniMapGeometry: only opposing edges with positive shared length form a passage.
export function previewConnections(chip: Room, others: Iterable<Room>): Connection[] {
  const connections: Connection[] = [];
  for (const room of others) {
    if (chip.id === room.id) continue;
    for (const vertical of [true, false]) {
      const axis = vertical ? 'x' : 'y', length = vertical ? 'width' : 'height';
      const along = vertical ? 'y' : 'x', span = vertical ? 'height' : 'width';
      let coordinate: number;
      if (chip[axis] === room[axis] + room[length]) coordinate = chip[axis];
      else if (chip[axis] + chip[length] === room[axis]) coordinate = room[axis];
      else continue;
      const start = Math.max(chip[along], room[along]), end = Math.min(chip[along] + chip[span], room[along] + room[span]);
      if (start < end) connections.push({ roomAId: chip.id, roomBId: room.id, vertical, coordinate, start, end });
    }
  }
  return connections;
}
