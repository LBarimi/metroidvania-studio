import { readdir, mkdir, readFile, writeFile, copyFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const root = path.dirname(fileURLToPath(import.meta.url));
const check = process.argv.includes('--check-contracts');
const target = process.argv.slice(2).find(value => !value.startsWith('--'));
const outputRoot = target ? path.resolve(target) : path.join(root, 'dist');
const textCheck = spawnSync(process.execPath, [path.join(root, '../tools/repository/check-text.mjs')],
  { stdio: 'inherit', windowsHide: true });
if (textCheck.error) throw textCheck.error;
if (textCheck.status !== 0) process.exit(textCheck.status || 1);
const generated = spawnSync(process.env.METROIDVANIA_STUDIO_DOTNET || 'dotnet', ['run', '--project', path.join(root, 'contracts/MetroidvaniaStudio.Contracts.csproj'),
  '--configuration', 'Debug', '--no-launch-profile', '--', path.join(root, 'web/contracts.generated.ts'), ...(check ? ['--check'] : [])],
  { stdio: 'inherit', windowsHide: true, env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
if (generated.error) throw generated.error;
if (generated.status !== 0) process.exit(generated.status || 1);
if (check) process.exit(0);
await mkdir(outputRoot, { recursive: true });
for (const name of await readdir(path.join(root, 'web'))) {
  if (name.endsWith('.ts')) {
    const source = await readFile(path.join(root, 'web', name), 'utf8');
    const output = stripTypeScriptTypes(source, { mode: 'strip' });
    await writeFile(path.join(outputRoot, name.replace(/\.ts$/, '.js')), output);
  } else if (/\.(html|css|svg)$/.test(name)) {
    await copyFile(path.join(root, 'web', name), path.join(outputRoot, name));
  }
}
const version = JSON.parse(await readFile(path.join(root, '../version.json'), 'utf8')).version;
const revision = spawnSync('git', ['rev-parse', '--short=8', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true });
await writeFile(path.join(outputRoot, 'build-info.json'), JSON.stringify({ version, revision: revision.status === 0 ? revision.stdout.trim() : '', builtAt: new Date().toISOString() }));
console.log(`Built ${outputRoot} (local modules, no downloaded dependencies).`);
