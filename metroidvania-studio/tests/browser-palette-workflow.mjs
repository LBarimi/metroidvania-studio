import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {chromium}=createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED,'1');
const base=process.env.METROIDVANIA_STUDIO_BASE_URL;
const browser=await chromium.launch({channel:process.platform==='win32'?'msedge':undefined,headless:true});
try {
 const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'ko-KR'}); page.setDefaultTimeout(12000);
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(base);await page.locator('#room-list button').first().waitFor();await page.locator('#language').selectOption('KR');
 assert.equal(await page.locator('#brush-options .danger').count(),0);
 assert.equal(await page.locator('#brush-options #add-palette-group').textContent(),'+ 그룹');
 assert.equal(await page.locator('[data-group-id="default"] .palette-group-name').textContent(),'디폴트 색상');
 assert.equal(await page.locator('[data-group-id="default-themes"] .palette-group-name').textContent(),'디폴트 테마');
 assert.equal(await page.locator('[data-group-id="default-themes"] .palette-entry').count(),5);
 assert.ok(await page.locator('#inspector .danger').count()>0);
 const opened=[];await page.route('**/api/workspace-folders/open',r=>{opened.push(r.request().postDataJSON());return r.fulfill({json:{}});});
 for(const slug of ['grassland','rock','ice-cavern','volcanic','ancient-ruins']) {
  await page.locator(`.palette-settings[data-material-id="biome-${slug}"]`).click();
  await page.waitForFunction(()=>document.querySelector('#tileset-apply')?.disabled===false);
  assert.equal(await page.getByRole('button',{name:'저장 폴더',exact:true}).count(),0);
  assert.equal(await page.locator('#tileset-mode').inputValue(),'blob47');
  await page.locator('#tileset-open-folder').click();
  await page.waitForTimeout(30);
  assert.deepEqual(opened.at(-1),{folder:'textures',asset:`Textures/biomes/47-tiles/${slug}.png`});
  assert.equal(await page.locator('#tileset-dialog .modal-error').textContent(),'');
  await page.keyboard.press('Escape');
 }
 assert.deepEqual(await page.locator('#camera-resolution optgroup[label="16:9"] option').evaluateAll(opts=>opts.map(o=>o.value)),['320x180','640x360','1280x720','1600x900','1920x1080','2560x1440']);
 for(const ratio of ['4:3','16:10','3:2']) {
  const sizes=await page.locator(`#camera-resolution optgroup[label="${ratio}"] option`).evaluateAll(opts=>opts.map(o=>o.value.split('x').map(Number)));
  const [a,b]=ratio.split(':').map(Number);
  assert.ok(sizes.every(([w,h],i)=>w>=320&&w*b===h*a&&(i===0||w>sizes[i-1][0])));
 }
 await page.locator('#language').selectOption('EN');
 await page.route('**/api/workspace-folders',async r=>{const res=await r.fetch();await r.fulfill({response:res,json:{...await res.json(),nativeMapDialogs:true}});});
 await page.reload();await page.locator('#room-list button').first().waitFor();
 let held, mode='wait';const calls=[];
 await page.route('**/api/native-map/pick',async r=>{
  calls.push(r.request().postDataJSON().mode);
  if(mode==='wait') {held=r;return;}
  if(mode==='error') return r.fulfill({status:500,json:{error:'Picker test failure'}});
  return r.fulfill({json:null});
 });
 const menu=async name=>{await page.locator('#file-menu-button').click();await page.getByRole('menuitem',{name,exact:true}).click();};
 await menu('Open map');await page.locator('#file-dialog-wait').waitFor();
 assert.equal(calls.at(-1),'open');await page.locator('#file-dialog-wait button').click();
 await page.locator('#file-dialog-wait').waitFor({state:'detached'});await held.fulfill({json:null}).catch(()=>{});
 mode='cancel';await menu('Save map as');
 await page.waitForFunction(()=>!document.querySelector('#file-dialog-wait'));
 for(let i=0;i<50&&calls.at(-1)!=='save';i++)await page.waitForTimeout(20);
 assert.equal(calls.at(-1),'save','Cancel releases the open/save busy state.');
 mode='error';await menu('Open map');await page.locator('#toast').filter({hasText:'Picker test failure'}).waitFor();
 mode='cancel';await menu('Open map');await page.waitForTimeout(100);
 assert.equal(await page.locator('#file-dialog-wait').count(),0);
 assert.deepEqual(calls,['open','save','open','open']);assert.deepEqual(errors,[]);
 console.log('Palette workflow: built-in themes, buttons, resource folders, resolution presets and file dialog cancellation/retry passed.');
} finally {await browser.close();}
