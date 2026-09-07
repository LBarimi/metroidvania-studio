import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, cpSync, copyFileSync, writeFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
test('release CLI forwards quoted workspace paths, JSON output and failure exit codes without a window', { skip: process.platform !== 'win32' }, () => {
  const cli = path.join(root, 'metroidvania-studio/cli/bin/Release/net10.0');
  assert.ok(existsSync(path.join(cli, 'MetroidvaniaStudio.Cli.dll')), 'Build the CLI before testing the release launcher.');
  const parent = path.join(root, '.local/release-cli-tests'); mkdirSync(parent, { recursive: true });
  const directory = mkdtempSync(path.join(parent, 'download space & 한글-'));
  const payload = path.join(directory, 'app/metroidvania-studio/cli'); cpSync(cli, payload, { recursive: true });
  const launcher = path.join(directory, 'metroidvania-studio-cli.cmd'); copyFileSync(path.join(root, 'tools/release/cli.cmd'), launcher);
  const workspace = path.join(directory, 'maps space & 한글'); mkdirSync(workspace);
  const quote = value => { assert.ok(!value.includes('"')); return '"' + value + '"'; };
  function run(args) {
    const command = '"' + [launcher, ...args].map(quote).join(' ') + '"';
    return spawnSync(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', command], {
      cwd: directory, env: { ...process.env, METROIDVANIA_STUDIO_DOTNET: process.env.METROIDVANIA_STUDIO_DOTNET || 'dotnet.exe' },
      windowsHide: true, windowsVerbatimArguments: true, encoding: 'utf8', timeout: 30000
    });
  }
  const version = run(['version']); assert.equal(version.status, 0, version.stdout + version.stderr); assert.ok(JSON.parse(version.stdout).version);
  const init = run(['--workspace', workspace, 'init', '--map', 'world.json']); assert.equal(init.status, 0, init.stdout + init.stderr);
  writeFileSync(path.join(workspace, 'layout.lua'), 'studio.room.add { name="Hall", x=0, y=0, width=12, height=8 }');
  const lua = run(['--workspace', workspace, 'run', '--map', 'world.json', '--script', 'layout.lua']); assert.equal(lua.status, 0, lua.stdout + lua.stderr);
  const invalid = run(['--workspace', workspace, 'inspect', '--map', '../outside.json']); assert.notEqual(invalid.status, 0); assert.doesNotThrow(() => JSON.parse(invalid.stdout));
});
