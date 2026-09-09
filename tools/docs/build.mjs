import { readFileSync, writeFileSync, mkdirSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const escape = text => String(text).replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));
const slug = text => text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
export function buildDocs(output = path.join(root, 'builds/docs'), { siteUrl } = {}) {
  if (siteUrl) {
    const url = new URL(siteUrl);
    if (url.protocol !== 'https:' || url.username || url.password || url.search || url.hash) throw new Error('Expected an HTTPS documentation base URL.');
    siteUrl = url.href.replace(/\/?$/, '/');
  }
  mkdirSync(output, { recursive: true });
  const sources = [];
  function walk(folder) {
    for (const file of readdirSync(path.join(root, folder), { withFileTypes: true })) {
      if (file.isSymbolicLink()) throw new Error('Documentation sources cannot be links.');
      const name = folder + '/' + file.name;
      // Public-site downloads are generated copies, not documentation sources.
      if (name === 'docs/assets') continue;
      if (file.isDirectory()) walk(name);
      else if (/\.(md|lua|json)$/.test(file.name)) sources.push(name);
    }
  }
  walk('docs');
  sources.push('metroidvania-studio/contracts/FORMAT.md', 'metroidvania-studio/contracts/map-format-v2.schema.json');
  const outputName = name => (siteUrl && !name.endsWith('.md') ? 'assets/' : '') + (name === 'docs/index.md' ? 'index.html' : name.replace(/^docs\//, '').replace(/\//g, '--').replace(/\.md$/, '.html').toLowerCase());
  const names = new Map(sources.map(name => [name, outputName(name)]));
  function link(href, from) {
    if (/^https?:\/\//i.test(href) || href.startsWith('#')) return escape(href);
    const [file, hash] = href.split('#');
    const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(from), file));
    if (!names.has(resolved)) throw new Error(`Unknown documentation link: ${from} -> ${href}`);
    return escape(names.get(resolved) + (hash ? '#' + hash : ''));
  }
  function inline(text, from) {
    const tokens = [];
    const token = html => { const n = tokens.push(html) - 1; return `\u0001${n}\u0002`; };
    text = text.replace(/`([^`]+)`/g, (_, code) => token('<code>' + escape(code) + '</code>'));
    text = text.replace(/\[([^\]]+)\]\(([^\s)]+)\)/g, (_, label, href) => token(`<a href="${link(href, from)}">${escape(label)}</a>`));
    return escape(text).replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>').replace(/\u0001(\d+)\u0002/g, (_, n) => tokens[Number(n)]);
  }
  function markdown(source, from) {
    const lines = source.replaceAll('\r\n', '\n').split('\n');
    const blocks = [], toc = [], counts = new Map();
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (!line.trim()) { i++; continue; }
      if (line.startsWith('```')) {
        const lang = line.slice(3).trim(), code = []; i++;
        while (i < lines.length && !lines[i].startsWith('```')) code.push(lines[i++]);
        if (i === lines.length) throw new Error('Unclosed code block: ' + from);
        i++;
        blocks.push(`<div class="code-block"><div class="code-label"><span>${escape(lang || 'text')}</span><button type="button" class="copy">Copy</button></div><pre><code>${escape(code.join('\n'))}</code></pre></div>`); continue;
      }
      const heading = /^(#{1,6}) (.+)$/.exec(line);
      if (heading) {
        const base = slug(heading[2]), count = counts.get(base) || 0; counts.set(base, count + 1);
        const id = base + (count ? '-' + count : '');
        blocks.push(`<h${heading[1].length} id="${id}">${inline(heading[2], from)}</h${heading[1].length}>`);
        if (heading[1].length === 2) toc.push({ id, text: heading[2].replace(/`/g, '') });
        i++; continue;
      }
      if (line.startsWith('|') && /^\|[\s:|-]+\|\s*$/.test(lines[i + 1] || '')) {
        const cells = row => row.trim().replace(/^\||\|$/g, '').split('|').map(x => x.trim());
        let html = '<div class="table-scroll"><table><thead><tr>' + cells(line).map(x => '<th>' + inline(x, from) + '</th>').join('') + '</tr></thead><tbody>'; i += 2;
        while (i < lines.length && lines[i].startsWith('|')) html += '<tr>' + cells(lines[i++]).map(x => '<td>' + inline(x, from) + '</td>').join('') + '</tr>';
        blocks.push(html + '</tbody></table></div>'); continue;
      }
      if (/^(?:- |\d+\. )/.test(line)) {
        const ordered = /^\d/.test(line), tag = ordered ? 'ol' : 'ul', items = [];
        while (i < lines.length && /^(?:- |\d+\. )/.test(lines[i])) {
          let item = lines[i++].replace(/^(?:- |\d+\. )/, '');
          while (i < lines.length && /^  +\S/.test(lines[i])) item += ' ' + lines[i++].trim();
          items.push('<li>' + inline(item, from) + '</li>');
        }
        blocks.push(`<${tag}>${items.join('')}</${tag}>`); continue;
      }
      const paragraph = [line]; i++;
      while (i < lines.length && lines[i].trim() && !/^(?:#|```|\||- |\d+\. )/.test(lines[i])) paragraph.push(lines[i++]);
      blocks.push('<p>' + inline(paragraph.join(' '), from) + '</p>');
    }
    return { html: blocks.join('\n'), toc };
  }
  const pages = sources.filter(name => name.endsWith('.md')).map(name => {
    const source = readFileSync(path.join(root, name), 'utf8'), title = /^# (.+)$/m.exec(source)?.[1]?.trim() || name;
    const paragraph = source.replaceAll('\r\n', '\n').split(/\n\s*\n/).find(block => !/^[#|`>\s]|^(?:- |\d+\. )/.test(block)) || title;
    const plain = paragraph.replace(/\[([^\]]+)\]\([^)]+\)/g, '$1').replace(/[*`]/g, '').replace(/\s+/g, ' ').trim();
    const description = plain.length > 180 ? plain.slice(0, 177).replace(/\s+\S*$/, '') + '…' : plain;
    return { name, title, description, source, href: names.get(name), ...markdown(source, name) };
  });
  const groups = [['Start here', ['docs/index.md', 'docs/sample-world.md']], ['Map editing', ['docs/room-layout.md', 'docs/tilesets.md', 'docs/objects-and-triggers.md', 'docs/room-restructuring.md', 'docs/minimap.md', 'docs/game-preview.md', 'docs/textures.md']], ['Automation', ['docs/cli/release-downloads.md', 'docs/cli/quick-start.md', 'docs/mcp/setup.md']], ['Lua scripts', ['docs/scripting/quick-start.md', 'docs/scripting/api-reference.md', 'docs/scripting/execution-limits.md']], ['API reference', ['docs/api/index.md', 'docs/api/operations.md', 'docs/api/queries.md', 'docs/api/live-api.md']], ['Reference & distribution', ['docs/cli/commands.md', 'docs/mcp/tools.md', 'metroidvania-studio/contracts/FORMAT.md', 'docs/distribution/local-package.md', 'docs/distribution/publication.md', 'docs/validation.md']]];
  const assigned = new Set(groups.flatMap(([, files]) => files));
  const other = pages.filter(p => !assigned.has(p.name)).map(p => p.name); if (other.length) groups.push(['More guides', other]);
  const version = JSON.parse(readFileSync(path.join(root, 'version.json'))).version;
  for (const page of pages) {
    const nav = groups.map(([label, files]) => '<section><h2>' + label + '</h2>' + files.map(name => {
      const entry = pages.find(p => p.name === name); if (!entry) return '';
      return `<a href="${entry.href}"${entry === page ? ' aria-current="page"' : ''}>${escape(entry.title)}</a>`;
    }).join('') + '</section>').join('');
    const canonical = siteUrl ? new URL(page.href === 'index.html' ? '' : page.href, siteUrl).href : '';
    const metadata = canonical ? `<link rel="canonical" href="${escape(canonical)}"><meta property="og:type" content="website"><meta property="og:title" content="${escape(page.title)}"><meta property="og:description" content="${escape(page.description)}"><meta property="og:url" content="${escape(canonical)}"><meta property="og:site_name" content="Metroidvania Studio">` : '';
    const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="description" content="${escape(page.description)}">${metadata}<title>${escape(page.title)} · Metroidvania Studio</title><link rel="icon" href="studio-icon.svg"><link rel="stylesheet" href="docs.css"><script src="search-index.js" defer></script><script src="docs.js" defer></script></head><body><a class="skip" href="#content">Skip to content</a><header><a class="brand" href="index.html"><img src="studio-icon.svg" width="28" height="28" alt="">Metroidvania Studio <span>Docs</span></a><span class="version">${escape(version)}</span><a href="https://github.com/LBarimi/metroidvania-studio">GitHub</a></header><div class="layout"><aside class="sidebar"><label for="search">Search documentation</label><input id="search" type="search" placeholder="Rooms, autotiling, Lua…" autocomplete="off"><div id="results" hidden aria-live="polite"></div><nav aria-label="Documentation">${nav}</nav></aside><main id="content">${page.html}<footer>Metroidvania Studio ${escape(version)} · ${siteUrl ? 'Offline guides are available through Help → Documentation in the studio.' : 'Documentation works offline.'}</footer></main><aside class="toc"><span>On this page</span>${page.toc.map(h => `<a href="#${h.id}">${escape(h.text)}</a>`).join('')}</aside></div></body></html>`;
    writeFileSync(path.join(output, page.href), html);
  }
  const index = pages.map(p => ({ title: p.title, href: p.href, text: p.source.replace(/[#*`|]/g, '').replace(/\s+/g, ' ').slice(0, 24000) }));
  writeFileSync(path.join(output, 'search-index.js'), 'window.studioDocs = ' + JSON.stringify(index).replaceAll('<', '\\u003c') + ';\n');
  for (const name of sources.filter(name => !name.endsWith('.md'))) {
    let bytes = readFileSync(path.join(root, name));
    if (name.endsWith('http.openapi.json')) bytes = Buffer.from(bytes.toString('utf8').replaceAll('automation.schema.json', 'api--automation.schema.json'));
    const target = path.join(output, names.get(name));
    mkdirSync(path.dirname(target), { recursive: true });
    writeFileSync(target, bytes);
  }
  for (const name of ['docs.css', 'docs.js']) writeFileSync(path.join(output, name), readFileSync(path.join(root, 'tools/docs', name)));
  writeFileSync(path.join(output, 'studio-icon.svg'), readFileSync(path.join(root, 'metroidvania-studio/web/studio-icon.svg')));
  if (siteUrl) {
    writeFileSync(path.join(output, '.nojekyll'), '');
    const locations = pages.map(page => '<url><loc>' + escape(new URL(page.href === 'index.html' ? '' : page.href, siteUrl).href) + '</loc></url>');
    writeFileSync(path.join(output, 'sitemap.xml'), '<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">' + locations.join('') + '</urlset>\n');
  }
  console.log(`Built ${pages.length} ${siteUrl ? 'public' : 'offline'} documentation pages.`);
  return pages;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) buildDocs(process.argv[2] ? path.resolve(process.argv[2]) : undefined);
