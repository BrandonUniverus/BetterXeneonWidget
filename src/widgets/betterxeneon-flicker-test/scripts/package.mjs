// Minimal packager. No Vite, no bundler — index.html is pure static HTML/CSS
// with no imports, so we just stage it as-is and let the icuewidget CLI zip
// + validate. Patch version stamped with Unix seconds so iCUE treats every
// build as an upgrade (the CLI rejects pre-release suffixes).

import { execFileSync } from 'node:child_process';
import { copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const widgetDir = resolve(here, '..');
const repoRoot = resolve(widgetDir, '..', '..', '..');
const repoDist = join(repoRoot, 'dist');
// Must NOT start with a dot — icuewidget CLI silently skips dot-prefixed
// ancestors and writes empty zips.
const stagingRoot = join(repoRoot, 'build');

const cli =
  process.env.ICUEWIDGET_CLI ??
  join(process.env.LOCALAPPDATA ?? '', 'Programs', 'iCUEWidgetCLI', 'bin', 'icuewidget.exe');

if (!existsSync(cli)) {
  console.error(`icuewidget CLI not found at: ${cli}`);
  console.error('Install from tools/widgetbuilder-kit/icuewidget-0.2.3-windows-installer.exe');
  process.exit(1);
}

const manifest = JSON.parse(readFileSync(join(widgetDir, 'manifest.json'), 'utf8'));
const folderName = String(manifest.id).replaceAll(/[^a-zA-Z0-9_-]/g, '-');
const stagingDir = join(stagingRoot, folderName);

if (existsSync(stagingDir)) rmSync(stagingDir, { recursive: true, force: true });
mkdirSync(stagingDir, { recursive: true });

copyFileSync(join(widgetDir, 'index.html'), join(stagingDir, 'index.html'));
copyFileSync(join(widgetDir, 'translation.json'), join(stagingDir, 'translation.json'));
cpSync(join(widgetDir, 'resources'), join(stagingDir, 'resources'), { recursive: true });

const stamped = { ...manifest };
const [maj, min] = String(manifest.version).split('.');
stamped.version = `${maj}.${min}.${Math.floor(Date.now() / 1000)}`;
writeFileSync(join(stagingDir, 'manifest.json'), JSON.stringify(stamped, null, 2));

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
