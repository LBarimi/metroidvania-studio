import { GameCamera } from './game-camera.js';
import { LiveRoomSizes, TileByteCounter } from './live-room-sizes.js';
import { defaultTile } from './tileset-preview.js';
import { AssetImages } from './asset-images.js';
import { resolveRoomGroupMove } from './room-layout.js';
import { activeRoom, colorCss, tileLayer, MAX_BRUSH_SIZE, roomOpacity } from './types.js';
import type { State, Room, Cell, MapObject, Point, Rect, SpriteRect, Command, CommandExpectation, CameraProfile, Definition, Color } from './types.js';

type TileCellKey = number | string;
interface TileGesture {
  roomId: string;
  layer: number;
  erase: boolean;
  live: boolean;
  material: string;
  shape: number;
  groupId: string;
  brushSize: number;
  tool: number;
  baseRevision: number;
  baseInstanceId: string;
  baseDocumentRevision: number;
  baseDocument: State['document'];
  points: Point[];
  rasterLength: number;
  cells: Map<TileCellKey, Cell | null>;
  overflow: boolean;
  size: { counter: TileByteCounter; version: number; bytes: number; count: number; baseCount: number; properties: number; applied?: boolean };
}
interface ObjectGesture {
  roomId: string;
  erase: boolean;
  baseRevision: number;
  baseInstanceId: string;
  points: Point[];
  hidden: Set<string>;
  definition: string;
  layer: number;
  groupId: string;
  tool: number;
  rebaseAfterPending: boolean;
  overflow: boolean;
}
interface Gesture { kind: 'game-camera' | 'selection' | 'pan' | 'paint' | 'room-menu' | 'room-create' | 'room-move' | 'room-resize' | 'object-move' | 'node-move'; pointer: number; start: Point; last: Point; screen: Point; center: Point; room: Room; rooms?: Room[]; area?: Rect; handle?: Point; terrainBounds?: Rect | null; node?: { id: string; index: number }; tile?: TileGesture; rawTileTime?: number; object?: ObjectGesture; expectation: CommandExpectation; tail: Promise<unknown>; failed: boolean }
interface ViewSnapshot { center: Point; pixelScale: number; overview: boolean }
interface LayerIndex {
  rows: Map<number, Map<number, Cell>>;
  positions: Map<number, Map<number, number>>;
  chunks: Map<string, Cell[]>;
  chunkPositions: Map<number, Map<number, number>>;
  cells: Cell[];
  lod: Map<string, HTMLCanvasElement | null>;
}
interface ObjectRenderEntry { object: MapObject; left: number; bottom: number; right: number; top: number; order: number }
interface ObjectLayerIndex { entries: ObjectRenderEntry[]; chunks: Map<string, ObjectRenderEntry[]>; spanning: ObjectRenderEntry[] }
interface ObjectPathIndex { nodes: Map<string, number[]>; segments: Map<string, number[]>; spanningSegments: number[] }
interface MaterialRenderInfo {
  color: string;
  rgba: [number, number, number, number];
  themeId: string;
  themeColor: string;
  solidMasks: Map<number, SpriteRect>;
  strictShapes: Map<number, SpriteRect>;
  fallbackShapes: Map<number, SpriteRect>;
  nativeShapes: Set<number>;
}
interface OverlayIndex {
  source: TileGesture | null;
  size: number;
  rows: Map<number, Map<number, Cell | null>>;
  chunks: Map<string, Point[]>;
  affectedChunks: Set<string>;
}
interface ExactTileChunk { canvas: HTMLCanvasElement; index: LayerIndex; dirty: boolean; frame: number }
interface OverlayLod {
  source: TileGesture;
  base: LayerIndex;
  chunks: Map<string, HTMLCanvasElement | null>;
}
type RevisionedState = State & { documentRevision?: number; catalogRevision?: number };
const OFFSETS: Point[] = [{ x: 0, y: 1 }, { x: 1, y: 1 }, { x: 1, y: 0 }, { x: 1, y: -1 }, { x: 0, y: -1 }, { x: -1, y: -1 }, { x: -1, y: 0 }, { x: -1, y: 1 }];
const MAX_TILE_GESTURE_POINTS = 16384;
// Local map cells are capped at 1024 per axis. A wider stride also keeps the
// maximum-work browser fixture collision-free while avoiding a short-lived
// string allocation for every preview cell on large brush segments.
const TILE_CELL_KEY_STRIDE = 65536;
const TILE_CHUNK_SIZE = 32;
const LOD_PHYSICAL_TILE_SIZE = 8;
const MAX_EXACT_VISIBLE_CELLS = 32768;
const MIN_CACHED_LOD_CHUNK_CELLS = 64;
const MAX_EXACT_TILE_CHUNKS = 64; // 64 MiB of 512x512 RGBA tiles, shared across rooms/layers.
const MIN_PIXEL_SCALE = .000001;
const MAX_OBJECT_CHUNKS = 256;
const MAX_PATH_SEGMENT_CHUNKS = 32;
// Must match EditorWorkspace.MaximumTileGestureWork. The server rejects larger
// brush-expanded paths before it starts a transaction.
const MAX_TILE_GESTURE_WORK = 65536;
const MAX_OBJECT_GESTURE_WORK = 262144;
const SHAPE_POINTS: Record<number, number[][]> = {
  1: [[0, 1], [1, 1], [0, 0]], 2: [[0, 1], [1, 1], [1, 0]],
  3: [[0, 1], [1, 0], [0, 0]], 4: [[1, 1], [1, 0], [0, 0]],
};
export function normalizeMask(mask: number): number { if ((mask & 5) !== 5) mask &= ~2; if ((mask & 20) !== 20) mask &= ~8; if ((mask & 80) !== 80) mask &= ~32; if ((mask & 65) !== 65) mask &= ~128; return mask; }
function connects(shape: number, x: number, y: number): boolean { return shape === 0 || shape === 1 && (x < 0 || y < 0) || shape === 2 && (x > 0 || y < 0) || shape === 3 && (x < 0 || y > 0) || shape === 4 && (x > 0 || y > 0); }
function contains(rect: Rect, point: Point): boolean { return point.x >= rect.x && point.x < rect.x + rect.width && point.y >= rect.y && point.y < rect.y + rect.height; }
function box(a: Point, b: Point): Rect { const x = Math.min(Math.floor(a.x), Math.floor(b.x)), y = Math.min(Math.floor(a.y), Math.floor(b.y)); return { x, y, width: Math.max(Math.floor(a.x), Math.floor(b.x)) - x + 1, height: Math.max(Math.floor(a.y), Math.floor(b.y)) - y + 1 }; }
function cell(point: Point): Point { return { x: Math.floor(point.x), y: Math.floor(point.y) }; }
function definitionKey(value: string): string { return value.toLowerCase(); }
function chunkKey(x: number, y: number): string { return `${Math.floor(x / TILE_CHUNK_SIZE)},${Math.floor(y / TILE_CHUNK_SIZE)}`; }
function overlaps(a: Rect, b: Rect): boolean { return a.x < b.x + b.width && a.x + a.width > b.x && a.y < b.y + b.height && a.y + a.height > b.y; }
function segmentIntersectsRect(from: Point, to: Point, rect: Rect): boolean {
  const right = rect.x + rect.width, top = rect.y + rect.height;
  if (from.x >= rect.x && from.x <= right && from.y >= rect.y && from.y <= top
    || to.x >= rect.x && to.x <= right && to.y >= rect.y && to.y <= top) return true;
  const dx = to.x - from.x, dy = to.y - from.y; let near = 0, far = 1;
  for (const [p, q] of [[-dx, from.x - rect.x], [dx, right - from.x], [-dy, from.y - rect.y], [dy, top - from.y]]) {
    if (p === 0) { if (q < 0) return false; continue; }
    const ratio = q / p;
    if (p < 0) { if (ratio > far) return false; near = Math.max(near, ratio); }
    else { if (ratio < near) return false; far = Math.min(far, ratio); }
  }
  return near <= far;
}
function rgba(value: Color | undefined, fallback = '#cb5574'): [number, number, number, number] {
  const css = colorCss(value, fallback).trim();
  const hex = /^#([0-9a-f]{3,8})$/i.exec(css)?.[1];
  if (hex) {
    const expanded = hex.length <= 4 ? [...hex].map(c => c + c).join('') : hex;
    if (expanded.length === 6 || expanded.length === 8) return [
      parseInt(expanded.slice(0, 2), 16), parseInt(expanded.slice(2, 4), 16), parseInt(expanded.slice(4, 6), 16),
      expanded.length === 8 ? parseInt(expanded.slice(6, 8), 16) : 255,
    ];
  }
  const rgb = /^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)(?:\s*,\s*([\d.]+))?\s*\)$/i.exec(css);
  if (rgb) return [Number(rgb[1]), Number(rgb[2]), Number(rgb[3]), Math.round((rgb[4] === undefined ? 1 : Number(rgb[4])) * 255)];
  return fallback === '#cb5574' ? [203, 85, 116, 255] : rgba(fallback);
}
function tileCellKey(x: number, y: number): number { return x * TILE_CELL_KEY_STRIDE + y; }
export class MapCanvas {
  state: State | null = null;
  center: Point = { x: 20, y: 12 };
  pixelScale = 2;
  overview = false;
  cameraPreview = false;
  gameCameraTool = false;
  alwaysShowGameCamera = localStorage.getItem('metroidvaniaStudio.alwaysShowGameCamera') === 'true';
  private get gameCameraVisible(): boolean { return this.gameCameraTool || this.alwaysShowGameCamera; }
  readonly gameCamera = new GameCamera();
  onPreviewChange: ((damage?: Rect) => void) | null = null;
  private previewViews = new WeakMap<HTMLCanvasElement, { stamp: string; lod: Map<string, boolean> }>();
  private previewRevision = 0;
  private previewTileRevision = 0;
  get previewVersion(): string { return this.previewRevision + ':' + this.gameCamera.revision + ':' + (this.tileOverlay?.size.version ?? -1) + ':' + this.previewTileRevision; }
  showGrid = true;
  showNames = true;
  snapRooms = true;
  crop = false;
  hover: Point = { x: 0, y: 0 };
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private command: Command;
  private onHover: (point: Point) => void;
  private onInspect: () => void;
  private onError: (key: string) => void;
  private writerPending: () => boolean;
  private refreshState: () => Promise<void>;
  private onBrushSize: (size: number) => void;
  private onRoomContextMenu: (world: Point, client: Point) => void;
  private gesture: Gesture | null = null;
  private images = new AssetImages();
  private imageChanged = () => { this.clearExactTileChunks(); this.previewRevision++; this.requestDraw(); };
  private occupancy = new Map<string, LayerIndex>();
  private objectLayers = new Map<string, Map<number, ObjectLayerIndex>>();
  private objectById = new Map<string, { roomId: string; entry: ObjectRenderEntry }>();
  private roomById = new Map<string, Room>();
  private objectPaths = new WeakMap<MapObject, ObjectPathIndex>();
  private groupById = new Map<string, State['document']['layerGroups'][number]>();
  private groupVisibility = new Map<string, boolean>();
  private materials = new Map<string, MaterialRenderInfo>();
  private definitions = new Map<string, Definition>();
  private definitionColors = new Map<string, string>();
  private documentToken: string | number | null = null;
  private catalogToken: string | number | null = null;
  private indexedDocument: State['document'] | null = null;
  private overlayIndex: OverlayIndex = { source: null, size: 0, rows: new Map(), chunks: new Map(), affectedChunks: new Set() };
  private exactTileChunks = new Map<string, ExactTileChunk>();
  private renderFrame = 0;
  private selectionRenderToken = '';
  private selectionBackdrop: HTMLCanvasElement | null = null;
  private overlayLod: OverlayLod | null = null;
  private renderOrigin: Point | null = null;
  private selectedObjects = new Set<string>();
  private active = true;
  private raf = 0;
  private resize: ResizeObserver;
  private abort = new AbortController();
  private width = 1;
  private height = 1;
  private dpr = 1;
  private initialized = false;
  private cameraScaleAuto = false;
  private cameraProfileToken = '';
  private editView: ViewSnapshot | null = null;
  private gestureTail: Promise<unknown> = Promise.resolve();
  private tileOverlay: TileGesture | null = null;
  private liveSizes = new LiveRoomSizes();
  get roomSizes() {
    const tile = this.tileOverlay;
    return this.state ? this.liveSizes.read(this.state, tile?.live && !tile.size.applied ? { version: tile.size.version,
      roomId: tile.roomId, bytes: this.tileSizeDelta(tile) } : undefined) : null;
  }
  private tileSizeDelta(tile: TileGesture): number {
    const size = tile.size;
    return size.bytes + Math.max(0, size.baseCount + size.count - 1) - Math.max(0, size.baseCount - 1)
      + (!tile.erase && tile.cells.size ? size.properties : 0);
  }
  private objectOverlay: ObjectGesture | null = null;
  private tileCommitPending = false;
  private tileLodModes = new Map<string, boolean>();
  private objectOutlinePadding = 2;
  private objectCommitsPending = 0;
  private gestureCommandsPending = 0;
  private tileIdle: Promise<void> = Promise.resolve();
  private resolveTileIdle: (() => void) | null = null;
  private wheelDelta = 0;
  private wheelDirection = 0;
  private wheelReset = 0;
  private wheelBrush = false;
  private brushSizeTarget: number | null = null;
  private brushSizeTask: Promise<void> | null = null;
  private roomSelectionId: string | null = null;
  private selectedRooms = new Set<string>();
  constructor(canvas: HTMLCanvasElement, command: Command, onHover: (point: Point) => void, onInspect: () => void,
    onError: (key: string) => void = () => undefined, writerPending: () => boolean = () => false,
    refreshState: () => Promise<void> = async () => undefined, onBrushSize: (size: number) => void = () => undefined,
    onRoomContextMenu: (world: Point, client: Point) => void = () => undefined) {
    this.canvas = canvas; this.command = command; this.onHover = onHover; this.onInspect = onInspect;
    this.onError = onError; this.writerPending = writerPending; this.refreshState = refreshState; this.onBrushSize = onBrushSize; this.onRoomContextMenu = onRoomContextMenu;
    // Let supporting browsers present brush strokes without waiting for DOM
    // compositing. Unsupported browsers retain the ordinary Canvas2D path.
    const ctx = canvas.getContext('2d', { alpha: false, desynchronized: true }); if (!ctx) throw new Error('Canvas2D unavailable'); this.ctx = ctx;
    canvas.tabIndex = 0;
    const options = { signal: this.abort.signal };
    canvas.addEventListener('contextmenu', e => e.preventDefault(), options);
    canvas.addEventListener('pointerdown', e => this.down(e), options);
    canvas.addEventListener('pointermove', e => this.move(e), options);
    canvas.addEventListener('pointerrawupdate', e => {
      if (this.gesture?.tile?.live && this.gesture.pointer === (e as PointerEvent).pointerId) this.move(e as PointerEvent);
    }, options);
    canvas.addEventListener('pointerup', e => this.up(e), options);
    canvas.addEventListener('pointercancel', e => this.cancel(e.pointerId), options);
    canvas.addEventListener('lostpointercapture', e => this.cancel(e.pointerId), options);
    canvas.addEventListener('wheel', e => this.wheel(e), { ...options, passive: false });
    window.addEventListener('blur', () => this.cancel(), options);
    this.resize = new ResizeObserver(() => this.requestDraw()); this.resize.observe(canvas);
  }
  get scale(): number { return 16 * this.pixelScale / this.dpr; }
  setGameCameraTool(enabled: boolean): void {
    if (this.gameCameraTool === enabled) return;
    this.cancel(); this.roomSelectionId = null; this.gameCameraTool = enabled;
    this.canvas.style.cursor = ''; this.requestDraw();
  }
  setAlwaysShowGameCamera(enabled: boolean): void {
    this.alwaysShowGameCamera = enabled;
    localStorage.setItem('metroidvaniaStudio.alwaysShowGameCamera', String(enabled)); this.requestDraw();
  }
  moveGameCamera(center: Point): void { if (this.gameCamera.move(center)) this.gameCameraVisible ? this.requestDraw() : this.onPreviewChange?.(); }
  setGameCameraPixelPerfect(enabled: boolean): void {
    if (!this.gameCamera.setPixelPerfect(enabled)) return;
    if (this.gesture?.kind === 'game-camera') this.cancel();
    this.gameCameraVisible ? this.requestDraw() : this.onPreviewChange?.();
  }
  centerGameCamera(): void { if (this.gameCamera.recenter()) this.gameCameraVisible ? this.requestDraw() : this.onPreviewChange?.(); }
  get interacting(): boolean { return this.gesture !== null; }
  get hasPendingWork(): boolean { return this.gesture !== null || this.gestureCommandsPending > 0 || this.brushSizeTask !== null; }
  get brushSize(): number { return this.brushSizeTarget ?? this.state?.selection.brushSize ?? 1; }
  get roomDeleteTarget(): string | null { if (this.selectedRooms.size > 1) return this.state?.selection.roomId || null; return this.roomSelectionId === this.state?.selection.roomId ? this.roomSelectionId : null; }
  selectRoomTarget(id: string | null): void { this.roomSelectionId = id; this.requestDraw(); }
  setActive(active: boolean): void {
    if (this.active === active) return;
    this.active = active;
    if (!active) { this.cancel(); this.resetWheel(); cancelAnimationFrame(this.raf); this.raf = 0; }
    else { if (this.state) this.syncIndexes(this.state); this.requestDraw(); }
  }
  async settled(): Promise<void> { await this.tileIdle; await this.gestureTail; if (this.brushSizeTask) await this.brushSizeTask; }
  get cameraProfile(): CameraProfile | undefined { return this.state?.camera || this.state?.catalog.camera; }
  get viewportSize(): Point {
    if (this.cameraPreview) {
      const camera = this.cameraProfile;
      if (camera) { const y = camera.orthographicSize * 2 * camera.ppu / 16; return { x: y * camera.referenceWidth / camera.referenceHeight, y }; }
    }
    return { x: this.width / this.scale, y: this.height / this.scale };
  }
  setState(state: State): void {
    const previousInstance = this.state?.instanceId;
    const previousRoom = this.state?.selection.roomId;
    if (previousInstance !== state.instanceId || previousRoom !== state.selection.roomId
      || this.state?.selection.tool !== state.selection.tool || this.state?.selection.layer !== state.selection.layer) this.roomSelectionId = null;
    const camera = state.camera || state.catalog.camera;
    const nextCameraToken = camera ? [camera.ppu, camera.referenceWidth, camera.referenceHeight, camera.orthographicSize].join(':') : '';
    const cameraChanged = nextCameraToken !== this.cameraProfileToken;
    const selectionToken = this.state?.selection === state.selection ? this.selectionRenderToken : JSON.stringify(state.selection);
    const redraw = cameraChanged || selectionToken !== this.selectionRenderToken || this.indexedDocument !== state.document
      || this.documentToken !== `${state.instanceId}:${state.documentRevision ?? state.revision}`
      || this.catalogToken !== `${state.instanceId}:${state.catalogRevision ?? state.revision}`;
    if (redraw) this.selectionBackdrop = null;
    this.selectionRenderToken = selectionToken;
    this.cameraProfileToken = nextCameraToken;
    if (this.gesture && previousInstance && previousInstance !== state.instanceId) this.cancel();
    if (previousInstance && previousInstance !== state.instanceId) this.brushSizeTarget = null;
    this.state = state;
    if (this.gameCamera.sync(activeRoom(state), camera, state.instanceId) && this.gesture?.kind === 'game-camera') this.cancel();
    this.selectedRooms = new Set(state.selection.roomIds || []);
    if (this.cameraPreview && this.cameraScaleAuto && cameraChanged) this.pixelScale = this.fittedCameraScale(camera);
    if (this.active) this.syncIndexes(state);
    if (!this.initialized && state.document.rooms.length) { this.frameRoom(); this.initialized = true; }
    if (this.gesture && previousRoom !== state.selection.roomId && this.gesture.room.id !== state.selection.roomId && this.gesture.kind !== 'room-create') this.cancel();
    if (redraw) { this.previewRevision++; this.requestDraw(); } if (cameraChanged) this.onHover(this.hover);
  }
  private syncIndexes(state: State): void {
    const revisioned = state as RevisionedState;
    const nextCatalogToken = `${state.instanceId}:${revisioned.catalogRevision ?? state.revision}`;
    if (this.catalogToken !== nextCatalogToken) { this.catalogToken = nextCatalogToken; this.rebuildCatalog(state); }
    const nextDocumentToken = `${state.instanceId}:${revisioned.documentRevision ?? state.revision}`;
    if (this.documentToken !== nextDocumentToken || this.indexedDocument !== state.document) {
      // A compact response advances the revision while deliberately retaining the
      // client's document object. Keep the existing indexes until its overlay is
      // merged below; rebuilding them here would scan the whole map per stroke.
      if (state.document === this.indexedDocument && this.tileCommitPending && this.tileOverlay) return;
      this.documentToken = nextDocumentToken; this.rebuildDocument(state);
      if (this.objectOverlay?.erase) this.refreshObjectErasePreview(state, this.objectOverlay);
    }
  }
  private rebuildCatalog(state: State): void {
    this.clearExactTileChunks();
    this.materials.clear(); this.definitions.clear(); this.definitionColors.clear();
    this.images.setCatalog(state.instanceId, state.catalogRevision, [
      ...state.catalog.materials.flatMap(material => material.sprites.map(sprite => sprite.asset)),
      ...state.catalog.objects.flatMap(definition => definition.sprite ? [definition.sprite.asset] : [])]);
    for (const material of state.catalog.materials) {
      const solidMasks = new Map<number, SpriteRect>(), strictShapes = new Map<number, SpriteRect>(), fallbackShapes = new Map<number, SpriteRect>();
      for (const sprite of material.sprites) {
        const fallbackShape = sprite.shape || 0;
        if (!fallbackShapes.has(fallbackShape)) fallbackShapes.set(fallbackShape, sprite);
        if (typeof sprite.shape === 'number' && !strictShapes.has(sprite.shape)) strictShapes.set(sprite.shape, sprite);
        if (sprite.shape === 0 && typeof sprite.mask === 'number' && !solidMasks.has(sprite.mask)) solidMasks.set(sprite.mask, sprite);
      }
      const components = rgba(material.color), themeId = typeof (material as { themeId?: unknown }).themeId === 'string' ? (material as { themeId: string }).themeId.trim() : '';
      const themeColor = '#' + components.slice(0, 3).map(value => Math.max(0, Math.min(255, Math.round(value))).toString(16).padStart(2, '0')).join('').toUpperCase();
      const nativeShapes = new Set<number>();
      for (const shape of [0, 1, 2, 3, 4]) {
        const sprites = shape === 0 ? [...solidMasks.values(), fallbackShapes.get(0)] : [strictShapes.get(shape) || fallbackShapes.get(shape)];
        if (sprites.length && sprites.every(sprite => sprite && sprite.width === 16 && sprite.height === 16)) nativeShapes.add(shape);
      }
      this.materials.set(material.id, { color: colorCss(material.color, '#cb5574'), rgba: components, themeId, themeColor, solidMasks, strictShapes, fallbackShapes, nativeShapes });
    }
    for (const definition of state.catalog.objects) {
      const key = definitionKey(definition.id); this.definitions.set(key, definition); this.definitionColors.set(key, colorCss(definition.color, '#d4ac61'));
    }
    for (const index of this.occupancy.values()) index.lod.clear();
    this.overlayLod = null;
  }
  private rebuildDocument(state: State): void {
    this.clearExactTileChunks();
    this.occupancy.clear(); this.objectLayers.clear(); this.objectById.clear(); this.roomById.clear(); this.objectPaths = new WeakMap();
    this.groupVisibility.clear(); this.groupById.clear(); this.overlayLod = null;
    this.objectOutlinePadding = 2;
    for (const group of state.document.layerGroups) this.groupById.set(group.id, group);
    for (const room of state.document.rooms) {
      this.roomById.set(room.id, room);
      for (const [layer, cells] of [[0, room.foreground], [1, room.background]] as [number, Cell[]][]) {
        const index: LayerIndex = { rows: new Map(), positions: new Map(), chunks: new Map(), chunkPositions: new Map(), cells, lod: new Map() };
        for (let position = 0; position < cells.length; position++) {
          const tile = cells[position];
          let row = index.rows.get(tile.y); if (!row) { row = new Map(); index.rows.set(tile.y, row); } row.set(tile.x, tile);
          let positions = index.positions.get(tile.y); if (!positions) { positions = new Map(); index.positions.set(tile.y, positions); } positions.set(tile.x, position);
          const key = chunkKey(tile.x, tile.y); let chunk = index.chunks.get(key); if (!chunk) { chunk = []; index.chunks.set(key, chunk); }
          let chunkPositions = index.chunkPositions.get(tile.y); if (!chunkPositions) { chunkPositions = new Map(); index.chunkPositions.set(tile.y, chunkPositions); }
          chunkPositions.set(tile.x, chunk.length); chunk.push(tile);
        }
        this.occupancy.set(room.id + ':' + layer, index);
      }
      const layers = new Map<number, ObjectLayerIndex>();
      for (let order = 0; order < room.objects.length; order++) {
        const object = room.objects[order];
        this.objectOutlinePadding = Math.max(this.objectOutlinePadding, 2 + 3 * Math.max(Math.abs(object.scaleX ?? 1), Math.abs(object.scaleY ?? 1)));
        const radians = (object.rotation || 0) * Math.PI / 180, cosine = Math.abs(Math.cos(radians)), sine = Math.abs(Math.sin(radians));
        const halfWidth = Math.abs(object.width * (object.scaleX ?? 1)) / 2, halfHeight = Math.abs(object.height * (object.scaleY ?? 1)) / 2;
        const radiusX = cosine * halfWidth + sine * halfHeight, radiusY = sine * halfWidth + cosine * halfHeight;
        const centerX = object.x + object.width / 2, centerY = object.y + object.height / 2;
        const entry = { object, left: centerX - radiusX, bottom: centerY - radiusY, right: centerX + radiusX, top: centerY + radiusY, order };
        let layerIndex = layers.get(object.layer);
        if (!layerIndex) { layerIndex = { entries: [], chunks: new Map(), spanning: [] }; layers.set(object.layer, layerIndex); }
        layerIndex.entries.push(entry); this.objectById.set(object.id, { roomId: room.id, entry });
        const left = Math.floor(entry.left / TILE_CHUNK_SIZE), right = Math.floor(entry.right / TILE_CHUNK_SIZE);
        const bottom = Math.floor(entry.bottom / TILE_CHUNK_SIZE), top = Math.floor(entry.top / TILE_CHUNK_SIZE);
        if ((right - left + 1) * (top - bottom + 1) > MAX_OBJECT_CHUNKS) layerIndex.spanning.push(entry);
        else for (let chunkY = bottom; chunkY <= top; chunkY++) for (let chunkX = left; chunkX <= right; chunkX++) {
          const key = `${chunkX},${chunkY}`; let chunk = layerIndex.chunks.get(key);
          if (!chunk) { chunk = []; layerIndex.chunks.set(key, chunk); } chunk.push(entry);
        }
      }
      this.objectLayers.set(room.id, layers);
    }
    this.indexedDocument = state.document;
  }
  frameRoom(fit = false): void {
    const room = activeRoom(this.state); if (!room) return;
    this.center = { x: room.x + room.width / 2, y: room.y + room.height / 2 };
    if (fit) {
      const bounds = this.canvas.getBoundingClientRect(), dpr = window.devicePixelRatio || 1;
      const scale = Math.max(MIN_PIXEL_SCALE, Math.min(4,
        Math.max(1, bounds.width - 80) * dpr / (Math.max(1, room.width) * 16),
        Math.max(1, bounds.height - 80) * dpr / (Math.max(1, room.height) * 16)));
      this.pixelScale = scale >= 1 ? Math.floor(scale) : 1 / Math.ceil(1 / scale);
      this.overview = false; this.onHover(this.hover);
    }
    this.requestDraw();
  }
  fit(): void {
    const rooms = this.state?.document.rooms.filter(room => room.visible); if (!rooms?.length) return;
    this.leaveCameraPreview(false);
    let left = Infinity, bottom = Infinity, right = -Infinity, top = -Infinity;
    for (const room of rooms) { left = Math.min(left, room.x); bottom = Math.min(bottom, room.y); right = Math.max(right, room.x + room.width); top = Math.max(top, room.y + room.height); }
    this.center = { x: (left + right) / 2, y: (bottom + top) / 2 };
    this.overview = true;
    this.pixelScale = Math.max(.000001, Math.min(32, Math.min((this.width - 50) * this.dpr / ((right - left) * 16), (this.height - 50) * this.dpr / ((top - bottom) * 16)))); this.requestDraw(); this.onHover(this.hover);
  }
  gameView(enabled = !this.cameraPreview): void {
    const camera = this.cameraProfile; if (!camera || enabled === this.cameraPreview) return;
    if (!enabled) { this.leaveCameraPreview(true); this.requestDraw(); this.onHover(this.hover); return; }
    this.editView = { center: { ...this.center }, pixelScale: this.pixelScale, overview: this.overview };
    this.cameraPreview = true; this.cameraScaleAuto = true; this.overview = false;
    const room = activeRoom(this.state); if (room) this.center = { x: room.x + room.width / 2, y: room.y + room.height / 2 };
    this.pixelScale = this.fittedCameraScale(camera); this.requestDraw(); this.onHover(this.hover);
  }
  zoom(point: Point, direction: number): void {
    if (!direction) return;
    if (this.cameraPreview) {
      this.pixelScale = Math.max(1, Math.min(32, this.pixelScale + direction)); this.cameraScaleAuto = false;
      this.requestDraw(); this.onHover(this.hover); return;
    }
    const world = this.toWorld(point);
    this.pixelScale = this.nextPixelScale(direction);
    this.overview = false;
    const moved = this.toWorld(point); this.center.x += world.x - moved.x; this.center.y += world.y - moved.y;
    this.requestDraw(); this.onHover(this.hover);
  }
  private wheel(event: WheelEvent): void {
    event.preventDefault();
    if (!this.active || this.gesture || !Number.isFinite(event.deltaY) || !event.deltaY
      || event.ctrlKey && (this.cameraPreview || this.gameCameraTool || !this.state || !tileLayer(this.state.selection.layer))) { this.resetWheel(); return; }
    if (event.ctrlKey !== this.wheelBrush) { this.resetWheel(); this.wheelBrush = event.ctrlKey; }
    const pixels = event.deltaY * (event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16 : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? Math.max(1, this.height) : 1);
    const direction = Math.sign(-pixels); if (!direction) return;
    if (direction !== this.wheelDirection) { this.wheelDelta = 0; this.wheelDirection = direction; }
    clearTimeout(this.wheelReset); this.wheelReset = window.setTimeout(() => this.resetWheel(), 180);
    const amount = Math.abs(pixels); let steps = 0;
    if (amount >= 100) { this.wheelDelta = 0; steps = Math.max(1, Math.round(amount / 120)); }
    else { this.wheelDelta += amount; steps = Math.floor(this.wheelDelta / 40); this.wheelDelta %= 40; }
    if (event.ctrlKey) { if (steps) this.adjustBrushSize(direction * steps); return; }
    const anchor = this.point(event);
    for (let step = 0; step < Math.min(64, steps); step++) this.zoom(anchor, direction);
  }
  private resetWheel(): void { clearTimeout(this.wheelReset); this.wheelReset = 0; this.wheelDelta = 0; this.wheelDirection = 0; }
  private adjustBrushSize(delta: number): void {
    const next = Math.max(1, Math.min(MAX_BRUSH_SIZE, this.brushSize + delta));
    if (next === this.brushSize) return;
    this.brushSizeTarget = next; this.onBrushSize(next); this.requestDraw();
    if (this.brushSizeTask) return;
    // Keep just the latest target while a local request is in flight. Fine or
    // fast wheels must not build a queue of obsolete options commands.
    this.brushSizeTask = Promise.resolve().then(async () => {
      await this.tileIdle; await this.gestureTail;
      while (this.brushSizeTarget !== null && !this.abort.signal.aborted) {
        let sentSize = this.brushSizeTarget;
        await this.command('options', () => {
          sentSize = this.brushSizeTarget ?? this.state?.selection.brushSize ?? 1;
          return { brushSize: sentSize };
        });
        if (this.brushSizeTarget === sentSize) this.brushSizeTarget = null;
      }
    }).catch(() => { this.brushSizeTarget = null; }).finally(() => {
      this.brushSizeTask = null; this.onBrushSize(this.brushSize); this.requestDraw();
    });
  }
  private nextPixelScale(direction: number): number {
    const current = Math.max(MIN_PIXEL_SCALE, Math.min(32, this.pixelScale));
    if (current < 1) {
      const exponent = Math.log2(current);
      const next = direction > 0 ? 2 ** (Math.floor(exponent + 1e-9) + 1) : 2 ** (Math.ceil(exponent - 1e-9) - 1);
      return Math.max(MIN_PIXEL_SCALE, Math.min(32, next));
    }
    if (this.overview && current !== Math.round(current))
      return Math.max(1, Math.min(32, direction > 0 ? Math.floor(current) + 1 : Math.ceil(current) - 1));
    return Math.max(direction < 0 && current <= 1 ? .5 : 1, Math.min(32, current + direction));
  }
  private leaveCameraPreview(restore: boolean): void {
    if (!this.cameraPreview) return;
    this.cameraPreview = false; this.cameraScaleAuto = false;
    if (restore && this.editView) { this.center = { ...this.editView.center }; this.pixelScale = this.editView.pixelScale; this.overview = this.editView.overview; }
    this.editView = null;
  }
  private fittedCameraScale(camera = this.cameraProfile): number {
    if (!camera || camera.referenceWidth <= 0 || camera.referenceHeight <= 0) return 1;
    return Math.max(1, Math.min(32, Math.floor(Math.min(this.width * this.dpr / camera.referenceWidth, this.height * this.dpr / camera.referenceHeight))));
  }
  toScreen(world: Point): Point { const origin = this.origin(); return { x: origin.x + world.x * this.scale, y: origin.y - world.y * this.scale }; }
  toWorld(point: Point): Point { const origin = this.origin(); return { x: (point.x - origin.x) / this.scale, y: -(point.y - origin.y) / this.scale }; }
  private origin(): Point { return this.renderOrigin || this.computeOrigin(); }
  private computeOrigin(): Point {
    const x = this.width / 2 - this.center.x * this.scale, y = this.height / 2 + this.center.y * this.scale;
    if (this.cameraPreview && !this.gameCamera.pixelPerfect) return { x, y };
    return { x: Math.round(x * this.dpr) / this.dpr, y: Math.round(y * this.dpr) / this.dpr };
  }
  private point(e: MouseEvent): Point { const r = this.canvas.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; }
  private roomAt(point: Point): Room | undefined {
    const current = activeRoom(this.state); if (current?.visible && contains(current, point)) return current;
    const rooms = this.state?.document.rooms; if (!rooms) return undefined;
    for (let index = rooms.length - 1; index >= 0; index--) if (rooms[index].visible && contains(rooms[index], point)) return rooms[index];
    return undefined;
  }
  private local(point: Point, room: Room): Point { return { x: point.x - room.x, y: point.y - room.y }; }
  private enqueue(g: Gesture, action: string, values: object | (() => object | null), accepted?: (state: State) => void): void {
    this.gestureCommandsPending++;
    g.tail = g.tail.then(async () => {
      if (g.failed) return;
      if (action === 'objectGesture' && g.object?.rebaseAfterPending) {
        if (!this.state || this.state.instanceId !== g.object.baseInstanceId) { this.onError('writerBusy'); return; }
        g.expectation = { instanceId: g.object.baseInstanceId, revision: this.state.revision };
      }
      const payload = typeof values === 'function' ? values() : values;
      if (payload === null) return;
      const state = await this.command(action, payload, g.expectation);
      g.expectation = { instanceId: state.instanceId, revision: state.revision };
      accepted?.(state);
    }).catch(() => {
      g.failed = true;
      if (this.gesture === g) { this.gesture = null; if (this.canvas.hasPointerCapture(g.pointer)) this.canvas.releasePointerCapture(g.pointer); }
      this.requestDraw();
    }).finally(() => { this.gestureCommandsPending--; });
  }
  private down(e: PointerEvent): void {
    if (!this.state || this.gesture || ![0, 1, 2].includes(e.button)) return;
    const screen = this.point(e), world = this.toWorld(screen), hit = this.roomAt(world);
    const previewPan = e.button === 1 || e.button === 0 && e.altKey || this.gameCameraTool && e.button === 2 && !!hit;
    if (this.cameraPreview && !previewPan) { e.preventDefault(); this.canvas.focus(); return; }
    const selection = this.state.selection;
    // Every authoring gesture captures a revision and document references at
    // pointer-down. Starting one behind another writer would show a full local
    // drag and then discard it as stale; only view panning is safe while busy.
    const busy = this.tileCommitPending || this.objectCommitsPending > 0 || this.brushSizeTask !== null || this.writerPending();
    const blocked = !previewPan && !this.gameCameraTool && busy;
    if (blocked) { e.preventDefault(); this.onError('writerBusy'); return; }
    e.preventDefault(); this.canvas.focus();
    const previousHover = this.hover;
    this.hover = world; this.onHover(world);
    const selectedRoom = activeRoom(this.state), current = selectedRoom?.visible ? selectedRoom : undefined;
    const room = current || hit || { x: 0, y: 0 } as Room;
    const g: Gesture = { kind: 'pan', pointer: e.pointerId, start: world, last: world, screen, center: { ...this.center }, room,
      expectation: { instanceId: this.state.instanceId, revision: this.state.revision }, tail: this.gestureTail, failed: false };
    const handle = current && this.canResizeRoom(current) ? this.roomHandle(screen, current) : null;
    if (previewPan) g.kind = 'pan';
    else if (e.button === 0 && !hit && this.selectedRooms.size > 1) {
      this.enqueue(g, 'cancel', {}, () => { this.roomSelectionId = null; });
      this.gestureTail = g.tail; return;
    }
    else if (e.button === 0 && (e.ctrlKey || e.metaKey)) {
      if (hit) {
        this.enqueue(g, 'selectRoom', { id: hit.id, toggle: true }, state => { this.roomSelectionId = state.selection.roomIds?.length ? state.selection.roomId : null; });
        this.gestureTail = g.tail;
      }
      return;
    } else if (e.button === 2 && !hit) g.kind = 'room-menu';
    else if (e.button === 0 && hit && this.selectedRooms.size > 1 && this.selectedRooms.has(hit.id)) {
      g.kind = 'room-move'; g.room = hit;
    } else if (e.button === 2 && hit && this.selectedRooms.size > 1) {
      this.enqueue(g, 'selectRoom', { id: hit.id }, () => { this.roomSelectionId = hit.id; });
      this.gestureTail = g.tail; return;
    }
    else if (e.button === 0 && handle) {
      g.kind = 'room-resize'; g.room = current!; g.handle = handle; this.roomSelectionId = current!.id;
      g.terrainBounds = this.roomTerrainBounds(current!);
    } else if (e.button === 0 && current && this.canResizeRoom(current) && this.roomMoveHandle(screen, current)) {
      g.kind = 'room-move'; g.room = current; this.roomSelectionId = current.id;
    } else if ((e.button === 0 || e.button === 2) && hit && hit.id !== selection.roomId) {
      // Either button consumes its first click/drag when activating another room.
      // Editing, erasing and picking require a new pointer-down in the active room.
      this.enqueue(g, 'selectRoom', { id: hit.id }, () => { this.roomSelectionId = hit.id; });
      this.gestureTail = g.tail; return;
    } else if (this.gameCameraTool) {
      const frame = this.gameCamera.visibleFrame;
      if (e.button !== 0 || !frame || !contains(frame, world)) return;
      g.kind = 'game-camera'; g.center = this.gameCamera.center; this.canvas.style.cursor = 'grabbing';
    } else if (selection.tool === 0 && e.button === 0) {
      if (hit) { g.kind = 'room-move'; g.room = hit; this.roomSelectionId = hit.id; }
      else g.kind = 'room-create';
    } else if (e.button === 2 && (e.ctrlKey || e.metaKey) && !tileLayer(selection.layer)) {
      this.roomSelectionId = null;
      const target = hit || current; if (!target) return;
      g.room = target;
      if (target.id !== selection.roomId) this.enqueue(g, 'selectRoom', { id: target.id });
      this.enqueue(g, 'objectClick', this.local(world, target), () => this.onInspect()); this.gestureTail = g.tail; return;
    } else if (tileLayer(selection.layer) && selection.tool === 2 && e.button === 0 && current) {
      this.roomSelectionId = null; g.kind = 'selection';
      const local = this.local(world, current), area = selection.area;
      if (area && !e.shiftKey && local.x >= area.x && local.y >= area.y && local.x < area.x + area.width && local.y < area.y + area.height) g.area = { ...area };
    } else if (!tileLayer(selection.layer) && selection.tool === 2 && e.button === 0) {
      this.roomSelectionId = null;
      if (!hit && !current) return;
      g.room = hit || current!; const local = this.local(world, g.room), node = this.nodeAt(screen, g.room);
      if (g.room.id !== selection.roomId) this.enqueue(g, 'selectRoom', { id: g.room.id });
      if (node) { g.kind = 'node-move'; g.node = node; this.enqueue(g, 'nodeSelect', { id: node.id, index: node.index, additive: e.shiftKey }); }
      else { g.kind = 'object-move'; this.enqueue(g, 'objectClick', { ...local, additive: e.shiftKey }); }
    } else {
      if (!hit && !current) return;
      this.roomSelectionId = null;
      g.kind = 'paint';
      // Every local gesture stays in the room under its first sample.
      g.room = hit || current!;
      if (!g.room) return;
      const local = this.local(world, g.room);
      if (e.button === 2 && (e.ctrlKey || e.metaKey) && tileLayer(selection.layer)) {
        if (g.room.id !== selection.roomId) this.enqueue(g, 'selectRoom', { id: g.room.id });
        this.enqueue(g, 'pick', local); this.gestureTail = g.tail; return;
      }
      const erase = e.button === 2 || e.shiftKey && tileLayer(selection.layer) && selection.tool !== 2;
      if (tileLayer(selection.layer)) this.beginTileGesture(g, local, erase, selection);
      else if (erase || selection.tool === 1) this.beginObjectGesture(g, local, erase, selection);
      else return;
    }
    // Room handles author map data even when the camera tool is selected.
    if (busy && (g.kind === 'room-move' || g.kind === 'room-resize')) { this.onError('writerBusy'); return; }
    if (g.kind === 'room-move') {
      g.rooms = this.selectedRooms.size > 1 && this.selectedRooms.has(g.room.id)
        ? this.state.document.rooms.filter(room => this.selectedRooms.has(room.id)) : [g.room];
      if (g.rooms.some(room => room.locked)) { this.onError('roomSelectionLocked'); return; }
    }
    if (g.kind === 'selection') {
      if (this.raf) { cancelAnimationFrame(this.raf); this.raf = 0; } this.draw();
      const backdrop = document.createElement('canvas'); backdrop.width = this.canvas.width; backdrop.height = this.canvas.height;
      backdrop.getContext('2d')!.drawImage(this.canvas, 0, 0); this.selectionBackdrop = backdrop;
    }
    this.gesture = g; this.canvas.setPointerCapture(e.pointerId);
    if (g.tile?.live) this.drawBrushDamage([previousHover, world]); else this.requestDraw();
  }
  private panGesture(g: Gesture, screen: Point, samples: PointerEvent[] = []): void {
    // Once a context click becomes a drag, returning to its origin cannot reopen the menu.
    const moved = (p: Point) => Math.hypot(p.x - g.screen.x, p.y - g.screen.y) > 4;
    if (g.kind === 'room-menu' && (moved(screen) || samples.some(sample => moved(this.point(sample))))) g.kind = 'pan';
    if (g.kind === 'pan') this.center = { x: g.center.x - (screen.x - g.screen.x) / this.scale, y: g.center.y + (screen.y - g.screen.y) / this.scale };
  }
  private move(e: PointerEvent): void {
    const g = this.gesture;
    if (g?.kind === 'game-camera') {
      this.hover = this.toWorld(this.point(e)); this.onHover(this.hover);
      if (g && g.pointer === e.pointerId) {
        e.preventDefault(); this.moveGameCamera({ x: g.center.x + this.hover.x - g.start.x, y: g.center.y + this.hover.y - g.start.y });
      }
      const frame = this.gameCamera.visibleFrame;
      const cursor = g ? 'grabbing' : frame && contains(frame, this.hover) ? 'grab' : '';
      if (this.canvas.style.cursor !== cursor) this.canvas.style.cursor = cursor;
      return;
    }
    const coalesced = typeof e.getCoalescedEvents === 'function' ? e.getCoalescedEvents() : [];
    let samples = coalesced.length ? coalesced : [e];
    // pointermove can repeat the raw samples already painted. Replaying them
    // would walk backwards along the stroke and spend its raster budget twice.
    if (g?.tile?.live && g.pointer === e.pointerId && e.type === 'pointermove' && g.rawTileTime !== undefined)
      samples = samples.filter(sample => sample.timeStamp > g.rawTileTime!);
    if (!samples.length) return;
    const previousHover = this.hover, previousRasterLength = g?.tile?.rasterLength;
    const screen = this.point(samples[samples.length - 1]);
    if (g && g.pointer === e.pointerId && (g.kind === 'pan' || g.kind === 'room-menu')) this.panGesture(g, screen, samples);
    this.hover = this.toWorld(screen); this.onHover(this.hover);
    const damage = [previousHover, this.hover];
    const active = activeRoom(this.state);
    const handle = !this.cameraPreview && !g && active && this.canResizeRoom(active) ? this.roomHandle(screen, active) : null;
    const groupMove = !this.cameraPreview && this.selectedRooms.size > 1 && this.selectedRooms.has(this.roomAt(this.hover)?.id || '');
    const moveHandle = !this.cameraPreview && !g && active && this.canResizeRoom(active) && this.roomMoveHandle(screen, active);
    const frame = this.gameCameraTool && this.gameCamera.visibleFrame;
    const cameraMove = frame && contains(frame, this.hover);
    const cursor = handle ? !handle.x ? 'ns-resize' : !handle.y ? 'ew-resize'
      : handle.x === handle.y ? 'nesw-resize' : 'nwse-resize' : (moveHandle || groupMove || cameraMove) && !g ? 'grab' : g?.kind === 'room-move' || g?.kind === 'pan' ? 'grabbing' : '';
    if (this.canvas.style.cursor !== cursor) this.canvas.style.cursor = cursor;
    if (this.gameCameraTool && !g) return;
    if (g && g.pointer === e.pointerId) {
      e.preventDefault(); g.last = this.hover;
      if (g.kind === 'paint' && g.tile) for (const sample of samples) {
        g.last = this.toWorld(this.point(sample));
        damage.push(g.last);
        this.extendTileGesture(g, this.local(g.last, g.room));
      }
      else if (g.kind === 'paint' && g.object) for (const sample of samples) {
        g.last = this.toWorld(this.point(sample));
        this.extendObjectGesture(g, this.local(g.last, g.room));
      }
    }
    if (g?.tile?.live && g.pointer === e.pointerId) {
      if (e.type === 'pointerrawupdate') g.rawTileTime = samples[samples.length - 1].timeStamp;
      if (previousRasterLength !== g.tile.rasterLength || Math.floor(previousHover.x) !== Math.floor(this.hover.x)
        || Math.floor(previousHover.y) !== Math.floor(this.hover.y)) this.drawBrushDamage(damage);
    } else this.requestDraw();
  }
  private up(e: PointerEvent): void {
    const g = this.gesture; if (!g || g.pointer !== e.pointerId) return;
    e.preventDefault(); const screen = this.point(e);
    if (g.kind === 'pan' || g.kind === 'room-menu') this.panGesture(g, screen);
    if (g.kind === 'game-camera') {
      const world = this.toWorld(screen);
      this.moveGameCamera({ x: g.center.x + world.x - g.start.x, y: g.center.y + world.y - g.start.y });
      const frame = this.gameCamera.visibleFrame; this.canvas.style.cursor = frame && contains(frame, world) ? 'grab' : '';
    }
    g.last = this.toWorld(screen); this.gesture = null; this.selectionBackdrop = null;
    if (g.kind === 'pan') { this.hover = g.last; this.onHover(this.hover); this.canvas.style.cursor = ''; }
    if (this.canvas.hasPointerCapture(e.pointerId)) this.canvas.releasePointerCapture(e.pointerId);
    const dx = Math.round(g.last.x - g.start.x), dy = Math.round(g.last.y - g.start.y);
    if (g.kind === 'paint' && g.tile) {
      this.extendTileGesture(g, this.local(g.last, g.room));
      if (g.tile.overflow) {
        this.releaseTileOverlay(g.tile);
        this.resolveTileIdle?.(); this.resolveTileIdle = null;
        this.gestureTail = g.tail; this.onError('strokeTooLong'); this.requestDraw(); return;
      }
      if (g.tile.erase && !g.tile.cells.size) {
        this.releaseTileOverlay(g.tile);
        this.resolveTileIdle?.(); this.resolveTileIdle = null;
        this.gestureTail = g.tail; this.requestDraw(); return;
      }
      this.tileCommitPending = true;
      this.enqueue(g, 'tileGesture', () => ({ roomId: g.tile!.roomId, erase: g.tile!.erase, points: g.tile!.points,
        baseRevision: g.tile!.baseRevision, compactDocument: g.tile!.live }), state => {
        if (g.tile!.live && !this.applyCommittedTileGesture(g.tile!, state)) void this.refreshState().catch(() => undefined);
      });
      this.gestureTail = g.tail;
      void g.tail.finally(() => {
        this.releaseTileOverlay(g.tile!);
        this.tileCommitPending = false;
        this.resolveTileIdle?.(); this.resolveTileIdle = null;
        this.requestDraw();
      });
    }
    else if (g.kind === 'paint' && g.object) {
      this.extendObjectGesture(g, this.local(g.last, g.room));
      if (g.object.overflow) {
        if (this.objectOverlay === g.object) this.objectOverlay = null;
        this.gestureTail = g.tail; this.onError('strokeTooLong'); this.requestDraw(); return;
      }
      this.objectCommitsPending++;
      this.enqueue(g, 'objectGesture', () => {
        if (!this.objectSelectionMatches(g.object!)) { this.onError('writerBusy'); return null; }
        return { roomId: g.object!.roomId, erase: g.object!.erase, points: g.object!.points,
          baseRevision: g.object!.rebaseAfterPending ? this.state!.revision : g.object!.baseRevision };
      });
      this.gestureTail = g.tail;
      void g.tail.finally(() => {
        if (this.objectOverlay === g.object) this.objectOverlay = null;
        this.objectCommitsPending--; this.requestDraw();
      });
    }
    else if (g.kind === 'selection') {
      if (g.area) this.enqueue(g, 'moveSelection', { dx, dy });
      else this.enqueue(g, 'selectArea', this.selectionRect(g));
    }
    else if (g.kind === 'room-menu') {
      if (e.button === 2)
        this.onRoomContextMenu({ x: Math.floor(g.start.x), y: Math.floor(g.start.y) }, { x: e.clientX, y: e.clientY });
    }
    else if (g.kind === 'room-create') this.enqueue(g, 'roomAdd', box(g.start, g.last));
    else if (g.kind === 'room-move' && (dx || dy)) this.enqueue(g, 'roomMove', { id: g.room.id, dx, dy, selected: (g.rooms?.length || 0) > 1 });
    else if (g.kind === 'room-resize') this.enqueue(g, 'roomResize', { id: g.room.id, ...this.resizedRoom(g), crop: this.crop, snap: this.snapRooms && !e.ctrlKey });
    else if (g.kind === 'object-move') { const step = e.ctrlKey || e.metaKey ? 16 : 1; const x = Math.round((g.last.x - g.start.x) * step) / step, y = Math.round((g.last.y - g.start.y) * step) / step; if (x || y) this.enqueue(g, 'objectMove', { dx: x, dy: y }); }
    else if (g.kind === 'node-move') this.enqueue(g, 'nodeMove', this.local(g.last, g.room));
    if (!g.tile) this.gestureTail = g.tail;
    this.requestDraw();
  }
  cancel(pointerId?: number): void {
    const g = this.gesture; if (!g || pointerId !== undefined && g.pointer !== pointerId) return; this.gesture = null; this.selectionBackdrop = null;
    if (g.kind === 'pan' || g.kind === 'game-camera') this.canvas.style.cursor = '';
    if (this.canvas.hasPointerCapture(g.pointer)) this.canvas.releasePointerCapture(g.pointer);
    if (g.kind === 'paint' && g.tile) {
      this.releaseTileOverlay(g.tile);
      this.resolveTileIdle?.(); this.resolveTileIdle = null;
    }
    else if (g.kind === 'paint' && g.object && this.objectOverlay === g.object) this.objectOverlay = null;
    this.gestureTail = g.tail;
    this.requestDraw();
  }
  private beginTileGesture(g: Gesture, point: Point, erase: boolean, selection: State['selection']): void {
    const first = cell(point);
    const tile: TileGesture = {
      roomId: g.room.id, layer: selection.layer, erase,
      live: erase || selection.tool === 1 || selection.tool === 3,
      material: selection.material, shape: selection.shape, groupId: selection.groupId,
      brushSize: selection.brushSize, tool: selection.tool,
      baseRevision: this.state!.revision, baseInstanceId: this.state!.instanceId, baseDocumentRevision: this.state!.documentRevision,
      baseDocument: this.state!.document,
      points: [first], rasterLength: 1, cells: new Map<TileCellKey, Cell | null>(), overflow: false,
      size: { counter: new TileByteCounter(), version: this.state!.export.version, bytes: 0, count: 0,
        baseCount: (selection.layer === 0 ? g.room.foreground : g.room.background).length, properties: 0 }
    };
    const material = this.materials.get(tile.material);
    if (!erase && material?.themeId) {
      const properties = g.room.properties.map(property => ({ ...property }));
      for (const [key, value] of [['mapMaker.terrainTheme', material.themeId], ['mapMaker.minimapColor', material.themeColor]]) {
        const property = properties.find(item => item.key === key);
        if (property) property.value = value; else properties.push({ key, value });
      }
      tile.size.properties = tile.size.counter.propertiesBytes(properties) - tile.size.counter.propertiesBytes(g.room.properties);
    }
    g.tile = tile; this.tileOverlay = tile;
    this.tileIdle = new Promise(resolve => this.resolveTileIdle = resolve);
    if (tile.live) this.stampTile(g.room, tile, first);
  }
  private extendTileGesture(g: Gesture, point: Point): void {
    const tile = g.tile!;
    if (tile.overflow && tile.live) return;
    const next = cell(point), previous = tile.points[tile.points.length - 1];
    if (previous.x === next.x && previous.y === next.y) return;
    if (!tile.live) {
      if (tile.tool === 5) return;
      const start = tile.points[0], rasterLength = 1 + Math.max(Math.abs(next.x - start.x), Math.abs(next.y - start.y));
      tile.rasterLength = rasterLength; tile.overflow = rasterLength > MAX_TILE_GESTURE_POINTS;
      if (tile.points.length === 1) tile.points.push(next); else tile.points[1] = next;
      return;
    }
    const segmentLength = Math.max(Math.abs(next.x - previous.x), Math.abs(next.y - previous.y));
    const rasterLength = tile.rasterLength + segmentLength;
    if (segmentLength > MAX_TILE_GESTURE_POINTS - tile.rasterLength
      || this.continuousTileWork(tile.brushSize, rasterLength) > MAX_TILE_GESTURE_WORK) { tile.overflow = true; return; }
    tile.rasterLength = rasterLength; tile.points.push(next);
    if (!g.room.visible || g.room.locked || !this.memberEditable(tile.layer, tile.groupId, tile.groupId)) return;
    const overlay = this.ensureOverlayIndex(), base = this.occupancy.get(g.room.id + ':' + tile.layer);
    let x = previous.x, y = previous.y;
    const dx = Math.abs(next.x - x), dy = Math.abs(next.y - y), sx = x < next.x ? 1 : -1, sy = y < next.y ? 1 : -1;
    let error = dx - dy;
    for (let count = 0; count < segmentLength; count++) {
      const previousX = x, previousY = y, twice = error * 2;
      if (twice > -dy) { error -= dy; x += sx; }
      if (twice < dx) { error += dx; y += sy; }
      this.stampTileDelta(g.room, tile, previousX, previousY, x, y, overlay, base);
    }
  }
  private stampTile(room: Room, tile: TileGesture, center: Point): void {
    if (!room.visible || room.locked || !this.memberEditable(tile.layer, tile.groupId, tile.groupId)) return;
    const offset = Math.floor((tile.brushSize - 1) / 2);
    if (center.x + tile.brushSize - 1 - offset < 0 || center.y + tile.brushSize - 1 - offset < 0
      || center.x - offset >= room.width || center.y - offset >= room.height) return;
    const overlay = this.ensureOverlayIndex(), base = this.occupancy.get(room.id + ':' + tile.layer);
    for (let y = 0; y < tile.brushSize; y++) for (let x = 0; x < tile.brushSize; x++) {
      const px = center.x - offset + x, py = center.y - offset + y;
      this.stampTileCell(room, tile, px, py, overlay, base);
    }
  }
  private stampTileDelta(room: Room, tile: TileGesture, previousX: number, previousY: number, centerX: number, centerY: number,
    overlay: OverlayIndex, base: LayerIndex | undefined): void {
    const dx = centerX - previousX, dy = centerY - previousY;
    const offset = Math.floor((tile.brushSize - 1) / 2), left = centerX - offset, bottom = centerY - offset;
    if (left >= room.width || bottom >= room.height || left + tile.brushSize <= 0 || bottom + tile.brushSize <= 0) return;
    let edgeX: number | null = null;
    if (dx) {
      edgeX = dx > 0 ? left + tile.brushSize - 1 : left;
      for (let y = 0; y < tile.brushSize; y++) this.stampTileCell(room, tile, edgeX, bottom + y, overlay, base);
    }
    if (dy) {
      const edgeY = dy > 0 ? bottom + tile.brushSize - 1 : bottom;
      for (let x = 0; x < tile.brushSize; x++) if (left + x !== edgeX) this.stampTileCell(room, tile, left + x, edgeY, overlay, base);
    }
  }
  private stampTileCell(room: Room, tile: TileGesture, x: number, y: number, overlay: OverlayIndex, base: LayerIndex | undefined): void {
    if (x < 0 || y < 0 || x >= room.width || y >= room.height) return;
    const row = overlay.rows.get(y), had = row?.has(x) || false;
    const existing = had ? row!.get(x) || undefined : base?.rows.get(y)?.get(x);
    if (existing && !this.memberEditable(tile.layer, existing.groupId || '', tile.groupId)) return;
    if (tile.erase && !existing) return;
    const replacement = tile.erase ? null : { x, y, shape: tile.shape, material: tile.material, groupId: existing?.groupId ?? tile.groupId };
    tile.size.bytes += (replacement ? tile.size.counter.cellBytes(replacement) : 0)
      - (existing ? tile.size.counter.cellBytes(existing) : 0);
    tile.size.count += Number(!!replacement) - Number(!!existing);
    this.setOverlayCell(tile, overlay, row, had, x, y, replacement, replacement || existing!);
  }
  private continuousTileWork(brushSize: number, rasterLength: number): number {
    return 3 * brushSize * brushSize + Math.max(0, rasterLength - 1) * (2 * brushSize - 1);
  }
  private beginObjectGesture(g: Gesture, point: Point, erase: boolean, selection: State['selection']): void {
    const object: ObjectGesture = {
      roomId: g.room.id, erase, baseRevision: this.state!.revision, baseInstanceId: this.state!.instanceId,
      points: [{ ...point }], hidden: new Set(),
      definition: selection.objectDefinition, layer: selection.layer, groupId: selection.groupId, tool: selection.tool,
      rebaseAfterPending: this.objectCommitsPending > 0, overflow: false,
    };
    if (erase && g.room.objects.length > MAX_OBJECT_GESTURE_WORK) object.overflow = true;
    g.object = object; this.objectOverlay = object;
    if (erase && !object.overflow) this.hideObjectAt(g.room, object, point);
  }
  private extendObjectGesture(g: Gesture, point: Point): void {
    const object = g.object!; if (object.overflow || !Number.isFinite(point.x) || !Number.isFinite(point.y)) { object.overflow = true; return; }
    const previous = object.points[object.points.length - 1];
    if (previous.x === point.x && previous.y === point.y) return;
    const next = { ...point };
    if (!object.erase) {
      if (object.points.length === 1) object.points.push(next); else object.points[1] = next;
      return;
    }
    this.hideObjectsBetween(g.room, object, previous, next);
    if (object.points.length >= 2 && this.collinearContinuation(object.points[object.points.length - 2], previous, next)) {
      object.points[object.points.length - 1] = next; return;
    }
    if (object.points.length >= MAX_TILE_GESTURE_POINTS || g.room.objects.length * (object.points.length + 1) > MAX_OBJECT_GESTURE_WORK) { object.overflow = true; return; }
    object.points.push(next);
  }
  private collinearContinuation(a: Point, b: Point, c: Point): boolean {
    const abX = b.x - a.x, abY = b.y - a.y, bcX = c.x - b.x, bcY = c.y - b.y;
    const cross = abX * bcY - abY * bcX, scale = Math.max(1, Math.abs(abX) + Math.abs(abY) + Math.abs(bcX) + Math.abs(bcY));
    return Math.abs(cross) <= 1e-10 * scale && abX * bcX + abY * bcY >= 0;
  }
  private objectSelectionMatches(gesture: ObjectGesture): boolean {
    const selection = this.state?.selection;
    return !!selection && selection.layer === gesture.layer && selection.groupId === gesture.groupId
      && selection.tool === gesture.tool && definitionKey(selection.objectDefinition) === definitionKey(gesture.definition);
  }
  private objectEditable(room: Room, gesture: ObjectGesture, object: MapObject): boolean {
    return room.visible && !room.locked && (gesture.layer === 6 || object.layer === gesture.layer)
      && this.memberEditable(object.layer, object.groupId || '', gesture.groupId);
  }
  private hideObjectAt(room: Room, gesture: ObjectGesture, point: Point): void {
    if (point.x < 0 || point.y < 0 || point.x >= room.width || point.y >= room.height) return;
    const hit = this.objectCandidates(room, this.objectGestureLayers(room, gesture), { x: point.x, y: point.y, width: 0, height: 0 })
      .filter(entry => !gesture.hidden.has(entry.object.id) && this.objectEditable(room, gesture, entry.object) && this.objectContains(entry.object, point))
      .sort((a, b) => this.objectLayerPriority(b.object.layer) - this.objectLayerPriority(a.object.layer)
        || Math.abs(a.object.width * a.object.height * a.object.scaleX * a.object.scaleY) - Math.abs(b.object.width * b.object.height * b.object.scaleX * b.object.scaleY)
        || b.order - a.order)[0];
    if (hit) gesture.hidden.add(hit.object.id);
  }
  private hideObjectsBetween(room: Room, gesture: ObjectGesture, from: Point, to: Point): void {
    const bounds = { x: Math.min(from.x, to.x), y: Math.min(from.y, to.y), width: Math.abs(to.x - from.x), height: Math.abs(to.y - from.y) };
    for (const entry of this.objectCandidates(room, this.objectGestureLayers(room, gesture), bounds)) {
      const object = entry.object;
      if (!gesture.hidden.has(object.id) && this.objectEditable(room, gesture, object) && this.objectSegmentHit(object, from, to)) gesture.hidden.add(object.id);
    }
  }
  private objectGestureLayers(room: Room, gesture: ObjectGesture): number[] {
    return gesture.layer === 6 ? [...(this.objectLayers.get(room.id)?.keys() || [])] : [gesture.layer];
  }
  private refreshObjectErasePreview(state: State, gesture: ObjectGesture): void {
    const room = state.document.rooms.find(candidate => candidate.id === gesture.roomId); if (!room || !gesture.points.length) return;
    gesture.hidden.clear(); this.hideObjectAt(room, gesture, gesture.points[0]);
    for (let index = 1; index < gesture.points.length; index++) this.hideObjectsBetween(room, gesture, gesture.points[index - 1], gesture.points[index]);
  }
  private objectContains(object: MapObject, point: Point): boolean {
    const local = this.inverseObjectPoint(object, point); if (!local) return false;
    return local.x >= object.x && local.x <= object.x + object.width && local.y >= object.y && local.y <= object.y + object.height;
  }
  private objectSegmentHit(object: MapObject, from: Point, to: Point): boolean {
    const localFrom = this.inverseObjectPoint(object, from), localTo = this.inverseObjectPoint(object, to);
    return !!localFrom && !!localTo && segmentIntersectsRect(localFrom, localTo, object);
  }
  private inverseObjectPoint(object: MapObject, point: Point): Point | null {
    const scaleX = object.scaleX ?? 1, scaleY = object.scaleY ?? 1;
    if (Math.abs(scaleX) < .000001 || Math.abs(scaleY) < .000001) return null;
    const centerX = object.x + object.width / 2, centerY = object.y + object.height / 2;
    const dx = point.x - centerX, dy = point.y - centerY, radians = (object.rotation || 0) * Math.PI / 180;
    const cosine = Math.cos(radians), sine = Math.sin(radians);
    return { x: centerX + (cosine * dx + sine * dy) / scaleX, y: centerY + (-sine * dx + cosine * dy) / scaleY };
  }
  private objectLayerPriority(layer: number): number { return layer === 4 ? 4 : layer === 2 ? 3 : layer === 3 ? 2 : 1; }
  private applyCommittedTileGesture(tile: TileGesture, state: State): boolean {
    if (state.instanceId !== tile.baseInstanceId || state.documentRevision < tile.baseDocumentRevision) return false;
    // Repainting an already identical cell is a successful command but does not
    // change the document revision. Its compact response is authoritative and
    // must not trigger an unnecessary full-state refresh/index rebuild.
    if (state.documentRevision === tile.baseDocumentRevision) return state.revision > tile.baseRevision;
    // A normal compact response retains the document object that the gesture
    // started from, so the optimistic cells still need to be merged locally.
    // A duplicate-command retry can instead return a new authoritative full
    // document after the first response was lost. That object already contains
    // the command and any interleaved edits; applying the overlay again would
    // overwrite those newer cells in the client only.
    if (state.document !== tile.baseDocument) return true;
    const room = state.document.rooms.find(candidate => candidate.id === tile.roomId);
    const index = this.occupancy.get(tile.roomId + ':' + tile.layer);
    if (!room || !index || index.cells !== (tile.layer === 0 ? room.foreground : room.background)) return false;
    this.liveSizes.commit(state.instanceId, { from: tile.size.version, to: state.export.version,
      roomId: tile.roomId, bytes: this.tileSizeDelta(tile) });
    tile.size.applied = true;
    const overlay = this.ensureOverlayIndex();
    for (const key of overlay.affectedChunks) {
      const cached = this.exactTileChunks.get(tile.roomId + ':' + tile.layer + ':' + key);
      if (cached) cached.dirty = true;
    }
    for (const [y, edits] of overlay.rows) for (const [x, replacement] of edits) {
      this.applyCellEdit(index, x, y, replacement); index.lod.delete(chunkKey(x, y));
    }
    if (!tile.erase && tile.cells.size) {
      const material = this.materials.get(tile.material);
      if (material?.themeId) {
        this.setRoomProperty(room, 'mapMaker.terrainTheme', material.themeId);
        this.setRoomProperty(room, 'mapMaker.minimapColor', material.themeColor);
      }
    }
    this.documentToken = `${state.instanceId}:${(state as RevisionedState).documentRevision ?? state.revision}`;
    this.overlayLod = null;
    return true;
  }
  private applyCellEdit(index: LayerIndex, x: number, y: number, replacement: Cell | null): void {
    const row = index.rows.get(y), positionRow = index.positions.get(y), position = positionRow?.get(x);
    if (replacement) {
      if (position === undefined) {
        const next = index.cells.length; index.cells.push(replacement);
        let cells = index.rows.get(y); if (!cells) { cells = new Map(); index.rows.set(y, cells); } cells.set(x, replacement);
        let positions = index.positions.get(y); if (!positions) { positions = new Map(); index.positions.set(y, positions); } positions.set(x, next);
        const key = chunkKey(x, y); let chunk = index.chunks.get(key); if (!chunk) { chunk = []; index.chunks.set(key, chunk); }
        let chunkPositions = index.chunkPositions.get(y); if (!chunkPositions) { chunkPositions = new Map(); index.chunkPositions.set(y, chunkPositions); }
        chunkPositions.set(x, chunk.length); chunk.push(replacement);
      } else {
        const previous = index.cells[position]; previous.shape = replacement.shape; previous.material = replacement.material; previous.groupId = replacement.groupId;
      }
      return;
    }
    if (position === undefined) return;
    const removed = index.cells[position], tailPosition = index.cells.length - 1, tail = index.cells[tailPosition];
    if (position !== tailPosition) {
      index.cells[position] = tail;
      index.positions.get(tail.y)!.set(tail.x, position);
    }
    index.cells.pop(); row!.delete(x); positionRow!.delete(x);
    if (!row!.size) index.rows.delete(y); if (!positionRow!.size) index.positions.delete(y);
    const key = chunkKey(x, y), chunk = index.chunks.get(key), chunkRow = index.chunkPositions.get(y), chunkPosition = chunkRow?.get(x);
    if (chunk && chunkPosition !== undefined) {
      const chunkTailPosition = chunk.length - 1, chunkTail = chunk[chunkTailPosition];
      if (chunkPosition !== chunkTailPosition) { chunk[chunkPosition] = chunkTail; index.chunkPositions.get(chunkTail.y)!.set(chunkTail.x, chunkPosition); }
      chunk.pop(); chunkRow!.delete(x); if (!chunkRow!.size) index.chunkPositions.delete(y); if (!chunk.length) index.chunks.delete(key);
    }
  }
  private setRoomProperty(room: Room, key: string, value: string): void {
    const property = room.properties.find(candidate => candidate.key === key);
    if (property) property.value = value; else room.properties.push({ key, value });
  }
  private memberEditable(layer: number, groupId: string, selectedGroupId: string): boolean {
    const selection = this.state?.selection;
    if (!selection || selection.hiddenLayers.includes(layer) || selection.lockedLayers.includes(layer)) return false;
    let matches = !selectedGroupId;
    const seen = new Set<string>();
    while (groupId && !seen.has(groupId)) {
      seen.add(groupId); const group = this.groupById.get(groupId);
      if (!group) break;
      if (!group.visible || group.locked) return false;
      if (group.id === selectedGroupId) matches = true;
      groupId = group.parentId;
    }
    return matches;
  }
  private resetOverlayIndex(source: TileGesture | null): void {
    const index: OverlayIndex = { source, size: source?.cells.size || 0, rows: new Map(), chunks: new Map(), affectedChunks: new Set() };
    if (source) for (const [key, value] of source.cells) {
      const parts = value ? [value.x, value.y] : typeof key === 'number'
        ? [Math.floor(key / TILE_CELL_KEY_STRIDE), key % TILE_CELL_KEY_STRIDE]
        : key.split(',').map(Number), x = parts[0], y = parts[1];
      let row = index.rows.get(y); if (!row) { row = new Map(); index.rows.set(y, row); } row.set(x, value);
      const chunkId = chunkKey(x, y); let points = index.chunks.get(chunkId); if (!points) { points = []; index.chunks.set(chunkId, points); } points.push(value || { x, y });
      this.markAffectedChunks(index, x, y);
    }
    this.overlayIndex = index; this.overlayLod = null;
  }
  private releaseTileOverlay(source: TileGesture): void {
    this.onHover(this.hover);
    if (this.tileOverlay === source) this.tileOverlay = null;
    if (this.overlayIndex.source === source)
      this.overlayIndex = { source: null, size: 0, rows: new Map(), chunks: new Map(), affectedChunks: new Set() };
    if (this.overlayLod?.source === source) this.overlayLod = null;
  }
  private ensureOverlayIndex(): OverlayIndex {
    const source = this.tileOverlay;
    if (this.overlayIndex.source !== source || this.overlayIndex.size !== (source?.cells.size || 0)) this.resetOverlayIndex(source);
    return this.overlayIndex;
  }
  private setOverlayCell(tile: TileGesture, index: OverlayIndex, row: Map<number, Cell | null> | undefined,
    had: boolean, x: number, y: number, value: Cell | null, point: Point): void {
    if (!row) { row = new Map(); index.rows.set(y, row); }
    tile.cells.set(tileCellKey(x, y), value); row.set(x, value);
    // Export versions stay fixed throughout a held stroke. Track its live edits
    // separately so Preview observes every sample, including equal-size replacements.
    this.previewTileRevision++;
    this.markAffectedChunks(index, x, y);
    if (!had) {
      const key = chunkKey(x, y); let points = index.chunks.get(key); if (!points) { points = []; index.chunks.set(key, points); } points.push(point);
    }
    if (this.overlayLod?.source === tile) this.overlayLod.chunks.delete(chunkKey(x, y));
    index.size = tile.cells.size;
  }
  private markAffectedChunks(index: OverlayIndex, x: number, y: number): void {
    for (let cy = Math.floor((y - 1) / TILE_CHUNK_SIZE); cy <= Math.floor((y + 1) / TILE_CHUNK_SIZE); cy++)
      for (let cx = Math.floor((x - 1) / TILE_CHUNK_SIZE); cx <= Math.floor((x + 1) / TILE_CHUNK_SIZE); cx++)
        index.affectedChunks.add(`${cx},${cy}`);
  }
  private effectiveCellAt(roomId: string, layer: number, x: number, y: number, base = this.occupancy.get(roomId + ':' + layer)): Cell | undefined {
    const overlay = this.tileOverlay;
    if (overlay?.roomId === roomId && overlay.layer === layer) {
      const row = this.ensureOverlayIndex().rows.get(y);
      if (row?.has(x)) return row.get(x) || undefined;
    }
    return base?.rows.get(y)?.get(x);
  }
  private selectionRect(g: Gesture): Rect {
    if (g.area) return { ...g.area,
      x: Math.max(0, Math.min(g.room.width - g.area.width, g.area.x + Math.round(g.last.x - g.start.x))),
      y: Math.max(0, Math.min(g.room.height - g.area.height, g.area.y + Math.round(g.last.y - g.start.y))) };
    const area = box(this.local(g.start, g.room), this.local(g.last, g.room));
    const x = Math.max(0, Math.min(g.room.width, area.x)), y = Math.max(0, Math.min(g.room.height, area.y));
    return { x, y, width: Math.max(0, Math.min(g.room.width, area.x + area.width) - x), height: Math.max(0, Math.min(g.room.height, area.y + area.height) - y) };
  }
  private roomMoveRect(room: Room): Rect {
    const r = this.screenRect(room);
    return { x: r.x, y: r.y - 34, width: Math.max(72, Math.min(r.width, 200)), height: 17 };
  }
  private roomMoveHandle(point: Point, room: Room): boolean {
    const r = this.roomMoveRect(room);
    return point.x >= r.x && point.x <= r.x + r.width && point.y >= r.y && point.y <= r.y + r.height;
  }
  private roomHandle(pointer: Point, room: Room): Point | null {
    for (const x of [-1, 0, 1]) for (const y of [-1, 0, 1]) {
      if (!x && !y) continue;
      const screen = this.roomHandlePoint(room, x, y);
      if (Math.abs(pointer.x - screen.x) <= 7 && Math.abs(pointer.y - screen.y) <= 7) return { x, y };
    } return null;
  }
  private canResizeRoom(room: Room): boolean {
    return this.selectedRooms.size <= 1 && room.visible && !room.locked;
  }
  private roomHandlePoint(room: Room, x: number, y: number): Point {
    const point = this.toScreen({ x: room.x + (x + 1) * room.width / 2, y: room.y + (y + 1) * room.height / 2 });
    // Keep the hit boxes outside paintable cells, including at 1x pixel scale.
    return { x: point.x + x * 9, y: point.y - y * 9 };
  }
  private nodeAt(pointer: Point, room: Room): { id: string; index: number } | null {
    const world = this.toWorld(pointer), local = { x: world.x - room.x, y: world.y - room.y };
    const radius = 8 / Math.max(this.scale, MIN_PIXEL_SCALE);
    for (const id of this.state?.selection.objects || []) {
      const indexed = this.objectById.get(id); if (indexed?.roomId !== room.id) continue;
      const object = indexed.entry.object;
      const candidates = this.pathCandidates(this.objectPathIndex(object).nodes,
        { x: local.x - radius, y: local.y - radius, width: radius * 2, height: radius * 2 });
      for (const i of candidates) {
        const screen = this.toScreen({ x: room.x + object.nodes[i].x, y: room.y + object.nodes[i].y });
        if (Math.hypot(screen.x - pointer.x, screen.y - pointer.y) <= 8) return { id: object.id, index: i };
      }
    }
    return null;
  }
  private roomTerrainBounds(room: Room): Rect | null {
    // Cache once at pointer-down; dense rooms must not be rescanned for every preview frame.
    let left = Infinity, bottom = Infinity, right = -Infinity, top = -Infinity;
    for (const cells of [room.foreground, room.background]) for (const cell of cells) {
      left = Math.min(left, cell.x); bottom = Math.min(bottom, cell.y);
      right = Math.max(right, cell.x + 1); top = Math.max(top, cell.y + 1);
    }
    return Number.isFinite(left) ? { x: room.x + left, y: room.y + bottom, width: right - left, height: top - bottom } : null;
  }
  private resizedRoom(g: Gesture): Rect {
    const dx = Math.round(g.last.x - g.start.x), dy = Math.round(g.last.y - g.start.y), r = g.room, h = g.handle!;
    let left = r.x, right = r.x + r.width, bottom = r.y, top = r.y + r.height;
    if (h.x < 0) left = Math.min(right - 1, left + dx); if (h.x > 0) right = Math.max(left + 1, right + dx);
    if (h.y < 0) bottom = Math.min(top - 1, bottom + dy); if (h.y > 0) top = Math.max(bottom + 1, top + dy);
    if (!this.crop && g.terrainBounds) {
      const terrain = g.terrainBounds;
      left = Math.min(left, terrain.x); bottom = Math.min(bottom, terrain.y);
      right = Math.max(right, terrain.x + terrain.width); top = Math.max(top, terrain.y + terrain.height);
    }
    return { x: left, y: bottom, width: right - left, height: top - bottom };
  }
  requestDraw(): void { this.onPreviewChange?.(); if (this.active && !this.raf) this.raf = requestAnimationFrame(() => { this.raf = 0; this.draw(); }); }
  private drawBrushDamage(points: Point[]): void {
    // A brush must reach the canvas during the input event, not in the next
    // animation frame behind panel/status updates. Limit work to the stroke,
    // old/new cursor and the neighbours whose autotile masks can change.
    if (!this.active || this.canvas.hidden) return;
    const radius = this.brushSize + 1;
    let left = Infinity, bottom = Infinity, right = -Infinity, top = -Infinity;
    for (const point of points) {
      left = Math.min(left, point.x - radius); right = Math.max(right, point.x + radius);
      bottom = Math.min(bottom, point.y - radius); top = Math.max(top, point.y + radius);
    }
    this.onPreviewChange?.({ x: left, y: bottom, width: right - left, height: top - bottom });
    if (this.raf) { cancelAnimationFrame(this.raf); this.raf = 0; this.draw(); return; }
    const screen = this.screenRect({ x: left, y: bottom, width: right - left, height: top - bottom });
    const x = Math.max(0, Math.floor(screen.x) - 2), y = Math.max(0, Math.floor(screen.y) - 2);
    const width = Math.min(this.width, Math.ceil(screen.x + screen.width) + 2) - x;
    const height = Math.min(this.height, Math.ceil(screen.y + screen.height) + 2) - y;
    if (width > 0 && height > 0) this.draw({ x, y, width, height });
  }
  private draw(damage?: Rect): void {
    if (!this.active || this.canvas.hidden) return;
    this.renderFrame++;
    const rect = this.canvas.getBoundingClientRect(), previousWidth = this.width, previousHeight = this.height, previousDpr = this.dpr;
    this.width = rect.width; this.height = rect.height; this.dpr = Math.max(1, window.devicePixelRatio || 1);
    const viewportChanged = this.width !== previousWidth || this.height !== previousHeight || this.dpr !== previousDpr;
    if (viewportChanged) { damage = undefined; this.selectionBackdrop = null; }
    if (this.selectionBackdrop && this.gesture?.kind === 'selection') {
      const ctx = this.ctx; ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.drawImage(this.selectionBackdrop, 0, 0);
      ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
      const area = this.selectionRect(this.gesture);
      this.outline({ ...area, x: area.x + this.gesture.room.x, y: area.y + this.gesture.room.y }, '#72bde5', true); return;
    }
    if (this.cameraPreview && this.cameraScaleAuto && viewportChanged) this.pixelScale = this.fittedCameraScale();
    if (viewportChanged) this.onHover(this.hover);
    const w = Math.round(rect.width * this.dpr), h = Math.round(rect.height * this.dpr);
    if (this.canvas.width !== w || this.canvas.height !== h) { this.canvas.width = w; this.canvas.height = h; }
    this.drawScene(damage);
  }
  /** Render synchronously into another viewport using the same indexes, images and live stroke.
   * The scoped render state is restored even if a texture fails. No editing state or camera
   * position escapes this pass, and the caller schedules it outside the brush input event. */
  renderGamePreview(canvas: HTMLCanvasElement, center: Point, worldDamage?: Rect): { tileScale: number; pixelScale: number } | null {
    const camera = this.cameraProfile, rect = canvas.getBoundingClientRect();
    if (!camera || rect.width <= 0 || rect.height <= 0) return null;
    const ctx = canvas.getContext('2d', { alpha: false }); if (!ctx) return null;
    const saved = { canvas: this.canvas, ctx: this.ctx, width: this.width, height: this.height, dpr: this.dpr,
      center: this.center, pixelScale: this.pixelScale, cameraPreview: this.cameraPreview,
      renderOrigin: this.renderOrigin, tileLodModes: this.tileLodModes, selectedObjects: this.selectedObjects };
    try {
      this.canvas = canvas; this.ctx = ctx; this.width = rect.width; this.height = rect.height;
      this.dpr = Math.max(1, window.devicePixelRatio || 1); this.center = center; this.cameraPreview = true;
      const previous = this.previewViews.get(canvas);
      this.renderOrigin = null; this.selectedObjects = new Set(); this.tileLodModes = previous?.lod || new Map(); this.renderFrame++;
      const width = Math.round(rect.width * this.dpr), height = Math.round(rect.height * this.dpr);
      if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height; }
      this.width = width / this.dpr; this.height = height / this.dpr;
      const fit = Math.min(width / camera.referenceWidth, height / camera.referenceHeight);
      // Integer magnification and reciprocal reduction keep the whole camera frame visible.
      this.pixelScale = fit >= 1 ? Math.floor(fit) : 1 / Math.ceil(1 / fit);
      const stamp = `${this.previewRevision}:${this.gameCamera.revision}:${width}:${height}:${this.dpr}:${this.pixelScale}:${center.x}:${center.y}`;
      let damage: Rect | undefined;
      // Partial compositing is exact on the source-pixel grid. Reduced or smooth
      // views use a full cached pass to avoid resampling seams along patch edges.
      if (worldDamage && previous?.stamp === stamp && this.pixelScale >= 1 && this.gameCamera.pixelPerfect) {
        const r = this.screenRect(worldDamage);
        const x = Math.max(0, Math.floor(r.x * this.dpr) - 2), y = Math.max(0, Math.floor(r.y * this.dpr) - 2);
        damage = { x: x / this.dpr, y: y / this.dpr,
          width: (Math.min(width, Math.ceil((r.x + r.width) * this.dpr) + 2) - x) / this.dpr,
          height: (Math.min(height, Math.ceil((r.y + r.height) * this.dpr) + 2) - y) / this.dpr };
      }
      if (!damage || damage.width > 0 && damage.height > 0) this.drawScene(damage);
      this.previewViews.set(canvas, { stamp, lod: this.tileLodModes });
      return { tileScale: this.scale, pixelScale: this.pixelScale };
    } finally { Object.assign(this, saved); }
  }
  private drawScene(damage?: Rect): void {
    // LOD is a viewport decision, not a dirty-rectangle decision. Crossing its
    // density threshold invalidates the entire frame so exact sprites and LOD
    // colours can never be mixed into differently rendered patches.
    const camera = this.cameraProfile, view = this.visibleWorldBounds(camera), nextLodModes = new Map<string, boolean>();
    const selectedRoom = activeRoom(this.state);
    const rooms = this.cameraPreview ? (selectedRoom ? [selectedRoom] : []) : this.state?.document.rooms || [];
    if (this.state) for (const room of rooms) {
      if (!room.visible || this.cameraPreview && room.id !== this.state.selection.roomId || !overlaps(room, view)) continue;
      for (const layer of [0, 1]) {
        if (this.state.selection.hiddenLayers.includes(layer)) continue;
        const key = room.id + ':' + layer, index = this.occupancy.get(key); if (!index) continue;
        const lod = this.useTileLod(room, layer, index,
          Math.max(0, Math.floor(view.x - room.x)), Math.max(0, Math.floor(view.y - room.y)),
          Math.min(room.width - 1, Math.ceil(view.x + view.width - room.x)),
          Math.min(room.height - 1, Math.ceil(view.y + view.height - room.y)));
        nextLodModes.set(key, lod);
        if (damage && this.tileLodModes.get(key) !== lod) damage = undefined;
      }
    }
    this.tileLodModes = nextLodModes;
    const ctx = this.ctx; ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0); ctx.imageSmoothingEnabled = false;
    if (damage) { ctx.save(); ctx.beginPath(); ctx.rect(damage.x, damage.y, damage.width, damage.height); ctx.clip(); }
    const mode = this.cameraPreview ? 'camera' : this.overview ? 'overview' : 'edit';
    if (this.canvas.dataset.viewMode !== mode) this.canvas.dataset.viewMode = mode;
    if (this.cameraPreview && camera) {
      const resolution = `${camera.referenceWidth}x${camera.referenceHeight}`, scale = String(this.pixelScale);
      if (this.canvas.dataset.cameraResolution !== resolution) this.canvas.dataset.cameraResolution = resolution;
      if (this.canvas.dataset.cameraScale !== scale) this.canvas.dataset.cameraScale = scale;
    } else {
      if (this.canvas.dataset.cameraResolution !== undefined) delete this.canvas.dataset.cameraResolution;
      if (this.canvas.dataset.cameraScale !== undefined) delete this.canvas.dataset.cameraScale;
    }
    ctx.fillStyle = this.cameraPreview ? '#0b0b0b' : '#191919'; ctx.fillRect(0, 0, this.width, this.height);
    if (!this.state) { if (damage) ctx.restore(); return; }
    this.renderOrigin = this.computeOrigin();
    const cameraRect = this.cameraPreview ? this.cameraScreenRect() : null;
    if (cameraRect) { ctx.save(); ctx.beginPath(); ctx.rect(cameraRect.x, cameraRect.y, cameraRect.width, cameraRect.height); ctx.clip(); ctx.fillStyle = '#191919'; ctx.fillRect(cameraRect.x, cameraRect.y, cameraRect.width, cameraRect.height); }
    const s = this.state.selection;
    const hiddenObjects = this.objectOverlay?.hidden; this.selectedObjects = new Set(s.objects);
    const low = damage && this.toWorld({ x: damage.x, y: damage.y + damage.height });
    const high = damage && this.toWorld({ x: damage.x + damage.width, y: damage.y });
    const tileView = low && high ? { x: low.x, y: low.y, width: high.x - low.x, height: high.y - low.y } : view;
    const padding = this.objectOutlinePadding / this.scale;
    const objectView = damage ? { x: tileView.x - padding, y: tileView.y - padding,
      width: tileView.width + padding * 2, height: tileView.height + padding * 2 } : view;
    for (const room of rooms) {
      if (!room.visible || this.cameraPreview && room.id !== s.roomId || !overlaps(room, view)) continue;
      const rect = this.screenRect(room);
      ctx.save(); ctx.globalAlpha = roomOpacity(room.id, s.roomId);
      ctx.fillStyle = '#242424'; ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
      for (const layer of [1, 5, 0, 3, 2, 4]) {
        if (s.hiddenLayers.includes(layer)) continue;
        if (tileLayer(layer)) this.drawTiles(room, layer, tileView);
        else for (const entry of this.visibleObjectEntries(room, layer, objectView)) {
          if (hiddenObjects?.has(entry.object.id)) continue;
          if (!overlaps({ x: room.x + entry.left, y: room.y + entry.bottom, width: entry.right - entry.left, height: entry.top - entry.bottom }, view)) continue;
          if (this.groupVisible(entry.object.groupId)) this.drawObject(room, entry.object);
        }
      }
      if (!this.cameraPreview) {
        ctx.strokeStyle = '#ffffff'; ctx.lineWidth = room.id === s.roomId ? 2 : 1;
        ctx.strokeRect(rect.x, rect.y, rect.width, rect.height);
        if (this.showNames && !(room.id === s.roomId && this.canResizeRoom(room))) { ctx.font = '11px system-ui'; ctx.fillStyle = room.id === s.roomId ? '#f0f0f0' : '#929292'; ctx.fillText((room.locked ? '▣ ' : '') + room.name, rect.x + 5, rect.y - 8); }
      }
      ctx.restore();
    }
    if (!this.cameraPreview && !this.gameCameraTool && this.selectedObjects.size) this.drawSelectedObjectPaths(view, hiddenObjects, s.hiddenLayers);
    if (!this.cameraPreview && !this.gameCameraTool && this.objectOverlay && !this.objectOverlay.erase) this.drawObjectPlacementPreview(this.objectOverlay);
    const room = activeRoom(this.state);
    if (!this.cameraPreview && room && this.showGrid && this.scale >= 12) {
      const r = this.screenRect(room); ctx.save(); ctx.beginPath(); ctx.rect(r.x, r.y, r.width, r.height); ctx.clip();
      ctx.beginPath();
      for (let x = Math.max(room.x, Math.ceil(view.x)); x <= Math.min(room.x + room.width, view.x + view.width); x++) { const p = this.toScreen({ x, y: 0 }); ctx.moveTo(p.x, Math.max(0, r.y)); ctx.lineTo(p.x, Math.min(this.height, r.y + r.height)); }
      for (let y = Math.max(room.y, Math.ceil(view.y)); y <= Math.min(room.y + room.height, view.y + view.height); y++) { const p = this.toScreen({ x: 0, y }); ctx.moveTo(Math.max(0, r.x), p.y); ctx.lineTo(Math.min(this.width, r.x + r.width), p.y); }
      ctx.strokeStyle = '#ffffff12'; ctx.lineWidth = 1 / this.dpr; ctx.stroke(); ctx.restore();
    }
    if (!this.cameraPreview && !this.gameCameraTool && room && s.area && this.gesture?.kind !== 'selection') this.outline({ x: room.x + s.area.x, y: room.y + s.area.y, width: s.area.width, height: s.area.height }, '#72bde5', true);
    if (!this.cameraPreview && room && this.canResizeRoom(room)) {
      const r = this.roomMoveRect(room); ctx.fillStyle = '#334b5d'; ctx.fillRect(r.x, r.y, r.width, r.height);
      ctx.fillStyle = '#e4f3fc'; ctx.font = '11px system-ui'; ctx.fillText('⠿ ' + room.name, r.x + 5, r.y + 12, r.width - 10);
    }
    if (!this.cameraPreview && room && this.canResizeRoom(room)) for (const x of [-1, 0, 1]) for (const y of [-1, 0, 1]) if (x || y) {
      const p = this.roomHandlePoint(room, x, y); ctx.fillStyle = '#6bb5dc'; ctx.fillRect(p.x - 3, p.y - 3, 6, 6);
    }
    const g = this.gesture;
    if (!this.cameraPreview && !this.gameCameraTool && g?.kind === 'selection') {
      const area = this.selectionRect(g); this.outline({ ...area, x: area.x + g.room.x, y: area.y + g.room.y }, '#72bde5', true);
    }
    if (!this.cameraPreview && !this.gameCameraTool && g?.kind === 'room-create') this.outline(box(g.start, g.last), '#72bde5', true);
    if (!this.cameraPreview && g?.kind === 'room-resize') this.outline(this.resizedRoom(g), '#ffffff', true);
    if (!this.cameraPreview && this.selectedRooms.size > 1) {
      for (const selected of this.state.document.rooms) if (selected.visible && this.selectedRooms.has(selected.id)) this.outline(selected, '#72bde5', false);
    }
    if (!this.cameraPreview && g?.kind === 'room-move') {
      const rooms = g.rooms || [g.room];
      const delta = resolveRoomGroupMove(rooms, { x: Math.round(g.last.x - g.start.x), y: Math.round(g.last.y - g.start.y) }, this.state.document.rooms);
      for (const moved of rooms) if (moved.visible) this.outline({ ...moved, x: moved.x + delta.x, y: moved.y + delta.y }, '#ffffff', true);
    }
    if (!this.cameraPreview && !this.gameCameraTool && this.selectedRooms.size <= 1 && room && tileLayer(s.layer) && s.tool !== 0 && s.tool !== 2) {
      const size = this.brushSize;
      const preview = g?.kind === 'paint' && [4, 6, 7, 8].includes(s.tool) ? box(g.start, this.hover) : { x: Math.floor(this.hover.x) - Math.floor((size - 1) / 2), y: Math.floor(this.hover.y) - Math.floor((size - 1) / 2), width: size, height: size };
      this.outline(preview, '#72bde5', false);
    }
    if (!this.cameraPreview && this.gameCameraVisible) this.drawGameCamera();
    if (cameraRect) ctx.restore();
    if (damage) ctx.restore();
    this.renderOrigin = null; this.selectedObjects.clear();
  }
  private drawGameCamera(): void {
    const frame = this.gameCamera.visibleFrame; if (!frame) return;
    const rect = this.screenRect(frame), center = this.toScreen(this.gameCamera.center), ctx = this.ctx;
    ctx.save(); ctx.beginPath(); ctx.rect(rect.x, rect.y, rect.width, rect.height); ctx.clip();
    ctx.fillStyle = '#ffffff08'; ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
    const line = Math.min(2, rect.width / 2, rect.height / 2);
    ctx.strokeStyle = '#ffffff'; ctx.lineWidth = line;
    ctx.strokeRect(rect.x + line / 2, rect.y + line / 2, Math.max(0, rect.width - line), Math.max(0, rect.height - line));
    ctx.beginPath(); ctx.moveTo(center.x - 8, center.y); ctx.lineTo(center.x + 8, center.y);
    ctx.moveTo(center.x, center.y - 8); ctx.lineTo(center.x, center.y + 8);
    ctx.lineWidth = 4; ctx.strokeStyle = '#000000b0'; ctx.stroke();
    ctx.lineWidth = 2; ctx.strokeStyle = '#ffffff'; ctx.stroke(); ctx.restore();
  }
  private visibleObjectEntries(room: Room, layer: number, view: Rect): ObjectRenderEntry[] {
    const localView = { x: view.x - room.x, y: view.y - room.y, width: view.width, height: view.height };
    return this.objectCandidates(room, [layer], localView).sort((a, b) => a.order - b.order);
  }
  private objectCandidates(room: Room, layers: number[], bounds: Rect): ObjectRenderEntry[] {
    const result: ObjectRenderEntry[] = [], seen = new Set<ObjectRenderEntry>();
    const x0 = Math.floor(bounds.x / TILE_CHUNK_SIZE), x1 = Math.floor((bounds.x + Math.max(bounds.width, 1e-9)) / TILE_CHUNK_SIZE);
    const y0 = Math.floor(bounds.y / TILE_CHUNK_SIZE), y1 = Math.floor((bounds.y + Math.max(bounds.height, 1e-9)) / TILE_CHUNK_SIZE);
    const chunkCount = Math.max(0, x1 - x0 + 1) * Math.max(0, y1 - y0 + 1), roomLayers = this.objectLayers.get(room.id);
    for (const layer of layers) {
      const index = roomLayers?.get(layer); if (!index) continue;
      const add = (entry: ObjectRenderEntry) => {
        if (seen.has(entry) || entry.left > bounds.x + bounds.width || entry.right < bounds.x
          || entry.bottom > bounds.y + bounds.height || entry.top < bounds.y) return;
        seen.add(entry); result.push(entry);
      };
      for (const entry of index.spanning) add(entry);
      if (chunkCount > Math.max(64, index.chunks.size * 2)) { for (const entry of index.entries) add(entry); continue; }
      for (let chunkY = y0; chunkY <= y1; chunkY++) for (let chunkX = x0; chunkX <= x1; chunkX++)
        for (const entry of index.chunks.get(`${chunkX},${chunkY}`) || []) add(entry);
    }
    return result;
  }
  private visibleWorldBounds(camera = this.cameraProfile): Rect {
    if (this.cameraPreview && camera) {
      const height = camera.orthographicSize * 2 * camera.ppu / 16, width = height * camera.referenceWidth / camera.referenceHeight;
      return { x: this.center.x - width / 2, y: this.center.y - height / 2, width, height };
    }
    const low = this.toWorld({ x: 0, y: this.height }), high = this.toWorld({ x: this.width, y: 0 });
    return { x: Math.min(low.x, high.x), y: Math.min(low.y, high.y), width: Math.abs(high.x - low.x), height: Math.abs(high.y - low.y) };
  }
  private cameraScreenRect(): Rect | null {
    const camera = this.cameraProfile; if (!camera) return null;
    const height = camera.orthographicSize * 2 * camera.ppu / 16, width = height * camera.referenceWidth / camera.referenceHeight;
    return this.screenRect({ x: this.center.x - width / 2, y: this.center.y - height / 2, width, height });
  }
  private screenRect(rect: Rect): Rect { const p = this.toScreen({ x: rect.x, y: rect.y + rect.height }); return { ...p, width: rect.width * this.scale, height: rect.height * this.scale }; }
  private outline(rect: Rect, color: string, fill: boolean): void { const r = this.screenRect(rect), ctx = this.ctx; if (fill) { ctx.fillStyle = color + '20'; ctx.fillRect(r.x, r.y, r.width, r.height); } ctx.strokeStyle = color; ctx.lineWidth = 1; ctx.strokeRect(r.x, r.y, r.width, r.height); }
  private groupVisible(id: string): boolean {
    if (!id) return true;
    const cached = this.groupVisibility.get(id); if (cached !== undefined) return cached;
    const path: string[] = [], seen = new Set<string>(); let current = id, visible = true;
    while (current && !seen.has(current)) {
      const known = this.groupVisibility.get(current); if (known !== undefined) { visible = known; break; }
      seen.add(current); path.push(current); const group = this.groupById.get(current);
      if (!group) break;
      if (!group.visible) { visible = false; break; }
      current = group.parentId;
    }
    for (const key of path) this.groupVisibility.set(key, visible);
    return visible;
  }
  private drawTiles(room: Room, layer: number, view: Rect): void {
    const index = this.occupancy.get(room.id + ':' + layer); if (!index) return;
    const liveOverlay = this.tileOverlay?.roomId === room.id && this.tileOverlay.layer === layer ? this.tileOverlay : null;
    if (!index.cells.length && !liveOverlay?.cells.size) return;
    const x0 = Math.max(0, Math.floor(view.x - room.x)), x1 = Math.min(room.width - 1, Math.ceil(view.x + view.width - room.x));
    const y0 = Math.max(0, Math.floor(view.y - room.y)), y1 = Math.min(room.height - 1, Math.ceil(view.y + view.height - room.y));
    if (x1 < x0 || y1 < y0) return;
    if (this.tileLodModes.get(room.id + ':' + layer) ?? this.useTileLod(room, layer, index, x0, y0, x1, y1)) { this.drawTilesLod(room, layer, index, x0, y0, x1, y1); return; }
    const overlay = liveOverlay ? this.ensureOverlayIndex() : null;
    const ctx = this.ctx, alpha = ctx.globalAlpha; ctx.globalAlpha = alpha * (layer === 1 ? .62 : 1);
    const chunkX0 = Math.floor(x0 / TILE_CHUNK_SIZE), chunkX1 = Math.floor(x1 / TILE_CHUNK_SIZE);
    const chunkY0 = Math.floor(y0 / TILE_CHUNK_SIZE), chunkY1 = Math.floor(y1 / TILE_CHUNK_SIZE);
    for (let chunkY = chunkY0; chunkY <= chunkY1; chunkY++) for (let chunkX = chunkX0; chunkX <= chunkX1; chunkX++) {
      const key = `${chunkX},${chunkY}`;
      const cells = index.chunks.get(key);
      // A held brush redraws only touched cells directly. Stable chunks can be
      // reused on pan, zoom, cursor movement and commit without recomputing
      // their neighbour masks or issuing one image call per tile.
      if (cells && !overlay?.affectedChunks.has(key) && (this.cameraPreview || Number.isInteger(this.pixelScale) && this.pixelScale >= 1)) {
        const cached = this.exactTileChunk(room, layer, index, key, chunkX, chunkY, cells,
          (x1 - x0 + 1) * (y1 - y0 + 1) >= MIN_CACHED_LOD_CHUNK_CELLS);
        if (cached) {
          const rect = this.screenRect({ x: room.x + chunkX * TILE_CHUNK_SIZE, y: room.y + chunkY * TILE_CHUNK_SIZE,
            width: TILE_CHUNK_SIZE, height: TILE_CHUNK_SIZE });
          ctx.drawImage(cached, rect.x, rect.y, rect.width, rect.height); continue;
        }
      }
      for (const original of cells || []) {
        if (original.x < x0 || original.x > x1 || original.y < y0 || original.y > y1) continue;
        const effective = this.effectiveCellAt(room.id, layer, original.x, original.y, index);
        if (effective && this.groupVisible(effective.groupId || '')) this.drawTileCell(room, layer, index, effective);
      }
      if (overlay) for (const point of overlay.chunks.get(key) || []) {
        if (point.x < x0 || point.x > x1 || point.y < y0 || point.y > y1 || index.rows.get(point.y)?.has(point.x)) continue;
        const effective = overlay.rows.get(point.y)?.get(point.x);
        if (effective && this.groupVisible(effective.groupId || '')) this.drawTileCell(room, layer, index, effective);
      }
    }
    ctx.globalAlpha = alpha;
  }
  private useTileLod(room: Room, layer: number, index: LayerIndex, x0: number, y0: number, x1: number, y1: number): boolean {
    if (!this.cameraPreview && this.scale * this.dpr < LOD_PHYSICAL_TILE_SIZE) return true;
    let count = 0;
    for (let chunkY = Math.floor(y0 / TILE_CHUNK_SIZE); chunkY <= Math.floor(y1 / TILE_CHUNK_SIZE); chunkY++) {
      for (let chunkX = Math.floor(x0 / TILE_CHUNK_SIZE); chunkX <= Math.floor(x1 / TILE_CHUNK_SIZE); chunkX++) {
        count += index.chunks.get(`${chunkX},${chunkY}`)?.length || 0;
        if (count > MAX_EXACT_VISIBLE_CELLS) return true;
      }
    }
    const overlay = this.tileOverlay;
    return !!overlay && overlay.roomId === room.id && overlay.layer === layer && count + overlay.cells.size > MAX_EXACT_VISIBLE_CELLS;
  }
  showDefaultTiles = false;
  setDefaultTiles(value: boolean): void { this.previewRevision++; this.showDefaultTiles = value; this.clearExactTileChunks(); this.requestDraw(); }
  private clearExactTileChunks(): void {
    for (const cached of this.exactTileChunks.values()) cached.canvas.width = cached.canvas.height = 1;
    this.exactTileChunks.clear();
  }
  private exactTileChunk(room: Room, layer: number, index: LayerIndex, key: string, chunkX: number, chunkY: number,
    cells: Cell[], allowBuild: boolean): HTMLCanvasElement | null {
    const id = room.id + ':' + layer + ':' + key;
    let cached = this.exactTileChunks.get(id);
    if (cached && cached.index === index && !cached.dirty) {
      cached.frame = this.renderFrame; this.exactTileChunks.delete(id); this.exactTileChunks.set(id, cached); return cached.canvas;
    }
    if (!allowBuild || cells.length < MIN_CACHED_LOD_CHUNK_CELLS) return null;
    // Resampling arbitrary sprite sizes or vector slopes via an intermediate
    // texture changes pixels. Only bake native 16x16 sprite materials.
    for (const tile of cells) if (!this.materials.get(tile.material)?.nativeShapes.has(tile.shape)) return null;
    if (!cached) {
      if (this.exactTileChunks.size >= MAX_EXACT_TILE_CHUNKS) {
        const oldest = this.exactTileChunks.entries().next().value;
        if (!oldest || oldest[1].frame === this.renderFrame) return null;
        this.exactTileChunks.delete(oldest[0]); oldest[1].canvas.width = oldest[1].canvas.height = 1;
      }
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = TILE_CHUNK_SIZE * 16;
      cached = { canvas, index, dirty: true, frame: this.renderFrame }; this.exactTileChunks.set(id, cached);
    }
    const ctx = cached.canvas.getContext('2d')!; ctx.clearRect(0, 0, cached.canvas.width, cached.canvas.height); ctx.imageSmoothingEnabled = false;
    for (const tile of cells) if (this.groupVisible(tile.groupId || ''))
      this.drawTileCell(room, layer, index, tile, ctx,
        { x: (tile.x - chunkX * TILE_CHUNK_SIZE) * 16, y: (TILE_CHUNK_SIZE - 1 - (tile.y - chunkY * TILE_CHUNK_SIZE)) * 16, width: 16, height: 16 });
    cached.dirty = false; cached.frame = this.renderFrame;
    return cached.canvas;
  }
  private drawTileCell(room: Room, layer: number, index: LayerIndex, tile: Cell, ctx = this.ctx, target?: Rect): void {
    let mask = 0;
    if (tile.shape === 0) for (let i = 0; i < OFFSETS.length; i++) {
      const offset = OFFSETS[i], x = tile.x + offset.x, y = tile.y + offset.y;
      const outside = x < 0 || y < 0 || x >= room.width || y >= room.height;
      const adjacent = outside ? undefined : this.effectiveCellAt(room.id, layer, x, y, index);
      if (outside || adjacent?.material === tile.material && connects(adjacent.shape, -offset.x, -offset.y)) mask |= 1 << i;
    }
    const info = this.materials.get(tile.material);
    const sprite = tile.shape === 0 ? info?.solidMasks.get(normalizeMask(mask)) || info?.fallbackShapes.get(0) : info?.strictShapes.get(tile.shape) || info?.fallbackShapes.get(tile.shape);
    const rect = target || this.screenRect({ x: room.x + tile.x, y: room.y + tile.y, width: 1, height: 1 });
    if (this.showDefaultTiles) { ctx.drawImage(defaultTile(normalizeMask(mask), tile.shape, info?.color || '#cb5574'), rect.x, rect.y, rect.width, rect.height); return; }
    if (sprite && this.sprite(sprite, rect, ctx)) return;
    ctx.fillStyle = info?.color || '#cb5574';
    const points = SHAPE_POINTS[tile.shape];
    if (!points) { ctx.fillRect(rect.x, rect.y, rect.width, rect.height); return; }
    ctx.beginPath();
    for (let i = 0; i < points.length; i++) { const [x, y] = points[i]; if (i) ctx.lineTo(rect.x + x * rect.width, rect.y + y * rect.height); else ctx.moveTo(rect.x + x * rect.width, rect.y + y * rect.height); }
    ctx.closePath(); ctx.fill();
  }
  private drawTilesLod(room: Room, layer: number, index: LayerIndex, x0: number, y0: number, x1: number, y1: number): void {
    const overlayGesture = this.tileOverlay?.roomId === room.id && this.tileOverlay.layer === layer ? this.tileOverlay : null;
    const overlay = overlayGesture ? this.ensureOverlayIndex() : null;
    const ctx = this.ctx, alpha = ctx.globalAlpha; ctx.globalAlpha = alpha * (layer === 1 ? .62 : 1);
    const chunkX0 = Math.floor(x0 / TILE_CHUNK_SIZE), chunkX1 = Math.floor(x1 / TILE_CHUNK_SIZE);
    const chunkY0 = Math.floor(y0 / TILE_CHUNK_SIZE), chunkY1 = Math.floor(y1 / TILE_CHUNK_SIZE);
    for (let chunkY = chunkY0; chunkY <= chunkY1; chunkY++) for (let chunkX = chunkX0; chunkX <= chunkX1; chunkX++) {
      const key = `${chunkX},${chunkY}`, edits = overlay?.chunks.get(key);
      if (edits?.length) {
        const source = this.tileOverlayLodChunk(room, index, overlay!, key, chunkX, chunkY);
        if (source) this.drawTileLodChunk(room, source, chunkX, chunkY);
        continue;
      }
      const cells = index.chunks.get(key); if (!cells?.length) continue;
      if (cells.length >= MIN_CACHED_LOD_CHUNK_CELLS) {
        const source = this.tileLodChunk(index, key, chunkX, chunkY, cells);
        if (source) this.drawTileLodChunk(room, source, chunkX, chunkY);
      } else {
        for (const tile of cells) {
          if (!this.groupVisible(tile.groupId || '')) continue;
          const rect = this.screenRect({ x: room.x + tile.x, y: room.y + tile.y, width: 1, height: 1 });
          ctx.fillStyle = this.materials.get(tile.material)?.color || '#cb5574'; ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
        }
      }
    }
    ctx.globalAlpha = alpha;
  }
  private tileLodChunk(index: LayerIndex, key: string, chunkX: number, chunkY: number, cells: Cell[]): HTMLCanvasElement | null {
    if (index.lod.has(key)) return index.lod.get(key) || null;
    const canvas = this.createTileLodChunk(cells, chunkX, chunkY); index.lod.set(key, canvas); return canvas;
  }
  private tileOverlayLodChunk(room: Room, base: LayerIndex, index: OverlayIndex, key: string, chunkX: number, chunkY: number): HTMLCanvasElement | null {
    let overlay = this.overlayLod;
    if (!overlay || overlay.source !== index.source || overlay.base !== base) {
      overlay = { source: index.source!, base, chunks: new Map() }; this.overlayLod = overlay;
    }
    if (overlay.chunks.has(key)) return overlay.chunks.get(key) || null;
    const effective: Cell[] = [], occupied = new Set<string>();
    for (const original of base.chunks.get(key) || []) {
      const tile = this.effectiveCellAt(room.id, index.source!.layer, original.x, original.y, base);
      if (tile) { effective.push(tile); occupied.add(`${tile.x},${tile.y}`); }
    }
    for (const point of index.chunks.get(key) || []) {
      const tile = index.rows.get(point.y)?.get(point.x);
      if (tile && !occupied.has(`${point.x},${point.y}`)) effective.push(tile);
    }
    const canvas = this.createTileLodChunk(effective, chunkX, chunkY); overlay.chunks.set(key, canvas); return canvas;
  }
  private createTileLodChunk(cells: Cell[], chunkX: number, chunkY: number): HTMLCanvasElement | null {
    const canvas = document.createElement('canvas'); canvas.width = canvas.height = TILE_CHUNK_SIZE;
    const context = canvas.getContext('2d', { alpha: true })!, image = context.createImageData(TILE_CHUNK_SIZE, TILE_CHUNK_SIZE), pixels = image.data;
    let drawn = false;
    for (const tile of cells) {
      if (!this.groupVisible(tile.groupId || '')) continue;
      const localX = tile.x - chunkX * TILE_CHUNK_SIZE, localY = TILE_CHUNK_SIZE - 1 - (tile.y - chunkY * TILE_CHUNK_SIZE);
      if (localX < 0 || localY < 0 || localX >= TILE_CHUNK_SIZE || localY >= TILE_CHUNK_SIZE) continue;
      const color = this.materials.get(tile.material)?.rgba || [203, 85, 116, 255], offset = (localY * TILE_CHUNK_SIZE + localX) * 4;
      pixels[offset] = color[0]; pixels[offset + 1] = color[1]; pixels[offset + 2] = color[2]; pixels[offset + 3] = color[3]; drawn = true;
    }
    if (!drawn) return null;
    context.putImageData(image, 0, 0); return canvas;
  }
  private drawTileLodChunk(room: Room, source: HTMLCanvasElement, chunkX: number, chunkY: number): void {
    const rect = this.screenRect({ x: room.x + chunkX * TILE_CHUNK_SIZE, y: room.y + chunkY * TILE_CHUNK_SIZE,
      width: TILE_CHUNK_SIZE, height: TILE_CHUNK_SIZE });
    this.ctx.drawImage(source, rect.x, rect.y, rect.width, rect.height);
  }
  private drawObjectPlacementPreview(gesture: ObjectGesture): void {
    const room = this.state?.document.rooms.find(candidate => candidate.id === gesture.roomId), definition = this.definitions.get(definitionKey(gesture.definition));
    if (!room?.visible || !definition || room.locked || !this.memberEditable(gesture.layer, gesture.groupId, gesture.groupId)) return;
    let start = gesture.points[0], end = gesture.points[gesture.points.length - 1];
    const rectangle = definition.placement === 1;
    if (rectangle && definitionKey(definition.id) === 'portal') {
      if (start.x < 0 || start.y < 0 || start.x >= room.width || start.y >= room.height) return;
      const cell = (point: Point) => ({ x: Math.max(0, Math.min(room.width - 1, Math.floor(point.x))),
        y: Math.max(0, Math.min(room.height - 1, Math.floor(point.y))) });
      start = cell(start); end = cell(end);
    }
    const snap = (value: number) => Math.round(value * 16) / 16;
    const x = rectangle ? Math.floor(Math.min(start.x, end.x)) : snap(start.x);
    const y = rectangle ? Math.floor(Math.min(start.y, end.y)) : snap(start.y);
    const rawWidth = rectangle ? Math.abs(Math.floor(end.x) - Math.floor(start.x)) + 1 : definition.width;
    const rawHeight = rectangle ? Math.abs(Math.floor(end.y) - Math.floor(start.y)) + 1 : definition.height;
    const width = definition.resizable === false ? definition.width : Math.max(definition.minimumWidth, rawWidth);
    const height = definition.resizable === false ? definition.height : Math.max(definition.minimumHeight, rawHeight);
    const nodes: Point[] = [];
    if (definition.placement === 2) {
      for (let index = 0; index < (definition.minimumNodes || 0); index++) nodes.push({ x: x + index + 1, y });
      const endpoint = { x: snap(end.x), y: snap(end.y) };
      if (nodes.length) nodes[nodes.length - 1] = endpoint;
      else if (definition.maximumNodes !== 0) nodes.push(endpoint);
    }
    const preview: MapObject = { id: '', definition: definition.id, layer: gesture.layer, groupId: gesture.groupId,
      x, y, width, height, rotation: 0, scaleX: 1, scaleY: 1, nodes, properties: [] };
    this.drawObject(room, preview);
    if (nodes.length) this.drawSelectedObjectPath(room, preview, this.visibleWorldBounds());
    const valid = width > 0 && height > 0 && x >= 0 && y >= 0 && x + width <= room.width && y + height <= room.height;
    this.outline({ x: room.x + x, y: room.y + y, width, height }, valid ? '#72bde5' : '#dc6d72', false);
  }
  private drawObject(room: Room, object: MapObject): void {
    const key = definitionKey(object.definition), def = this.definitions.get(key), ctx = this.ctx;
    const color = this.definitionColors.get(key) || '#d4ac61';
    const selected = !this.cameraPreview && !this.gameCameraTool && this.selectedObjects.has(object.id);
    const center = this.toScreen({ x: room.x + object.x + object.width / 2, y: room.y + object.y + object.height / 2 });
    ctx.save(); ctx.translate(center.x, center.y); ctx.rotate(-(object.rotation || 0) * Math.PI / 180); ctx.scale(object.scaleX ?? 1, object.scaleY ?? 1);
    const rect = { x: -object.width * this.scale / 2, y: -object.height * this.scale / 2, width: object.width * this.scale, height: object.height * this.scale };
    if (!def?.sprite || !this.sprite(def.sprite, rect)) { const alpha = ctx.globalAlpha; ctx.fillStyle = color; ctx.globalAlpha = alpha * (object.layer === 3 || key === 'portal' ? .36 : .8); ctx.fillRect(rect.x, rect.y, rect.width, rect.height); ctx.globalAlpha = alpha; ctx.strokeStyle = color; ctx.strokeRect(rect.x, rect.y, rect.width, rect.height); }
    if (selected) { ctx.strokeStyle = '#ffe894'; ctx.lineWidth = 2 / Math.max(Math.abs(object.scaleX || 1), 1); ctx.strokeRect(rect.x - 1, rect.y - 1, rect.width + 2, rect.height + 2); }
    ctx.restore();
    const description = object.properties.find(p => p.key === 'desc')?.value.trim();
    if (!this.cameraPreview && description && this.scale >= 8) {
      const normalized = description.replace(/\s+/g, ' ');
      const label = normalized.length > 32 ? normalized.slice(0, 31) + '…' : normalized;
      ctx.save(); ctx.font = '11px system-ui'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.lineWidth = 3; ctx.strokeStyle = '#111111'; ctx.fillStyle = '#ffffff';
      ctx.strokeText(label, center.x, center.y); ctx.fillText(label, center.x, center.y); ctx.restore();
    }
  }
  private drawSelectedObjectPaths(view: Rect, hidden: Set<string> | undefined, hiddenLayers: number[]): void {
    for (const id of this.selectedObjects) {
      const indexed = this.objectById.get(id), object = indexed?.entry.object, room = indexed ? this.roomById.get(indexed.roomId) : undefined;
      if (!object || !room?.visible || hidden?.has(id) || hiddenLayers.includes(object.layer) || !this.groupVisible(object.groupId)) continue;
      this.ctx.save(); this.ctx.globalAlpha = roomOpacity(room.id, this.state?.selection.roomId);
      this.drawSelectedObjectPath(room, object, view);
      this.ctx.restore();
    }
  }
  private drawSelectedObjectPath(room: Room, object: MapObject, view: Rect): void {
    if (!object.nodes.length) return;
    const localView = { x: view.x - room.x, y: view.y - room.y, width: view.width, height: view.height };
    const margin = 12 / Math.max(this.scale, MIN_PIXEL_SCALE);
    const expanded = { x: localView.x - margin, y: localView.y - margin,
      width: localView.width + margin * 2, height: localView.height + margin * 2 };
    const index = this.objectPathIndex(object), ctx = this.ctx;
    ctx.strokeStyle = '#ffd982'; ctx.lineWidth = 1; ctx.setLineDash([4, 3]); ctx.beginPath();
    for (const nodeIndex of this.pathCandidates(index.segments, expanded, index.spanningSegments)) {
      const from = nodeIndex ? object.nodes[nodeIndex - 1] : { x: object.x + object.width / 2, y: object.y + object.height / 2 };
      const to = object.nodes[nodeIndex];
      if (!to || !segmentIntersectsRect(from, to, expanded)) continue;
      const a = this.toScreen({ x: room.x + from.x, y: room.y + from.y });
      const b = this.toScreen({ x: room.x + to.x, y: room.y + to.y });
      ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y);
    }
    ctx.stroke(); ctx.setLineDash([]);
    for (const nodeIndex of this.pathCandidates(index.nodes, expanded)) {
      const node = object.nodes[nodeIndex];
      if (!node || node.x < expanded.x || node.x > expanded.x + expanded.width
        || node.y < expanded.y || node.y > expanded.y + expanded.height) continue;
      const p = this.toScreen({ x: room.x + node.x, y: room.y + node.y });
      ctx.fillStyle = '#ffdc83'; ctx.fillRect(p.x - 4, p.y - 4, 8, 8);
      ctx.fillStyle = '#151a22'; ctx.font = '10px system-ui'; ctx.fillText(String(nodeIndex + 1), p.x + 7, p.y + 4);
    }
  }
  private objectPathIndex(object: MapObject): ObjectPathIndex {
    const cached = this.objectPaths.get(object); if (cached) return cached;
    const result: ObjectPathIndex = { nodes: new Map(), segments: new Map(), spanningSegments: [] };
    const add = (chunks: Map<string, number[]>, key: string, index: number) => {
      let values = chunks.get(key); if (!values) { values = []; chunks.set(key, values); } values.push(index);
    };
    let previous: Point = { x: object.x + object.width / 2, y: object.y + object.height / 2 };
    for (let index = 0; index < object.nodes.length; index++) {
      const node = object.nodes[index]; add(result.nodes, chunkKey(node.x, node.y), index);
      const left = Math.floor(Math.min(previous.x, node.x) / TILE_CHUNK_SIZE), right = Math.floor(Math.max(previous.x, node.x) / TILE_CHUNK_SIZE);
      const bottom = Math.floor(Math.min(previous.y, node.y) / TILE_CHUNK_SIZE), top = Math.floor(Math.max(previous.y, node.y) / TILE_CHUNK_SIZE);
      if ((right - left + 1) * (top - bottom + 1) > MAX_PATH_SEGMENT_CHUNKS) result.spanningSegments.push(index);
      else for (let y = bottom; y <= top; y++) for (let x = left; x <= right; x++) add(result.segments, `${x},${y}`, index);
      previous = node;
    }
    this.objectPaths.set(object, result); return result;
  }
  private pathCandidates(chunks: Map<string, number[]>, bounds: Rect, initial: number[] = []): number[] {
    const result = new Set(initial);
    const x0 = Math.floor(bounds.x / TILE_CHUNK_SIZE), x1 = Math.floor((bounds.x + bounds.width) / TILE_CHUNK_SIZE);
    const y0 = Math.floor(bounds.y / TILE_CHUNK_SIZE), y1 = Math.floor((bounds.y + bounds.height) / TILE_CHUNK_SIZE);
    const chunkCount = Math.max(0, x1 - x0 + 1) * Math.max(0, y1 - y0 + 1);
    if (chunkCount > Math.max(64, chunks.size * 2)) for (const values of chunks.values()) for (const index of values) result.add(index);
    else for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++)
      for (const index of chunks.get(`${x},${y}`) || []) result.add(index);
    return [...result];
  }
  private sprite(sprite: SpriteRect, rect: Rect, ctx = this.ctx): boolean {
    const image = this.images.get(sprite.asset, this.imageChanged);
    if (!image?.naturalWidth) return false;
    ctx.drawImage(image, sprite.x, image.naturalHeight - sprite.y - sprite.height, sprite.width, sprite.height, rect.x, rect.y, rect.width, rect.height); return true;
  }
  dispose(): void { this.cancel(); this.active = false; this.resetWheel(); this.abort.abort(); this.resize.disconnect(); this.images.clear(); cancelAnimationFrame(this.raf); this.clearExactTileChunks(); }
}
