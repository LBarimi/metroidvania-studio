import test from 'node:test';
import assert from 'node:assert/strict';
import { contentText } from './content-text.mjs';
import { inspectBoundaries } from './check-boundaries.mjs';
import { createMatcher, fingerprint, inspectFiles } from './check-text.mjs';

const marker = ['Z', ':', '/', 'fixture-account', '/', 'room.json'].join('');
const subblocks = bytes => Buffer.concat([Buffer.from([bytes.length]), bytes, Buffer.from([0])]);
const header = Buffer.concat([Buffer.from('GIF89a'), Buffer.from([1, 0, 1, 0, 0, 0, 0])]);
const image = Buffer.concat([Buffer.from([0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2]), subblocks(Buffer.from(marker))]);
const extension = (label, parts) => Buffer.concat([Buffer.from([0x21, label]),
  ...parts.map(part => Buffer.concat([Buffer.from([part.length]), Buffer.from(part)])), Buffer.from([0])]);
const gif = (...parts) => Buffer.concat([header, ...parts, image, Buffer.from([0x3b])]);
const inspect = bytes => inspectBoundaries([{ name: 'media/demo.gif', bytes }]);

test('GIF image sub-blocks are not treated as literal machine paths', () => {
  assert.equal(contentText(gif()), '');
  assert.deepEqual(inspect(gif()), []);
  const local = Buffer.from(image); local[9] = 128;
  const palette = Buffer.from([0, 0, 0, 255, 255, 255]);
  const framed = Buffer.concat([header, local.subarray(0, 10), palette, local.subarray(10), Buffer.from([0x3b])]);
  assert.deepEqual(inspect(framed), []);
});

test('GIF comments, plain text, application data and unknown extensions remain checked', () => {
  for (const label of [0xfe, 0x01, 0xff, 0xce]) {
    const bytes = gif(extension(label, [marker.slice(0, 9), marker.slice(9)]));
    assert.ok(inspect(bytes).some(issue => issue.rule === 'machine-path'));
  }
  const term = 'blocked fixture';
  const matcher = createMatcher([fingerprint(term)]);
  assert.equal(inspectFiles([{ name: 'media/demo.gif', bytes: gif(extension(0xfe, [term])) }], matcher).length, 1);
});

test('GIF trailing data and malformed blocks fall back to literal inspection', () => {
  const complete = gif();
  const trailing = Buffer.concat([complete, Buffer.from(marker)]);
  assert.ok(inspect(trailing).some(issue => issue.rule === 'machine-path'));
  for (const bytes of [complete.subarray(0, complete.length - 1), Buffer.concat([header, Buffer.from([0xff]), Buffer.from(marker)])])
    assert.ok(inspect(bytes).some(issue => issue.rule === 'machine-path'));
  assert.equal(contentText(Buffer.from('GIF89a')), 'GIF89a');
  assert.ok(inspect(Buffer.from(marker)).some(issue => issue.rule === 'machine-path'));
});
