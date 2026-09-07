import test from 'node:test';
import assert from 'node:assert/strict';
import { inflateRawSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { root, packageEntries, zip } from './build-packages.mjs';

test('all engine archives contain complete portable samples, manifests and canonical source bytes',()=>{
  for(const engine of ['godot','ue4','ue5','sdl']) {
    const entries=packageEntries(engine),a=zip(entries),b=zip(entries);assert.deepEqual(a,b);
    const decoded=new Map();let offset=0;
    while(a.readUInt32LE(offset)===0x04034b50) {
      const size=a.readUInt32LE(offset+18),length=a.readUInt16LE(offset+26),name=a.subarray(offset+30,offset+30+length).toString('utf8');
      const bytes=inflateRawSync(a.subarray(offset+30+length,offset+30+length+size));
      assert.deepEqual(bytes,entries.get(name));decoded.set(name,bytes);offset+=30+length+size;
    }
    assert.equal(decoded.size,entries.size);assert.equal(a.readUInt32LE(offset),0x02014b50);
    const manifest=JSON.parse(decoded.get('package-manifest.json'));assert.equal(manifest.mapFormatVersion,2);assert.equal(manifest.engine,engine);
    for(const item of manifest.files)assert.equal(createHash('sha256').update(decoded.get(item.path)).digest('hex'),item.sha256);
    const base=(engine==='ue4'||engine==='ue5')?'plugin/MetroidvaniaStudio/samples':'samples',catalog=JSON.parse(decoded.get(base+'/catalog.json'));
    for(const sprite of [...catalog.materials.flatMap(m=>m.sprites),...catalog.objects.map(o=>o.sprite).filter(Boolean)])assert.ok(decoded.has(base+'/'+sprite.asset),sprite.asset);
    assert.ok(decoded.has(base+'/maps/Coverage.json'));
    for(const name of decoded.keys())assert.ok(!/(?:^|\/)(?:\.local|builds|Binaries|Intermediate|\.godot|\.git)(?:\/|$)/.test(name),name);
  }
});
test('archive writer rejects traversal and absolute names',()=>{
  for(const name of ['../bad','/bad','a/../b','a\\b','a//b'])assert.throws(()=>zip(new Map([[name,Buffer.from('')]])));
});

test('SDL ships the runnable sample source beside its archive without a second implementation',()=>{
  const entries=packageEntries('sdl');
  for(const name of ['main.cpp','sample-room.cpp','sample-room.h']) {
    const archive=entries.get('src-sample/'+name);assert.ok(archive,name);
    const canonical=readFileSync(path.join(root,'integrations/sdl/src-sample',name),'utf8').replaceAll('\r\n','\n');
    assert.equal(archive.toString('utf8'),canonical);
    assert.deepEqual(readFileSync(path.join(root,'engine-packages/sdl/src-sample',name)),archive);
  }
  assert.ok(!entries.has('src/Preview.cpp'),'There should be only one runnable sample implementation.');
});

test('Unreal packages share their implementation and retain distinct build settings',()=>{
  const old=packageEntries('ue4'),current=packageEntries('ue5');
  const prefix='plugin/MetroidvaniaStudio/Source/MetroidvaniaStudio/';
  for(const [name,bytes] of old)if(name.startsWith(prefix+'Public/')||name.startsWith(prefix+'Private/'))
    assert.deepEqual(bytes,current.get(name),name);
  assert.match(old.get(prefix+'MetroidvaniaStudio.Build.cs').toString(),/CppStandardVersion.Cpp17/);
  assert.equal(JSON.parse(old.get('package-manifest.json')).engine,'ue4');
  assert.equal(JSON.parse(current.get('package-manifest.json')).engine,'ue5');
});
