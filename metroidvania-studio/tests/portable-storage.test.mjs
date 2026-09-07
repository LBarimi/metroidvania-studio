import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdirSync, mkdtempSync, copyFileSync, readFileSync, existsSync, rmSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const dotnet=process.env.METROIDVANIA_STUDIO_DOTNET||'dotnet';
test('default launcher migrates a verified live legacy workspace and keeps engine resources portable', {timeout:90000}, async()=>{
  const index=JSON.parse(readFileSync(path.join(root,'builds/latest.json'),'utf8'));
  const bundle=path.join(root,'builds',index.folder), launcher=path.join(bundle,'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll');
  const parent=path.join(root,'.local/storage-launcher-tests');mkdirSync(parent,{recursive:true});
  const storage=mkdtempSync(path.join(parent,'portable space-')), legacy=path.join(storage,'.local/workspace');
  mkdirSync(path.join(legacy,'Maps'),{recursive:true});mkdirSync(path.join(legacy,'.studio'),{recursive:true});
  copyFileSync(path.join(root,'samples/maps/Sample.map.json'),path.join(legacy,'Maps/world.json'));
  copyFileSync(path.join(root,'samples/catalog.json'),path.join(legacy,'.studio/catalog.json'));
  const probe=createServer();await new Promise(resolve=>probe.listen(0,'127.0.0.1',resolve));const port=probe.address().port;await new Promise(resolve=>probe.close(resolve));
  const url='http://127.0.0.1:'+port;
  async function run(action, explicit=false){
    const args=[launcher,action,'--studio-root',storage,'--build-directory',bundle,'--port',String(port),'--no-browser'];
    if(explicit)args.push('--project',legacy);
    const child=spawn(dotnet,args,{windowsHide:true,env:{...process.env,DOTNET_CLI_TELEMETRY_OPTOUT:'1'},stdio:['ignore','pipe','pipe']});let output='';
    child.stdout.on('data',data=>output+=data);child.stderr.on('data',data=>output+=data);
    const code=await new Promise((resolve,reject)=>{child.on('error',reject);child.on('close',resolve);});assert.equal(code,0,output);
  }
  let active=false, portable=false, completed=false;
  try{
    await run('run',true);active=true;
    const before=await(await fetch(url+'/api/state?full=true')).json();
    const response=await fetch(url+'/api/command',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'documentProperties',name:'Migration progress',clientId:'migration',commandId:crypto.randomUUID(),expectedRevision:before.revision,expectedInstanceId:before.instanceId})});
    assert.ok(response.ok,await response.text());
    await run('run');portable=true;
    const health=await(await fetch(url+'/api/health')).json();assert.equal(path.resolve(health.projectPath),storage);
    const after=await(await fetch(url+'/api/state?full=true')).json();assert.equal(after.document.name,'Migration progress');assert.equal(after.dirty,true);
    assert.deepEqual(after.document.rooms,before.document.rooms);
    const folders=await(await fetch(url+'/api/workspace-folders')).json();assert.equal(folders.catalog,path.join(storage,'catalog.json'));
    const catalog=JSON.parse(readFileSync(folders.catalog,'utf8'));
    for(const material of catalog.materials)for(const sprite of material.sprites){
      assert.ok(sprite.asset.startsWith('Textures/'));assert.ok(existsSync(path.join(storage,sprite.asset)),'Exported engine resource exists at its portable path.');
    }
    assert.ok(existsSync(path.join(legacy,'Maps/.Recovery/Workspace.map.json')),'The legacy backup is preserved.');
    await run('stop');active=false;await run('run');active=true;
    assert.equal((await(await fetch(url+'/api/state?full=true')).json()).document.name,'Migration progress');
    await run('stop');active=false;completed=true;
  }finally{
    if(active)await run('stop',!portable);
    if(completed){assert.equal(path.dirname(storage),parent);rmSync(storage,{recursive:true,force:true});}
  }
});
