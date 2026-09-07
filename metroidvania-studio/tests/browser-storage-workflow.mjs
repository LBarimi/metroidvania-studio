import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['localhost','127.0.0.1'].includes(new URL(base).hostname));
const foldersResponse = await fetch(base + '/api/workspace-folders'); assert.ok(foldersResponse.ok);
const folders = await foldersResponse.json();
assert.equal(folders.maps, path.join(folders.project, 'Maps')); assert.equal(folders.textures, path.join(folders.project, 'Textures'));
assert.equal(folders.autoExport, path.join(folders.project, 'Maps', 'AutoExport'));
assert.equal((await fetch(base + '/api/workspace-folders/open', { method: 'POST', headers: {'content-type':'application/json'}, body: JSON.stringify({folder:'../outside'}) })).status, 400);
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'en-US' }); page.setDefaultTimeout(14000);
  const errors=[]; page.on('pageerror',e=>errors.push(e.message));
  await page.addInitScript(() => {
    window.__chosen = 'Maps';
    window.showDirectoryPicker = async () => (await navigator.storage.getDirectory()).getDirectoryHandle(window.__chosen, {create:true});
    window.showOpenFilePicker = async options => { window.__openOptions = options; return []; };
    window.showSaveFilePicker = async options => { window.__saveOptions = options; return (await navigator.storage.getDirectory()).getFileHandle('chosen.png', {create:true}); };
  });
  await page.route('**/api/workspace-folders', async route => { const response = await route.fetch(); const data = await response.json(); data.nativeMapDialogs = false; await route.fulfill({response,json:data}); });
  await page.goto(base); await page.locator('.palette-settings').first().waitFor(); await page.locator('#language').selectOption('EN');
  await page.locator('#file-menu-button').click(); await page.getByRole('menuitem', {name:'Storage folders',exact:true}).click();
  assert.equal(await page.locator('#storage-maps').inputValue(), folders.maps);
  assert.equal(await page.locator('#storage-exports').inputValue(), folders.currentExport);
  await page.route('**/api/workspace-folders/open', async route => {
    assert.equal(route.request().postDataJSON().folder, 'exports'); await route.fulfill({ status: 200, contentType: 'application/json', body: '{}' });
  });
  await page.locator('#storage-open-exports').click(); await page.unroute('**/api/workspace-folders/open');
  await page.locator('#storage-choose-maps').click(); await page.waitForFunction(()=>document.querySelector('#storage-choose-maps').textContent.includes('Remembered folder'));
  await page.evaluate(()=>window.__chosen='Textures'); await page.locator('#storage-choose-textures').click();
  await page.waitForFunction(()=>document.querySelector('#storage-choose-textures').textContent.includes('Remembered folder'));
  await page.keyboard.press('Escape'); await page.reload(); await page.locator('.palette-settings').first().waitFor();
  const remembered = await page.evaluate(async () => {
    const folders = await import('./workspace-folders.js'); await folders.initializeWorkspaceFolders();
    const files = await import('./file-access.js'); await files.pickMapFile(); await files.pickMapSave('map.json');
    return [window.__openOptions.startIn.name, window.__saveOptions.startIn.name, folders.folderStartIn('textures').startIn.name];
  });
  assert.deepEqual(remembered,['Maps','Maps','Textures']);
  await page.locator('.palette-settings').first().click(); await page.waitForFunction(()=>!document.querySelector('#tileset-apply').disabled);
  await page.locator('#tileset-mode').selectOption('four'); await page.locator('#tileset-load-template').click();
  await page.waitForFunction(()=>!document.querySelector('#tileset-apply').disabled&&document.querySelectorAll('.tileset-slot:not(.unassigned)').length===4);
  const before = await page.locator('#tileset-example').evaluate(c=>c.toDataURL()), slots=await page.locator('.tileset-slot small').allTextContents();
  assert.equal(await page.locator('#tileset-source, #tileset-auto-assign, #tileset-assign-selected').count(), 0);
  await page.locator('.tileset-slot').nth(3).click();
  assert.deepEqual(await page.locator('.tileset-slot small').allTextContents(),slots,'Slot selection must not change any rule.');
  assert.equal(await page.locator('#tileset-example').evaluate(c=>c.toDataURL()),before,'Slot selection must preserve the composed preview.');
  const inputTiming = await page.evaluate(() => {
    const color=document.querySelector('#tileset-color'), slots=document.querySelector('#tileset-slots'), first=slots.firstChild;
    const start=performance.now();
    for(let i=0;i<200;i++){color.value='#'+(0x234500+i).toString(16);color.dispatchEvent(new Event('input',{bubbles:true}));}
    const elapsed=performance.now()-start; const unchanged=first===slots.firstChild; color.dispatchEvent(new Event('change',{bubbles:true}));
    return {elapsed,unchanged};
  });
  assert.ok(inputTiming.unchanged,'Color pointer input must not rebuild the slot UI synchronously.');
  assert.ok(inputTiming.elapsed<100,`200 color events took ${inputTiming.elapsed}ms`);
  await page.locator('#tileset-import').click(); assert.equal(await page.evaluate(()=>window.__openOptions.startIn.name),'Textures');
  await page.getByRole('button',{name:'Download PNG',exact:true}).click();
  await page.waitForFunction(()=>window.__saveOptions?.id==='studio-texture');
  assert.equal(await page.evaluate(()=>window.__saveOptions.startIn.name),'Textures');
  await page.keyboard.press('Escape'); assert.deepEqual(errors,[]);
  await page.unroute('**/api/workspace-folders');
  await page.route('**/api/workspace-folders', async route => { const response=await route.fetch(); const data=await response.json(); data.nativeMapDialogs=true; await route.fulfill({response,json:data}); });
  let selectedText='', pickedMode='', cancel=false, writes=0;
  await page.route('**/api/native-map/*', async route => {
    const action=route.request().url().split('/').pop(), data=route.request().postDataJSON();
    if(action==='pick'){pickedMode=data.mode;await route.fulfill({json:cancel?null:{token:'chosen-json',name:'chosen.map.json'}});return;}
    assert.equal(data.token,'chosen-json','Only the OS-selected file grant is used.');
    if(action==='read'){await route.fulfill({contentType:'application/json',body:selectedText});return;}
    assert.equal(action,'write');assert.equal(typeof data.hash,'string');assert.ok(data.document.rooms);selectedText=JSON.stringify(data.document);writes++;await route.fulfill({json:{}});
  });
  await page.reload();await page.locator('.palette-settings').first().waitFor();
  await page.evaluate(async()=>{
    const files=await import('./file-access.js'), handle=await files.pickMapSave('world.map.json');
    const document=(await(await fetch('/api/state?full=true')).json()).document;
    await files.writeMapFile(handle,JSON.stringify(document),await files.fileHash(await handle.getFile()));
  });
  assert.equal(pickedMode,'save');assert.equal(writes,1);assert.ok(selectedText.length>0);
  const name=await page.evaluate(async()=>{const files=await import('./file-access.js');const chosen=await files.pickMapFile();return chosen.file.name;});
  assert.equal(pickedMode,'open');assert.equal(name,'chosen.map.json');
  cancel=true;
  assert.equal(await page.evaluate(async()=>{try{await(await import('./file-access.js')).pickMapSave('cancel.json');return false;}catch(e){return e.name==='AbortError';}}),true);
  assert.equal(writes,1,'Cancel must not trigger a download or save.');assert.deepEqual(errors,[]);
  console.log('PASS: visible storage paths, folder scope rejection, remembered map/PNG dialogs, compact import panel, non-destructive slot selection, color input '+inputTiming.elapsed.toFixed(2)+'ms / 200 events.');
} finally { await browser.close(); }
