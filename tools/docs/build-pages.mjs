import { copyFileSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildDocs } from './build.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const destination = path.join(root, 'docs');
const manifest = path.join(destination, '.site-files');
const cache = path.join(root, 'builds/docs-pages');
mkdirSync(cache, { recursive: true });
const output = mkdtempSync(path.join(cache, 'site-'));
buildDocs(output, { siteUrl: 'https://lbarimi.github.io/metroidvania-studio/' });

function validOutput(name) {
  return /^(?:[a-z0-9-]+\.(?:html|css|js|svg|xml)|\.nojekyll|assets\/[a-z0-9.-]+\.(?:json|lua|gif))$/.test(name);
}
const previous = existsSync(manifest) ? readFileSync(manifest, 'utf8').split(/\r?\n/).filter(Boolean) : [];
const files = readdirSync(output, { recursive: true }).filter(name => lstatSync(path.join(output, name)).isFile()).map(name => name.replaceAll('\\', '/')).sort();
if ([...previous, ...files].some(name => !validOutput(name))) throw new Error('Unexpected generated documentation path.');

// Validate all destinations before copying or removing only recorded generated files.
for (const name of new Set([...previous, ...files])) {
  const target = path.resolve(destination, name);
  if (!target.startsWith(destination + path.sep)) throw new Error('Documentation output must stay inside docs.');
  for (let current = target; current !== destination; current = path.dirname(current)) {
    if (existsSync(current) && lstatSync(current).isSymbolicLink()) throw new Error('Documentation output cannot use links.');
  }
  if (existsSync(target) && !previous.includes(name)) throw new Error('Unmanaged documentation file already exists: ' + name);
}
for (const name of files) {
  const target = path.join(destination, name);
  mkdirSync(path.dirname(target), { recursive: true });
  copyFileSync(path.join(output, name), target);
}
for (const name of previous) {
  const target = path.join(destination, name);
  if (!files.includes(name) && existsSync(target)) unlinkSync(target);
}
writeFileSync(manifest, files.join('\n') + '\n');
console.log('Prepared docs/ for GitHub Pages. Review and commit the files; pushing main publishes them.');
