import { createHash } from 'node:crypto';
import { inspectBoundaries } from './check-boundaries.mjs';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const normalize = text => text.normalize('NFKC').toLowerCase();
const digest = text => createHash('sha256').update(text).digest('hex');

export function fingerprint(text) {
  const value = normalize(text); let rolling = 0;
  for (let i = 0; i < value.length; i++) rolling = (Math.imul(rolling, 31) + value.charCodeAt(i)) >>> 0;
  return { length: value.length, rolling, sha256: digest(value) };
}

export function createMatcher(rules) {
  const lengths = new Map();
  for (const [id, rule] of rules.entries()) {
    if (!Number.isInteger(rule.length) || rule.length < 2 || rule.length > 128
      || !Number.isInteger(rule.rolling) || !/^[a-f0-9]{64}$/.test(rule.sha256)) throw new Error('Invalid text policy fingerprint.');
    let group = lengths.get(rule.length);
    if (!group) {
      let power = 1; for (let i = 1; i < rule.length; i++) power = Math.imul(power, 31) >>> 0;
      group = { power, hashes: new Map() }; lengths.set(rule.length, group);
    }
    const candidates = group.hashes.get(rule.rolling) || [];
    candidates.push({ id: id + 1, sha256: rule.sha256 }); group.hashes.set(rule.rolling, candidates);
  }
  return text => {
    const value = normalize(text), hits = [];
    for (const [length, group] of lengths) {
      if (value.length < length) continue;
      let rolling = 0;
      for (let i = 0; i < length; i++) rolling = (Math.imul(rolling, 31) + value.charCodeAt(i)) >>> 0;
      for (let start = 0; start <= value.length - length; start++) {
        const candidates = group.hashes.get(rolling);
        if (candidates) {
          const hash = digest(value.slice(start, start + length));
          for (const rule of candidates) if (hash === rule.sha256) {
            hits.push({ rule: rule.id, index: start });
            if (hits.length >= 20) return hits;
          }
        }
        if (start + length < value.length)
          rolling = (Math.imul((rolling - Math.imul(value.charCodeAt(start), group.power)) >>> 0, 31) + value.charCodeAt(start + length)) >>> 0;
      }
    }
    return hits;
  };
}

export function loadMatcher() {
  return createMatcher(JSON.parse(readFileSync(path.join(root, 'Tools/Repository/restricted-text-fingerprints.json'), 'utf8')));
}

export function contentText(bytes) {
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return bytes.subarray(2).toString('utf16le');
  if (bytes[0] === 0xfe && bytes[1] === 0xff) {
    const copy = Buffer.from(bytes.subarray(2));
    for (let i = 0; i + 1 < copy.length; i += 2) [copy[i], copy[i + 1]] = [copy[i + 1], copy[i]];
    return copy.toString('utf16le');
  }
  // This checks literal byte strings, not text compressed into images/PDFs.
  return bytes.toString('utf8');
}

export function stagedFiles(cwd = root) {
  const index = execFileSync('git', ['ls-files', '--stage', '-z'], { cwd, encoding: 'utf8', windowsHide: true });
  const entries = index.split('\0').filter(Boolean).map(line => {
    const match = /^(\d+) ([a-f0-9]+) ([0-3])\t([\s\S]*)$/.exec(line);
    if (!match || match[3] !== '0') throw new Error('Resolve unmerged index entries before committing.');
    return { mode: match[1], oid: match[2], name: match[4] };
  }).filter(entry => entry.mode !== '160000');
  const ids = [...new Set(entries.map(entry => entry.oid))];
  if (!ids.length) return [];
  const output = execFileSync('git', ['cat-file', '--batch'], {
    cwd, input: ids.join('\n') + '\n', maxBuffer: 128 * 1024 * 1024, windowsHide: true,
  });
  const blobs = new Map(); let offset = 0;
  for (const oid of ids) {
    const end = output.indexOf(10, offset), header = output.subarray(offset, end).toString('ascii').split(' ');
    if (end < 0 || header[0] !== oid || header[1] !== 'blob') throw new Error('Could not inspect staged blob.');
    const size = Number(header[2]); offset = end + 1;
    if (!Number.isSafeInteger(size) || size < 0 || offset + size >= output.length || output[offset + size] !== 10)
      throw new Error('Incomplete staged blob data.');
    blobs.set(oid, output.subarray(offset, offset + size)); offset += size + 1;
  }
  return entries.map(entry => ({ name: entry.name, bytes: blobs.get(entry.oid) }));
}

export function workingFiles() {
  const names = execFileSync('git', ['ls-files', '--cached', '--others', '--exclude-standard', '-z'], {
    cwd: root, encoding: 'utf8', windowsHide: true,
  }).split('\0').filter(Boolean);
  return [...new Set(names)].flatMap(name => {
    try { return [{ name, bytes: readFileSync(path.join(root, name)) }]; }
    catch (error) { if (error.code === 'ENOENT' || error.code === 'EISDIR') return []; throw error; }
  });
}

export function inspectFiles(files, matcher) {
  return files.flatMap(file => {
    const text = contentText(file.bytes);
    return [...matcher(file.name).map(hit => ({ path: file.name, location: 'filename', rule: hit.rule })),
      ...matcher(text).map(hit => ({ path: file.name, location: 'content', rule: hit.rule }))];
  });
}

export function main(args = process.argv.slice(2)) {
  const matcher = loadMatcher();
  const files = args.length === 0 ? workingFiles()
    : args.length === 1 && args[0] === '--staged' ? stagedFiles()
    : args.length === 2 && args[0] === '--message' ? [{ name: 'commit message', bytes: readFileSync(args[1]) }]
    : (() => { throw new Error('Usage: check-text.mjs [--staged | --message <path>]'); })();
  const violations = [...inspectFiles(files, matcher), ...inspectBoundaries(files)];
  if (violations.length) {
    console.error('Repository content check failed. Remove restricted text or boundary violations before committing.');
    for (const item of violations.slice(0, 20)) console.error(`${item.path}: ${item.location} (policy ${item.rule})`);
    return 1;
  }
  console.log(`Repository text check passed (${files.length} files).`); return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try { process.exitCode = main(); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
