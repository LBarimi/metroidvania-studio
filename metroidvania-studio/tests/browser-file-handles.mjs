// Replace only OS-dialog selection; exercise the browser's real file handles and atomic writable streams.
export async function installFilePickers(page) {
  await page.route('**/api/workspace-folders', async route => { const response = await route.fetch(); const data = await response.json(); data.nativeMapDialogs = false; await route.fulfill({response,json:data}); });
  await page.addInitScript(() => {
    const test = window.__filePickers = { saveName: 'saved.map.json', openName: 'opened.map.json', cancel: false, deny: false,
      failWrite: false, afterWriteName: '', saveCalls: 0, openCalls: 0, activeCalls: [], writes: 0,
      supported: { open: typeof window.showOpenFilePicker, save: typeof window.showSaveFilePicker } };
    const handle = async name => (await navigator.storage.getDirectory()).getFileHandle(name, { create: true });
    window.__putMapFile = async (name, text) => { const file = await handle(name), writer = await file.createWritable(); await writer.write(text); await writer.close(); };
    window.__readMapFile = async name => (await (await handle(name)).getFile()).text();
    const wrap = file => ({ name: file.name, getFile: () => file.getFile(),
      requestPermission: async () => test.deny ? 'denied' : 'granted',
      createWritable: async options => {
        if (test.failWrite) throw new DOMException('Test write failure', 'NotAllowedError');
        const stream = await file.createWritable(options);
        return { write: text => stream.write(text), abort: () => stream.abort(), close: async () => {
          await stream.close(); test.writes++;
          if (test.afterWriteName) {
            const state = await (await fetch('/api/state?full=true')).json(), name = test.afterWriteName; test.afterWriteName = '';
            const response = await fetch('/api/command', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({
              action: 'documentProperties', name, clientId: 'concurrent-file-test', commandId: crypto.randomUUID(), expectedInstanceId: state.instanceId, expectedRevision: state.revision }) });
            if (!response.ok) throw new Error('Concurrent edit failed.');
          }
        } };
      } });
    const choose = () => {
      test.activeCalls.push(navigator.userActivation.isActive);
      if (test.cancel) throw new DOMException('Dialog canceled', 'AbortError');
    };
    window.showOpenFilePicker = async () => { choose(); test.openCalls++; return [wrap(await handle(test.openName))]; };
    window.showSaveFilePicker = async options => { choose(); test.saveCalls++; test.lastSaveOptions = options; return wrap(await handle(test.saveName)); };
  });
}
