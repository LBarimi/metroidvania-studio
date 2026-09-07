import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, copyFileSync, existsSync, readdirSync, realpathSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const parent = realpathSync(os.tmpdir());
const temporary = mkdtempSync(path.join(parent, 'studio-installer-'));
after(() => {
  assert.equal(path.dirname(realpathSync(temporary)), parent);
  rmSync(temporary, { recursive: true, force: true });
});
const powershell = 'powershell.exe';
const windows = process.platform === 'win32';
let serial = 0;
function put(name, bytes) { mkdirSync(path.dirname(name), { recursive: true }); writeFileSync(name, bytes); }
function fixture(major, fail = false) {
  const folder = path.join(temporary, 'case ' + (++serial));
  const engine = path.join(folder, 'engine'), project = path.join(folder, 'project/Studio.uproject');
  const packageRoot = path.join(folder, 'package'), tasks = path.join(engine, 'Engine/Build/BatchFiles');
  const plugin = path.join(project, '../Plugins/MetroidvaniaStudio');
  put(path.join(engine, 'Engine/Build/Build.version'), JSON.stringify({ MajorVersion: major, MinorVersion: major === 4 ? 27 : 8 }));
  put(project, JSON.stringify({ FileVersion: 3, EngineAssociation: major === 4 ? '4.27' : '5.8', CustomSetting: 'keep', Plugins: [{ Name: 'ExistingPlugin', Enabled: true }] }));
  put(path.join(plugin, 'previous.txt'), 'previous plugin');
  put(path.join(packageRoot, 'package-manifest.json'), JSON.stringify({ engine: 'ue' + major }));
  put(path.join(packageRoot, 'plugin/MetroidvaniaStudio/MetroidvaniaStudio.uplugin'), '{}');
  for (const name of ['Install-Integration.ps1', 'Resolve-UnrealEngine.ps1'])
    copyFileSync(path.join(root, 'integrations/shared', name), path.join(packageRoot, name));
  put(path.join(tasks, 'RunUAT.bat'), '@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fixture-build.ps1" %*\r\nexit /b %errorlevel%\r\n');
  put(path.join(tasks, 'fixture-build.ps1'), `
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$BuildArguments)
$ErrorActionPreference = 'Stop'
$pluginArgument = @($BuildArguments | Where-Object { $_.StartsWith('-Plugin=') })
$outputArgument = @($BuildArguments | Where-Object { $_.StartsWith('-Package=') })
if ($pluginArgument.Count -ne 1 -or $outputArgument.Count -ne 1) { throw 'Incorrect build arguments.' }
${major === 4 ? "if ($BuildArguments -notcontains '-VS2019') { throw 'Missing UE4 build option.' }" : ''}
${fail ? "exit 42" : ''}
$source = Split-Path -Parent $pluginArgument[0].Substring(8)
$output = $outputArgument[0].Substring(9)
Copy-Item -LiteralPath $source -Destination $output -Recurse
$binary = Join-Path $output 'Binaries/Win64'
New-Item -ItemType Directory -Path $binary -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $binary '${major === 4 ? 'UE4Editor' : 'UnrealEditor'}-MetroidvaniaStudio.dll'), 'fixture binary')
exit 0
`);
  return { folder, engine, project, plugin, packageRoot };
}
function install(f, engine = f.engine) {
  return spawnSync(powershell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(f.packageRoot, 'Install-Integration.ps1'), '-ProjectFile', f.project, '-EngineRoot', engine], { encoding: 'utf8', windowsHide: true, env: { ...process.env, TEMP: f.folder, TMP: f.folder } });
}
for (const major of [4, 5]) test('UE' + major + ' installer uses its binary, preserves project data and backs up repeated installs', { skip: !windows }, () => {
  const f = fixture(major), original = readFileSync(f.project);
  let result = install(f); assert.equal(result.status, 0, result.stdout + result.stderr);
  const project = JSON.parse(readFileSync(f.project));
  assert.equal(project.CustomSetting, 'keep');
  assert.deepEqual(project.Plugins, [{ Name: 'ExistingPlugin', Enabled: true }, { Name: 'MetroidvaniaStudio', Enabled: true }]);
  assert.ok(existsSync(path.join(f.plugin, 'Binaries/Win64/' + (major === 4 ? 'UE4Editor' : 'UnrealEditor') + '-MetroidvaniaStudio.dll')));
  const backups = path.join(f.project, '../.metroidvania-studio-backups');
  const first = path.join(backups, readdirSync(backups)[0]);
  assert.deepEqual(readFileSync(path.join(first, 'Studio.uproject')), original);
  assert.equal(readFileSync(path.join(first, 'previous-plugin/previous.txt'), 'utf8'), 'previous plugin');
  result = install(f); assert.equal(result.status, 0, result.stdout + result.stderr);
  assert.equal(readdirSync(backups).length, 2);
  assert.equal(JSON.parse(readFileSync(f.project)).Plugins.filter(p => p.Name === 'MetroidvaniaStudio').length, 1);
});
test('wrong engine and failed build preserve the existing project and plugin', { skip: !windows }, () => {
  const f = fixture(4), wrong = fixture(5), original = readFileSync(f.project);
  assert.notEqual(install(f, wrong.engine).status, 0);
  assert.deepEqual(readFileSync(f.project), original);
  assert.equal(readFileSync(path.join(f.plugin, 'previous.txt'), 'utf8'), 'previous plugin');
  assert.ok(!existsSync(path.join(f.project, '../.metroidvania-studio-backups')));
  const failed = fixture(4, true), before = readFileSync(failed.project);
  assert.notEqual(install(failed).status, 0);
  assert.deepEqual(readFileSync(failed.project), before);
  assert.equal(readFileSync(path.join(failed.plugin, 'previous.txt'), 'utf8'), 'previous plugin');
});
test('engine discovery prefers a matching major and rejects project version mismatches', { skip: !windows }, () => {
  const old = fixture(4), current = fixture(5);
  const script = path.join(old.folder, 'resolve.ps1');
  put(script, `param($Resolver)\n$ErrorActionPreference='Stop'\nSet-StrictMode -Version Latest\n. $Resolver\n$found=Resolve-StudioUnrealEngine -PackageEngine ue4 -Association 4.27\nif($found.Major -ne 4 -or $found.Root -ne $env:UNREAL_ENGINE4_PATH){throw 'Wrong engine.'}\ntry{Resolve-StudioUnrealEngine -ExplicitRoot $env:UNREAL_ENGINE4_PATH -PackageEngine ue4 -Association 5.8;throw 'Accepted wrong project.'}catch{if($_.Exception.Message -eq 'Accepted wrong project.'){throw}}\n`);
  execFileSync(powershell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, path.join(old.packageRoot, 'Resolve-UnrealEngine.ps1')], { windowsHide: true, env: { ...process.env, UNREAL_ENGINE_PATH: current.engine, UNREAL_ENGINE4_PATH: old.engine } });
});
