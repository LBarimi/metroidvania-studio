import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, writeFileSync, copyFileSync, chmodSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { inspectFiles, loadMatcher } from '../repository/check-text.mjs';
import { inspectBoundaries } from '../repository/check-boundaries.mjs';
import { findDotnet } from '../../bin/metroidvania-studio.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const json = file => JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
export function findNpm(env = process.env) {
  const candidates = [env.METROIDVANIA_STUDIO_NPM_CLI, env.npm_execpath,
    path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js'),
    path.resolve(path.dirname(process.execPath), '../node_modules/npm/bin/npm-cli.js')].filter(Boolean);
  const found = candidates.find(file => path.basename(file) === 'npm-cli.js' && existsSync(file));
  if (!found) throw new Error('Run through npm run pack:local, or set METROIDVANIA_STUDIO_NPM_CLI to npm-cli.js from an installed npm.');
  return found;
}
function execute(command, args, cwd = root, env = process.env) {
  const result = spawnSync(command, args, { cwd, env, encoding: 'utf8', windowsHide: true, maxBuffer: 32 * 1024 * 1024 });
  if (result.error || result.status !== 0) throw new Error(result.error?.message ?? result.stderr ?? result.stdout);
  return result.stdout;
}
function files(directory, prefix = '') {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const name = prefix + entry.name, full = path.join(directory, entry.name);
    if (entry.isSymbolicLink()) throw new Error('Package input must not contain symbolic links: ' + name);
    return entry.isDirectory() ? files(full, name + '/') : [{ name, full }];
  });
}
function copy(source, target) { mkdirSync(path.dirname(target), { recursive: true }); copyFileSync(source, target); }
export function validateMetadata(manifest, metadata) {
  if (manifest.name !== 'metroidvania-studio' || manifest.mcpName !== metadata.name || manifest.version !== metadata.version)
    throw new Error('npm and MCP names and versions must match.');
  const item = metadata.packages?.[0];
  if (metadata.packages?.length !== 1 || item.registryType !== 'npm' || item.identifier !== manifest.name || item.version !== manifest.version || item.transport?.type !== 'stdio')
    throw new Error('MCP metadata must reference the matching stdio npm package.');
}
export function validateDocumentLinks(entries) {
  const paths = new Set(entries.map(file => file.name));
  for (const file of entries.filter(file => /\.md$/i.test(file.name))) {
    for (const match of file.bytes.toString('utf8').matchAll(/\]\(([^\s)]+)(?:\s+"[^"]*")?\)/g)) {
      const link = match[1].replace(/^<|>$/g, '');
      if (/^[a-z][a-z\d+.-]*:/i.test(link) || link.startsWith('#')) continue;
      const target = path.posix.normalize(path.posix.join(path.posix.dirname(file.name), decodeURIComponent(link.split(/[?#]/)[0])));
      if (!paths.has(target)) throw new Error('Broken packaged documentation link: ' + file.name + ' -> ' + link);
    }
  }
}
export function auditPackage(directory) {
  const entries = files(directory).map(file => ({ name: file.name, bytes: readFileSync(file.full) }));
  validateDocumentLinks(entries);
  const prohibited = /(?:^|\/)(?:media|platform|engine-packages|integrations|src|node_modules|\.local|\.git)(?:\/|$)|\.(?:pdb|exe|cs|csproj|map\.json)$/i;
  if (entries.some(file => prohibited.test(file.name))) throw new Error('Package contains source, build symbols, engine assets, or private data.');
  const issues = [...inspectFiles(entries, loadMatcher()), ...inspectBoundaries(entries)];
  if (issues.length) throw new Error('Package audit failed: ' + issues.map(issue => issue.path + ' (' + issue.rule + ')').join(', '));
  for (const name of ['app/MetroidvaniaStudio.Cli.dll', 'app/MetroidvaniaStudio.Cli.deps.json', 'app/MetroidvaniaStudio.Cli.runtimeconfig.json',
    'app/MoonSharp.Interpreter.dll', 'app/ModelContextProtocol.Core.dll', 'app/licenses/moonsharp-license.txt',
    'app/licenses/mcp-sdk-license.txt', 'app/licenses/mcp-sdk-third-party-notices.txt', 'app/licenses/microsoft-extensions-ai-license.txt',
    'app/licenses/microsoft-extensions-ai-third-party-notices.txt', 'app/licenses/microsoft-extensions-license.txt',
    'app/licenses/microsoft-extensions-third-party-notices.txt', 'app/licenses/dependencies.json', 'metroidvania-studio/contracts/FORMAT.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md'])
    if (!entries.some(file => file.name === name)) throw new Error('Missing package component: ' + name);
  return entries;
}
export function pack() {
  if (Number(process.versions.node.split('.')[0]) < 24) throw new Error('Node.js 24 or later is required.');
  const manifest = json(path.join(root, 'package.json')), metadata = json(path.join(root, 'server.json'));
  if (manifest.private !== true) throw new Error('The source manifest must stay private.');
  if (json(path.join(root, 'version.json')).version !== manifest.version) throw new Error('The source and package versions must match.');
  validateMetadata(manifest, metadata);
  const npm = findNpm(), dotnet = findDotnet();
  execute(process.execPath, [path.join(root, 'tools/scripting/build-runtime.mjs')]);
  const destination = path.join(root, 'builds/npm'); mkdirSync(destination, { recursive: true });
  const stage = mkdtempSync(path.join(destination, 'stage-')), app = path.join(stage, 'app');
  const project = path.join(root, 'metroidvania-studio/cli/MetroidvaniaStudio.Cli.csproj');
  const flags = ['--configuration', 'Release', '--nologo', '-p:UseSharedCompilation=false', '-p:DebugType=None', '-p:DebugSymbols=false',
    '-p:UseAppHost=false', '-p:PathMap=' + root + '=/_/src', '-p:Version=' + manifest.version];
  execute(dotnet, ['build', project, '--no-incremental', ...flags]);
  const publish = path.join(stage, 'publish');
  execute(dotnet, ['publish', project, '--no-build', '--no-restore', '--self-contained', 'false', '--output', publish, ...flags]);
  for (const file of files(publish)) {
    if (/\.dll$|\.(?:deps|runtimeconfig)\.json$/.test(file.name) || /^licenses\/[^/]+\.(?:txt|md|json)$/i.test(file.name)) copy(file.full, path.join(app, file.name));
  }
  // Only explicit package paths are staged; publish intermediates stay outside the npm files allowlist.
  copy(path.join(root, 'bin/metroidvania-studio.mjs'), path.join(stage, 'bin/metroidvania-studio.mjs'));
  chmodSync(path.join(stage, 'bin/metroidvania-studio.mjs'), 0o755);
  for (const name of ['LICENSE', 'THIRD-PARTY-NOTICES.md', 'server.json']) copy(path.join(root, name), path.join(stage, name));
  copy(path.join(root, 'tools/npm/README.md'), path.join(stage, 'README.md'));
  for (const file of files(path.join(root, 'docs'))) copy(file.full, path.join(stage, 'docs', file.name));
  for (const name of ['FORMAT.md', 'map-format-v2.schema.json']) copy(path.join(root, 'metroidvania-studio/contracts', name), path.join(stage, 'metroidvania-studio/contracts', name));
  const published = { name: manifest.name, version: manifest.version, description: manifest.description, license: manifest.license,
    type: 'module', private: false, engines: manifest.engines, mcpName: manifest.mcpName, repository: manifest.repository,
    bin: { 'metroidvania-studio': 'bin/metroidvania-studio.mjs' }, files: ['bin/', 'app/', 'docs/', 'metroidvania-studio/contracts/', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'server.json'] };
  writeFileSync(path.join(stage, 'package.json'), JSON.stringify(published, null, 2) + '\n');
  const auditRoot = mkdtempSync(path.join(destination, 'audit-'));
  for (const file of files(stage).filter(file => !file.name.startsWith('publish/'))) copy(file.full, path.join(auditRoot, file.name));
  chmodSync(path.join(auditRoot, 'bin/metroidvania-studio.mjs'), 0o755);
  const audited = auditPackage(auditRoot);
  const env = { ...process.env, npm_config_audit: 'false', npm_config_fund: 'false', npm_config_update_notifier: 'false' };
  const packed = JSON.parse(execute(process.execPath, [npm, 'pack', auditRoot, '--json', '--ignore-scripts', '--offline', '--pack-destination', destination], root, env));
  const result = Array.isArray(packed) ? packed[0] : packed[manifest.name];
  if (!result?.files) throw new Error('npm returned an unexpected package inventory.');
  if (result.files.some(file => /^(?:publish|node_modules)\//.test(file.path))) throw new Error('npm included an unexpected build path.');
  const report = { version: manifest.version, archive: result.filename, files: audited.map(file => file.name), size: result.size, integrity: result.integrity, published: false };
  writeFileSync(path.join(destination, 'latest.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
  return path.join(destination, result.filename);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { if (process.argv.length !== 2) throw new Error('This tool only creates a local npm package.'); pack(); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
