// Builds a series of small bisect-test .icuewidget files, each isolating one
// suspect element of our complex widget. User installs each, reports which
// fails, and we narrow down the offending construct.

import { execFileSync } from 'node:child_process';
import { copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const stagingRoot = join(repoRoot, 'build', 'bisect');
const distDir = join(repoRoot, 'dist');
const cli =
  process.env.ICUEWIDGET_CLI ??
  join(process.env.LOCALAPPDATA ?? '', 'Programs', 'iCUEWidgetCLI', 'bin', 'icuewidget.exe');

const realWidgetDir = join(repoRoot, 'src', 'widgets', 'betterxeneon-audio-switcher');
const iconSvg = readFileSync(join(realWidgetDir, 'resources', 'icon.svg'), 'utf8');
const realIndex = readFileSync(join(realWidgetDir, 'dist', 'index.html'), 'utf8');

// Pull the bundled <script type="module">...</script> from the real build,
// so we can paste it into one of the bisect tests verbatim.
const moduleScriptMatch = realIndex.match(/<script type="module"[\s\S]*?<\/script>/);
if (!moduleScriptMatch) {
  console.error('Could not find <script type="module"> in the real widget dist. Run `npm run build:audio-switcher` first.');
  process.exit(1);
}
const bundledScript = moduleScriptMatch[0];

const baseManifest = (id, name) => ({
  author: 'Brandon Bachmeier',
  id,
  name,
  description: `Bisect test: ${name}`,
  version: '0.1.0',
  preview_icon: 'resources/icon.svg',
  min_framework_version: '1.0.0',
  os: [{ platform: 'windows' }],
  supported_devices: [{ type: 'dashboard_lcd' }],
});

const tests = [
  {
    name: 'TestMeta',
    id: 'com.betterxeneon.testmeta',
    html: `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>Test Meta</title>
<link rel="icon" type="image/svg+xml" href="resources/icon.svg">

<meta name="x-icue-property" content="pollIntervalMs"
      data-label="'Polling Interval'"
      data-type="slider"
      data-default="1500"
      data-min="500"
      data-max="5000"
      data-step="100">

<meta name="x-icue-property" content="textColor"
      data-label="'Text Color'"
      data-type="color"
      data-default="'#ffffff'">

<meta name="x-icue-property" content="useSystemAccent"
      data-label="'Use System Accent'"
      data-type="switch"
      data-default="true">

<script id="x-icue-groups" type="application/json">
[
  { "title": "'Settings'", "properties": ["pollIntervalMs", "textColor", "useSystemAccent"] }
]
</script>
</head>
<body>
<div style="display:flex;align-items:center;justify-content:center;width:100vw;height:100vh;background:#0a0a0c;color:#fff;font-family:sans-serif;font-size:5vmin">Test Meta</div>
</body>
</html>
`,
  },
  {
    name: 'TestScript',
    id: 'com.betterxeneon.testscript',
    html: `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>Test Script</title>
<link rel="icon" type="image/svg+xml" href="resources/icon.svg">
</head>
<body>
<div id="msg" style="display:flex;align-items:center;justify-content:center;width:100vw;height:100vh;background:#0a0a0c;color:#fff;font-family:sans-serif;font-size:5vmin">Test Script</div>
<script>
icueEvents = {
  onICUEInitialized: function () {
    document.getElementById('msg').textContent = 'Script ran';
  },
  onDataUpdated: function () {}
};
if (typeof iCUE_initialized !== 'undefined' && iCUE_initialized) {
  icueEvents.onICUEInitialized();
}
</script>
</body>
</html>
`,
  },
  {
    name: 'TestBundle',
    id: 'com.betterxeneon.testbundle',
    html: `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>Test Bundle</title>
<link rel="icon" type="image/svg+xml" href="resources/icon.svg">
</head>
<body>
<div id="app"></div>
${bundledScript}
</body>
</html>
`,
  },
];

if (existsSync(stagingRoot)) rmSync(stagingRoot, { recursive: true, force: true });
mkdirSync(stagingRoot, { recursive: true });
mkdirSync(distDir, { recursive: true });

for (const test of tests) {
  const stage = join(stagingRoot, test.name);
  mkdirSync(stage, { recursive: true });
  mkdirSync(join(stage, 'resources'), { recursive: true });

  writeFileSync(join(stage, 'index.html'), test.html);
  writeFileSync(join(stage, 'manifest.json'), JSON.stringify(baseManifest(test.id, test.name), null, 2) + '\n');
  writeFileSync(join(stage, 'translation.json'), '{}\n');
  writeFileSync(join(stage, 'resources', 'icon.svg'), iconSvg);

  const out = join(distDir, `${test.name}.icuewidget`);
  if (existsSync(out)) rmSync(out);
  console.log(`--- packaging ${test.name} ---`);
  execFileSync(cli, ['package', stage, '--output', out], { stdio: 'inherit' });
  console.log(`Output: ${out}\n`);
}

console.log('Three bisect packages written to dist/. Try each in iCUE and report which fail.');
