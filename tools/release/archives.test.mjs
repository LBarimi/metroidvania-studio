import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, chmodSync, realpathSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { execFileSync } from 'node:child_process';
import { gzipSync } from 'node:zlib';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readZip, safePath, runtimeEntries, validateEntries } from './archives.mjs';
import { zip } from '../../integrations/build-packages.mjs';
import { tarFile } from '../../integrations/unity/build-package.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const payload = kind => new Map([
  ['LICENSE', Buffer.from('license')], ['THIRD-PARTY-NOTICES.md', Buffer.from('notices')],
  ['build-info.json', Buffer.from('{"version":"1.0.0"}')],
  ...['app/metroidvania-studio/dist/index.html', 'app/metroidvania-studio/dist/app.js', 'app/samples/catalog.json',
    'app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll', 'app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.deps.json',
    'app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.runtimeconfig.json', 'app/docs/index.md', 'app/docs/api/index.md', 'app/metroidvania-studio/dist/docs/index.html', 'app/metroidvania-studio/dist/docs/docs.js',
    'app/metroidvania-studio/contracts/FORMAT.md', 'app/metroidvania-studio/contracts/map-format-v2.schema.json',
    'app/metroidvania-studio/server/MetroidvaniaStudio.Server.dll', 'app/metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll',
    ...({ web: ['metroidvania-studio-cli.cmd', 'metroidvania-studio-cli.sh', 'metroidvania-studio.bat', 'metroidvania-studio.command', 'metroidvania-studio.sh', 'app/launch.ps1'],
      mac: ['metroidvania-studio-cli.sh', 'metroidvania-studio.command', 'app/runtime/osx-arm64/dotnet', 'app/runtime/osx-x64/dotnet'],
      linux: ['metroidvania-studio-cli.sh', 'metroidvania-studio.sh', 'app/runtime/linux-arm64/dotnet', 'app/runtime/linux-x64/dotnet'], win: ['metroidvania-studio-cli.cmd', 'metroidvania-studio.exe', 'app/runtime/win-x64/dotnet.exe'] }[kind])]
    .map(name => [name, Buffer.from(name.endsWith('.exe') ? 'MZfixture' : 'fixture')])
]);
test('release layouts require immediate root launch files and complete matching runtimes', () => {
  for (const kind of ['web', 'win', 'mac', 'linux']) {
    const files = payload(kind); assert.doesNotThrow(() => validateEntries(files, kind, '1.0.0'));
    assert.throws(() => validateEntries(files, kind, '1.0.1'));
    for (const file of ['media/readme/demo.gif', 'README.md', 'app/README.txt', 'app/samples/textures/biomes/README.md', 'platform/mac/run.command', '.local/secret', 'app/test.pdb']) {
      const invalid = new Map(files); invalid.set(file, Buffer.from('bad')); assert.throws(() => validateEntries(invalid, kind, '1.0.0'));
    }
    const start = { web: 'metroidvania-studio.bat', win: 'metroidvania-studio.exe', mac: 'metroidvania-studio.command', linux: 'metroidvania-studio.sh' }[kind];
    files.delete(start); assert.throws(() => validateEntries(files, kind, '1.0.0'));
  }
});
test('ZIP roots and Unix executable permissions survive archive creation', () => {
  const files = payload('linux'), executable = 'app/runtime/linux-x64/dotnet';
  const bytes = zip(files, { executables: new Set([executable]) });
  assert.deepEqual(readZip(bytes), files);
  let offset = bytes.indexOf(Buffer.from([0x50, 0x4b, 0x01, 0x02])); const modes = new Map();
  while (bytes.readUInt32LE(offset) === 0x02014b50) {
    const length = bytes.readUInt16LE(offset + 28), extra = bytes.readUInt16LE(offset + 30), comment = bytes.readUInt16LE(offset + 32);
    const name = bytes.subarray(offset + 46, offset + 46 + length).toString('utf8');
    modes.set(name, bytes.readUInt32LE(offset + 38) >>> 16); offset += 46 + length + extra + comment;
  }
  assert.equal(modes.get(executable) & 0o777, 0o755);
  assert.equal(modes.get('metroidvania-studio.sh') & 0o777, 0o755);
  assert.equal(modes.get('LICENSE') & 0o777, 0o644);
});
test('archive inputs reject traversal, links and incomplete runtime payloads', () => {
  for (const name of ['../escape', '/escape', 'a\\escape', 'a/../escape', 'a//escape']) assert.throws(() => safePath(name));
  const files = ['dotnet', 'LICENSE.txt', 'shared/Microsoft.NETCore.App/10.0.11/runtime.dll', 'shared/Microsoft.AspNetCore.App/10.0.11/web.dll'];
  const archive = gzipSync(Buffer.concat([...files.map(name => tarFile(name, Buffer.from('fixture'))), Buffer.alloc(1024)]));
  assert.equal(runtimeEntries(archive).entries.size, files.length);
  const longName = 'shared/Microsoft.AspNetCore.App/10.0.11/' + 'assembly'.repeat(14) + '.dll';
  const longHeader = tarFile('././@LongLink', Buffer.from(longName + '\0')); longHeader[156] = 'L'.charCodeAt(0);
  const expanded = gzipSync(Buffer.concat([longHeader, tarFile('truncated-name', Buffer.from('long-file')), ...files.map(name => tarFile(name, Buffer.from('fixture'))), Buffer.alloc(1024)]));
  assert.equal(runtimeEntries(expanded).entries.get(longName).toString(), 'long-file');
  const unsafe = tarFile('././@LongLink', Buffer.from('../outside\0')); unsafe[156] = 'L'.charCodeAt(0);
  assert.throws(() => runtimeEntries(gzipSync(unsafe)), /Unsafe archive path/);

  assert.throws(() => runtimeEntries(gzipSync(tarFile('LICENSE.txt', Buffer.from('fixture')))));
  const link = tarFile('dotnet', Buffer.alloc(0)); link[156] = '2'.charCodeAt(0);
  assert.throws(() => runtimeEntries(gzipSync(link)), /links/);
});
const shell = process.platform === 'win32' ? [process.env.ProgramFiles, process.env['ProgramFiles(x86)']].filter(Boolean).map(base => path.join(base, 'Git/bin/bash.exe')).find(existsSync) : '/bin/sh';
const posix = target => process.platform === 'win32' ? target.replaceAll('\\', '/').replace(/^([A-Za-z]):/, (_, drive) => '/' + drive.toLowerCase()) : target;
test('root Unix runners select architecture and preserve arguments with spaces', { skip: !shell }, () => {
  const parent = realpathSync(tmpdir()), folder = mkdtempSync(path.join(parent, 'studio release test '));
  try {
    const tools = path.join(folder, 'tools'); mkdirSync(tools);
    const put = (file, text) => { mkdirSync(path.dirname(file), { recursive: true }); writeFileSync(file, text); chmodSync(file, 0o755); };
    put(path.join(tools, 'uname'), '#!/bin/sh\nif [ "$1" = -s ]; then echo "$STUDIO_TEST_OS"; else echo "$STUDIO_TEST_ARCH"; fi\n');
    const capture = path.join(folder, 'arguments.txt');
    for (const [kind, os, arch, rid] of [['mac', 'Darwin', 'arm64', 'osx-arm64'], ['mac', 'Darwin', 'x86_64', 'osx-x64'], ['linux', 'Linux', 'aarch64', 'linux-arm64'], ['linux', 'Linux', 'x86_64', 'linux-x64']]) {
      const script = path.join(folder, 'metroidvania-studio.' + (kind === 'mac' ? 'command' : 'sh'));
      put(script, readFileSync(path.join(root, 'tools/release/launch.sh'), 'utf8').replace('@STUDIO_TARGET@', kind));
      put(path.join(folder, 'app/runtime', rid, 'dotnet'), '#!/bin/sh\nprintf "%s\\n" "$@" > "$STUDIO_TEST_CAPTURE"\n');
      const env = { ...process.env, PATH: tools + path.delimiter + process.env.PATH, STUDIO_TEST_OS: os, STUDIO_TEST_ARCH: arch, STUDIO_TEST_CAPTURE: posix(capture), STUDIO_TEST_TOOLS: posix(tools) };
      execFileSync(shell, ['-n', posix(script)], { windowsHide: true });
      for (const option of ['', '--stop', '--check']) {
        execFileSync(shell, ['-c', 'PATH="$STUDIO_TEST_TOOLS:$PATH"; export PATH; exec /bin/sh "$@"', 'release-test', posix(script), ...(option ? [option] : []), '--project', 'workspace space & 한글', '--port', '19401', '--no-browser'], { env, windowsHide: true });
        const args = readFileSync(capture, 'utf8').trimEnd().split('\n');
        assert.equal(args[1], option === '--stop' ? 'stop' : option === '--check' ? 'check' : 'run');
        assert.equal(args[args.indexOf('--storage-root') + 1], posix(folder), 'Portable data belongs beside the launch script.');
        assert.ok(args.indexOf('--auto-port') > 1 && args.indexOf('--auto-port') < args.indexOf('--port'));
        assert.deepEqual(args.slice(-5), ['--project', 'workspace space & 한글', '--port', '19401', '--no-browser']);
      }
    }
  } finally { assert.equal(path.dirname(realpathSync(folder)), parent); rmSync(folder, { recursive: true, force: true }); }
});


test('every release rejects a missing automation worker or linked documentation', () => {
  for (const kind of ['web', 'win', 'mac', 'linux']) {
    for (const required of ['app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll',
      'app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.deps.json', 'app/metroidvania-studio/cli/MetroidvaniaStudio.Cli.runtimeconfig.json',
      'app/docs/index.md', 'app/docs/api/index.md', 'app/metroidvania-studio/dist/docs/index.html', 'app/metroidvania-studio/dist/docs/docs.js', 'app/metroidvania-studio/contracts/FORMAT.md',
      'app/metroidvania-studio/contracts/map-format-v2.schema.json']) {
      const files = payload(kind); files.delete(required);
      assert.throws(() => validateEntries(files, kind, '1.0.0'), /Incomplete release/, `${kind}: ${required}`);
    }
  }
});
