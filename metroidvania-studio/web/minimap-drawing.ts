import type { Connection, Rect, Room, State } from './types.js';
import { MINIMAP_CHIP_GROUPS, MINIMAP_CHIP_UNIT } from './contracts.generated.js';

export const chipUnit: number = MINIMAP_CHIP_UNIT;
export const chipGroups = MINIMAP_CHIP_GROUPS;
export interface MapChip { width: number; height: number }

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
