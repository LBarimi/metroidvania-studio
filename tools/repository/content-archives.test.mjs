import test from 'node:test';
import assert from 'node:assert/strict';
import { gzipSync } from 'node:zlib';
import { contentText } from './content-text.mjs';
import { inspectBoundaries } from './check-boundaries.mjs';
import { createMatcher, fingerprint } from './check-text.mjs';
import { zip } from '../../integrations/build-packages.mjs';
import { tarFile } from '../../integrations/unity/build-package.mjs';

const drive = ['Z', ':', '/'].join('');
const privatePath = drive + ['Users', 'fixture-account', 'workspace', 'map.json'].join('/');
const forbidden = 'blocked asset';
const matcher = createMatcher([fingerprint(forbidden)]);
const issues = bytes => inspectBoundaries([{ name: 'engine-packages/godot/package.zip', bytes }]);

test('ZIP timestamps are binary while filenames and compressed UTF-8/UTF-16 content are inspected', () => {
  const clean = zip(new Map([['README.txt', Buffer.from('Install the package.')]]));
  // A valid DOS timestamp happens to resemble a drive path when decoded as text.
  const stamp = Buffer.from(drive + 'x');
  stamp.copy(clean, 10);
  const central = clean.indexOf(Buffer.from([0x50, 0x4b, 0x01, 0x02]));
  stamp.copy(clean, central + 12);
  assert.ok(clean.toString('utf8').includes(drive));
  assert.deepEqual(issues(clean), []);
  for (const data of [Buffer.from(privatePath), Buffer.concat([Buffer.from([255, 254]), Buffer.from(privatePath, 'utf16le')])]) {
    assert.ok(issues(zip(new Map([['settings.txt', data]]))).some(x => x.rule === 'machine-path'));
  }
  assert.ok(issues(zip(new Map([[privatePath, Buffer.from('data')]]))).some(x => x.rule === 'machine-path'));
  assert.equal(matcher(contentText(zip(new Map([['notes.txt', Buffer.from(forbidden)]])))).length, 1);
});

test('ZIP comments, trailing data and nested archives cannot hide prohibited text', () => {
  const bytes = zip(new Map([['nested.zip', zip(new Map([['notes.txt', Buffer.from(forbidden)]]))]]));
  assert.equal(matcher(contentText(bytes)).length, 1);
  const empty = zip(new Map()), comment = Buffer.from(privatePath);
  empty.writeUInt16LE(comment.length, 20);
  assert.ok(issues(Buffer.concat([empty, comment])).some(x => x.rule === 'machine-path'));
  assert.ok(issues(Buffer.concat([zip(new Map()), comment])).some(x => x.rule === 'machine-path'));
});

test('Unity archives inspect decompressed paths, metadata and text assets', () => {
  const tar = Buffer.concat([tarFile('entry/pathname', Buffer.from('Assets/Guide.txt')),
    tarFile('entry/asset', Buffer.from(privatePath)), Buffer.alloc(1024)]);
  assert.ok(issues(gzipSync(tar)).some(x => x.rule === 'machine-path'));
  assert.equal(matcher(contentText(gzipSync(Buffer.from(forbidden)))).length, 1);
  const clean = Buffer.concat([tarFile('entry/asset', Buffer.from('Install the package.')), Buffer.alloc(1024)]);
  assert.deepEqual(issues(gzipSync(clean)), []);
});

test('truncated or inconsistent archives fail inspection instead of bypassing it', () => {
  const original = zip(new Map([['guide.txt', Buffer.from('Install the package.')]]));
  assert.throws(() => contentText(original.subarray(0, original.length - 3)));
  const changed = Buffer.from(original); changed.writeUInt32LE(100000, 22);
  assert.throws(() => contentText(changed));
  const tar = Buffer.concat([tarFile('entry/asset', Buffer.from('Install.')), Buffer.alloc(1024)]);
  tar[0] ^= 1;
  assert.throws(() => contentText(gzipSync(tar)), /checksum/);
  let nested = Buffer.from('Install.');
  for (let i = 0; i < 10; i++) nested = gzipSync(nested);
  assert.throws(() => contentText(nested), /nesting/);
});
