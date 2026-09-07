import type { State, Room, Rect, Point, Connection } from './types.js';
import { roomOpacity } from './types.js';

// Style values are measured at five screen pixels per tile, then zoom with the map.
const STYLE_SCALE = 5, MIN_SCALE = .00000001, MAX_SCALE = 80;
const MAX_PASSAGE_GAP = 4; // Map tiles; long shared boundaries retain a compact central opening.

interface Span { vertical: boolean; coordinate: number; start: number; end: number; connection: Connection }
export function roomColor(room: Room): string {
  const color = room.properties.find(p => p.key === 'mapMaker.minimapColor')?.value || room.properties.find(p => p.key === 'color')?.value;
  return color && /^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(color) ? color : '#c72b36';
}
function dimColor(color: string, opacity: number): string {
  if (opacity === 1) return color;
  const rgb = Number.parseInt(color.slice(1, 7), 16);
  const red = Math.round((rgb >> 16) * opacity), green = Math.round((rgb >> 8 & 255) * opacity), blue = Math.round((rgb & 255) * opacity);
  return '#' + ((red << 16) | (green << 8) | blue).toString(16).padStart(6, '0') + color.slice(7);
}
export class MiniMap {
  state: State | null = null;
  center: Point = { x: 0, y: 0 };
  scale = STYLE_SCALE;
  showNames = false;
  showGrid = false;
  outlineWidth = 9;
  entranceLength = 15;
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private width = 1;
  private height = 1;
  private pixelRatio = 1;
  private resize: ResizeObserver;
  private abort = new AbortController();
  private raf = 0;
  private initialized = false;
  private active = false;
  private drag: { id: number; point: Point; center: Point } | null = null;
  private wheelDelta = 0;
  private wheelDirection = 0;
  private wheelReset = 0;
  private select: (id: string) => void;
  constructor(canvas: HTMLCanvasElement, select: (id: string) => void, open: (id: string) => void = () => {}) {
    this.canvas = canvas; this.select = select;
    const ctx = canvas.getContext('2d', { alpha: false }); if (!ctx) throw new Error('Canvas2D unavailable'); this.ctx = ctx;
    const options = { signal: this.abort.signal };
    canvas.tabIndex = 0;
    canvas.addEventListener('contextmenu', e => e.preventDefault(), options);
    canvas.addEventListener('pointerdown', e => {
      if (!this.active || this.drag || ![0, 1, 2].includes(e.button)) return;
      e.preventDefault(); canvas.focus(); const point = this.point(e);
      if (e.button === 1 || e.button === 0 && e.altKey) { this.drag = { id: e.pointerId, point, center: { ...this.center } }; canvas.setPointerCapture(e.pointerId); }
      else if (e.button === 0) { const room = this.roomAt(this.world(point)); if (room) this.select(room.id); }
    }, options);
    canvas.addEventListener('pointermove', e => { if (!this.active || !this.drag || this.drag.id !== e.pointerId) return; e.preventDefault(); const p = this.point(e); this.center = { x: this.drag.center.x - (p.x - this.drag.point.x) / this.scale, y: this.drag.center.y + (p.y - this.drag.point.y) / this.scale }; this.requestDraw(); }, options);
    const end = (event?: PointerEvent) => { const drag = this.drag; if (!drag || event && event.pointerId !== drag.id) return; this.drag = null; if (canvas.hasPointerCapture(drag.id)) canvas.releasePointerCapture(drag.id); };
    canvas.addEventListener('pointerup', end, options); canvas.addEventListener('pointercancel', end, options); canvas.addEventListener('lostpointercapture', end, options); window.addEventListener('blur', () => end(), options);
    canvas.addEventListener('dblclick', e => {
      if (!this.active || this.drag || e.button !== 0 || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
      const room = this.roomAt(this.world(this.point(e))); if (!room) return;
      e.preventDefault(); open(room.id);
    }, options);
    canvas.addEventListener('wheel', e => this.wheel(e), { ...options, passive: false });
    this.resize = new ResizeObserver(() => this.requestDraw()); this.resize.observe(canvas);
  }
  get interacting(): boolean { return this.drag !== null; }
  setActive(active: boolean): void {
    if (active === this.active) return;
    this.active = active;
    if (!active) { this.cancelInteraction(); this.resetWheel(); cancelAnimationFrame(this.raf); this.raf = 0; return; }
    if (!this.initialized && this.state?.document.rooms.length) { this.initialized = true; this.fit(); }
    else this.requestDraw();
  }
  cancelInteraction(): void {
    const drag = this.drag; this.drag = null;
    if (drag && this.canvas.hasPointerCapture(drag.id)) this.canvas.releasePointerCapture(drag.id);
  }
  private wheel(event: WheelEvent): void {
    event.preventDefault();
    if (!this.active || this.drag || !Number.isFinite(event.deltaY) || !event.deltaY) { if (this.drag) this.resetWheel(); return; }
    const direction = Math.sign(event.deltaY);
    if (direction !== this.wheelDirection) { this.wheelDelta = 0; this.wheelDirection = direction; }
    clearTimeout(this.wheelReset);
    this.wheelReset = window.setTimeout(() => this.resetWheel(), 180);
    const amount = event.deltaMode === WheelEvent.DOM_DELTA_PIXEL ? Math.abs(event.deltaY) : 120;
    if (amount >= 100) this.wheelDelta = 0;
    else { this.wheelDelta += amount; if (this.wheelDelta < 40) return; this.wheelDelta %= 40; }
    const point = this.point(event), before = this.world(point);
    this.scale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, this.scale * (direction < 0 ? 1.2 : 1 / 1.2)));
    const after = this.world(point); this.center.x += before.x - after.x; this.center.y += before.y - after.y; this.requestDraw();
  }
  private resetWheel(): void { clearTimeout(this.wheelReset); this.wheelReset = 0; this.wheelDelta = 0; this.wheelDirection = 0; }
  setState(state: State): void {
    const previous = this.state, changed = !previous || previous.instanceId !== state.instanceId
      || previous.documentRevision !== state.documentRevision || previous.selection.roomId !== state.selection.roomId;
    this.state = state;
    if (this.active && !this.initialized && state.document.rooms.length) { this.initialized = true; this.fit(); }
    else if (changed) this.requestDraw();
  }
  fit(): void {
    if (!this.active) { this.initialized = false; return; }
    const rooms = this.state?.document.rooms; if (!rooms?.length) { this.center = { x: 0, y: 0 }; return; }
    this.initialized = true;
    const bounds = this.canvas.getBoundingClientRect();
    let x = Infinity, y = Infinity, right = -Infinity, top = -Infinity;
    for (const room of rooms) { x = Math.min(x, room.x); y = Math.min(y, room.y); right = Math.max(right, room.x + room.width); top = Math.max(top, room.y + room.height); }
    this.center = { x: (x + right) / 2, y: (y + top) / 2 };
    this.scale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, Math.min(Math.max(1, bounds.width - 90) / Math.max(1, right - x), Math.max(1, bounds.height - 90) / Math.max(1, top - y)))); this.requestDraw();
  }
  frameRoom(): void { const r = this.state?.document.rooms.find(r => r.id === this.state?.selection.roomId); if (r) this.center = { x: r.x + r.width / 2, y: r.y + r.height / 2 }; this.requestDraw(); }
  private point(event: MouseEvent): Point { const r = this.canvas.getBoundingClientRect(); return { x: event.clientX - r.left, y: event.clientY - r.top }; }
  private roomAt(point: Point): Room | undefined {
    const rooms = this.state?.document.rooms; if (!rooms) return undefined;
    for (let index = rooms.length - 1; index >= 0; index--) {
      const room = rooms[index];
      if (point.x >= room.x && point.x < room.x + room.width && point.y >= room.y && point.y < room.y + room.height) return room;
    }
    return undefined;
  }
  private world(p: Point): Point { return { x: this.center.x + (p.x - this.width / 2) / this.scale, y: this.center.y - (p.y - this.height / 2) / this.scale }; }
  private snap(value: number): number { return Math.round(value * this.pixelRatio) / this.pixelRatio; }
  private screen(p: Point): Point { return { x: this.snap(this.width / 2 + (p.x - this.center.x) * this.scale), y: this.snap(this.height / 2 - (p.y - this.center.y) * this.scale) }; }
  private rect(room: Room): Rect { const a = this.screen({ x: room.x, y: room.y + room.height }), b = this.screen({ x: room.x + room.width, y: room.y }); return { x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y }; }
  private span(connection: Connection): Span {
    const length = connection.end - connection.start;
    const marker = Math.min(this.entranceLength / STYLE_SCALE, Math.max(0, (length - 1 / STYLE_SCALE) / 2));
    const opening = Math.min(length, marker * 2 + MAX_PASSAGE_GAP);
    if (opening < length) {
      const middle = connection.start + length / 2;
      connection = { ...connection, start: middle - opening / 2, end: middle + opening / 2 };
    }
    const a = this.screen(connection.vertical ? { x: connection.coordinate, y: connection.start } : { x: connection.start, y: connection.coordinate });
    const b = this.screen(connection.vertical ? { x: connection.coordinate, y: connection.end } : { x: connection.end, y: connection.coordinate });
    return { vertical: connection.vertical, coordinate: connection.vertical ? a.x : a.y,
      start: connection.vertical ? b.y : a.x, end: connection.vertical ? a.y : b.x, connection };
  }
  private stub(span: Span): number {
    // Limit both ends in map units, so a short doorway keeps its proportions at every zoom.
    const length = (span.connection.end - span.connection.start) * STYLE_SCALE;
    return Math.min(this.entranceLength, Math.max(0, (length - 1) / 2)) * this.scale / STYLE_SCALE;
  }
  private halfStroke(thickness: number): number { return Math.floor(Math.round(thickness * this.pixelRatio) / 2) / this.pixelRatio; }
  requestDraw(): void { if (this.active && !this.raf) this.raf = requestAnimationFrame(() => { this.raf = 0; this.draw(); }); }
  private draw(): void {
    if (!this.active || this.canvas.hidden) return;
    const r = this.canvas.getBoundingClientRect(); if (r.width <= 0 || r.height <= 0) return;
    const dpr = window.devicePixelRatio || 1; this.width = r.width; this.height = r.height; this.pixelRatio = dpr;
    const width = Math.round(r.width * dpr), height = Math.round(r.height * dpr); if (this.canvas.width !== width || this.canvas.height !== height) { this.canvas.width = width; this.canvas.height = height; }
    const ctx = this.ctx; ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.fillStyle = '#000'; ctx.fillRect(0, 0, this.width, this.height); if (!this.state) return;
    const spans = this.state.connections.map(c => this.span(c)), roomSpans = new Map<string, Span[]>();
    for (const span of spans) for (const id of [span.connection.roomAId, span.connection.roomBId]) { if (!roomSpans.has(id)) roomSpans.set(id, []); roomSpans.get(id)!.push(span); }
    const rooms = new Map(this.state.document.rooms.map(room => [room.id, room]));
    const thickness = this.outlineWidth * this.scale / STYLE_SCALE;
    const walls = new Path2D();
    const opacity = (room: Room) => roomOpacity(room.id, this.state!.selection.roomId);
    for (const room of rooms.values()) { const rect = this.rect(room); ctx.globalAlpha = room.visible ? 1 : .4; ctx.fillStyle = dimColor(roomColor(room), opacity(room)); ctx.fillRect(rect.x, rect.y, rect.width, rect.height); }
    ctx.globalAlpha = 1;
    for (const room of rooms.values()) {
      const rect = this.rect(room), shares = roomSpans.get(room.id) || [];
      const path = walls;
      this.edge(path, false, rect.y, rect.x, rect.x + rect.width, shares, thickness);
      this.edge(path, false, rect.y + rect.height, rect.x, rect.x + rect.width, shares, thickness);
      this.edge(path, true, rect.x, rect.y, rect.y + rect.height, shares, thickness);
      this.edge(path, true, rect.x + rect.width, rect.y, rect.y + rect.height, shares, thickness);
    }
    // Shared boundaries are drawn once, with exactly the same centered stroke as the outer walls.
    for (const span of spans) {
      const a = rooms.get(span.connection.roomAId), b = rooms.get(span.connection.roomBId); if (!a || !b) continue;
      const path = walls;
      const stub = this.stub(span), low = this.halfStroke(thickness), high = thickness - low;
      this.stroke(path, span.vertical, span.coordinate, span.start - low, span.start + stub, thickness);
      this.stroke(path, span.vertical, span.coordinate, span.end - stub, span.end + high, thickness);
    }
    // Selection dims room interiors only. Fill all walls and entrances together so
    // shared edges keep identical white coverage, including at fractional zoom.
    ctx.fillStyle = '#ffffff'; ctx.fill(walls);
    if (this.showNames) { ctx.font = '11px system-ui'; ctx.textAlign = 'center'; for (const room of rooms.values()) { const p = this.screen({ x: room.x + room.width / 2, y: room.y + room.height / 2 }); ctx.fillStyle = '#000c'; ctx.fillRect(p.x - ctx.measureText(room.name).width / 2 - 4, p.y - 8, ctx.measureText(room.name).width + 8, 16); ctx.fillStyle = dimColor('#ffffff', opacity(room)); ctx.fillText(room.name, p.x, p.y + 4); } }
    ctx.globalAlpha = 1;
    const selected = rooms.get(this.state.selection.roomId || ''); if (selected) { const p = this.screen({ x: selected.x + selected.width / 2, y: selected.y + selected.height / 2 }); ctx.fillStyle = '#000'; ctx.fillRect(p.x - 4, p.y - 4, 8, 8); ctx.fillStyle = '#fff16c'; ctx.fillRect(p.x - 3, p.y - 3, 6, 6); }
  }
  private edge(path: Path2D, vertical: boolean, coordinate: number, start: number, end: number, spans: Span[], thickness: number): void {
    const cuts = spans.filter(s => s.vertical === vertical && s.coordinate === coordinate && s.end > start && s.start < end).sort((a, b) => a.start - b.start);
    let cursor = start; const low = this.halfStroke(thickness), high = thickness - low;
    for (const cut of cuts) { const a = Math.max(start, cut.start); if (a > cursor) this.stroke(path, vertical, coordinate, cursor - low, a + high, thickness); cursor = Math.max(cursor, cut.end); }
    if (cursor < end) this.stroke(path, vertical, coordinate, cursor - low, end + high, thickness);
  }
  private stroke(path: Path2D, vertical: boolean, coordinate: number, start: number, end: number, thickness: number): void {
    if (end <= start || thickness <= 0) return;
    // Align the leading edge in both orientations while retaining fractional coverage.
    // Rounding the width or forcing a minimum pixel would make zoomed-out walls too heavy.
    const axis = this.snap(coordinate) - this.halfStroke(thickness);
    if (vertical) path.rect(axis, start, thickness, end - start);
    else path.rect(start, axis, end - start, thickness);
  }
  dispose(): void { this.cancelInteraction(); this.resetWheel(); this.abort.abort(); this.resize.disconnect(); cancelAnimationFrame(this.raf); }
}
