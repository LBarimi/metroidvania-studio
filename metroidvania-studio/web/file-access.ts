import { folderStartIn } from './workspace-folders.js';

export interface MapFileHandle {
  name: string;
  getFile(): Promise<File>;
  requestPermission(options: { mode: 'readwrite' }): Promise<string>;
  createWritable(options: { mode: 'exclusive' }): Promise<{ write(data: string): Promise<void>; close(): Promise<void>; abort(): Promise<void> }>;
}
type PickerWindow = Window & {
  showOpenFilePicker?: (options: object) => Promise<MapFileHandle[]>;
  showSaveFilePicker?: (options: object) => Promise<MapFileHandle>;
};
const picker = window as PickerWindow;
const types = [{ description: 'JSON map', accept: { 'application/json': ['.json'] } }];
export const canceledFileDialog = (error: unknown): boolean => error instanceof DOMException && error.name === 'AbortError';
export async function fileHash(file: Blob): Promise<string> {
  if (file.size > 32 * 1024 * 1024) throw new Error('@importTooLarge');
  return [...new Uint8Array(await crypto.subtle.digest('SHA-256', await file.arrayBuffer()))].map(b => b.toString(16).padStart(2, '0')).join('');
}
export async function pickMapFile(): Promise<{ file: File; handle?: MapFileHandle } | null> {
  // Invoke before any network await, while the click still has user activation.
  if (picker.showOpenFilePicker) {
    const [handle] = await picker.showOpenFilePicker({ id: 'studio-map', types, multiple: false, ...folderStartIn('maps') });
    return handle ? { file: await handle.getFile(), handle } : null;
  }
  return new Promise(resolve => {
    const input = document.createElement('input'); input.type = 'file'; input.accept = '.json,application/json'; input.id = 'map-file-open'; input.hidden = true;
    const finish = (file?: File) => { input.remove(); resolve(file ? { file } : null); };
    input.addEventListener('cancel', () => finish(), { once: true });
    input.addEventListener('change', () => finish(input.files?.[0]), { once: true });
    document.body.append(input); input.click();
  });
}
export async function pickMapSave(suggestedName: string, existing?: MapFileHandle): Promise<MapFileHandle | null> {
  if (existing) {
    if (await existing.requestPermission({ mode: 'readwrite' }) !== 'granted') throw new Error('@filePermissionDenied');
    return existing;
  }
  if (!picker.showSaveFilePicker) return null;
  return picker.showSaveFilePicker({ id: 'studio-map', types, suggestedName, ...folderStartIn('maps') });
}
export async function writeMapFile(handle: MapFileHandle, text: string, expectedHash: string): Promise<string> {
  const writtenHash = await fileHash(new Blob([text]));
  if (await fileHash(await handle.getFile()) !== expectedHash) throw new Error('@fileChangedOutside');
  const writer = await handle.createWritable({ mode: 'exclusive' });
  try {
    await writer.write(text);
    if (await fileHash(await handle.getFile()) !== expectedHash) throw new Error('@fileChangedOutside');
    await writer.close();
  } catch (error) { try { await writer.abort(); } catch { /* Preserve the original write failure. */ } throw error; }
  return writtenHash;
}
export function downloadMap(text: string, name: string): void {
  const url = URL.createObjectURL(new Blob([text], { type: 'application/json;charset=utf-8' }));
  const link = document.createElement('a'); link.href = url; link.download = name; link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}
