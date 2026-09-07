import {readFileSync,writeFileSync,readdirSync,lstatSync,existsSync} from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {createHash} from 'node:crypto';
import assert from 'node:assert/strict';
import {zip} from '../../../../integrations/build-packages.mjs';
import {inspectFiles,loadMatcher} from '../../../../tools/repository/check-text.mjs';
import {inspectBoundaries} from '../../../../tools/repository/check-boundaries.mjs';
import {auditWindowsMetadata} from '../../../../tools/release/vendor-metadata.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../../../..');
const program=path.join(root,'builds/win/program');
const entries=new Map();
function add(name){
 const file=path.join(program,name);assert.ok(!lstatSync(file).isSymbolicLink());
 entries.set(name,readFileSync(file));
}
function tree(name){
 for(const entry of readdirSync(path.join(program,name),{withFileTypes:true})) {
  assert.ok(!entry.isSymbolicLink());
  const child=name+'/'+entry.name;
  if(entry.isDirectory())tree(child);else add(child);
 }
}
for(const file of ['metroidvania-studio.exe','metroidvania-studio-cli.cmd','MetroidvaniaStudio.Server.deps.json','MetroidvaniaStudio.Server.runtimeconfig.json','build-info.json','runtime-requirements.txt'])add(file);
for(const file of ['LICENSE','THIRD-PARTY-NOTICES.md','NOTICE'])if(existsSync(path.join(program,file)))add(file);
for(const folder of ['app/metroidvania-studio/dist','app/metroidvania-studio/cli','app/metroidvania-studio/contracts','app/docs','app/metroidvania-studio/localization','app/samples','app/notices'])tree(folder);
entries.set('desktop-settings.json',Buffer.from('{}\n'));
entries.set('INSTALL_EN.txt',Buffer.from('1. Install .NET 10 Desktop Runtime (x64), ASP.NET Core Runtime 10 (x64), and Microsoft Edge WebView2 Runtime.\n2. Extract the entire archive to a writable folder.\n3. Double-click metroidvania-studio.exe. Keep the app folder beside it.\n\nMaps are saved in %LOCALAPPDATA%/MetroidvaniaStudio/workspace.\nKeep that workspace when updating the program.\n'));
for(const name of entries.keys())assert.ok(!/(^|\/)(?:\.local|src|obj|bin|logs|workspace|desktop-self-test\.js)(\/|$)|\.(?:cs|csproj|pdb|ps1|map)$/i.test(name),name);
const bytes=zip(entries),name='builds/win/program/metroidvania-studio-win-x64.zip';
assert.deepEqual(inspectFiles([{name,bytes}],loadMatcher()),[]);
const boundaryIssues=[];
for (const [entryName,entryBytes] of entries) {
 if(entryName==='metroidvania-studio.exe') boundaryIssues.push(...(await auditWindowsMetadata(entryBytes)).issues);
 else boundaryIssues.push(...inspectBoundaries([{name:'commit message',bytes:entryBytes}]));
}
if(boundaryIssues.length){writeFileSync(path.join(root,'.local/metroidvania-studio-win-x64.zip'),bytes);writeFileSync(path.join(root,'.local/package-review.json'),JSON.stringify({archive:name,bytes:bytes.length,files:entries.size,boundaryIssues,privateSourcesExcluded:true},null,2));throw new Error('Archive candidate saved privately; repository boundary review is still required.');}
writeFileSync(path.join(root,name),bytes);
const report={archive:name,files:entries.size,bytes:bytes.length,exeBytes:entries.get('metroidvania-studio.exe').length,sha256:createHash('sha256').update(bytes).digest('hex'),portableWorkspace:true,privateSourcesExcluded:true};
writeFileSync(path.join(root,'.local/desktop-package-validation.json'),JSON.stringify(report,null,2));
console.log(JSON.stringify(report,null,2));