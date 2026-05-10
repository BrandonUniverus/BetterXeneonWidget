// Build orchestrator: runs `vite build`, copies the iCUE-required sidecar
// files (manifest.json, translation.json, resources/), stamps the manifest
// patch version with a build timestamp so iCUE treats each install as an
// upgrade, and runs the `icuewidget` CLI to validate and package. Produces
// <id>.icuewidget at repo root /dist.

import { execFileSync } from 'node:child_process';
import { copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const widgetDir = resolve(here, '..');
const buildDir = join(widgetDir, 'dist');
const repoRoot = resolve(widgetDir, '..', '..', '..');
const repoDist = join(repoRoot, 'dist');
// Must NOT start with a dot — the icuewidget CLI silently filters paths whose
// ancestors begin with `.` and writes an empty zip. See memory/reference_icuewidget_cli_quirks.md.
const stagingRoot = join(repoRoot, 'build');

const cli =
  process.env.ICUEWIDGET_CLI ??
  join(process.env.LOCALAPPDATA ?? '', 'Programs', 'iCUEWidgetCLI', 'bin', 'icuewidget.exe');

console.log('--- vite build ---');
execFileSync('npx', ['vite', 'build'], { cwd: widgetDir, stdio: 'inherit', shell: true });

if (!existsSync(buildDir)) {
  console.error('vite build did not produce a dist/ directory.');
  process.exit(1);
}
if (!existsSync(cli)) {
  console.error(`icuewidget CLI not found at: ${cli}`);
  console.error('Install from tools/widgetbuilder-kit/icuewidget-0.2.3-windows-installer.exe');
  console.error('or set ICUEWIDGET_CLI to the absolute path.');
  process.exit(1);
}

const manifest = JSON.parse(readFileSync(join(widgetDir, 'manifest.json'), 'utf8'));
const folderName = String(manifest.id).replaceAll(/[^a-zA-Z0-9_-]/g, '-');
const stagingDir = join(stagingRoot, folderName);

if (existsSync(stagingDir)) rmSync(stagingDir, { recursive: true, force: true });
mkdirSync(stagingDir, { recursive: true });

cpSync(buildDir, stagingDir, { recursive: true });

// Stamp the staged manifest with a build-unique patch version. iCUE's
// validator only accepts strict X.Y.Z semver, so we burn the patch number
// with the current Unix timestamp to keep it monotonically increasing.
const stamped = { ...manifest };
const [maj, min] = String(manifest.version).split('.');
const patch = Math.floor(Date.now() / 1000);
stamped.version = `${maj}.${min}.${patch}`;
writeFileSync(join(stagingDir, 'manifest.json'), JSON.stringify(stamped, null, 2));

const translation = join(widgetDir, 'translation.json');
if (existsSync(translation)) copyFileSync(translation, join(stagingDir, 'translation.json'));

const resources = join(widgetDir, 'resources');
if (existsSync(resources)) cpSync(resources, join(stagingDir, 'resources'), { recursive: true });

// Vite rewrites <link rel="icon" href="resources/icon.svg"> to a hashed name and
// emits the file at dist root. iCUE reads that <link> for the widget selector
// icon and the path must resolve, so we restore the canonical resources/icon.svg
// reference and drop the orphaned hashed copy.
const indexHtmlPath = join(stagingDir, 'index.html');
if (existsSync(indexHtmlPath)) {
  const html = readFileSync(indexHtmlPath, 'utf8');
  const fixed = html.replaceAll(/href="\.?\/?icon-[A-Za-z0-9_-]+\.svg"/g, 'href="resources/icon.svg"');
  if (fixed !== html) writeFileSync(indexHtmlPath, fixed);
}
for (const entry of readdirSync(stagingDir)) {
  if (/^icon-[A-Za-z0-9_-]+\.svg$/.test(entry)) {
    rmSync(join(stagingDir, entry));
  }
}

console.log('--- icuewidget validate ---');
execFileSync(cli, ['validate', stagingDir], { stdio: 'inherit' });

mkdirSync(repoDist, { recursive: true });
const outputPath = join(repoDist, `${folderName}.icuewidget`);
if (existsSync(outputPath)) rmSync(outputPath);

console.log('\n--- icuewidget package ---');
execFileSync(cli, ['package', stagingDir, '--output', outputPath], { stdio: 'inherit' });

if (!existsSync(outputPath)) {
  console.error(`CLI reported success but ${outputPath} is missing.`);
  process.exit(2);
}
console.log(`\nPackaged: ${outputPath}`);
console.log('Double-click to install in iCUE.');
