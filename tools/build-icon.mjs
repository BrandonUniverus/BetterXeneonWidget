// Renders src/installer/Resources/icon.svg to a multi-resolution Windows .ico.
// Run once after editing the SVG; check the resulting .ico into source control.
//
// Sizes follow Windows conventions: 16/24/32/48/64/128/256. The 256 entry is
// PNG-encoded inside the ICO (Vista+ supports this and it keeps the file small).

import sharp from 'sharp';
import toIco from 'png-to-ico';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const svgPath = join(repoRoot, 'src', 'installer', 'Resources', 'icon.svg');
const icoPath = join(repoRoot, 'src', 'installer', 'Resources', 'icon.ico');

const svg = readFileSync(svgPath);
const sizes = [16, 24, 32, 48, 64, 128, 256];

console.log(`Rendering ${svgPath} at ${sizes.length} sizes...`);
const pngs = await Promise.all(sizes.map(async size => {
  const buffer = await sharp(svg, { density: 384 })
    .resize(size, size)
    .png({ compressionLevel: 9 })
    .toBuffer();
  console.log(`  ${size}x${size}  ${buffer.length} bytes`);
  return buffer;
}));

console.log('Packing into .ico...');
const ico = await toIco(pngs);
writeFileSync(icoPath, ico);
console.log(`Wrote ${icoPath} (${ico.length} bytes)`);
