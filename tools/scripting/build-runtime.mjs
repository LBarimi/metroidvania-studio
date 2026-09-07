import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync, renameSync, rmSync, openSync, closeSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readZip } from '../release/archives.mjs';
import { zip } from '../../integrations/build-packages.mjs';
import { inspectBoundaries } from '../repository/check-boundaries.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const cache = path.join(root, '.local/scripting-runtime');
const lockPath = path.join(root, 'tools/scripting/source-lock.json');
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const json = file => JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const xml = value => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('"', '&quot;');
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

function run(dotnet, args, cwd) {
  const result = spawnSync(dotnet, args, { cwd, windowsHide: true, encoding: 'utf8', timeout: 180_000,
    env: { ...process.env, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1' } });
  if (result.error || result.status !== 0) throw new Error(result.error?.message || result.stderr || result.stdout || 'Interpreter build failed.');
  return result.stdout.trim();
}

function inside(relative) {
  const file = path.resolve(cache, relative);
  assert.ok(file.startsWith(cache + path.sep), 'Runtime cache path escaped its directory.');
  return file;
}

function write(relative, bytes) {
  const file = inside(relative);
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, bytes);
}

async function acquireLock() {
  mkdirSync(cache, { recursive: true });
  const filename = inside('build.lock');
  const until = Date.now() + 210_000;
  while (true) {
    try {
      const fd = openSync(filename, 'wx');
      writeFileSync(fd, JSON.stringify({ pid: process.pid }));
      return () => { closeSync(fd); rmSync(filename); };
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      let pid;
      try { pid = json(filename).pid; } catch { }
      if (Number.isSafeInteger(pid)) {
        try { process.kill(pid, 0); }
        catch (probe) { if (probe.code === 'ESRCH') { rmSync(filename, { force: true }); continue; } }
      }
      if (Date.now() > until) throw new Error('Another interpreter build is still running. Try again after it finishes.');
      await wait(100);
    }
  }
}

async function sourceArchive(lock) {
  const archivePath = inside(`source-${lock.sourceSha256}.zip`);
  if (existsSync(archivePath)) {
    const bytes = readFileSync(archivePath);
    assert.equal(sha(bytes), lock.sourceSha256, 'Cached interpreter source checksum mismatch.');
    return bytes;
  }
  const response = await fetch(lock.sourceUrl, { signal: AbortSignal.timeout(120_000) });
  if (!response.ok) throw new Error(`Interpreter source download failed (${response.status}).`);
  const chunks = []; let size = 0;
  for await (const chunk of response.body) {
    size += chunk.length;
    if (size > 64 * 1024 * 1024) throw new Error('Interpreter source archive exceeds its download limit.');
    chunks.push(chunk);
  }
  const bytes = Buffer.concat(chunks);
  assert.equal(sha(bytes), lock.sourceSha256, 'Downloaded interpreter source checksum mismatch.');
  writeFileSync(archivePath, bytes);
  return bytes;
}

function cachedBuild(fingerprint) {
  try {
    const manifest = json(inside('build-manifest.json'));
    if (manifest.fingerprint !== fingerprint) return false;
    const expected = ['lib/MoonSharp.Interpreter.dll', 'feed/metroidvaniastudio.luaruntime.2.0.0.nupkg', 'lib/LICENSE.txt', 'lib/provenance.json'];
    if (JSON.stringify(Object.keys(manifest.files).sort()) !== JSON.stringify(expected.sort())) return false;
    for (const [file, checksum] of Object.entries(manifest.files))
      if (sha(readFileSync(inside(file))) !== checksum) return false;
    const dll = readFileSync(inside('lib/MoonSharp.Interpreter.dll'));
    return inspectBoundaries([{ name: 'runtime/MoonSharp.Interpreter.dll', bytes: dll }]).length === 0;
  } catch { return false; }
}

export async function buildRuntime({ dotnet = process.env.METROIDVANIA_STUDIO_DOTNET || 'dotnet', force = false } = {}) {
  const releaseLock = await acquireLock();
  try {
    const lock = json(lockPath);
    const sdk = run(dotnet, ['--version'], root);
    assert.match(sdk, /^10\./, 'The interpreter build requires .NET SDK 10.');
    const fingerprint = sha(Buffer.concat([readFileSync(lockPath), readFileSync(fileURLToPath(import.meta.url)), readFileSync(path.join(root, 'integrations/build-packages.mjs')), Buffer.from(sdk)]));
    if (!force && cachedBuild(fingerprint)) {
      console.log('Lua runtime is ready (verified cached build).');
      return inside('lib/MoonSharp.Interpreter.dll');
    }
    const archive = readZip(await sourceArchive(lock));
    const license = archive.get(lock.archiveRoot + 'LICENSE');
    assert.ok(license, 'Interpreter source license is missing.');
    assert.equal(sha(license), lock.licenseSha256, 'Interpreter license checksum mismatch.');
    const retainedLicense = readFileSync(path.join(root, 'metroidvania-studio/scripting/licenses/moonsharp-license.txt'));
    assert.deepEqual(retainedLicense, license, 'The retained interpreter license must match the source release.');
    const work = inside('build-' + fingerprint.slice(0, 16));
    mkdirSync(work, { recursive: true });
    const prefix = lock.archiveRoot + lock.sourcePrefix;
    let sources = 0; const compileFiles = [];
    for (const [name, bytes] of archive) {
      if (!name.startsWith(prefix) || !name.endsWith('.cs')) continue;
      const relative = name.slice(prefix.length);
      if (relative.startsWith('_Projects/') || /(?:^|\/)(?:obj|bin)\//.test(relative)) continue;
      const target = path.resolve(work, 'src', relative);
      assert.ok(target.startsWith(path.join(work, 'src') + path.sep), 'Interpreter source path escaped its directory.');
      mkdirSync(path.dirname(target), { recursive: true });
      writeFileSync(target, bytes); compileFiles.push('src/' + relative); sources++;
    }
    assert.ok(sources >= 200 && sources <= 500, 'Unexpected interpreter source count.');
    compileFiles.push('src/runtime-compatibility.cs');
    // Keep upstream files intact while resolving a newer framework type with the same name.
    writeFileSync(path.join(work, 'src', 'runtime-compatibility.cs'), 'global using ReferenceEqualityComparer = MoonSharp.Interpreter.DataStructs.ReferenceEqualityComparer;\n');
    const project = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>${xml(lock.targetFramework)}</TargetFramework>
    <AssemblyName>${xml(lock.assemblyName)}</AssemblyName>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <GenerateTargetFrameworkAttribute>true</GenerateTargetFrameworkAttribute>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable>
    <DefineConstants>${lock.constants.map(xml).join(';')}</DefineConstants>
    <DebugType>None</DebugType><DebugSymbols>false</DebugSymbols>
    <Deterministic>true</Deterministic><Optimize>true</Optimize>
    <NuGetAudit>false</NuGetAudit>
    <NoWarn>3021;1591;SYSLIB0050;SYSLIB0051</NoWarn>
  </PropertyGroup>
  <ItemGroup>${compileFiles.map(file => `<Compile Include="${xml(file)}" />`).join("\n")}</ItemGroup>
</Project>\n`;
    writeFileSync(path.join(work, 'runtime.csproj'), project);
    writeFileSync(path.join(work, 'nuget.config'), '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /></packageSources></configuration>\n');
    run(dotnet, ['build', 'runtime.csproj', '--configuration', 'Release', '--no-incremental', '--nologo',
      '-p:UseSharedCompilation=false', `-p:PathMap=${work}=/_/runtime`], work);
    const dll = readFileSync(path.join(work, 'bin/Release/net10.0/MoonSharp.Interpreter.dll'));
    assert.deepEqual(inspectBoundaries([{ name: 'runtime/MoonSharp.Interpreter.dll', bytes: dll }]), [], 'Clean interpreter contains forbidden machine paths.');
    const nuspec = `<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
<id>MetroidvaniaStudio.LuaRuntime</id><version>2.0.0</version><authors>Metroidvania Studio</authors>
<description>Source-built Lua interpreter runtime. Original copyright and terms are retained in LICENSE.txt.</description>
<license type="file">LICENSE.txt</license><dependencies><group targetFramework="net10.0" /></dependencies>
</metadata></package>\n`;
    const provenance = Buffer.from(JSON.stringify({ interpreter: lock.interpreter, version: lock.version, revision: lock.revision,
      sourceSha256: lock.sourceSha256, sdk, assemblyVersion: lock.assemblyVersion, dllSha256: sha(dll) }, null, 2) + '\n');
    const packageBytes = zip(new Map([
      ['MetroidvaniaStudio.LuaRuntime.nuspec', Buffer.from(nuspec)],
      ['lib/net10.0/MoonSharp.Interpreter.dll', dll], ['LICENSE.txt', license], ['provenance.json', provenance]
    ]));
    const extracted = readZip(packageBytes);
    assert.deepEqual(extracted.get('lib/net10.0/MoonSharp.Interpreter.dll'), dll, 'Rebuilt package assembly mismatch.');
    const files = new Map([['lib/MoonSharp.Interpreter.dll', dll], ['feed/metroidvaniastudio.luaruntime.2.0.0.nupkg', packageBytes],
      ['lib/LICENSE.txt', license], ['lib/provenance.json', provenance]]);
    for (const [relative, bytes] of files) { write(relative + '.tmp', bytes); renameSync(inside(relative + '.tmp'), inside(relative)); }
    write('build-manifest.json', JSON.stringify({ fingerprint, sdk, sourceSha256: lock.sourceSha256,
      files: Object.fromEntries([...files].map(([file, bytes]) => [file, sha(bytes)])) }, null, 2) + '\n');
    console.log(`Lua runtime built from verified source (${sources} source files; no debug paths).`);
    return inside('lib/MoonSharp.Interpreter.dll');
  } finally { releaseLock(); }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.some(arg => arg !== '--force')) { console.error('Usage: node tools/scripting/build-runtime.mjs [--force]'); process.exitCode = 1; }
  else buildRuntime({ force: args.includes('--force') }).catch(error => { console.error(error.message); process.exitCode = 1; });
}
