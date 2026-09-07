export type TilesetMode = 'template' | 'four' | 'blob47';
export interface TilesetSlot { x: number; y: number; asset?: string }
export interface TilesetSettings { mode: TilesetMode; source: string; slots: (TilesetSlot | null)[] }
export function normalizedMask(mask: number): number {
  if ((mask & 5) !== 5) mask &= ~2;
  if ((mask & 20) !== 20) mask &= ~8;
  if ((mask & 80) !== 80) mask &= ~32;
  if ((mask & 65) !== 65) mask &= ~128;
  return mask;
}
export const TILESET_MASKS = Array.from({ length: 256 }, (_, i) => i).filter(i => normalizedMask(i) === i);
const templates = new Map<string, HTMLCanvasElement>();
export function defaultTile(mask: number, shape: number, color: string): HTMLCanvasElement {
  const key = color + ':' + mask + ':' + shape; const cached = templates.get(key); if (cached) return cached;
  const tile = document.createElement('canvas'); tile.width = tile.height = 16;
  const ctx = tile.getContext('2d')!; ctx.fillStyle = color; ctx.fillRect(0, 0, 16, 16);
  const data = ctx.getImageData(0, 0, 16, 16);
  for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) {
    const up = 15 - y, offset = (y * 16 + x) * 4;
    const filled = shape === 1 ? x + up <= 15 : shape === 2 ? up <= x : shape === 3 ? up >= x : shape === 4 ? x + up >= 15 : true;
    if (!filled) { data.data[offset + 3] = 0; continue; }
    const edge = shape ? shape === 1 || shape === 4 ? x + up === 15 : x === up
      : x === 0 && !(mask & 64) || x === 15 && !(mask & 4) || y === 0 && !(mask & 1) || y === 15 && !(mask & 16)
      || x === 0 && y === 0 && !(mask & 128) || x === 15 && y === 0 && !(mask & 2)
      || x === 0 && y === 15 && !(mask & 32) || x === 15 && y === 15 && !(mask & 8);
    if (edge) data.data[offset] = data.data[offset + 1] = data.data[offset + 2] = 255;
  }
  ctx.putImageData(data, 0, 0);
  if (templates.size >= 1024) templates.delete(templates.keys().next().value!);
  templates.set(key, tile); return tile;
}
const canonicalMasks = [124, 112, 127];
export function fourPart(mask: number, x: number, y: number): [number, number] {
  for (let slot = 0; slot < 3; slot++) for (let turn = 0; turn < 4; turn++) {
    const shift = turn * 2;
    if (((canonicalMasks[slot] << shift | canonicalMasks[slot] >> (8 - shift)) & 255) === mask) return [slot, turn];
  }
  const horizontal = !!(mask & (x < 8 ? 64 : 4)), vertical = !!(mask & (y < 8 ? 1 : 16));
  const diagonal = mask & (y < 8 ? x < 8 ? 128 : 2 : x < 8 ? 32 : 8);
  if (!horizontal && !vertical) return [1, y < 8 ? x < 8 ? 3 : 0 : x < 8 ? 2 : 1];
  if (horizontal && vertical) return diagonal ? [3, 0] : [2, y < 8 ? x < 8 ? 0 : 1 : x < 8 ? 3 : 2];
  return [0, !vertical ? y < 8 ? 0 : 2 : x < 8 ? 3 : 1];
}
export function composeTileset(sources: ReadonlyMap<string, HTMLImageElement>, settings: TilesetSettings, color: string): HTMLCanvasElement {
  const atlas = document.createElement('canvas'); atlas.width = 128; atlas.height = 112; const ctx = atlas.getContext('2d')!;
  ctx.imageSmoothingEnabled = false;
  for (let i = 0; i < 51; i++) {
    const left = i % 8 * 16, top = Math.floor(i / 8) * 16, mask = TILESET_MASKS[i] || 0, shape = i < 47 ? 0 : i - 46;
    ctx.drawImage(defaultTile(mask, shape, color), left, top);
    if (settings.mode === 'template') continue;
    const copy = (slot: TilesetSlot | null | undefined, rotation: number) => {
      const source = sources.get(slot?.asset || settings.source);
      if (!source || !slot || slot.x < 0 || slot.y < 0 || slot.x + 16 > source.naturalWidth || slot.y + 16 > source.naturalHeight) return;
      // Transparent source pixels replace the template as well.
      ctx.clearRect(left, top, 16, 16);
      ctx.translate(left + 8, top + 8); ctx.rotate(rotation * Math.PI / 2);
      ctx.drawImage(source, slot.x, slot.y, 16, 16, -8, -8, 16, 16);
    };
    if (settings.mode === 'blob47' || i >= 47) { ctx.save(); copy(settings.slots[settings.mode === 'four' ? i - 43 : i], 0); ctx.restore(); continue; }
    for (let qy = 0; qy < 2; qy++) for (let qx = 0; qx < 2; qx++) {
      const [slot, rotation] = fourPart(mask, qx * 8, qy * 8);
      ctx.save(); ctx.beginPath(); ctx.rect(left + qx * 8, top + qy * 8, 8, 8); ctx.clip(); copy(settings.slots[slot], rotation); ctx.restore();
    }
  }
  return atlas;
}
export function drawTilesetExample(canvas: HTMLCanvasElement, atlas: HTMLCanvasElement): void {
  const pattern = ['111111111', '110000011', '100100001', '100110001', '100011001', '111111111'];
  const ctx = canvas.getContext('2d')!; canvas.width = 288; canvas.height = 192; ctx.imageSmoothingEnabled = false;
  const offsets = [[0,-1],[1,-1],[1,0],[1,1],[0,1],[-1,1],[-1,0],[-1,-1]];
  for (let y = 0; y < pattern.length; y++) for (let x = 0; x < pattern[y].length; x++) {
    if (pattern[y][x] !== '1') continue;
    let mask = 0; offsets.forEach(([dx, dy], i) => { if (pattern[y + dy]?.[x + dx] === '1') mask |= 1 << i; });
    const index = TILESET_MASKS.indexOf(normalizedMask(mask));
    ctx.drawImage(atlas, index % 8 * 16, Math.floor(index / 8) * 16, 16, 16, x * 32, y * 32, 32, 32);
  }
}
