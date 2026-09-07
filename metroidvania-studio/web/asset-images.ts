type Entry = { current?: HTMLImageElement; pending?: HTMLImageElement; callbacks: Set<() => void>; timer?: number };

// Decode a replacement before retiring the previous image. A graphics editor can
// briefly lock or truncate a file while saving; failed reads retry without flicker.
export class AssetImages {
  private entries = new Map<string, Entry>();
  private token = '';
  get loading(): boolean { return [...this.entries.values()].some(entry => !!entry.pending); }
  setCatalog(instanceId: string, revision: number, assets: Iterable<string>): void {
    const token = instanceId + ':' + revision;
    if (token === this.token) return;
    if (!this.token.startsWith(instanceId + ':')) this.clear();
    this.token = token;
    const keep = new Set(assets);
    for (const [asset, entry] of this.entries) {
      this.cancel(entry);
      if (!keep.has(asset)) this.entries.delete(asset);
    }
    for (const [asset, entry] of this.entries) this.load(asset, entry);
  }
  get(asset: string, changed?: () => void): HTMLImageElement | undefined {
    if (!asset) return undefined;
    let entry = this.entries.get(asset);
    if (!entry) { entry = { callbacks: new Set() }; this.entries.set(asset, entry); this.load(asset, entry); }
    if (entry.pending && changed) entry.callbacks.add(changed);
    return entry.current;
  }
  private load(asset: string, entry: Entry): void {
    const image = new Image(); entry.pending = image;
    image.onload = () => {
      if (entry.pending !== image) return;
      entry.current = image; entry.pending = undefined;
      const callbacks = [...entry.callbacks]; entry.callbacks.clear();
      for (const changed of callbacks) changed();
    };
    image.onerror = () => {
      if (entry.pending !== image) return;
      entry.timer = window.setTimeout(() => { entry.timer = undefined; this.load(asset, entry); }, 1000);
    };
    image.src = '/api/asset?path=' + encodeURIComponent(asset) + '&v=' + encodeURIComponent(this.token);
  }
  private cancel(entry: Entry): void {
    if (entry.pending) { entry.pending.onload = null; entry.pending.onerror = null; entry.pending = undefined; }
    window.clearTimeout(entry.timer); entry.timer = undefined; entry.callbacks.clear();
  }
  clear(): void { for (const entry of this.entries.values()) this.cancel(entry); this.entries.clear(); }
}
