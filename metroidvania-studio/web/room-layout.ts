import type { Room, Point } from './types.js';
// Mirrors the core's open-interval collision rule; merely nearby rooms never attract a drag.
export function resolveRoomMove(source: Room, delta: Point, rooms: Room[]): Point {
  const x = source.x + delta.x, y = source.y + delta.y;
  const targets = rooms.filter(room => room.id !== source.id);
  const exits = (at: number, intervals: [number, number][]): number[] => {
    intervals.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
    for (let i = 0; i < intervals.length; i++) {
      const left = intervals[i][0]; let right = intervals[i][1];
      while (i + 1 < intervals.length && intervals[i + 1][0] < right) right = Math.max(right, intervals[++i][1]);
      if (left < at && at < right) return [left, right];
    }
    return [at];
  };
  const horizontal = targets.filter(r => y < r.y + r.height && y + source.height > r.y).map(r => [r.x - source.width, r.x + r.width] as [number, number]);
  const vertical = targets.filter(r => x < r.x + r.width && x + source.width > r.x).map(r => [r.y - source.height, r.y + r.height] as [number, number]);
  const candidates = [...exits(x, horizontal).map(x => ({ x, y })), ...exits(y, vertical).map(y => ({ x, y }))]
    .filter(p => p.x >= -2147483648 && p.y >= -2147483648 && p.x + source.width <= 2147483647 && p.y + source.height <= 2147483647
      && p.x - source.x >= -2147483648 && p.x - source.x <= 2147483647 && p.y - source.y >= -2147483648 && p.y - source.y <= 2147483647)
    .sort((a, b) => Math.abs(a.x - x) + Math.abs(a.y - y) - Math.abs(b.x - x) - Math.abs(b.y - y));
  const result = candidates[0] || source;
  return { x: result.x - source.x, y: result.y - source.y };
}
