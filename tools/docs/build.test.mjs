import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readdirSync, readFileSync, existsSync } from 'node:fs';
import { Script } from 'node:vm';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildDocs } from './build.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
test('offline site resolves navigation, anchors, code, downloadable schemas and examples', () => {
  const local = path.join(root, '.local/docs-tests'); mkdirSync(local, { recursive: true });
  const output = mkdtempSync(path.join(local, 'site-')), pages = buildDocs(output);
  assert.ok(pages.length >= 17);
  for (const file of readdirSync(output)) {
    const source = readFileSync(path.join(output, file), 'utf8');
    if (file.endsWith('.js')) new Script(source);
    if (!file.endsWith('.html')) continue;
    assert.ok(source.includes('charset="utf-8"') && source.includes('id="search"') && source.includes('aria-current="page"'));
    for (const [, link] of source.matchAll(/(?:href|src)="([^"?]+)"/g)) {
      if (/^https?:/.test(link)) continue;
      const [target, fragment] = link.split('#'), targetPath = path.join(output, target || file);
      assert.ok(existsSync(targetPath), file + ' -> ' + link);
      if (fragment) assert.ok(readFileSync(targetPath, 'utf8').includes('id="' + fragment + '"'), 'Missing anchor: ' + link);
    }
  }
  const openapi = JSON.parse(readFileSync(path.join(output, 'api--http.openapi.json')));
  assert.equal(openapi.info.version, JSON.parse(readFileSync(path.join(root, 'version.json'))).version);
  assert.ok(openapi.paths['/api/v1/scripts'].post && openapi.paths['/api/v1/jobs'].post);
  assert.ok(readFileSync(path.join(output, 'api--http.openapi.json'), 'utf8').includes('api--automation.schema.json'));
  const search = readFileSync(path.join(output, 'search-index.js'), 'utf8');
  assert.ok(search.includes('Headless from a release download'));
});
