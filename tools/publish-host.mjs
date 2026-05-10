// Publishes BetterXeneonWidget.Host as a self-contained, single-file Windows
// executable, then drops the install/uninstall/launcher scripts alongside it.
// Output: <repo>/dist/host/  (ready to copy to a target machine)

import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const projectFile = join(repoRoot, 'src', 'host', 'BetterXeneonWidget.Host', 'BetterXeneonWidget.Host.csproj');
const outputDir = join(repoRoot, 'dist', 'host');
const bundleSrc = join(here, 'host-bundle');

if (existsSync(outputDir)) rmSync(outputDir, { recursive: true, force: true });
mkdirSync(outputDir, { recursive: true });

console.log('--- dotnet publish (self-contained, single-file, win-x64) ---');
execFileSync('dotnet', [
  'publish', projectFile,
  '--configuration', 'Release',
  '--runtime', 'win-x64',
  '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  '-p:PublishTrimmed=false',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:DebugType=embedded',
  '--output', outputDir,
  '--nologo',
], { stdio: 'inherit' });

console.log('\n--- copying install/uninstall/launcher scripts ---');
const bundleFiles = ['install-host.ps1', 'uninstall-host.ps1', 'launcher.vbs', 'oauth-forward.vbs', 'README.md'];
for (const f of bundleFiles) {
  copyFileSync(join(bundleSrc, f), join(outputDir, f));
}

console.log('\n--- zipping bundle ---');
const zipPath = join(repoRoot, 'dist', 'BetterXeneonWidget-Host.zip');
if (existsSync(zipPath)) rmSync(zipPath);
execFileSync('powershell', [
  '-NoProfile',
  '-Command',
  `Compress-Archive -Path '${outputDir.replaceAll("'", "''")}\\*' -DestinationPath '${zipPath.replaceAll("'", "''")}' -Force`,
], { stdio: 'inherit' });

console.log(`\nPublished bundle: ${outputDir}`);
console.log(`Zipped:           ${zipPath}`);
console.log('\nTo install on a target machine:');
console.log('  1. Copy BetterXeneonWidget-Host.zip to the target PC');
console.log('  2. Right-click → Extract All');
console.log('  3. In the extracted folder, right-click install-host.ps1 → Run with PowerShell');
console.log('     (or: pwsh -ExecutionPolicy Bypass -File install-host.ps1)');
