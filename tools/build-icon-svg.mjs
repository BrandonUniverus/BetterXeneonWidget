// Generates the installer icon SVG: a circular dark background with a
// hexagonal network of dots/lines, colored nodes overlaid, and a hollow
// hexagon at the center. Themed for "iCUE widget" — the hex pattern echoes
// Corsair's existing hex aesthetic without lifting their actual logo.
//
// Run after editing parameters here, then run `npm run build:icon` to
// rasterize to .ico.

import { writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..');
const outPath = join(repoRoot, 'src', 'installer', 'Resources', 'icon.svg');

const W = 256;
const C = W / 2;            // center
const R = 122;              // outer dark-circle radius (icon perimeter)
const DX = 22;              // hex grid column spacing
const DY = DX * Math.sqrt(3) / 2;
const DOT_R = 1.7;          // grid dot radius
const NODE_R = 6.5;         // colored node radius
const GRID_INSET = 8;       // shrink grid so it doesn't touch the perimeter
const GRID_LIMIT_R2 = (R - GRID_INSET) ** 2;

// --- Hex grid generation ---------------------------------------------------
const positions = [];
for (let r = -7; r <= 7; r++) {
  const y = C + r * DY;
  const offsetX = (Math.abs(r) % 2 === 1) ? DX / 2 : 0;
  for (let c = -8; c <= 8; c++) {
    const x = C + offsetX + c * DX;
    const dx = x - C, dy = y - C;
    if (dx * dx + dy * dy <= GRID_LIMIT_R2) {
      positions.push({ x, y });
    }
  }
}

// Lines connect each dot to its 3 forward neighbors (avoids duplicates).
const lines = [];
for (const p of positions) {
  for (const a of [0, 60, 120]) {
    const rad = (a * Math.PI) / 180;
    const nx = p.x + DX * Math.cos(rad);
    const ny = p.y + DX * Math.sin(rad);
    const neighbor = positions.find(q =>
      Math.abs(q.x - nx) < 0.5 && Math.abs(q.y - ny) < 0.5);
    if (neighbor) lines.push({ x1: p.x, y1: p.y, x2: neighbor.x, y2: neighbor.y });
  }
}

// --- Colored nodes ---------------------------------------------------------
// Positioned on actual grid points so they read as "highlighted" cells.
// Mirrors the rough pattern from the reference image: a diagonal stripe of
// nodes from upper-right to lower-left around the central hex.
const nodes = [
  { color: '#ff3b5c', x: C + 2 * DX,        y: C - 3 * DY },           // red,    upper-right
  { color: '#ffcc1f', x: C + DX + DX/2,     y: C - 2 * DY },           // yellow  (offset row)
  { color: '#33d97a', x: C + DX/2,          y: C - DY },               // green,  just above center
  { color: '#3aafff', x: C - 2 * DX,        y: C },                    // blue,   left of center
  { color: '#9145ff', x: C - 3 * DX + DX/2, y: C + 2 * DY },           // purple, lower-left
  { color: '#ff3aac', x: C - DX - DX/2,     y: C + 3 * DY },           // magenta, bottom-left
];

// --- Center hex -----------------------------------------------------------
const HEX_R = 22;
function hexPoints(cx, cy, r) {
  // Pointy-top hex (vertex at top), matching the reference.
  const pts = [];
  for (let i = 0; i < 6; i++) {
    const a = ((60 * i - 30) * Math.PI) / 180;
    pts.push(`${(cx + r * Math.cos(a)).toFixed(2)},${(cy + r * Math.sin(a)).toFixed(2)}`);
  }
  return pts.join(' ');
}

// --- SVG assembly ---------------------------------------------------------
const fmt = n => Number(n).toFixed(2);

const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${W}">
  <defs>
    <radialGradient id="bg" cx="50%" cy="42%" r="62%">
      <stop offset="0%"  stop-color="#1d1b35"/>
      <stop offset="55%" stop-color="#0a0a14"/>
      <stop offset="100%" stop-color="#04040a"/>
    </radialGradient>
    <filter id="glow" x="-50%" y="-50%" width="200%" height="200%">
      <feGaussianBlur stdDeviation="3" result="b"/>
      <feMerge>
        <feMergeNode in="b"/>
        <feMergeNode in="SourceGraphic"/>
      </feMerge>
    </filter>
    <clipPath id="circ"><circle cx="${C}" cy="${C}" r="${R}"/></clipPath>
  </defs>

  <!-- Dark circular base. Anything outside this circle stays transparent. -->
  <circle cx="${C}" cy="${C}" r="${R}" fill="url(#bg)"/>

  <!-- Hex network — lines first, dots on top, all clipped to the circle. -->
  <g clip-path="url(#circ)">
    <g stroke="#3a3a4a" stroke-width="0.55" opacity="0.55" stroke-linecap="round">
      ${lines.map(l => `<line x1="${fmt(l.x1)}" y1="${fmt(l.y1)}" x2="${fmt(l.x2)}" y2="${fmt(l.y2)}"/>`).join('\n      ')}
    </g>
    <g fill="#5e5e72" opacity="0.85">
      ${positions.map(p => `<circle cx="${fmt(p.x)}" cy="${fmt(p.y)}" r="${DOT_R}"/>`).join('\n      ')}
    </g>
  </g>

  <!-- Colored highlighted nodes with a soft glow halo. -->
  <g filter="url(#glow)">
    ${nodes.map(n => `<circle cx="${fmt(n.x)}" cy="${fmt(n.y)}" r="${NODE_R}" fill="${n.color}"/>`).join('\n    ')}
  </g>

  <!-- Center hex: hollow, with a thick white border. -->
  <polygon points="${hexPoints(C, C, HEX_R)}" fill="#0a0a14" stroke="#ffffff" stroke-width="3.5" stroke-linejoin="round"/>
</svg>
`;

writeFileSync(outPath, svg);
console.log(`Wrote ${outPath} — ${positions.length} dots, ${lines.length} lines, ${nodes.length} colored nodes.`);
