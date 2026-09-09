import assert from 'node:assert/strict';
import test from 'node:test';
import { GameCamera } from '../web/game-camera.ts';

const room = (overrides = {}) => ({ id: 'room', visible: true, x: 0, y: 0, width: 40, height: 24, ...overrides });
const profile = (ppu = 16, width = 320, height = 180) => ({ ppu, referenceWidth: width, referenceHeight: height, orthographicSize: height / (2 * ppu), x: 0, y: 0 });

test('pixel-perfect toggle switches between source-pixel steps and fractional motion', () => {
  const camera = new GameCamera(); camera.sync(room(), profile(16, 427, 239), 'a');
  assert.equal(camera.pixelPerfect, true);
  const initial = camera.center, revision = camera.revision;
  assert.equal(camera.setPixelPerfect(false), true);
  assert.deepEqual(camera.center, initial); assert.equal(camera.revision, revision + 1);
  const target = { x: 19.153, y: 13.141 };
  camera.move(target); assert.deepEqual(camera.center, target);
  assert.ok(Math.abs(camera.frame.x * 16 - Math.round(camera.frame.x * 16)) > .01);
  camera.setPixelPerfect(true);
  assert.equal(camera.frame.x * 16, Math.round(camera.frame.x * 16));
  assert.equal(camera.frame.y * 16, Math.round(camera.frame.y * 16));
  assert.ok(Math.abs(camera.center.x - target.x) <= 1 / 32);
  const snappedRevision = camera.revision;
  assert.equal(camera.setPixelPerfect(true), false); assert.equal(camera.revision, snappedRevision);
});

test('smooth movement retains room bounds, small-axis locks and viewing preference across rooms', () => {
  const camera = new GameCamera(); camera.setPixelPerfect(false);
  camera.sync(room({ x: -40, y: -24 }), profile(), 'a');
  for (const x of [-1000, 1000]) for (const y of [-1000, 1000]) {
    camera.move({ x, y }); const frame = camera.frame;
    assert.ok(frame.x >= -40 && frame.y >= -24 && frame.x + frame.width <= 0 && frame.y + frame.height <= 0);
  }
  camera.sync(room({ id: 'narrow', width: 12, height: 30 }), profile(), 'a');
  camera.move({ x: 999, y: 17.013 }); assert.deepEqual(camera.center, { x: 10, y: 17.013 });
  camera.sync(undefined, profile(), 'a'); camera.sync(room(), profile(), 'b');
  assert.equal(camera.pixelPerfect, false);
  const before = camera.center; assert.equal(camera.move({ x: Infinity, y: 1 }), false); assert.deepEqual(camera.center, before);
});
test('camera frame matches reference pixels and clamps at every room edge', () => {
  const camera = new GameCamera(); camera.sync(room({ x: -40, y: -24 }), profile(), 'a');
  assert.deepEqual(camera.center, { x: -30, y: -18.375 });
  assert.deepEqual(camera.frame, { x: -40, y: -24, width: 20, height: 11.25 });
  for (const x of [-1000, 1000]) for (const y of [-1000, 1000]) {
    camera.move({ x, y }); const frame = camera.frame;
    assert.ok(frame.x >= -40 && frame.y >= -24 && frame.x + frame.width <= 0 && frame.y + frame.height <= 0);
    assert.deepEqual(camera.visibleFrame, frame);
  }
  const before = camera.center; assert.equal(camera.move({ x: NaN, y: 0 }), false); assert.deepEqual(camera.center, before);
});
test('small axes stay anchored at the lower-left while the visible box remains inside the room', () => {
  const camera = new GameCamera(); camera.sync(room({ x: 10, width: 12, height: 30 }), profile(), 'a');
  camera.move({ x: 999, y: 999 });
  assert.equal(camera.center.x, 20); assert.equal(camera.frame.width, 20); assert.equal(camera.visibleFrame.width, 12);
  assert.equal(camera.visibleFrame.x, 10); assert.equal(camera.frame.y + camera.frame.height, 30); assert.equal(camera.clipped, true);
  camera.sync(room({ width: 12, height: 8 }), profile(), 'a');
  camera.move({ x: -999, y: -999 }); assert.deepEqual(camera.center, { x: 10, y: 5.625 });
  assert.deepEqual(camera.visibleFrame, { x: 0, y: 0, width: 12, height: 8 });
});
test('pixel alignment includes odd resolutions and PPU changes preserve framing', () => {
  const camera = new GameCamera(); camera.sync(room(), profile(16, 427, 239), 'a'); camera.move({ x: 19.153, y: 13.141 });
  assert.equal(camera.frame.x * 16, Math.round(camera.frame.x * 16));
  assert.equal(camera.frame.y * 16, Math.round(camera.frame.y * 16));
  const before = camera.frame, revision = camera.revision;
  assert.equal(camera.sync(room(), profile(32, 427, 239), 'a'), false);
  assert.deepEqual(camera.frame, before); assert.equal(camera.revision, revision);
});
test('moving, resizing, changing resolution and switching rooms reconcile camera bounds', () => {
  const camera = new GameCamera(); camera.sync(room(), profile(), 'a'); camera.move({ x: 25, y: 15 });
  camera.sync(room({ x: 80, y: -40 }), profile(), 'a'); assert.deepEqual(camera.center, { x: 105, y: -25 });
  camera.sync(room({ x: 80, y: -40, width: 22 }), profile(), 'a'); assert.equal(camera.center.x, 92);
  camera.sync(room({ x: 80, y: -40, width: 22 }), profile(16, 640, 360), 'a'); assert.equal(camera.center.x, 100);
  camera.sync(room({ id: 'other' }), profile(), 'a'); assert.deepEqual(camera.center, { x: 10, y: 5.625 });
  camera.move({ x: 25, y: 15 }); camera.sync(room({ id: 'other' }), profile(), 'b'); assert.deepEqual(camera.center, { x: 10, y: 5.625 });
  camera.sync(undefined, profile(), 'b'); assert.equal(camera.frame, null); assert.equal(camera.visibleFrame, null);
});

test('resolution changes preserve the lower-left view origin at every PPU', () => {
  for (const ppu of [6, 12, 16, 24, 32, 100, 128]) {
    const camera = new GameCamera(), large = room({ x: -7, y: 3, width: 120, height: 80 });
    camera.sync(large, profile(ppu), 'a');
    assert.deepEqual(camera.frame, { x: -7, y: 3, width: 20, height: 11.25 });
    camera.sync(large, profile(ppu, 640, 360), 'a');
    assert.deepEqual(camera.frame, { x: -7, y: 3, width: 40, height: 22.5 });
    camera.move({ x: 30, y: 25 }); const origin = camera.frame;
    camera.sync(large, profile(ppu, 320, 180), 'a');
    assert.equal(camera.frame.x, origin.x); assert.equal(camera.frame.y, origin.y);
  }
});
