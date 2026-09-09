export type BundleRevision = { instanceId: string; documentRevision: number; catalogRevision: number };

export function openProjectBundleDialog(prepare: () => Promise<BundleRevision>, t: (key: string) => string): void {
  if (document.getElementById('project-bundle-dialog')) return;
  const dialog = document.createElement('dialog'); dialog.id = 'project-bundle-dialog';
  const title = document.createElement('div'); title.className = 'dialog-title'; title.textContent = t('bundleExport');
  const body = document.createElement('div'); body.className = 'dialog-body';
  const help = document.createElement('p'); help.textContent = t('bundleHelp');
  const note = document.createElement('p'); note.textContent = t('bundleOpenHelp');
  const progress = document.createElement('div'); progress.setAttribute('role', 'status');
  const error = document.createElement('div'); error.className = 'modal-error';
  const footer = document.createElement('div'); footer.className = 'dialog-footer';
  const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = t('cancel'); cancel.onclick = () => dialog.close();
  const start = document.createElement('button'); start.type = 'button'; start.className = 'accent'; start.id = 'project-bundle-download'; start.textContent = t('bundleDownload');
  let controller: AbortController | undefined;
  const errorText = (value: unknown) => {
    const message = value instanceof Error ? value.message : String(value);
    if (!message.startsWith('@')) return t('bundleFailed');
    const [key, ...args] = message.slice(1).split(':');
    return t(key).replace(/\{(\d+)\}/g, (_, index) => args[Number(index)] || '');
  };
  start.onclick = async () => {
    if (controller) return;
    controller = new AbortController(); const active = controller;
    let timedOut = false;
    const timeout = window.setTimeout(() => { timedOut = true; active.abort(); }, 120000);
    start.disabled = true; error.textContent = ''; progress.textContent = t('bundlePreparing');
    try {
      const revision = await prepare();
      if (active.signal.aborted || !dialog.isConnected) return;
      const response = await fetch('/api/project-bundle', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(revision), signal: active.signal });
      if (!response.ok) throw new Error((await response.json()).error);
      const data = await response.blob();
      if (active.signal.aborted || !dialog.isConnected) return;
      const url = URL.createObjectURL(data), link = document.createElement('a');
      link.href = url; link.download = 'project.zip'; link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 60000);
      progress.textContent = t('bundleDownloaded'); cancel.textContent = t('close');
    } catch (reason) {
      if (dialog.isConnected) { progress.textContent = ''; error.textContent = timedOut ? t('bundleTimedOut') : errorText(reason); }
    } finally { window.clearTimeout(timeout); controller = undefined; start.disabled = false; }
  };
  dialog.addEventListener('close', () => { controller?.abort(); dialog.remove(); });
  body.append(help, note, progress, error); footer.append(cancel, start); dialog.append(title, body, footer);
  document.body.append(dialog); dialog.showModal();
}
