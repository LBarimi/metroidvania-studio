import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {readFile} from 'node:fs/promises';
import path from 'node:path';
const {chromium}=createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED,'1');
const base=process.env.METROIDVANIA_STUDIO_BASE_URL;
const current=async()=> (await fetch(base+'/api/state')).json();
const initial=await current();
async function command(action,values={}) {
 const s=await current(),r=await fetch(base+'/api/command',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,...values,clientId:'live-size-check',commandId:crypto.randomUUID(),expectedInstanceId:s.instanceId,expectedRevision:s.revision})});
 const result=await r.json();assert.ok(r.ok,JSON.stringify(result));return result.state||result;
}
async function until(predicate) {for(let i=0;i<160;i++){const s=await current();if(predicate(s))return s;await new Promise(r=>setTimeout(r,50));}throw Error('State deadline');}
const doc=structuredClone(initial.document),room=doc.rooms[0];
doc.rooms=[room];room.width=80;room.height=30;room.foreground=[];room.background=[];room.objects=[];room.properties=[];
doc.layerGroups=[];
await command('import',{document:doc,discard:true});
await command('selectRoom',{id:room.id});
await command('options',{tool:3,layer:0,brushSize:1,shape:0,material:'biome-rock',groupId:'',hiddenLayers:[],lockedLayers:[]});
const browser=await chromium.launch({channel:'msedge',headless:true});
const page=await browser.newPage({viewport:{width:1440,height:900},locale:'en-US'});
const errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.route('**/app.js',async route=>{const response=await route.fetch();await route.fulfill({response,body:await response.text()+'\nwindow.__sizeMap=map;window.__sizeApi=api;'});});
const sizes=()=>page.evaluate(()=>window.__sizeMap.roomSizes);
const point=async(x,y)=>page.evaluate(({x,y})=>{const map=window.__sizeMap,b=map.canvas.getBoundingClientRect(),p=map.toScreen({x,y});return{x:b.x+p.x,y:b.y+p.y};},{x:room.x+x,y:room.y+y});
async function move(x,y){const p=await point(x,y);await page.mouse.move(p.x,p.y);}
async function saved() {
 const s=await until(s=>s.export.phase==='saved'&&!s.export.sizesPending);
 await page.waitForFunction(s=>window.__sizeMap.state.export.measuredVersion===s.export.measuredVersion && window.__sizeMap.state.revision>=s.revision,s);
 const bytes=(await readFile(path.join(process.env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT,s.export.path))).length;
 assert.equal((await sizes()).selectedBytes,bytes);assert.equal((await sizes()).totalBytes,bytes);
 return bytes;
}
try{
 await page.goto(base);await page.locator('#room-list button').first().waitFor();await page.locator('#language').selectOption('EN');
 await page.evaluate(()=>{const m=window.__sizeMap;m.pixelScale=.5;m.requestDraw();});
 const empty=await saved();
 await move(3.5,12.5);await page.mouse.down();await move(10.5,12.5);
 let first=(await sizes()).selectedBytes;assert.ok(first>empty,'Held brush must increase bytes before mouse-up/export');
 await move(23.5,12.5);let painted=await sizes();assert.ok(painted.selectedBytes>first);
 await page.waitForFunction(bytes=>document.querySelector('#status-room-sizes').textContent.includes((bytes/1024).toFixed(2)+' KB'),painted.selectedBytes);
 await move(10.5,12.5);assert.equal((await sizes()).selectedBytes,painted.selectedBytes,'Revisiting cells does not double count');
 await page.mouse.up();await page.evaluate(()=>window.__sizeMap.settled());
 assert.equal((await sizes()).selectedBytes,painted.selectedBytes,'Compact acknowledgement must not reset or double the preview');
 // A second stroke before the idle export must build on the first one.
 await move(4.5,14.5);await page.mouse.down();await move(11.5,14.5);painted=await sizes();
 await page.mouse.up();await page.evaluate(()=>window.__sizeMap.settled());
 assert.equal((await sizes()).selectedBytes,painted.selectedBytes);
 assert.equal(await saved(),painted.selectedBytes,'Live count exactly matches compact UTF-8 bytes on disk');
 await move(3.5,12.5);await page.mouse.down({button:'right'});await move(9.5,12.5);
 const erased=await sizes();assert.ok(erased.selectedBytes<painted.selectedBytes,'Held eraser decreases bytes');
 await page.mouse.up({button:'right'});await page.evaluate(()=>window.__sizeMap.settled());
 assert.equal(await saved(),erased.selectedBytes);
 await move(4.5,16.5);await page.mouse.down();await move(19.5,16.5);assert.ok((await sizes()).selectedBytes>erased.selectedBytes);
 await page.keyboard.press('Escape');await page.mouse.up();await page.evaluate(()=>window.__sizeApi.settled());
 assert.equal((await sizes()).totalBytes,erased.totalBytes,'Cancel restores the pre-stroke size');
 assert.equal((await sizes()).selectedBytes,0,'Escape clears the selected room size');
 await command('selectRoom',{id:room.id}); await command('undo');assert.equal(await saved(),painted.selectedBytes);await command('redo');assert.equal(await saved(),erased.selectedBytes);
 // Unicode group identifiers exercise UTF-8 and surrogate escaping in actual exported tiles.
 const unicode=structuredClone((await current()).document);unicode.layerGroups=[{id:'한글-🌿',name:'Group',layer:0,parentId:'',visible:true,locked:false}];
 await command('import',{document:unicode,discard:true});await command('selectRoom',{id:room.id});await command('options',{groupId:'한글-🌿'});await saved();
 await page.waitForFunction(()=>window.__sizeMap.state.selection.groupId==='한글-🌿');
 await move(25.5,12.5);await page.mouse.down();await move(31.5,12.5);const unicodeSize=(await sizes()).selectedBytes;
 await page.mouse.up();await page.evaluate(()=>window.__sizeMap.settled());assert.equal(await saved(),unicodeSize);
 assert.deepEqual(errors,[]);console.log('Live room size checks passed: held brush/eraser, repeated cells, consecutive strokes, cancellation, undo/redo, UTF-8 and exported bytes.');
}finally{await browser.close();if(initial.file)await command('open',{path:initial.file,discard:true});else await command('import',{document:initial.document,discard:true});}
