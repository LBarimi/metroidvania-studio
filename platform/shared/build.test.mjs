import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { chmodSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, symlinkSync, unlinkSync, utimesSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { acquireBuildLock, buildStudio, checkedInputs, runCommand } from './build.mjs';
import { currentBuild, sourceState } from './build-state.mjs';

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
  put(path.join(checkout, 'metroidvania-studio/cli/MetroidvaniaStudio.Cli.csproj'));
  put(path.join(checkout, 'samples/maps/Sample.map.json'), '{"rooms":[]}');
  put(path.join(checkout, 'samples/catalog.json'), '{}');
  put(path.join(checkout, 'metroidvania-studio/localization/MetroidvaniaStudioLocale.csv'), 'Key,KR,EN');
  put(path.join(checkout, 'platform/shared/launch.sh'), '#!/bin/sh');
  put(path.join(checkout, 'tools/build/package-inputs.json'), JSON.stringify([
    'metroidvania-studio/dist', 'samples', 'metroidvania-studio/localization', 'platform/shared/launch.sh', 'LICENSE', 'THIRD-PARTY-NOTICES.md'
  ]));
  for (const notice of ['LICENSE', 'THIRD-PARTY-NOTICES.md'])
    put(path.join(checkout, notice), readFileSync(path.join(root, notice)));
  return checkout;
}
function buildRunner(checkout, failure = '') {
  const calls = [];
  const runner = (command, args) => {
    calls.push({ command, args });
    if (args[0] === '--list-sdks') return '10.0.100 [sdk]\n';
    if (command === 'git' && args[0] === '--version') return 'git version 2.0\n';
    if (args[0] === 'build') return '';
    if (args[0] === 'publish') {
      const name = args[1].includes('/server/') || args[1].includes('\\server\\') ? 'Server' : args[1].includes('Cli.csproj') ? 'Cli' : 'Launcher';
      if (failure === name) throw new Error('Fixture publish failure: ' + name);
      const output = args[args.indexOf('--output') + 1];
      if (failure !== 'missing-' + name) for (const extension of ['dll', 'deps.json', 'runtimeconfig.json'])
        put(path.join(output, 'MetroidvaniaStudio.' + name + '.' + extension), name);
      return '';
    }
    if (args[0].endsWith('build-runtime.mjs')) return '';
    if (args[0].endsWith('build-web.mjs')) { for (const file of ['index.html', 'app.js', 'map-canvas.js']) put(path.join(args[1], file), 'fixture web'); return ''; }
    if (args[0].endsWith('build-packages.mjs')) {
      const output = args[args.indexOf('--output') + 1];
      for (const engine of ['unity', 'godot', 'ue4', 'ue5', 'sdl'])
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
  assert.equal(readFileSync(path.join(first, 'metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll'), 'utf8'), 'Cli');
  assert.ok(existsSync(path.join(first, 'platform/shared/launch.sh')));
  const manifest = JSON.parse(readFileSync(path.join(root, 'tools/build/package-inputs.json')));
  for (const notice of ['LICENSE', 'THIRD-PARTY-NOTICES.md']) {
    assert.ok(manifest.includes(notice), notice + ' must be a required build input.');
    assert.deepEqual(readFileSync(path.join(first, notice)), readFileSync(path.join(root, notice)));
  }
  assert.ok(existsSync(path.join(first, 'engine-packages/unity/metroidvania-studio.unitypackage')));
  assert.equal(calls.filter(call => call.args[0] === 'build').length, 0);
  assert.equal(calls.filter(call => call.args[0] === 'publish').length, 3);
  assert.ok(calls.filter(call => call.args[0] === 'publish').every(call => !call.args.includes('--no-build') && !call.args.includes('--no-restore')));
  const second = buildStudio({ ...options(checkout, runner), rebuild: true });
  assert.equal(calls.filter(call => call.args[0] === 'build').length, 3);
  assert.notEqual(second, first);
  assert.ok(existsSync(path.join(first, 'metroidvania-studio/dist/index.html')));
  assert.equal(JSON.parse(readFileSync(path.join(checkout, '.local/toolchain.json')))['dotnet.exe'], options(checkout, runner).dotnet);
  assert.equal(calls.filter(call => call.args[0] === 'publish').length, 6);
  assert.equal(existsSync(path.join(checkout, '.local/build-v2.lock')), false);
});
for (const failure of ['Launcher', 'missing-Launcher', 'Cli', 'missing-Cli', 'missing-godot', 'missing-ue4', 'missing-ue5', 'missing-sdl']) test(failure + ' failure preserves previous successful build and environment', () => {
  const checkout = fixture();
  const good = buildStudio(options(checkout, buildRunner(checkout).runner));
  const latest = path.join(checkout, 'builds/latest.json'), bytes = readFileSync(latest);
  const previous = process.env.METROIDVANIA_STUDIO_DOTNET;
  process.env.METROIDVANIA_STUDIO_DOTNET = 'preserved-tool-selection';
  try {
    assert.throws(() => buildStudio({ ...options(checkout, buildRunner(checkout, failure).runner), rebuild: true }), /failure|Incomplete build|Incomplete engine package/);
    assert.deepEqual(readFileSync(latest), bytes);
    assert.ok(existsSync(path.join(good, 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll')));
    assert.equal(process.env.METROIDVANIA_STUDIO_DOTNET, 'preserved-tool-selection');
    assert.equal(existsSync(path.join(checkout, '.local/build-v2.lock')), false);
  } finally {
    if (previous === undefined) delete process.env.METROIDVANIA_STUDIO_DOTNET;
    else process.env.METROIDVANIA_STUDIO_DOTNET = previous;
  }
});

test('default build and ensure reuse matching content, including timestamp-only and local workspace changes', () => {
  const checkout = fixture(), { runner } = buildRunner(checkout), output = buildStudio(options(checkout, runner));
  assert.equal(currentBuild(checkout).current, true);
  const source = path.join(checkout, 'metroidvania-studio/server/MetroidvaniaStudio.Server.csproj');
  utimesSync(source, new Date(), new Date(Date.now() + 60000));
  for (const relative of ['.local/workspace/Maps/draft.json', 'metroidvania-studio/.local/log.txt',
    'metroidvania-studio/server/obj/generated.cs', 'metroidvania-studio/web/contracts.generated.ts']) put(path.join(checkout, relative), 'local or generated');
  const noBuild = () => { throw new Error('Current builds must not invoke compilers or SDK checks.'); };
  assert.equal(buildStudio(options(checkout, noBuild)), output);
  assert.equal(buildStudio({ ...options(checkout, noBuild), ensureCurrent: true }), output);
});

test('source comparison detects edits, additions, deletions and version changes', () => {
  const checkout = fixture(), output = buildStudio(options(checkout, buildRunner(checkout).runner));
  const original = sourceState(checkout);
  for (const relative of ['metroidvania-studio/web/app.ts', 'metroidvania-studio/server/NewFeature.cs',
    'metroidvania-studio/contracts/schema.json', 'metroidvania-studio/localization/extra.csv', 'samples/textures/custom.png',
    'docs/guide.md', 'tools/docs/theme.css', 'platform/shared/build.mjs', 'engine-packages/godot/INSTALL_EN.txt']) {
    const target = path.join(checkout, relative);
    put(target, 'new input'); assert.equal(currentBuild(checkout).current, false, relative);
    put(target, 'edited input'); assert.notEqual(sourceState(checkout).sourceHash, original.sourceHash);
    unlinkSync(target); assert.equal(currentBuild(checkout).current, true, relative);
  }
  const source = path.join(checkout, 'samples/maps/Sample.map.json'), bytes = readFileSync(source);
  unlinkSync(source); assert.equal(currentBuild(checkout).current, false);
  put(source, bytes); assert.equal(currentBuild(checkout).current, true);
  put(path.join(checkout, 'version.json'), '{"version":"0.1.1"}');
  assert.match(currentBuild(checkout).reason, /version/);
  assert.ok(existsSync(path.join(output, 'source-state.json')));
});

test('automatic rebuild upgrades legacy metadata and incomplete outputs without changing a successful pointer on failure', () => {
  const checkout = fixture(), first = buildStudio(options(checkout, buildRunner(checkout).runner));
  unlinkSync(path.join(first, 'source-state.json'));
  assert.equal(currentBuild(checkout).current, false);
  const next = buildStudio({ ...options(checkout, buildRunner(checkout).runner), ensureCurrent: true });
  assert.notEqual(next, first); assert.equal(currentBuild(checkout).current, true);
  const pointer = path.join(checkout, 'builds/latest.json'), before = readFileSync(pointer);
  unlinkSync(path.join(next, 'metroidvania-studio/dist/app.js'));
  assert.match(currentBuild(checkout).reason, /incomplete/);
  assert.throws(() => buildStudio({ ...options(checkout, buildRunner(checkout, 'Launcher').runner), ensureCurrent: true }), /failure/);
  assert.deepEqual(readFileSync(pointer), before);
  const repaired = buildStudio({ ...options(checkout, buildRunner(checkout).runner), ensureCurrent: true });
  assert.notEqual(repaired, next); assert.equal(currentBuild(checkout).current, true);
});

test('editing source during a build cannot mark the partial result current', () => {
  const checkout = fixture(), { runner } = buildRunner(checkout);
  buildStudio(options(checkout, runner));
  const pointer = path.join(checkout, 'builds/latest.json'), before = readFileSync(pointer);
  const changingRunner = (command, args, settings) => {
    const result = runner(command, args, settings);
    if (args[0].endsWith('build-packages.mjs')) put(path.join(checkout, 'metroidvania-studio/web/late.ts'), 'changed during build');
    return result;
  };
  put(path.join(checkout, 'metroidvania-studio/web/early.ts'), 'saved change');
  assert.throws(() => buildStudio(options(checkout, changingRunner)), /changed during the build/);
  assert.deepEqual(readFileSync(pointer), before);
});

test('incremental publish updates shared code, removes deleted types and preserves the last good bundle on compiler failure',
  { skip: !process.env.METROIDVANIA_STUDIO_TEST_DOTNET }, () => {
    const checkout = fixture(), dotnet = process.env.METROIDVANIA_STUDIO_TEST_DOTNET;
    const properties = '<TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit>';
    put(path.join(checkout, 'metroidvania-studio/core/Core.csproj'), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>${properties}</PropertyGroup></Project>`);
    const common = path.join(checkout, 'metroidvania-studio/core/BuildValue.cs');
    const extra = path.join(checkout, 'metroidvania-studio/core/ExtraValue.cs');
    put(common, 'public static class BuildValue { public static string Text => "first"; }');
    put(extra, 'public class ExtraValue {}');
    for (const name of ['Server', 'Launcher', 'Cli']) {
      const folder = path.join(checkout, 'metroidvania-studio', name.toLowerCase());
      put(path.join(folder, `MetroidvaniaStudio.${name}.csproj`), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>${properties}<OutputType>Exe</OutputType><AssemblyName>MetroidvaniaStudio.${name}</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="../core/Core.csproj" /></ItemGroup></Project>`);
      put(path.join(folder, 'Program.cs'), 'System.Console.Write(BuildValue.Text + "|" + (typeof(BuildValue).Assembly.GetType("ExtraValue") != null) + "|" + typeof(BuildValue).Assembly.GetName().Version);');
    }
    const fixtureRunner = buildRunner(checkout).runner;
    const runner = (command, args, settings) => ['publish', 'build', '--list-sdks'].includes(args[0])
      ? runCommand(dotnet, args, { ...settings, capture: true }) : fixtureRunner(command, args, settings);
    const build = (extra = {}) => buildStudio({ studioRoot: checkout, dotnet, runner, ...extra });
    const inspect = (output, expected) => {
      for (const name of ['Server', 'Launcher', 'Cli'])
        assert.equal(runCommand(dotnet, [path.join(output, 'metroidvania-studio', name.toLowerCase(), `MetroidvaniaStudio.${name}.dll`)], { capture: true }), expected);
    };
    const first = build(); inspect(first, 'first|True|0.1.0.0');
    put(common, 'public static class BuildValue { public static string Text => "second"; }');
    unlinkSync(extra);
    put(path.join(checkout, 'version.json'), '{"version":"0.1.1"}');
    const second = build(); inspect(second, 'second|False|0.1.1.0'); inspect(first, 'first|True|0.1.0.0');
    assert.equal(build(), second);
    put(common, 'invalid source');
    assert.throws(() => build());
    assert.equal(JSON.parse(readFileSync(path.join(checkout, 'builds/latest.json'))).folder, path.basename(second));
    inspect(second, 'second|False|0.1.1.0');
    put(common, 'public static class BuildValue { public static string Text => "second"; }');
    const rebuilt = build({ rebuild: true }); assert.notEqual(rebuilt, second); inspect(rebuilt, 'second|False|0.1.1.0');
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

test('POSIX source runner checks freshness before launching and stops on a failed build', { skip: !shell }, () => {
  const checkout = fixture(), launch = path.join(checkout, 'platform/shared/launch.sh');
  copyFileSync(path.join(root, 'platform/shared/launch.sh'), launch);
  put(path.join(checkout, 'metroidvania-studio/launcher/bin/Release/net10.0/MetroidvaniaStudio.Launcher.dll'), 'fixture');
  const log = path.join(checkout, 'launch-order.txt'), node = path.join(checkout, 'fake node'), dotnet = path.join(checkout, 'fake dotnet');
  put(node, "#!/bin/sh\nprintf 'build %s\\n' \"$*\" >> \"$STUDIO_TEST_ARGUMENTS\"\nexit \"$STUDIO_TEST_FAILURE\"\n");
  put(dotnet, "#!/bin/sh\nprintf 'launch %s\\n' \"$*\" >> \"$STUDIO_TEST_ARGUMENTS\"\n");
  chmodSync(node, 0o755); chmodSync(dotnet, 0o755);
  const env = { ...process.env, METROIDVANIA_STUDIO_DOTNET: shellPath(dotnet), METROIDVANIA_STUDIO_NODE: shellPath(node), STUDIO_TEST_ARGUMENTS: shellPath(log), STUDIO_TEST_FAILURE: '0' };
  execFileSync(shell, [shellPath(launch), 'run', '--no-browser'], { env, windowsHide: true });
  assert.match(readFileSync(log, 'utf8'), /^build .*--ensure\nlaunch /);
  put(log, '');
  assert.throws(() => execFileSync(shell, [shellPath(launch), 'run', '--no-browser'], { env: { ...env, STUDIO_TEST_FAILURE: '1' }, windowsHide: true, stdio: 'pipe' }));
  assert.doesNotMatch(readFileSync(log, 'utf8'), /launch /);
});

test('Windows source runner builds before launching, preserves arguments and aborts on failure', { skip: process.platform !== 'win32' }, () => {
  const checkout = fixture(), runtime = path.join(checkout, 'metroidvania-studio/Runtime-Tools.ps1');
  copyFileSync(path.join(root, 'metroidvania-studio/Runtime-Tools.ps1'), runtime);
  const cachedLauncher = path.join(checkout, 'metroidvania-studio/launcher/bin/Release/net10.0/MetroidvaniaStudio.Launcher.dll');
  put(cachedLauncher, 'fixture');
  const log = path.join(checkout, 'launch-order.txt'), harness = path.join(checkout, 'invoke.ps1');
  put(path.join(checkout, 'node.ps1'), '[IO.File]::AppendAllText($env:STUDIO_TEST_ARGUMENTS, "build $args" + [Environment]::NewLine)\n$global:LASTEXITCODE = [int]$env:STUDIO_TEST_FAILURE');
  put(path.join(checkout, 'dotnet.ps1'), '[IO.File]::AppendAllText($env:STUDIO_TEST_ARGUMENTS, "launch $args" + [Environment]::NewLine)\n$global:LASTEXITCODE = 0');
  put(harness, [
    "$ErrorActionPreference = 'Stop'",
    ". (Join-Path $PSScriptRoot 'metroidvania-studio/Runtime-Tools.ps1')",
    "function Find-StudioRuntime { param($Name) if ($Name -eq 'node.exe') { return (Join-Path $PSScriptRoot 'node.ps1') } return (Join-Path $PSScriptRoot 'dotnet.ps1') }",
    "Invoke-StudioLauncher -Action 'run' -Arguments @('--project', 'workspace 한글 space', '--no-browser')",
  ].join('\n'));
  const powershell = path.join(process.env.SystemRoot, 'System32/WindowsPowerShell/v1.0/powershell.exe');
  const env = { ...process.env, STUDIO_TEST_ARGUMENTS: log, STUDIO_TEST_FAILURE: '0' };
  const invoke = environment => execFileSync(powershell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', harness], { env: environment, windowsHide: true, stdio: 'pipe' });
  invoke(env);
  const lines = readFileSync(log, 'utf8').trim().split(/\r?\n/);
  assert.match(lines[0], /^build .*--ensure$/);
  assert.match(lines[1], /^launch .*--project workspace .* space --no-browser$/);
  put(log, '');
  assert.throws(() => invoke({ ...env, STUDIO_TEST_FAILURE: '1' }));
  assert.doesNotMatch(readFileSync(log, 'utf8'), /launch /);
  // A ready-to-run package remains usable without Node or a source compiler.
  put(log, '');
  put(path.join(checkout, 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll'), 'published fixture');
  invoke({ ...env, STUDIO_TEST_FAILURE: '1' });
  assert.match(readFileSync(log, 'utf8'), /^launch /);
});
