import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, renameSync, rmdirSync, writeFileSync } from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const dotnet = process.env.METROIDVANIA_STUDIO_DOTNET || 'dotnet';
const launcher = path.join(repository, 'MetroidvaniaStudio/Launcher/bin/Release/net10.0/MetroidvaniaStudio.Launcher.dll');
const readJson = file => JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const writeJson = (file, value) => writeFileSync(file, JSON.stringify(value, null, 2) + '\n');
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

async function availablePort() {
  const listener = net.createServer();
  await new Promise((resolve, reject) => {
    listener.once('error', reject);
    listener.listen(0, '127.0.0.1', resolve);
  });
  const port = listener.address().port;
  await new Promise(resolve => listener.close(resolve));
  return port;
}
async function jsonRequest(port, route, init = {}) {
  const response = await fetch(`http://127.0.0.1:${port}${route}`, {
    ...init, redirect: 'error', signal: AbortSignal.timeout(15000),
  });
  assert.equal(response.ok, true, await response.clone().text());
  return response.json();
}
async function optionalHealth(port) {
  try {
    const response = await fetch(`http://127.0.0.1:${port}/api/health`, {
      redirect: 'error', signal: AbortSignal.timeout(1000),
    });
    return response.ok ? await response.json() : null;
  } catch { return null; }
}
async function waitUntil(predicate, message, milliseconds = 15000) {
  const deadline = Date.now() + milliseconds;
  while (Date.now() < deadline) {
    if (await predicate()) return;
    await delay(100);
  }
  assert.fail(message);
}
function invoke(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(dotnet, [launcher, ...args], {
      cwd: repository, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' },
    });
    let output = '';
    child.stdout.on('data', chunk => { output += chunk; });
    child.stderr.on('data', chunk => { output += chunk; });
    const timeout = setTimeout(() => {
      // Only terminate this test-owned launcher command, never its detached server.
      child.kill();
      reject(new Error(`Launcher command timed out: ${output}`));
    }, 60000);
    child.once('error', error => { clearTimeout(timeout); reject(error); });
    child.once('exit', (code, signal) => {
      clearTimeout(timeout);
      resolve({ code, signal, output });
    });
  });
}

// Every scenario uses a disposable public-sample workspace, never the user's live map.
test('platform launcher preserves ownership, builds and pending edits', { timeout: 300000 }, async context => {
  assert.ok(existsSync(launcher), 'Build the Launcher project before running this gate.');
  const sourcePointer = readJson(path.join(repository, 'Builds/latest.json'));
  const sourceBundle = process.env.METROIDVANIA_STUDIO_TEST_BUILD
    ? path.resolve(process.env.METROIDVANIA_STUDIO_TEST_BUILD)
    : path.join(repository, 'Builds', sourcePointer.folder);
  assert.ok(existsSync(path.join(sourceBundle, 'MetroidvaniaStudio/Launcher/MetroidvaniaStudio.Launcher.dll')),
    'Build the full platform bundle before running this gate.');
  const fixtureParent = path.join(repository, '.local', 'platform-launcher-tests');
  mkdirSync(fixtureParent, { recursive: true });
  // Spaces, Unicode, ampersand and a trailing separator exercise argv preservation.
  const fixture = mkdtempSync(path.join(fixtureParent, 'launch space 한글 & '));
  const bundleName = '0.1.0-20000101-000000-aaaaaaaa';
  const builds = path.join(fixture, 'Builds');
  const bundle = path.join(builds, bundleName);
  mkdirSync(builds, { recursive: true });
  cpSync(sourceBundle, bundle, { recursive: true, filter: source => !['.local', 'Logs'].includes(path.basename(source)) });
  const pointerPath = path.join(builds, 'latest.json');
  const validPointer = { ...sourcePointer, folder: bundleName };
  writeJson(pointerPath, validPointer);
  const workspace = path.join(fixture, 'workspace 한글 & sample');
  const otherWorkspace = path.join(fixture, 'other-workspace');
  mkdirSync(path.join(workspace, 'Maps'), { recursive: true });
  cpSync(path.join(repository, 'Samples/Maps/Sample.map.json'), path.join(workspace, 'Maps/Launcher.map.json'));
  const port = await availablePort();
  const sessionPath = path.join(fixture, 'MetroidvaniaStudio/.local', `server-${port}.json`);
  const owned = new Map();
  const argumentsFor = (action, extra = [], targetPort = port, project = workspace) => [
    action, '--studio-root', fixture, '--project', project + path.sep,
    '--port', String(targetPort), '--no-browser', ...extra,
  ];
  async function start(extra = [], targetPort = port, project = workspace) {
    let result, health;
    try { result = await invoke(argumentsFor('run', extra, targetPort, project)); }
    finally {
      health = await optionalHealth(targetPort);
      if (health?.projectPath && path.resolve(health.projectPath) === path.resolve(project)) owned.set(targetPort, health.instanceId);
    }
    assert.equal(result.code, 0, result.output);
    assert.ok(health?.instanceId, 'Run must not report success before health is ready.');
    return health;
  }
  async function expectFailure(action, extra = [], targetPort = port, project = workspace) {
    const result = await invoke(argumentsFor(action, extra, targetPort, project));
    assert.notEqual(result.code, 0, 'The unsafe command unexpectedly succeeded.\n' + result.output);
    return result;
  }
  async function assertUnchanged(expected) {
    const actual = await jsonRequest(port, '/api/health');
    assert.equal(actual.instanceId, expected.instanceId, 'The running instance changed.');
    assert.equal(actual.processId, expected.processId, 'The running process changed.');
  }
  try {
    await context.test('published bundle launches with exact quoted workspace and reuses its running instance', async () => {
      const first = await start();
      assert.equal(path.resolve(first.projectPath), path.resolve(workspace));
      assert.ok(Number.isInteger(first.processId) && first.processId > 0);
      assert.ok(first.launchToken, 'Launcher-owned processes carry a startup token.');
      const record = readJson(sessionPath);
      assert.equal(record.pid, first.processId);
      assert.equal(record.instanceId, first.instanceId);
      assert.equal(record.launchToken, first.launchToken);
      const second = await start();
      assert.equal(second.instanceId, first.instanceId);
      assert.equal(second.processId, first.processId);
    });

    assert.ok(existsSync(sessionPath), 'Startup did not establish a usable session record; remaining lifecycle checks cannot run.');

    await context.test('concurrent run commands reuse one verified process', async () => {
      const stop = await invoke(argumentsFor('stop'));
      assert.equal(stop.code, 0, stop.output);
      await waitUntil(async () => !(await optionalHealth(port)), 'The server did not stop before the concurrency fixture.');
      owned.delete(port);
      const results = await Promise.all([invoke(argumentsFor('run')), invoke(argumentsFor('run'))]);
      const health = await optionalHealth(port);
      if (health?.instanceId) owned.set(port, health.instanceId);
      for (const result of results) assert.equal(result.code, 0, result.output);
      assert.ok(health?.instanceId);
      const record = readJson(sessionPath);
      assert.equal(record.instanceId, health.instanceId);
      assert.equal(record.pid, health.processId);
      assert.equal(record.launchToken, health.launchToken);
    });

    await context.test('restart flushes and restores unsaved document changes', async () => {
      const before = await jsonRequest(port, '/api/state');
      const marker = randomUUID();
      const properties = [...(before.document.properties || []).filter(item => item.key !== 'launcher.validation'),
        { key: 'launcher.validation', value: marker }];
      await jsonRequest(port, '/api/command', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action: 'documentProperties', properties, clientId: 'launcher-validation',
          commandId: randomUUID(), expectedRevision: before.revision, expectedInstanceId: before.instanceId }),
      });
      const restarted = await start(['--restart']);
      assert.notEqual(restarted.instanceId, before.instanceId);
      const after = await jsonRequest(port, '/api/state');
      assert.equal(after.document.properties.find(item => item.key === 'launcher.validation')?.value, marker);
    });

    await context.test('mismatched workspace cannot reuse or stop the current instance', async () => {
      const current = await jsonRequest(port, '/api/health');
      await expectFailure('run', [], port, otherWorkspace);
      await expectFailure('stop', [], port, otherWorkspace);
      await assertUnchanged(current);
    });

    await context.test('invalid pointer and incomplete newer build keep the running instance intact', async () => {
      const current = await jsonRequest(port, '/api/health');
      try {
        writeFileSync(pointerPath, '{broken', 'utf8');
        await expectFailure('run');
        await assertUnchanged(current);
        writeJson(pointerPath, { ...validPointer, folder: '../escape' });
        await expectFailure('run');
        await assertUnchanged(current);
        const incompleteName = '0.1.0-20000101-000001-bbbbbbbb';
        const serverFolder = path.join(builds, incompleteName, 'MetroidvaniaStudio/Server');
        mkdirSync(serverFolder, { recursive: true });
        cpSync(path.join(bundle, 'MetroidvaniaStudio/Server/MetroidvaniaStudio.Server.dll'),
          path.join(serverFolder, 'MetroidvaniaStudio.Server.dll'));
        writeJson(pointerPath, { ...validPointer, folder: incompleteName });
        await expectFailure('run');
        await assertUnchanged(current);
      } finally { writeJson(pointerPath, validPointer); }
    });

    await context.test('stale or reused PID records never stop the verified server or another process', async () => {
      const current = await jsonRequest(port, '/api/health');
      const original = readFileSync(sessionPath);
      try {
        const record = readJson(sessionPath);
        writeJson(sessionPath, { ...record, pid: process.pid, processStartUtc: '2000-01-01T00:00:00.0000000Z' });
        await expectFailure('stop');
        await assertUnchanged(current);
        writeJson(sessionPath, { ...record, instanceId: randomUUID() });
        await expectFailure('stop');
        await assertUnchanged(current);
      } finally { writeFileSync(sessionPath, original); }
    });

    await context.test('a second port cannot open the same writable workspace', async () => {
      const current = await jsonRequest(port, '/api/health');
      const secondPort = await availablePort();
      const result = await invoke(argumentsFor('run', [], secondPort));
      const health = await optionalHealth(secondPort);
      if (health?.projectPath === current.projectPath) owned.set(secondPort, health.instanceId);
      assert.notEqual(result.code, 0, result.output);
      assert.equal(health, null, 'A second writer must not start for the same workspace.');
      await assertUnchanged(current);
    });

    await context.test('a foreign HTTP listener is neither adopted nor shut down', async () => {
      let shutdownRequests = 0;
      const listener = http.createServer((request, response) => {
        if (request.url === '/api/shutdown') shutdownRequests++;
        response.setHeader('Content-Type', 'application/json');
        response.end(JSON.stringify({ service: 'launcher-validation-fixture' }));
      });
      await new Promise((resolve, reject) => {
        listener.once('error', reject); listener.listen(0, '127.0.0.1', resolve);
      });
      const foreignPort = listener.address().port;
      try {
        await expectFailure('run', [], foreignPort, otherWorkspace);
        await expectFailure('stop', [], foreignPort, otherWorkspace);
        assert.equal(shutdownRequests, 0);
        assert.equal((await jsonRequest(foreignPort, '/api/health')).service, 'launcher-validation-fixture');
      } finally {
        listener.closeAllConnections();
        await new Promise(resolve => listener.close(resolve));
      }
    });

    await context.test('failed recovery write refuses shutdown and leaves the session editable', async () => {
      const current = await jsonRequest(port, '/api/health');
      const recoveryPath = path.join(workspace, 'Maps/.Recovery/Workspace.map.json');
      const backupPath = recoveryPath + '.held';
      await waitUntil(() => existsSync(recoveryPath), 'The dirty document has no recovery snapshot.');
      renameSync(recoveryPath, backupPath);
      mkdirSync(recoveryPath);
      try {
        await expectFailure('stop');
        await assertUnchanged(current);
        assert.ok(existsSync(sessionPath), 'Failed shutdown must retain its ownership record for retry.');
        const state = await jsonRequest(port, '/api/state');
        await jsonRequest(port, '/api/command', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ action: 'options', brushSize: 2, clientId: 'launcher-validation',
            commandId: randomUUID(), expectedRevision: state.revision, expectedInstanceId: state.instanceId }),
        });
      } finally {
        // Only remove the exact empty directory created above; retain every map file.
        rmdirSync(recoveryPath);
        renameSync(backupPath, recoveryPath);
      }
    });
    await context.test('stop completes gracefully and removes only the matching session record', async () => {
      const result = await invoke(argumentsFor('stop'));
      assert.equal(result.code, 0, result.output);
      await waitUntil(async () => !(await optionalHealth(port)), 'The stopped server remained reachable.');
      assert.equal(existsSync(sessionPath), false);
      owned.delete(port);
    });
  } finally {
    writeJson(pointerPath, validPointer);
    // An assertion must not leave a test server running. Only the exact captured
    // instance in this disposable workspace can receive a graceful stop request.
    const cleanupErrors = [];
    for (const [targetPort, instanceId] of owned) {
      try {
        const health = await optionalHealth(targetPort);
        if (!health || health.instanceId !== instanceId) continue;
        assert.equal(path.resolve(health.projectPath), path.resolve(workspace));
        await jsonRequest(targetPort, '/api/shutdown', {
          method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Metroidvania-Studio-Instance': instanceId },
          body: '{}',
        });
        await waitUntil(async () => !(await optionalHealth(targetPort)), 'A test server is still saving.');
      } catch (error) { cleanupErrors.push(error); }
    }
    if (cleanupErrors.length) throw new AggregateError(cleanupErrors, 'Graceful launcher-test cleanup failed.');
  }
});