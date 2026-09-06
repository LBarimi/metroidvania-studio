import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const stableVersion = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

export const bundleInputs = ['MetroidvaniaStudio/dist', 'MetroidvaniaStudio/Start-MetroidvaniaStudio.ps1', 'MetroidvaniaStudio/Start-MetroidvaniaStudio.bat',
    'MetroidvaniaStudio/Stop-MetroidvaniaStudio.ps1', 'MetroidvaniaStudio/Localization',
    'MetroidvaniaStudio/Contracts/map-format-v2.schema.json', 'MetroidvaniaStudio/Contracts/FORMAT.md', 'Samples', 'README.md', 'CHANGELOG.md', 'version.json'];

export function validateReleaseState(state) {
  assert.equal(state.branch, 'main', 'Releases are allowed only from main.');
  assert.ok(/^[0-9a-f]{40}$/.test(state.head), 'A committed main revision is required.');
  assert.equal(state.status, '', 'Commit or remove pending files before preparing a release.');
  assert.ok(stableVersion.test(state.version), 'Use a numeric major.minor.patch version.');
  assert.equal(state.tagExists, false, 'The release tag already exists; increment the version.');
  if (state.remoteMain) assert.equal(state.head, state.remoteMain, 'main must match the fetched origin/main revision.');
  assert.ok(state.changeLog.includes(`## ${state.version}\n`), 'Add the release version to CHANGELOG.md.');
  return `v${state.version}`;
}
export function validatePublishContext(state, env) {
  validateReleaseState(state);
  assert.equal(env.GITHUB_ACTIONS, 'true', 'Publishing is available only through the manual release workflow.');
  assert.equal(env.GITHUB_EVENT_NAME, 'workflow_dispatch', 'Publishing requires a manual workflow run.');
  assert.equal(env.GITHUB_REF, 'refs/heads/main', 'Feature branches cannot publish releases.');
  assert.equal(env.RELEASE_PUBLISH, 'true', 'The publish input must be explicitly enabled.');
  assert.equal(env.GITHUB_SHA, state.head, 'Publish the exact revision selected for this workflow run.');
  assert.ok(state.remoteMain, 'Fetch origin/main before publishing.');
}
function run(command, args, options = {}) {
  return execFileSync(command, args, { cwd: root, windowsHide: true, stdio: 'inherit', ...options });
}
function git(...args) { return run('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim(); }
function optionalRef(ref) {
  const result = spawnSync('git', ['rev-parse', '--verify', '--quiet', ref], { cwd: root, encoding: 'utf8', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status === 1) return null;
  if (result.status !== 0) throw new Error(`Could not inspect Git reference ${ref}.`);
  return result.stdout.trim();
}
export function readReleaseState() {
  const version = JSON.parse(readFileSync(path.join(root, 'version.json'), 'utf8')).version;
  return { branch: git('branch', '--show-current'), head: git('rev-parse', 'HEAD'),
    status: git('status', '--porcelain', '--untracked-files=all'), version,
    tagExists: stableVersion.test(version) && optionalRef(`refs/tags/v${version}`) !== null,
    remoteMain: optionalRef('refs/remotes/origin/main'),
    changeLog: readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8').replaceAll('\r\n', '\n') };
}
function copy(relative, bundle) {
  cpSync(path.join(root, relative), path.join(bundle, relative), { recursive: true,
    filter: source => !['bin', 'obj', '.local', 'node_modules'].includes(path.basename(source)) });
}
function packageFiles(folder, relative = '') {
  return readdirSync(path.join(folder, relative), { withFileTypes: true }).flatMap(entry => {
    const next = path.join(relative, entry.name);
    return entry.isDirectory() ? packageFiles(folder, next) : [next];
  });
}
export function packRelease(state) {
  validateReleaseState(state);
  assert.equal(process.platform, 'win32', 'The current release archive targets Windows.');
  const node = process.execPath, dotnet = process.env.METROIDVANIA_STUDIO_DOTNET || 'dotnet';
  run(node, ['Tools/Repository/check-text.mjs']);
  run(node, ['--test', 'Tools/Repository/check-text.test.mjs', 'Tools/Repository/check-boundaries.test.mjs', 'Tools/Release/release.test.mjs']);
  run(node, ['MetroidvaniaStudio/build-web.mjs', '--check-contracts']);
  for (const suite of ['Core.Tests', 'Server.Tests']) {
    run(dotnet, ['run', '--project', `MetroidvaniaStudio/${suite}/MetroidvaniaStudio.${suite}.csproj`, '--configuration', 'Release', '-p:UseSharedCompilation=false']);
  }
  run(node, ['MetroidvaniaStudio/build-web.mjs']);
  const output = path.join(root, '.local/release');
  mkdirSync(output, { recursive: true });
  const staging = mkdtempSync(path.join(output, 'staging-'));
  const bundle = path.join(staging, 'MetroidvaniaStudio');
  mkdirSync(path.join(bundle, 'MetroidvaniaStudio/Server'), { recursive: true });
  run(dotnet, ['publish', 'MetroidvaniaStudio/Server/MetroidvaniaStudio.Server.csproj', '--configuration', 'Release',
    '--self-contained', 'false', '-p:UseAppHost=false', '-p:UseSharedCompilation=false', '-p:DebugType=None',
    '--output', path.join(bundle, 'MetroidvaniaStudio/Server')]);
  for (const relative of bundleInputs) copy(relative, bundle);
  if (existsSync(path.join(root, 'LICENSE'))) copy('LICENSE', bundle);
  if (existsSync(path.join(root, 'NOTICE'))) copy('NOTICE', bundle);
  const manifest = packageFiles(bundle).sort().map(file => ({ path: file.replaceAll('\\', '/'),
    sha256: createHash('sha256').update(readFileSync(path.join(bundle, file))).digest('hex') }));
  writeFileSync(path.join(bundle, 'release-manifest.json'), JSON.stringify({ version: state.version, commit: state.head, files: manifest }, null, 2) + '\n');
  const archive = path.join(output, `metroidvania-studio-${state.version}-win-x64.zip`);
  const archiveScript = path.join(staging, 'archive.ps1');
  writeFileSync(archiveScript, 'param([string]$Source, [string]$Destination)\n$ErrorActionPreference = "Stop"\nCompress-Archive -LiteralPath $Source -DestinationPath $Destination -Force\n');
  run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', archiveScript, bundle, archive]);
  // Builds must not silently change tracked generated contracts or sources.
  const after = readReleaseState();
  validateReleaseState(after);
  assert.equal(after.head, state.head, 'The release revision changed during validation.');
  console.log(`Release archive: ${archive}`);
  return archive;
}
export function main(args = process.argv.slice(2)) {
  assert.ok(args.length <= 1 && (!args.length || ['--dry-run', '--pack', '--publish'].includes(args[0])),
    'Usage: node Tools/Release/release.mjs [--dry-run|--pack|--publish]');
  const state = readReleaseState();
  const tag = validateReleaseState(state);
  if (!args.length || args[0] === '--dry-run') { console.log(`Ready to validate and package ${tag} from main. No files or tags changed.`); return; }
  if (args[0] === '--publish') validatePublishContext(state, process.env);
  const archive = packRelease(state);
  if (args[0] !== '--publish') return;
  // Refresh the branch immediately before publication; never force-update a tag.
  run('git', ['fetch', 'origin', 'main', '--no-tags']);
  const finalState = readReleaseState();
  validatePublishContext(finalState, process.env);
  assert.equal(finalState.head, state.head);
  run('git', ['tag', tag, state.head]);
  run('git', ['push', 'origin', `refs/tags/${tag}`]);
  run('gh', ['release', 'create', tag, archive, '--verify-tag', '--title', `MetroidvaniaStudio ${state.version}`,
    '--notes-file', 'CHANGELOG.md', ...(state.version.startsWith('0.') ? ['--prerelease'] : [])]);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
