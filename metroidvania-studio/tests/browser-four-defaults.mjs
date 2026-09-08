import assert from 'node:assert/strict';import {createRequire} from 'node:module';import {readFile,writeFile} from 'node:fs/promises';import path from 'node:path';
const {chromium}=createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE||'playwright');
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED,'1');
const base=process.env.METROIDVANIA_STUDIO_BASE_URL;
const state=async()=> (await fetch(base+'/api/state?full=true')).json();
const browser=await chromium.launch({channel:process.platform==='win32'?'msedge':undefined,headless:true});
try {
 const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'en-US'});page.setDefaultTimeout(15000);
 const errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto(base);await page.locator('.palette-settings').first().waitFor();await page.locator('#language').selectOption('EN');
 const ready=async()=>page.waitForFunction(()=>document.querySelector('#tileset-apply')?.disabled===false);
 const source=()=>page.locator('#tileset-image').inputValue();
 const apply=async()=>{const response=page.waitForResponse(r=>r.url().endsWith('/api/command')&&r.request().postDataJSON()?.action==='paletteConfigure');await page.locator('#tileset-apply').click();const result=await response;assert.ok(result.ok(),await result.text());await page.locator('#tileset-dialog').waitFor({state:'detached'});};
 for(const slug of ['grassland','rock','ice-cavern','volcanic','ancient-ruins']) {
  const id='biome-'+slug;await page.locator(`.palette-settings[data-material-id="${id}"]`).click();await ready();
  assert.equal(await page.locator('#tileset-mode').inputValue(),'four');assert.equal(await source(),`Textures/biomes/4-tiles/${slug}.png`);
  assert.equal(await page.locator('.tileset-slot.unassigned').count(),0);
  const four=await page.locator('#tileset-example').evaluate(c=>c.toDataURL());
  await page.locator('#tileset-mode').selectOption('blob47');await ready();
  assert.equal(await source(),`Textures/biomes/47-tiles/${slug}.png`);
  assert.equal(await page.locator('.tileset-slot').count(),47);
  assert.equal(await page.locator('#tileset-example').evaluate(c=>c.toDataURL()),four,'Switching sheet formats preserves the terrain pixels.');
  await apply();
  await page.locator(`.palette-settings[data-material-id="${id}"]`).click();await ready();assert.equal(await page.locator('#tileset-mode').inputValue(),'blob47');
  await page.locator('#tileset-mode').selectOption('four');await ready();
  await page.locator('#tileset-load-template').click();await ready();
  assert.equal(await source(),`Textures/biomes/4-tiles/${slug}.png`,'Loading the theme template must not generate color placeholders.');
  await page.locator('#tileset-color').fill('#647586');await page.locator('#tileset-color').dispatchEvent('change');await apply();
  const saved=(await state()).catalog.materials.find(m=>m.id===id);assert.equal(saved.editorTileset.source,`Textures/biomes/4-tiles/${slug}.png`);
 }
 // A missing paired file must keep the previous usable settings.
 await page.locator('.palette-settings[data-material-id="biome-rock"]').click();await ready();
 await page.route('**/api/asset?path=*',r=>r.request().url().includes('47-tiles')?r.fulfill({status:404,body:''}):r.continue());
 await page.locator('#tileset-mode').selectOption('blob47');await ready();
 await page.waitForFunction(()=>document.querySelector('#tileset-mode').value==='four');
 assert.equal(await source(),'Textures/biomes/4-tiles/rock.png');assert.ok(await page.locator('#tileset-error').textContent());
 await page.keyboard.press('Escape');await page.unroute('**/api/asset?path=*');
 for(const ppu of [6,12,128,16]) {
  await page.locator('#camera-ppu').selectOption(String(ppu));
  let s;for(let i=0;i<100;i++){s=await state();if(s.camera.ppu===ppu)break;await page.waitForTimeout(50);}
  assert.equal(s.camera.ppu,ppu);assert.ok(Math.abs(s.camera.orthographicSize-s.camera.referenceHeight/(2*ppu))<1e-5);
 }
 const ppuValues=await page.locator('#camera-ppu option').evaluateAll(opts=>opts.map(o=>+o.value));assert.ok(ppuValues.includes(6)&&ppuValues.includes(12)&&ppuValues.every(v=>v<=128));
 const current=await state();const grass=current.catalog.materials.find(m=>m.id==='biome-grassland');
 const texture=path.join(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT,grass.editorTileset.source);const original=await readFile(texture);
 const atlasURL=base+'/api/asset?path='+encodeURIComponent(grass.sprites[0].asset);const oldAtlas=Buffer.from(await(await fetch(atlasURL)).arrayBuffer());
 try {
  const replacement=await page.evaluate(async asset=>{const image=new Image();image.src='/api/asset?path='+encodeURIComponent(asset);await image.decode();const c=document.createElement('canvas');c.width=image.width;c.height=image.height;const ctx=c.getContext('2d');ctx.drawImage(image,0,0);ctx.fillStyle='#cc44ee';ctx.fillRect(0,0,16,16);return c.toDataURL().split(',')[1];},grass.editorTileset.source);
  await writeFile(texture,Buffer.from(replacement,'base64'));
  let changed=false;for(let i=0;i<100;i++){await page.waitForTimeout(100);const next=Buffer.from(await(await fetch(atlasURL)).arrayBuffer());if(!next.equals(oldAtlas)){changed=true;break;}}
  assert.ok(changed,'Editing the bound four-source PNG regenerates the rendered atlas.');
 } finally {await writeFile(texture,original);}
 assert.deepEqual(errors,[]);console.log('Four-tile themes: paired formats, persistence, color-placeholder prevention, failed-load rollback, live PNG edits, PPU 6/12/128 passed.');
}finally{await browser.close();}
