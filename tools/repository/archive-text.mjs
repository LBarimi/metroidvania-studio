import { inflateRawSync, gunzipSync } from 'node:zlib';

const limit = 64 * 1024 * 1024;
export function archiveText(bytes, visit, decode) {
  const chunks = [];
  const take = (offset, length) => {
    if (length < 0 || offset < 0 || offset + length > bytes.length) throw new Error('Incomplete archive.');
    return bytes.subarray(offset, offset + length);
  };
  const u16 = offset => take(offset, 2).readUInt16LE();
  const u32 = offset => take(offset, 4).readUInt32LE();
  const plain = (offset, length) => chunks.push(decode(take(offset, length)));
  if (bytes[0] === 0x50 && bytes[1] === 0x4b && [0x04034b50, 0x06054b50].includes(u32(0))) {
    let offset = 0;
    const locals = new Map();
    while (u32(offset) === 0x04034b50) {
      if (locals.size >= 10000) throw new Error('Archive has too many entries.');
      take(offset, 30);
      const flags = u16(offset + 6), method = u16(offset + 8);
      if (flags & ~0x800 || ![0, 8].includes(method)) throw new Error('Unsupported ZIP encoding.');
      const crc = u32(offset + 14), packed = u32(offset + 18), size = u32(offset + 22);
      const nameLength = u16(offset + 26), extraLength = u16(offset + 28);
      const name = take(offset + 30, nameLength);
      const dataStart = offset + 30 + nameLength + extraLength;
      const data = take(dataStart, packed);
      if (size > limit) throw new Error('Archive entry is too large to inspect.');
      const expanded = method === 8 ? inflateRawSync(data, { maxOutputLength: limit }) : data;
      if (expanded.length !== size) throw new Error('ZIP entry size mismatch.');
      chunks.push(decode(name)); plain(offset + 30 + nameLength, extraLength);
      chunks.push(visit(expanded));
      locals.set(offset, { name, flags, method, crc, packed, size });
      offset = dataStart + packed;
    }
    const centralStart = offset, seen = new Set();
    while (u32(offset) === 0x02014b50) {
      take(offset, 46);
      const localOffset = u32(offset + 42), local = locals.get(localOffset);
      const nameLength = u16(offset + 28), extraLength = u16(offset + 30), commentLength = u16(offset + 32);
      if (!local || seen.has(localOffset) || u16(offset + 34) !== 0
          || u16(offset + 8) !== local.flags || u16(offset + 10) !== local.method
          || u32(offset + 16) !== local.crc || u32(offset + 20) !== local.packed
          || u32(offset + 24) !== local.size || !take(offset + 46, nameLength).equals(local.name))
        throw new Error('ZIP directory mismatch.');
      seen.add(localOffset);
      plain(offset + 46 + nameLength, extraLength + commentLength);
      offset += 46 + nameLength + extraLength + commentLength;
    }
    if (u32(offset) !== 0x06054b50 || u16(offset + 4) || u16(offset + 6)
        || u16(offset + 8) !== locals.size || u16(offset + 10) !== locals.size
        || seen.size !== locals.size || u32(offset + 12) !== offset - centralStart
        || u32(offset + 16) !== centralStart) throw new Error('Invalid ZIP directory.');
    const commentLength = u16(offset + 20);
    plain(offset + 22, commentLength);
    plain(offset + 22 + commentLength, bytes.length - offset - 22 - commentLength);
    return chunks.join('\n');
  }
  if (bytes[0] === 0x1f && bytes[1] === 0x8b) {
    take(0, 10);
    const flags = bytes[3];
    if (bytes[2] !== 8 || flags & 0xe0) throw new Error('Unsupported GZIP encoding.');
    let offset = 10;
    if (flags & 4) { const length = u16(offset); offset += 2; plain(offset, length); offset += length; }
    for (const flag of [8, 16]) if (flags & flag) {
      const end = bytes.indexOf(0, offset);
      if (end < 0) throw new Error('Incomplete GZIP metadata.');
      plain(offset, end - offset); offset = end + 1;
    }
    if (flags & 2) take(offset, 2);
    chunks.push(visit(gunzipSync(bytes, { maxOutputLength: limit })));
    return chunks.join('\n');
  }
  if (bytes.length >= 512 && bytes.subarray(257, 262).toString('ascii') === 'ustar') {
    let offset = 0;
    const number = field => {
      const value = field.toString('ascii').replaceAll('\0', '').trim();
      if (!/^[0-7]+$/.test(value)) throw new Error('Invalid TAR number.');
      return parseInt(value, 8);
    };
    while (offset < bytes.length) {
      const header = take(offset, 512);
      if (header.every(byte => byte === 0)) {
        if (!take(offset + 512, 512).every(byte => byte === 0)) throw new Error('Invalid TAR terminator.');
        plain(offset + 1024, bytes.length - offset - 1024);
        return chunks.join('\n');
      }
      const checksum = header.reduce((sum, byte, i) => sum + (i >= 148 && i < 156 ? 32 : byte), 0);
      if (number(header.subarray(148, 156)) !== checksum) throw new Error('Invalid TAR checksum.');
      const size = number(header.subarray(124, 136));
      if (size > limit) throw new Error('Archive entry is too large to inspect.');
      // Inspect names, link targets, owner names and prefixes; numeric headers are not text.
      for (const [start, length] of [[0, 100], [157, 100], [265, 32], [297, 32], [345, 155]]) plain(offset + start, length);
      chunks.push(visit(take(offset + 512, size)));
      const padded = Math.ceil(size / 512) * 512;
      plain(offset + 512 + size, padded - size);
      offset += 512 + padded;
    }
    throw new Error('Missing TAR terminator.');
  }
  return null;
}
