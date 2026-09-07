#!/usr/bin/env node
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, realpathSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export function findDotnet(env = process.env, platform = process.platform, probe = spawnSync) {
  const explicit = env.METROIDVANIA_STUDIO_DOTNET;
  const candidates = explicit ? [explicit] : [
    'dotnet',
    ...(env.DOTNET_ROOT ? [path.join(env.DOTNET_ROOT, platform === 'win32' ? 'dotnet.exe' : 'dotnet')] : []),
    ...(platform === 'win32' && env.ProgramFiles ? [path.join(env.ProgramFiles, 'dotnet', 'dotnet.exe')] : []),
  ];
  for (const candidate of candidates) {
    const result = probe(candidate, ['--list-runtimes'], { env, encoding: 'utf8', windowsHide: true, timeout: 10000, maxBuffer: 1024 * 1024 });
    if (!result.error && result.status === 0 && /^Microsoft\.NETCore\.App 10\.\d+\.\d+\s/m.test(result.stdout ?? '')) return candidate;
  }
  throw new Error(explicit
    ? 'METROIDVANIA_STUDIO_DOTNET must point to an installed .NET 10 runtime host.'
    : 'Install the .NET 10 runtime, or set METROIDVANIA_STUDIO_DOTNET to its dotnet executable. No SDK is required.');
}

export async function run(args = process.argv.slice(2)) {
  if (Number(process.versions.node.split('.')[0]) < 24) throw new Error('Node.js 24 or later is required.');
  const dll = fileURLToPath(new URL('../app/MetroidvaniaStudio.Cli.dll', import.meta.url));
  if (!existsSync(dll)) throw new Error('The packaged CLI is missing. Use a built package; source checkouts use the local pack script.');
  const env = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1' };
  const executable = findDotnet(env);
  env.DOTNET_HOST_PATH = executable;
  return await new Promise((resolve, reject) => {
    const child = spawn(executable, [dll, ...args], { env, windowsHide: true, stdio: 'inherit', shell: false });
    const handlers = new Map();
    for (const signal of ['SIGINT', 'SIGTERM']) {
      const handler = () => { if (child.exitCode === null && child.signalCode === null) child.kill(signal); };
      handlers.set(signal, handler); process.on(signal, handler);
    }
    const cleanup = () => { for (const [signal, handler] of handlers) process.off(signal, handler); };
    child.once('error', error => { cleanup(); reject(new Error('The .NET CLI could not start: ' + error.message)); });
    child.once('exit', (code, signal) => { cleanup(); resolve(code ?? (signal === 'SIGINT' ? 130 : 143)); });
  });
}

if (process.argv[1] && realpathSync(path.resolve(process.argv[1])) === realpathSync(fileURLToPath(import.meta.url))) {
  run().then(code => { process.exitCode = code; }).catch(error => {
    process.stderr.write('Metroidvania Studio: ' + error.message + '\n'); process.exitCode = 1;
  });
}
