import { mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

// Downloaded builds ship portable guides; the public homepage belongs to the website.
export function copyDocSources(output) {
  function walk(folder = '') {
    for (const entry of readdirSync(path.join(root, 'docs', folder), { withFileTypes: true })) {
      const name = path.posix.join(folder, entry.name);
      if (entry.isSymbolicLink()) throw new Error('Documentation sources cannot be links.');
      if (name === 'assets') continue;
      if (entry.isDirectory()) { walk(name); continue; }
      if (!/\.(md|lua|json)$/.test(name)) continue;
      const source = name === 'index.md' ? 'guides.md' : name;
      const target = path.join(output, name);
      mkdirSync(path.dirname(target), { recursive: true });
      const contents = readFileSync(path.join(root, 'docs', source));
      // Packaged Markdown keeps the walkthrough, while its demonstration GIFs ship on the public site.
      writeFileSync(target, name.endsWith('.md')
        ? contents.toString('utf8').replace(/^!\[[^\]\r\n]*\]\(assets\/[\w-]+\.gif\)\r?\n(?:\r?\n)?/gm, '')
        : contents);
    }
  }
  walk();
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (!process.argv[2]) throw new Error('Provide the documentation output directory.');
  copyDocSources(path.resolve(process.argv[2]));
}
