import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';

const ignoredDirectories = new Set(['.git', '.local', '.studio', 'bin', 'obj', 'dist', 'builds', 'node_modules', 'Library', 'Temp', 'logs', '.test-output']);
const sourceRoots = ['docs', 'samples', 'metroidvania-studio', 'integrations', 'tools', 'platform/shared', 'platform/win/web', 'platform/mac', 'platform/linux'];
const rootFiles = ['version.json', 'package.json', 'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config', 'nuget.config', 'LICENSE', 'NOTICE'];
export const requiredBuildFiles = [
  ...['Server', 'Launcher', 'Cli'].flatMap(name => ['dll', 'deps.json', 'runtimeconfig.json'].map(extension =>
    'metroidvania-studio/' + name.toLowerCase() + '/MetroidvaniaStudio.' + name + '.' + extension)),
  'metroidvania-studio/dist/index.html', 'metroidvania-studio/dist/app.js', 'metroidvania-studio/dist/map-canvas.js',
  'metroidvania-studio/localization/MetroidvaniaStudioLocale.csv', 'samples/catalog.json',
  ...['unity', 'godot', 'ue4', 'ue5', 'sdl'].map(engine => 'engine-packages/' + engine + '/metroidvania-studio.' + (engine === 'unity' ? 'unitypackage' : 'zip')),
];
const json = file => JSON.parse(readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));

export function sourceState(root) {
  const inputs = json(path.join(root, 'tools/build/package-inputs.json'));
  if (!Array.isArray(inputs) || !inputs.length) throw new Error('Build inputs must be a nonempty array.');
  const files = new Set();
  function visit(relative) {
    if (typeof relative !== 'string' || !relative || relative.includes('\\') || path.isAbsolute(relative) || /^[A-Za-z]:/.test(relative)
      || relative.split('/').some(part => !part || part === '.' || part === '..')) throw new Error('Invalid source input path.');
    if (relative.split('/').some(part => ignoredDirectories.has(part)) || relative === 'metroidvania-studio/web/contracts.generated.ts'
      || /(?:\.log|\.pdb|\.tmp|\.user)$/.test(relative)) return;
    const absolute = path.join(root, relative);
    if (!existsSync(absolute)) return;
    const stat = lstatSync(absolute);
    if (stat.isSymbolicLink()) throw new Error('Build sources cannot contain links: ' + relative);
    if (stat.isDirectory()) for (const name of readdirSync(absolute).sort()) visit(relative + '/' + name);
    else if (stat.isFile()) files.add(relative);
  }
  for (const relative of [...sourceRoots, ...rootFiles, ...inputs]) visit(relative);
  // Packages are regenerated, but their installation guides are authored inputs.
  for (const engine of ['unity', 'godot', 'ue4', 'ue5', 'sdl']) for (const language of ['KR', 'EN', 'JP', 'CN', 'TW'])
    visit('engine-packages/' + engine + '/INSTALL_' + language + '.txt');
  const hash = createHash('sha256').update('studio-source-v1\0');
  for (const relative of [...files].sort()) {
    hash.update(relative + '\0');
    hash.update(createHash('sha256').update(readFileSync(path.join(root, relative))).digest());
  }
  return { formatVersion: 1, version: json(path.join(root, 'version.json')).version, sourceHash: hash.digest('hex') };
}

export function currentBuild(root, state = sourceState(root)) {
  let index;
  try { index = json(path.join(root, 'builds/latest.json')); }
  catch { return { current: false, reason: 'No complete local build exists.' }; }
  if (index?.formatVersion !== 1 || typeof index.folder !== 'string' || !/^\d+\.\d+\.\d+-\d{8}-\d{6}-[a-f0-9]{8}$/.test(index.folder))
    return { current: false, reason: 'The local build index needs to be recreated.' };
  const output = path.join(root, 'builds', index.folder);
  let previous;
  try { previous = json(path.join(output, 'source-state.json')); }
  catch { return { current: false, reason: 'The local build has no source comparison record.' }; }
  if (!previous || typeof previous !== 'object') return { current: false, reason: 'The source comparison record is invalid.' };
  if (previous.version !== state.version || index.version !== state.version)
    return { current: false, reason: 'The local build version differs from the source version.' };
  if (previous.formatVersion !== state.formatVersion || previous.sourceHash !== state.sourceHash)
    return { current: false, reason: 'The source has changed since the last build.' };
  if (requiredBuildFiles.some(file => !existsSync(path.join(output, file))))
    return { current: false, reason: 'The local build is incomplete.' };
  return { current: true, output };
}
