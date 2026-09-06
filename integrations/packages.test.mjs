import test from 'node:test';
import assert from 'node:assert/strict';
import { inflateRawSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { packageEntries, zip } from './build-packages.mjs';

test('all engine archives contain complete portable samples, manifests and canonical source bytes',()=>{
  for(const engine of ['godot','ue','sdl']) {
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
    const base=engine==='ue'?'plugin/MetroidvaniaStudio/samples':'samples',catalog=JSON.parse(decoded.get(base+'/catalog.json'));
    for(const sprite of [...catalog.materials.flatMap(m=>m.sprites),...catalog.objects.map(o=>o.sprite).filter(Boolean)])assert.ok(decoded.has(base+'/'+sprite.asset),sprite.asset);
    assert.ok(decoded.has(base+'/maps/Coverage.json'));
    for(const name of decoded.keys())assert.ok(!/(?:^|\/)(?:\.local|builds|Binaries|Intermediate|\.godot|\.git)(?:\/|$)/.test(name),name);
  }
});
test('archive writer rejects traversal and absolute names',()=>{
  for(const name of ['../bad','/bad','a/../b','a\\b','a//b'])assert.throws(()=>zip(new Map([[name,Buffer.from('')]])));
});
