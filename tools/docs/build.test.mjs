import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readdirSync, readFileSync, existsSync } from 'node:fs';
import { Script } from 'node:vm';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildDocs } from './build.mjs';
import { copyDocSources } from './copy-sources.mjs';
import { validateDocumentLinks } from '../npm/pack.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
test('offline site resolves navigation, anchors, code, downloadable schemas and examples', () => {
  const local = path.join(root, '.local/docs-tests'); mkdirSync(local, { recursive: true });
  const output = mkdtempSync(path.join(local, 'site-')), pages = buildDocs(output);
  assert.ok(pages.length >= 17);
  for (const file of readdirSync(output)) {
    const source = readFileSync(path.join(output, file), 'utf8');
    if (file.endsWith('.js')) new Script(source);
    if (!file.endsWith('.html')) continue;
    assert.ok(source.includes('charset="utf-8"') && source.includes('aria-current="page"'));
    if (!source.includes('class="homepage"')) assert.ok(source.includes('id="search"'));
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
  assert.ok(search.includes('Minimap design') && search.includes('Room layout'));
  assert.ok(!readFileSync(path.join(output, 'index.html'), 'utf8').includes('rel="canonical"'));
  assert.ok(!existsSync(path.join(output, 'assets')), 'Published downloads must not become offline sources');
});

test('public site has stable project URLs and portable schema and example downloads', () => {
  const local = path.join(root, '.local/docs-tests'); mkdirSync(local, { recursive: true });
  const output = mkdtempSync(path.join(local, 'public-'));
  const siteUrl = 'https://lbarimi.github.io/metroidvania-studio/';
  const pages = buildDocs(output, { siteUrl });
  const sitemap = readFileSync(path.join(output, 'sitemap.xml'), 'utf8');
  assert.ok(existsSync(path.join(output, '.nojekyll')));
  assert.equal([...sitemap.matchAll(/<loc>/g)].length, pages.length);
  const descriptions = new Set();
  for (const page of pages) {
    const source = readFileSync(path.join(output, page.href), 'utf8');
    const canonical = new URL(page.href.replace(/(?:^|\/)index\.html$/, match => match.startsWith('/') ? '/' : ''), siteUrl).href;
    assert.ok(source.includes(`rel="canonical" href="${canonical}"`));
    assert.ok(sitemap.includes(`<loc>${canonical}</loc>`));
    assert.ok(page.description.length > 15 && page.description.length <= 180);
    descriptions.add(page.description);
    for (const [, link] of source.matchAll(/(?:href|src)="([^"?]+)"/g)) {
      if (/^https?:/.test(link)) continue;
      const [target, fragment] = link.split('#'), targetPath = target ? path.resolve(output, path.dirname(page.href), target) : path.join(output, page.href);
      assert.ok(existsSync(targetPath), page.href + ' -> ' + link);
      if (fragment) assert.ok(readFileSync(targetPath, 'utf8').includes('id="' + fragment + '"'), link);
    }
  }
  assert.equal(descriptions.size, pages.length, 'Each page should describe its own content');
  const assets = path.join(output, 'assets');
  const openapi = JSON.parse(readFileSync(path.join(assets, 'api--http.openapi.json')));
  function checkReferences(value) {
    if (!value || typeof value !== 'object') return;
    for (const [key, child] of Object.entries(value)) {
      if (key === '$ref' && !child.startsWith('#')) assert.ok(existsSync(path.join(assets, child.split('#')[0])), child);
      else checkReferences(child);
    }
  }
  checkReferences(openapi);
  assert.ok(existsSync(path.join(assets, 'examples--connected-rooms.lua')));
  assert.throws(() => buildDocs(output, { siteUrl: 'http://localhost/docs/' }), /HTTPS/);
});

test('English homepage keeps search metadata and previews without language variants', () => {
  const local = path.join(root, '.local/docs-tests'); mkdirSync(local, { recursive: true });
  const output = mkdtempSync(path.join(local, 'home-'));
  const siteUrl = 'https://lbarimi.github.io/metroidvania-studio/';
  buildDocs(output, { siteUrl });
  const source = readFileSync(path.join(output, 'index.html'), 'utf8');
  assert.ok(source.includes('<html lang="en">'));
  assert.equal([...source.matchAll(/<h1\b/g)].length, 1);
  assert.equal([...source.matchAll(/rel="canonical"/g)].length, 1);
  assert.ok(source.includes(`rel="canonical" href="${siteUrl}"`));
  assert.ok(source.includes('releases/latest'));
  assert.equal([...source.matchAll(/<figure>/g)].length, 2);
  assert.ok(!source.includes('hreflang=') && !source.includes('class="language"'));
  assert.ok(!existsSync(path.join(output, 'ko')));
  assert.ok(!readFileSync(path.join(output, 'sitemap.xml'), 'utf8').includes('/ko/'));
  assert.ok(!readFileSync(path.join(output, 'search-index.js'), 'utf8').includes('ko/index.html'));
  const title = /<title>(.*?)<\/title>/.exec(source)[1];
  assert.equal(title.split('Metroidvania Studio').length - 1, 1);
  assert.ok(!/<meta name="description"[^>]*…/.test(source));
  const ids = [...source.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
  assert.equal(new Set(ids).size, ids.length);
  assert.ok(ids.every(Boolean));
  for (const name of ['create-and-paint.gif', 'design-the-minimap.gif']) {
    assert.deepEqual(readFileSync(path.join(output, 'assets', name)), readFileSync(path.join(root, 'media/readme', name)));
  }
  const offline = mkdtempSync(path.join(local, 'offline-home-'));
  buildDocs(offline);
  const entry = readFileSync(path.join(offline, 'index.html'), 'utf8');
  assert.ok(entry.includes('id="search"') && entry.includes('id="choose-a-document-owner"'));
  assert.ok(!entry.includes('class="homepage"') && !entry.includes('<figure>'));
  assert.ok(!existsSync(path.join(offline, 'assets')));
});

test('packaged source guides retain local links without website previews or generated pages', () => {
  const local = path.join(root, '.local/docs-tests'); mkdirSync(local, { recursive: true });
  const output = mkdtempSync(path.join(local, 'package-'));
  copyDocSources(path.join(output, 'docs'));
  const names = readdirSync(path.join(output, 'docs'), { recursive: true }).map(name => name.replaceAll('\\', '/'));
  assert.ok(!names.some(name => /(?:^|\/)assets(?:\/|$)|\.(gif|html|css|js)$/.test(name)));
  assert.equal(readFileSync(path.join(output, 'docs/index.md'), 'utf8'), readFileSync(path.join(root, 'docs/guides.md'), 'utf8'));
  const entries = names.filter(name => /\.(md|lua|json)$/.test(name)).map(name => ({ name: 'docs/' + name, bytes: readFileSync(path.join(output, 'docs', name)) }));
  for (const name of ['metroidvania-studio/contracts/FORMAT.md', 'metroidvania-studio/contracts/map-format-v2.schema.json']) entries.push({ name, bytes: readFileSync(path.join(root, name)) });
  validateDocumentLinks(entries);
});
