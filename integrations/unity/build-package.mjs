import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../..');
export function tarFile(name, bytes) {
  const header = Buffer.alloc(512);
  header.write(name, 0, 100, 'utf8');
  header.write('0000644\0', 100, 8); header.write('0000000\0', 108, 8); header.write('0000000\0', 116, 8);
  header.write(bytes.length.toString(8).padStart(11, '0') + '\0', 124, 12); header.write('00000000000\0', 136, 12);
  header.fill(32, 148, 156); header.write('0', 156); header.write('ustar\0', 257, 6); header.write('00', 263, 2);
  let checksum = 0; for (const byte of header) checksum += byte;
  header.write(checksum.toString(8).padStart(6, '0') + '\0 ', 148, 8);
  return Buffer.concat([header, bytes, Buffer.alloc((512 - bytes.length % 512) % 512)]);
}
export function buildPackage(destination) {
  const version = JSON.parse(readFileSync(path.join(root, 'version.json'), 'utf8')).version;
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error('Invalid package version.');
  const source = path.join(here, 'Assets/metroidvania-studio-integration');
  const entries = [], guids = new Set();
  function visit(asset) {
    const metaPath = asset + '.meta';
    if (!existsSync(metaPath)) throw new Error('Every package asset needs stable metadata.');
    const metadata = readFileSync(metaPath);
    const guid = /^guid: ([a-f0-9]{32})\s*$/m.exec(metadata.toString('utf8'))?.[1];
    if (!guid || guids.has(guid)) throw new Error('Invalid or duplicate asset GUID.');
    guids.add(guid);
    // Keep the installed package paths stable when source folders are renamed.
    const relative = path.relative(here, asset).replaceAll('\\', '/')
      .replace('Assets/metroidvania-studio-integration', 'Assets/MetroidvaniaStudioIntegration')
      .replace('/runtime', '/Runtime');
    if (!relative.startsWith('Assets/MetroidvaniaStudioIntegration')) throw new Error('Unexpected package root.');
    entries.push(tarFile(`${guid}/pathname`, Buffer.from(relative)), tarFile(`${guid}/asset.meta`, metadata));
    if (metadata.toString('utf8').includes('folderAsset: yes')) {
      for (const child of readdirSync(asset, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
        if (child.isSymbolicLink()) throw new Error('Package sources must not be linked.');
        if (!child.name.endsWith('.meta')) visit(path.join(asset, child.name));
      }
    } else entries.push(tarFile(`${guid}/asset`, readFileSync(asset)));
  }
  visit(source); entries.push(Buffer.alloc(1024));
  const output = destination ? path.resolve(destination) : path.join(root, 'engine/unity/metroidvania-studio.unitypackage');
  mkdirSync(path.dirname(output), { recursive: true }); writeFileSync(output, gzipSync(Buffer.concat(entries), { level: 9 }));
  console.log(`Unity package: ${output} (${guids.size} assets)`); return output;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) buildPackage(process.argv[2]);
