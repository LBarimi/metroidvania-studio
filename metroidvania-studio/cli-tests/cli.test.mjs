import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, readFile, rm, symlink, access } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createInterface } from 'node:readline';
import { createServer } from 'node:http';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const dotnet = process.env.DOTNET_HOST_PATH || 'dotnet';
const dll = process.env.METROIDVANIA_STUDIO_TEST_CLI || join(root, 'metroidvania-studio/cli/bin/Release/net10.0/MetroidvaniaStudio.Cli.dll');
const sample = { formatVersion: 2, tileSize: 16, name: 'Test', rooms: [], properties: [], stylegrounds: [], layerGroups: [] };

function processCli(args, input = '') {
  const child = spawn(dotnet, [dll, ...args], { cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  let out = '', err = '';
  child.stdout.on('data', data => out += data);
  child.stderr.on('data', data => err += data);
  const done = new Promise((resolvePromise, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error('CLI did not finish')); }, 25000);
    child.once('error', reject);
    child.once('close', code => { clearTimeout(timer); resolvePromise({ code, out, err, json: out.trim() ? JSON.parse(out) : null }); });
  });
  child.stdin.on('error', () => {});
  child.stdin.end(input);
  return { child, done };
}
const cli = async (workspace, args) => processCli(['--workspace', workspace, ...args]).done;

async function fixture(t) {
  await mkdir(join(root, '.local/cli-tests'), { recursive: true });
  const directory = await mkdtemp(join(root, '.local/cli-tests/workspace space-'));
  await writeFile(join(directory, 'world.json'), JSON.stringify(sample));
  t.after(() => rm(directory, { recursive: true, force: true }));
  return directory;
}

function mcp(workspace, flags = []) {
  const child = spawn(dotnet, [dll, 'mcp', ...(workspace ? ['--workspace', workspace] : []), ...flags], { cwd: root, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const pending = new Map();
  const messages = [];
  let sequence = 0, stderr = '';
  child.stderr.on('data', data => stderr += data);
  const reader = createInterface({ input: child.stdout });
  reader.on('line', line => {
    const value = JSON.parse(line);
    messages.push(value);
    const waiter = pending.get(value.id);
    if (waiter) { pending.delete(value.id); waiter(value); }
  });
  const closed = new Promise(resolvePromise => child.once('close', code => resolvePromise(code)));
  return {
    child, messages,
    notify(method, params = {}) { child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n'); },
    request(method, params = {}) {
      const id = ++sequence;
      let timer;
      const promise = new Promise((resolvePromise, reject) => {
        timer = setTimeout(() => { pending.delete(id); reject(new Error('MCP timeout: ' + method + ' ' + stderr)); }, 22000);
        pending.set(id, value => { clearTimeout(timer); resolvePromise(value); });
      });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
      return { id, promise, abandon() { clearTimeout(timer); pending.delete(id); } };
    },
    async call(name, args) { return (await this.request('tools/call', { name, arguments: args }).promise).result; },
    async initialize() {
      const result = await this.request('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'studio-test', version: '1.0.0' } }).promise;
      assert.equal(result.result.protocolVersion, '2025-11-25');
      this.notify('notifications/initialized');
      return result;
    },
    async close() {
      child.stdin.end();
      const timer = setTimeout(() => child.kill(), 5000);
      const code = await closed;
      clearTimeout(timer);
      for (const message of messages) assert.equal(message.jsonrpc, '2.0', 'stdout only contains protocol messages');
      return code;
    }
  };
}

const addRoom = { apiVersion: 1, operations: [{ op: 'room.add', id: 'start', name: 'Start', x: 0, y: 0, width: 20, height: 12 }] };

test('CLI provides machine-readable help/version and operation schemas', async () => {
  for (const command of ['help', 'version', 'capabilities']) {
    const result = await processCli([command]).done;
    assert.equal(result.code, 0, result.out);
    assert.equal(result.err, '');
    assert.ok(result.json);
  }
});

test('CLI creates, inspects, validates and refuses accidental overwrite', async t => {
  const workspace = await fixture(t);
  assert.equal((await cli(workspace, ['init', '--map', 'new/map.json', '--name', 'New'])).code, 0);
  assert.equal((await cli(workspace, ['init', '--map', 'new/map.json'])).code, 3);
  assert.equal((await cli(workspace, ['validate', '--map', 'new/map.json'])).json.valid, true);
  const inspect = await cli(workspace, ['inspect', '--map', 'new/map.json']);
  assert.equal(inspect.json.document.name, 'New');
  assert.match(inspect.json.revision, /^[a-f0-9]{64}$/);
});

test('atomic batch, dry-run, revision conflict, room export, SVG preview', async t => {
  const workspace = await fixture(t);
  await writeFile(join(workspace, 'edit.json'), JSON.stringify(addRoom));
  const original = await readFile(join(workspace, 'world.json'), 'utf8');
  let result = await cli(workspace, ['apply', '--map', 'world.json', '--batch', 'edit.json', '--dry-run']);
  assert.equal(result.code, 0, result.out);
  assert.equal(result.json.changed, true);
  assert.equal(await readFile(join(workspace, 'world.json'), 'utf8'), original);
  result = await cli(workspace, ['apply', '--map', 'world.json', '--batch', 'edit.json']);
  assert.equal(result.code, 0, result.out);
  assert.equal(JSON.parse(await readFile(join(workspace, 'world.json'), 'utf8')).rooms.length, 1);
  assert.equal((await cli(workspace, ['apply', '--map', 'world.json', '--batch', 'edit.json', '--expected-revision', '0'.repeat(64)])).code, 3);
  const exported = await cli(workspace, ['export-room', '--map', 'world.json', '--room', 'start', '--output', 'exports/start.json']);
  assert.equal(exported.code, 0, exported.out);
  assert.equal(JSON.parse(await readFile(join(workspace, 'exports/start.json'), 'utf8')).rooms.length, 1);
  const preview = await cli(workspace, ['preview', '--map', 'world.json', '--output', 'preview.svg']);
  assert.equal(preview.code, 0, preview.out);
  assert.match(await readFile(join(workspace, 'preview.svg'), 'utf8'), /<svg.*#ffffff/);
  assert.equal((await cli(workspace, ['preview', '--map', 'world.json', '--output', 'world.json', '--force'])).code, 2);
});

test('failed batch rolls back and Lua worker reports useful errors without a save', async t => {
  const workspace = await fixture(t);
  const original = await readFile(join(workspace, 'world.json'), 'utf8');
  await writeFile(join(workspace, 'bad.json'), JSON.stringify({ apiVersion: 1, operations: [...addRoom.operations, { op: 'not-an-operation' }] }));
  assert.equal((await cli(workspace, ['apply', '--map', 'world.json', '--batch', 'bad.json'])).code, 2);
  await writeFile(join(workspace, 'script.lua'), 'error("intentional")');
  const failed = await cli(workspace, ['run', '--map', 'world.json', '--script', 'script.lua']);
  assert.equal(failed.code, 2, failed.out);
  assert.match(failed.json.error.message, /intentional/);
  assert.equal(await readFile(join(workspace, 'world.json'), 'utf8'), original);
  await writeFile(join(workspace, 'script.lua'), 'studio.room.add { id = "generated", name = "Generated", x = 0, y = 0, width = 20, height = 12 }');
  const result = await cli(workspace, ['run', '--map', 'world.json', '--script', 'script.lua']);
  assert.equal(result.code, 0, result.out);
  assert.equal(result.json.operationCount, 1);
});

test('workspace root, symlinks, reserved paths and read-only policy are enforced', async t => {
  const workspace = await fixture(t);
  for (const path of ['../outside.json', resolve(workspace, 'world.json'), '.studio/server.lock', 'folder/../world.json', 'world.json:stream'])
    assert.equal((await cli(workspace, ['inspect', '--map', path])).code, 2, path);
  assert.equal((await cli(workspace, ['init', '--map', 'readonly.json', '--read-only'])).code, 6);
  assert.equal((await cli(workspace, ['init', '--map', 'readonly.json', '--read-only', '--dry-run'])).code, 0);
  const other = await fixture(t);
  await symlink(other, join(workspace, 'linked'), 'junction');
  assert.equal((await cli(workspace, ['inspect', '--map', 'linked/world.json'])).code, 2);
  assert.equal((await cli(join(workspace, 'linked'), ['inspect', '--map', 'world.json'])).code, 2);
});

test('CLI cannot edit a workspace held by another writer', async t => {
  const workspace = await fixture(t);
  await writeFile(join(workspace, 'long.lua'), 'while true do end');
  const running = processCli(['run', '--workspace', workspace, '--map', 'world.json', '--script', 'long.lua']);
  for (let n = 0; n < 80; n++) {
    try { await access(join(workspace, '.studio/server.lock')); break; } catch {}
    await new Promise(resolvePromise => setTimeout(resolvePromise, 25));
  }
  const busy = await cli(workspace, ['init', '--map', 'second.json']);
  assert.equal(busy.code, 4, busy.out);
  assert.notEqual((await running.done).code, 0);
});

test('worker contract accepts one input and emits one output without touching workspace', async () => {
  const result = await processCli(['worker'], JSON.stringify({ kind: 'batch', documentJson: JSON.stringify(sample), batch: addRoom })).done;
  assert.equal(result.code, 0, result.out);
  assert.equal(result.json.operationCount, 1);
  assert.equal(JSON.parse(result.json.documentJson).rooms.length, 1);
  assert.equal((await processCli(['worker'], '{bad').done).code, 2);
});

test('MCP negotiation, tools, structured errors, revisions and read-only mode', async t => {
  const workspace = await fixture(t);
  const session = mcp(workspace);
  t.after(() => session.close());
  await session.initialize();
  const list = await session.request('tools/list').promise;
  assert.equal(list.result.tools.length, 9);
  const apply = list.result.tools.find(tool => tool.name === 'studio_apply');
  assert.ok(apply.inputSchema.required.includes('expectedRevision'));
  assert.equal(apply.annotations.openWorldHint, false);
  assert.equal((await session.request('unknown/method').promise).error.code, -32601);
  const inspect = await session.call('studio_inspect', { map: 'world.json' });
  const revision = inspect.structuredContent.revision;
  const result = await session.call('studio_apply', { map: 'world.json', batch: addRoom, expectedRevision: revision });
  assert.equal(result.isError, false, JSON.stringify(result));
  assert.equal(result.structuredContent.operationCount, 1);
  const stale = await session.call('studio_apply', { map: 'world.json', batch: addRoom, expectedRevision: revision });
  assert.equal(stale.isError, true);
  assert.equal(stale.structuredContent.error.code, 'conflict');
  const traversal = await session.call('studio_inspect', { map: '../outside.json' });
  assert.equal(traversal.structuredContent.error.code, 'invalid_input');
  const unknown = await session.request('tools/call', { name: 'unknown_tool', arguments: {} }).promise;
  assert.ok(unknown.error || unknown.result?.isError);
  const readonly = mcp(workspace, ['--read-only']);
  t.after(() => readonly.close());
  await readonly.initialize();
  const denied = await readonly.call('studio_create', { map: 'denied.json' });
  assert.equal(denied.structuredContent.error.code, 'read_only');
});

test('MCP cancellation discards a long-running Lua edit and keeps transport usable', async t => {
  const workspace = await fixture(t);
  const session = mcp(workspace);
  t.after(() => session.close());
  await session.initialize();
  const original = await readFile(join(workspace, 'world.json'), 'utf8');
  const inspect = await session.call('studio_inspect', { map: 'world.json' });
  const request = session.request('tools/call', { name: 'studio_run_lua', arguments: { map: 'world.json', source: 'while true do end', expectedRevision: inspect.structuredContent.revision } });
  await new Promise(resolvePromise => setTimeout(resolvePromise, 150));
  session.notify('notifications/cancelled', { requestId: request.id, reason: 'test cancellation' });
  request.abandon();
  await new Promise(resolvePromise => setTimeout(resolvePromise, 200));
  const nextEdit = await session.call("studio_create", { map: "after-cancel.json" });
  assert.equal(nextEdit.isError, false, JSON.stringify(nextEdit));
  assert.equal(await readFile(join(workspace, 'world.json'), 'utf8'), original);
  assert.ok((await session.request('ping').promise).result);
});



test('current MCP discovery flow advertises its supported protocol versions', async t => {
  const workspace = await fixture(t);
  const session = mcp(workspace);
  t.after(() => session.close());
  const discovered = await session.request('server/discover', { _meta: { 'io.modelcontextprotocol/protocolVersion': '2026-07-28', 'io.modelcontextprotocol/clientCapabilities': {}, 'io.modelcontextprotocol/clientInfo': { name: 'studio-test', version: '1.0.0' } } }).promise;
  assert.ok(discovered.result?.supportedVersions.includes('2026-07-28'), JSON.stringify(discovered));
});

test('live CLI/MCP uses instance/revision guards and refuses network redirects', async t => {
  const workspace = await fixture(t);
  let map = structuredClone(sample), revision = 1;
  const received = [];
  const jobs = new Map();
  const server = createServer(async (req, res) => {
    let body = '';
    for await (const chunk of req) body += chunk;
    const value = body ? JSON.parse(body) : null;
    received.push({ path: req.url, body: value });
    const send = (status, result) => { res.writeHead(status, { 'content-type': 'application/json' }); res.end(JSON.stringify(result)); };
    if (req.url === '/api/v1/document') return send(200, { apiVersion: 1, instanceId: 'test-instance', documentRevision: revision, document: map });
    if (req.url === '/api/v1/jobs' && req.method === 'POST') {
      if (value.expectedInstanceId !== 'test-instance' || value.expectedDocumentRevision !== revision) return send(409, { error: 'Conflict' });
      const id = String(jobs.size + 1);
      const job = { id, phase: value.kind === 'lua' ? 'running' : 'completed', dryRun: value.dryRun, operationCount: 1, changed: true, logs: [] };
      jobs.set(id, job);
      if (value.kind === 'batch' && !value.dryRun) { map.name = 'Edited live'; revision++; }
      return send(202, job);
    }
    const cancel = /^\/api\/v1\/jobs\/(\d+)\/cancel$/.exec(req.url);
    if (cancel) { jobs.get(cancel[1]).phase = 'cancelled'; return send(200, jobs.get(cancel[1])); }
    const get = /^\/api\/v1\/jobs\/(\d+)$/.exec(req.url);
    if (get) return send(200, jobs.get(get[1]));
    send(404, {});
  });
  await new Promise(resolvePromise => server.listen(0, '127.0.0.1', resolvePromise));
  const url = `http://127.0.0.1:${server.address().port}`;
  t.after(() => new Promise(resolvePromise => server.close(resolvePromise)));
  let result = await processCli(['inspect', '--url', url, '--map', '@active']).done;
  assert.equal(result.code, 0, result.out);
  assert.equal(result.json.instanceId, 'test-instance');
  const firstRevision = result.json.revision;
  await writeFile(join(workspace, 'edit.json'), JSON.stringify({ apiVersion: 1, operations: [{ op: 'document.update', name: 'Edited live' }] }));
  result = await cli(workspace, ['apply', '--url', url, '--map', '@active', '--batch', 'edit.json', '--expected-revision', firstRevision]);
  assert.equal(result.code, 0, result.out);
  assert.equal(result.json.live, true);
  assert.equal(received.find(item => item.path === '/api/v1/jobs').body.expectedDocumentRevision, 1);
  assert.equal((await cli(workspace, ['apply', '--url', url, '--map', '@active', '--batch', 'edit.json', '--expected-revision', firstRevision])).code, 3);
  assert.equal((await processCli(['inspect', '--url', url, '--map', 'world.json']).done).code, 2);
  const session = mcp(null, ['--url', url]);
  t.after(() => session.close());
  await session.initialize();
  const inspect = await session.call('studio_inspect', { map: '@active' });
  const request = session.request('tools/call', { name: 'studio_run_lua', arguments: { map: '@active', source: 'while true do end', expectedRevision: inspect.structuredContent.revision } });
  for (let n = 0; n < 100 && !received.some(item => item.body?.kind === 'lua'); n++) await new Promise(resolvePromise => setTimeout(resolvePromise, 20));
  session.notify('notifications/cancelled', { requestId: request.id });
  request.abandon();
  for (let n = 0; n < 100 && !received.some(item => item.path.endsWith('/cancel')); n++) await new Promise(resolvePromise => setTimeout(resolvePromise, 20));
  const cancel = received.find(item => item.path.endsWith('/cancel'));
  assert.ok(cancel, 'client sends live job cancellation');
  assert.equal(cancel.body.clientId, received.find(item => item.body?.kind === 'lua').body.clientId);
  const redirectServer = createServer((req, res) => { res.writeHead(302, { location: 'http://example.invalid/' }); res.end(); });
  await new Promise(resolvePromise => redirectServer.listen(0, '127.0.0.1', resolvePromise));
  t.after(() => new Promise(resolvePromise => redirectServer.close(resolvePromise)));
  result = await processCli(['inspect', '--url', `http://127.0.0.1:${redirectServer.address().port}`, '--map', '@active']).done;
  assert.equal(result.code, 2);
  assert.match(result.json.error.message, /redirect/);
});

test('live URL validation rejects external hosts, credentials, paths and privileged ports', async () => {
  for (const url of ['https://127.0.0.1:18765', 'http://example.invalid:18765', 'http://127.0.0.1:80', 'http://127.0.0.1:18765/path', 'http://user@127.0.0.1:18765', 'http://127.0.0.1:18765/?query=true'])
    assert.equal((await processCli(['inspect', '--url', url, '--map', '@active']).done).code, 2, url);
});



test('bounded tile queries expose actual terrain and reject out-of-room or oversized regions', async t => {
  const workspace = await fixture(t);
  const document = structuredClone(sample);
  document.rooms.push({ id: 'hall', name: 'Hall', x: 0, y: 0, width: 100, height: 100, visible: true, locked: false, foreground: [
    { x: 1, y: 1, shape: 3, material: 'stone', groupId: '' },
    { x: 0, y: 0, shape: 0, material: 'terrain', groupId: '' }
  ], background: [], objects: [], properties: [] });
  await writeFile(join(workspace, 'world.json'), JSON.stringify(document));
  const args = ['query-tiles', '--map', 'world.json', '--room', 'hall', '--layer', 'foreground'];
  const result = await cli(workspace, [...args, '--width', '2', '--height', '2']);
  assert.equal(result.code, 0, result.out);
  assert.equal(result.json.query.cells.length, 2);
  assert.equal(result.json.query.cells[0].x, 0);
  assert.equal(result.json.query.cells[1].materialId, 'stone');
  assert.equal((await cli(workspace, [...args, '--width', '100', '--height', '100'])).code, 2);
  assert.equal((await cli(workspace, [...args, '--x', '100'])).code, 2);
  const session = mcp(workspace);
  t.after(() => session.close());
  await session.initialize();
  const query = await session.call('studio_query_tiles', { map: 'world.json', roomId: 'hall', layer: 'foreground', width: 2, height: 2 });
  assert.equal(query.isError, false, JSON.stringify(query));
  assert.deepEqual(query.structuredContent.query.cells, result.json.query.cells);
  assert.equal(await readFile(join(workspace, 'world.json'), 'utf8'), JSON.stringify(document));
});

test('live job recovers a lost POST response with the same command and cancels after a polling failure', async t => {
  const workspace = await fixture(t);
  await writeFile(join(workspace, 'edit.json'), JSON.stringify({ apiVersion: 1, operations: [] }));
  let submissions = [], cancellation = null, failPolling = false;
  const server = createServer(async (req, res) => {
    let text = '';
    for await (const chunk of req) text += chunk;
    const body = text ? JSON.parse(text) : null;
    const send = value => { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(value)); };
    if (req.url === '/api/v1/document') return send({ instanceId: 'network-test', documentRevision: 1, document: sample });
    if (req.url === '/api/v1/jobs') {
      submissions.push(body);
      if (submissions.length === 1) { req.socket.destroy(); return; }
      return send({ id: 'original-job', phase: failPolling ? 'running' : 'completed', operationCount: 0, changed: false, logs: [] });
    }
    if (req.url === '/api/v1/jobs/original-job/cancel') { cancellation = body; return send({ phase: 'cancelled' }); }
    if (req.url === '/api/v1/jobs/original-job') { req.socket.destroy(); return; }
    res.writeHead(404); res.end('{}');
  });
  await new Promise(resolvePromise => server.listen(0, '127.0.0.1', resolvePromise));
  t.after(() => new Promise(resolvePromise => server.close(resolvePromise)));
  const args = ['apply', '--url', `http://127.0.0.1:${server.address().port}`, '--map', '@active', '--batch', 'edit.json'];
  const result = await cli(workspace, args);
  assert.equal(result.code, 0, result.out);
  assert.equal(submissions.length, 2);
  assert.deepEqual(submissions[0], submissions[1], 'uncertain POST retries retain exact instance/revision/client/command and batch');
  failPolling = true;
  const failed = await cli(workspace, args);
  assert.equal(failed.code, 1, failed.out);
  assert.equal(failed.json.error.code, 'uncertain_result');
  assert.equal(cancellation.clientId, submissions.at(-1).clientId);
});
