import type { Cell, State } from './types.js';

// Match compact System.Text.Json strings, including UTF-16 escapes for astral
// characters and the encoder's blocked Unicode categories. Cache identifiers:
// pointer input measures only changed cells, never a room or a document.
const encoder = new TextEncoder();
const blocked = /[\p{Cc}\p{Co}\p{Cn}\p{Zl}\p{Zp}\p{Zs}]/u;
export class TileByteCounter {
  private strings = new Map<string, number>();
  stringBytes(value: string): number {
    const cached = this.strings.get(value); if (cached !== undefined) return cached;
    let bytes = 2;
    for (const ch of value) {
      const code = ch.codePointAt(0)!;
      if (code > 0xffff) bytes += 12;
      else if ('"\\\b\t\n\f\r'.includes(ch)) bytes += 2;
      else if (code !== 32 && (blocked.test(ch) || code >= 0xd800 && code <= 0xdfff)) bytes += 6;
      else bytes += encoder.encode(ch).length;
    }
    this.strings.set(value, bytes); return bytes;
  }
  cellBytes(cell: Cell): number {
    // {"x":N,"y":N,"shape":N,"material":S,"groupId":S}
    return 43 + String(cell.x).length + String(cell.y).length + String(cell.shape).length
      + this.stringBytes(cell.material) + this.stringBytes(cell.groupId || '');
  }
  propertiesBytes(properties: {key: string; value: string}[]): number {
    return 2 + Math.max(0, properties.length - 1) + properties.reduce((sum, p) =>
      sum + 17 + this.stringBytes(p.key) + this.stringBytes(p.value), 0);
  }
}

interface SizeDelta { from: number; to: number; roomId: string; bytes: number }
export class LiveRoomSizes {
  private instance = '';
  private deltas: SizeDelta[] = [];
  commit(instance: string, delta: SizeDelta): void {
    if (this.instance !== instance) { this.instance = instance; this.deltas = []; }
    if (delta.to > delta.from) this.deltas.push(delta);
  }
  read(state: State, preview?: { version: number; roomId: string; bytes: number }) {
    if (this.instance !== state.instanceId) { this.instance = state.instanceId; this.deltas = []; }
    const base = state.export;
    const measured = base.measuredVersion ?? (base.sizesPending ? -1 : base.version);
    this.deltas = this.deltas.filter(delta => delta.to > measured);
    let version = measured, total = base.totalBytes, selected = base.selectedBytes;
    const selectedIds = new Set(state.selection.roomIds || []);
    const add = (delta: {roomId: string; bytes: number}) => {
      if (total != null) total += delta.bytes;
      if (selected != null && selectedIds.has(delta.roomId)) selected += delta.bytes;
    };
    for (const delta of this.deltas) {
      if (delta.from !== version) break;
      add(delta); version = delta.to;
    }
    if (preview && preview.version === version) add(preview);
    return { selectedBytes: selected, totalBytes: total,
      pending: version !== base.version || total == null || selected == null };
  }
}
