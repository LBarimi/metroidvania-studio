import type { State, CommandExpectation, CommandValues } from './types.js';

const auxiliaryRequestTimeoutMs = 5000;

async function readLocalResponse<T>(resource: string, reader: (response: Response) => Promise<T>,
  init: RequestInit = {}, timeoutMs = auxiliaryRequestTimeoutMs): Promise<T> {
  const controller = new AbortController(); let timedOut = false;
  const timer = window.setTimeout(() => { timedOut = true; controller.abort(); }, timeoutMs);
  try {
    const response = await fetch(resource, { ...init, signal: controller.signal });
    if (!response.ok) throw new Error(`Local editor server returned ${response.status}.`);
    return await reader(response);
  }
  catch (error) {
    if (timedOut) throw new Error(`The local editor server did not respond within ${timeoutMs / 1000} seconds.`);
    throw error;
  }
  finally { clearTimeout(timer); }
}

export function localText(resource: string, init: RequestInit = {}, timeoutMs = auxiliaryRequestTimeoutMs): Promise<string> {
  return readLocalResponse(resource, response => response.text(), init, timeoutMs);
}

export function localJson<T>(resource: string, init: RequestInit = {}, timeoutMs = auxiliaryRequestTimeoutMs): Promise<T> {
  return readLocalResponse(resource, response => response.json() as Promise<T>, init, timeoutMs);
}

export class EditorApiError extends Error {
  readonly status: number;
  constructor(message: string, status: number) {
    super(message);
    this.name = 'EditorApiError';
    this.status = status;
  }
}

/** One ordered writer. A revision conflict is reported, never silently replayed. */
export class EditorApi extends EventTarget {
  state: State | null = null;
  online = false;
  pending = 0;
  private tail: Promise<unknown> = Promise.resolve();
  private timer = 0;
  private refreshTask: Promise<void> | null = null;
  private stopped = false;
  private requests = new Set<AbortController>();
  private clientId = crypto.randomUUID();

  async start(): Promise<void> { try { await this.refresh(); } finally { this.schedule(); } }
  /** Wait for the ordered writer that was queued when this method was called. */
  async settled(): Promise<void> { await this.tail; if (this.refreshTask) await this.refreshTask; }
  stop(abortPending = true): void {
    this.stopped = true; clearTimeout(this.timer);
    // A page that is being closed must leave an already-started local write
    // alive long enough for the browser to deliver it. Explicit disposal still
    // aborts every request, including a response body that has only partly
    // arrived.
    if (abortPending) { for (const request of this.requests) request.abort(); this.requests.clear(); }
  }
  private schedule(): void { if (!this.stopped) this.timer = window.setTimeout(() => { void this.poll().finally(() => this.schedule()); }, 400); }
  private async poll(): Promise<void> {
    if (this.pending || this.refreshTask || document.hidden) return;
    try { await this.refresh(true); }
    catch (error) { this.dispatchEvent(new CustomEvent('offline', { detail: error })); }
  }
  async refresh(onlyChanges = false): Promise<void> {
    if (this.refreshTask) return this.refreshTask;
    const task = (async () => {
      const query = new URLSearchParams();
      if (onlyChanges && this.state) {
        query.set('since', String(this.state.revision)); query.set('instanceId', this.state.instanceId);
        if (typeof this.state.documentRevision === 'number') query.set('documentRevision', String(this.state.documentRevision));
        if (typeof this.state.catalogRevision === 'number') query.set('catalogRevision', String(this.state.catalogRevision));
      }
      const { response, body } = await this.requestJson('/api/state' + (query.size ? '?' + query.toString() : ''), { cache: 'no-store' });
      if (response.status === 204) return;
      if (!response.ok) throw new Error(`Server ${response.status}`);
      this.accept(body);
    })();
    this.refreshTask = task;
    try { await task; } finally { if (this.refreshTask === task) this.refreshTask = null; }
  }
  command = (action: string, values: CommandValues = {}, expectation?: CommandExpectation): Promise<State> => {
    const commandId = crypto.randomUUID();
    this.pending++; this.dispatchEvent(new Event('busy'));
    const run = this.tail.then(async () => {
      if (this.refreshTask) await this.refreshTask;
      if (!this.state) await this.refresh();
      const expected = expectation || { instanceId: this.state!.instanceId, revision: this.state!.revision };
      const payload = typeof values === 'function' ? values() : values;
      const requestBody = JSON.stringify({ ...payload, action, clientId: this.clientId, commandId,
        expectedInstanceId: expected.instanceId, expectedRevision: expected.revision });
      const request = () => this.requestJson('/api/command', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: requestBody,
        // Keep ordinary edit commands deliverable when the user accepts the
        // browser's unsaved-work warning. Browsers reject keepalive bodies near
        // 64 KiB, so large batch edits use the normal request path.
        keepalive: new TextEncoder().encode(requestBody).byteLength <= 60 * 1024,
      });
      let received: { response: Response; body: any };
      try {
        received = await request();
      } catch (error) {
        // The server can finish a synchronous mutation after a response is lost
        // or the first local request times out. Retry the same command ID once;
        // the server acknowledges a completed ID without applying it twice.
        if (this.stopped) throw error;
        try { received = await request(); }
        catch {
          try { await this.refresh(false); } catch { /* The original transport failure is more useful. */ }
          throw error;
        }
      }
      const { response, body: responseBody } = received;
      if (!response.ok) {
        if (responseBody.state) this.accept(responseBody.state);
        else await this.refresh();
        throw new EditorApiError(typeof responseBody?.error === 'string' ? responseBody.error : `Command failed (${response.status})`, response.status);
      }
      this.accept(responseBody.state || responseBody);
      return this.state!;
    });
    this.tail = run.catch(() => undefined);
    return run.catch(error => { this.dispatchEvent(new CustomEvent('error', { detail: error })); throw error; })
      .finally(() => { this.pending--; this.dispatchEvent(new Event('busy')); });
  };
  private async requestJson(resource: string, init: RequestInit = {}): Promise<{ response: Response; body: any }> {
    if (this.stopped) throw new Error('The editor connection is closed.');
    const controller = new AbortController(); let timedOut = false;
    const timer = window.setTimeout(() => { timedOut = true; controller.abort(); }, 15000); this.requests.add(controller);
    try {
      const response = await fetch(resource, { ...init, signal: controller.signal });
      const body = response.status === 204 ? undefined : await response.json();
      this.setOnline(true);
      return { response, body };
    }
    catch (error) { this.setOnline(false); if (timedOut) throw new Error('The local editor server did not respond within 15 seconds.'); throw error; }
    finally { clearTimeout(timer); this.requests.delete(controller); }
  }
  private setOnline(value: boolean): void {
    if (this.online === value) return;
    this.online = value; this.dispatchEvent(new Event('connection'));
  }
  private accept(update: Partial<State>): void {
    if (!update || typeof update.revision !== 'number' || typeof update.documentRevision !== 'number'
      || typeof update.catalogRevision !== 'number' || typeof update.instanceId !== 'string') throw new Error('Invalid editor response');
    const previous = this.state;
    if (previous && update.instanceId === previous.instanceId && update.revision < previous.revision) return;
    if ((!previous || update.instanceId !== previous.instanceId) && (!update.document || !update.catalog)) throw new Error('Incomplete initial editor response');
    const state = { ...previous, ...update } as State;
    state.document = Object.hasOwn(update, 'document') ? update.document! : previous!.document;
    state.catalog = Object.hasOwn(update, 'catalog') ? update.catalog! : previous!.catalog;
    state.connections = Object.hasOwn(update, 'connections') ? update.connections! : previous?.connections || [];
    this.state = state; this.dispatchEvent(new Event('state'));
  }
}
