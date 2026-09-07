import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { inspectWindowsMetadata, codeViewRecords } from './vendor-metadata.mjs';
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
function pe() {
  const bytes = Buffer.alloc(1024); bytes.write('MZ'); bytes.writeUInt32LE(64, 60); bytes.writeUInt32LE(0x4550, 64);
  bytes.writeUInt16LE(1, 70); bytes.writeUInt16LE(240, 84); bytes.writeUInt16LE(0x20b, 88);
  bytes.writeUInt32LE(0x1000, 248); bytes.writeUInt32LE(28, 252);
  bytes.writeUInt32LE(512, 336); bytes.writeUInt32LE(0x1000, 340); bytes.writeUInt32LE(512, 344); bytes.writeUInt32LE(512, 348);
  bytes.writeUInt32LE(2, 524); bytes.writeUInt32LE(40, 528); bytes.writeUInt32LE(600, 536); bytes.write('RSDS', 600); bytes.write('vendor.pdb', 624);
  return bytes;
}
test('vendor audit preserves executable bytes and rejects unrelated paths or modified components', () => {
  const host = pe(), sdk = Buffer.from('locked SDK component'), component = { path: 'sdk.dll', bytes: sdk, sha256: digest(sdk), source: 'https://example.invalid/sdk' };
  const executable = Buffer.concat([host, sdk]), original = Buffer.from(executable);
  const origin = { bytes: host, sha256: digest(host), source: 'https://example.invalid/host' };
  assert.deepEqual(inspectWindowsMetadata(executable, [component], origin).issues, []); assert.deepEqual(executable, original);
  assert.equal(inspectWindowsMetadata(Buffer.concat([executable, sdk]), [component], origin).verified[0].copies, 2);
  const privatePath = Buffer.from(['C:', 'Users', 'private', 'file.pdb'].join(String.fromCharCode(92)));
  assert.equal(inspectWindowsMetadata(Buffer.concat([executable, privatePath]), [component], origin).issues[0].rule, 'machine-path');
  assert.throws(() => inspectWindowsMetadata(executable, [{ ...component, sha256: '0'.repeat(64) }], origin), /checksum/);
  const changed = Buffer.from(executable); changed[627] ^= 1;
  assert.throws(() => inspectWindowsMetadata(changed, [component], origin), /differs/);
});
test('malformed executable metadata cannot receive a vendor exemption', () => {
  assert.throws(() => codeViewRecords(Buffer.alloc(32)));
  const broken = pe(); broken.writeUInt32LE(0xfffffff0, 536);
  assert.throws(() => codeViewRecords(broken), /Invalid CodeView/);
});
