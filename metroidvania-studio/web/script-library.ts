type SavedScript = { id: string; name: string; source: string; revision: string; readOnly: boolean };
type Entry = Pick<SavedScript, 'id' | 'name' | 'readOnly'>;
type Host = { t: (key: string) => string; source: HTMLTextAreaElement; output: HTMLElement; busy: (value: boolean) => void; closed: () => boolean };
export function scriptLibrary(host: Host) {
  const root = document.createElement('div'); root.className = 'script-library';
  const select = document.createElement('select'); select.id = 'script-library'; select.setAttribute('aria-label', host.t('scripts.library'));
  const name = document.createElement('input'); name.id = 'script-name'; name.value = 'map-script.lua'; name.maxLength = 100; name.setAttribute('aria-label', host.t('scripts.filename'));
  let current: SavedScript | null = null, cleanSource = host.source.value;
  function button(key: string, action: () => void) { const b = document.createElement('button'); b.type = 'button'; b.textContent = host.t(key); b.onclick = action; return b; }
  async function request<T>(url: string, body?: unknown): Promise<T> {
    const response = await fetch(url, { method: body ? 'POST' : 'GET', headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined, signal: AbortSignal.timeout(6000) });
    const value = await response.json(); if (!response.ok) throw new Error(value.error || `Server ${response.status}`); return value;
  }
  function canReplace(): boolean { return host.source.value === cleanSource || confirm(host.t('scripts.discard')); }
  function imported(filename = 'map-script.lua'): void { current = null; select.value = ''; name.value = filename; cleanSource = host.source.value; }
  async function refresh(): Promise<void> {
    const entries = await request<Entry[]>('/api/v1/scripts'); if (host.closed()) return;
    select.replaceChildren(new Option(host.t('scripts.draft'), ''));
    for (const readOnly of [false, true]) {
      const group = document.createElement('optgroup'); group.label = host.t(readOnly ? 'scripts.examples' : 'scripts.library');
      for (const entry of entries.filter(e => e.readOnly === readOnly)) group.append(new Option(entry.name, entry.id));
      if (group.children.length) select.append(group);
    }
    select.value = current?.id || '';
  }
  async function work(action: () => Promise<void>): Promise<void> {
    host.busy(true);
    try { await action(); }
    catch (error) { if (!host.closed()) host.output.textContent = error instanceof Error ? error.message : String(error); }
    finally { host.busy(false); }
  }
  select.onchange = () => {
    const id = select.value;
    if (!id || !canReplace()) { select.value = current?.id || ''; return; }
    void work(async () => {
      const script = await request<SavedScript>('/api/v1/scripts/source?id=' + encodeURIComponent(id));
      if (host.closed()) return;
      current = script; host.source.value = cleanSource = script.source; name.value = script.name; select.value = script.id;
      host.source.dispatchEvent(new Event('input')); host.output.textContent = host.t('scripts.opened');
    });
  };
  const create = button('scripts.new', () => { if (!canReplace()) return; host.source.value = ''; imported(); host.source.dispatchEvent(new Event('input')); host.source.focus(); });
  create.id = 'script-new';
  const save = button('scripts.saveLibrary', () => void work(async () => {
    const chosen = name.value.trim();
    const script = await request<SavedScript>('/api/v1/scripts', { name: chosen, source: host.source.value, expectedRevision: current && !current.readOnly && current.name === chosen ? current.revision : null });
    if (host.closed()) return;
    current = script; cleanSource = script.source; name.value = script.name; await refresh(); host.output.textContent = host.t('scripts.savedLibrary');
  })); save.id = 'script-save-library';
  const reload = button('scripts.refresh', () => void work(refresh)); reload.id = 'script-refresh-library';
  const docs = document.createElement('a'); docs.href = '/docs/scripting--api-reference.html'; docs.target = '_blank'; docs.rel = 'noopener'; docs.textContent = host.t('scripts.apiGuide');
  root.append(select, reload, create, name, save, docs);
  return { root, imported, refresh: () => work(refresh), setBusy: (busy: boolean) => { for (const input of [select, name, save, reload, create]) input.disabled = busy; } };
}
