import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base=process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED,'1');
assert.ok(['localhost','127.0.0.1'].includes(new URL(base).hostname));
const state=async()=> (await fetch(base+'/api/state?full=true')).json();
async function command(action,values={}) {
  const s=await state(); const response=await fetch(base+'/api/command',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({action,...values,clientId:'tileset-test',commandId:crypto.randomUUID(),expectedRevision:s.revision,expectedInstanceId:s.instanceId})});
  assert.ok(response.ok,await response.text());
}
await command('options',{layer:0,groupId:''});
await command('paletteAdd',{name:'Tileset workflow',color:'#315f84'});
const initial=await state(),id=initial.selection.material;
const browser=await chromium.launch({channel:process.platform==='win32'?'msedge':undefined,headless:true});
try {
  const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'ko-KR'});page.setDefaultTimeout(15000);
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base);await page.locator('.palette-settings').first().waitFor();
  await page.locator('#language').selectOption('KR');
  const open=async()=>{await page.locator(`.palette-settings[data-material-id="${id}"]`).click(); await page.waitForFunction(()=>!document.querySelector('#tileset-apply')?.disabled);};
  const apply=async()=>{
    const response=page.waitForResponse(r=>r.url().endsWith('/api/command')&&r.request().postDataJSON()?.action==='paletteConfigure');
    await page.locator('#tileset-apply').click();const result=await response;assert.ok(result.ok(),await result.text());await page.locator('#tileset-dialog').waitFor({state:'detached'});
  };
  await open();await page.locator('#tileset-mode').selectOption('four');
  await page.locator('#tileset-apply').click();assert.match(await page.locator('#tileset-error').innerText(),/4개/);
  await page.locator('#tileset-load-template').click();await page.waitForFunction(()=>!document.querySelector('#tileset-apply').disabled && document.querySelector('#tileset-source').width===64);
  assert.equal(await page.locator('.tileset-slot').count(),4);
  assert.deepEqual(await page.locator('.tileset-slot span').allTextContents(),['위쪽 표면','바깥 모서리 ↗','안쪽 모서리 ↖','내부 채움']);
  await page.locator('#tileset-slope-tab').click();assert.equal(await page.locator('.tileset-slot').count(),4);
  await page.locator('#tileset-solid-tab').click();
  if(process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT)await page.screenshot({path:process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT});
  await apply();
  let saved=(await state()).catalog.materials.find(m=>m.id===id);
  assert.equal(saved.editorTileset.mode,'four');assert.equal(saved.sprites.length,51);
  const full=await state();assert.deepEqual(full.document,initial.document);assert.equal(full.documentRevision,initial.documentRevision);
  async function comparePreview(material) {
    const result=await page.evaluate(async material=>{
      const {composeTileset}=await import('/tileset-preview.js'); const settings=material.editorTileset;
      const images=new Map();for(const key of new Set([settings.source,...settings.slots.map(s=>s?.asset).filter(Boolean)])){
        const image=new Image();image.src='/api/asset?path='+encodeURIComponent(key)+'&test='+Date.now();await image.decode();images.set(key,image);
      }
      const preview=composeTileset(images,settings,material.color),pc=preview.getContext('2d');
      const atlas=new Image();atlas.src='/api/asset?path='+encodeURIComponent(material.sprites[0].asset)+'&test='+Date.now();await atlas.decode();
      const c=document.createElement('canvas');c.width=atlas.width;c.height=atlas.height;const ctx=c.getContext('2d');ctx.drawImage(atlas,0,0);
      for(let i=0;i<51;i++){
        const s=material.sprites[i],expected=pc.getImageData(i%8*16,Math.floor(i/8)*16,16,16).data,actual=ctx.getImageData(s.x,atlas.height-s.y-16,16,16).data;
        const diff=expected.findIndex((v,k)=>v!==actual[k]);
        if(diff>=0)return {index:i,shape:s.shape,mask:s.mask,channel:diff,expected:expected[diff],actual:actual[diff],source:settings.slots[i]};
      }return null;
    },material);
    assert.equal(result,null,'Browser preview and saved atlas must match every pixel: '+JSON.stringify(result));
  }
  await comparePreview(saved);
  // Use deliberately asymmetric opaque pixels, so accidental rotations or mirroring cannot pass unnoticed.
  const strips=await page.evaluate(()=>{
    const c=document.createElement('canvas');c.width=64;c.height=16;const ctx=c.getContext('2d'),pixels=ctx.createImageData(64,16);
    for(let y=0;y<16;y++)for(let x=0;x<64;x++){const i=(y*64+x)*4;pixels.data.set([20+Math.floor(x/16)*50,x%16*15,y*15,255],i);}ctx.putImageData(pixels,0,0);
    const atlas=c.toDataURL().split(',')[1],separate=[];
    for(let i=0;i<4;i++){const p=document.createElement('canvas');p.width=p.height=16;p.getContext('2d').drawImage(c,i*16,0,16,16,0,0,16,16);separate.push(p.toDataURL().split(',')[1]);}
    return{atlas,separate};
  });
  await open();await page.locator('#tileset-file').setInputFiles({name:'strip.png',mimeType:'image/png',buffer:Buffer.from(strips.atlas,'base64')});
  await page.waitForFunction(()=>!document.querySelector('#tileset-apply').disabled);
  await apply(); saved=(await state()).catalog.materials.find(m=>m.id===id);await comparePreview(saved);
  const atlasBytes=Buffer.from(await(await fetch(base+'/api/asset?path='+encodeURIComponent(saved.sprites[0].asset))).arrayBuffer());
  await open();await page.locator('#tileset-file').setInputFiles(strips.separate.map((buffer,i)=>({name:`part-${i+1}.png`,mimeType:'image/png',buffer:Buffer.from(buffer,'base64')})));
  await page.waitForFunction(()=>!document.querySelector('#tileset-apply').disabled&&document.querySelector('#tileset-image').options.length===4);
  await page.locator('.tileset-slot[data-slot="2"]').click();assert.match(await page.locator('#tileset-image option:checked').innerText(),/part-3/);
  await page.setViewportSize({width:760,height:800});
  const layout=await page.locator('#tileset-dialog').evaluate(d=>({width:d.getBoundingClientRect().width,inner:d.querySelector('.dialog-body').scrollWidth,visible:d.querySelector('.dialog-body').clientWidth}));
  assert.ok(layout.width<=760&&layout.inner<=layout.visible+1,'The settings dialog must fit a narrow viewport.');
  await page.setViewportSize({width:1440,height:1000});await apply();
  saved=(await state()).catalog.materials.find(m=>m.id===id);assert.equal(new Set(saved.editorTileset.slots.slice(0,4).map(s=>s.asset)).size,4);await comparePreview(saved);
  const separateBytes=Buffer.from(await(await fetch(base+'/api/asset?path='+encodeURIComponent(saved.sprites[0].asset))).arrayBuffer());assert.deepEqual(separateBytes,atlasBytes);
  await page.reload();await page.locator('.palette-settings').first().waitFor();await open();assert.equal(await page.locator('#tileset-mode').inputValue(),'four');assert.equal(await page.locator('#tileset-image option').count(),4);
  await page.locator('#tileset-file').setInputFiles({name:'invalid.png',mimeType:'image/png',buffer:Buffer.from('incomplete')});
  await page.waitForFunction(()=>document.querySelector('#tileset-error').textContent.length>0);await page.keyboard.press('Escape');
  assert.deepEqual((await state()).catalog.materials.find(m=>m.id===id),saved);
  await open();await page.locator('#tileset-mode').selectOption('blob47');await page.locator('#tileset-load-template').click();
  await page.waitForFunction(()=>!document.querySelector('#tileset-apply').disabled&&document.querySelector('#tileset-source').width===128);
  assert.equal(await page.locator('.tileset-slot').count(),47);await apply();saved=(await state()).catalog.materials.find(m=>m.id===id);await comparePreview(saved);
  const unchanged=await state();await page.locator('#default-tile-view').click();assert.equal(await page.locator('#default-tile-view').getAttribute('aria-pressed'),'true');
  await page.locator('#default-tile-view').click();assert.deepEqual((await state()).document,unchanged.document);
  // Edit the workspace original, then wait for the atlas and browser resource revision together.
  const sourceFile=path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT,saved.editorTileset.source);
  assert.ok(sourceFile.startsWith(path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT)+path.sep));
  const replacement=await page.evaluate(async asset=>{
    const img=new Image();img.src='/api/asset?path='+encodeURIComponent(asset);await img.decode();const c=document.createElement('canvas');c.width=img.width;c.height=img.height;const ctx=c.getContext('2d');ctx.drawImage(img,0,0);ctx.fillStyle='#ff1188';ctx.fillRect(0,0,16,16);return c.toDataURL().split(',')[1];
  },saved.editorTileset.source);
  await writeFile(sourceFile,Buffer.from(replacement,'base64'));
  await page.waitForFunction(async previous=>(await(await fetch('/api/state?full=true')).json()).catalogRevision>previous,unchanged.catalogRevision);
  let refreshed=false;
  for(let attempt=0;attempt<80&&!refreshed;attempt++) {
    refreshed=await page.evaluate(async asset=>{
    const img=new Image();img.src='/api/asset?path='+encodeURIComponent(asset)+'&probe='+Date.now();await img.decode();
    const c=document.createElement('canvas');c.width=c.height=16;const ctx=c.getContext('2d');ctx.drawImage(img,0,img.height-16,16,16,0,0,16,16);
    const p=ctx.getImageData(0,0,1,1).data;return p[0]===255&&p[1]===17&&p[2]===136;
    },saved.sprites[0].asset);
    if(!refreshed)await page.waitForTimeout(100);
  }
  assert.ok(refreshed,'The generated atlas must publish changed pixels.');
  await comparePreview(saved);assert.deepEqual((await state()).document,unchanged.document);
  if(process.env.METROIDVANIA_STUDIO_TEST_TILESET){
    await open();await page.locator('#tileset-mode').selectOption('four');
    await page.locator('#tileset-file').setInputFiles({name:'reference.png',mimeType:'image/png',buffer:await readFile(process.env.METROIDVANIA_STUDIO_TEST_TILESET)});
    await page.waitForFunction(()=>!document.querySelector('#tileset-apply').disabled&&document.querySelector('#tileset-source').width===64);
    if(process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT)await page.screenshot({path:process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT});
    await apply();await comparePreview((await state()).catalog.materials.find(m=>m.id===id));
  }
  assert.deepEqual(errors,[]);
  console.log('PASS: palette UI, four and 47 modes, atlas/separate PNGs, pixel parity, slopes, narrow layout, persistence, cancel, live image rebuild and view-only toggle.');
} finally {await browser.close();}
