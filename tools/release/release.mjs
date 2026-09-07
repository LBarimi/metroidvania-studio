import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildArchives, readZip, releaseNames } from './archives.mjs';
import { inspectBoundaries } from '../repository/check-boundaries.mjs';
import { auditWindowsMetadata } from './vendor-metadata.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const stableVersion = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
export function validateReleaseState(state, replaceTag = '') {
  assert.equal(state.branch, 'main', 'Releases are allowed only from main.');
  assert.ok(/^[0-9a-f]{40}$/.test(state.head), 'A committed main revision is required.');
  assert.equal(state.status, '', 'Commit or remove pending files before preparing a release.');
  assert.ok(stableVersion.test(state.version), 'Use a numeric major.minor.patch version.');
  if (replaceTag) {
    assert.match(replaceTag, /^[0-9a-f]{40}$/, 'Replacing a release requires its exact previous tag object.');
    assert.equal(state.tagExists, true, 'Only an existing tag can be replaced.');
    assert.equal(state.tagObject, replaceTag, 'The previous release tag changed; inspect it before retrying.');
  } else assert.equal(state.tagExists, false, 'The release tag already exists; increment the version or explicitly replace its exact tag object.');
  if (state.remoteMain) assert.equal(state.head, state.remoteMain, 'main must match the fetched origin/main revision.');
  assert.ok(state.changeLog.includes(`## ${state.version}\n`), 'Add the release version to CHANGELOG.md.');
  return `v${state.version}`;
}
export function validatePublishContext(state, confirmation, report, replaceTag = '') {
  const tag = validateReleaseState(state, replaceTag);
  assert.equal(report.replacesTag || '', replaceTag, 'Prepare archives for this exact release replacement.');
  assert.equal(confirmation, tag, 'Publishing requires --confirm followed by the exact version tag.');
  assert.ok(state.remoteMain, 'Fetch origin/main before publishing.');
  assert.equal(report.commit, state.head, 'Prepare archives from this exact main revision.');
  assert.equal(report.version, state.version);
  assert.deepEqual(report.archives.map(file => file.name).sort(), [...releaseNames].sort(), 'All four packages are required.');
  assert.deepEqual(report.blockingIssues, [], 'Resolve release inspection issues before publishing.');
}
function run(command, args, options = {}) {
  return execFileSync(command, args, { cwd: root, windowsHide: true, stdio: 'inherit',
    env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' }, ...options });
}
function git(...args) { return run('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim(); }
function optionalRef(ref) {
  const result = spawnSync('git', ['rev-parse', '--verify', '--quiet', ref], { cwd: root, encoding: 'utf8', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status === 1) return null;
  if (result.status !== 0) throw new Error('Could not inspect Git reference.');
  return result.stdout.trim();
}
export function readReleaseState() {
  const version = JSON.parse(readFileSync(path.join(root, 'version.json'), 'utf8')).version;
  const tagObject = stableVersion.test(version) ? optionalRef(`refs/tags/v${version}`) : null;
  return { branch: git('branch', '--show-current'), head: git('rev-parse', 'HEAD'), status: git('status', '--porcelain', '--untracked-files=all'), version,
    tagExists: tagObject !== null, tagObject,
    remoteMain: optionalRef('refs/remotes/origin/main'), changeLog: readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8').replaceAll('\r\n', '\n') };
}
function validateSource() {
  const node = process.execPath, dotnet = process.env.METROIDVANIA_STUDIO_DOTNET || 'dotnet';
  run(node, ['tools/scripting/build-runtime.mjs']);
  run(node, ['tools/repository/check-text.mjs']);
  run(node, ['--test', 'tools/repository/check-text.test.mjs', 'tools/repository/check-boundaries.test.mjs', 'tools/release/release.test.mjs',
    'tools/release/archives.test.mjs', 'tools/docs/build.test.mjs', 'tools/release/vendor-metadata.test.mjs', 'platform/shared/build.test.mjs', 'integrations/packages.test.mjs', 'integrations/unity/package.test.mjs', 'integrations/sdl/tests/boundary.test.mjs']);
  run(node, ['metroidvania-studio/build-web.mjs', '--check-contracts']);
  for (const suite of ['Core.Tests', 'Server.Tests']) run(dotnet, ['run', '--project', `metroidvania-studio/${suite.toLowerCase().replace('.', '-')}/MetroidvaniaStudio.${suite}.csproj`, '--configuration', 'Release', '-p:UseSharedCompilation=false']);
  for (const [folder, name] of [['automation-tests', 'Automation.Tests'], ['scripting-tests', 'Scripting.Tests'], ['server-automation-tests', 'Server.Automation.Tests']])
    run(dotnet, ['run', '--project', `metroidvania-studio/${folder}/MetroidvaniaStudio.${name}.csproj`, '--configuration', 'Release', '-p:UseSharedCompilation=false']);
  run(dotnet, ['build', 'metroidvania-studio/cli/MetroidvaniaStudio.Cli.csproj', '--configuration', 'Release', '-p:UseSharedCompilation=false']);
  run(node, ['--test', 'tools/release/cli.test.mjs', 'metroidvania-studio/cli-tests/cli.test.mjs', 'metroidvania-studio/cli-tests/live-server.test.mjs']);
}
export async function prepareRelease(state, windowsPackage, { candidate = false, replaceTag = '' } = {}) {
  if (!candidate) validateReleaseState(state, replaceTag);
  else { assert.ok(stableVersion.test(state.version)); assert.ok(/^[0-9a-f]{40}$/.test(state.head)); }
  assert.ok(windowsPackage && existsSync(windowsPackage), 'Provide the rebuilt Windows ZIP with --windows-package.');
  validateSource();
  const output = path.join(root, '.local/release', state.version + (candidate ? '-candidate' : ''));
  mkdirSync(output, { recursive: true });
  const stage = mkdtempSync(path.join(output, 'staging-')), common = path.join(stage, 'common');
  mkdirSync(common, { recursive: true });
  const dotnet = process.env.METROIDVANIA_STUDIO_DOTNET || 'dotnet';
  for (const component of ['server', 'launcher', 'cli']) {
    const name = component === 'server' ? 'Server' : component === 'launcher' ? 'Launcher' : 'Cli';
    const project = `metroidvania-studio/${component}/MetroidvaniaStudio.${name}.csproj`;
    const properties = ['--configuration', 'Release', '--self-contained', 'false', '-p:UseAppHost=false',
      '-p:UseSharedCompilation=false', '-p:DebugType=None', '-p:DebugSymbols=false', `-p:PathMap=${root}=/_/src`, `-p:Version=${state.version}`];
    run(dotnet, ['build', project, '--no-incremental', ...properties]);
    run(dotnet, ['publish', project, '--no-build', '--no-restore', ...properties, '--output', path.join(common, 'app/metroidvania-studio', component)]);
  }
  run(process.execPath, ['metroidvania-studio/build-web.mjs', path.join(common, 'app/metroidvania-studio/dist')]);
  for (const relative of ['metroidvania-studio/localization', 'samples', 'docs',
    'metroidvania-studio/contracts/FORMAT.md', 'metroidvania-studio/contracts/map-format-v2.schema.json']) {
    const destination = path.join(common, 'app', relative);
    mkdirSync(path.dirname(destination), { recursive: true });
    cpSync(path.join(root, relative), destination, { recursive: true });
  }
  for (const name of ['LICENSE', 'THIRD-PARTY-NOTICES.md']) cpSync(path.join(root, name), path.join(common, name));
  run(process.execPath, ['integrations/build-packages.mjs', '--output', path.join(common, 'engine-packages')]);
  const archives = await buildArchives({ common, output, version: state.version, commit: state.head, windowsPackage: path.resolve(windowsPackage) });
  const blockingIssues = [], vendorMetadata = [];
  for (const archive of archives) {
    const entries = readZip(readFileSync(archive.path)), manifest = JSON.parse(entries.get('release-manifest.json'));
    const vendor = new Map(manifest.vendorFiles.map(file => [file.path, file]));
    for (const [name, bytes] of entries) {
      const issues = inspectBoundaries([{ name: 'commit message', bytes }]);
      if (!issues.length) continue;
      if (archive.name === 'metroidvania-studio-win.zip' && name === 'metroidvania-studio.exe') {
        const audit = await auditWindowsMetadata(bytes);
        if (audit.issues.length) blockingIssues.push({ archive: archive.name, path: name, rules: audit.issues.map(issue => issue.rule) });
        vendorMetadata.push({ archive: archive.name, path: name, verifiedSdkComponents: audit.verified });
        continue;
      }
      const origin = vendor.get(name);
      if (origin && createHash('sha256').update(bytes).digest('hex') === origin.sha256)
        vendorMetadata.push({ archive: archive.name, path: name, source: origin.source, rules: issues.map(issue => issue.rule) });
      else blockingIssues.push({ archive: archive.name, path: name, rules: issues.map(issue => issue.rule) });
    }
  }
  const report = { version: state.version, commit: state.head, candidate, replacesTag: replaceTag || null, archives, blockingIssues, vendorMetadata };
  writeFileSync(path.join(output, 'validation.json'), JSON.stringify(report, null, 2) + '\n');
  if (!candidate) { const after = readReleaseState(); validateReleaseState(after, replaceTag); assert.equal(after.head, state.head); }
  console.log(`Release inspection: ${blockingIssues.length} items require review. ${vendorMetadata.length} verified upstream runtime files contain vendor metadata.`);
  return report;
}
export async function main(args = process.argv.slice(2)) {
  const options = { action: '--dry-run', confirmation: '', windowsPackage: '', replaceTag: '' };
  for (let i = 0; i < args.length; i++) {
    const value = () => { assert.ok(args[i + 1], 'Missing option value.'); return args[++i]; };
    if (['--dry-run', '--pack', '--candidate', '--publish'].includes(args[i])) options.action = args[i];
    else if (args[i] === '--confirm') options.confirmation = value();
    else if (args[i] === '--replace-tag') options.replaceTag = value();
    else if (args[i] === '--windows-package') options.windowsPackage = value();
    else throw new Error('Unknown release option.');
  }
  const state = readReleaseState();
  if (options.action === '--candidate') { await prepareRelease(state, options.windowsPackage, { candidate: true }); return; }
  const tag = validateReleaseState(state, options.replaceTag);
  if (options.action === '--dry-run') { console.log(`Ready to prepare ${tag} from main. No files or tags changed.`); return; }
  if (options.action === '--pack') { await prepareRelease(state, options.windowsPackage, { replaceTag: options.replaceTag }); return; }
  const output = path.join(root, '.local/release', state.version), report = JSON.parse(readFileSync(path.join(output, 'validation.json'), 'utf8'));
  assert.equal(report.candidate, false);
  validatePublishContext(state, options.confirmation, report, options.replaceTag);
  for (const archive of report.archives) {
    assert.equal(path.resolve(archive.path), path.join(output, archive.name));
    assert.equal(createHash('sha256').update(readFileSync(archive.path)).digest('hex'), archive.sha256, 'Archive changed after validation.');
  }
  const gh = process.env.METROIDVANIA_STUDIO_GH || 'gh';
  run(gh, ['auth', 'status']);
  run('git', ['fetch', 'origin', 'main', '--no-tags']);
  const finalState = readReleaseState(); validatePublishContext(finalState, options.confirmation, report, options.replaceTag); assert.equal(finalState.head, state.head);
  const notes = path.join(output, 'release-notes.md');
  const content = state.changeLog.split(`## ${state.version}\n`)[1].split(/\n## /)[0].trim();
  writeFileSync(notes, content + '\n\nDownloads: web (installed ASP.NET Core Runtime 10), Windows (desktop EXE), macOS and Linux (browser UI; ARM64 and x64 runtimes included).\n\nExtract the entire ZIP and open the launch file at its top level. Engine packages and license notices are included.\n');
  if (options.replaceTag) {
    const remoteTag = git('ls-remote', '--refs', 'origin', `refs/tags/${tag}`).split(/\s/)[0];
    assert.equal(remoteTag, options.replaceTag, 'The remote release tag changed; nothing was replaced.');
    run(gh, ['release', 'view', tag]);
    // Hide downloads while replacing the complete set, then publish only after the
    // guarded tag update succeeds. A failed upload leaves a recoverable draft.
    run(gh, ['release', 'edit', tag, '--draft=true']);
    run(gh, ['release', 'upload', tag, ...report.archives.map(file => file.path), path.join(output, 'SHA256SUMS.txt'), '--clobber']);
    run('git', ['tag', '-a', '-f', tag, '-m', `Metroidvania Studio ${state.version}`, state.head]);
    run('git', ['push', `--force-with-lease=refs/tags/${tag}:${options.replaceTag}`, 'origin', `refs/tags/${tag}`]);
    run(gh, ['release', 'edit', tag, '--verify-tag', '--title', `Metroidvania Studio ${state.version}`, '--notes-file', notes, '--draft=false', '--latest']);
    return;
  }
  run('git', ['tag', '-a', tag, '-m', `Metroidvania Studio ${state.version}`, state.head]);
  run('git', ['push', 'origin', `refs/tags/${tag}`]);
  run(gh, ['release', 'create', tag, ...report.archives.map(file => file.path), path.join(output, 'SHA256SUMS.txt'), '--verify-tag', '--title', `Metroidvania Studio ${state.version}`, '--notes-file', notes, '--latest']);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main().catch(error => { console.error(error.stack); process.exitCode = 1; });
