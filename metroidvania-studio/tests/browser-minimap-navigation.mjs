import assert from 'node:assert/strict';

export async function verifyMiniMapNavigation(page) {
  const state = () => page.evaluate(async () => (await fetch('/api/state')).json());
  const before = await state(), snapshot = JSON.stringify(before.document);
  assert.ok(before.document.rooms.length >= 2);
  const rooms = before.document.rooms, target = rooms.find(room => room.id !== before.selection.roomId);
  const wait = async () => {
    await page.waitForFunction(() => !document.querySelector('#status-state').classList.contains('working'));
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
  async function miniPoint(view, room) {
    const bounds = await view.locator('#mini-canvas').boundingBox(); assert.ok(bounds);
    const left = Math.min(...rooms.map(r => r.x)), right = Math.max(...rooms.map(r => r.x + r.width));
    const bottom = Math.min(...rooms.map(r => r.y)), top = Math.max(...rooms.map(r => r.y + r.height));
    const scale = Math.min(80, (bounds.width - 90) / (right - left), (bounds.height - 90) / (top - bottom));
    return { x: bounds.x + bounds.width / 2 + (room.x + room.width / 2 - (left + right) / 2) * scale,
      y: bounds.y + bounds.height / 2 - (room.y + room.height / 2 - (bottom + top) / 2) * scale };
  }
  async function verifyFocus(view, room) {
    await view.locator('#map-canvas').waitFor({ state: 'visible' });
    await view.waitForFunction(id => document.querySelector('#room-list button.active .room-label')?.textContent === id, room.name);
    const bounds = await view.locator('#map-canvas').boundingBox(), dpr = await view.evaluate(() => devicePixelRatio);
    await view.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await view.waitForFunction(() => document.querySelector('#status-coordinates').textContent.includes('16 px'));
    await view.waitForTimeout(60);
    const coordinates = (await view.locator('#status-coordinates').textContent()).match(/X (-?\d+)\s+Y (-?\d+)/);
    assert.ok(coordinates);
    assert.ok(Math.abs(Number(coordinates[1]) - (room.x + room.width / 2)) <= 1);
    assert.ok(Math.abs(Number(coordinates[2]) - (room.y + room.height / 2)) <= 1);
    const scale = Number((await view.locator('.zoom-readout').textContent()).replace('×', ''));
    assert.ok(Number.isInteger(scale) || Math.abs(1 / scale - Math.round(1 / scale)) < 1e-8, scale);
    assert.ok(room.width * 16 * scale / dpr <= bounds.width - 79);
    assert.ok(room.height * 16 * scale / dpr <= bounds.height - 79);
    assert.equal(await view.locator('#map-canvas').getAttribute('data-view-mode'), 'edit');
  }

  await page.locator('#tabs button').nth(1).click(); await wait();
  const bounds = await page.locator('#mini-canvas').boundingBox();
  await page.mouse.dblclick(bounds.x + 8, bounds.y + 8); await wait();
  assert.ok(await page.locator('#mini-canvas').isVisible(), 'Empty space must not open the editor.');
  const point = await miniPoint(page, target);
  await page.mouse.click(point.x, point.y); await wait();
  assert.equal((await state()).selection.roomId, target.id);
  assert.ok(await page.locator('#mini-canvas').isVisible(), 'Single click only selects.');
  await page.keyboard.down('Alt'); await page.mouse.dblclick(point.x, point.y); await page.keyboard.up('Alt'); await wait();
  assert.ok(await page.locator('#mini-canvas').isVisible(), 'Modified pan clicks must not open the editor.');
  await page.mouse.dblclick(point.x, point.y, { button: 'right' }); await wait();
  assert.ok(await page.locator('#mini-canvas').isVisible());
  const delaySelection = async route => {
    if (route.request().postDataJSON()?.action === 'selectRoom') await new Promise(resolve => setTimeout(resolve, 180));
    await route.continue();
  };
  await page.route('**/api/command', delaySelection);
  try { await page.mouse.dblclick(point.x, point.y, { delay: 65 }); await verifyFocus(page, target); }
  finally { await page.unroute('**/api/command', delaySelection); }
  assert.equal(JSON.stringify((await state()).document), snapshot, 'Navigation must not paint or mutate room data.');

  const popout = await page.context().newPage();
  try {
    const url = new URL(page.url()); url.searchParams.set('view', 'minimap');
    await popout.goto(url.href); await popout.locator('#mini-canvas').waitFor({ state: 'visible' });
    await popout.waitForFunction(() => document.querySelector('#room-list button'));
    await popout.waitForTimeout(100);
    const other = rooms.find(room => room.id !== target.id), at = await miniPoint(popout, other);
    await popout.mouse.dblclick(at.x, at.y, { delay: 65 }); await verifyFocus(popout, other);
    assert.ok(await popout.locator('.sidebar').isVisible());
    assert.ok(await popout.locator('#tabs').isVisible());
    assert.equal(await popout.locator('#app').evaluate(node => node.classList.contains('standalone-app')), false);
    assert.equal(new URL(popout.url()).searchParams.get('view'), null);
    assert.equal(JSON.stringify((await state()).document), snapshot);
  } finally { await popout.close(); }
  return 'Minimap single click selects; double-click focuses the room in the editor without painting, including popout and delayed responses.';
}
