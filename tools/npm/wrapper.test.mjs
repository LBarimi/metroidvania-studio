import assert from 'node:assert/strict';
import test from 'node:test';
import { spawn, spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, existsSync, mkdirSync, mkdtempSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { once } from 'node:events';
import { findDotnet } from '../../bin/metroidvania-studio.mjs';
import { findNpm, validateMetadata, validateDocumentLinks } from './pack.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const json = file => JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const runtime = { status: 0, stdout: 'Microsoft.NETCore.App 10.0.11 [runtime]\n' };

test('explicit dotnet override is literal and never falls back', () => {
  const candidate = path.join('tools with spaces & unicode-한글', 'dotnet');
  const seen = [];
  assert.equal(findDotnet({ METROIDVANIA_STUDIO_DOTNET: candidate }, 'linux', (command, args, options) => {
    seen.push(command); assert.deepEqual(args, ['--list-runtimes']); assert.equal(options.windowsHide, true); return runtime;
  }), candidate);
  assert.deepEqual(seen, [candidate]);
  assert.throws(() => findDotnet({ METROIDVANIA_STUDIO_DOTNET: 'missing' }, 'win32', () => ({ status: 1, stdout: '' })), /METROIDVANIA_STUDIO_DOTNET/);
});
test('normal Windows runtime discovery needs no PATH entry', () => {
  const expected = path.join('program-files', 'dotnet', 'dotnet.exe');
  assert.equal(findDotnet({ ProgramFiles: 'program-files' }, 'win32', candidate => candidate === expected ? runtime : ({ status: 1 })), expected);
});
test('runtime detection rejects older and preview-only runtimes', () => {
  for (const version of ['9.0.11', '10.0.0-preview.1', '11.0.0'])
    assert.throws(() => findDotnet({}, 'linux', () => ({ status: 0, stdout: `Microsoft.NETCore.App ${version} [runtime]` })), /.NET 10 runtime/);
});
test('source npm and MCP metadata remain synchronized', () => {
  const manifest = json(path.join(root, 'package.json')), metadata = json(path.join(root, 'tools/mcp/server.json'));
  assert.equal(manifest.private, true); validateMetadata(manifest, metadata);
  assert.throws(() => validateMetadata({ ...manifest, mcpName: 'different' }, metadata), /match/);
});

test('packaged documentation links must resolve inside the published inventory', () => {
  assert.throws(() => validateDocumentLinks([{ name: 'docs/index.md', bytes: Buffer.from('[format](../missing.md)') }]), /Broken packaged/);
  validateDocumentLinks([{ name: 'docs/index.md', bytes: Buffer.from('[format](../FORMAT.md#version)') }, { name: 'FORMAT.md', bytes: Buffer.from('# Format') }]);
});
const report = path.join(root, 'builds/npm/latest.json');
const archive = process.env.METROIDVANIA_STUDIO_NPM_ARCHIVE ?? (existsSync(report) ? path.join(root, 'builds/npm', json(report).archive) : null);
test('installed local package runs CLI, Lua and MCP without a source checkout', { skip: !archive, timeout: 120000 }, async t => {
  const testRoot = path.join(root, '.local/npm-tests'); mkdirSync(testRoot, { recursive: true });
  const directory = mkdtempSync(path.join(testRoot, 'install space 한글-'));
  const workspace = path.join(directory, 'maps space & 한글'); mkdirSync(workspace);
  writeFileSync(path.join(directory, 'package.json'), '{"private":true}\n');
  const env = { ...process.env, PATH: path.dirname(process.execPath) + path.delimiter + (process.env.PATH ?? ''), npm_config_audit: 'false', npm_config_fund: 'false', npm_config_update_notifier: 'false' };
  const install = spawnSync(process.execPath, [findNpm(), 'install', archive, '--offline', '--ignore-scripts', '--no-audit', '--no-fund', '--package-lock=false'],
    { cwd: directory, env, encoding: 'utf8', windowsHide: true, timeout: 60000 });
  assert.equal(install.status, 0, install.stderr);
  const packageRoot = path.join(directory, 'node_modules/metroidvania-studio');
  const wrapper = path.join(packageRoot, 'bin/metroidvania-studio.mjs');
  const manifest = json(path.join(packageRoot, 'package.json'));
  assert.equal(manifest.private, false); assert.equal(manifest.scripts, undefined); assert.equal(manifest.dependencies, undefined);
  const packagedMetadata = json(path.join(packageRoot, 'server.json'));
  assert.deepEqual(packagedMetadata, json(path.join(root, 'tools/mcp/server.json')));
  validateMetadata(manifest, packagedMetadata);
  assert.ok(existsSync(path.join(packageRoot, 'app/licenses/moonsharp-license.txt')));
  assert.ok(!existsSync(path.join(packageRoot, 'media')) && !existsSync(path.join(packageRoot, 'publish')));
  const cli = args => {
    const result = spawnSync(process.execPath, [wrapper, ...args], { cwd: directory, env, encoding: 'utf8', windowsHide: true, timeout: 30000 });
    assert.equal(result.status, 0, result.stdout + result.stderr); return JSON.parse(result.stdout);
  };
  assert.equal(cli(['version']).version, json(path.join(root, 'version.json')).version);
  const executable = spawnSync(process.execPath, [findNpm(), 'exec', '--offline', '--', 'metroidvania-studio', 'version'],
    { cwd: directory, env, encoding: 'utf8', windowsHide: true, timeout: 30000 });
  assert.equal(executable.status, 0, executable.stdout + executable.stderr);
  assert.equal(JSON.parse(executable.stdout).version, json(path.join(root, 'version.json')).version);
  cli(['--workspace', workspace, 'init', '--map', 'world.map.json', '--name', 'World & 한글']);
  const batch = { apiVersion: 1, operations: [
    { op: 'room.add', id: 'a', name: 'Room & 한글', x: 0, y: 0, width: 16, height: 12 },
    { op: 'tiles.rectangle', roomId: 'a', layer: 'foreground', x: 0, y: 0, width: 16, height: 2 }
  ] };
  writeFileSync(path.join(workspace, 'paint.json'), JSON.stringify(batch));
  cli(['--workspace', workspace, 'apply', '--map', 'world.map.json', '--batch', 'paint.json']);
  const inspection = cli(['--workspace', workspace, 'inspect', '--map', 'world.map.json']);
  assert.equal(inspection.document.name, 'World & 한글'); assert.equal(inspection.document.rooms[0].foregroundCount, 32);
  writeFileSync(path.join(workspace, 'change.lua'), 'studio.rename { name = "Scripted World" }');
  cli(['--workspace', workspace, 'run', '--map', 'world.map.json', '--script', 'change.lua', '--dry-run']);
  assert.equal(cli(['--workspace', workspace, 'inspect', '--map', 'world.map.json']).document.name, 'World & 한글');

  const child = spawn(process.execPath, [wrapper, 'mcp', '--workspace', workspace], { cwd: directory, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  t.after(() => { if (child.exitCode === null) child.kill(); });
  let pending = '', stderr = '';
  const replies = new Map(), waiters = new Map();
  child.stderr.setEncoding('utf8'); child.stderr.on('data', data => { stderr += data; });
  child.stdout.setEncoding('utf8'); child.stdout.on('data', data => {
    pending += data;
    let newline;
    while ((newline = pending.indexOf('\n')) >= 0) {
      const line = pending.slice(0, newline).trim(); pending = pending.slice(newline + 1);
      if (!line) continue;
      const value = JSON.parse(line);
      replies.set(value.id, value); waiters.get(value.id)?.(value);
    }
  });
  let next = 1;
  const call = (method, params) => new Promise((resolve, reject) => {
    const id = next++;
    const timer = setTimeout(() => reject(new Error('MCP reply timeout: ' + stderr)), 15000);
    const handler = value => { clearTimeout(timer); waiters.delete(id); resolve(value); };
    handler.timer = timer; waiters.set(id, handler);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  const init = await call('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'package-check', version: '1.0' } });
  assert.equal(init.result.serverInfo.name, 'metroidvania-studio');
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const tools = await call('tools/list', {});
  assert.ok(tools.result.tools.some(tool => tool.name === 'studio_apply'));
  const result = await call('tools/call', { name: 'studio_inspect', arguments: { map: 'world.map.json' } });
  assert.equal(result.error, undefined); assert.notEqual(result.result.isError, true);
  const beforeCancellation = readFileSync(path.join(workspace, 'world.map.json'), 'utf8');
  const revision = cli(['--workspace', workspace, 'inspect', '--map', 'world.map.json']).revision;
  const cancelId = next;
  const canceled = call('tools/call', { name: 'studio_run_lua', arguments: {
    map: 'world.map.json', expectedRevision: revision, source: 'studio.rename {name="Canceled"}; while true do end'
  } });
  setTimeout(() => child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: cancelId, reason: 'Package cancellation check' } }) + '\n'), 30);
  // A canceled MCP request may intentionally have no response. The connection must remain usable.
  const cancelReply = await Promise.race([canceled, new Promise(resolve => setTimeout(() => resolve(null), 700))]);
  if (cancelReply) assert.ok(cancelReply.error || cancelReply.result?.isError, 'Canceled script must fail.');
  clearTimeout(waiters.get(cancelId)?.timer); waiters.delete(cancelId);
  assert.ok((await call('tools/list', {})).result.tools.length > 0);
  assert.equal(readFileSync(path.join(workspace, 'world.map.json'), 'utf8'), beforeCancellation);
  const ended = once(child, 'exit'); child.stdin.end();
  const [exit] = await Promise.race([ended, new Promise((_, reject) => setTimeout(() => reject(new Error('MCP did not exit on EOF.')), 15000).unref())]);
  assert.equal(exit, 0, stderr);

  const terminated = spawn(process.execPath, [wrapper, 'mcp', '--workspace', workspace], { cwd: directory, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  t.after(() => { if (terminated.exitCode === null) terminated.kill(); });
  await new Promise(resolve => setTimeout(resolve, 700));
  const stopped = once(terminated, 'exit'); terminated.kill('SIGTERM');
  await Promise.race([stopped, new Promise((_, reject) => setTimeout(() => reject(new Error('Wrapper did not forward termination.')), 15000).unref())]);
});
