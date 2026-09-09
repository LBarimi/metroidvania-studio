import type { CameraProfile, Point, Rect, Room } from './types.js';

/** Local viewing state. Moving this camera never changes a map or its undo history. */
export class GameCamera {
  revision = 0;
  private room: Rect | null = null;
  private key = '';
  private width = 20;
  private height = 11.25;
  private position: Point = { x: 0, y: 0 };
  private snapping = true;
  get pixelPerfect(): boolean { return this.snapping; }
  setPixelPerfect(enabled: boolean): boolean {
    if (this.snapping === enabled) return false;
    this.snapping = enabled;
    if (this.room) this.position = this.clamp(this.position);
    this.revision++; return true;
  }
  get center(): Point { return { ...this.position }; }
  get frame(): Rect | null {
    return this.room ? { x: this.position.x - this.width / 2, y: this.position.y - this.height / 2, width: this.width, height: this.height } : null;
  }
  get visibleFrame(): Rect | null {
    const frame = this.frame, room = this.room; if (!frame || !room) return null;
    const x = Math.max(frame.x, room.x), y = Math.max(frame.y, room.y);
    return { x, y, width: Math.min(frame.x + frame.width, room.x + room.width) - x,
      height: Math.min(frame.y + frame.height, room.y + room.height) - y };
  }
  get clipped(): boolean { return !!this.room && (this.width > this.room.width || this.height > this.room.height); }
  sync(room: Room | undefined, camera: CameraProfile | undefined, instanceId: string): boolean {
    if (!room?.visible || !camera) {
      if (!this.room) return false;
      this.room = null; this.key = ''; this.position = { x: 0, y: 0 }; this.revision++; return true;
    }
    const key = instanceId + ':' + room.id, previous = this.room;
    const height = camera.referenceHeight / 16;
    const width = camera.referenceWidth / 16;
    const changed = this.key !== key || !previous || previous.x !== room.x || previous.y !== room.y
      || previous.width !== room.width || previous.height !== room.height || this.width !== width || this.height !== height;
    if (!changed) return false;
    const center = this.key === key && previous
      ? { x: this.position.x + room.x - previous.x + (width - this.width) / 2, y: this.position.y + room.y - previous.y + (height - this.height) / 2 }
      : { x: room.x + width / 2, y: room.y + height / 2 };
    this.key = key; this.room = { x: room.x, y: room.y, width: room.width, height: room.height };
    this.width = width; this.height = height; this.position = this.clamp(center); this.revision++; return true;
  }
  move(center: Point): boolean {
    if (!this.room || !Number.isFinite(center.x) || !Number.isFinite(center.y)) return false;
    const next = this.clamp(center);
    if (next.x === this.position.x && next.y === this.position.y) return false;
    this.position = next; this.revision++; return true;
  }
  recenter(): boolean {
    const room = this.room;
    return !!room && this.move({ x: room.x + room.width / 2, y: room.y + room.height / 2 });
  }
  private clamp(center: Point): Point {
    const room = this.room!;
    const axis = (center: number, start: number, length: number, frame: number) => {
      if (length <= frame) return start + frame / 2;
      // Snap the frame origin, rather than its center, to source pixels. This
      // also keeps odd reference resolutions aligned to the tile pixel grid.
      const rawOrigin = center - frame / 2;
      const origin = this.snapping ? Math.round(rawOrigin * 16) / 16 : rawOrigin;
      return Math.max(start, Math.min(start + length - frame, origin)) + frame / 2;
    };
    return { x: axis(center.x, room.x, room.width, this.width), y: axis(center.y, room.y, room.height, this.height) };
  }
}
