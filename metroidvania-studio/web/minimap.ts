import type { State, Room, Rect, Point, Connection } from './types.js';
import { roomOpacity } from './types.js';
import { defaultMinimapColor, minimapColorProperty, minimapRegionColorProperty, minimapWorldEdge, readMinimapShape } from './minimap-shape.js';
import { chipUnit, previewConnections, roomGrid, type MinimapEditTools } from './minimap-drawing.js';
import type { MinimapShape } from './minimap-shape.js';

// Style values are measured at five screen pixels per tile, then zoom with the map.
const STYLE_SCALE = 5, MIN_SCALE = .00000001, MAX_SCALE = 80;
const GENERATION_DURATION_MS = 1000;
const MAX_PASSAGE_GAP = 4; // Map tiles; long shared boundaries retain a compact central opening.
const regionColorKeys = [minimapRegionColorProperty, minimapColorProperty, 'color'];

interface Span { vertical: boolean; coordinate: number; start: number; end: number; connection: Connection }
export function roomColor(room: Room): string {
  for (const key of regionColorKeys) {
    const color = room.properties.find(p => p.key === key)?.value;
    if (color && /^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(color)) return color;
  }
  return defaultMinimapColor;
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
  private readonly edit?: MinimapEditTools;
  private grid = roomGrid([]);
  private hover: Point | null = null;
  private readonly previewId = 'preview-' + crypto.randomUUID();
  private moving: { pointerId: number; room: Room; start: Point; state: State } | null = null;
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
  private generationStart: number | null = null;
  private drag: { id: number; point: Point; center: Point } | null = null;
  private wheelDelta = 0;
  private wheelDirection = 0;
  private wheelReset = 0;
  private select: (id: string) => void;
  private shapes = new WeakMap<Room, MinimapShape | null>();
  constructor(canvas: HTMLCanvasElement, select: (id: string) => void, open: (id: string) => void = () => {}, edit?: MinimapEditTools) {
    this.canvas = canvas; this.select = select; this.edit = edit;
    const ctx = canvas.getContext('2d', { alpha: false }); if (!ctx) throw new Error('Canvas2D unavailable'); this.ctx = ctx;
    const options = { signal: this.abort.signal };
    canvas.tabIndex = 0;
    canvas.addEventListener('contextmenu', e => e.preventDefault(), options);
    canvas.addEventListener('pointerdown', e => {
      if (!this.active || this.generationStart !== null || this.interacting || ![0, 1, 2].includes(e.button)) return;
      e.preventDefault(); canvas.focus();
      const point = this.point(e);
      if (e.button === 1 || (e.button === 0 && e.altKey)) {
        this.drag = { id: e.pointerId, point, center: { ...this.center } };
        canvas.setPointerCapture(e.pointerId);
      } else if (e.button === 2 && edit && this.state) {
        this.hover = null;
        const room = this.roomAt(this.world(point));
        if (room && !room.locked) edit.remove(room.id, this.state);
      } else if (e.button === 0 && this.state) {
        this.hover = point;
        const world = this.world(point), room = this.roomAt(world);
        if (room && edit && !room.locked) {
          this.moving = { pointerId: e.pointerId, room, start: world, state: this.state };
          canvas.setPointerCapture(e.pointerId);
        } else if (room) this.select(room.id);
        else if (edit) {
          const bounds = this.chipBounds();
          if (bounds && this.free(bounds)) edit.place(bounds, this.state);
        }
      }
      if (edit) this.requestDraw();
    }, options);
    canvas.addEventListener('pointermove', e => {
      if (!this.active || this.generationStart !== null || (!edit && !this.drag)) return;
      const p = this.point(e);
      if (this.drag?.id === e.pointerId) {
        e.preventDefault();
        this.center = { x: this.drag.center.x - (p.x - this.drag.point.x) / this.scale, y: this.drag.center.y + (p.y - this.drag.point.y) / this.scale };
      } else if (!edit || (this.moving && this.moving.pointerId !== e.pointerId)) return;
      this.hover = p; this.requestDraw();
    }, options);
    canvas.addEventListener('pointerleave', () => { if (edit && !this.interacting) { this.hover = null; this.requestDraw(); } }, options);
    canvas.addEventListener('pointerup', e => {
      const move = this.moving;
      if (move?.pointerId === e.pointerId && edit) {
        this.hover = this.point(e);
        const bounds = this.chipBounds();
        if (bounds && (bounds.x !== move.room.x || bounds.y !== move.room.y)) {
          if (this.free(bounds, move.room.id)) edit.move(move.room.id, bounds.x - move.room.x, bounds.y - move.room.y, move.state);
        } else this.select(move.room.id);
      }
      if (move?.pointerId === e.pointerId || this.drag?.id === e.pointerId) this.cancelInteraction();
    }, options);
    const cancel = (e: PointerEvent) => {
      if (e.pointerId === this.drag?.id || e.pointerId === this.moving?.pointerId) this.cancelInteraction();
    };
    canvas.addEventListener('pointercancel', cancel, options);
    canvas.addEventListener('lostpointercapture', cancel, options);
    window.addEventListener('blur', () => this.cancelInteraction(), options);
    canvas.addEventListener('dblclick', e => {
      if (!this.active || this.generationStart !== null || this.drag || e.button !== 0 || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
      const room = this.roomAt(this.world(this.point(e))); if (!room) return;
      e.preventDefault(); open(room.id);
    }, options);
    canvas.addEventListener('wheel', e => this.wheel(e), { ...options, passive: false });
    this.resize = new ResizeObserver(() => this.requestDraw()); this.resize.observe(canvas);
  }
  get interacting(): boolean { return this.drag !== null || this.moving !== null; }
  setActive(active: boolean): void {
    if (active === this.active) return;
    this.active = active;
    if (!active) { this.generationStart = null; this.cancelInteraction(); this.resetWheel(); cancelAnimationFrame(this.raf); this.raf = 0; return; }
    if (this.edit && this.state) this.grid = roomGrid(this.state.document.rooms);
    if (!this.initialized && this.state?.document.rooms.length) { this.initialized = true; this.fit(); }
    else this.requestDraw();
  }
  cancelInteraction(): void {
    const id = this.drag?.id ?? this.moving?.pointerId;
    this.drag = null; this.moving = null; this.hover = null;
    if (id !== undefined && this.canvas.hasPointerCapture(id)) this.canvas.releasePointerCapture(id);
    this.requestDraw();
  }
  private wheel(event: WheelEvent): void {
    event.preventDefault();
    if (!this.active || this.generationStart !== null || this.interacting || !Number.isFinite(event.deltaY) || !event.deltaY) { if (this.drag) this.resetWheel(); return; }
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
    if (previous?.instanceId !== state.instanceId || previous?.documentRevision !== state.documentRevision) this.generationStart = null;
    this.state = state;
    if (this.edit && this.active && previous?.document.rooms !== state.document.rooms) this.grid = roomGrid(state.document.rooms);
    if (this.active && !this.initialized && state.document.rooms.length) { this.initialized = true; this.fit(); }
    else if (changed) this.requestDraw();
  }
  showGeneration(): void {
    this.cancelInteraction();
    this.fit();
    if (this.active && this.state?.document.rooms.length) this.generationStart = performance.now();
  }
  fit(): void {
    if (!this.active) { this.initialized = false; return; }
    const rooms = this.state?.document.rooms;
    if (!rooms?.length) {
      this.center = { x: 0, y: 0 };
      if (this.edit) { this.initialized = true; this.scale = STYLE_SCALE; }
      this.requestDraw(); return;
    }
    this.initialized = true;
    const bounds = this.canvas.getBoundingClientRect();
    let x = Infinity, y = Infinity, right = -Infinity, top = -Infinity;
    for (const room of rooms) { x = Math.min(x, room.x); y = Math.min(y, room.y); right = Math.max(right, room.x + room.width); top = Math.max(top, room.y + room.height); }
    if (this.edit) { x -= 32; y -= 32; right += 32; top += 32; }
    this.center = { x: (x + right) / 2, y: (y + top) / 2 };
    this.scale = Math.max(MIN_SCALE, Math.min(MAX_SCALE, Math.min(Math.max(1, bounds.width - 90) / Math.max(1, right - x), Math.max(1, bounds.height - 90) / Math.max(1, top - y)))); this.requestDraw();
  }
  frameRoom(): void { const r = this.state?.document.rooms.find(r => r.id === this.state?.selection.roomId); if (r) this.center = { x: r.x + r.width / 2, y: r.y + r.height / 2 }; this.requestDraw(); }
  private point(event: MouseEvent): Point { const r = this.canvas.getBoundingClientRect(); return { x: event.clientX - r.left, y: event.clientY - r.top }; }
  private roomAt(point: Point): Room | undefined {
    const rooms = this.state?.document.rooms; if (!rooms) return undefined;
    for (let index = rooms.length - 1; index >= 0; index--) {
      const room = rooms[index];
      if (point.x < room.x || point.x >= room.x + room.width || point.y < room.y || point.y >= room.y + room.height) continue;
      const shape = this.shape(room);
      if (!shape || shape.rows[Math.floor((point.y - room.y) * shape.height / room.height)]?.[Math.floor((point.x - room.x) * shape.width / room.width)] === '1') return room;
    }
    return undefined;
  }
  private world(p: Point): Point { return { x: this.center.x + (p.x - this.width / 2) / this.scale, y: this.center.y - (p.y - this.height / 2) / this.scale }; }
  private snap(value: number): number { return Math.round(value * this.pixelRatio) / this.pixelRatio; }
  private screen(p: Point): Point { return { x: this.snap(this.width / 2 + (p.x - this.center.x) * this.scale), y: this.snap(this.height / 2 - (p.y - this.center.y) * this.scale) }; }
  private rect(room: Rect): Rect { const a = this.screen({ x: room.x, y: room.y + room.height }), b = this.screen({ x: room.x + room.width, y: room.y }); return { x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y }; }
  private shape(room: Room): MinimapShape | null {
    let shape = this.shapes.get(room);
    if (shape === undefined) { shape = readMinimapShape(room); this.shapes.set(room, shape); }
    return shape;
  }
  private anchor(room: Room): Point {
    const shape = this.shape(room);
    const x = shape ? room.width * shape.anchor.x / shape.width : room.width / 2;
    const y = shape ? room.height * shape.anchor.y / shape.height : room.height / 2;
    return this.screen({ x: room.x + x, y: room.y + y });
  }
  private clipConnection(connection: Connection, rooms: Map<string, Room>): Connection[] {
    let segments = [connection];
    for (const id of [connection.roomAId, connection.roomBId]) {
      const room = rooms.get(id);
      if (!room) continue;
      const shape = this.shape(room);
      if (!shape) continue;
      const origin = connection.vertical ? room.x : room.y;
      const side = connection.coordinate === origin ? 0 : connection.vertical ? shape.width : shape.height;
      const edges = shape.edges.filter(edge => edge.vertical === connection.vertical && edge.coordinate === side)
        .map(edge => minimapWorldEdge(room, shape, edge));
      segments = segments.flatMap(segment => edges.flatMap(edge => {
        const start = Math.max(segment.start, edge.start), end = Math.min(segment.end, edge.end);
        return end > start ? [{ ...segment, start, end }] : [];
      }));
    }
    return segments;
  }
  private wallThickness(room: Room, shape: MinimapShape | null, thickness: number): number {
    return shape ? Math.min(thickness, .4 * this.scale * Math.min(room.width / shape.width, room.height / shape.height)) : thickness;
  }
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
    let visible = this.state.document.rooms, connections = this.state.connections;
    let arriving: Room | undefined;
    if (this.generationStart !== null) {
      const progress = (performance.now() - this.generationStart) / GENERATION_DURATION_MS;
      if (progress >= 1) this.generationStart = null;
      else {
        const position = progress * visible.length, index = Math.floor(position), size = position - index;
        const room = visible[index];
        visible = visible.slice(0, index);
        if (size > 0) arriving = { ...room, x: room.x + room.width * (1 - size) / 2, y: room.y + room.height * (1 - size) / 2,
          width: room.width * size, height: room.height * size };
        this.requestDraw();
      }
    }
    if (this.edit) this.drawGrid(visible);
    const rooms = new Map(visible.map(room => [room.id, room]));
    if (this.generationStart !== null) connections = connections.filter(c => rooms.has(c.roomAId) && rooms.has(c.roomBId));
    if (arriving) rooms.set(arriving.id, arriving);
    const bounds = this.drag || this.generationStart !== null ? null : this.chipBounds();
    const blocked = bounds !== null && !this.free(bounds, this.moving?.room.id);
    this.canvas.classList.toggle('chip-blocked', blocked);
    let selection = this.state.selection.roomId;
    if (this.edit && bounds && !blocked) {
      const original = this.moving?.room;
      const chip: Room = original ? { ...original, ...bounds } : {
        ...bounds, id: this.previewId, name: '', visible: true, locked: false, background: [], foreground: [], objects: [],
        properties: [{ key: minimapRegionColorProperty, value: this.edit.color }]
      };
      // Keep custom room silhouettes and render the prospective doorways on both sides.
      if (original) this.shapes.set(chip, this.shape(original));
      rooms.set(chip.id, chip);
      const existing = original ? connections.filter(c => c.roomAId !== chip.id && c.roomBId !== chip.id) : connections;
      connections = existing.concat(previewConnections(chip, rooms.values()));
      selection = chip.id;
    }
    const hasDrawing = visible.some(room => this.shape(room));
    if (hasDrawing) connections = connections.flatMap(connection => this.clipConnection(connection, rooms));
    const spans = connections.map(connection => this.span(connection)), roomSpans = new Map<string, Span[]>();
    for (const span of spans) for (const id of [span.connection.roomAId, span.connection.roomBId]) {
      if (!roomSpans.has(id)) roomSpans.set(id, []);
      roomSpans.get(id)!.push(span);
    }
    const thickness = this.outlineWidth * this.scale / STYLE_SCALE;
    const walls = new Path2D();
    const opacity = (room: Room) => roomOpacity(room.id, selection);
    for (const room of rooms.values()) {
      const rect = this.rect(room), shape = this.shape(room);
      ctx.globalAlpha = room.visible ? 1 : .4;
      ctx.fillStyle = dimColor(roomColor(room), opacity(room));
      if (!shape) ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
      else {
        ctx.save();
        ctx.translate(rect.x, rect.y + rect.height);
        ctx.scale(rect.width / shape.width, -rect.height / shape.height);
        ctx.fill(shape.fill);
        ctx.restore();
      }
    }
    ctx.globalAlpha = 1;
    for (const room of rooms.values()) {
      const rect = this.rect(room), shares = roomSpans.get(room.id) || [], shape = this.shape(room);
      const path = walls;
      if (shape) {
        const width = this.wallThickness(room, shape, thickness);
        for (const boundary of shape.edges) {
          const edge = minimapWorldEdge(room, shape, boundary);
          const a = this.screen(edge.vertical ? { x: edge.coordinate, y: edge.end } : { x: edge.start, y: edge.coordinate });
          const b = this.screen(edge.vertical ? { x: edge.coordinate, y: edge.start } : { x: edge.end, y: edge.coordinate });
          this.edge(path, edge.vertical, edge.vertical ? a.x : a.y, edge.vertical ? a.y : a.x, edge.vertical ? b.y : b.x, shares, width);
        }
        continue;
      }
      this.edge(path, false, rect.y, rect.x, rect.x + rect.width, shares, thickness);
      this.edge(path, false, rect.y + rect.height, rect.x, rect.x + rect.width, shares, thickness);
      this.edge(path, true, rect.x, rect.y, rect.y + rect.height, shares, thickness);
      this.edge(path, true, rect.x + rect.width, rect.y, rect.y + rect.height, shares, thickness);
    }
    // Shared boundaries are drawn once, with exactly the same centered stroke as the outer walls.
    for (const span of spans) {
      const a = rooms.get(span.connection.roomAId), b = rooms.get(span.connection.roomBId); if (!a || !b) continue;
      const width = hasDrawing ? Math.min(this.wallThickness(a, this.shape(a), thickness), this.wallThickness(b, this.shape(b), thickness)) : thickness;
      const stub = this.stub(span), low = this.halfStroke(width), high = width - low;
      this.stroke(walls, span.vertical, span.coordinate, span.start - low, span.start + stub, width);
      this.stroke(walls, span.vertical, span.coordinate, span.end - stub, span.end + high, width);
    }
    // Selection dims room interiors only. Fill all walls and entrances together so
    // shared edges keep identical white coverage, including at fractional zoom.
    ctx.fillStyle = '#ffffff'; ctx.fill(walls);
    if (this.showNames) { ctx.font = '11px system-ui'; ctx.textAlign = 'center'; for (const room of rooms.values()) { const p = this.anchor(room); ctx.fillStyle = '#000c'; ctx.fillRect(p.x - ctx.measureText(room.name).width / 2 - 4, p.y - 8, ctx.measureText(room.name).width + 8, 16); ctx.fillStyle = dimColor('#ffffff', opacity(room)); ctx.fillText(room.name, p.x, p.y + 4); } }
    ctx.globalAlpha = 1;
    const selected = rooms.get(selection || ''); if (selected && selected !== arriving) { const p = this.anchor(selected); ctx.fillStyle = '#000'; ctx.fillRect(p.x - 4, p.y - 4, 8, 8); ctx.fillStyle = '#fff16c'; ctx.fillRect(p.x - 3, p.y - 3, 6, 6); }
    if (bounds && blocked) {
      const rect = this.rect(bounds);
      ctx.save(); ctx.strokeStyle = '#ff7474'; ctx.fillStyle = 'rgba(224,84,84,.24)'; ctx.lineWidth = 2;
      ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
      ctx.setLineDash([5, 3]); ctx.strokeRect(rect.x, rect.y, rect.width, rect.height); ctx.restore();
    }
  }
  private chipBounds(): Rect | null {
    if (!this.edit || !this.hover) return null;
    const p = this.world(this.hover), grid = this.grid;
    if (this.moving) {
      const { room, start } = this.moving, dx = p.x - start.x, dy = p.y - start.y;
      if (Math.hypot(dx, dy) * this.scale < 3) return room;
      return { x: room.x + Math.round(dx / grid.step) * grid.step, y: room.y + Math.round(dy / grid.step) * grid.step,
        width: room.width, height: room.height };
    }
    if (this.roomAt(p)) return null;
    return { x: grid.x + Math.floor((p.x - grid.x) / grid.step) * grid.step, y: grid.y + Math.floor((p.y - grid.y) / grid.step) * grid.step,
      width: this.edit.chip.width, height: this.edit.chip.height };
  }
  private free(bounds: Rect, exceptId?: string): boolean {
    return !!this.state && bounds.x >= -2147483648 && bounds.y >= -2147483648
      && bounds.x + bounds.width <= 2147483647 && bounds.y + bounds.height <= 2147483647
      && !this.state.document.rooms.some(room => room.id !== exceptId && bounds.x < room.x + room.width &&
        bounds.x + bounds.width > room.x && bounds.y < room.y + room.height && bounds.y + bounds.height > room.y);
  }
  private drawGrid(rooms: Room[]): void {
    const ctx = this.ctx, lower = this.world({ x: 0, y: this.height }), upper = this.world({ x: this.width, y: 0 });
    let visibleStep = this.grid.step;
    while (visibleStep * this.scale * this.pixelRatio < 3) visibleStep *= 2;
    const majorStep = Math.max(chipUnit, visibleStep), line = 1 / this.pixelRatio;
    for (const axis of ['x', 'y'] as const) {
      const origin = this.grid[axis];
      const drawLine = (value: number) => {
        if (axis === 'x') ctx.fillRect(this.screen({ x: value, y: 0 }).x, 0, line, this.height);
        else ctx.fillRect(0, this.screen({ x: 0, y: value }).y, this.width, line);
      };
      const first = origin + Math.ceil((lower[axis] - origin) / visibleStep) * visibleStep;
      for (let value = first; value <= upper[axis]; value += visibleStep) {
        ctx.fillStyle = (value - origin) % majorStep === 0 ? '#323232' : '#242424';
        drawLine(value);
      }
      if (visibleStep === this.grid.step) continue;
      // At distant zoom, retain guides for actual room edges between the visible grid lines.
      const edges = new Set<number>();
      for (const room of rooms) {
        edges.add(room[axis]); edges.add(room[axis] + (axis === 'x' ? room.width : room.height));
      }
      ctx.fillStyle = '#242424';
      for (const edge of edges) {
        if (edge >= lower[axis] && edge <= upper[axis] && (edge - origin) % visibleStep !== 0) drawLine(edge);
      }
    }
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
