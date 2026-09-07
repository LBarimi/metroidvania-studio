import { crc32, inflateSync } from 'node:zlib';

const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const limit = 8 * 1024 * 1024;

// Image pixels are binary. Keep inspecting text, profiles, unknown chunks and
// trailing bytes, including compressed metadata, without decoding image data.
export function pngText(bytes, decode) {
  if (!bytes.subarray(0, 8).equals(signature)) return null;
  let offset = 8, remaining = limit, sawHeader = false, sawPixels = false;
  const output = [];
  const append = value => {
    remaining -= value.length;
    if (remaining < 0) throw new Error('PNG metadata is too large to inspect.');
    output.push(decode(value));
  };
  while (offset + 12 <= bytes.length) {
    const length = bytes.readUInt32BE(offset);
    const end = offset + 8 + length;
    if (end + 4 > bytes.length) throw new Error('Incomplete PNG chunk.');
    const type = bytes.toString('ascii', offset + 4, offset + 8);
    const payload = bytes.subarray(offset + 8, end);
    if (crc32(bytes.subarray(offset + 4, end)) !== bytes.readUInt32BE(end))
      throw new Error('Invalid PNG chunk checksum.');
    if (!sawHeader) {
      if (type !== 'IHDR' || length !== 13) throw new Error('Invalid PNG header.');
      sawHeader = true;
    } else if (type === 'IHDR') throw new Error('Duplicate PNG header.');
    const inflate = value => inflateSync(value, { maxOutputLength: Math.max(1, remaining) });
    if (type === 'zTXt' || type === 'iCCP') {
      const zero = payload.indexOf(0);
      if (zero < 1 || zero + 2 > length || payload[zero + 1] !== 0) throw new Error('Invalid PNG compressed metadata.');
      append(payload.subarray(0, zero));
      append(inflate(payload.subarray(zero + 2)));
    } else if (type === 'iTXt') {
      const zero = payload.indexOf(0);
      if (zero < 1 || zero + 3 > length || payload[zero + 1] > 1 || payload[zero + 2] !== 0)
        throw new Error('Invalid PNG international text.');
      const languageEnd = payload.indexOf(0, zero + 3);
      const translatedEnd = languageEnd < 0 ? -1 : payload.indexOf(0, languageEnd + 1);
      if (translatedEnd < 0) throw new Error('Incomplete PNG international text.');
      append(payload.subarray(0, translatedEnd + 1));
      const text = payload.subarray(translatedEnd + 1);
      append(payload[zero + 1] ? inflate(text) : text);
    } else if (!['IHDR', 'PLTE', 'IDAT', 'IEND'].includes(type)) append(payload);
    if (type === 'IDAT') sawPixels = true;
    offset = end + 4;
    if (type === 'IEND') {
      if (length !== 0 || !sawPixels) throw new Error('Invalid PNG ending.');
      append(bytes.subarray(offset));
      return output.join('\n');
    }
  }
  throw new Error('Incomplete PNG image.');
}
