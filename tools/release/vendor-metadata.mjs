import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readZip } from './archives.mjs';
import { inspectBoundaries } from '../repository/check-boundaries.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

export function codeViewRecords(bytes) {
  assert.ok(bytes.length >= 64 && bytes.subarray(0, 2).toString() === 'MZ', 'Expected a Windows executable.');
  const pe = bytes.readUInt32LE(60);
  assert.ok(pe + 24 <= bytes.length && bytes.readUInt32LE(pe) === 0x4550, 'Invalid PE header.');
  const sections = bytes.readUInt16LE(pe + 6), optionalSize = bytes.readUInt16LE(pe + 20), optional = pe + 24;
  const directory = optional + (bytes.readUInt16LE(optional) === 0x20b ? 112 : 96);
  assert.ok(directory + 56 <= optional + optionalSize && optional + optionalSize + sections * 40 <= bytes.length, 'Invalid PE sections.');
  const rva = bytes.readUInt32LE(directory + 48), size = bytes.readUInt32LE(directory + 52);
  assert.ok(size > 0 && size % 28 === 0, 'Missing debug records.');
  let offset = -1;
  for (let i = 0; i < sections; i++) {
    const at = optional + optionalSize + i * 40;
    const virtualSize = bytes.readUInt32LE(at + 8), address = bytes.readUInt32LE(at + 12);
    const rawSize = bytes.readUInt32LE(at + 16), pointer = bytes.readUInt32LE(at + 20);
    if (rva >= address && rva < address + Math.max(virtualSize, rawSize)) offset = pointer + rva - address;
  }
  assert.ok(offset >= 0 && offset + size <= bytes.length, 'Debug directory is outside the file.');
  const records = [];
  for (let at = offset; at < offset + size; at += 28) {
    if (bytes.readUInt32LE(at + 12) !== 2) continue;
    const length = bytes.readUInt32LE(at + 16), start = bytes.readUInt32LE(at + 24);
    assert.ok(length >= 24 && start + length <= bytes.length, 'Invalid CodeView record.');
    const data = bytes.subarray(start, start + length);
    assert.equal(data.subarray(0, 4).toString(), 'RSDS');
    records.push({ offset: start, bytes: data });
  }
  assert.ok(records.length > 0, 'Missing CodeView metadata.');
  return records;
}

export function inspectWindowsMetadata(executable, components, host) {
  const scanned = Buffer.from(executable), verified = [];
  for (const component of components) {
    assert.equal(sha256(component.bytes), component.sha256, 'SDK component checksum mismatch.');
    let start = 0, copies = 0, offset;
    while ((offset = executable.indexOf(component.bytes, start)) >= 0) {
      assert.ok(++copies <= 8, 'Unexpected number of bundled SDK copies.');
      scanned.fill(0, offset, offset + component.bytes.length); start = offset + component.bytes.length;
    }
    assert.ok(copies > 0, 'Bundled SDK component differs from the original.');
    verified.push({ component: component.path, sha256: component.sha256, source: component.source, copies });
  }
  assert.equal(sha256(host.bytes), host.sha256, 'SDK apphost checksum mismatch.');
  const original = codeViewRecords(host.bytes), actual = codeViewRecords(executable);
  assert.equal(actual.length, original.length, 'Unexpected apphost debug records.');
  for (let i = 0; i < actual.length; i++) {
    assert.ok(actual[i].bytes.equals(original[i].bytes), 'Apphost metadata differs from the original SDK.');
    let start = 0, offset, copies = 0;
    while ((offset = executable.indexOf(original[i].bytes, start)) >= 0) {
      assert.ok(++copies <= 8, 'Unexpected number of apphost metadata copies.');
      scanned.fill(0, offset, offset + original[i].bytes.length); start = offset + original[i].bytes.length;
    }
  }
  verified.push({ component: 'apphost CodeView metadata', sha256: host.sha256, source: host.source });
  // Only the in-memory audit copy is masked. Distributed SDK bytes stay unchanged.
  return { issues: inspectBoundaries([{ name: 'commit message', bytes: scanned }]), verified };
}

export async function auditWindowsMetadata(executable, cache = path.join(root, '.local/release-cache')) {
  const lock = JSON.parse(readFileSync(path.join(root, 'tools/release/windows-sdk-lock.json'), 'utf8'));
  mkdirSync(cache, { recursive: true });
  const components = []; let host;
  for (const source of lock.sources) {
    const url = new URL(source.url);
    assert.equal(url.protocol, 'https:'); assert.equal(url.hostname, 'api.nuget.org');
    const filename = path.join(cache, path.basename(url.pathname));
    if (!existsSync(filename)) {
      const response = await fetch(url, { signal: AbortSignal.timeout(120000) }); assert.ok(response.ok, 'SDK download failed.');
      const bytes = Buffer.from(await response.arrayBuffer()); assert.equal(sha256(bytes), source.sha256);
      writeFileSync(filename, bytes);
    }
    const archive = readFileSync(filename); assert.equal(sha256(archive), source.sha256, 'SDK archive checksum mismatch.');
    const entries = readZip(archive);
    for (const file of source.files) {
      const component = { ...file, bytes: entries.get(file.path), source: source.url };
      assert.ok(component.bytes, 'Missing original SDK component.');
      if (source.component === 'apphost') host = component; else components.push(component);
    }
  }
  assert.ok(host && components.length === 3, 'Incomplete SDK provenance lock.');
  return inspectWindowsMetadata(executable, components, host);
}
