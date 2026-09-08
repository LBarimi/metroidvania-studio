import { renderPaletteGroups, paletteGroupName, expandPaletteGroup } from './palette-groups.js';
import type { EditorPaletteGroup } from './types.js';
import { openPaletteDialog } from './palette-dialog.js';
import { AssetImages } from './asset-images.js';
import { pickMapFile, pickMapSave, fileHash, writeMapFile, downloadMap, canceledFileDialog } from './file-access.js';
import type { MapFileHandle } from './file-access.js';
import { initializeWorkspaceFolders, openWorkspaceFolders } from './workspace-folders.js';
import { openScriptDialog } from './script-dialog.js';
import { EditorApi, EditorApiError, localJson } from './api.js';
import { createCameraControls } from './camera-controls.js';
import { Locale, LANGUAGES } from './locale.js';
import type { Language } from './locale.js';
import { syncLabels } from './sync-status.js';
import { MapCanvas } from './map-canvas.js';
import { MiniMap, roomColor } from './minimap.js';
import { activeRoom, colorCss, tileLayer, TOOLS, LAYERS, SHAPES, MAX_BRUSH_SIZE } from './types.js';
import type { State, Room, Point, SpriteRect, Property, MapObject, CommandExpectation, CommandValues, Definition, Material } from './types.js';

const api = new EditorApi(), locale = new Locale();
let standalone = new URLSearchParams(location.search).get('view') === 'minimap';
let miniMode = standalone, inspectorHidden = localStorage.getItem('mapstudio.inspectorHidden') === 'true';
let lastNotice = '';
let clipboardScope: 'room' | 'content' = 'content';
let inspectorSelectionKey = '';
let inspectorSelectionIds: string[] = [], inspectorSelectionVersion = 0, inspectorObjectVersion = 0;
let inspectorObjectSource: MapObject[] | null = null;
let inspectorObjectIndex = new Map<string, MapObject>();
let state: State | null = null, roomSearch = '', paletteSearch = '', uiRaf = 0, statusRaf = 0, toastTimer = 0, lastInspectorKey = '', lastPanelKey = '', languageVersion = 0;
let pendingStatusPoint: Point | null = null;
const app = document.getElementById('app')!;
app.className = 'app' + (standalone ? ' standalone-app' : '');
// This template contains only application-owned markup. Project text is always assigned with textContent/value.
app.innerHTML = `<header class="topbar"><div class="brand"><img class="brand-mark" src="studio-icon.svg" alt=""><span class="brand-text">Metroidvania Studio</span></div><div class="top-actions" id="file-actions"></div><div class="spacer"></div><div class="project-name" id="project-name"></div><span class="connection" id="connection"></span><select class="language" id="language" aria-label="Language"><option value="KR">한국어</option><option value="EN">English</option></select></header><nav class="tabbar" id="tabs"></nav><main class="workspace" id="workspace"><aside class="sidebar"><section class="section"><div class="section-title"><span data-label="rooms"></span><span class="count" id="room-count"></span><button id="add-room" class="icon">+</button></div><input id="room-search" class="room-search"><div class="room-list" id="room-list"></div></section><section class="section"><div class="section-title" data-label="tools"></div><div class="tool-grid" id="tools"></div></section><section class="section"><div class="section-title" data-label="layers"></div><div id="layers"></div><select id="group-select"></select><div class="row" id="group-actions"></div></section><section class="section"><div class="section-title"><span data-label="palette"></span><button id="add-palette" class="icon">+</button></div><div id="brush-options"></div><input id="palette-search" class="room-search"><div class="palette-list" id="palette"></div></section></aside><section class="content"><div class="view-toolbar" id="view-toolbar"></div><div class="canvas-wrap"><canvas class="map-canvas" id="map-canvas"></canvas><canvas class="mini-canvas" id="mini-canvas" hidden></canvas><div class="canvas-help" id="canvas-help"></div></div></section><aside class="inspector" id="inspector"></aside></main><footer class="statusbar"><span class="status-state" id="status-state"></span><span class="sync-status" id="status-export" aria-live="polite"></span><span id="status-coordinates"></span><span id="status-layer"></span><span id="status-room-sizes"></span><div class="spacer"></div><span id="status-camera"></span><span id="status-revision"></span></footer><div class="error-banner" id="toast" hidden role="status"></div>`;
function el<T extends HTMLElement = HTMLElement>(id: string): T { return document.getElementById(id) as T; }
function text(tag: string, value: string, className = ''): HTMLElement { const node = document.createElement(tag); node.textContent = value; node.className = className; return node; }
function button(label: string, action: () => void | Promise<unknown>, className = ''): HTMLButtonElement { const b = document.createElement('button'); b.type = 'button'; b.textContent = label; b.className = className; b.addEventListener('click', () => { Promise.resolve().then(action).catch(error => toast(error)); }); return b; }
function labelInput(caption: string, value: string | number, type = 'text'): [HTMLLabelElement, HTMLInputElement] { const label = document.createElement('label'), input = document.createElement('input'); label.append(text('span', caption)); input.type = type; input.value = String(value); label.append(input); return [label, input]; }
function numberValue(input: HTMLInputElement): number {
  const raw = input.value.trim(), value = Number(raw);
  if (!raw || !Number.isFinite(value)) throw new Error(locale.t('required'));
  return value;
}
function numberValues(inputs: Map<string, HTMLInputElement>): Record<string, number> {
  return Object.fromEntries([...inputs].map(([key, input]) => [key, numberValue(input)]));
}
function check(caption: string, value: boolean, change?: (checked: boolean) => void): [HTMLLabelElement, HTMLInputElement] { const label = document.createElement('label'), input = document.createElement('input'); label.className = 'check'; input.type = 'checkbox'; input.checked = value; label.append(input, text('span', caption)); if (change) input.addEventListener('change', () => change(input.checked)); return [label, input]; }
function select(options: Array<[string, string]>, value: string): HTMLSelectElement { const s = document.createElement('select'); for (const [id, name] of options) { const o = document.createElement('option'); o.value = id; o.textContent = name; s.append(o); } s.value = value; return s; }
function section(title: string): HTMLElement { const div = text('section', '', 'section'); div.append(text('div', title, 'section-title')); return div; }
function row(...children: Node[]): HTMLElement { const div = text('div', '', 'row'); div.append(...children); return div; }
function propObject(properties: Property[]): Record<string, string> { return Object.fromEntries(properties.map(p => [p.key, p.value])); }
function properties(value: unknown): Property[] { if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(locale.t('invalidJson')); return Object.entries(value).map(([key, value]) => ({ key, value: String(value) })); }
function definitionKey(value: string): string { return value.toLowerCase(); }
function definitionName(value: Definition): string {
  if (value.name !== value.id) return value.name;
  const key = 'builtin.object.' + value.id, translated = locale.t(key);
  return translated === key ? value.name : translated;
}
const defaultThemeNames: Record<string, [string, string]> = {
  terrain: ['Green', 'green'], 'terrain-blue': ['Blue', 'blue'], 'terrain-dark-gray': ['Dark Gray', 'darkGray'],
  'terrain-orange': ['Orange', 'orange'], 'terrain-yellow': ['Yellow', 'yellow'],
  'biome-grassland': ['Grassland', 'grassland'], 'biome-rock': ['Rock', 'rock'],
  'biome-ice-cavern': ['Ice cavern', 'iceCavern'], 'biome-volcanic': ['Volcanic', 'volcanic'],
  'biome-ancient-ruins': ['Ancient ruins', 'ancientRuins']
};
function materialName(value: Material): string {
  const entry = defaultThemeNames[value.themeId || value.id];
  if (!entry || value.name !== entry[0]) return value.name;
  const key = 'stageThemes.' + entry[1], translated = locale.t(key);
  return translated === key ? value.name : translated;
}

function toast(error: unknown, success = false): void { const t = el('toast'); const message = error instanceof Error ? error.message : String(error); const [key, ...args] = message.slice(1).split(':'); t.textContent = message.startsWith('@') ? locale.t(key).replace(/\{(\d+)\}/g, (_, index) => args[Number(index)] || '') : message; t.classList.toggle('success', success); t.hidden = false; clearTimeout(toastTimer); toastTimer = window.setTimeout(() => t.hidden = true, success ? 3500 : 9000); }
const map = new MapCanvas(el('map-canvas'), api.command, point => drawStatus(point), () => { inspectorHidden = false; updateView(); renderInspector(true); }, key => toast(locale.t(key)), () => api.pending > 0, () => api.refresh(false), size => {
  const brush = el('brush-options').querySelector<HTMLInputElement>('input[type="number"]');
  if (brush) brush.value = String(size);
}, (point, client) => roomContextMenu(point, client));
const mini = new MiniMap(el('mini-canvas'),
  id => { void run('selectRoom', { id }).catch(() => undefined); },
  id => { void editMiniRoom(id).catch(error => toast(error)); });
async function editMiniRoom(id: string): Promise<void> {
  const selected = await run('selectRoom', { id });
  if (!miniMode || selected.selection.roomId !== id || state?.selection.roomId !== id) return;
  if (standalone) {
    standalone = false;
    const url = new URL(location.href); url.searchParams.delete('view'); history.replaceState(null, '', url);
  }
  miniMode = false; map.gameView(false); map.selectRoomTarget(id); updateView();
  map.frameRoom(true); el('map-canvas').focus();
}
function expectedAt(value: State): CommandExpectation { return { instanceId: value.instanceId, revision: value.revision }; }
async function run(action: string, values: CommandValues = {}, expectation?: CommandExpectation): Promise<State> { await map.settled(); return api.command(action, values, expectation); }
async function settleFileSnapshot(): Promise<void> { await map.settled(); await api.settled(); }
async function option(values: CommandValues): Promise<void> {
  map.cancel(); await run('options', () => {
    const next = { ...(typeof values === 'function' ? values() : values) } as Record<string, unknown>;
    if (typeof next.layer === 'number' && next.tool === undefined) {
      const current = state?.selection.tool ?? 3;
      next.tool = tileLayer(next.layer) ? current === 1 ? 3 : current : current === 0 || current === 2 ? current : next.layer === 6 ? 2 : 1;
    } return next;
  });
}
function showModal(title: string, content: (body: HTMLElement) => void, apply?: () => Promise<unknown>, action = 'apply'): HTMLDialogElement {
  const dialog = document.createElement('dialog'); const heading = text('div', title, 'dialog-title'), body = text('div', '', 'dialog-body'), footer = text('div', '', 'dialog-footer'), error = text('div', '', 'modal-error');
  heading.append(button('×', () => dialog.close(), 'icon ghost')); content(body); body.append(error); footer.append(button(locale.t(apply ? 'cancel' : 'close'), () => dialog.close()));
  if (apply) { const submit = button(locale.t(action), async () => { submit.disabled = true; error.textContent = ''; try { if (await apply() !== false) dialog.close(); } catch (e) { const message = e instanceof Error ? e.message : String(e); const [key, ...args] = message.slice(1).split(':'); error.textContent = message.startsWith('@') ? locale.t(key).replace(/\{(\d+)\}/g, (_, index) => args[Number(index)] || '') : message; } finally { submit.disabled = false; } }, 'accent'); footer.append(submit); }
  dialog.append(heading, body, footer); dialog.addEventListener('close', () => dialog.remove()); document.body.append(dialog); dialog.showModal(); return dialog;
}
async function confirmAction(message: string): Promise<boolean> { return new Promise(resolve => { let accepted = false; const dialog = showModal(locale.t('confirm'), body => body.append(text('p', message)), async () => { accepted = true; }, 'continue'); dialog.addEventListener('close', () => resolve(accepted)); }); }
async function confirmRun(message: string, action: string, values: object = {}, expectation?: CommandExpectation): Promise<void> {
  await map.settled(); const expected = expectation || (state ? expectedAt(state) : undefined);
  if (await confirmAction(message)) await run(action, values, expected);
}
function isExistingTargetConflict(error: unknown): error is EditorApiError {
  return error instanceof EditorApiError && (error.status === 400 || error.status === 409)
    && /already exists|exists at|confirm overwrite|이미\s*있|덮어쓰/iu.test(error.message);
}
async function runFileAction(action: 'save' | 'exportRooms', values: Record<string, unknown>, expectation: CommandExpectation | undefined, target: string): Promise<boolean> {
  try { await run(action, values, expectation); }
  catch (error) {
    if (!isExistingTargetConflict(error)) throw error;
    if (!await confirmAction(`${locale.t('confirmOverwrite')}\n${target}`)) return false;
    await run(action, { ...values, overwrite: true }, expectation);
  }
  return true;
}
async function fileDialog(action: string, scope = 'all'): Promise<void> {
  await settleFileSnapshot();
  const snapshot = state, expectation = snapshot ? expectedAt(snapshot) : undefined;
  if (action === 'new' && snapshot?.dirty && !await confirmAction(locale.t('confirmDiscard'))) return;
  let input: HTMLInputElement;
  const dialog = showModal(locale.t(action === 'exportRooms' ? scope === 'selected' ? 'exportSelected' : scope === 'changed' ? 'exportChanged' : 'exportAll' : action), body => {
    const [label, field] = labelInput(locale.t(action === 'new' ? 'name' : 'exportDirectory'), action === 'new' ? '' : 'Exports');
    input = field; body.append(label); if (action === 'exportRooms') body.append(text('p', locale.t('exportHelp')));
  }, async () => {
    const value = input.value.trim(); if (!value) throw new Error(locale.t('required')); map.cancel();
    if (action === 'new') { await run('new', { name: value, discard: true }, expectation); map.frameRoom(); mini.fit(); }
    else if (!await runFileAction('exportRooms', { directory: value, scope }, expectation, value)) return false;
    toast(locale.t('changeApplied'), true); return true;
  }, action === 'new' ? 'new' : 'export');
  dialog.addEventListener('keydown', e => { if (e.key === 'Enter' && e.target instanceof HTMLInputElement) { e.preventDefault(); (dialog.querySelector('.accent') as HTMLButtonElement).click(); } });
}
let attachedFile: { instanceId: string; id: string; handle: MapFileHandle; hash: string } | undefined;
let fileBusy = false;
async function openMap(): Promise<void> {
  if (fileBusy || !state) return;
  fileBusy = true; let choosing = true;
  try {
    const chosen = await pickMapFile(key => locale.t(key)); choosing = false; if (!chosen) return;
    if (chosen.file.size > 32 * 1024 * 1024) throw new Error(locale.t('importTooLarge'));
    const document = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(await chosen.file.arrayBuffer()));
    const hash = await fileHash(chosen.file);
    await settleFileSnapshot(); const snapshot = state;
    if (snapshot.dirty && !await confirmAction(locale.t('confirmDiscard'))) return;
    const next = await run('browserOpen', { document, fileName: chosen.file.name, discard: true }, expectedAt(snapshot));
    attachedFile = chosen.handle ? { instanceId: next.instanceId, id: next.browserFileId!, handle: chosen.handle, hash } : undefined;
    map.frameRoom(); mini.fit();
  } catch (error) { if (!choosing || !canceledFileDialog(error)) toast(error); }
  finally { fileBusy = false; }
}
async function save(saveAs = false): Promise<void> {
  if (fileBusy || !state) return;
  fileBusy = true; let choosing = true;
  try {
    const previous = !saveAs && attachedFile?.instanceId === state.instanceId && attachedFile?.id === state.browserFileId ? attachedFile : undefined;
    const fileName = state.file?.split('/').pop() || state.document.name.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '-').trim() + '.map.json';
    const handle = await pickMapSave(fileName, previous?.handle, key => locale.t(key)); choosing = false;
    const hash = previous?.hash ?? (handle ? await fileHash(await handle.getFile()) : '');
    await settleFileSnapshot(); const snapshot = state;
    const json = JSON.stringify(snapshot.document);
    if (!handle) { downloadMap(json, fileName); toast(locale.t('fileDownloadStarted'), true); return; }
    const savedHash = await writeMapFile(handle, json, hash);
    if (previous) previous.hash = savedHash;
    const next = await run('browserSave', { fileName: handle.name, browserFileId: previous?.id ?? null }, expectedAt(snapshot));
    attachedFile = { instanceId: next.instanceId, id: next.browserFileId!, handle, hash: savedHash };
    toast(locale.t('saved'), true);
  } catch (error) { if (!choosing || !canceledFileDialog(error)) toast(error); }
  finally { fileBusy = false; }
}

async function openSampleWorld(): Promise<void> {
  await settleFileSnapshot(); const snapshot = state; if (!snapshot) return;
  if (snapshot.dirty && !await confirmAction(locale.t('confirmDiscard'))) return;
  await run('sampleWorld', { discard: true }, expectedAt(snapshot));
  if (standalone) { standalone = false; const url = new URL(location.href); url.searchParams.delete('view'); history.replaceState(null, '', url); }
  miniMode = false; map.gameView(false); updateView();
  requestAnimationFrame(() => { map.fit(); mini.fit(); });
}
function importDocument(): void {
  const input = document.createElement('input'); input.type = 'file'; input.accept = '.json,application/json';
  input.id = 'json-import'; input.hidden = true; document.body.append(input);
  input.addEventListener('cancel', () => input.remove(), { once: true });
  input.addEventListener('change', () => {
    void (async () => {
      try {
        const file = input.files?.[0]; if (!file) return;
        if (file.size > 32 * 1024 * 1024) throw new Error(locale.t('importTooLarge'));
        const document = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer()));
        if (!document || typeof document !== 'object' || Array.isArray(document)) throw new Error(locale.t('invalidJson'));
        await settleFileSnapshot(); const snapshot = state;
        if (!snapshot) return;
        if (snapshot.dirty && !await confirmAction(locale.t('confirmDiscard'))) return;
        await run('import', { document, discard: true }, expectedAt(snapshot)); map.frameRoom();
      } catch (error) { toast(error); }
      finally { input.remove(); }
    })();
  }, { once: true });
  input.click();
}
function importRooms(): void {
  const picker = document.createElement('input'); picker.type = 'file'; picker.accept = '.json,application/json'; picker.multiple = true;
  picker.id = 'room-json-import'; picker.hidden = true; document.body.append(picker);
  picker.addEventListener('cancel', () => picker.remove(), { once: true });
  picker.addEventListener('change', () => { void (async () => {
    try {
      const files = [...(picker.files || [])]; if (!files.length) return;
      if (files.reduce((sum, file) => sum + file.size, 0) > 32 * 1024 * 1024) throw new Error(locale.t('importTooLarge'));
      const documents = await Promise.all(files.map(async file => JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer()))));
      await settleFileSnapshot(); if (!state) return;
      const existing = new Set(state.document.rooms.map(room => room.id));
      const overwrite = documents.some(document => Array.isArray(document?.rooms) && document.rooms.some((room: Room) => existing.has(room.id)));
      if (overwrite && !await confirmAction(locale.t('confirmRoomReplace'))) return;
      await run('importRooms', { documents, overwrite }, expectedAt(state)); map.selectRoomTarget(state?.selection.roomId || null); map.frameRoom();
    } catch (error) { toast(error); } finally { picker.remove(); }
  })(); }, { once: true }); picker.click();
}
function shortcutsDialog(): void {
  const mod = /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘' : 'Ctrl';
  const bindings: [string, string][] = [
    ['shortcutDraw', locale.t('keyLeftDrag')],
    ['shortcutEraseRoom', locale.t('keyRight')],
    ['shortcutPan', locale.t('keyMiddleDrag')],
    ['shortcutPanEmpty', locale.t('keyRightDrag')],
    ['shortcutRoomMenu', locale.t('keyRight')],
    ['zoom', locale.t('keyWheel')],
    ['shortcutBrushSize', `Ctrl + ${locale.t('keyWheel')}`],
    ['frameRoom', 'F'],
    ['shortcutMoveRoom', locale.t('keyRoomTitleDrag')],
    ['shortcutResizeRoom', locale.t('keyRoomHandleDrag')],
    ['shortcutSelectRoom', locale.t('keyOtherRoomClick')],
    ['shortcutRoomMultiSelect', `Ctrl / Cmd + ${locale.t('keyLeftClick')}`],
    ['shortcutRoomGroupMove', locale.t('keyLeftDrag')],
    ['shortcutTileSelection', locale.t('keyLeftDrag')],
    ['shortcutMoveSelection', locale.t('keySelectionDrag')],
    ['shortcutNewSelection', `Shift + ${locale.t('keyLeftDrag')}`],
    ['shortcutPlaceObject', locale.t('keyPaletteClick')],
    ['shortcutSizeObject', locale.t('keyLeftDrag')],
    ['shortcutObjectProperties', `${mod} + ${locale.t('keyRight')}`],
    ['shortcutSelectNode', locale.t('keyNodeClick')],
    ['save', `${mod} + S`], ['saveAs', `${mod} + Shift + S`],
    ['undo', `${mod} + Z`], ['redo', `${mod} + Y / ${mod} + Shift + Z`],
    ['copy', `${mod} + C`], ['cut', `${mod} + X`], ['paste', `${mod} + V`],
    ['selectAll', `${mod} + A`], ['shortcutDeleteSelected', 'Delete / Backspace'],
    ['cancel', 'Esc'],
    ...TOOLS.map((name, index): [string, string] => [`enum.MetroidvaniaStudioTool.${name}`, toolKeys[index].toUpperCase()]),
    ['file', 'Alt + F'], ['edit', 'Alt + E'], ['helpMenu', 'Alt + H']
  ];
  const dialog = showModal(locale.t('shortcut'), body => {
    body.tabIndex = 0; body.setAttribute('aria-label', locale.t('shortcut')); body.setAttribute('role', 'region');
    const list = document.createElement('dl'); list.className = 'shortcut-list';
    for (const [action, binding] of bindings) {
      const row = text('div', '', 'shortcut-row'), key = text('dd', '', 'shortcut-binding');
      key.append(text('kbd', binding)); row.append(text('dt', locale.t(action)), key); list.append(row);
    }
    body.append(list);
  });
  dialog.classList.add('shortcuts-dialog'); dialog.id = 'shortcuts-dialog';
}
async function aboutDialog(): Promise<void> {
  let info: { version?: string; revision?: string; builtAt?: string } = {};
  try { info = await localJson('/build-info.json'); } catch { /* Source previews can omit build metadata. */ }
  showModal(locale.t('about'), body => {
    const icon = document.createElement('img'); icon.src = 'studio-icon.svg'; icon.alt = ''; icon.width = icon.height = 80;
    const head = text('div', '', 'about-heading'); head.append(icon, text('strong', 'Metroidvania Studio')); body.append(head, text('p', locale.t('aboutDescription')));
    body.append(text('div', `${locale.t('buildVersion')}: ${info.version || locale.t('unavailable')}`, 'build-version'));
    if (info.revision) body.append(text('div', `${locale.t('revision')}: ${info.revision}`));
    if (info.builtAt) body.append(text('div', `${locale.t('buildDate')}: ${new Date(info.builtAt).toLocaleString(locale.htmlLanguage)}`));
    const credits = text('div', '', 'about-credits');
    credits.append(text('div', 'Copyright ⓒ Barimi', 'about-copyright'), text('div', 'sdfsdgxc@naver.com', 'about-email'));
    body.append(credits);
  });
}
function menu(id: string, caption: string, items: (HTMLButtonElement | null)[]): HTMLElement {
  const root = text('div', '', 'menu'); root.id = id;
  const panel = text('div', '', 'menu-popup'); panel.setAttribute('role', 'menu'); panel.hidden = true;
  const trigger = button(caption, () => toggle()); trigger.setAttribute('aria-haspopup', 'menu'); trigger.setAttribute('aria-expanded', 'false');
  trigger.id = id + '-button'; panel.setAttribute('aria-labelledby', trigger.id);
  function toggle() {
    const open = panel.hidden; closeMenus(); panel.hidden = !open; trigger.setAttribute('aria-expanded', String(open));
    if (open) panel.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
  }
  for (const item of items) {
    if (!item) { const divider = document.createElement('hr'); divider.setAttribute('role', 'separator'); panel.append(divider); continue; }
    item.setAttribute('role', 'menuitem'); item.addEventListener('click', closeMenus); panel.append(item);
  }
  root.append(trigger, panel);
  root.addEventListener('keydown', event => {
    if (!['Escape', 'ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault(); event.stopPropagation();
    if (event.key === 'Escape') { closeMenus(); trigger.focus(); return; }
    if (panel.hidden) { toggle(); return; }
    const buttons = [...panel.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')], index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : (index + (event.key === 'ArrowUp' ? -1 : 1) + buttons.length) % buttons.length;
    buttons[next]?.focus();
  }); return root;
}
function closeMenus(): void {
  document.getElementById('room-context-menu')?.remove();
  for (const node of document.querySelectorAll<HTMLElement>('.menu-popup')) node.hidden = true;
  for (const node of document.querySelectorAll('.menu > button')) node.setAttribute('aria-expanded', 'false');
}
document.addEventListener('pointerdown', event => {
  if ((event.target as HTMLElement).closest('.menu')) return;
  const dismissCanvasMenu = document.getElementById('room-context-menu') && event.target === el('map-canvas') && event.button === 0;
  closeMenus();
  // Dismissing the menu must not also paint, activate, move or resize a room.
  if (dismissCanvasMenu) { event.preventDefault(); event.stopPropagation(); el('map-canvas').focus(); }
}, true);
window.addEventListener('blur', closeMenus);
window.addEventListener('resize', closeMenus);
el('map-canvas').addEventListener('wheel', closeMenus, { passive: true });
function roomContextMenu(point: Point, client: Point): void {
  if (!state || miniMode || map.cameraPreview) return;
  closeMenus();
  const instance = state.instanceId, documentRevision = state.documentRevision ?? state.revision;
  const root = text('div', '', 'menu canvas-context-menu'); root.id = 'room-context-menu';
  const panel = text('div', '', 'menu-popup'); panel.setAttribute('role', 'menu'); panel.setAttribute('aria-label', locale.t('rooms'));
  const create = button(locale.t('addRoomHere'), () => {
    closeMenus();
    if (!state || state.instanceId !== instance || (state.documentRevision ?? state.revision) !== documentRevision) { toast(locale.t('writerBusy')); return; }
    roomAddDialog(point);
  });
  create.setAttribute('role', 'menuitem'); panel.append(create); root.append(panel); document.body.append(root);
  const bounds = root.getBoundingClientRect();
  root.style.left = `${Math.max(4, Math.min(client.x, window.innerWidth - bounds.width - 4))}px`;
  root.style.top = `${Math.max(4, Math.min(client.y, window.innerHeight - bounds.height - 4))}px`;
  root.addEventListener('contextmenu', event => event.preventDefault());
  root.addEventListener('keydown', event => {
    if (!['Escape', 'Tab', 'ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault(); event.stopPropagation();
    if (event.key === 'Escape' || event.key === 'Tab') { closeMenus(); el('map-canvas').focus(); }
    else create.focus();
  });
  create.focus();
}
let cameraControls: ReturnType<typeof createCameraControls> | undefined;
function roomAddDialog(at?: Point): void {
  const expectation = state ? expectedAt(state) : undefined;
  let name: HTMLInputElement; const inputs = new Map<string, HTMLInputElement>();
  showModal(locale.t('addRoom'), body => {
    const pair = labelInput(locale.t('name'), locale.t('rooms') + ' ' + ((state?.document.rooms.length || 0) + 1)); name = pair[1]; name.id = 'room-add-name'; body.append(pair[0]);
    const fields = text('div', '', 'fields');
    for (const [key, value] of Object.entries({ x: at?.x ?? Math.round(map.center.x), y: at?.y ?? Math.round(map.center.y), width: 16, height: 10 })) {
      const [label, input] = labelInput(['x', 'y'].includes(key) ? key.toUpperCase() : locale.t(key), value, 'number');
      input.step = '1'; input.id = `room-add-${key}`;
      if (key === 'width' || key === 'height') { input.min = '1'; input.max = '1024'; }
      inputs.set(key, input); fields.append(label);
    }
    body.append(fields, text('p', locale.t('addRoomPlacementHelp')));
  }, async () => {
    const created = await run('roomAdd', { name: name.value, ...numberValues(inputs) }, expectation);
    map.selectRoomTarget(created.selection.roomId); map.frameRoom();
  }, 'addRoom');
}
function addGroup(): void { if (!state || state.selection.layer === 6) return; const snapshot = state, expectation = expectedAt(state); let name: HTMLInputElement; showModal(locale.t('addGroup'), body => { const pair = labelInput(locale.t('name'), ''); name = pair[1]; body.append(pair[0]); }, async () => { if (!name.value.trim()) throw new Error(locale.t('required')); const id = crypto.randomUUID(); const changed = await run('documentProperties', { layerGroups: [...snapshot.document.layerGroups, { id, name: name.value, parentId: snapshot.selection.groupId || '', layer: snapshot.selection.layer, visible: true, locked: false }] }, expectation); await run('options', { groupId: id }, expectedAt(changed)); }); }
function drawChrome(): void {
  const brand = locale.t('brand');
  if (brand !== 'brand') {
    app.querySelector<HTMLElement>('.brand-text')!.textContent = brand;
    document.title = standalone ? `${brand} · ${locale.t('minimap')}` : brand;
  }
  document.documentElement.lang = locale.htmlLanguage; el<HTMLSelectElement>('language').replaceChildren(...LANGUAGES.map(([key, label]) => new Option(label, key))); el<HTMLSelectElement>('language').value = locale.language; el('language').setAttribute('aria-label', locale.t('language'));
  for (const node of document.querySelectorAll<HTMLElement>('[data-label]')) node.textContent = locale.t(node.dataset.label!);
  el<HTMLButtonElement>('add-palette').title = locale.t('paletteAdd'); el('add-palette').setAttribute('aria-label', locale.t('paletteAdd'));
  el<HTMLInputElement>('room-search').placeholder = locale.t('search'); el<HTMLInputElement>('palette-search').placeholder = locale.t('search'); el<HTMLButtonElement>('add-room').title = locale.t('addRoom');
  const actions = el('file-actions'); actions.replaceChildren();
  const undo = button(locale.t('undo') + '   Ctrl+Z', () => run('undo')), redo = button(locale.t('redo') + '   Ctrl+Y', () => run('redo')); undo.title = locale.t('undo') + ' · Ctrl+Z'; redo.title = locale.t('redo') + ' · Ctrl+Y'; undo.id = 'undo'; redo.id = 'redo';
  const inspectorToggle = button('☷ ' + locale.t('inspector'), () => { inspectorHidden = !inspectorHidden; localStorage.setItem('mapstudio.inspectorHidden', String(inspectorHidden)); updateView(); }, 'ghost'); inspectorToggle.id = 'inspector-toggle';
  const scripts = button(locale.t('scripts.menu'), () => openScriptDialog({ t: key => locale.t(key),
    prepare: async () => { await settleFileSnapshot(); await api.refresh(false); if (!api.state) throw new Error(locale.t('loading')); return { instanceId: api.state.instanceId, documentRevision: api.state.documentRevision }; },
    refresh: () => api.refresh(false) })); scripts.id = 'scripts-action';
  actions.prepend(menu('file-menu', locale.t('file') + ' (F)', [
    button(locale.t('new'), () => fileDialog('new')), button(locale.t('open'), openMap), button(locale.t('import'), importDocument), null,
    button(locale.t('save') + '   Ctrl+S', save), button(locale.t('saveAs'), () => save(true)), null,
    button(locale.t('addRoom'), roomAddDialog), button(locale.t('importRooms'), importRooms), null,
    button(locale.t('exportSelected'), () => fileDialog('exportRooms', 'selected')),
    button(locale.t('exportAll'), () => fileDialog('exportRooms', 'all')),
    button(locale.t('exportChanged'), () => fileDialog('exportRooms', 'changed')), null, button(locale.t('storageFolders'), () => openWorkspaceFolders(locale)), scripts]));
  actions.append(menu('edit-menu', locale.t('edit') + ' (E)', [undo, redo, null,
    roomRestructureButton('merge', 'room-merge-action'), roomRestructureButton('split', 'room-split-action')]),
    menu('help-menu', locale.t('helpMenu') + ' (H)', [button(locale.t('sampleWorld'), openSampleWorld), null, button(locale.t('docs.title'), () => { window.open('/docs/index.html', '_blank', 'noopener'); }), button(locale.t('docs.api'), () => { window.open('/docs/api--index.html', '_blank', 'noopener'); }), null, button(locale.t('shortcut'), shortcutsDialog), button(locale.t('about'), aboutDialog)]));
  const tabs = el('tabs'); tabs.replaceChildren(button(locale.t('editor'), () => { miniMode = false; updateView(); }), button(locale.t('minimap'), () => { if (map.cameraPreview) map.gameView(false); miniMode = true; updateView(); mini.fit(); }), button('↗ ' + locale.t('popout'), () => { window.open(new URL('?view=minimap', location.href), '_blank', 'noopener'); }, 'ghost'), text('div', '', 'spacer'), inspectorToggle);
  updateView(); drawPanels(true); renderInspector(true);
}
function updateView(): void {
  app.classList.toggle('standalone-app', standalone);
  const cameraPreview = !miniMode && map.cameraPreview;
  const viewOnly = miniMode || cameraPreview;
  const workspace = el('workspace'); workspace.classList.toggle('inspector-hidden', inspectorHidden); workspace.classList.toggle('minimap-mode', miniMode); workspace.classList.toggle('standalone', standalone); workspace.classList.toggle('camera-preview', cameraPreview);
  el<HTMLButtonElement>('inspector-toggle').disabled = viewOnly;
  el<HTMLCanvasElement>('map-canvas').hidden = miniMode; el<HTMLCanvasElement>('mini-canvas').hidden = !miniMode;
  map.setActive(!miniMode); mini.setActive(miniMode);
  const tabButtons = el('tabs').querySelectorAll('button'); tabButtons[0]?.classList.toggle('active', !miniMode); tabButtons[1]?.classList.toggle('active', miniMode);
  const toolbar = el('view-toolbar'); toolbar.replaceChildren(button('⊞ ' + locale.t('fit'), () => { if (miniMode) mini.fit(); else if (map.cameraPreview) { map.gameView(false); updateView(); requestAnimationFrame(() => map.fit()); } else map.fit(); }), button('⌾ ' + locale.t('frameRoom'), () => miniMode ? mini.frameRoom() : map.frameRoom()));
  cameraControls = undefined;
  if (!miniMode) {
    const camera = button(locale.t('camera'), () => { map.gameView(); updateView(); }, map.cameraPreview ? 'active' : ''); camera.id = 'camera-preview'; camera.setAttribute('aria-pressed', String(map.cameraPreview)); camera.title = locale.t('cameraHelp');
    const defaults = button(locale.t('tilesetDefaultView'), () => { map.setDefaultTiles(!map.showDefaultTiles); updateView(); }, map.showDefaultTiles ? 'active' : '');
    defaults.id = 'default-tile-view'; defaults.setAttribute('aria-pressed', String(map.showDefaultTiles));
    toolbar.append(defaults);
    cameraControls = createCameraControls(key => locale.t(key), values => run('cameraSettings', () => {
      const current = map.cameraProfile!;
      return { ppu: current.ppu, referenceWidth: current.referenceWidth, referenceHeight: current.referenceHeight, ...values };
    }), toast);
    cameraControls.update(map.cameraProfile);
    toolbar.append(camera, cameraControls.element, button('−', () => map.zoom({ x: el('map-canvas').clientWidth / 2, y: el('map-canvas').clientHeight / 2 }, -1), 'icon'), text('span', '', 'zoom-readout'), button('+', () => map.zoom({ x: el('map-canvas').clientWidth / 2, y: el('map-canvas').clientHeight / 2 }, 1), 'icon'));
  }
  toolbar.append(text('div', '', 'spacer'));
  if (!miniMode && !map.cameraPreview) toolbar.append(check(locale.t('grid'), map.showGrid, checked => { map.showGrid = checked; map.requestDraw(); })[0]);
  if (miniMode || !map.cameraPreview) toolbar.append(check(locale.t('names'), miniMode ? mini.showNames : map.showNames, checked => { if (miniMode) { mini.showNames = checked; mini.requestDraw(); } else { map.showNames = checked; map.requestDraw(); } })[0]);

  if (miniMode) {
    const width = select(Array.from({ length: 20 }, (_, i) => [String(i + 1), locale.t('outline') + ' ' + (i + 1)] as [string, string]), String(mini.outlineWidth));
    width.id = 'minimap-outline-width'; width.style.width = 'auto'; width.setAttribute('aria-label', locale.t('outline')); width.title = locale.t('outlineHelp');
    width.addEventListener('change', () => { mini.outlineWidth = Number(width.value); mini.requestDraw(); });
    const entrance = select(Array.from({ length: 40 }, (_, i) => [String(i + 1), locale.t('entranceLength') + ' ' + (i + 1)] as [string, string]), String(mini.entranceLength));
    entrance.id = 'minimap-entrance-length'; entrance.style.width = 'auto'; entrance.setAttribute('aria-label', locale.t('entranceLength')); entrance.title = locale.t('entranceLengthHelp');
    entrance.addEventListener('change', () => { mini.entranceLength = Number(entrance.value); mini.requestDraw(); });
    toolbar.append(width, entrance);
  }
  const undo = el<HTMLButtonElement>('undo'), redo = el<HTMLButtonElement>('redo'); undo.disabled = viewOnly || !state?.canUndo; redo.disabled = viewOnly || !state?.canRedo;
  map.requestDraw(); mini.requestDraw(); drawStatus();
}
function panelKey(value: State): string {
  const rooms = value.document.rooms.map(room => [room.id, room.name, room.x, room.y, room.width, room.height,
    room.visible, room.locked, roomColor(room)]);
  const groups = value.document.layerGroups.map(group => [group.id, group.parentId, group.layer, group.name, group.visible, group.locked]);
  const selection = value.selection, panelSelection = [selection.roomIds, selection.roomId, selection.tool, selection.layer, selection.shape,
    selection.material, selection.brushSize, selection.filled, selection.objectDefinition, selection.groupId,
    selection.hiddenLayers, selection.lockedLayers];
  return JSON.stringify([value.instanceId, value.catalogRevision, value.document.name, rooms, groups, panelSelection,
    value.file || '', value.dirty, value.canUndo, value.canRedo, roomSearch, paletteSearch, languageVersion]);
}
function queuePanels(): void {
  if (!state || uiRaf) return;
  uiRaf = requestAnimationFrame(() => { uiRaf = 0; drawPanels(); });
}
function toolAvailable(tool: number, layer: number): boolean { return tileLayer(layer) ? tool !== 1 : layer === 6 ? [0, 2].includes(tool) : [0, 1, 2].includes(tool); }
function drawPanels(force = false): void {
  if (!state) return;
  const key = panelKey(state); if (!force && key === lastPanelKey) { drawStatus(); renderInspector(); return; } lastPanelKey = key;
  const s = state.selection;
  el('project-name').replaceChildren(text('span', state.dirty ? '●' : '', 'dirty-dot'), document.createTextNode(state.document.name)); el('project-name').title = state.file || state.document.name;
  const viewOnly = miniMode || map.cameraPreview;
  el<HTMLButtonElement>('inspector-toggle').disabled = viewOnly;
  el<HTMLButtonElement>('room-merge-action').disabled = !canRestructureRoom('merge');
  el<HTMLButtonElement>('room-split-action').disabled = !canRestructureRoom('split');
  el<HTMLButtonElement>('undo').disabled = viewOnly || !state.canUndo; el<HTMLButtonElement>('redo').disabled = viewOnly || !state.canRedo;
  const connection = el('connection'); connection.textContent = locale.t(api.online ? 'studioConnected' : 'serverOffline'); connection.classList.toggle('online', api.online);
  el('room-count').textContent = s.roomIds.length > 1 ? `${s.roomIds.length} / ${state.document.rooms.length}` : String(state.document.rooms.length);
  el('room-count').title = locale.t('selectedRooms').replace('{0}', String(s.roomIds.length));
  const rooms = el('room-list'); rooms.replaceChildren();
  const selectedRooms = new Set(s.roomIds);
  for (const room of state.document.rooms.filter(r => r.name.toLocaleLowerCase().includes(roomSearch.toLocaleLowerCase()))) {
    const b = document.createElement('button'); b.type = 'button'; b.className = 'room-row';
    b.addEventListener('click', event => {
      const toggle = event.ctrlKey || event.metaKey;
      void run('selectRoom', { id: room.id, toggle }).then(next => {
        map.selectRoomTarget(next.selection.roomIds.length ? next.selection.roomId : null);
        if (!toggle) map.frameRoom();
      }).catch(error => toast(error));
    });
    b.classList.toggle('active', room.id === s.roomId); b.classList.toggle('selected', selectedRooms.has(room.id));
    b.setAttribute('aria-pressed', String(selectedRooms.has(room.id))); b.classList.toggle('hidden', !room.visible);
    const swatch = text('span', '', 'swatch'); swatch.style.background = roomColor(room);
    b.append(swatch, text('span', (room.locked ? '▣ ' : '') + room.name, 'room-label'), text('span', room.width + '×' + room.height, 'room-size'));
    b.title = room.name + ' · ' + room.x + ', ' + room.y; rooms.append(b);
  }
  if (!rooms.childElementCount) rooms.append(text('div', locale.t('noRooms'), 'empty'));
  const tools = el('tools'); tools.replaceChildren(); const icons = ['▱', '+', '⬚', '▰', '□', '▨', '╱', '○', '⬭'];
  TOOLS.forEach((name, index) => { if (!toolAvailable(index, s.layer)) return; const b = button('', () => option({ tool: index }), 'tool-button'); b.append(text('span', icons[index], 'symbol'), text('span', index === 2 ? locale.t(tileLayer(s.layer) ? 'tileSelection' : 'objectSelection') : locale.enum('MetroidvaniaStudioTool', name, index))); b.classList.toggle('active', s.tool === index); b.dataset.tool = String(index); b.title = locale.t(index === 2 ? 'selectionHelp' : index === 1 ? 'placementHelp' : index === 0 ? 'roomHelp' : 'help'); tools.append(b); });
  const layers = el('layers'); layers.replaceChildren(); LAYERS.forEach((name, index) => { const div = text('div', '', 'layer-row'), b = button(locale.enum('MapLayer', name, index), () => option({ layer: index, groupId: '', ...(!tileLayer(index) && index !== 6 ? { objectDefinition: state!.catalog.objects.find(o => o.layer === index || [4, 5].includes(o.layer) && [4, 5].includes(index))?.id || '' } : {}) }), 'layer-select'); b.classList.toggle('active', index === s.layer); div.append(b); if (index !== 6) { const visible = button('', () => option(() => { const hidden = state!.selection.hiddenLayers; return { hiddenLayers: hidden.includes(index) ? hidden.filter(layer => layer !== index) : [...hidden, index] }; }), 'icon ghost layer-visibility'); const shown = !s.hiddenLayers.includes(index), eye = document.createElement('img'); eye.src = shown ? 'layer-eye-open.svg' : 'layer-eye-closed.svg'; eye.alt = ''; eye.draggable = false; eye.setAttribute('aria-hidden', 'true'); visible.append(eye); visible.title = locale.enum('MapLayer', name, index) + ' · ' + locale.t('visible'); visible.setAttribute('aria-label', visible.title); visible.setAttribute('aria-pressed', String(shown)); const locked = button(s.lockedLayers.includes(index) ? '▣' : '▫', () => option(() => { const locked = state!.selection.lockedLayers; return { lockedLayers: locked.includes(index) ? locked.filter(layer => layer !== index) : [...locked, index] }; }), 'icon ghost'); locked.title = locale.t('locked'); div.append(visible, locked); } layers.append(div); });
  const groupSelect = el<HTMLSelectElement>('group-select'); const group = select([['', locale.t('ungrouped')], ...state.document.layerGroups.filter(g => g.layer === s.layer).map(g => [g.id, (g.locked ? '▣ ' : '') + (!g.visible ? '○ ' : '') + g.name] as [string, string])], s.groupId); groupSelect.replaceChildren(...group.children); groupSelect.value = s.groupId;
  const groupActions = el('group-actions'); groupActions.replaceChildren(button('+ ' + locale.t('group'), addGroup)); const selectedGroup = state.document.layerGroups.find(g => g.id === s.groupId); if (selectedGroup) for (const key of ['visible', 'locked'] as const) groupActions.append(button(locale.t(key), () => run('documentProperties', () => ({ layerGroups: state!.document.layerGroups.map(group => group.id === selectedGroup.id ? { ...group, [key]: !group[key] } : group) })), selectedGroup[key] ? 'active' : ''));
  drawPalette(); drawStatus(); renderInspector();
}
function drawPalette(): void {
  if (!state) return;
  const catalogToken = `${state.instanceId}:${state.catalogRevision}`;
  if (thumbnailCatalogToken !== catalogToken) {
    thumbnailCatalogToken = catalogToken;
    thumbnails.setCatalog(state.instanceId, state.catalogRevision, [
      ...state.catalog.materials.flatMap(material => material.sprites.map(sprite => sprite.asset)),
      ...state.catalog.objects.flatMap(definition => definition.sprite ? [definition.sprite.asset] : [])]);
  }
  const s = state.selection, options = el('brush-options'), palette = el('palette'); options.replaceChildren(); palette.replaceChildren();
  el<HTMLButtonElement>('add-palette').hidden = !tileLayer(s.layer);
  if (tileLayer(s.layer)) {
    const shapes = text('div', '', 'shape-row'); ['■', '◣', '◢', '◤', '◥'].forEach((symbol, i) => { const b = button(symbol, () => option({ shape: i })); b.classList.toggle('active', s.shape === i); b.title = locale.enum('TileShape', SHAPES[i], i); shapes.append(b); }); options.append(shapes);
    const [brushLabel, brush] = labelInput(locale.t('brush'), map.brushSize, 'number'); brush.min = '1'; brush.max = String(MAX_BRUSH_SIZE); brush.step = '1'; brush.title = locale.t('brushWheelHelp'); brush.addEventListener('change', () => { void option({ brushSize: Number(brush.value) }).catch(() => undefined); }); options.append(row(brushLabel, check(locale.t('filled'), s.filled, filled => { void option({ filled }).catch(() => undefined); })[0]));
    const addGroup = button('+ ' + locale.t('group'), addPaletteGroup); addGroup.id = 'add-palette-group'; addGroup.title = locale.t('paletteGroupAdd');
    options.append(addGroup);
    renderPaletteGroups(palette, state, locale, paletteSearch, run, {
      name: materialName,
      thumbnail: material => thumbnail(material.sprites.find(p => p.shape === s.shape && (s.shape !== 0 || p.mask === 0)), colorCss(material.color)),
      select: material => { void option({ material: material.id, tool: s.tool === 0 || s.tool === 2 ? 3 : s.tool }); },
      settings: material => openPaletteDialog(material, locale, run, state!.paletteGroups),
      groupSettings: editPaletteGroup, report: toast
    });
  } else {
    for (const def of [...state.catalog.objects].sort((a, b) => { const order = ['Spawn', 'Portal', 'Path', 'Respawn']; return (order.includes(a.id) ? order.indexOf(a.id) : 99) - (order.includes(b.id) ? order.indexOf(b.id) : 99); }).filter(d => !(d.name === d.id && ['Marker', 'Object'].includes(d.id)) && (d.layer === s.layer || [4, 5].includes(s.layer) && [4, 5].includes(d.layer)) && definitionName(d).toLowerCase().includes(paletteSearch.toLowerCase()))) { const b = button('', () => option({ objectDefinition: def.id, tool: 1 }), 'palette-item'); b.classList.toggle('active', definitionKey(def.id) === definitionKey(s.objectDefinition)); b.append(thumbnail(def.sprite, colorCss(def.color)), text('span', definitionName(def))); palette.append(b); }
  }
  if (!palette.childElementCount) palette.append(text('div', locale.t('noMaterials'), 'empty'));
}
function addPaletteDialog(): void {
  if (!state || !tileLayer(state.selection.layer)) return;
  const [nameLabel, name] = labelInput(locale.t('name'), locale.t('paletteDefaultName').replace('{0}', String(state.catalog.materials.length + 1)));
  const groups = state.paletteGroups;
  const group = select(groups.map(g => [g.id, paletteGroupName(g, locale)] as [string, string]), groups.find(g => g.materials.includes(state!.selection.material))?.id || 'default'); group.id = 'palette-new-group';
  const groupLabel = text('label', locale.t('paletteGroup')); groupLabel.append(group);
  name.id = 'palette-name'; name.maxLength = 80;
  const [colorLabel, color] = labelInput(locale.t('theme'), '#9655CF');
  color.id = 'palette-color-hex'; color.maxLength = 7; colorLabel.htmlFor = color.id; color.remove();
  const picker = document.createElement('input'); picker.type = 'color'; picker.id = 'palette-color-picker';
  picker.value = color.value; picker.setAttribute('aria-label', locale.t('theme'));
  color.addEventListener('input', () => { if (/^#[0-9a-f]{6}$/i.test(color.value.trim())) picker.value = color.value.trim(); });
  const pick = () => { color.value = picker.value.toUpperCase(); };
  picker.addEventListener('input', pick); picker.addEventListener('change', pick);
  const fields = text('div', '', 'room-color-field'), inputs = text('div', '', 'room-color-inputs');
  inputs.append(color, picker); fields.append(colorLabel, inputs);
  showModal(locale.t('paletteAdd'), body => body.append(nameLabel, groupLabel, fields, text('p', locale.t('paletteAddHelp'), 'hint')), async () => {
    const value = name.value.trim(), hex = color.value.trim();
    if (!value || value.length > 80 || /[\u0000-\u001f\u007f]/.test(value)) throw new Error(locale.t('paletteInvalidName'));
    if (!/^#[0-9a-f]{6}$/i.test(hex)) throw new Error(locale.t('paletteInvalidColor'));
    if (state!.catalog.materials.some(m => m.name.toLocaleLowerCase() === value.toLocaleLowerCase())) throw new Error(locale.t('paletteDuplicateName'));
    await run('paletteAdd', { name: value, color: hex, groupId: group.value }); expandPaletteGroup(group.value);
    paletteSearch = ''; el<HTMLInputElement>('palette-search').value = ''; drawPanels(true);
  }, 'paletteAdd');
  name.focus(); name.select();
}

function addPaletteGroup(): void {
  if (!state || !tileLayer(state.selection.layer)) return;
  const [field, name] = labelInput(locale.t('name'), ''); name.id = 'palette-group-name'; name.maxLength = 80;
  const dialog = showModal(locale.t('paletteGroupAdd'), body => body.append(field), async () => {
    await run('paletteGroupAdd', { name: name.value }); paletteSearch = ''; el<HTMLInputElement>('palette-search').value = ''; drawPanels(true);
  }, 'paletteGroupAdd'); dialog.id = 'palette-group-dialog'; name.focus();
}
function editPaletteGroup(group: EditorPaletteGroup): void {
  const groups = state!.paletteGroups, [field, name] = labelInput(locale.t('name'), paletteGroupName(group, locale));
  name.id = 'palette-group-name'; name.maxLength = 80; name.disabled = ['default', 'default-themes'].includes(group.id);
  let index = groups.findIndex(g => g.id === group.id);
  const position = text('span', ''), up = button('↑', () => { index--; update(); }), down = button('↓', () => { index++; update(); });
  up.id = 'palette-group-up'; down.id = 'palette-group-down'; up.title = locale.t('paletteMoveUp'); down.title = locale.t('paletteMoveDown');
  function update(): void { up.disabled = index === 0; down.disabled = index === groups.length - 1; position.textContent = locale.t('palettePosition').replace('{0}', String(index + 1)).replace('{1}', String(groups.length)); }
  update();
  const dialog = showModal(locale.t('paletteGroupSettings'), body => body.append(field, row(position, up, down)), async () => {
    const others = groups.filter(g => g.id !== group.id);
    await run('paletteGroupMove', { id: group.id, beforeId: others[index]?.id ?? null, name: name.disabled ? group.name : name.value, expectedGroups: groups });
  }); dialog.id = 'palette-group-dialog';
}

const thumbnails = new AssetImages();
let thumbnailCatalogToken = '';
function thumbnail(sprite: SpriteRect | null | undefined, color: string): HTMLCanvasElement {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 32; const ctx = canvas.getContext('2d')!; ctx.imageSmoothingEnabled = false; ctx.fillStyle = color; ctx.fillRect(4, 4, 24, 24);
  if (sprite) {
    const draw = () => {
      const image = thumbnails.get(sprite.asset, draw); if (!image?.naturalWidth) return;
      ctx.clearRect(0, 0, 32, 32);
      const factor = Math.min(28 / sprite.width, 28 / sprite.height), w = sprite.width * factor, h = sprite.height * factor;
      ctx.drawImage(image, sprite.x, image.naturalHeight - sprite.y - sprite.height, sprite.width, sprite.height, (32 - w) / 2, (32 - h) / 2, w, h);
    }; draw();
  }
  return canvas;
}
function selectedObjects(room: Room | undefined, ids: string[]): MapObject[] {
  if (!room) return [];
  if (inspectorObjectSource !== room.objects) {
    inspectorObjectSource = room.objects; inspectorObjectVersion++;
    inspectorObjectIndex = new Map(room.objects.map(object => [object.id, object]));
  }
  const selected: MapObject[] = [];
  for (const id of ids) { const object = inspectorObjectIndex.get(id); if (object) selected.push(object); }
  return selected;
}
function updateInspectorSelection(ids: string[]): boolean {
  if (ids.length === inspectorSelectionIds.length && ids.every((id, index) => id === inspectorSelectionIds[index])) return false;
  inspectorSelectionIds = ids.slice(); inspectorSelectionVersion++; return true;
}
async function editSelection(action: string, values: object = {}, inspector = false): Promise<void> {
  await map.settled(); if (!state) return;
  const room = activeRoom(state), selection = state.selection;
  const roomTarget = selection.roomIds.length > 1 || selection.tool === 0 || !!map.roomDeleteTarget
    || inspector && !selection.area && !selection.objects.length;
  const scope = action === 'paste' ? clipboardScope : roomTarget ? 'room' : 'content';
  if (action === 'copy' || action === 'cut') clipboardScope = scope;
  const actions: Record<string, string> = { copy: 'roomCopy', cut: 'roomCut', paste: 'roomPaste', flip: 'roomFlip', rotate: 'roomRotate', delete: 'roomDeleteSelected' };
  const point = scope === 'room' ? map.hover : { x: map.hover.x - (room?.x || 0), y: map.hover.y - (room?.y || 0) };
  const next = await run(scope === 'room' ? actions[action] : action,
    { ...values, ...(action === 'paste' ? { x: Math.floor(point.x), y: Math.floor(point.y) } : {}) }, expectedAt(state));
  if (scope === 'room') map.selectRoomTarget(action === 'delete' || action === 'cut' ? null : next.selection.roomId);
  renderInspector(true);
}
function canRestructureRoom(action: 'merge' | 'split'): boolean {
  if (!state || miniMode || map.cameraPreview) return false;
  const selection = state.selection, room = activeRoom(state);
  if (action === 'merge') return selection.roomIds.length >= 2
    && !state.document.rooms.some(candidate => selection.roomIds.includes(candidate.id) && candidate.locked);
  const area = selection.area;
  return !!room && !room.locked && selection.tool === 2 && !!area && area.width > 0 && area.height > 0
    && (area.width !== room.width || area.height !== room.height);
}
function roomRestructureButton(action: 'merge' | 'split', id: string): HTMLButtonElement {
  const control = button(locale.t(action === 'merge' ? 'roomMerge' : 'roomSplit'), () => restructureRoom(action));
  control.id = id; control.disabled = !canRestructureRoom(action);
  control.title = locale.t(action === 'merge' ? 'roomMergeHint' : 'roomSplitHint');
  return control;
}
async function restructureRoom(action: 'merge' | 'split'): Promise<void> {
  await settleFileSnapshot();
  if (!state || !canRestructureRoom(action)) return;
  const snapshot = state, room = activeRoom(snapshot)!, expectation = expectedAt(snapshot);
  const key = action === 'merge' ? 'roomMerge' : 'roomSplit';
  const dialog = showModal(locale.t(key), body => {
    body.append(text('p', locale.t(key + 'Hint')));
    if (action === 'merge') {
      body.append(text('p', snapshot.document.rooms.filter(candidate => snapshot.selection.roomIds.includes(candidate.id)).map(candidate => candidate.name).join(' + ')));
      body.append(text('p', locale.t('roomMergePrimary').replace('{0}', room.name)));
    } else {
      const area = snapshot.selection.area!;
      body.append(text('p', `${room.name} · ${area.width} × ${area.height}`));
      body.append(text('p', locale.t('roomSplitContents')));
    }
  }, async () => {
    const next = await run(action === 'merge' ? 'roomMerge' : 'roomSplit', {}, expectation);
    map.selectRoomTarget(next.selection.roomId); map.frameRoom(true); mini.fit(); renderInspector(true);
  }, key);
  dialog.id = 'room-restructure-dialog';
}
function renderInspector(force = false): void {
  if (!state) return; const inspector = el('inspector');
  if (updateInspectorSelection(state.selection.objects)) force = true;
  const selectionKey = `${state.instanceId}:${state.selection.roomId}:${inspectorSelectionVersion}`;
  if (selectionKey !== inspectorSelectionKey) { inspectorSelectionKey = selectionKey; force = true; }
  if (!force && inspector.contains(document.activeElement)) return;
  const room = activeRoom(state), selected = selectedObjects(room, state.selection.objects);
  const contentKey = selected.length ? ['objects', inspectorObjectVersion] : room
    ? ['room', room.id, room.name, room.x, room.y, room.width, room.height, room.visible, room.locked, room.properties] : null;
  const selection = state.selection, inspectorSelection = [selection.roomId, selection.roomIds, selection.tool, selection.layer, selection.shape,
    selection.material, selection.brushSize, selection.filled, selection.objectDefinition, selection.groupId,
    selection.hiddenLayers, selection.lockedLayers, selection.area, selection.nodes, inspectorSelectionVersion];
  const key = JSON.stringify([state.instanceId, state.catalogRevision, inspectorSelection, contentKey, languageVersion]); if (!force && lastInspectorKey === key) return; lastInspectorKey = key;
  inspector.replaceChildren(); const heading = text('div', '', 'inspector-heading'); heading.append(text('strong', locale.t('inspector')), button('×', () => { inspectorHidden = true; updateView(); }, 'ghost icon')); inspector.append(heading);
  if (!room) { inspector.append(text('div', locale.t('noSelection'), 'empty')); return; }
  const expectation = expectedAt(state); if (selected.length) objectInspector(inspector, selected, expectation); else roomInspector(inspector, expectation);
  // These buttons do not submit inspector field values. Use the click-time revision so
  // an unrelated compact tile edit cannot leave otherwise valid actions stale.
  if (state.selection.area) { const area = state.selection.area; const summary = section(locale.t('selectionSummary')); summary.id = 'selection-summary'; summary.append(text('div', `${area.width} × ${area.height} · ${locale.enum('MapLayer', LAYERS[state.selection.layer], state.selection.layer)}`), text('p', locale.t('selectionHelp'))); inspector.append(summary); }
  const actions = section(locale.t(!state.selection.area && !selected.length ? 'roomActions' : 'selectionActions')); actions.dataset.scope = !state.selection.area && !selected.length ? 'room' : 'content';
  if (state.selection.roomIds.length > 1) actions.append(roomRestructureButton('merge', 'room-merge-selection'));
  if (state.selection.area && state.selection.tool === 2) actions.append(roomRestructureButton('split', 'room-split-selection'));
  actions.append(row(button(locale.t('copy'), () => editSelection('copy', {}, true)), button(locale.t('paste'), () => editSelection('paste', {}, true))),
    row(button(locale.t('flipH'), () => editSelection('flip', { horizontal: true }, true)), button(locale.t('flipV'), () => editSelection('flip', { horizontal: false }, true))),
    row(button('↻ ' + locale.t('rotation'), () => editSelection('rotate', { clockwise: true }, true)), button(locale.t('delete'), () => editSelection('delete', {}, true), 'danger')));

  // Clearing is room-wide and the inspector intentionally does not rerender for every
  // tile/object edit. Capture the revision when the user opens the confirmation dialog,
  // rather than retaining the revision from the last inspector render.
  actions.append(button(locale.t('clearLayer'), () => confirmRun(locale.t('confirmClear'), 'clearLayer'), 'danger'), button(locale.t('clearObjects'), () => confirmRun(locale.t('confirmClear'), 'clearObjects'), 'danger')); inspector.append(actions);
}
function roomInspector(inspector: HTMLElement, expectation: CommandExpectation): void {
  const room = activeRoom(state)!; const content = section(locale.t('rooms')); const [nameLabel, name] = labelInput(locale.t('name'), room.name); content.append(nameLabel);
  const fields = text('div', '', 'fields'), inputs = new Map<string, HTMLInputElement>(); for (const key of ['x', 'y', 'width', 'height'] as const) { const [label, input] = labelInput(key === 'x' || key === 'y' ? key.toUpperCase() : locale.t(key), room[key], 'number'); input.step = '1'; inputs.set(key, input); fields.append(label); } content.append(fields);
  const [visibleLabel, visible] = check(locale.t('visible'), room.visible), [lockedLabel, locked] = check(locale.t('locked'), room.locked); content.append(row(visibleLabel, lockedLabel));
  content.append(check(locale.t('crop'), map.crop, value => map.crop = value)[0]);
  const [colorLabel, color] = labelInput(locale.t('theme'), roomColor(room));
  color.id = 'room-color-hex'; color.placeholder = '#C72B36'; color.setAttribute('aria-label', locale.t('theme') + ' HEX');
  colorLabel.htmlFor = color.id; color.remove();
  const picker = document.createElement('input'); picker.type = 'color'; picker.id = 'room-color-picker';
  picker.value = color.value.slice(0, 7); picker.title = locale.t('theme'); picker.setAttribute('aria-label', locale.t('theme'));
  color.addEventListener('input', () => {
    const hex = color.value.trim(); if (/^#[0-9a-f]{6}([0-9a-f]{2})?$/i.test(hex)) picker.value = hex.slice(0, 7);
  });
  const pickColor = () => {
    // The native RGB picker must preserve any alpha already entered in the HEX field.
    const hex = color.value.trim(), alpha = /^#[0-9a-f]{8}$/i.test(hex) ? hex.slice(7) : '';
    color.value = picker.value.toUpperCase() + alpha;
  };
  picker.addEventListener('input', pickColor); picker.addEventListener('change', pickColor);
  const colorField = text('div', '', 'room-color-field'), colorInputs = text('div', '', 'room-color-inputs');
  colorInputs.append(color, picker); colorField.append(colorLabel, colorInputs); content.append(colorField);
  const area = document.createElement('textarea'); area.value = JSON.stringify(propObject(room.properties), null, 2); const details = document.createElement('details'); details.append(text('summary', locale.t('properties')), area); content.append(details);
  content.append(button(locale.t('apply'), async () => {
    const values = numberValues(inputs), { x, y, width, height } = values;
    const props = properties(JSON.parse(area.value)), colorValue = color.value.trim(); if (colorValue !== roomColor(room)) { const found = props.find(p => p.key === 'mapMaker.minimapColor'); if (found) found.value = colorValue; else props.push({ key: 'mapMaker.minimapColor', value: colorValue }); }
    await run('roomProperties', { id: room.id, x, y, width, height, crop: map.crop, name: name.value, visible: visible.checked, locked: locked.checked, properties: props }, expectation); renderInspector(true);
  }, 'accent'));
  content.append(button(locale.t('deleteRoom'), () => confirmRun(locale.t('confirmClear'), 'roomDelete', { id: room.id }, expectation), 'danger')); inspector.append(content);
}
function objectInspector(inspector: HTMLElement, selected: MapObject[], expectation: CommandExpectation): void {
  const object = selected[0], def = state!.catalog.objects.find(d => definitionKey(d.id) === definitionKey(object.definition)), content = section(locale.t('objectProperties')); content.append(text('div', selected.length > 1 ? `${selected.length} ${locale.t('selected')}` : (def ? definitionName(def) : object.definition), 'object-chip'));
  const originalObjects = JSON.stringify(selected);
  const currentExpectation = (): CommandExpectation => {
    // Unrelated selections may advance the revision while the form stays unchanged.
    // A changed object or workspace must still reject this form's stale edits.
    const current = state && selectedObjects(activeRoom(state), state.selection.objects);
    return state && state.instanceId === expectation.instanceId && JSON.stringify(current) === originalObjects ? expectedAt(state) : expectation;
  };
  if (selected.length === 1) {
    const fields = text('div', '', 'fields'), inputs = new Map<string, HTMLInputElement>(); for (const key of ['x', 'y', 'width', 'height', 'rotation', 'scaleX', 'scaleY'] as const) { const caption = key === 'x' || key === 'y' ? key.toUpperCase() : key.startsWith('scale') ? locale.t('scale') + ' ' + key.slice(-1) : locale.t(key); const [label, input] = labelInput(caption, object[key] ?? 1, 'number'); input.step = key === 'rotation' ? '1' : '.0625'; fields.append(label); inputs.set(key, input); } content.append(fields, button(locale.t('applyTransform'), () => run('objectTransform', numberValues(inputs), currentExpectation())));
  }
  const fieldInputs = new Map<string, HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>();
  const keys = new Set([...(def?.properties || []).map(p => p.key), ...object.properties.map(p => p.key), 'desc']);
  for (const key of keys) {
    if (key !== 'desc' && !selected.every(o => definitionKey(o.definition) === definitionKey(object.definition) || o.properties.some(p => p.key === key))) continue;
    const field = def?.properties.find(p => p.key === key), label = document.createElement('label');
    const localized = ['desc', 'event', 'once'].includes(key) || def?.name === def?.id && ['label', 'name', 'player', 'tint'].includes(key);
    label.append(text('span', localized ? locale.t('builtin.field.' + key) : field?.label || key));
    const fieldValue = (item: MapObject): string => {
      const stored = propObject(item.properties)[key];
      if (key === 'once' && stored === undefined) return definitionKey(item.definition) === 'portal' ? 'true' : 'false';
      if (key === 'event' && field?.choices?.includes('None') && !stored?.trim()) return 'None';
      return stored ?? field?.defaultValue ?? '';
    };
    const value = fieldValue(object), mixed = selected.some(o => fieldValue(o) !== value);
    let input: HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement;
    if (field?.kind === 3) {
      const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = value.trim().toLowerCase() === 'true'; checkbox.indeterminate = mixed;
      if (key === 'once' && definitionKey(object.definition) === 'portal') { checkbox.checked = true; checkbox.disabled = true; }
      checkbox.addEventListener('change', () => checkbox.indeterminate = false); input = checkbox;
    } else if (key === 'desc') { const area = document.createElement('textarea'); area.rows = 2; area.maxLength = 2048; area.value = mixed ? '' : value; if (mixed) area.placeholder = locale.t('mixed'); input = area; }
    else if (field?.choices?.length) {
      const choices = field.choices.map(value => [value, value] as [string, string]);
      if (value && !field.choices.includes(value)) choices.unshift([value, locale.t('legacyEvent') + ': ' + value]);
      if (mixed) choices.unshift(['', locale.t('mixed')]); input = select(choices, mixed ? '' : value);
    } else { input = document.createElement('input'); input.value = mixed ? '' : value; if (mixed) input.placeholder = locale.t('mixed'); }
    input.dataset.property = key; input.dataset.modified = 'false';
    input.addEventListener('input', () => input.dataset.modified = 'true'); input.addEventListener('change', () => input.dataset.modified = 'true');
    label.append(input); content.append(label); fieldInputs.set(key, input);
  }
  content.append(button(locale.t('applyProperties'), async () => {
    const edits = Object.fromEntries([...fieldInputs].filter(([, input]) => input.dataset.modified === 'true').map(([key, input]) =>
      [key, input instanceof HTMLInputElement && input.type === 'checkbox' ? String(input.checked) : input.value]));
    await run('objectProperties', { values: edits }, currentExpectation()); renderInspector(true);
  }, 'accent')); inspector.append(content);
  if (selected.length === 1) {
    const nodes = section(locale.t('nodes')); object.nodes.forEach((node, index) => { const x = document.createElement('input'), y = document.createElement('input'); x.type = y.type = 'number'; x.step = y.step = '.0625'; x.value = String(node.x); y.value = String(node.y); x.setAttribute('aria-label', `${locale.t('nodes')} ${index + 1} X`); y.setAttribute('aria-label', `${locale.t('nodes')} ${index + 1} Y`); const div = text('div', '', 'node-row'); div.append(button(String(index + 1), () => run('nodeSelect', { id: object.id, index, additive: false }, expectation), state!.selection.nodes?.some(n => n.id === object.id && n.index === index) ? 'active' : ''), x, y, button('✓', async () => { const next = { x: numberValue(x), y: numberValue(y) }; const selectedState = await run('nodeSelect', { id: object.id, index, additive: false }, expectation); await run('nodeMove', next, expectedAt(selectedState)); })); nodes.append(div); });
    const room = activeRoom(state)!; nodes.append(button('+ ' + locale.t('addNode'), () => run('nodeAdd', { x: map.hover.x - room.x, y: map.hover.y - room.y }, expectation)), button(locale.t('deleteNode'), () => run('nodeDelete', {}, expectation), 'danger')); inspector.append(nodes);
  }
}
function statusText(id: string, value: string): void { const node = el(id); if (node.textContent !== value) node.textContent = value; }
function drawStatus(point = map.hover): void {
  pendingStatusPoint = { ...point };
  if (!statusRaf) statusRaf = requestAnimationFrame(() => { statusRaf = 0; renderStatus(pendingStatusPoint || map.hover); pendingStatusPoint = null; });
}
function renderStatus(point: Point): void {
  const pending = api.pending > 0, status = el('status-state');
  statusText('status-state', locale.t(pending ? 'working' : state?.dirty ? 'sourceModified' : state ? 'sourceSaved' : 'loading'));
  const sourceHint = locale.t('sourceSaveHint'); if (status.title !== sourceHint) status.title = sourceHint;
  if (status.classList.contains('working') !== pending) status.classList.toggle('working', pending);
  const sync = syncLabels(state, api.online, pending || map.hasPendingWork);
  for (const [id, label] of [['status-export', sync.json]] as const) {
    const node = el(id); statusText(id, locale.t(label.key));
    if (node.dataset.tone !== label.tone) node.dataset.tone = label.tone;
    const title = locale.t(label.key + 'Hint') + (label.error ? '\n' + label.error : '');
    if (node.title !== title) node.title = title;
    if (node.dataset.state !== label.key) node.dataset.state = label.key;
  }
  const connection = el('connection');
  statusText('connection', locale.t(api.online ? 'studioConnected' : 'serverOffline'));
  const online = api.online;
  if (connection.classList.contains('online') !== online) connection.classList.toggle('online', online);
  statusText('status-coordinates', `X ${Math.floor(point.x)}  Y ${Math.floor(point.y)}  ·  16 px`);
  const viewing = state?.document.rooms.find(room => room.visible && map.center.x >= room.x && map.center.x < room.x + room.width && map.center.y >= room.y && map.center.y < room.y + room.height);
  statusText('status-layer', state ? locale.enum('MapLayer', LAYERS[state.selection.layer], state.selection.layer) + ' · ' + (viewing?.name || '—') : '');
  const sizeStatus = map.roomSizes, sizes = el('status-room-sizes');
  const kilobytes = (bytes: number | null | undefined) => bytes == null ? '—' : (bytes / 1024).toFixed(2) + ' KB';
  statusText('status-room-sizes', locale.t('statusSelectedSize').replace('{0}', kilobytes(sizeStatus?.selectedBytes))
    + ' · ' + locale.t('statusTotalSize').replace('{0}', kilobytes(sizeStatus?.totalBytes)));
  const sizesPending = !sizeStatus || sizeStatus.pending;
  const pendingSizeValue = String(sizesPending);
  if (sizes.dataset.pending !== pendingSizeValue) sizes.dataset.pending = pendingSizeValue;
  const sizeHint = locale.t('statusSizeHint') + (sizesPending ? '\n' + locale.t('statusSizePending') : '');
  if (sizes.title !== sizeHint) sizes.title = sizeHint;
  const view = map.viewportSize, scale = map.overview ? map.pixelScale.toFixed(2) : String(map.pixelScale), camera = map.cameraProfile;
  if (miniMode) statusText('status-camera', locale.t('minimap'));
  else if (map.cameraPreview && camera) statusText('status-camera', `${locale.t('camera')} · ${camera.referenceWidth}×${camera.referenceHeight} · ×${scale} · ${locale.t('cameraSize')} ${camera.orthographicSize.toFixed(3)} · PPU ${camera.ppu}`);
  else statusText('status-camera', `${map.overview ? locale.t('overview') : locale.t('editView')} · ×${scale} · ${locale.t('visibleArea')} ${view.x.toFixed(2)}×${view.y.toFixed(2)} · PPU ${camera?.ppu || 16}`);
  const revision = el('status-revision');
  statusText('status-revision', state ? `r${state.revision}` : '');
  if (state) { if (revision.dataset.instanceId !== state.instanceId) revision.dataset.instanceId = state.instanceId; }
  else if (revision.dataset.instanceId !== undefined) delete revision.dataset.instanceId;
  const zoom = el('view-toolbar').querySelector('.zoom-readout'); if (zoom && zoom.textContent !== `×${scale}`) zoom.textContent = `×${scale}`;
  statusText('canvas-help', locale.t(miniMode ? 'minimapHelp' : map.cameraPreview ? 'cameraHelp' : state?.selection.tool === 0 ? 'roomHelp' : tileLayer(state?.selection.layer ?? 0) && state?.selection.tool === 2 ? 'selectionHelp' : state?.selection.tool === 1 ? 'placementHelp' : !tileLayer(state?.selection.layer ?? 0) && state?.selection.tool === 2 ? 'objectHelp' : 'help'));
}
function stateChanged(): void {
  state = api.state; if (!state) return;
  if (attachedFile && (attachedFile.instanceId !== state.instanceId || attachedFile.id !== state.browserFileId)) attachedFile = undefined;
  if (state.notice && state.notice !== lastNotice) toast(state.notice);
  else if (!state.notice && lastNotice && el('toast').textContent === lastNotice) {
    el('toast').hidden = true; clearTimeout(toastTimer);
  }
  lastNotice = state.notice || ''; map.setState(state); mini.setState(state); cameraControls?.update(map.cameraProfile); queuePanels(); drawStatus();
}
api.addEventListener('state', stateChanged); api.addEventListener('busy', () => drawStatus()); api.addEventListener('error', event => toast((event as CustomEvent).detail));
api.addEventListener('connection', () => drawStatus()); api.addEventListener('offline', () => drawStatus());
locale.addEventListener('change', () => { languageVersion++; drawChrome(); }); el('language').addEventListener('change', () => locale.set(el<HTMLSelectElement>('language').value as Language));
el('add-palette').addEventListener('click', addPaletteDialog);
el('add-room').addEventListener('click', () => roomAddDialog()); el('room-search').addEventListener('input', () => { roomSearch = el<HTMLInputElement>('room-search').value; drawPanels(); }); el('palette-search').addEventListener('input', () => { paletteSearch = el<HTMLInputElement>('palette-search').value; drawPalette(); }); el('group-select').addEventListener('change', () => { void option({ groupId: el<HTMLSelectElement>('group-select').value }).catch(() => undefined); });
el('inspector').addEventListener('focusout', () => { window.setTimeout(() => renderInspector(), 0); });
const toolKeys = ['r', 'p', 'v', 'b', 'u', 'g', 'l', 'c', 'e'];
function authoringShortcut(key: string, mod: boolean): boolean {
  return mod && ['z', 'y', 'c', 'x', 'v', 'a'].includes(key) || key === 'delete' || key === 'backspace' || !mod && toolKeys.includes(key);
}
document.addEventListener('keydown', event => {
  if (event.defaultPrevented || document.querySelector('dialog[open]')) return;
  if (event.altKey && !event.ctrlKey && ['f', 'e', 'h'].includes(event.key.toLowerCase())) { event.preventDefault(); el<HTMLButtonElement>(event.key.toLowerCase() === 'f' ? 'file-menu-button' : event.key.toLowerCase() === 'e' ? 'edit-menu-button' : 'help-menu-button').click(); return; }
  if (document.querySelector('.menu-popup:not([hidden])')) return;
  const typing = event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLSelectElement || (event.target as HTMLElement)?.isContentEditable;
  if (typing && !(event.key.toLowerCase() === 's' && (event.ctrlKey || event.metaKey))) return;
  const key = event.key.toLowerCase(), mod = event.ctrlKey || event.metaKey;
  const recognized = authoringShortcut(key, mod) || mod && key === 's' || !mod && key === 'f';
  if (((miniMode || map.cameraPreview) && authoringShortcut(key, mod)) || ((map.interacting || mini.interacting) && key !== 'escape' && recognized)) { event.preventDefault(); return; }
  let action: (() => unknown) | undefined;
  if (mod && key === 's') action = () => save(event.shiftKey);
  else if (mod && key === 'z') action = () => run(event.shiftKey ? 'redo' : 'undo');
  else if (mod && key === 'y') action = () => run('redo');
  else if (mod && key === 'a') action = () => { map.selectRoomTarget(null); return run('selectAll'); };
  else if (mod && ['c', 'x', 'v'].includes(key)) action = () => editSelection(({ c: 'copy', x: 'cut', v: 'paste' } as Record<string, string>)[key]);
  else if (key === 'delete' || key === 'backspace') action = async () => {
    if (event.repeat) return;
    await map.settled();
    const id = map.roomDeleteTarget;
    const expectation = state ? expectedAt(state) : undefined;
    if (id || state?.selection.tool === 0) { await run('roomDeleteSelected', {}, expectation); map.selectRoomTarget(null); }
    else await run('delete', {}, expectation);
  };
  else if (key === 'escape') action = () => { if (!miniMode && map.cameraPreview) { map.gameView(false); updateView(); } else if (miniMode) mini.cancelInteraction(); else { map.selectRoomTarget(null); map.cancel(); void run('cancel').catch(() => undefined); } };
  else if (!mod && key === 'f') action = () => miniMode ? mini.frameRoom() : map.frameRoom();
  else if (!mod && key === 'b') action = () => option({ tool: 3,
    ...(!tileLayer(state?.selection.layer ?? 0) ? { layer: 0, groupId: '' } : {}) });
  else if (!mod && toolKeys.includes(key) && toolAvailable(toolKeys.indexOf(key), state?.selection.layer ?? 0)) action = () => option({ tool: toolKeys.indexOf(key) });
  if (action) { event.preventDefault(); Promise.resolve().then(action).catch(error => toast(error)); }
});
window.addEventListener('beforeunload', event => {
  if (!standalone && (state?.dirty || fileBusy || api.pending > 0 || map.hasPendingWork)) { event.preventDefault(); event.returnValue = ''; }
});
window.addEventListener('pagehide', event => {
  // A persisted page remains live in the back-forward cache. For a real unload,
  // stop polling and rendering without aborting an edit request already in flight.
  if (event.persisted) return;
  cancelAnimationFrame(uiRaf); cancelAnimationFrame(statusRaf); api.stop(false); map.dispose(); mini.dispose();
});
drawChrome();
void locale.load().then(() => { languageVersion++; drawChrome(); }).catch(error => toast(error));
await initializeWorkspaceFolders().catch(() => undefined);
try { await api.start(); } catch (error) { toast(locale.t('serverOffline')); }
