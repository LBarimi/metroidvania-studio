import test from 'node:test';
import assert from 'node:assert/strict';
import { crc32, deflateSync } from 'node:zlib';
import { contentText } from './content-text.mjs';
import { inspectBoundaries } from './check-boundaries.mjs';

const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const drive = ['Z', ':', '/'].join('');
const privatePath = drive + ['Users', 'fixture-account', 'workspace'].join('/');
const chunk = (type, data = Buffer.alloc(0)) => {
  const result = Buffer.alloc(data.length + 12);
  result.writeUInt32BE(data.length); result.write(type, 4, 'ascii'); data.copy(result, 8);
  result.writeUInt32BE(crc32(result.subarray(4, data.length + 8)), data.length + 8);
  return result;
};
const png = (...extra) => {
  const header = Buffer.alloc(13); header.writeUInt32BE(1); header.writeUInt32BE(1, 4); header[8] = 8; header[9] = 2;
  // An uncompressed DEFLATE block retains a path-like sequence in actual pixels.
  return Buffer.concat([signature, chunk('IHDR', header), ...extra,
    chunk('IDAT', deflateSync(Buffer.from([0, ...Buffer.from(drive)]), { level: 0 })), chunk('IEND')]);
};
const issues = bytes => inspectBoundaries([{ name: 'media/preview.png', bytes }]);

test('PNG compressed pixels are not mistaken for local paths', () => {
  const image = png();
  assert.ok(image.includes(Buffer.from(drive)));
  assert.deepEqual(issues(image), []);
});

test('PNG text, compressed text, profiles, unknown chunks and trailing data remain inspected', () => {
  const text = Buffer.from(privatePath);
  for (const metadata of [
    chunk('tEXt', Buffer.concat([Buffer.from('Comment\0'), text])),
    chunk('zTXt', Buffer.concat([Buffer.from('Comment\0\0'), deflateSync(text)])),
    chunk('iCCP', Buffer.concat([Buffer.from('Profile\0\0'), deflateSync(text)])),
    chunk('iTXt', Buffer.concat([Buffer.from('Comment\0\0\0en\0Title\0'), text])),
    chunk('iTXt', Buffer.concat([Buffer.from('Comment'), Buffer.from([0, 1, 0]), Buffer.from('en\0Title\0'), deflateSync(text)])),
    chunk('eXIf', text), chunk('zzZZ', text),
  ]) assert.ok(issues(png(metadata)).some(issue => issue.rule === 'machine-path'));
  assert.ok(issues(Buffer.concat([png(), text])).some(issue => issue.rule === 'machine-path'));
});

test('invalid PNG framing and oversized compressed metadata fail inspection', () => {
  const image = png();
  assert.throws(() => contentText(image.subarray(0, image.length - 1)), /Incomplete/);
  const changed = Buffer.from(image); changed[16] ^= 1;
  assert.throws(() => contentText(changed), /checksum/);
  assert.throws(() => contentText(png(chunk('zTXt', Buffer.from('bad')))), /metadata/);
  assert.throws(() => contentText(png(chunk('zTXt', Buffer.concat([
    Buffer.from('Comment\0\0'), deflateSync(Buffer.alloc(9 * 1024 * 1024, 65)),
  ])))));
});
