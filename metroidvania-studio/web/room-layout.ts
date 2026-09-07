import type { Room, Point } from './types.js';
// Mirrors the core's open-interval collision rule; merely nearby rooms never attract a drag.
export function resolveRoomMove(source: Room, delta: Point, rooms: Room[]): Point {
  return resolveRoomGroupMove([source], delta, rooms);
}
export function resolveRoomGroupMove(sources: Room[], delta: Point, rooms: Room[]): Point {
  if (!sources.length) return { x: 0, y: 0 };
  const ids = new Set(sources.map(room => room.id));
  const targets = rooms.filter(room => !ids.has(room.id));
  const exits = (at: number, intervals: [number, number][]): number[] => {
    intervals.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
    for (let i = 0; i < intervals.length; i++) {
      const left = intervals[i][0]; let right = intervals[i][1];
      while (i + 1 < intervals.length && intervals[i + 1][0] < right) right = Math.max(right, intervals[++i][1]);
      if (left < at && at < right) return [left, right];
    }
    return [at];
  };
  const horizontal: [number, number][] = [], vertical: [number, number][] = [];
  for (const source of sources) {
    const x = source.x + delta.x, y = source.y + delta.y;
    for (const target of targets) {
      if (y < target.y + target.height && y + source.height > target.y)
        horizontal.push([target.x - source.width - source.x, target.x + target.width - source.x]);
      if (x < target.x + target.width && x + source.width > target.x)
        vertical.push([target.y - source.height - source.y, target.y + target.height - source.y]);
    }
  }
  const valid = (p: Point) => p.x >= -2147483648 && p.x <= 2147483647 && p.y >= -2147483648 && p.y <= 2147483647
    && sources.every(s => s.x + p.x >= -2147483648 && s.y + p.y >= -2147483648 && s.x + p.x + s.width <= 2147483647 && s.y + p.y + s.height <= 2147483647);
  if (!valid(delta)) return { x: 0, y: 0 };
  const candidates = [...exits(delta.x, horizontal).map(x => ({ x, y: delta.y })), ...exits(delta.y, vertical).map(y => ({ x: delta.x, y }))]
    .filter(valid).sort((a, b) => Math.abs(a.x - delta.x) + Math.abs(a.y - delta.y) - Math.abs(b.x - delta.x) - Math.abs(b.y - delta.y));
  return candidates[0] || { x: 0, y: 0 };
}
