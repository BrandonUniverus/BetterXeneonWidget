import { mount } from 'svelte';
import { getIcueProperty, registerIcueLifecycle } from '@betterxeneon/shared';
import App from './App.svelte';
import { host } from './api.ts';
import { startPolling } from './polling.ts';
import { appStore, loadConfig } from './state.ts';
import './style.css';

const FALLBACK_ACCENT = '#0078d4';

/**
 * Mirrors the .theme-* CSS palettes in src/widgets/betterxeneon-media-v2/
 * index.html (the `--theme-accent` values). Keep this table in sync if the
 * media widget gains/changes themes — the audio-switcher tracks the media
 * widget's `theme` setting (0..5) and applies the matching accent so the
 * two widgets visually agree on a single accent at all times.
 *
 * Index 0 ("album") is null because the media widget derives that accent
 * from the current album art — the audio-switcher has no album context
 * here, so we fall back to the host's system accent for theme 0.
 */
const THEME_ACCENTS: ReadonlyArray<string | null> = [
  null,        // 0 = album (derived) → fall back to systemAccent
  '#ff3b6b',   // 1 = rainbow palette → static accent for non-eq UI
  '#00e5ff',   // 2 = neon
  '#ff5e87',   // 3 = sunset
  '#3ce0ff',   // 4 = aurora
  '#ffffff',   // 5 = mono
];

function applyTheme(): void {
  const root = document.documentElement;
  const text = getIcueProperty<string>('textColor');
  const bg = getIcueProperty<string>('backgroundColor');
  const transparency = getIcueProperty<number>('transparency');

  const useSystem = getIcueProperty<boolean>('useSystemAccent');
  const customAccent = getIcueProperty<string>('accentColor');

  // Pull both the system accent and the shared widget-settings snapshot in
  // a single subscribe() call — read once, unsubscribe immediately. Cheaper
  // than two subscribes and guarantees a consistent view of the store.
  let systemAccent: string | null = null;
  let themeIndex: number | null = null;
  appStore.subscribe(s => {
    systemAccent = s.systemAccentColor;
    const raw = s.widgetSettings?.theme;
    const n = typeof raw === 'number' ? raw : Number(raw);
    themeIndex = Number.isFinite(n) ? Math.max(0, Math.min(5, Math.round(n))) : null;
  })();

  // Resolution order for the accent:
  //   1. Media widget's chosen theme (if set & non-album). This is the
  //      "match the other widget" behavior — overrides everything else.
  //   2. iCUE custom accent (only when useSystemAccent is explicitly off).
  //      Kept for backwards-compat with any old iCUE option values that
  //      might still be stored, even though the widget no longer exposes
  //      these in its UI.
  //   3. Host-reported Windows system accent.
  //   4. Hard-coded FALLBACK_ACCENT (Windows blue).
  const themeAccent =
    themeIndex !== null && themeIndex >= 0 && themeIndex < THEME_ACCENTS.length
      ? THEME_ACCENTS[themeIndex]
      : null;

  const accent =
    themeAccent
      ?? (useSystem === false && typeof customAccent === 'string' && customAccent
            ? customAccent
            : systemAccent ?? customAccent ?? FALLBACK_ACCENT);

  if (typeof text === 'string' && text) root.style.setProperty('--text-color', text);
  if (typeof bg === 'string' && bg) root.style.setProperty('--bg-color', bg);
  root.style.setProperty('--accent-color', accent);

  // "Background Transparency" — 0 means fully opaque, 100 means fully transparent.
  const t = typeof transparency === 'number' ? transparency : Number(transparency ?? 0);
  if (Number.isFinite(t)) {
    const clamped = Math.max(0, Math.min(100, t));
    root.style.setProperty('--bg-opacity', String((100 - clamped) / 100));
  }
}

async function loadSystemAccent(): Promise<void> {
  try {
    const accent = await host.getAccentColor();
    appStore.update(s => ({ ...s, systemAccentColor: accent.hex }));
    applyTheme();
  } catch {
    /* fall back to FALLBACK_ACCENT until host is reachable */
  }
}

const target = document.getElementById('app');
if (!target) throw new Error('#app element missing');

// Touchscreen UX — long-press in QtWebEngine/Chromium pops the OS context menu
// (copy / save image / inspect). Suppress globally; the widget has no need for it.
document.addEventListener('contextmenu', e => e.preventDefault());
// Selection on long-press also feels wrong on a touchscreen control — kill it
// at the document level too. Components that need selectable text (none today)
// can re-enable per element.
document.addEventListener('selectstart', e => e.preventDefault());

// Fire and forget: pulls saved pins from the host so they survive iCUE preset
// swaps and widget reinstalls (per-widget localStorage is too volatile).
void loadConfig();
startPolling();
const app = mount(App, { target });
void loadSystemAccent();

registerIcueLifecycle({
  onReady: applyTheme,
  onPropertyChanged: applyTheme,
});

// iCUE doesn't reliably fire onDataUpdated for every property change in
// every QtWebEngine build. Re-apply on a 1.5s interval as a fallback so
// color / transparency / accent changes are picked up. applyTheme is
// just a handful of CSS variable writes — negligible cost.
setInterval(applyTheme, 1500);

export default app;
