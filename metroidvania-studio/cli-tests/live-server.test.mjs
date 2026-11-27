import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, readFile, readdir, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createInterface } from 'node:readline';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const dotnet = process.env.DOTNET_HOST_PATH || 'dotnet';
const dll = process.env.METROIDVANIA_STUDIO_TEST_CLI || join(root, 'metroidvania-studio/cli/bin/Release/net10.0/MetroidvaniaStudio.Cli.dll');
const serverDll = process.env.METROIDVANIA_STUDIO_TEST_SERVER || join(root, 'metroidvania-studio/server/bin/Release/net10.0/MetroidvaniaStudio.Server.dll');
const wait = ms => new Promise(resolvePromise => setTimeout(resolvePromise, ms));
async function cli(args) {
  const child = spawn(dotnet, [dll, ...args], { cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let out = '', err = '';
  child.stdout.on('data', chunk => out += chunk); child.stderr.on('data', chunk => err += chunk);
  const code = await new Promise((resolvePromise, reject) => { child.once('error', reject); child.once('close', resolvePromise); });
  return { code, json: JSON.parse(out), out, err };
}
async function json(url, path, body) {
  const response = await fetch(url + path, body ? { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) } : {});
  const value = await response.json();
  assert.ok(response.ok, JSON.stringify(value));
  return value;
}

test('actual web server accepts CLI/MCP live edits, shares Undo and exports, and protects its workspace', { timeout: 60000 }, async t => {
  await mkdir(join(root, '.local/cli-live-tests'), { recursive: true });
  const fixture = await mkdtemp(join(root, '.local/cli-live-tests/server-'));
  const workspace = join(fixture, 'workspace');
  await mkdir(join(workspace, 'Maps'), { recursive: true });
  const source = { formatVersion: 2, tileSize: 16, name: 'Live source', rooms: [{ id: 'hall', name: 'Hall', x: 0, y: 0, width: 20, height: 12, visible: true, locked: false, foreground: [], background: [], objects: [], properties: [] }], properties: [], layerGroups: [], stylegrounds: [] };
  await writeFile(join(workspace, 'Maps/World.map.json'), JSON.stringify(source));
  const portProbe = createServer();
  await new Promise(resolvePromise => portProbe.listen(0, '127.0.0.1', resolvePromise));
  const port = portProbe.address().port;
  await new Promise(resolvePromise => portProbe.close(resolvePromise));
  const url = `http://127.0.0.1:${port}`;
  const server = spawn(dotnet, [serverDll, '--studio-root', root, '--project', workspace, '--port', String(port), '--web-root', join(root, 'metroidvania-studio/dist')], { cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let logs = '', health;
  server.stdout.on('data', chunk => logs += chunk); server.stderr.on('data', chunk => logs += chunk);
  const exited = new Promise(resolvePromise => server.once('close', resolvePromise));
  t.after(async () => {
    if (server.exitCode === null) {
      if (health) {
        const response = await fetch(url + '/api/shutdown', { method: 'POST', headers: { 'content-type': 'application/json', 'X-Metroidvania-Studio-Instance': health.instanceId }, body: '{}' });
        assert.ok(response.ok, await response.text());
      } else server.kill();
      await exited;
    }
    await rm(fixture, { recursive: true, force: true });
  });
  for (let n = 0; n < 150; n++) {
    try { const candidate = await json(url, '/api/health'); if (candidate.processId !== server.pid) throw new Error('Unexpected port owner'); health = candidate; break; } catch {}
    assert.equal(server.exitCode, null, logs);
    await wait(50);
  }
  assert.equal(health.processId, server.pid, logs);
  assert.equal(resolve(health.runtimePath), resolve(serverDll), 'launcher identifies the server executable assembly');
  assert.equal((await cli(['editor', '--workspace', workspace])).code, 4, 'headless session respects the web editor writer lock');
  const inspected = await cli(['inspect', '--url', url, '--map', '@active']);
  assert.equal(inspected.code, 0, inspected.out);
  assert.equal(inspected.json.document.name, 'Live source');
  const originalRevision = inspected.json.revision;
  await writeFile(join(workspace, 'edit.json'), JSON.stringify({ apiVersion: 1, operations: [{ op: 'document.update', name: 'CLI live edit' }, { op: 'tiles.rectangle', roomId: 'hall', layer: 'foreground', x: 0, y: 0, width: 20, height: 2 }] }));
  const args = ['apply', '--url', url, '--workspace', workspace, '--map', '@active', '--batch', 'edit.json', '--expected-revision', originalRevision];
  assert.equal((await cli([...args, '--dry-run'])).code, 0);
  assert.equal((await json(url, '/api/v1/document')).document.name, 'Live source');
  const edited = await cli(args);
  assert.equal(edited.code, 0, edited.out);
  assert.equal((await json(url, '/api/v1/document')).document.rooms[0].foreground.length, 40);
  assert.equal((await cli(args)).code, 3, 'stale CLI revision is rejected');
  assert.equal((await cli(['init', '--workspace', workspace, '--map', 'denied.json'])).code, 4, 'live server retains filesystem writer lock');
  const query = await cli(['query-tiles', '--url', url, '--map', '@active', '--room', 'hall', '--layer', 'foreground', '--width', '2', '--height', '2']);
  assert.equal(query.code, 0, query.out);
  assert.equal(query.json.query.cells.length, 4);
  let exported = [];
  for (let n = 0; n < 80; n++) {
    try { exported = (await readdir(join(workspace, 'Maps/AutoExport'), { recursive: true })).filter(path => path.endsWith('.json')); } catch {}
    if (exported.length) break;
    await wait(50);
  }
  assert.ok(exported.length, 'live edit produces automatic room JSON');
  const state = await json(url, '/api/state');
  await json(url, '/api/command', { action: 'undo', clientId: 'cli-live-test', commandId: 'undo-1', expectedInstanceId: state.instanceId, expectedRevision: state.revision });
  assert.deepEqual((await json(url, '/api/v1/document')).document, source, 'one web Undo reverts complete CLI batch');

  const mcp = spawn(dotnet, [dll, 'mcp', '--url', url], { cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const pending = new Map(); let sequence = 0;
  const lines = createInterface({ input: mcp.stdout });
  lines.on('line', line => { const value = JSON.parse(line); assert.equal(value.jsonrpc, '2.0'); pending.get(value.id)?.(value); pending.delete(value.id); });
  const mcpClosed = new Promise(resolvePromise => mcp.once('close', resolvePromise));
  t.after(async () => { mcp.stdin.end(); await mcpClosed; });
  const request = (method, params) => new Promise((resolvePromise, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => reject(new Error('MCP real-server timeout')), 20000);
    pending.set(id, value => { clearTimeout(timer); resolvePromise(value); });
    mcp.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  await request('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'live-test', version: '1.0.0' } });
  mcp.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const snapshot = await request('tools/call', { name: 'studio_inspect', arguments: { map: '@active' } });
  const lua = await request('tools/call', { name: 'studio_run_lua', arguments: { map: '@active', source: 'studio.rename { name = "MCP live edit" }\nprint("live done")', expectedRevision: snapshot.result.structuredContent.revision } });
  assert.equal(lua.result.isError, false, JSON.stringify(lua));
  assert.deepEqual(lua.result.structuredContent.logs, ['live done']);
  assert.equal((await json(url, '/api/v1/document')).document.name, 'MCP live edit');

  const inspectActive = async () => (await request('tools/call', { name: 'studio_inspect', arguments: { map: '@active' } })).result.structuredContent;
  const before = await inspectActive();
  const stamp = { op: 'minimap.place', chip: '8x24', x: 21, y: 3, id: 'shaft', regionColor: '#34899f' };
  const place = async (operations, dryRun = false) => (await request('tools/call', { name: 'studio_apply',
    arguments: { map: '@active', expectedRevision: before.revision, batch: { apiVersion: 1, operations }, dryRun } })).result;
  assert.equal((await place([stamp], true)).isError, false);
  assert.equal((await place([stamp, { ...stamp, id: 'overlap' }])).isError, true);
  assert.equal((await inspectActive()).revision, before.revision);
  const placed = await place([stamp]);
  assert.equal(placed.isError, false, JSON.stringify(placed));
  const after = await inspectActive();
  const shaft = after.document.rooms.find(room => room.roomId === 'shaft');
  assert.deepEqual([shaft.x, shaft.y, shaft.width, shaft.height], [20, 0, 8, 24]);
  assert.equal(shaft.foregroundCount + shaft.backgroundCount + shaft.objectCount, 0);
  assert.equal(shaft.properties['mapMaker.minimapRegionColor'], '#34899f');
  assert.equal(after.document.minimap.connections.length, 1);
  const editor = await json(url, '/api/state');
  assert.equal(editor.document.rooms.find(room => room.id === 'shaft').width, 8);
  await json(url, '/api/command', { action: 'undo', clientId: 'cli-live-test', commandId: 'undo-minimap', expectedInstanceId: editor.instanceId, expectedRevision: editor.revision });
  assert.deepEqual((await inspectActive()).document, before.document);
});
