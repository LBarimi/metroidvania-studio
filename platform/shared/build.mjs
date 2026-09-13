import { closeSync, copyFileSync, cpSync, existsSync, fsyncSync, mkdirSync, openSync, readFileSync, realpathSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { currentBuild, sourceState } from './build-state.mjs';

const defaultRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const versionPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const inside = (root, target) => { const relative = path.relative(root, target); return relative !== '' && !relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative); };
export function runCommand(command, args, { cwd, capture = false } = {}) {
  return execFileSync(command, args, { cwd, windowsHide: true, encoding: 'utf8', stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit',
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
}
export function writeAtomic(target, value) {
  mkdirSync(path.dirname(target), { recursive: true });
  const temporary = path.join(path.dirname(target), path.basename(target) + '-' + randomBytes(8).toString('hex') + '.tmp');
  let descriptor;
  try {
    descriptor = openSync(temporary, 'wx');
    writeFileSync(descriptor, JSON.stringify(value, null, 2) + '\n');
    fsyncSync(descriptor); closeSync(descriptor); descriptor = undefined;
    renameSync(temporary, target);
  } finally {
    if (descriptor !== undefined) closeSync(descriptor);
    if (existsSync(temporary)) unlinkSync(temporary);
  }
}
function readBuildLock(lockPath) {
  let content;
  try { content = readFileSync(lockPath, 'utf8'); }
  catch (error) { if (error.code === 'ENOENT') return null; throw error; }
  let owner;
  try { owner = JSON.parse(content); }
  catch { throw new Error('A build lock is being created or is unreadable. Retry after the current build finishes.'); }
  if (!Number.isSafeInteger(owner?.pid) || owner.pid <= 0 || owner.pid > 2147483647 || typeof owner.token !== 'string' || !owner.token)
    throw new Error('Invalid build lock. Check that no build is running before removing .local/build-v2.lock.');
  return { content, owner };
}
function ownerHasExited(pid) {
  try { process.kill(pid, 0); return false; }
  catch (error) { return error.code === 'ESRCH'; }
}
function recoverBuildLock(localRoot, lockPath, recorded) {
  // Only one contender may remove this exact stale record. Rechecking without
  // an exclusive claim could delete a new owner's lock during simultaneous starts.
  const digest = createHash('sha256').update(recorded.content).digest('hex');
  const claimPath = path.join(localRoot, 'build-recovery-' + digest + '.lock');
  let descriptor;
  try { descriptor = openSync(claimPath, 'wx'); }
  catch (error) {
    if (error.code !== 'EEXIST') throw error;
    throw new Error('Build lock recovery is in progress or was interrupted. Retry; if it persists, confirm no build is running before removing .local/build-recovery-*.lock.');
  }
  try {
    const current = readBuildLock(lockPath);
    if (!current || current.content !== recorded.content) return;
    // Permission errors and reused live PIDs are deliberately treated as busy.
    if (!ownerHasExited(current.owner.pid)) throw new Error('Another build is running. Wait for it to finish.');
    unlinkSync(lockPath);
    console.log('Recovered the lock from an interrupted build.');
  } finally {
    closeSync(descriptor);
    unlinkSync(claimPath);
  }
}
export function acquireBuildLock(localRoot) {
  mkdirSync(localRoot, { recursive: true });
  const lockPath = path.join(localRoot, 'build-v2.lock');
  const token = randomBytes(12).toString('hex');
  for (let attempt = 0; attempt < 4; attempt++) {
    let descriptor;
    try { descriptor = openSync(lockPath, 'wx'); }
    catch (error) {
      if (error.code !== 'EEXIST') throw error;
      const recorded = readBuildLock(lockPath);
      if (!recorded) continue;
      if (!ownerHasExited(recorded.owner.pid)) throw new Error('Another build is running. Wait for it to finish.');
      recoverBuildLock(localRoot, lockPath, recorded);
      continue;
    }
    try { writeFileSync(descriptor, JSON.stringify({ pid: process.pid, token })); }
    finally { closeSync(descriptor); }
    return () => {
      try { if (JSON.parse(readFileSync(lockPath, 'utf8')).token === token) unlinkSync(lockPath); }
      catch (error) { if (error.code !== 'ENOENT') throw error; }
    };
  }
  throw new Error('Could not acquire the build lock. Retry after the current build finishes.');
}
export function checkedInputs(studioRoot) {
  const inputs = JSON.parse(readFileSync(path.join(studioRoot, 'tools/build/package-inputs.json'), 'utf8'));
  if (!Array.isArray(inputs) || !inputs.length) throw new Error('Build inputs must be a nonempty array.');
  return inputs.map(relative => {
    if (typeof relative !== 'string' || !relative || path.isAbsolute(relative) || /^[A-Za-z]:/.test(relative) || relative.includes('\\')) throw new Error('Invalid build input.');
    const source = path.resolve(studioRoot, relative);
    if (!inside(studioRoot, source) || relative.split('/').some(part => part === '..' || part === '.' || !part)) throw new Error('Build inputs must remain inside the studio checkout.');
    if (relative !== 'metroidvania-studio/dist' && (!existsSync(source) || !inside(realpathSync(studioRoot), realpathSync(source)))) throw new Error(`Missing or external build input: ${relative}`);
    return relative;
  });
}
export function buildStudio({ studioRoot = defaultRoot, dotnet = process.env.METROIDVANIA_STUDIO_DOTNET || 'dotnet', runner = runCommand, checkOnly = false, ensureCurrent = true, rebuild = false } = {}) {
  studioRoot = path.resolve(studioRoot);
  if (Number(process.versions.node.split('.')[0]) < 24) throw new Error('Node.js 24 or later is required for building.');
  const serverProject = path.join(studioRoot, 'metroidvania-studio/server/MetroidvaniaStudio.Server.csproj');
  const launcherProject = path.join(studioRoot, 'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.csproj');
  const cliProject = path.join(studioRoot, 'metroidvania-studio/cli/MetroidvaniaStudio.Cli.csproj');
  if (!existsSync(serverProject) || !existsSync(launcherProject) || !existsSync(cliProject)) throw new Error('Building requires a source checkout. Use the run script for a prebuilt application.');
  if (ensureCurrent && !rebuild && !checkOnly) {
    const status = currentBuild(studioRoot);
    if (status.current) { console.log('Local build is up to date.'); return status.output; }
    console.log(status.reason + ' Building before launch...');
  }
  const sdk = runner(dotnet, ['--list-sdks'], { cwd: studioRoot, capture: true });
  if (!/^10\./m.test(sdk || '')) throw new Error('.NET SDK 10 is required for building.');
  if (!/^git version /m.test(runner('git', ['--version'], { cwd: studioRoot, capture: true }) || '')) throw new Error('Git is required for source validation.');
  if (checkOnly) { console.log('Build tools are ready: Node.js 24+, .NET SDK 10 and Git.'); return null; }
  const releaseLock = acquireBuildLock(path.join(studioRoot, '.local'));
  const started = performance.now();
  const previousDotnet = process.env.METROIDVANIA_STUDIO_DOTNET;
  process.env.METROIDVANIA_STUDIO_DOTNET = dotnet;
  try {
    const version = JSON.parse(readFileSync(path.join(studioRoot, 'version.json'), 'utf8')).version;
    if (!versionPattern.test(version)) throw new Error('Invalid build version.');
    const inputs = checkedInputs(studioRoot);
    const source = sourceState(studioRoot);
    const stamp = new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);
    const folder = `${version}-${stamp}-${randomBytes(4).toString('hex')}`;
    const buildsRoot = path.join(studioRoot, 'builds'), output = path.join(buildsRoot, folder);
    mkdirSync(buildsRoot, { recursive: true }); mkdirSync(output);
    runner(process.execPath, [path.join(studioRoot, 'tools/scripting/build-runtime.mjs')], { cwd: studioRoot });
    console.log('Building the web editor...');
    runner(process.execPath, [path.join(studioRoot, 'metroidvania-studio/build-web.mjs'), path.join(output, 'metroidvania-studio/dist')], { cwd: studioRoot });
    for (const [name, project] of [['Server', serverProject], ['Launcher', launcherProject], ['Cli', cliProject]]) {
      console.log(`Building ${name.toLowerCase()}...`);
      const properties = ['--configuration', 'Release', '--self-contained', 'false', '-p:UseAppHost=false',
        '-p:UseSharedCompilation=false', '-p:DebugType=None', '-p:DebugSymbols=false', `-p:PathMap=${studioRoot}=/_/src`, `-p:Version=${version}`];
      // Publish already performs an incremental build and restore. Keep shared
      // project outputs for subsequent targets instead of compiling them again.
      if (rebuild) runner(dotnet, ['build', project, '--no-incremental', ...properties, '--nologo'], { cwd: studioRoot });
      runner(dotnet, ['publish', project, ...(rebuild ? ['--no-build', '--no-restore'] : []), ...properties, '--output', path.join(output, `metroidvania-studio/${name.toLowerCase()}`), '--nologo'], { cwd: studioRoot });
    }
    for (const relative of inputs) {
      if (relative === 'metroidvania-studio/dist') continue;
      const destination = path.join(output, relative);
      mkdirSync(path.dirname(destination), { recursive: true });
      cpSync(path.join(studioRoot, relative), destination, { recursive: true, dereference: true,
        filter: candidate => { if (!inside(realpathSync(studioRoot), realpathSync(candidate))) throw new Error('Build input links outside the checkout.'); return true; } });
    }
    for (const notice of ['LICENSE', 'NOTICE']) if (existsSync(path.join(studioRoot, notice))) copyFileSync(path.join(studioRoot, notice), path.join(output, notice));
    runner(process.execPath, [path.join(studioRoot, 'integrations/build-packages.mjs'), '--output', path.join(output, 'engine-packages')], { cwd: studioRoot });
    for (const engine of ['unity', 'godot', 'ue4', 'ue5', 'sdl']) {
      const packageName = engine === 'unity' ? 'metroidvania-studio.unitypackage' : 'metroidvania-studio.zip';
      if (!existsSync(path.join(output, 'engine-packages', engine, packageName))) throw new Error(`Incomplete engine package: ${engine}`);
    }
    for (const needed of ['metroidvania-studio/dist/index.html', 'metroidvania-studio/server/MetroidvaniaStudio.Server.dll',
      'metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll', 'metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll']) if (!existsSync(path.join(output, needed))) throw new Error(`Incomplete build: ${needed}`);
    if (sourceState(studioRoot).sourceHash !== source.sourceHash)
      throw new Error('Source files changed during the build. Run again after saving your changes; the previous build is preserved.');
    writeAtomic(path.join(output, 'source-state.json'), source);
    // Cache is private, ignored state. Only absolute discovered executables are persisted.
    let dotnetPath = path.isAbsolute(dotnet) ? dotnet : '';
    if (!dotnetPath) {
      const search = process.platform === 'win32' ? 'where.exe' : '/bin/sh';
      const args = process.platform === 'win32' ? [dotnet] : ['-c', 'command -v "$1"', 'find-runtime', dotnet];
      try { dotnetPath = (runner(search, args, { cwd: studioRoot, capture: true }) || '').trim().split(/\r?\n/)[0]; } catch { }
    }
    const toolchain = { 'node.exe': process.execPath, node: process.execPath };
    if (dotnetPath && path.isAbsolute(dotnetPath)) { toolchain['dotnet.exe'] = dotnetPath; toolchain.dotnet = dotnetPath; }
    writeAtomic(path.join(studioRoot, '.local/toolchain.json'), toolchain);
    // A failed build never replaces the last successful output or its pointer.
    writeAtomic(path.join(buildsRoot, 'latest.json'), { formatVersion: 1, folder, version });
    console.log(`Build ready (${((performance.now() - started) / 1000).toFixed(1)}s): ${output}`);
    return output;
  } finally {
    if (previousDotnet === undefined) delete process.env.METROIDVANIA_STUDIO_DOTNET; else process.env.METROIDVANIA_STUDIO_DOTNET = previousDotnet;
    releaseLock();
  }
}
export function main(args = process.argv.slice(2)) {
  const options = {};
  for (let index = 0; index < args.length; index++) {
    if (args[index] === '--check') options.checkOnly = true;
    else if (args[index] === '--ensure') options.ensureCurrent = true;
    else if (args[index] === '--rebuild') options.rebuild = true;
    else if (['--studio-root', '--dotnet'].includes(args[index]) && args[index + 1]) options[args[index++] === '--studio-root' ? 'studioRoot' : 'dotnet'] = args[index];
    else throw new Error('Usage: build.mjs [--studio-root <folder>] [--dotnet <executable>] [--check] [--ensure] [--rebuild]');
  }
  return buildStudio(options);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
