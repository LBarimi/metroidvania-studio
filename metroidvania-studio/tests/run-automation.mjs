import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { access, copyFile, mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const args = process.argv.slice(2);
if (args.some(arg => arg !== '--performance')) throw new Error('Usage: node metroidvania-studio/tests/run-automation.mjs [--performance]');
const dotnet = process.env.METROIDVANIA_STUDIO_DOTNET || process.env.DOTNET_HOST_PATH || 'dotnet';
const serverDll = path.join(root, 'metroidvania-studio/server/bin/Release/net10.0/MetroidvaniaStudio.Server.dll');
const cliDll = path.join(root, 'metroidvania-studio/cli/bin/Release/net10.0/MetroidvaniaStudio.Cli.dll');
await Promise.all([access(serverDll), access(cliDll)]).catch(() => { throw new Error('Build the Release server and CLI before running browser automation. See docs/cli/quick-start.md.'); });
const scratchRoot = path.join(root, '.local/automation-validation');
await mkdir(scratchRoot, { recursive: true });
const scratch = await mkdtemp(path.join(scratchRoot, 'run-'));
const workspace = path.join(scratch, 'workspace'), dist = path.join(scratch, 'dist');
await mkdir(path.join(workspace, 'Maps'), { recursive: true });
await copyFile(path.join(root, 'samples/maps/Sample.map.json'), path.join(workspace, 'Maps/Sample.map.json'));
const env = { ...process.env, METROIDVANIA_STUDIO_DOTNET: dotnet, DOTNET_HOST_PATH: dotnet, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' };
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
let server, serverExit, health, url, serverLog = '', completed = false;
async function run(script, scriptArgs = []) {
  const child = spawn(process.execPath, [path.join(root, script), ...scriptArgs], { cwd: root, env, windowsHide: true, stdio: 'inherit' });
  const timer = setTimeout(() => child.kill(), 180000);
  try {
    const code = await new Promise((resolve, reject) => { child.once('error', reject); child.once('close', resolve); });
    if (code !== 0) throw new Error(`Background validation failed: ${script}`);
  } finally { clearTimeout(timer); }
}
try {
  await run('metroidvania-studio/build-web.mjs', [dist]);
  const probe = createServer();
  await new Promise((resolve, reject) => { probe.once('error', reject); probe.listen(0, '127.0.0.1', resolve); });
  const port = probe.address().port;
  await new Promise(resolve => probe.close(resolve));
  url = `http://127.0.0.1:${port}`;
  server = spawn(dotnet, [serverDll, '--studio-root', root, '--project', workspace, '--web-root', dist, '--port', String(port)], {
    cwd: root, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  let startError;
  server.on('error', error => { startError = error; });
  serverExit = new Promise(resolve => server.once('close', resolve));
  const log = chunk => { serverLog = (serverLog + chunk.toString()).slice(-65536); };
  server.stdout.on('data', log); server.stderr.on('data', log);
  for (let attempt = 0; attempt < 150; attempt++) {
    if (startError) throw startError;
    if (server.exitCode !== null) throw new Error(`The isolated server exited before becoming ready.\n${serverLog}`);
    try {
      const response = await fetch(url + '/api/health', { signal: AbortSignal.timeout(1000) });
      const candidate = await response.json();
      if (candidate.processId !== server.pid || path.resolve(candidate.projectPath) !== workspace)
        throw new Error('The selected test port belongs to another process.');
      health = candidate; break;
    } catch (error) {
      if (error.message === 'The selected test port belongs to another process.') throw error;
      await pause(100);
    }
  }
  if (!health) throw new Error(`The isolated server did not become ready.\n${serverLog}`);
  env.METROIDVANIA_STUDIO_BASE_URL = url;
  env.METROIDVANIA_STUDIO_TEST_ISOLATED = '1';
  env.METROIDVANIA_STUDIO_TEST_PROJECT_ROOT = workspace;
  env.METROIDVANIA_STUDIO_TEST_WEB_ROOT = dist;
  await run('metroidvania-studio/tests/browser-palette-workflow.mjs');
  await run('metroidvania-studio/tests/browser-camera-settings.mjs');
  await run('metroidvania-studio/tests/browser-documentation.mjs');
  await run('metroidvania-studio/tests/browser-sample-world.mjs');
  await run('metroidvania-studio/tests/browser-palettes.mjs');
  await run('metroidvania-studio/tests/browser-palette-groups.mjs');
  await run('metroidvania-studio/tests/browser-tilesets.mjs');
  await run('metroidvania-studio/tests/browser-texture-reload.mjs');
  await run('metroidvania-studio/tests/browser-native-files.mjs');
  await run('metroidvania-studio/tests/browser-storage-workflow.mjs');
  await run('metroidvania-studio/tests/browser-authoring.mjs');
  await run('metroidvania-studio/tests/browser-automation.mjs');
  if (args.includes('--performance')) {
    await run('metroidvania-studio/tests/browser-brush-latency.mjs');
    await run('metroidvania-studio/tests/browser-tile-scaling.mjs');
  }
  completed = true;
  console.log('Isolated background browser automation passed.');
} finally {
  if (server && server.exitCode === null && !server.killed) {
    if (health) {
      try {
        const response = await fetch(url + '/api/shutdown', { method: 'POST', headers: {
          'content-type': 'application/json', 'X-Metroidvania-Studio-Instance': health.instanceId
        }, body: '{}', signal: AbortSignal.timeout(45000) });
        if (!response.ok) throw new Error('The isolated server could not finish saving.');
        await Promise.race([serverExit, pause(15000)]);
      } catch (error) { console.error(error.message); completed = false; }
    }
    if (server.exitCode === null) { server.kill(); await serverExit; }
  }
  await writeFile(path.join(scratch, 'server.log'), serverLog, 'utf8');
  if (completed) {
    const relative = path.relative(scratchRoot, scratch);
    if (!relative.startsWith('run-') || path.dirname(scratch) !== scratchRoot || relative.includes(path.sep))
      throw new Error('Refusing to remove an unexpected validation directory.');
    await rm(scratch, { recursive: true, force: true });
  } else {
    console.error(`Validation output was preserved in ${scratch}`);
    process.exitCode = 1;
  }
}
