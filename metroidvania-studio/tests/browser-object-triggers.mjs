import {createRequire} from 'node:module';
import assert from 'node:assert/strict';
const {chromium} = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const state = async () => (await fetch(base + '/api/state')).json();
async function command(action, values = {}) {
  const current = await state();
  const response = await fetch(base + '/api/command', {method:'POST', headers:{'Content-Type':'application/json'},
    body:JSON.stringify({action,...values,clientId:'trigger-browser',commandId:crypto.randomUUID(),expectedRevision:current.revision,expectedInstanceId:current.instanceId})});
  const result = await response.json(); assert.ok(response.ok, JSON.stringify(result)); return result;
}
const initial = await state(), template = initial.document.rooms[0];
const room = {...template,id:'trigger-browser',name:'Trigger room',x:0,y:0,width:16,height:10,foreground:[],background:[],objects:[],properties:[],locked:false,visible:true};
await command('import',{document:{...initial.document,rooms:[room],layerGroups:[],stylegrounds:[],properties:[]},discard:true});
await command('selectRoom',{id:room.id});
await command('options',{layer:2,tool:1,objectDefinition:'Portal',hiddenLayers:[],lockedLayers:[],groupId:''});
const browser = await chromium.launch({channel:'msedge',headless:true});
const page = await browser.newPage({viewport:{width:1440,height:900},locale:'en-US'});
const errors = []; page.on('pageerror', e => errors.push(e.message));
const paint = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
async function settle() {
  const current = await state();
  await page.waitForFunction(r => Number(document.querySelector('#status-revision')?.textContent.slice(1)) >= r && !document.querySelector('#status-state')?.classList.contains('working'),current.revision);
  await paint();
}
async function until(predicate) {
  for(let i=0;i<100;i++){const s=await state();if(predicate(s)){await settle();return s;}await new Promise(r=>setTimeout(r,50));}
  const latest=await state(); throw Error('Trigger browser operation timed out: '+JSON.stringify({objects:latest.document.rooms[0].objects,selection:latest.selection,notice:latest.notice,toast:await page.locator('#toast').textContent()}));
}
async function at(x,y) {
  const r=await page.locator('#map-canvas').boundingBox();
  return {x:r.x+r.width/2+(x-8)*32,y:r.y+r.height/2-(y-5)*32};
}
try {
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  await page.locator('#room-list button').first().click(); await settle();
  const names=await page.locator('.palette-item').allTextContents();
  assert.deepEqual(names.map(n=>n.trim()),['Spawn','Portal','Invisible wall','Path','Respawn point']);
  let p=await at(3.2,3.7); await page.mouse.click(p.x,p.y);
  let s=await until(s=>s.document.rooms[0].objects.length===1);
  const portal=s.document.rooms[0].objects[0]; assert.equal(portal.definition,'Portal'); assert.ok(portal.id);
  const bounds = o => [o.x,o.y,o.width,o.height];
  assert.deepEqual(bounds(portal),[3,3,1,1],'Click snaps the portal to a tile cell.');
  async function stroke(from,to,expected) {
    const start=await at(...from), end=await at(...to);
    await page.mouse.move(start.x,start.y); await page.mouse.down();
    await page.mouse.move(end.x,end.y,{steps:12}); await paint();
    assert.equal((await state()).document.rooms[0].objects.length,1,'Dragging previews one area without placing per-pointer objects.');
    await page.mouse.up();
    const placed=(await until(s=>s.document.rooms[0].objects.length===2)).document.rooms[0].objects.find(o=>o.id!==portal.id);
    assert.deepEqual(bounds(placed),expected);
    await command('undo'); await settle();
    assert.equal((await state()).document.rooms[0].objects.length,1);
    return placed;
  }
  const drawn=await stroke([6.2,3.8],[8.7,6.2],[6,3,3,4]);
  await command('redo'); await settle();
  assert.equal((await state()).document.rooms[0].objects.find(o=>o.id===drawn.id)?.width,3);
  await command('undo'); await settle();
  await stroke([8.7,6.2],[6.2,3.8],[6,3,3,4]);
  await stroke([14.2,3.8],[17.2,6.2],[14,3,2,4]);
  p=await at(10.2,3.8); const cancelled=await at(12.7,6.2);
  await page.mouse.move(p.x,p.y); await page.mouse.down(); await page.mouse.move(cancelled.x,cancelled.y,{steps:4});
  await page.keyboard.press('Escape'); await page.mouse.up(); await settle();
  assert.equal((await state()).document.rooms[0].objects.length,1,'Escape cancels the portal preview.');
  await command('objectClick',{x:portal.x+.5,y:portal.y+.5}); await settle();
  assert.equal(await page.locator('#object-id').count(),0,'The inspector hides internal IDs.');
  assert.match(portal.id,/^[1-9][0-9]{0,15}$/);
  assert.ok(BigInt(portal.id)<=9007199254740991n);
  assert.equal(await page.locator('[data-property="once"]').count(),0);
  assert.equal(await page.locator('[data-property="event"]').count(),0);
  assert.ok(portal.properties.every(p=>!['event','once'].includes(p.key)));
  await page.locator('[data-property="desc"]').fill('North gate');
  await page.getByRole('button',{name:'Apply properties',exact:true}).click();
  await until(s=>s.document.rooms[0].objects[0].properties.some(p=>p.key==='desc'&&p.value==='North gate'));
  await command('copy'); await command('paste',{x:7,y:3}); await settle();
  s=await state(); assert.equal(s.document.rooms[0].objects.length,2); assert.equal(new Set(s.document.rooms[0].objects.map(o=>o.id)).size,2);
  await command('options',{layer:3,tool:1,objectDefinition:'Area'}); await settle();
  p=await at(2,6); const end=await at(7,8); await page.mouse.move(p.x,p.y); await page.mouse.down(); await page.mouse.move(end.x,end.y,{steps:6}); await page.mouse.up();
  s=await until(s=>s.document.rooms[0].objects.some(o=>o.layer===3));
  const trigger=s.document.rooms[0].objects.find(o=>o.layer===3);
  await command('objectClick',{x:trigger.x+.5,y:trigger.y+.5}); await settle();
  assert.equal(await page.locator('[data-property="event"] option').count(),201);
  assert.equal(await page.locator('[data-property="once"]').getAttribute('type'),'checkbox');
  assert.ok(await page.locator('[data-property="once"]').isChecked(),'New triggers default to Once.');
  await page.locator('[data-property="once"]').uncheck();
  await page.locator('[data-property="event"]').selectOption('Trigger200');
  await page.locator('[data-property="desc"]').fill('Open gate');
  await page.getByRole('button',{name:'Apply properties',exact:true}).click();
  s=await until(s=>s.document.rooms[0].objects.find(o=>o.id===trigger.id).properties.some(p=>p.key==='once'&&p.value==='false'));
  if (process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT) await page.screenshot({path:process.env.METROIDVANIA_STUDIO_TEST_SCREENSHOT});
  await command('undo'); await settle();
  assert.equal((await state()).document.rooms[0].objects.find(o=>o.id===trigger.id).properties.find(p=>p.key==='once').value,'true');
  await command('redo'); await settle();
  await page.locator('[data-property="once"]').uncheck();
  await page.getByRole('button',{name:'Apply properties',exact:true}).click();
  s=await until(s=>s.document.rooms[0].objects.find(o=>o.id===trigger.id).properties.some(p=>p.key==='once'&&p.value==='false'));
  const legacy=structuredClone(s.document); legacy.rooms[0].objects.find(o=>o.id===trigger.id).properties.find(p=>p.key==='event').value='existing-event';
  await command('import',{document:legacy,discard:true}); await command('objectClick',{x:trigger.x+.5,y:trigger.y+.5}); await settle();
  assert.equal(await page.locator('[data-property="event"]').inputValue(),'existing-event');
  await page.locator('[data-property="desc"]').fill('Updated legacy description');
  await page.getByRole('button',{name:'Apply properties',exact:true}).click();
  s=await until(s=>s.document.rooms[0].objects.find(o=>o.id===trigger.id).properties.some(p=>p.key==='desc'&&p.value==='Updated legacy description'));
  assert.equal(s.document.rooms[0].objects.find(o=>o.id===trigger.id).properties.find(p=>p.key==='event').value,'existing-event');
  const unassigned = structuredClone(s.document);
  unassigned.rooms[0].objects.find(o=>o.id===trigger.id).properties = unassigned.rooms[0].objects.find(o=>o.id===trigger.id).properties.filter(p=>!['event','once'].includes(p.key));
  await command('import',{document:unassigned,discard:true}); await command('objectClick',{x:trigger.x+.5,y:trigger.y+.5}); await settle();
  assert.equal(await page.locator('[data-property="event"]').inputValue(),'None','An old missing event must not appear assigned to Trigger001.');
  assert.ok(!await page.locator('[data-property="once"]').isChecked(),'An old trigger without Once retains repeatable behavior.');
  // A real concurrent edit must not be overwritten by an already open form.
  await page.locator('[data-property="desc"]').fill('Unsaved form');
  await command('objectProperties',{values:{desc:'Concurrent value'}}); await settle();
  await page.getByRole('button',{name:'Apply properties',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('#toast')?.textContent.includes('changed in another view'));
  assert.equal((await state()).document.rooms[0].objects.find(o=>o.id===trigger.id).properties.find(p=>p.key==='desc').value,'Concurrent value');

  const oldPortalMap = structuredClone(s.document);
  oldPortalMap.rooms[0].objects.find(o=>o.id===portal.id).properties.push({key:'event',value:'Trigger200'},{key:'once',value:'true'});
  await command('import',{document:oldPortalMap,discard:true});
  await command('options',{layer:2,tool:1,objectDefinition:'Portal'});
  await command('objectClick',{x:portal.x+.5,y:portal.y+.5}); await settle();
  assert.equal(await page.locator('[data-property="event"]').count(),0,'Legacy portals do not expose numbered events.');
  assert.equal(await page.locator('[data-property="once"]').count(),0);
  await page.locator('[data-property="desc"]').fill('Independent portal');
  await page.getByRole('button',{name:'Apply properties',exact:true}).click();
  s=await until(s=>s.document.rooms[0].objects.find(o=>o.id===portal.id).properties.some(p=>p.key==='desc'&&p.value==='Independent portal'));
  assert.equal(s.document.rooms[0].objects.find(o=>o.id===portal.id).properties.find(p=>p.key==='event').value,'Trigger200','Opaque legacy properties survive description edits.');

  await command('import',{document:{...initial.document,rooms:[room],layerGroups:[],stylegrounds:[],properties:[]},discard:true});
  await command('selectRoom',{id:room.id});
  await command('options',{layer:2,tool:1,objectDefinition:'InvisibleWall'}); await settle();
  await page.mouse.move(10,10); await paint();
  const previewImage = () => page.locator('#game-preview-canvas').evaluate(c=>c.toDataURL());
  const beforeWall = await previewImage();
  const startWall=await at(8.2,1.2),endWall=await at(10.8,3.8);
  await page.mouse.move(startWall.x,startWall.y); await page.mouse.down();
  await page.mouse.move(endWall.x,endWall.y,{steps:8});
  assert.equal((await state()).document.rooms[0].objects.length,0);
  await page.mouse.up();
  s=await until(s=>s.document.rooms[0].objects.length===1);
  const wall=s.document.rooms[0].objects[0];
  assert.equal(wall.definition,'InvisibleWall'); assert.deepEqual(bounds(wall),[8,1,3,3]);
  assert.ok(wall.id); assert.deepEqual(wall.properties.map(p=>p.key),['desc']);
  await page.mouse.move(10,10); await paint();
  assert.equal(await previewImage(),beforeWall,'Invisible walls never cover the Game Preview.');
  const sample=await at(8.5,1.5);
  const rgba=await page.locator('#map-canvas').evaluate((c,p)=>{
    const rect=c.getBoundingClientRect();
    return Array.from(c.getContext('2d').getImageData(Math.floor((p.x-rect.x)*c.width/rect.width),Math.floor((p.y-rect.y)*c.height/rect.height),1,1).data);
  },sample);
  assert.ok(rgba[2]>rgba[1]&&rgba[1]>rgba[0]&&rgba[2]<255,'Editor wall fill is translucent blue.');
  await command('undo'); await settle();
  assert.equal((await state()).document.rooms[0].objects.length,0);
  await command('redo'); await settle();
  assert.equal((await state()).document.rooms[0].objects[0].id,wall.id);
  const savedWall=(await state()).document;
  await command('import',{document:savedWall,discard:true}); await settle();
  assert.deepEqual(bounds((await state()).document.rooms[0].objects[0]),[8,1,3,3]);

  assert.deepEqual(errors,[]);
  console.log('Object UI passed: five palette items, portal color catalog, hidden numeric IDs, copy IDs, 200 events, checkbox, descriptions, undo/redo and legacy values.');
} finally {
  await browser.close();
  if(initial.file) await command('open',{path:initial.file,discard:true});
  else await command('import',{document:initial.document,discard:true});
}
