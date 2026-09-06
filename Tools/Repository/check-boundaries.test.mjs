import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { inspectBoundaries } from './check-boundaries.mjs';
import { stagedFiles } from './check-text.mjs';

const file = (name, text) => ({ name, bytes: Buffer.from(text) });
const drive = ['Z', ':', '/'].join('');
const privatePath = drive + ['Users', 'fixture-account', 'PrivateWorkspace', 'map.json'].join('/');

test('detects concrete machine paths in UTF-8, UTF-16, escaped JSON and commit messages', () => {
  const unix = ['', 'home', 'fixture-account', 'PrivateWorkspace', 'map.json'].join('/');
  const network = String.fromCharCode(92, 92) + ['fixture-host', 'share', 'map.json'].join(String.fromCharCode(92));
  const values = [privatePath, unix, network, JSON.stringify(privatePath.replaceAll('/', String.fromCharCode(92)))];
  for (const value of values) {
    assert.ok(inspectBoundaries([file('commit message', value)]).some(x => x.rule === 'machine-path'));
    const bytes = Buffer.concat([Buffer.from([255, 254]), Buffer.from(value, 'utf16le')]);
    assert.ok(inspectBoundaries([{ name: 'settings.json', bytes }]).some(x => x.rule === 'machine-path'));
  }
});
test('keeps portable environment paths, web routes and normal engine discussion valid', () => {
  for (const value of ['$env:ProgramFiles/dotnet/dotnet.exe', 'process.env.LOCALAPPDATA',
    '/api/state', 'https://example.invalid/docs', '../MyWorkspace', 'No engine installation is required.']) {
    assert.deepEqual(inspectBoundaries([file('README.md', value)]), []);
  }
});
test('blocks engine files, namespaces and escaped source links while allowing local core references', () => {
  const engineNamespace = ['Unit', 'yEngine'].join('');
  const source = 'using ' + engineNamespace + ';';
  assert.ok(inspectBoundaries([file('Core/Tool.cs', source)]).some(x => x.rule === 'engine-code'));
  assert.ok(inspectBoundaries([file('Core/Fake.cs', 'namespace ' + engineNamespace + ' {}')]).length);
  for (const name of ['Parts/Scene.' + 'unity', 'Parts/Assembly.' + 'asmdef', 'Parts/Texture.png.' + 'meta', 'Pack/Map.' + 'uasset'])
    assert.ok(inspectBoundaries([file(name, '')]).some(x => x.rule === 'engine-file'));
  const project = relative => '<Project><ItemGroup><Compile Include="' + relative + '" /></ItemGroup></Project>';
  assert.ok(inspectBoundaries([file('Core/Test.csproj', project('../../external/*.cs'))]).some(x => x.rule === 'external-source-link'));
  assert.ok(inspectBoundaries([file('Core/Test.csproj', project('../' + 'Assets/Runtime/*.cs'))]).some(x => x.rule === 'engine-source-link'));
  assert.deepEqual(inspectBoundaries([file('Core/Test.csproj', project('../Shared/*.cs'))]), []);
});
test('sample catalog rejects private roots, engine resource identifiers and source asset paths', () => {
  for (const value of [{ projectPath: '.' }, { materials: [{ id: 'a'.repeat(32) }] },
    { objects: [{ id: 'decal:' + 'b'.repeat(32) + ':23' }] }, { sprite: { asset: ['Assets', 'Art', 'tile.png'].join('/') } }]) {
    assert.ok(inspectBoundaries([file('Samples/catalog.json', JSON.stringify(value))]).length);
  }
  assert.deepEqual(inspectBoundaries([file('Samples/catalog.json', JSON.stringify({ projectPath: '', materials: [{ id: 'terrain', sprite: { asset: 'Textures/tile.png' } }] }))]), []);
  assert.ok(inspectBoundaries([file('Maps/private.map.json', '{}')]).some(x => x.rule === 'private-map-location'));
  assert.deepEqual(inspectBoundaries([file('Samples/Maps/Sample.map.json', '{}')]), []);
});
test('staged engine references remain blocked when unstaged text is already clean', () => {
  const base = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../MetroidvaniaStudio/.local');
  mkdirSync(base, { recursive: true });
  const scratch = mkdtempSync(path.join(base, 'boundary-test-'));
  const git = args => execFileSync('git', args, { cwd: scratch, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  try {
    git(['init', '--quiet']);
    writeFileSync(path.join(scratch, 'settings.json'), JSON.stringify({ path: privatePath }));
    git(['add', 'settings.json']);
    writeFileSync(path.join(scratch, 'settings.json'), '{}');
    assert.ok(inspectBoundaries(stagedFiles(scratch)).some(x => x.rule === 'machine-path'));
    git(['add', 'settings.json']);
    assert.deepEqual(inspectBoundaries(stagedFiles(scratch)), []);
  } finally {
    assert.equal(path.dirname(realpathSync(scratch)), realpathSync(base));
    rmSync(scratch, { recursive: true, force: true });
  }
});
