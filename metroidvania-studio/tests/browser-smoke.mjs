import { createRequire } from 'node:module';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
await mkdir(path.join(project, '.local/logs'), { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, locale: 'ko-KR', deviceScaleFactor: 1 });
const page = await context.newPage(), errors = [];
page.on('pageerror', error => errors.push(error.message));
page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
try {
  await page.goto(process.env.METROIDVANIA_STUDIO_BASE_URL || 'http://127.0.0.1:18765');
  await page.locator('#room-list button').first().waitFor();
  await page.waitForTimeout(600);
  await page.screenshot({ path: path.join(project, '.local/logs/ExternalEditor-Web.png') });
  console.log(JSON.stringify({ title: await page.title(), canvas: await page.locator('#map-canvas').boundingBox(), buttons: await page.locator('button').allTextContents(), status: await page.locator('.statusbar').innerText(), errors }, null, 2));
  if (errors.length) throw new Error(errors.join('\n'));
} finally { await browser.close(); }
