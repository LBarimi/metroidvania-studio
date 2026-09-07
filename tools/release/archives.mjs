import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, readdirSync, lstatSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { gunzipSync, inflateRawSync } from 'node:zlib';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { zip } from '../../integrations/build-packages.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
export const releaseNames = ['web', 'win', 'mac', 'linux'].map(kind => `metroidvania-studio-${kind}.zip`);
const forbidden = /(?:^|\/)(?:media|platform|\.git|\.local|node_modules|obj|workspace)(?:\/|$)|(?:^|\/)readme(?:\.[^/]*)?$|\.(?:pdb|csproj|sln)$/i;
const digest = (data, algorithm = 'sha256') => createHash(algorithm).update(data).digest('hex');
export function safePath(name) {
  assert.ok(name && !name.startsWith('/') && !name.includes('\\') && !name.includes(':') && !name.split('/').some(part => !part || part === '.' || part === '..'), 'Unsafe archive path: ' + name);
  return name;
}
export function readZip(bytes) {
  const entries = new Map(); let offset = 0;
  while (offset + 30 <= bytes.length && bytes.readUInt32LE(offset) === 0x04034b50) {
    const flags = bytes.readUInt16LE(offset + 6), method = bytes.readUInt16LE(offset + 8);
    assert.equal(flags & 9, 0, 'Encrypted and streaming ZIP entries are unsupported.');
    const size = bytes.readUInt32LE(offset + 18), expected = bytes.readUInt32LE(offset + 22);
    const length = bytes.readUInt16LE(offset + 26), extra = bytes.readUInt16LE(offset + 28);
    const rawName = bytes.subarray(offset + 30, offset + 30 + length).toString('utf8');
    const directory = rawName.endsWith('/');
    const name = safePath(directory ? rawName.slice(0, -1) : rawName);
    const start = offset + 30 + length + extra;
    assert.ok(start + size <= bytes.length && expected <= 256 * 1024 * 1024, 'Invalid ZIP size.');
    assert.ok(!entries.has(name), 'Duplicate ZIP entry.');
    const packed = bytes.subarray(start, start + size);
    const data = method === 0 ? packed : method === 8 ? inflateRawSync(packed, { maxOutputLength: expected }) : null;
    assert.ok(data && data.length === expected, 'Invalid ZIP entry.');
    if (!directory) entries.set(name, data); offset = start + size;
  }
  assert.ok(entries.size && bytes.readUInt32LE(offset) === 0x02014b50, 'Incomplete ZIP archive.');
  return entries;
}
export function runtimeEntries(bytes) {
  const tar = gunzipSync(bytes, { maxOutputLength: 512 * 1024 * 1024 });
  const entries = new Map(); const executables = new Set(); let longName = null;
  for (let offset = 0; offset + 512 <= tar.length && tar[offset] !== 0;) {
    const text = (start, length) => tar.subarray(offset + start, offset + start + length).toString('utf8').split('\0')[0];
    const prefix = text(345, 155), headerName = (prefix ? prefix + '/' : '') + text(0, 100);
    const type = text(156, 1), size = Number.parseInt(text(124, 12).trim(), 8) || 0;
    assert.ok(Number.isSafeInteger(size) && size >= 0 && offset + 512 + size <= tar.length, 'Invalid TAR size.');
    if (type === 'L') {
      assert.ok(longName === null && size > 0 && size <= 4096, 'Invalid long filename record.');
      longName = tar.subarray(offset + 512, offset + 512 + size).toString('utf8').split('\0')[0];
      safePath(longName.replace(/^\.\//, ''));
      offset += 512 + Math.ceil(size / 512) * 512; continue;
    }
    const name = longName ?? headerName; longName = null;
    if (type !== '5') {
      assert.ok(type === '0' || type === '', 'Runtime archives cannot contain links or special files.');
      const normalized = safePath(name.replace(/^\.\//, ''));
      assert.ok(!entries.has(normalized), 'Duplicate runtime entry.');
      if (!forbidden.test(normalized)) {
        entries.set(normalized, tar.subarray(offset + 512, offset + 512 + size));
        if ((Number.parseInt(text(100, 8).trim(), 8) || 0) & 0o111) executables.add(normalized);
      }
    }
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  assert.equal(longName, null, 'Unfinished long filename record.');
  assert.ok(entries.has('dotnet'), 'The runtime archive must include its host.');
  assert.ok([...entries.keys()].some(name => name.startsWith('shared/Microsoft.AspNetCore.App/')), 'ASP.NET Core is missing.');
  assert.ok([...entries.keys()].some(name => name.startsWith('shared/Microsoft.NETCore.App/')), '.NET runtime is missing.');
  assert.ok(entries.has('LICENSE.txt') || entries.has('LICENSE.TXT'), 'Runtime license is missing.');
  return { entries, executables };
}
function tree(folder, entries, prefix = '') {
  for (const item of readdirSync(folder, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
    assert.ok(!item.isSymbolicLink(), 'Package sources cannot contain links.');
    const name = prefix + item.name, source = path.join(folder, item.name);
    if (forbidden.test(name)) continue;
    if (item.isDirectory()) tree(source, entries, name + '/');
    else { safePath(name); assert.ok(!entries.has(name), 'Duplicate package file.'); entries.set(name, readFileSync(source)); }
  }
}
export function validateEntries(entries, kind, version) {
  for (const name of entries.keys()) { safePath(name); assert.ok(!forbidden.test(name), 'Development file in release: ' + name); }
  for (const name of ['LICENSE', 'THIRD-PARTY-NOTICES.md', 'build-info.json', 'app/metroidvania-studio/dist/index.html', 'app/metroidvania-studio/dist/app.js', 'app/samples/catalog.json',
    'app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll', 'app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.deps.json',
    'app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.runtimeconfig.json', 'app/docs/index.md', 'app/docs/api/index.md', 'app/metroidvania-studio/dist/docs/index.html', 'app/metroidvania-studio/dist/docs/docs.js',
    'app/metroidvania-studio/contracts/FORMAT.md', 'app/metroidvania-studio/contracts/map-format-v2.schema.json']) assert.ok(entries.has(name), 'Incomplete release: ' + name);
  assert.equal(JSON.parse(entries.get('build-info.json')).version, version);
  const start = { web: 'metroidvania-studio.bat', win: 'metroidvania-studio.exe', mac: 'metroidvania-studio.command', linux: 'metroidvania-studio.sh' }[kind];
  assert.ok(start && entries.has(start), 'Root launch file is missing.');
  if (kind === 'web' || kind === 'win') assert.ok(entries.has('metroidvania-studio-cli.cmd'), 'Windows CLI launcher is missing.');
  if (kind !== 'win') assert.ok(entries.has('metroidvania-studio-cli.sh'), 'Unix CLI launcher is missing.');
  if (kind === 'win') {
    assert.ok(entries.get(start).subarray(0, 2).equals(Buffer.from('MZ')), 'The Windows file must be a real executable.');
    assert.ok(entries.has('app/runtime/win-x64/dotnet.exe'), 'Bundled Windows runtime is missing.');
  }
  else for (const name of ['server/MetroidvaniaStudio.Server.dll', 'launcher/MetroidvaniaStudio.Launcher.dll']) assert.ok(entries.has('app/metroidvania-studio/' + name), name);
  if (kind === 'web') for (const name of ['metroidvania-studio.command', 'metroidvania-studio.sh', 'app/launch.ps1']) assert.ok(entries.has(name), name);
  if (kind === 'mac' || kind === 'linux') for (const arch of ['arm64', 'x64']) assert.ok(entries.has(`app/runtime/${kind === 'mac' ? 'osx' : 'linux'}-${arch}/dotnet`), 'Bundled runtime is missing.');
}
export async function buildArchives({ common, output, version, commit, windowsPackage, cache = path.join(root, '.local/release-cache') }) {
  const lock = JSON.parse(readFileSync(path.join(root, 'tools/release/runtime-lock.json'), 'utf8'));
  mkdirSync(output, { recursive: true }); mkdirSync(cache, { recursive: true });
  const commonEntries = new Map(); tree(common, commonEntries);
  const archives = [];
  for (const kind of ['web', 'win', 'mac', 'linux']) {
    const entries = kind === 'win' ? readZip(readFileSync(windowsPackage)) : new Map(commonEntries);
    const executables = new Set(), vendorFiles = [];
    if (kind === 'win') {
      assert.equal(JSON.parse(entries.get('build-info.json')).version, version, 'Rebuild the Windows program for this version.');
      for (const [name, bytes] of commonEntries) if (name.startsWith('engine-packages/') || ['LICENSE', 'THIRD-PARTY-NOTICES.md'].includes(name)) entries.set(name, bytes);
      executables.add('metroidvania-studio.exe');
    } else {
      const shell = readFileSync(path.join(root, 'tools/release/launch.sh'), 'utf8').replaceAll('\r\n', '\n').replace('@STUDIO_TARGET@', kind);
      if (kind !== 'linux') entries.set('metroidvania-studio.command', Buffer.from(shell));
      if (kind !== 'mac') entries.set('metroidvania-studio.sh', Buffer.from(shell));
      if (kind === 'web') {
        entries.set('metroidvania-studio.bat', Buffer.from(readFileSync(path.join(root, 'tools/release/launch.bat'), 'utf8').replaceAll('\r\n', '\n').replaceAll('\n', '\r\n')));
        entries.set('app/launch.ps1', readFileSync(path.join(root, 'tools/release/launch.ps1')));
      }
    }
    if (kind !== 'web') {
      const rids = kind === 'win' ? ['win-x64'] : [(kind === 'mac' ? 'osx' : 'linux') + '-arm64', (kind === 'mac' ? 'osx' : 'linux') + '-x64'];
      for (const source of lock.files.filter(file => rids.includes(file.rid))) {
        const rid = source.rid;
        assert.equal(new URL(source.url).protocol, 'https:');
        assert.ok(['builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com'].includes(new URL(source.url).hostname));
        const file = path.join(cache, `${source.component || 'aspnetcore'}-${lock.version}-${rid}.${rid === 'win-x64' ? 'zip' : 'tar.gz'}`);
        if (!existsSync(file)) {
          const response = await fetch(source.url, { signal: AbortSignal.timeout(180000) }); assert.ok(response.ok, 'Runtime download failed.');
          const bytes = Buffer.from(await response.arrayBuffer()); assert.equal(digest(bytes, 'sha512'), source.sha512);
          writeFileSync(file, bytes);
        }
        const bytes = readFileSync(file); assert.equal(digest(bytes, 'sha512'), source.sha512, 'Runtime checksum mismatch.');
        const runtime = rid === 'win-x64' ? { entries: readZip(bytes), executables: new Set() } : runtimeEntries(bytes);
        for (const [name, data] of runtime.entries) {
          if (forbidden.test(name)) continue;
          const destination = /^(?:license|third-party-notices|notice)(?:\.[^/]*)?$/i.test(name) && source.component
            ? `app/notices/${source.component}-${rid}-${name.toLowerCase()}` : `app/runtime/${rid}/${name}`;
          if (entries.has(destination)) assert.ok(entries.get(destination).equals(data), 'Conflicting runtime files: ' + destination);
          entries.set(destination, data);
          vendorFiles.push({ path: destination, sha256: digest(data), source: source.url, sourceSha512: source.sha512 });
          if (runtime.executables.has(name)) executables.add(destination);
        }
      }
    }
    if (kind === 'web' || kind === 'win') entries.set('metroidvania-studio-cli.cmd', readFileSync(path.join(root, 'tools/release/cli.cmd')));
    if (kind !== 'win') { entries.set('metroidvania-studio-cli.sh', readFileSync(path.join(root, 'tools/release/cli.sh'))); executables.add('metroidvania-studio-cli.sh'); }
    entries.set('build-info.json', Buffer.from(JSON.stringify({ version, commit, package: kind, runtimeIncluded: kind !== 'web' }, null, 2) + '\n'));
    if (kind === 'win') {
      entries.delete('runtime-requirements.txt');
      entries.set('INSTALL_EN.txt', Buffer.from('Extract the entire archive to a writable folder.\nOpen metroidvania-studio.exe. Keep the app folder beside it.\nThe .NET runtime is included. If WebView2 Runtime is missing, install it from https://developer.microsoft.com/microsoft-edge/webview2/ before starting.\nMaps are stored in %LOCALAPPDATA%/MetroidvaniaStudio/workspace and remain when replacing this download.\n'));
    } else entries.set('INSTALL_EN.txt', Buffer.from(kind === 'web'
      ? 'Install ASP.NET Core Runtime 10. No SDK, Node.js, Git, or game engine is needed.\nExtract the entire archive to a writable folder.\nWindows: open metroidvania-studio.bat.\nmacOS: open metroidvania-studio.command.\nLinux: run bash metroidvania-studio.sh.\nThe launch file opens your browser. Add --stop to save and stop the local server.\nMaps are kept in your user data folder when replacing this download.\n'
      : `Extract the entire archive to a writable folder.\n${kind === 'mac' ? 'Open metroidvania-studio.command. If macOS blocks the downloaded file, use Open Anyway in Privacy & Security after verifying its source.' : 'Run bash metroidvania-studio.sh, or open it as an executable in your file manager.'}\nThe launch file opens your browser. Add --stop to save and stop the local server.\nThe .NET runtime is included for ARM64 and x64.\nMaps are kept in your user data folder when replacing this download.\n`));
    entries.set('INSTALL_EN.txt', Buffer.concat([entries.get('INSTALL_EN.txt'), Buffer.from('\nHeadless commands: run metroidvania-studio-cli.cmd help on Windows, or bash metroidvania-studio-cli.sh help on macOS/Linux.\nOffline API guide: open app/metroidvania-studio/dist/docs/index.html, or use Help > Documentation in the editor.\n')]));
    validateEntries(entries, kind, version);
    const manifest = { version, commit, package: kind, files: [...entries].sort(([a], [b]) => a.localeCompare(b, 'en')).map(([name, bytes]) => ({ path: name, sha256: digest(bytes) })), vendorFiles };
    entries.set('release-manifest.json', Buffer.from(JSON.stringify(manifest, null, 2) + '\n'));
    const filename = path.join(output, `metroidvania-studio-${kind}.zip`), bytes = zip(entries, { executables });
    writeFileSync(filename, bytes); archives.push({ name: path.basename(filename), path: filename, bytes: bytes.length, sha256: digest(bytes) });
    console.log(`${path.basename(filename)}: ${(bytes.length / 1024 / 1024).toFixed(1)} MiB, ${entries.size} files`);
  }
  writeFileSync(path.join(output, 'SHA256SUMS.txt'), archives.map(file => file.sha256 + '  ' + file.name).join('\n') + '\n');
  return archives;
}
