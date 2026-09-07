import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const root = path.resolve(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT || '.');
const relative = path.relative(path.join(repository, 'metroidvania-studio/.local'), root);
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state', { signal: AbortSignal.timeout(10000) })).json();
async function command(action, values = {}) {
 const current = await state();
 const response = await fetch(base + '/api/command', { method:'POST', headers:{'Content-Type':'application/json'},
  body:JSON.stringify({action,...values,clientId:'multi-room-test',commandId:crypto.randomUUID(),expectedRevision:current.revision,expectedInstanceId:current.instanceId}),
  signal:AbortSignal.timeout(10000) });
 const result = await response.json();assert(response.ok,JSON.stringify(result));return result;
}
const initial = await state();
const template = initial.document.rooms[0];
const document = {...initial.document,name:'Room selection',layerGroups:[],stylegrounds:[],properties:[],rooms:['A','B','C'].map((id,i)=>({
 ...template,id,name:id,x:i*12,y:0,width:8,height:8,visible:true,locked:false,properties:[],
 foreground:[{x:1,y:1,shape:0,material:'terrain',groupId:''}],background:[],
 objects:[{id:'object-'+id,definition:initial.catalog.objects.find(o=>o.layer===3)?.id || 'test-trigger',layer:3,x:2,y:2,width:1,height:1,rotation:0,scaleX:1,scaleY:1,groupId:'',nodes:[],properties:[]}]
}))};
const browser = await chromium.launch({channel:'msedge',headless:true});
const page = await browser.newPage({viewport:{width:1600,height:1000},locale:'ko-KR'});
const errors=[], requests=[], checks=[];
page.on('pageerror',e=>errors.push(e.message));
page.on('request',r=>{if(r.url().endsWith('/api/command'))requests.push(r.postDataJSON());});
const paint = () => page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
async function at(revision) {
 await page.waitForFunction(r=>Number(document.querySelector('#status-revision')?.textContent.slice(1))>=r && !document.querySelector('#status-state')?.classList.contains('working'),revision);
 await paint();
}
async function until(predicate) {
 for(let i=0;i<100;i++) { const current=await state();if(predicate(current)){await at(current.revision);return current;}await new Promise(r=>setTimeout(r,50)); }
 throw Error('Room selection did not reach the expected state: '+JSON.stringify((await state()).selection));
}
let center={x:4,y:4};
async function point(x,y) { const r=await page.locator('#map-canvas').boundingBox();return {x:r.x+r.width/2+(x-center.x)*32,y:r.y+r.height/2-(y-center.y)*32}; }
async function click(x,y,mod) { const p=await point(x,y);if(mod)await page.keyboard.down(mod);try{await page.mouse.click(p.x,p.y);}finally{if(mod)await page.keyboard.up(mod);}await paint(); }
async function drag(x,y,dx,dy,beforeRelease) { const a=await point(x,y),b=await point(x+dx,y+dy);await page.mouse.move(a.x,a.y);await page.mouse.down();await page.mouse.move(b.x,b.y,{steps:8});await paint();if(beforeRelease)await beforeRelease();await page.mouse.up(); }
async function selectGroup() {
 await page.locator('#room-list button').filter({hasText:/^A/}).click();await until(s=>s.selection.roomId==='A'&&s.selection.roomIds.length===1);center={x:4,y:4};
 await click(14,3,'Control');await until(s=>s.selection.roomIds.length===2);
}
try {
 await command('import',{document,discard:true});await command('selectRoom',{id:'A'});await command('options',{tool:3,layer:0});
 await page.goto(base);await page.locator('#room-list button').first().waitFor();await at((await state()).revision);
 const before=await state(),snapshot=JSON.stringify(before.document);
 await click(14,3,'Control');await until(s=>s.selection.roomIds.length===2);
 assert.equal(await page.locator('#room-list button.selected').count(),2);
 assert.equal(await page.locator('#room-list button[aria-pressed="true"]').count(),2);
 await click(14,3,'Control');await until(s=>s.selection.roomIds.length===1&&s.selection.roomId==='A');
 await click(14,3,'Meta');await until(s=>s.selection.roomIds.length===2);

 for (const modifier of ['Control', undefined]) {
  requests.length=0;
  await click(-2,-2,modifier);await until(s=>s.selection.roomIds.length===0);
  assert.equal(await page.locator('#room-list button.selected').count(),0);
  assert.equal(JSON.stringify((await state()).document),snapshot);assert.equal((await state()).documentRevision,before.documentRevision);
  assert.deepEqual(requests.map(r=>r.action),['cancel']);
  await selectGroup();
 }
 checks.push('Ctrl/Cmd-click toggles rooms; ordinary and Ctrl-held empty clicks clear every selected row without painting or modifying the map');
 await at((await command('options',{tool:0})).revision);await selectGroup();requests.length=0;
 await click(-2,-2);await until(s=>s.selection.roomIds.length===0);
 assert.deepEqual(requests.map(r=>r.action),['cancel']);assert.equal(JSON.stringify((await state()).document),snapshot);
 await at((await command('options',{tool:3})).revision);await selectGroup();
 checks.push('Empty clicks with the Rooms tool clear a group instead of creating a room');
 const outside=await point(-2,-2);
 for (const button of ['middle','right']) {
  requests.length=0;
  await page.mouse.move(outside.x,outside.y);await page.mouse.down({button});
  await page.mouse.move(outside.x+64,outside.y+32,{steps:4});
  await page.mouse.move(outside.x,outside.y,{steps:4});await page.mouse.up({button});await paint();
  assert.equal((await state()).selection.roomIds.length,2);
  assert.equal(JSON.stringify((await state()).document),snapshot);assert.deepEqual(requests,[]);
  assert.equal(await page.locator('#room-context-menu').count(),0);
 }
 checks.push('Middle and right dragging across empty space keep the group selected and never open a context menu');
 await page.evaluate(async()=>{
  const {MapCanvas}=await import('/map-canvas.js');const original=MapCanvas.prototype.outline;
  MapCanvas.prototype.outline=function(rect,color,dashed){if(color==='#ffffff'&&dashed){window.__groupPreview ||= [];window.__groupPreview.push({...rect});}return original.call(this,rect,color,dashed);};
 });
 requests.length=0;
 await drag(14,3,6,0,async()=>{
  const preview=await page.evaluate(()=>window.__groupPreview.slice(-2).map(r=>[r.x,r.y]));
  assert.deepEqual(preview,[[4,0],[16,0]],'Every selected room previews the same resolved delta.');
  assert.equal(JSON.stringify((await state()).document),snapshot,'A drag only commits on release.');
 });
 const moved=await until(s=>s.document.rooms[0].x===4);
 assert.deepEqual(moved.document.rooms.map(r=>r.x),[4,16,24]);assert.equal(moved.selection.roomIds.length,2);
 assert.deepEqual(requests.map(r=>r.action),['roomMove']);assert.equal(requests[0].selected,true);
 assert(moved.document.rooms.every(r=>r.foreground.length===1&&r.foreground[0].x===1&&r.objects[0].x===2));
 await page.keyboard.press('Control+z');await until(s=>JSON.stringify(s.document)===snapshot);
 await page.keyboard.press('Control+y');await until(s=>s.document.rooms[0].x===4);
 await page.keyboard.press('Control+z');await until(s=>JSON.stringify(s.document)===snapshot);
 checks.push('Dragging a selected secondary room moves the whole group, previews collision resolution, preserves all local contents and uses one command and one Undo/Redo');
 await drag(14,3,1,2,async()=>{await page.keyboard.press('Escape');});
 await until(s=>s.selection.roomIds.length===0);assert.equal(JSON.stringify((await state()).document),snapshot);
 checks.push('Escape cancels a held group drag and clears selection without a partial move');
 await selectGroup();await page.locator('#map-canvas').focus();await page.keyboard.press('b');await until(s=>s.selection.roomIds.length===1);
 await click(14,3);await until(s=>s.document.rooms[1].foreground.length===2);
 await page.keyboard.press('Control+z');await until(s=>JSON.stringify(s.document)===snapshot);
 checks.push('B returns to ordinary single-room painting after a group selection');
 await selectGroup();await at((await command('roomProperties',{id:'A',locked:true})).revision);
 const locked=JSON.stringify((await state()).document);requests.length=0;
 await drag(14,3,1,2);assert.equal(JSON.stringify((await state()).document),locked);assert(!requests.some(r=>r.action==='roomMove'));
 await at((await command('roomProperties',{id:'A',locked:false})).revision);
 checks.push('A locked member prevents the complete group drag without moving the remaining rooms');
 await selectGroup();
 await page.keyboard.down('Control');await page.locator('#room-list button').filter({hasText:/^B/}).click();await page.keyboard.up('Control');
 await until(s=>s.selection.roomIds.length===1&&s.selection.roomId==='A');
 await click(14,3,'Control');await until(s=>s.selection.roomIds.length===2);
 await page.locator('#room-list button').filter({hasText:/^A/}).click();await until(s=>s.selection.roomIds.length===1&&s.selection.roomId==='A');
 assert.equal(JSON.stringify((await state()).document),snapshot);
 checks.push('Room-list Ctrl-click toggles without reframing; ordinary room selection replaces the group');
 assert.deepEqual(errors,[]);console.log(JSON.stringify({passed:checks.length,checks,errors},null,2));
} finally {
 await browser.close();await command('open',{path:initial.file,discard:true});
}
