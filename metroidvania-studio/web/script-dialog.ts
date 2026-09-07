import { scriptLibrary } from './script-library.js';
type ScriptHost = {
  t: (key: string) => string;
  prepare: () => Promise<{ instanceId: string; documentRevision: number }>;
  refresh: () => Promise<void>;
};
type Submission = { kind: 'lua'; source: string; seed: number; dryRun: boolean; clientId: string; commandId: string; expectedInstanceId: string; expectedDocumentRevision: number };
class ServerRejection extends Error { readonly status: number; constructor(status: number, message: string) { super(message); this.status = status; } }
type Job = { id: string; phase: string; dryRun: boolean; changed: boolean; operationCount: number; logs: string[]; error?: string };
const defaultSource = `local rooms = studio.rooms()
if #rooms > 0 then
  local room = rooms[1]
  studio.tiles.rectangle {
    roomId = room.roomId, layer = "foreground",
    x = 0, y = 0, width = room.width, height = 1
  }
  print("Painted floor in " .. room.name)
else
  print("Create a room first.")
end
`;

export function openScriptDialog(host: ScriptHost): void {
  const existing = document.querySelector<HTMLDialogElement>('#script-dialog');
  if (existing) { existing.focus(); return; }
  const dialog = document.createElement('dialog'); dialog.id = 'script-dialog'; dialog.className = 'script-dialog';
  const heading = document.createElement('div'); heading.className = 'dialog-title'; heading.textContent = host.t('scripts.title');
  const hint = document.createElement('p'); hint.className = 'subtle'; hint.textContent = host.t('scripts.hint');
  const source = document.createElement('textarea'); source.id = 'script-source'; source.spellcheck = false;
  source.setAttribute('aria-label', host.t('scripts.source')); source.value = localStorage.getItem('metroidvania-studio.lua') || defaultSource;
  const toolbar = document.createElement('div'); toolbar.className = 'row script-options';
  function button(key: string, click: () => void): HTMLButtonElement {
    const node = document.createElement('button'); node.type = 'button'; node.textContent = host.t(key); node.onclick = click; return node;
  }
  const output = document.createElement('pre'); output.id = 'script-output'; output.setAttribute('role', 'status'); output.setAttribute('aria-live', 'polite');
  output.textContent = host.t('scripts.ready');
  const seedLabel = document.createElement('label'); seedLabel.textContent = host.t('scripts.seed');
  const seed = document.createElement('input'); seed.type = 'number'; seed.value = '1'; seed.step = '1'; seed.id = 'script-seed'; seedLabel.append(seed);
  const dryLabel = document.createElement('label'); const dry = document.createElement('input'); dry.type = 'checkbox'; dry.id = 'script-dry-run'; dryLabel.append(dry, document.createTextNode(host.t('scripts.dryRun')));
  let job: Job | null = null, starting = false, closed = false, timer = 0, libraryBusy = false;
  let library: ReturnType<typeof scriptLibrary> | null = null;
  let pending: Submission | null = null, cancelWanted = false, recovering = false;
  const clientId = crypto.randomUUID();
  async function request<T>(url: string, body?: unknown): Promise<T> {
    const response = await fetch(url, { method: body === undefined ? 'GET' : 'POST',
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(6000) });
    let value: any;
    try { value = await response.json(); }
    catch (error) { if (response.status >= 400 && response.status < 500) throw new ServerRejection(response.status, `Server ${response.status}`); throw error; }
    if (!response.ok) {
      const message = typeof value.error === 'string' ? value.error : `Server ${response.status}`;
      if (response.status >= 400 && response.status < 500) throw new ServerRejection(response.status, message);
      throw new Error(message);
    }
    return value as T;
  }
  function controls(): void {
    const busy = libraryBusy || starting || pending !== null || job?.phase === 'running';
    library?.setBusy(!!busy);
    run.disabled = !!busy; cancel.disabled = !busy; source.readOnly = !!busy;
    seed.disabled = dry.disabled = load.disabled = save.disabled = !!busy;
  }
  function display(value: Job): void {
    const title = value.error || host.t('scripts.' + value.phase);
    output.textContent = [title, value.phase === 'completed' ? `${host.t('scripts.operations')}: ${value.operationCount}` : '', ...value.logs].filter(Boolean).join('\n');
    controls();
  }
  function schedule(action: () => Promise<void>, delay: number): void {
    clearTimeout(timer);
    if (!closed) timer = window.setTimeout(() => void action(), delay);
  }
  async function cancelKnown(): Promise<void> {
    if (!job || job.phase !== 'running') return;
    const id = job.id;
    try {
      const result = await request<Job>('/api/v1/jobs/' + encodeURIComponent(id) + '/cancel', { clientId });
      if (job?.id !== id) return;
      job = result;
      if (!closed) { display(job); schedule(poll, 100); }
    } catch {
      if (!closed) { output.textContent = host.t('scripts.connectionUnknown'); schedule(poll, 1000); }
    }
  }
  async function recoverForCancellation(): Promise<void> {
    if (!pending || recovering) return;
    recovering = true;
    const body = pending;
    try {
      // Lookup never creates a new job after Close or Cancel. Stop after three
      // attempts when its submission outcome cannot be recovered.
      for (let attempt = 0; attempt < 3 && pending === body; attempt++) {
        try {
          const result = await request<Job>('/api/v1/jobs/lookup?clientId=' + encodeURIComponent(body.clientId) + '&commandId=' + encodeURIComponent(body.commandId));
          if (pending !== body) return;
          job = result; pending = null;
          await cancelKnown();
          if (!closed) { display(job); schedule(poll, 100); }
          return;
        } catch {
          if (!closed) output.textContent = host.t('scripts.connectionUnknown');
          if (attempt < 2) await new Promise(resolve => setTimeout(resolve, 500));
        }
      }
    } finally { recovering = false; controls(); }
  }
  async function poll(): Promise<void> {
    if (!job || closed) return;
    const id = job.id;
    try {
      const result = await request<Job>('/api/v1/jobs/' + encodeURIComponent(id));
      if (job?.id !== id) return;
      job = result; display(job);
      if (job.phase === 'running') {
        if (cancelWanted) await cancelKnown();
        else schedule(poll, 250);
      } else if (job.phase === 'completed' && !job.dryRun) await host.refresh();
    } catch {
      output.textContent = host.t('scripts.connectionUnknown');
      schedule(poll, 1000);
    }
  }
  async function submitPending(): Promise<void> {
    if (!pending || starting) return;
    if (closed || cancelWanted) { await recoverForCancellation(); return; }
    starting = true; controls();
    const body = pending;
    try {
      // The exact client/command identity and payload survive a lost response.
      // Replaying this request recovers its job instead of executing it twice.
      job = await request<Job>('/api/v1/jobs', body);
      pending = null;
      if (closed || cancelWanted) { await cancelKnown(); if (!closed) { display(job); schedule(poll, 0); } }
      else { display(job); schedule(poll, 0); }
    } catch (error) {
      if (error instanceof ServerRejection) {
        pending = null;
        if (!closed) output.textContent = error.message;
      } else {
        if (!closed) output.textContent = host.t('scripts.connectionUnknown');
        if (closed || cancelWanted) await recoverForCancellation();
        else schedule(submitPending, 1000);
      }
    } finally { starting = false; controls(); }
  }
  const run = button('scripts.run', () => { void (async () => {
    if (libraryBusy || starting || pending || job?.phase === 'running') return;
    starting = true; cancelWanted = false; job = null; controls(); output.textContent = host.t('scripts.running');
    try {
      const bytes = new TextEncoder().encode(source.value);
      if (!bytes.length || bytes.length > 65536) throw new Error(host.t('scripts.sourceLimit'));
      const chosenSeed = Number(seed.value);
      if (!Number.isInteger(chosenSeed) || chosenSeed < -2147483648 || chosenSeed > 2147483647) throw new Error(host.t('scripts.seedInvalid'));
      const snapshot = await host.prepare();
      if (closed || cancelWanted) { output.textContent = host.t('scripts.cancelled'); return; }
      localStorage.setItem('metroidvania-studio.lua', source.value);
      pending = { kind: 'lua', source: source.value, seed: chosenSeed, dryRun: dry.checked, clientId,
        commandId: crypto.randomUUID(), expectedInstanceId: snapshot.instanceId, expectedDocumentRevision: snapshot.documentRevision };
    } catch (error) { if (!closed) output.textContent = error instanceof Error ? error.message : String(error); }
    finally { starting = false; controls(); }
    if (pending) await submitPending();
  })(); }); run.id = 'script-run'; run.className = 'accent';
  const cancel = button('scripts.stop', () => {
    cancelWanted = true; clearTimeout(timer); output.textContent = host.t('scripts.cancelPending');
    if (pending && !starting) void recoverForCancellation();
    else if (job) void cancelKnown();
  }); cancel.id = 'script-cancel';
  const load = button('scripts.load', () => {
    const picker = document.createElement('input'); picker.type = 'file'; picker.accept = '.lua,text/plain';
    picker.onchange = () => { void (async () => { try {
      const file = picker.files?.[0]; if (!file) return;
      if (file.size > 65536) throw new Error(host.t('scripts.sourceLimit'));
      if (closed || libraryBusy || starting || pending || job?.phase === 'running') return;
      const importedSource = new TextDecoder('utf-8', { fatal: true }).decode(await file.arrayBuffer());
      if (closed || libraryBusy || starting || pending || job?.phase === 'running') return;
      source.value = importedSource;
      library?.imported(file.name); source.dispatchEvent(new Event('input'));
    } catch (error) { output.textContent = String(error); } })(); }; picker.click();
  });
  const save = button('scripts.save', () => {
    const url = URL.createObjectURL(new Blob([source.value], { type: 'text/plain;charset=utf-8' }));
    const link = document.createElement('a'); link.href = url; link.download = 'map-script.lua'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  const close = button('close', () => dialog.close());
  library = scriptLibrary({ t: host.t, source, output, closed: () => closed, busy: value => { libraryBusy = value; controls(); } });
  source.addEventListener('input', () => { try { localStorage.setItem('metroidvania-studio.lua', source.value); } catch { /* Keep editing when browser storage is full. */ } });
  toolbar.append(load, save, seedLabel, dryLabel);
  const footer = document.createElement('div'); footer.className = 'dialog-footer'; footer.append(cancel, run, close);
  dialog.append(heading, hint, library.root, toolbar, source, output, footer);
  dialog.addEventListener('keydown', event => {
    event.stopPropagation();
    if (event.key === 'Escape') { event.preventDefault(); dialog.close(); }
    if (event.key === 'Tab' && event.target === source && !source.readOnly) {
      event.preventDefault(); source.setRangeText('  ', source.selectionStart, source.selectionEnd, 'end');
    }
  });
  const escape = (event: KeyboardEvent): void => {
    if (event.key === 'Escape' && dialog.open) {
      event.preventDefault(); event.stopImmediatePropagation(); dialog.close();
    }
  };
  document.addEventListener('keydown', escape, true);
  dialog.addEventListener('close', () => {
    document.removeEventListener('keydown', escape, true);
    closed = true; cancelWanted = true; clearTimeout(timer);
    if (pending && !starting) void recoverForCancellation();
    else if (job?.phase === 'running') void cancelKnown();
    dialog.remove();
  });
  document.body.append(dialog); dialog.show(); controls(); source.focus(); void library.refresh();
}
