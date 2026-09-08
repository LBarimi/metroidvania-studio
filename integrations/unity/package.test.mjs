import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { gunzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { buildPackage } from './build-package.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

test('Unity imports canonical legal text with stable metadata inside its package', () => {
  const parent = realpathSync(tmpdir()), folder = mkdtempSync(path.join(parent, 'studio-unity-package-'));
  try {
    const output = path.join(folder, 'metroidvania-studio.unitypackage');
    buildPackage(output);
    const first = readFileSync(output);
    buildPackage(output);
    assert.deepEqual(readFileSync(output), first, 'Package bytes must be reproducible.');
    const tar = gunzipSync(first), entries = new Map();
    for (let offset = 0; offset + 512 <= tar.length && tar[offset] !== 0;) {
      const name = tar.subarray(offset, offset + 100).toString('utf8').split('\0')[0];
      const size = Number.parseInt(tar.subarray(offset + 124, offset + 136).toString('ascii').replaceAll('\0', '').trim(), 8);
      assert.ok(Number.isSafeInteger(size) && size >= 0 && offset + 512 + size <= tar.length);
      assert.ok(!entries.has(name), 'Duplicate archive entry: ' + name);
      entries.set(name, tar.subarray(offset + 512, offset + 512 + size));
      offset += 512 + Math.ceil(size / 512) * 512;
    }
    for (const [source, imported] of [['LICENSE', 'LICENSE.txt'], ['THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt'], ['docs/objects-and-triggers.md', 'TRIGGERS.md']]) {
      const matches = [...entries].filter(([name, bytes]) => name.endsWith('/pathname') && bytes.toString('utf8') === 'Assets/MetroidvaniaStudioIntegration/' + imported);
      assert.equal(matches.length, 1, imported);
      const guid = matches[0][0].split('/')[0];
      assert.match(guid, /^[a-f0-9]{32}$/);
      assert.equal(entries.get(guid + '/asset').toString('utf8'), readFileSync(path.join(root, source), 'utf8').replaceAll('\r\n', '\n'));
      const metadata = entries.get(guid + '/asset.meta').toString('utf8');
      assert.ok(metadata.includes('guid: ' + guid + '\n'));
      assert.ok(metadata.includes('TextScriptImporter:'));
    }
  } finally {
    assert.equal(path.dirname(realpathSync(folder)), parent);
    rmSync(folder, { recursive: true, force: true });
  }
});
