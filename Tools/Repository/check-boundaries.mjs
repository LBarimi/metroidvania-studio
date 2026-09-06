import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const engineAsset = /\.(?:asmdef|asmref|meta|unity|prefab|asset|mat|anim|controller|inputactions|uasset|umap|uproject|uplugin|tscn|tres|gd)$/i;
const engineRoot = /^(?:Assets|Packages|ProjectSettings|Library|Temp|UserSettings)(?:\/|$)/i;
const sourceFile = /\.(?:cs|csproj|props|targets|ts|js|mjs|cjs|cpp|h|hpp|ps1|py)$/i;
const engineCode = /\b(?:using|namespace)\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:UnityEngine|UnityEditor|Godot)\b|\b(?:UnityEngine|UnityEditor|Godot)\.[A-Za-z_]|:\s*(?:[M]onoBehaviour|[S]criptableObject)\b|#\s*include\s*[<"](?:CoreMinimal\.h|Engine\/|GameFramework\/|UObject\/)/;
const engineReference = /<(?:Reference|PackageReference)\b[^>]*\bInclude\s*=\s*["'](?:Unity|Godot)[^"']*["']/i;
const windowsPath = /(?:^|[^A-Za-z0-9])([A-Za-z]:[\\/][^\s"'<>`]+)/;
const homePath = /\/(?:Users|home|mnt|media|Volumes)\/[^/\\\s"'<>]+\//;
const uncPath = /(?:^|[^\\])\\\\[A-Za-z0-9_.-]+\\[A-Za-z0-9_.-]+/;
const absoluteAsset = /^(?:[A-Za-z]:|\/|\\)/;

function decode(bytes) {
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return bytes.subarray(2).toString('utf16le');
  if (bytes[0] === 0xfe && bytes[1] === 0xff) {
    const value = Buffer.from(bytes.subarray(2));
    for (let i = 0; i + 1 < value.length; i += 2) [value[i], value[i + 1]] = [value[i + 1], value[i]];
    return value.toString('utf16le');
  }
  return bytes.toString('utf8');
}
export function inspectBoundaries(files) {
  const issues = [];
  for (const file of files) {
    const name = file.name.replaceAll('\\', '/');
    const text = decode(file.bytes);
    const report = (rule, location = 'content') => issues.push({ path: file.name, location, rule });
    const integration = name.startsWith('engine/unity/');
    if (!integration && (engineAsset.test(name) || engineRoot.test(name))) report('engine-file', 'filename');
    if (/\.map\.json$/i.test(name) && !name.startsWith('Samples/Maps/') && !/(?:^|\/)Tests\/Fixtures\//.test(name))
      report('private-map-location', 'filename');
    if (windowsPath.test(text) || homePath.test(text) || uncPath.test(text)) report('machine-path');
    if (!integration && sourceFile.test(name) && (engineCode.test(text) || engineReference.test(text))) report('engine-code');
    if (/\.(?:csproj|props|targets)$/i.test(name)) {
      for (const match of text.matchAll(/<(?:Compile|ProjectReference|EmbeddedResource|Content|None)\b[^>]*\b(?:Include|Update)\s*=\s*["']([^"']+)["']/g)) {
        for (const item of match[1].split(';')) {
          if (item.includes('$(') || item.includes('%(')) continue;
          const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(name), item.replaceAll('\\', '/')));
          if (absoluteAsset.test(item) || resolved === '..' || resolved.startsWith('../')) report('external-source-link');
          if (engineRoot.test(resolved) || !integration && resolved.startsWith('engine/')) report('engine-source-link');
        }
      }
    }
    if (name.startsWith('Samples/') && name.endsWith('.json')) {
      try {
        const document = JSON.parse(text);
        if (document.projectPath) report('private-catalog-root');
        const visit = value => {
          if (!value || typeof value !== 'object') return;
          if (typeof value.asset === 'string' && (absoluteAsset.test(value.asset) || /^Assets\//.test(value.asset) || value.asset.split(/[\\/]/).includes('..')))
            report('private-catalog-asset');
          if (typeof value.id === 'string' && (/^[a-f0-9]{32}$/i.test(value.id) || /^decal:[a-f0-9]{32}:/i.test(value.id)))
            report('engine-resource-id');
          for (const child of Object.values(value)) if (child && typeof child === 'object') visit(child);
        };
        visit(document);
      } catch { report('invalid-sample-json'); }
    }
  }
  return issues;
}
export async function main(args = process.argv.slice(2)) {
  const { workingFiles, stagedFiles } = await import('./check-text.mjs');
  const files = args.length === 0 ? workingFiles()
    : args.length === 1 && args[0] === '--staged' ? stagedFiles()
    : args.length === 2 && args[0] === '--message' ? [{ name: 'commit message', bytes: readFileSync(args[1]) }]
    : (() => { throw new Error('Usage: check-boundaries.mjs [--staged | --message <path>]'); })();
  const issues = inspectBoundaries(files);
  if (issues.length) {
    console.error('Repository boundary check failed.');
    for (const issue of issues.slice(0, 30)) console.error(`${issue.path}: ${issue.location} (${issue.rule})`);
    return 1;
  }
  console.log(`Repository boundary check passed (${files.length} files).`);
  return 0;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then(code => { process.exitCode = code; }).catch(error => { console.error(error.message); process.exitCode = 1; });
}
