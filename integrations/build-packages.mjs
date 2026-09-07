import { readFileSync, writeFileSync, mkdirSync, readdirSync, lstatSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { deflateRawSync } from 'node:zlib';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const crcTable = Array.from({length:256},(_,n)=>{for(let i=0;i<8;i++)n=n&1?0xedb88320^(n>>>1):n>>>1;return n>>>0;});
const crc32 = bytes => {let crc=0xffffffff;for(const b of bytes)crc=crcTable[(crc^b)&255]^(crc>>>8);return (crc^0xffffffff)>>>0;};
function files(folder) {
  return readdirSync(path.join(root,folder)).sort().flatMap(name=>{
    const relative=folder+'/'+name,stat=lstatSync(path.join(root,relative));
    if(stat.isSymbolicLink())throw new Error('Package inputs cannot contain links.');
    return stat.isDirectory()?files(relative):[relative];
  });
}
export function zip(entries) {
  const chunks=[],central=[];let offset=0;
  for(const [name,bytes] of [...entries].sort(([a],[b])=>a<b?-1:a>b?1:0)) {
    if(!name||name.startsWith('/')||name.includes('\\')||name.split('/').some(x=>!x||x==='.'||x==='..'))throw new Error('Invalid archive path.');
    const filename=Buffer.from(name),packed=deflateRawSync(bytes,{level:9}),crc=crc32(bytes);
    const header=Buffer.alloc(30);header.writeUInt32LE(0x04034b50);header.writeUInt16LE(20,4);header.writeUInt16LE(0x800,6);header.writeUInt16LE(8,8);header.writeUInt16LE(33,12);header.writeUInt32LE(crc,14);header.writeUInt32LE(packed.length,18);header.writeUInt32LE(bytes.length,22);header.writeUInt16LE(filename.length,26);
    chunks.push(header,filename,packed);
    const directory=Buffer.alloc(46);directory.writeUInt32LE(0x02014b50);directory.writeUInt16LE(0x0314,4);directory.writeUInt16LE(20,6);directory.writeUInt16LE(0x800,8);directory.writeUInt16LE(8,10);directory.writeUInt16LE(33,14);directory.writeUInt32LE(crc,16);directory.writeUInt32LE(packed.length,20);directory.writeUInt32LE(bytes.length,24);directory.writeUInt16LE(filename.length,28);directory.writeUInt32LE(((name.endsWith('.sh')?0o100755:0o100644)*65536)>>>0,38);directory.writeUInt32LE(offset,42);
    central.push(directory,filename);offset+=header.length+filename.length+packed.length;
  }
  const index=Buffer.concat(central),end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50);end.writeUInt16LE(entries.size,8);end.writeUInt16LE(entries.size,10);end.writeUInt32LE(index.length,12);end.writeUInt32LE(offset,16);return Buffer.concat([...chunks,index,end]);
}
export function packageEntries(engine) {
  if(!['godot','ue4','ue5','sdl'].includes(engine))throw new Error('Unsupported integration.');
  const entries=new Map();
  const add=(destination,source)=>{if(entries.has(destination))throw new Error('Duplicate archive entry.');let bytes=readFileSync(path.join(root,source));
    if(/\.(?:md|txt|json|cfg|gd|uid|cs|cpp|h|ps1|bat|sh|uplugin)$/.test(source)||source.endsWith('CMakeLists.txt')) {
      let text=bytes.toString('utf8').replaceAll('\r\n','\n');
      if(/\.(?:bat|ps1)$/.test(source))text=text.replaceAll('\n','\r\n');
      bytes=Buffer.from(text);
    }
    entries.set(destination,bytes);};
  const tree=(source,destination=source)=>{for(const name of files(source))add(destination+name.slice(source.length),name);};
  add('README.md',`integrations/${engine}/README.md`);
  entries.set('README.md',Buffer.from(entries.get('README.md').toString('utf8').replaceAll(`../../engine-packages/${engine}/`,'')));
  for(const language of ['KR','EN','JP','CN','TW'])add(`INSTALL_${language}.txt`,`engine-packages/${engine}/INSTALL_${language}.txt`);
  if(engine==='godot')tree('integrations/godot/addons','addons');
  if(engine==='ue4'||engine==='ue5') {
    const base='plugin/MetroidvaniaStudio';
    add(base+'/MetroidvaniaStudio.uplugin',`integrations/${engine}/MetroidvaniaStudio.uplugin`);
    add(base+'/Source/MetroidvaniaStudio/MetroidvaniaStudio.Build.cs',`integrations/${engine}/source/MetroidvaniaStudio.Build.cs`);
    for(const folder of ['public','private'])tree(`integrations/shared/unreal/${folder}`,base+'/Source/MetroidvaniaStudio/'+(folder==='public'?'Public':'Private'));
    add(base+'/Source/MetroidvaniaStudio/Private/StudioDocument.h','integrations/shared/native/StudioDocument.h');
    entries.set(base+'/Config/FilterPlugin.ini',Buffer.from('[FilterPlugin]\n/samples/...\n'));
  }
  if(engine==='sdl') {
    tree('integrations/sdl/include','include');tree('integrations/sdl/src','src');
    tree('integrations/sdl/src-sample','src-sample');
    add('include/StudioDocument.h','integrations/shared/native/StudioDocument.h');
    for(const file of ['CMakeLists.txt','Build-Sdl.ps1','build.bat','run.bat','build.sh','run.sh','THIRD-PARTY-NOTICES.txt'])add(file,'integrations/sdl/'+file);
  } else for(const file of ['install.bat','Install-Integration.ps1','Resolve-UnrealEngine.ps1'])add(file,'integrations/shared/'+file);
  const sampleRoot=(engine==='ue4'||engine==='ue5')?'plugin/MetroidvaniaStudio/samples':'samples';tree('samples',sampleRoot);
  add(sampleRoot+'/maps/Coverage.json','integrations/shared/tests/fixtures/Coverage.json');
  // Catalog paths are case-sensitive on supported Unix filesystems.
  const catalog=JSON.parse(entries.get(sampleRoot+'/catalog.json').toString('utf8').replace(/^\uFEFF/,''));
  const sampleFiles=new Map(files('samples').map(name=>[name.slice(8).toLowerCase(),name.slice(8)]));
  for(const sprite of [...catalog.materials.flatMap(m=>m.sprites),...catalog.objects.map(o=>o.sprite).filter(Boolean)]) {
    const actual=sampleFiles.get(sprite.asset.toLowerCase());if(!actual)throw new Error('Missing public sample texture.');sprite.asset=actual;
  }
  entries.set(sampleRoot+'/catalog.json',Buffer.from(JSON.stringify(catalog,null,2)+'\n'));
  add('contract/FORMAT.md','metroidvania-studio/contracts/FORMAT.md');add('contract/map-format-v2.schema.json','metroidvania-studio/contracts/map-format-v2.schema.json');
  for(const notice of ['LICENSE','NOTICE'])if(existsSync(path.join(root,notice)))add(notice,notice);
  const version=JSON.parse(readFileSync(path.join(root,'version.json'),'utf8')).version;
  entries.set('package-manifest.json',Buffer.from(JSON.stringify({formatVersion:1,packageVersion:version,mapFormatVersion:2,engine,files:[...entries].sort(([a],[b])=>a<b?-1:1).map(([name,bytes])=>({path:name,sha256:createHash('sha256').update(bytes).digest('hex')}))},null,2)+'\n'));
  return entries;
}
export function buildPackages(output=path.join(root,'engine-packages'),check=false) {
  for(const engine of ['godot','ue4','ue5','sdl']) {
    const entries=packageEntries(engine),bytes=zip(entries),filename=path.join(output,engine,'metroidvania-studio.zip');
    if(check) {if(!existsSync(filename)||!readFileSync(filename).equals(bytes))throw new Error('Stale engine package: '+engine);}
    else {mkdirSync(path.dirname(filename),{recursive:true});writeFileSync(filename,bytes);}
    for(const language of ['KR','EN','JP','CN','TW']) {
      const name=`INSTALL_${language}.txt`,guide=path.join(output,engine,name),content=entries.get(name);
      if(check) {if(!existsSync(guide)||!readFileSync(guide).equals(content))throw new Error('Stale install guide: '+engine+'/'+name);}
      else writeFileSync(guide,content);
    }
    if(engine==='sdl')for(const [name,content] of entries)if(name.startsWith('src-sample/')) {
      const sample=path.join(output,engine,name);
      if(check) {if(!existsSync(sample)||!readFileSync(sample).equals(content))throw new Error('Stale SDL sample: '+name);}
      else {mkdirSync(path.dirname(sample),{recursive:true});writeFileSync(sample,content);}
    }
    console.log(`${engine}: ${bytes.length} bytes${check?' (current)':''}`);
  }
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const args=process.argv.slice(2),check=args.includes('--check');
  const output=args.includes('--output')?path.resolve(args[args.indexOf('--output')+1]):path.join(root,'engine-packages');
  if(args.some(a=>a.startsWith('--')&&!['--output','--check'].includes(a)))throw new Error('Usage: build-packages.mjs [--check] [--output <engine-folder>]');
  if(!check)execFileSync(process.execPath,[path.join(root,'integrations/unity/build-package.mjs'),path.join(output,'unity/metroidvania-studio.unitypackage')],{cwd:root,stdio:'inherit',windowsHide:true});
  buildPackages(output,check);
}
