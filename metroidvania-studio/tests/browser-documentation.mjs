import { createRequire } from 'node:module';
import assert from 'node:assert/strict';

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_MODULE || 'playwright');
const base = process.env.METROIDVANIA_STUDIO_BASE_URL;
assert.equal(process.env.METROIDVANIA_STUDIO_TEST_ISOLATED, '1');
assert.ok(['127.0.0.1', 'localhost'].includes(new URL(base).hostname));
const request = (resource, redirect = 'error') => fetch(base + resource, { redirect, signal: AbortSignal.timeout(10000) });
for (const alias of ['/docs', '/docs/']) {
  const response = await request(alias, 'manual');
  assert.equal(response.status, 302);
  assert.equal(response.headers.get('location'), '/docs/index.html', 'Both aliases must redirect to a file rather than matching themselves.');
}
for (const [resource, type] of [
  ['/docs/index.html', 'text/html'], ['/docs/api--index.html', 'text/html'],
  ['/docs/docs.css', 'text/css'], ['/docs/docs.js', 'text/javascript'],
  ['/docs/search-index.js', 'text/javascript'], ['/docs/api--http.openapi.json', 'application/json']
]) {
  const response = await request(resource); assert.equal(response.status, 200, resource);
  assert.ok(response.headers.get('content-type')?.startsWith(type), resource);
}
assert.equal((await request('/docs/missing-page.html')).status, 404);
const browser = await chromium.launch({ channel: process.platform === 'win32' ? 'msedge' : undefined, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, locale: 'ko-KR' });
  await page.addInitScript(() => Object.defineProperty(navigator, 'platform', { value: 'MacIntel' }));
  const errors = []; page.context().on('page', opened => opened.on('pageerror', error => errors.push(error.message)));
  await page.goto(base); await page.locator('#room-list button').first().waitFor();
  await page.locator('#language').selectOption('EN');
  assert.match(await page.locator('#undo').textContent(), /⌘\+Z/);
  assert.match(await page.locator('#status-state').getAttribute('title'), /⌘\+S/);
  for (const [menuItem, target] of [['Documentation', '/docs/index.html'], ['API reference', '/docs/api--index.html']]) {
    await page.locator('#help-menu-button').click();
    const popup = page.waitForEvent('popup'); await page.getByRole('menuitem', { name: menuItem, exact: true }).click();
    const document = await popup; await document.waitForLoadState('load');
    assert.equal(new URL(document.url()).pathname, target);
    await document.locator('main h1').waitFor();
    assert.equal(await document.locator('#search').count(), 1);
    assert.equal(await document.evaluate(() => getComputedStyle(document.querySelector('.sidebar')).display), 'block');
    const link = document.locator('.sidebar nav a').filter({ hasText: 'Quick start' }).first();
    if (await link.count()) { await link.click(); await document.locator('main h1').waitFor(); assert.equal(new URL(document.url()).origin, new URL(base).origin); }
    await document.close();
  }
  for (const [language, label, tag, search, empty] of [
    ['KR', '단축키', 'ko', '문서 검색', '검색 결과가 없습니다'],
    ['EN', 'Shortcuts', 'en', 'Search documentation', 'No matching pages'],
    ['JA', 'ショートカット', 'ja', 'ドキュメントを検索', '該当するページはありません'],
    ['ZH_CN', '快捷键', 'zh-hans', '搜索文档', '没有找到匹配的页面'],
    ['ZH_TW', '快速鍵', 'zh-hant-tw', '搜尋說明文件', '找不到符合的頁面'],
    ['RU', 'Горячие клавиши', 'ru', 'Поиск по документации', 'Страницы не найдены']
  ]) {
    await page.locator('#language').selectOption(language);
    if (language === 'RU') {
      const canvas = page.locator('#map-canvas');
      await canvas.dispatchEvent('keydown', { key: 'р', code: 'KeyH', altKey: true, isComposing: true });
      assert.equal(await page.locator('#help-menu-button').getAttribute('aria-expanded'), 'false');
      await canvas.dispatchEvent('keydown', { key: 'р', code: 'KeyH', altKey: true });
    } else await page.locator('#help-menu-button').click();
    const popup = page.waitForEvent('popup');
    await page.getByRole('menuitem', { name: label, exact: true }).click();
    const guide = await popup; await guide.waitForLoadState('load');
    assert.equal(new URL(guide.url()).pathname, tag === 'en' ? '/docs/shortcuts.html' : '/docs/shortcuts--' + tag + '.html');
    assert.equal(await guide.locator('html').getAttribute('lang'), tag);
    assert.equal(await guide.locator('main table').count(), 6);
    assert.ok(await guide.getByRole('columnheader', { name: 'macOS', exact: true }).isVisible());
    assert.equal(await guide.locator('nav a[aria-current="page"]').count(), 1);
    await guide.getByRole('searchbox', { name: search }).fill('studio-missing-entry-12345');
    await guide.getByText(empty, { exact: true }).waitFor();
    await guide.close();
  }
  assert.deepEqual(errors, []);
  console.log('Help opens local documentation, the API reference and shortcut tables in all six editor languages.');
} finally { await browser.close(); }
