import { activeRoom } from './types.js';
import type { Point } from './types.js';
import type { MapCanvas } from './map-canvas.js';

/** A view of the shared renderer; owns no map data, tile indexes or editing commands. */
export class GamePreview {
  readonly element = document.createElement('section');
  private canvas = document.createElement('canvas');
  private title = document.createElement('strong');
  private roomName = document.createElement('span');
  private info = document.createElement('span');
  private collapseButton = document.createElement('button');
  private maximizeButton = document.createElement('button');
  private centerButton = document.createElement('button');
  private body = document.createElement('div');
  private source: MapCanvas;
  private t: (key: string) => string;
  private returnFocus: () => void;
  private resize: ResizeObserver;
  private abort = new AbortController();
  private raf = 0;
  private visible = true;
  private collapsed = localStorage.getItem('metroidvaniaStudio.previewCollapsed') === 'true';
  maximized = false;
  private stamp = '';
  private roomStamp = '';
  private center: Point = { x: 0, y: 0 };
  private scale = 16;
  private pan: { id: number; x: number; y: number; center: Point } | null = null;

  constructor(source: MapCanvas, t: (key: string) => string, returnFocus: () => void) {
    this.source = source; this.t = t; this.returnFocus = returnFocus;
    const header = document.createElement('header'), footer = document.createElement('footer');
    this.element.id = 'game-preview'; this.element.className = 'game-preview';
    this.element.setAttribute('aria-label', 'Game Preview');
    this.title.textContent = 'Game Preview'; this.roomName.className = 'preview-room';
    this.body.id = 'game-preview-body'; this.body.className = 'preview-body';
    this.canvas.id = 'game-preview-canvas'; this.canvas.tabIndex = 0;
    const button = (element: HTMLButtonElement, id: string, symbol: string, action: () => void) => {
      element.type = 'button'; element.id = id; element.textContent = symbol; element.addEventListener('click', action);
    };
    button(this.collapseButton, 'preview-collapse', '−', () => this.setCollapsed(!this.collapsed));
    button(this.maximizeButton, 'preview-maximize', '□', () => this.setMaximized(!this.maximized));
    button(this.centerButton, 'preview-center', '⌾', () => this.frameRoom());
    this.collapseButton.setAttribute('aria-controls', this.body.id);
    header.append(this.title, this.roomName, this.collapseButton, this.maximizeButton);
    footer.append(this.info, this.centerButton); this.body.append(this.canvas, footer); this.element.append(header, this.body);
    this.resize = new ResizeObserver(() => this.requestDraw(true)); this.resize.observe(this.canvas);
    const options = { signal: this.abort.signal };
    this.canvas.addEventListener('contextmenu', event => event.preventDefault(), options);
    this.canvas.addEventListener('wheel', event => event.preventDefault(), { ...options, passive: false });
    this.canvas.addEventListener('pointerdown', event => {
      event.preventDefault(); this.canvas.focus();
      if (this.pan || ![0, 1, 2].includes(event.button)) return;
      this.pan = { id: event.pointerId, x: event.clientX, y: event.clientY, center: { ...this.center } };
      this.canvas.setPointerCapture(event.pointerId); this.canvas.classList.add('panning');
    }, options);
    this.canvas.addEventListener('pointermove', event => {
      const pan = this.pan; if (!pan || pan.id !== event.pointerId) return;
      this.center = { x: pan.center.x - (event.clientX - pan.x) / this.scale, y: pan.center.y + (event.clientY - pan.y) / this.scale };
      this.requestDraw(true);
    }, options);
    for (const name of ['pointerup', 'pointercancel', 'lostpointercapture'])
      this.canvas.addEventListener(name, () => this.cancelPan(), options);
    window.addEventListener('blur', () => this.cancelPan(), options);
    this.element.addEventListener('keydown', event => {
      // Preview cannot dispatch editor shortcuts such as Delete, paste or brush selection.
      if (event.ctrlKey || event.metaKey) { if (event.key.toLowerCase() === 's') return; }
      if (event.key === 'Escape') { event.preventDefault(); this.cancelPan(); if (this.maximized) this.setMaximized(false); this.returnFocus(); }
      else if (event.key.toLowerCase() === 'f') { event.preventDefault(); this.frameRoom(); }
      else if (event.key !== 'Tab' && !(event.target instanceof HTMLButtonElement && ['Enter', ' '].includes(event.key))) event.preventDefault();
      event.stopPropagation();
    }, options);
    source.onPreviewChange = () => this.requestDraw();
    this.updateLabels(); this.applyLayout();
  }
  updateLabels(): void {
    this.canvas.setAttribute('aria-label', this.t('previewHelp'));
    this.canvas.title = this.t('previewHelp');
    for (const [button, key] of [[this.collapseButton, this.collapsed ? 'previewExpand' : 'previewCollapse'],
      [this.maximizeButton, this.maximized ? 'previewRestore' : 'previewMaximize'], [this.centerButton, 'frameRoom']] as const) {
      button.title = this.t(key); button.setAttribute('aria-label', this.t(key));
    }
    this.requestDraw(true);
  }
  setVisible(visible: boolean): void {
    this.visible = visible; this.element.hidden = !visible;
    if (!visible) { this.cancelPan(); cancelAnimationFrame(this.raf); this.raf = 0; }
    else this.requestDraw(true);
  }
  private setCollapsed(collapsed: boolean): void {
    this.cancelPan(); this.collapsed = collapsed;
    if (collapsed) this.maximized = false;
    localStorage.setItem('metroidvaniaStudio.previewCollapsed', String(collapsed));
    this.applyLayout();
  }
  setMaximized(maximized: boolean): void {
    this.cancelPan(); this.maximized = maximized;
    if (maximized) { this.collapsed = false; localStorage.setItem('metroidvaniaStudio.previewCollapsed', 'false'); }
    this.applyLayout();
  }
  private applyLayout(): void {
    this.element.classList.toggle('collapsed', this.collapsed); this.element.classList.toggle('maximized', this.maximized);
    this.body.hidden = this.collapsed;
    this.collapseButton.textContent = this.collapsed ? '+' : '−';
    this.maximizeButton.textContent = this.maximized ? '❐' : '□';
    this.collapseButton.setAttribute('aria-expanded', String(!this.collapsed));
    this.maximizeButton.setAttribute('aria-pressed', String(this.maximized));
    if (this.collapsed) { cancelAnimationFrame(this.raf); this.raf = 0; }
    this.updateLabels();
  }
  frameRoom(): void {
    const room = activeRoom(this.source.state);
    this.center = room ? { x: room.x + room.width / 2, y: room.y + room.height / 2 } : { x: 0, y: 0 };
    this.requestDraw(true);
  }
  private cancelPan(): void {
    const pan = this.pan; this.pan = null; this.canvas.classList.remove('panning');
    if (pan && this.canvas.hasPointerCapture(pan.id)) this.canvas.releasePointerCapture(pan.id);
  }
  requestDraw(force = false): void {
    if (!this.visible) return;
    const stamp = this.source.previewVersion;
    if (!force && stamp === this.stamp) return;
    this.stamp = stamp;
    // Keep pointer input free of DOM writes and layout. Coalesce live edits into one
    // secondary pass after the editor has drawn its immediate brush damage.
    if (this.raf) return;
    this.raf = requestAnimationFrame(() => {
      this.raf = 0;
      const room = activeRoom(this.source.state), profile = this.source.cameraProfile;
      const roomStamp = room ? `${this.source.state?.instanceId}:${room.id}:${room.x}:${room.y}:${room.width}:${room.height}` : '';
      if (roomStamp !== this.roomStamp) {
        this.cancelPan(); this.roomStamp = roomStamp;
        this.center = room ? { x: room.x + room.width / 2, y: room.y + room.height / 2 } : { x: 0, y: 0 };
      }
      const name = room?.name || this.t('previewNoRoom');
      if (this.roomName.textContent !== name) { this.roomName.textContent = name; this.roomName.title = name; }
      this.centerButton.disabled = !room;
      const aspect = profile ? `${profile.referenceWidth} / ${profile.referenceHeight}` : '16 / 9';
      if (this.element.style.getPropertyValue('--preview-aspect') !== aspect) this.element.style.setProperty('--preview-aspect', aspect);
      if (this.collapsed) return;
      const result = this.source.renderGamePreview(this.canvas, this.center);
      if (!result) return;
      this.scale = result.tileScale;
      const camera = this.source.cameraProfile;
      this.info.textContent = camera ? `${camera.referenceWidth}×${camera.referenceHeight} · PPU ${camera.ppu} · ×${Number(result.pixelScale.toFixed(3))}` : this.t('previewNoRoom');
    });
  }
  dispose(): void {
    this.source.onPreviewChange = null; this.cancelPan(); this.abort.abort(); this.resize.disconnect(); cancelAnimationFrame(this.raf);
  }
}
