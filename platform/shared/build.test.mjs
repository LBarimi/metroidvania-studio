import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { chmodSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { acquireBuildLock, buildStudio, checkedInputs } from './build.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const fixtureParent = path.join(root, 'metroidvania-studio/.local');
mkdirSync(fixtureParent, { recursive: true });
const fixtures = mkdtempSync(path.join(fixtureParent, 'platform-build-'));
after(() => {
  assert.equal(path.dirname(realpathSync(fixtures)), realpathSync(fixtureParent));
  rmSync(fixtures, { recursive: true, force: true });
});
let fixtureCount = 0;
function put(target, content = '') {
  mkdirSync(path.dirname(target), { recursive: true });
  writeFileSync(target, content);
}
function fixture() {
  const checkout = path.join(fixtures, 'checkout ' + (++fixtureCount));
  put(path.join(checkout, 'version.json'), '{"version":"0.1.0"}');
  put(path.join(checkout, 'metroidvania-studio/server/MetroidvaniaStudio.Server.csproj'));
  put(path.join(checkout, 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.csproj'));
  put(path.join(checkout, 'samples/maps/Sample.map.json'), '{"rooms":[]}');
  put(path.join(checkout, 'platform/shared/launch.sh'), '#!/bin/sh');
  put(path.join(checkout, 'tools/build/package-inputs.json'), JSON.stringify([
    'metroidvania-studio/dist', 'samples', 'platform/shared/launch.sh'
  ]));
  return checkout;
}
function buildRunner(checkout, failure = '') {
  const calls = [];
  const runner = (command, args) => {
    calls.push({ command, args });
    if (args[0] === '--list-sdks') return '10.0.100 [sdk]\n';
    if (command === 'git' && args[0] === '--version') return 'git version 2.0\n';
    if (args[0] === 'publish') {
      const name = args[1].includes('/server/') || args[1].includes('\\server\\') ? 'Server' : 'Launcher';
      if (failure === name) throw new Error('Fixture publish failure: ' + name);
      const output = args[args.indexOf('--output') + 1];
      if (failure !== 'missing-' + name) put(path.join(output, 'MetroidvaniaStudio.' + name + '.dll'), name);
      return '';
    }
    if (args[0].endsWith('build-web.mjs')) { put(path.join(args[1], 'index.html'), '<!doctype html>'); return ''; }
    if (args[0].endsWith('build-packages.mjs')) {
      const output = args[args.indexOf('--output') + 1];
      for (const engine of ['unity', 'godot', 'ue', 'sdl'])
        if (failure !== 'missing-' + engine) put(path.join(output, engine, engine === 'unity' ? 'metroidvania-studio.unitypackage' : 'metroidvania-studio.zip'), 'fixture package');
      return '';
    }
    throw new Error('Unexpected build command: ' + command);
  };
  return { runner, calls };
}
function options(checkout, runner) {
  return { studioRoot: checkout, dotnet: path.join(checkout, 'fake-dotnet'), runner };
}
test('successful build publishes complete immutable output and then latest pointer', () => {
  const checkout = fixture(), { runner, calls } = buildRunner(checkout);
  const first = buildStudio(options(checkout, runner));
  const pointer = JSON.parse(readFileSync(path.join(checkout, 'builds/latest.json')));
  assert.equal(pointer.folder, path.basename(first));
  assert.equal(pointer.formatVersion, 1);
  assert.equal(readFileSync(path.join(first, 'metroidvania-studio/server/MetroidvaniaStudio.Server.dll'), 'utf8'), 'Server');
  assert.equal(readFileSync(path.join(first, 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll'), 'utf8'), 'Launcher');
  assert.ok(existsSync(path.join(first, 'platform/shared/launch.sh')));
  assert.ok(existsSync(path.join(first, 'engine-packages/unity/metroidvania-studio.unitypackage')));
  const second = buildStudio(options(checkout, runner));
  assert.notEqual(second, first);
  assert.ok(existsSync(path.join(first, 'metroidvania-studio/dist/index.html')));
  assert.equal(JSON.parse(readFileSync(path.join(checkout, '.local/toolchain.json')))['dotnet.exe'], options(checkout, runner).dotnet);
  assert.equal(calls.filter(call => call.args[0] === 'publish').length, 4);
  assert.equal(existsSync(path.join(checkout, '.local/build-v2.lock')), false);
});
for (const failure of ['Launcher', 'missing-Launcher', 'missing-godot', 'missing-ue', 'missing-sdl']) test(failure + ' failure preserves previous successful build and environment', () => {
  const checkout = fixture();
  const good = buildStudio(options(checkout, buildRunner(checkout).runner));
  const latest = path.join(checkout, 'builds/latest.json'), bytes = readFileSync(latest);
  const previous = process.env.METROIDVANIA_STUDIO_DOTNET;
  process.env.METROIDVANIA_STUDIO_DOTNET = 'preserved-tool-selection';
  try {
    assert.throws(() => buildStudio(options(checkout, buildRunner(checkout, failure).runner)), /failure|Incomplete build|Incomplete engine package/);
    assert.deepEqual(readFileSync(latest), bytes);
    assert.ok(existsSync(path.join(good, 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll')));
    assert.equal(process.env.METROIDVANIA_STUDIO_DOTNET, 'preserved-tool-selection');
    assert.equal(existsSync(path.join(checkout, '.local/build-v2.lock')), false);
  } finally {
    if (previous === undefined) delete process.env.METROIDVANIA_STUDIO_DOTNET;
    else process.env.METROIDVANIA_STUDIO_DOTNET = previous;
  }
});
test('check only is read-only and validates SDK and Git without build output', () => {
  const checkout = fixture(), { runner, calls } = buildRunner(checkout);
  assert.equal(buildStudio({ ...options(checkout, runner), checkOnly: true }), null);
  assert.equal(existsSync(path.join(checkout, 'builds')), false);
  assert.equal(existsSync(path.join(checkout, '.local')), false);
  assert.deepEqual(calls.map(call => call.args[0]), ['--list-sdks', '--version']);
  assert.throws(() => buildStudio({ ...options(checkout, () => '9.0.100'), checkOnly: true }), /SDK 10/);
  assert.throws(() => buildStudio({ ...options(checkout, (command, args) => args[0] === '--list-sdks' ? '10.0.100' : ''), checkOnly: true }), /Git is required/);
});
test('build lock refuses active, stale and unreadable owners without deleting them', () => {
  const checkout = fixture(), local = path.join(checkout, '.local'), lock = path.join(local, 'build-v2.lock');
  const release = acquireBuildLock(local);
  const active = readFileSync(lock);
  assert.throws(() => acquireBuildLock(local), /Another build/);
  assert.deepEqual(readFileSync(lock), active);
  release(); assert.equal(existsSync(lock), false);
  put(lock, JSON.stringify({ pid: 2147483647, token: 'interrupted' }));
  const stale = readFileSync(lock);
  assert.throws(() => acquireBuildLock(local), /interrupted build/);
  assert.deepEqual(readFileSync(lock), stale);
  put(lock, 'partial');
  assert.throws(() => acquireBuildLock(local), /unreadable/);
  assert.equal(readFileSync(lock, 'utf8'), 'partial');
});
test('build lock release does not remove a replacement owner', () => {
  const checkout = fixture(), local = path.join(checkout, '.local'), lock = path.join(local, 'build-v2.lock');
  const release = acquireBuildLock(local);
  const replacement = JSON.stringify({ pid: process.pid, token: 'another-owner' });
  put(lock, replacement); release();
  assert.equal(readFileSync(lock, 'utf8'), replacement);
});
test('package inputs reject traversal, absolute paths, missing paths and external links', () => {
  const checkout = fixture(), inputs = path.join(checkout, 'tools/build/package-inputs.json');
  for (const invalid of ['../outside', './samples', 'samples//Maps', path.resolve(fixtures, 'absolute'), 'samples\\Maps', 'missing']) {
    put(inputs, JSON.stringify([invalid]));
    assert.throws(() => checkedInputs(checkout), /Invalid|inside|Missing/);
  }
  const external = path.join(fixtures, 'external-resources');
  mkdirSync(external);
  symlinkSync(external, path.join(checkout, 'External'), process.platform === 'win32' ? 'junction' : 'dir');
  put(inputs, '["External"]');
  assert.throws(() => checkedInputs(checkout), /external build input/);
});
const shell = process.platform === 'win32'
  ? [process.env.ProgramFiles, process.env['ProgramFiles(x86)']].filter(Boolean).map(base => path.join(base, 'Git/bin/bash.exe')).find(existsSync)
  : '/bin/sh';
const shellPath = target => process.platform === 'win32' ? target.replaceAll('\\', '/').replace(/^([A-Za-z]):/, (_, drive) => '/' + drive.toLowerCase()) : target;
test('POSIX wrappers parse and preserve spaces, Unicode and modes without Node on prebuilt runs', { skip: !shell }, () => {
  const checkout = fixture(), argumentsPath = path.join(checkout, 'captured arguments.txt');
  for (const relative of ['platform/shared/launch.sh', ...['linux', 'mac'].flatMap(platform =>
    ['build', 'run', 'stop'].map(action => 'platform/' + platform + '/' + action + (platform === 'mac' ? '.command' : '.sh')))]) {
    const destination = path.join(checkout, relative);
    mkdirSync(path.dirname(destination), { recursive: true });
    copyFileSync(path.join(root, relative), destination);
    execFileSync(shell, ['-n', shellPath(destination)], { windowsHide: true });
  }
  put(path.join(checkout, 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll'), 'fixture');
  const dotnet = path.join(checkout, 'fake dotnet');
  put(dotnet, '#!/bin/sh\nprintf \'%s\\n\' "$@" > "$STUDIO_TEST_ARGUMENTS"\n');
  chmodSync(dotnet, 0o755);
  const workspace = 'workspace \uD55C\uAE00 space';
  const env = { ...process.env, METROIDVANIA_STUDIO_DOTNET: shellPath(dotnet),
    METROIDVANIA_STUDIO_NODE: 'missing-node-for-prebuilt-run', STUDIO_TEST_ARGUMENTS: shellPath(argumentsPath) };
  for (const [platform, extension] of [['linux', '.sh'], ['mac', '.command']]) {
    for (const action of ['run', 'stop']) {
      execFileSync(shell, [shellPath(path.join(checkout, 'platform', platform, action + extension)), '--project', workspace, '--port', '18888', '--no-browser'],
        { env, windowsHide: true, stdio: 'pipe' });
      const args = readFileSync(argumentsPath, 'utf8').trimEnd().split('\n');
      assert.equal(args[1], action);
      assert.deepEqual(args.slice(-5), ['--project', workspace, '--port', '18888', '--no-browser']);
      assert.equal(args[2], '--studio-root');
      assert.equal(args[3], shellPath(checkout));
    }
  }
  const run = shellPath(path.join(checkout, 'platform/linux/run.sh'));
  execFileSync(shell, [run, '--check', '--project', workspace, '--no-browser'], { env, windowsHide: true });
  assert.equal(readFileSync(argumentsPath, 'utf8').split('\n')[1], 'check');
  const publishedLauncher = path.join(checkout, 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll');
  put(path.join(checkout, 'metroidvania-studio/launcher/bin/Release/net10.0/MetroidvaniaStudio.Launcher.dll'), 'fixture');
  unlinkSync(publishedLauncher);
  // An explicitly selected bundle does not require a latest pointer or rebuild.
  execFileSync(shell, [run, '--build-directory', 'explicit bundle', '--project', workspace, '--no-browser'], { env, windowsHide: true });
  assert.deepEqual(readFileSync(argumentsPath, 'utf8').trimEnd().split('\n').slice(-5), ['--build-directory', 'explicit bundle', '--project', workspace, '--no-browser']);
  assert.equal(existsSync(path.join(checkout, 'builds')), false);
});
