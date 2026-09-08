import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
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
  visit(source);
  const guides = new Map();
  const guideSources = ['KR', 'EN', 'JP', 'CN', 'TW'].map(language => [`INSTALL_${language}.txt`, `engine-packages/unity/INSTALL_${language}.txt`]);
  guideSources.push(['TRIGGERS.md', 'docs/objects-and-triggers.md']);
  for (const [name, sourcePath] of guideSources) {
    const relative = `Assets/MetroidvaniaStudioIntegration/${name}`;
    const bytes = Buffer.from(readFileSync(path.join(root, sourcePath), 'utf8').replaceAll('\r\n', '\n'));
    // Text guides live beside the archive; stable import metadata is generated only inside it.
    const guid = createHash('sha256').update(`metroidvania-studio:install-guide:${relative}`).digest('hex').slice(0, 32);
    if (guids.has(guid)) throw new Error('Duplicate install guide GUID.');
    guids.add(guid); guides.set(name, bytes);
    const metadata = Buffer.from(`fileFormatVersion: 2\nguid: ${guid}\nTextScriptImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n`);
    entries.push(tarFile(`${guid}/pathname`, Buffer.from(relative)), tarFile(`${guid}/asset.meta`, metadata), tarFile(`${guid}/asset`, bytes));
  }
  for (const [sourceName, name] of [['LICENSE', 'LICENSE.txt'], ['THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-NOTICES.txt']]) {
    const relative = `Assets/MetroidvaniaStudioIntegration/${name}`;
    const bytes = Buffer.from(readFileSync(path.join(root, sourceName), 'utf8').replaceAll('\r\n', '\n'));
    const guid = createHash('sha256').update(`metroidvania-studio:legal-notice:${relative}`).digest('hex').slice(0, 32);
    if (guids.has(guid)) throw new Error('Duplicate legal notice GUID.');
    guids.add(guid);
    const metadata = Buffer.from(`fileFormatVersion: 2\nguid: ${guid}\nTextScriptImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n`);
    entries.push(tarFile(`${guid}/pathname`, Buffer.from(relative)), tarFile(`${guid}/asset.meta`, metadata), tarFile(`${guid}/asset`, bytes));
  }
  entries.push(Buffer.alloc(1024));
  const output = destination ? path.resolve(destination) : path.join(root, 'engine-packages/unity/metroidvania-studio.unitypackage');
  mkdirSync(path.dirname(output), { recursive: true }); writeFileSync(output, gzipSync(Buffer.concat(entries), { level: 9 }));
  for (const [name, bytes] of guides) writeFileSync(path.join(path.dirname(output), name), bytes);
  console.log(`Unity package: ${output} (${guids.size} assets)`); return output;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) buildPackage(process.argv[2]);
