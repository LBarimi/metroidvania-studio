import type { Locale } from './locale.js';

export type FolderKind = 'maps' | 'textures';
type DirectoryHandle = { kind: 'directory'; name: string };
type FolderInfo = { project: string; maps: string; textures: string; catalog: string; autoExport: string; currentExport: string; nativeMapDialogs?: boolean };
type DirectoryPicker = Window & { showDirectoryPicker?: (options: object) => Promise<DirectoryHandle>; chrome?: { webview?: {
  postMessage(message: string): void;
  addEventListener(type: string, listener: (event: { data: unknown; additionalObjects?: DirectoryHandle[] }) => void): void;
} } };
const host = window as DirectoryPicker;
const directories: Partial<Record<FolderKind, DirectoryHandle>> = {};
let info: FolderInfo | undefined, ready: Promise<void> | undefined;
export const nativeMapDialogs = (): boolean => info?.nativeMapDialogs === true && !host.chrome?.webview;
export const workspaceFolder = (kind: FolderKind): string => info?.[kind] || '';
export const folderStartIn = (kind: FolderKind): { startIn?: DirectoryHandle } => directories[kind] ? { startIn: directories[kind] } : {};

async function database(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('metroidvania-studio-file-folders', 1);
    request.onupgradeneeded = () => request.result.createObjectStore('folders');
    request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
  });
}
async function remember(kind: FolderKind, handle: DirectoryHandle): Promise<void> {
  directories[kind] = handle;
  let db: IDBDatabase | undefined;
  try {
    db = await database(); const transaction = db.transaction('folders', 'readwrite');
    transaction.objectStore('folders').put(handle, info!.project + ':' + kind);
    await new Promise<void>((resolve, reject) => { transaction.oncomplete = () => resolve(); transaction.onerror = () => reject(transaction.error); });
  } catch { /* Private browsing can retain the handle for this tab only. */ }
  finally { db?.close(); }
}
export function initializeWorkspaceFolders(): Promise<void> {
  return ready ??= (async () => {
    const response = await fetch('/api/workspace-folders');
    if (!response.ok) throw new Error('Storage folder information is unavailable.');
    info = await response.json() as FolderInfo;
    const bridge = host.chrome?.webview;
    if (bridge) {
      bridge.addEventListener('message', event => {
        if (event.data !== 'studio-workspace-folders' || event.additionalObjects?.length !== 2) return;
        const [maps, textures] = event.additionalObjects;
        if (maps.kind === 'directory' && textures.kind === 'directory') { directories.maps = maps; directories.textures = textures; }
      });
      bridge.postMessage('studio-workspace-folders');
    } else {
      let db: IDBDatabase | undefined;
      try {
        db = await database();
        await Promise.all((['maps', 'textures'] as const).map(kind => new Promise<void>(resolve => {
          const request = db!.transaction('folders').objectStore('folders').get(info!.project + ':' + kind);
          request.onsuccess = () => { if (request.result?.kind === 'directory') directories[kind] = request.result; resolve(); };
          request.onerror = () => resolve();
        })));
      } catch { /* Browser file pickers still remember their own recent location. */ }
      finally { db?.close(); }
    }
  })().catch(error => { ready = undefined; throw error; });
}

export function openWorkspaceFolders(locale: Locale, focus: FolderKind = 'maps'): void {
  const dialog = document.createElement('dialog'); dialog.id = 'workspace-folders-dialog'; dialog.className = 'storage-dialog';
  const heading = document.createElement('div'); heading.className = 'dialog-title'; heading.textContent = locale.t('storageFolders');
  const body = document.createElement('div'); body.className = 'dialog-body';
  const error = document.createElement('p'); error.className = 'modal-error'; error.setAttribute('role', 'status');
  const action = (caption: string, run: () => void | Promise<void>) => {
    const button = document.createElement('button'); button.type = 'button'; button.textContent = locale.t(caption);
    button.onclick = () => { try { void Promise.resolve(run()).catch(report); } catch (e) { report(e); } }; return button;
  };
  function report(e: unknown): void { if (e instanceof DOMException && e.name === 'AbortError') return; error.textContent = e instanceof Error ? e.message : String(e); }
  body.append(error);
  heading.append(action('external.close', () => dialog.close())); dialog.append(heading, body); document.body.append(dialog); dialog.showModal();
  dialog.addEventListener('close', () => dialog.remove(), { once: true });
  void initializeWorkspaceFolders().then(async () => {
    const latest = await fetch('/api/workspace-folders');
    if (!latest.ok) throw new Error('Storage folder information is unavailable.');
    info = await latest.json() as FolderInfo;
    if (!dialog.open) return;
    for (const kind of ['maps', 'textures'] as const) {
      const section = document.createElement('section'), title = document.createElement('strong'), field = document.createElement('input');
      title.textContent = kind === 'maps' ? 'Maps' : 'Textures'; field.readOnly = true; field.value = info![kind]; field.title = field.value; field.id = 'storage-' + kind;
      const actions = document.createElement('div'); actions.className = 'tileset-actions';
      actions.append(action('storageOpen', async () => {
        const response = await fetch('/api/workspace-folders/open', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ folder: kind }) });
        if (!response.ok) throw new Error((await response.json()).error);
      }), action('storageCopy', () => navigator.clipboard.writeText(info![kind])));
      if (host.showDirectoryPicker && !host.chrome?.webview && (!nativeMapDialogs() || kind === 'textures')) {
        const choose = action('storageRemember', async () => {
          // The picker must be invoked directly within this click's user activation.
          const handle = await host.showDirectoryPicker!({ id: 'studio-' + kind + '-folder', mode: 'read', ...folderStartIn(kind) });
          await remember(kind, handle); choose.textContent = locale.t('storageRemembered') + ': ' + handle.name;
        }); choose.id = 'storage-choose-' + kind; actions.append(choose);
      }
      section.append(title, field, actions); body.append(section);
      if (kind === focus) field.focus();
    }
    const details = document.createElement('p'); details.className = 'hint'; details.textContent = locale.t('storageBundleHelp');
    const exports = document.createElement('input'); exports.readOnly = true; exports.value = info!.currentExport; exports.id = 'storage-exports'; exports.title = exports.value; exports.setAttribute('aria-label', locale.t('storageAutoExport'));
    const autoLabel = document.createElement('label'); autoLabel.textContent = locale.t('storageAutoExport'); autoLabel.append(exports);
    const exportActions = document.createElement('div'); exportActions.className = 'tileset-actions';
    const openExport = action('storageOpen', async () => {
      const response = await fetch('/api/workspace-folders/open', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ folder: 'exports' }) });
      if (!response.ok) throw new Error((await response.json()).error);
    }); openExport.id = 'storage-open-exports';
    exportActions.append(openExport, action('storageCopy', () => navigator.clipboard.writeText(info!.currentExport)));
    body.append(autoLabel, exportActions, details);
    if (!host.chrome?.webview && host.showDirectoryPicker) { const note = document.createElement('p'); note.className = 'hint'; note.textContent = locale.t('storageBrowserHelp'); body.append(note); }
    body.append(error);
  }).catch(report);
}

/** Open only a validated texture resource folder on the local host. */
export async function openTextureFolder(asset?: string): Promise<void> {
  const response = await fetch('/api/workspace-folders/open', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ folder: 'textures', asset }) });
  if (!response.ok) throw new Error((await response.json()).error);
}
