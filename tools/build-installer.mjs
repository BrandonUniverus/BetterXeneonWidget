// Builds the single-exe installer at dist/BetterXeneonWidget-Setup.exe.
//
// Pipeline:
//   1. Publish host (single-file, self-contained, win-x64) → dist/host/
//   2. Package both widgets via icuewidget CLI                → dist/*.icuewidget
//   3. Zip those artifacts into payload.zip                    → src/installer/Resources/payload.zip
//   4. dotnet publish the installer (single-file, self-contained, win-x64)
//      with the payload embedded as a resource
//   5. Move the resulting exe to dist/BetterXeneonWidget-Setup.exe and clean up

import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, rmSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const installerDir = join(repoRoot, 'src', 'installer');
const installerProj = join(installerDir, 'BetterXeneonWidget.Installer.csproj');
const installerResourcesDir = join(installerDir, 'Resources');
const distDir = join(repoRoot, 'dist');
const distHost = join(distDir, 'host');

function run(cmd, args, opts = {}) {
  execFileSync(cmd, args, { stdio: 'inherit', ...opts });
}

function step(label) {
  console.log(`\n--- ${label} ---`);
}

step('1/5 Publishing host');
run('node', [join(here, 'publish-host.mjs')]);

step('2/5 Packaging widgets');
run('npm', ['run', 'package:audio-switcher'], { shell: true });
run('npm', ['run', 'package:media'], { shell: true });

step('3/5 Building payload.zip');
mkdirSync(installerResourcesDir, { recursive: true });
const payloadZip = join(installerResourcesDir, 'payload.zip');
if (existsSync(payloadZip)) rmSync(payloadZip);

const payloadFiles = [
  join(distHost, 'BetterXeneonWidget.Host.exe'),
  join(distHost, 'launcher.vbs'),
  join(distHost, 'oauth-forward.vbs'),
  join(distHost, 'appsettings.json'),
  join(distDir, 'com-betterxeneon-audioswitcher.icuewidget'),
  join(distDir, 'com-betterxeneon-media.icuewidget'),
];
for (const f of payloadFiles) {
  if (!existsSync(f)) throw new Error(`Missing artifact for payload: ${f}`);
}

// Compress-Archive can't take a flat list directly without -Path globs,
// so stage them into a clean dir first, then zip the whole thing.
const stagingDir = join(repoRoot, 'build', 'installer-payload');
if (existsSync(stagingDir)) rmSync(stagingDir, { recursive: true, force: true });
mkdirSync(stagingDir, { recursive: true });
for (const f of payloadFiles) {
  copyFileSync(f, join(stagingDir, f.split(/[/\\]/).pop()));
}
run('powershell', [
  '-NoProfile', '-Command',
  `Compress-Archive -Path '${stagingDir.replaceAll("'", "''")}\\*' -DestinationPath '${payloadZip.replaceAll("'", "''")}' -Force`,
]);
console.log(`payload.zip: ${(statSync(payloadZip).size / (1024 * 1024)).toFixed(1)} MB`);

step('4/5 Publishing installer');
const installerOut = join(distDir, 'installer-build');
if (existsSync(installerOut)) rmSync(installerOut, { recursive: true, force: true });
run('dotnet', [
  'publish', installerProj,
  '--configuration', 'Release',
  '--runtime', 'win-x64',
  '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:DebugType=embedded',
  '--output', installerOut,
  '--nologo',
]);

step('5/5 Finalizing');
const finalExe = join(distDir, 'BetterXeneonWidget-Setup.exe');
const builtExe = join(installerOut, 'BetterXeneonWidget.Installer.exe');
if (!existsSync(builtExe)) throw new Error(`Installer build did not produce ${builtExe}`);
if (existsSync(finalExe)) rmSync(finalExe);
copyFileSync(builtExe, finalExe);
rmSync(installerOut, { recursive: true, force: true });
rmSync(stagingDir, { recursive: true, force: true });
rmSync(payloadZip);

const sizeMB = (statSync(finalExe).size / (1024 * 1024)).toFixed(1);
console.log(`\nFinal: ${finalExe} (${sizeMB} MB)`);
console.log('Distribute this single file. End user just double-clicks it.');
