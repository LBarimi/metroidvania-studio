import {createRequire} from 'node:module';
import assert from 'node:assert/strict';
const {chromium}=createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE||'playwright');
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED,'1');
const base=process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1','localhost'].includes(new URL(base).hostname));
const state=async()=>(await fetch(base+'/api/state')).json();
async function command(action,values={}){
  const current=await state();
  const response=await fetch(base+'/api/command',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,...values,clientId:'fit-browser',commandId:crypto.randomUUID(),expectedRevision:current.revision,expectedInstanceId:current.instanceId})});
  const result=await response.json();assert.ok(response.ok,JSON.stringify(result));return result;
}
const initial=await state(),template=initial.document.rooms[0];
const cell=(x,y)=>({x,y,shape:0,material:initial.catalog.materials[0].id,groupId:''});
const room={...template,id:'fit',name:'Fit room',x:-10,y:4,width:40,height:24,visible:true,locked:false,properties:[],
  foreground:[cell(0,0),cell(30,18)],background:[cell(23,17)],objects:[{id:'12345',definition:'InvisibleWall',layer:2,groupId:'',x:19,y:3,width:2,height:1,rotation:0,scaleX:1,scaleY:1,nodes:[],properties:[]}]};
await command('import',{document:{...initial.document,rooms:[room],layerGroups:[],stylegrounds:[],properties:[]},discard:true});
await command('cameraSettings',{ppu:16,referenceWidth:320,referenceHeight:180});
await command('selectRoom',{id:'fit'}); await command('options',{layer:0,tool:3});
const browser=await chromium.launch({channel:'msedge',headless:true});
const page=await browser.newPage({viewport:{width:1440,height:1000},locale:'en-US'});
const errors=[]; page.on('pageerror',e=>errors.push(e.message));
async function settle(){const current=await state();await page.waitForFunction(r=>Number(document.querySelector('#status-revision')?.textContent.slice(1))>=r,current.revision);}
async function until(predicate){for(let i=0;i<120;i++){const s=await state();if(predicate(s)){await settle();return s;}await new Promise(r=>setTimeout(r,50));}throw Error('Room fit timed out');}
try{
  await page.goto(base); const button=page.locator('#room-fit-resolution'); await button.waitFor();
  assert.match(await button.getAttribute('title'),/20 × 12/);
  const before=(await state()).document;
  await button.click(); const dialog=page.locator('#room-fit-resolution-warning'); await dialog.waitFor();
  assert.match(await dialog.textContent(),/2 tiles and 1 objects/);
  await dialog.getByRole('button',{name:'Cancel',exact:true}).click();
  assert.deepEqual((await state()).document,before,'Cancel preserves contents.');
  await button.click();await dialog.waitFor();
  await command('cameraSettings',{ppu:16,referenceWidth:640,referenceHeight:360});await settle();
  await dialog.getByRole('button',{name:'Continue',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('#room-fit-resolution-warning .modal-error')?.textContent.length>0);
  assert.equal((await state()).document.rooms[0].width,40,'Stale confirmation does not resize.');
  await dialog.getByRole('button',{name:'Cancel',exact:true}).click();
  await command('cameraSettings',{ppu:16,referenceWidth:320,referenceHeight:180});await settle();
  const beforeApply=(await state()).document;
  await button.click();await dialog.waitFor();await dialog.getByRole('button',{name:'Continue',exact:true}).click();
  const fitted=await until(s=>s.document.rooms[0].width===20&&s.document.rooms[0].height===12);
  assert.equal(await page.locator('dialog[open]').count(),0);
  assert.deepEqual([fitted.document.rooms[0].x,fitted.document.rooms[0].y],[-10,4]);
  assert.equal(fitted.document.rooms[0].objects.length,0);assert.equal(fitted.document.rooms[0].foreground.length,1);assert.equal(fitted.document.rooms[0].background.length,0);
  await command('undo');await settle();assert.deepEqual((await state()).document,beforeApply);
  await command('redo');await settle();assert.deepEqual((await state()).document,fitted.document);
  await command('cameraSettings',{ppu:32,referenceWidth:640,referenceHeight:360});await settle();
  await page.waitForFunction(()=>document.querySelector('#room-fit-resolution')?.title.includes('40 × 23'));
  await button.click();await until(s=>s.document.rooms[0].width===40&&s.document.rooms[0].height===23);
  assert.equal(await page.locator('dialog[open]').count(),0,'No warning without deleted contents.');
  assert.deepEqual(errors,[]);
  console.log('PASS room resolution fit: rounding, warning counts, Cancel, stale confirmation, crops, Undo/Redo, PPU and no-deletion resize.');
}finally{await browser.close();}
