// Round 2 of bisecting: each test combines elements that pass individually,
// to find which combination breaks.

import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const stagingRoot = join(repoRoot, 'build', 'bisect2');
const distDir = join(repoRoot, 'dist');
const cli =
  process.env.ICUEWIDGET_CLI ??
  join(process.env.LOCALAPPDATA ?? '', 'Programs', 'iCUEWidgetCLI', 'bin', 'icuewidget.exe');

const realWidgetDir = join(repoRoot, 'src', 'widgets', 'betterxeneon-audio-switcher');
const iconSvg = readFileSync(join(realWidgetDir, 'resources', 'icon.svg'), 'utf8');
const realIndex = readFileSync(join(realWidgetDir, 'dist', 'index.html'), 'utf8');
const bundle = realIndex.match(/<script type="module"[\s\S]*?<\/script>/)?.[0];
if (!bundle) throw new Error('Could not extract module script from real widget dist.');

const baseManifest = (id, name) => ({
  author: 'Brandon Bachmeier',
  id,
  name,
  description: `Bisect test 2: ${name}`,
  version: '0.1.0',
  preview_icon: 'resources/icon.svg',
  min_framework_version: '1.0.0',
  os: [{ platform: 'windows' }],
  supported_devices: [{ type: 'dashboard_lcd' }],
  interactive: true,
});

// Mimics TestMeta (3 properties: slider, color, switch) + the bundle.
const COMBO_SIMPLE = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>Combo Simple</title>
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
<div id="app"></div>
${bundle}
</body>
</html>
`;

// All 6 of complex's meta tags + groups + bundle. Mirrors our actual widget exactly.
const COMBO_FULL = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Combo Full</title>
<link rel="icon" type="image/svg+xml" href="resources/icon.svg">

<meta name="x-icue-property" content="pollIntervalMs"
      data-label="'Polling Interval (ms)'"
      data-type="slider"
      data-default="1500"
      data-min="500"
      data-max="5000"
      data-step="100">

<meta name="x-icue-property" content="useSystemAccent"
      data-label="'Use Windows Accent Color'"
      data-type="switch"
      data-default="true">

<meta name="x-icue-property" content="textColor"
      data-label="'Text Color'"
      data-type="color"
      data-default="'#ffffff'">

<meta name="x-icue-property" content="accentColor"
      data-label="'Custom Accent Color'"
      data-type="color"
      data-default="'#0078d4'">

<meta name="x-icue-property" content="backgroundColor"
      data-label="'Background Color'"
      data-type="color"
      data-default="'#0a0a0c'">

<meta name="x-icue-property" content="transparency"
      data-label="'Background Transparency'"
      data-type="slider"
      data-default="100"
      data-min="0"
      data-max="100"
      data-step="1">

<script id="x-icue-groups" type="application/json">
[
  { "title": "'Behavior'", "properties": ["pollIntervalMs"] },
  { "title": "'Appearance'", "properties": ["useSystemAccent", "textColor", "accentColor", "backgroundColor", "transparency"] }
]
</script>
</head>
<body>
<div id="app"></div>
${bundle}
</body>
</html>
`;

const tests = [
  { name: 'ComboSimple', id: 'com.betterxeneon.combosimple', html: COMBO_SIMPLE },
  { name: 'ComboFull',   id: 'com.betterxeneon.combofull',   html: COMBO_FULL },
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

console.log('Try ComboSimple and ComboFull. Whichever fails, paste back the iCUE log.');
