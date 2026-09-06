import type { State } from './types.js';

export interface SyncLabel { key: string; tone: 'quiet' | 'busy' | 'ok' | 'error'; error?: string }
export function syncLabels(state: State | null, online: boolean, editing: boolean): { json: SyncLabel } {
  const label = (key: string, tone: SyncLabel['tone'] = 'quiet', error?: string): SyncLabel => ({ key, tone, error });
  if (!online) return { json: label('syncServerOffline', 'error') };
  if (!state) return { json: label('loading') };
  const output = state.export;
  const json = editing ? label('syncEditing', 'busy')
    : output?.phase === 'error' ? label('syncExportError', 'error', output.error || undefined)
    : output?.phase === 'saved' ? label('syncExportSaved', 'ok')
    : output?.phase === 'exporting' ? label('syncExporting', 'busy') : label('syncExportQueued', 'busy');
  return { json };
}
