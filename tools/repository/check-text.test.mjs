import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { contentText, createMatcher, fingerprint, inspectFiles, stagedFiles } from './check-text.mjs';

const terms = ['prohibited fixture', 'blocked asset', '가상 검사'];
const matcher = createMatcher(terms.map(fingerprint));

test('detects case, compatibility Unicode, substrings and both string boundaries', () => {
  for (const text of ['PROHIBITED FIXTURE', 'ｐｒｏｈｉｂｉｔｅｄ ｆｉｘｔｕｒｅ', 'xblocked assety', '가상 검사'])
    assert.equal(matcher(text).length, 1);
  assert.equal(matcher('prohibited fixture / blocked asset').length, 2);
  assert.deepEqual(matcher('A normal room with tiles, tools and layer groups.'), []);
});

test('SHA-256 rejects a rolling-hash collision candidate', () => {
  const rule = { ...fingerprint('prohibited fixture'), sha256: '0'.repeat(64) };
  assert.deepEqual(createMatcher([rule])('prohibited fixture'), []);
});

test('rolling scan matches an independent substring oracle', () => {
  let seed = 123; const random = () => ((seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0) % 26);
  for (let trial = 0; trial < 100; trial++) {
    let text = Array.from({ length: trial * 3 }, () => String.fromCharCode(97 + random())).join('');
    if (trial % 2 === 0) text += terms[trial % terms.length];
    if (trial % 3 === 0) text = terms[(trial + 1) % terms.length] + text;
    const expected = terms.flatMap((term, id) => {
      const result = []; let index = text.indexOf(term);
      while (index >= 0) { result.push({ rule: id + 1, index }); index = text.indexOf(term, index + 1); }
      return result;
    });
    const order = (a, b) => a.index - b.index || a.rule - b.rule;
    assert.deepEqual(matcher(text).sort(order), expected.sort(order));
  }
});

test('UTF-8 and UTF-16 literal strings are checked without discarding binary prefixes', () => {
  const le = Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('blocked asset', 'utf16le')]);
  const be = Buffer.from(le); be.swap16();
  for (const bytes of [Buffer.from('blocked asset'), le, be, Buffer.from('\0\0blocked asset\0')])
    assert.equal(matcher(contentText(bytes)).length, 1);
});

test('filenames and commit-message payloads are checked', () => {
  assert.equal(inspectFiles([{ name: 'Docs/blocked asset.md', bytes: Buffer.from('ordinary text') }], matcher)[0].location, 'filename');
  assert.equal(inspectFiles([{ name: 'commit message', bytes: Buffer.from('docs: prohibited fixture') }], matcher)[0].location, 'content');
});

test('staged snapshots cannot be hidden by clean unstaged content and preserve binary framing', () => {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../metroidvania-studio/.local');
  mkdirSync(root, { recursive: true });
  const temp = mkdtempSync(path.join(root, 'text-check-test-'));
  const git = args => execFileSync('git', args, { cwd: temp, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  try {
    git(['init', '--quiet']);
    writeFileSync(path.join(temp, 'draft 한글.txt'), 'prohibited fixture');
    const binary = Buffer.from([0, 10, 1, 10, 2, 0, 255]);
    writeFileSync(path.join(temp, 'image.bin'), binary);
    git(['add', '--', 'draft 한글.txt', 'image.bin']);
    writeFileSync(path.join(temp, 'draft 한글.txt'), 'clean unstaged text');
    const staged = stagedFiles(temp);
    assert.equal(staged.length, 2);
    assert.deepEqual(staged.find(file => file.name === 'image.bin').bytes, binary);
    assert.equal(inspectFiles(staged, matcher).length, 1);
    git(['rm', '--cached', '--quiet', '--force', '--', 'draft 한글.txt']);
    assert.equal(inspectFiles(stagedFiles(temp), matcher).length, 0);
  } finally {
    assert.equal(path.dirname(realpathSync(temp)), realpathSync(root));
    rmSync(temp, { recursive: true, force: true });
  }
});
