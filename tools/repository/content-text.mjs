import { archiveText } from './archive-text.mjs';
import { pngText } from './png-text.mjs';

function decode(bytes) {
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return bytes.subarray(2).toString('utf16le');
  if (bytes[0] === 0xfe && bytes[1] === 0xff) {
    const copy = Buffer.from(bytes.subarray(2));
    for (let i = 0; i + 1 < copy.length; i += 2) [copy[i], copy[i + 1]] = [copy[i + 1], copy[i]];
    return copy.toString('utf16le');
  }
  return bytes.toString('utf8');
}

// GIF color tables and compressed pixels are not literal text. Inspect extension
// payloads and trailing data; malformed framing falls back to the full byte scan.
function gifText(bytes) {
  let offset = 13;
  const chunks = [];
  const skip = length => {
    if (offset + length > bytes.length) throw new Error('Incomplete GIF block.');
    offset += length;
  };
  const blocks = collect => {
    const parts = [];
    for (;;) {
      if (offset >= bytes.length) throw new Error('Incomplete GIF sub-block.');
      const length = bytes[offset++];
      if (!length) break;
      const start = offset; skip(length);
      if (collect) parts.push(bytes.subarray(start, offset));
    }
    if (collect) chunks.push(Buffer.concat(parts));
  };
  try {
    if (bytes.length < 13) throw new Error('Incomplete GIF header.');
    if (bytes[10] & 128) skip(3 * (2 ** ((bytes[10] & 7) + 1)));
    while (offset < bytes.length) {
      const tag = bytes[offset++];
      if (tag === 0x3b) {
        chunks.push(bytes.subarray(offset));
        return chunks.map(decode).join('\n');
      }
      if (tag === 0x21) {
        skip(1); // All extension payloads, including comments and application data.
        blocks(true);
      } else if (tag === 0x2c) {
        skip(9);
        const packed = bytes[offset - 1];
        if (packed & 128) skip(3 * (2 ** ((packed & 7) + 1)));
        skip(1); // LZW minimum code size.
        blocks(false);
      } else throw new Error('Unknown GIF block.');
    }
  } catch { }
  return decode(bytes);
}

export function contentText(bytes) {
  let remaining = 128 * 1024 * 1024;
  const visit = (data, depth = 0) => {
    if (depth > 8) throw new Error('Archive nesting is too deep to inspect.');
    remaining -= data.length;
    if (remaining < 0) throw new Error('Archive content is too large to inspect.');
    const archive = archiveText(data, child => visit(child, depth + 1), decode);
    if (archive !== null) return archive;
    const png = pngText(data, decode);
    if (png !== null) return png;
    return /^GIF8[79]a$/.test(data.subarray(0, 6).toString('ascii')) ? gifText(data) : decode(data);
  };
  return visit(bytes);
}
