import { mount } from 'svelte';
import { getIcueProperty, registerIcueLifecycle } from '@betterxeneon/shared';
import App from './App.svelte';
import { host } from './api.ts';
import { startPolling } from './polling.ts';
import { appStore, loadConfig } from './state.ts';
import './style.css';

const FALLBACK_ACCENT = '#0078d4';
// Hex sanity check — three or six hex digits after a leading '#'. Anything
// else (rgb(...), named colors, garbage) is rejected so we fall back cleanly.
const HEX_RE = /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i;

function applyTheme(): void {
  const root = document.documentElement;
  const text = getIcueProperty<string>('textColor');
  const bg = getIcueProperty<string>('backgroundColor');
  const transparency = getIcueProperty<number>('transparency');

  const useSystem = getIcueProperty<boolean>('useSystemAccent');
  const customAccent = getIcueProperty<string>('accentColor');

  // Pull system accent and the shared widget-settings snapshot in a single
  // subscribe() call — read once, unsubscribe immediately.
  let systemAccent: string | null = null;
  let publishedAccent: string | null = null;
  appStore.subscribe(s => {
    systemAccent = s.systemAccentColor;
    const v = s.widgetSettings?.currentThemeAccent;
    publishedAccent = typeof v === 'string' && HEX_RE.test(v) ? v : null;
  })();

  // Resolution order for the accent:
  //   1. `currentThemeAccent` published by the media widget — already the
  //      *effective* accent (album-derived hex when theme=0, or the named
  //      palette's hex for theme 1..5). This is the "match the other
  //      widget" behavior. Missing when the media widget hasn't run yet.
  //   2. iCUE custom accent (only when useSystemAccent is explicitly off).
  //      Kept for backwards-compat — the widget no longer exposes these
  //      in its UI but old saved values might still be present.
  //   3. Host-reported Windows system accent.
  //   4. Hard-coded FALLBACK_ACCENT (Windows blue).
  const accent =
    publishedAccent
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
  } catch {
    /* fall back to FALLBACK_ACCENT until host is reachable */
  }
}

let widgetSettingsInFlight = false;
async function refreshWidgetSettings(): Promise<void> {
  if (widgetSettingsInFlight) return;
  widgetSettingsInFlight = true;
  try {
    const widgetSettings = await host.getWidgetSettings();
    appStore.update(s =>
      s.widgetSettings?.currentThemeAccent === widgetSettings.currentThemeAccent
        ? s
        : { ...s, widgetSettings }
    );
  } catch {
    /* host offline — the normal connection banner is driven by polling */
  } finally {
    widgetSettingsInFlight = false;
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
void refreshWidgetSettings();

let lastThemeKey = '';
appStore.subscribe(s => {
  const rawAccent = s.widgetSettings?.currentThemeAccent;
  const themeAccent = typeof rawAccent === 'string' && HEX_RE.test(rawAccent) ? rawAccent : '';
  const key = `${s.systemAccentColor ?? ''}|${themeAccent}`;
  if (key === lastThemeKey) return;
  lastThemeKey = key;
  applyTheme();
});

registerIcueLifecycle({
  onReady: () => {
    applyTheme();
    void refreshWidgetSettings();
  },
  onPropertyChanged: () => {
    applyTheme();
    void refreshWidgetSettings();
  },
});

// iCUE doesn't reliably fire onDataUpdated for every property change in
// every QtWebEngine build. Re-apply on a 1.5s interval as a fallback so
// color / transparency / accent changes are picked up. applyTheme is
// just a handful of CSS variable writes — negligible cost.
setInterval(applyTheme, 1500);
setInterval(() => { void refreshWidgetSettings(); }, 500);

export default app;
