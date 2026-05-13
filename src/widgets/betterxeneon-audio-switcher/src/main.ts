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

  // Pull the shared widget-settings snapshot in a single subscribe() call:
  // read once, unsubscribe immediately.
  let publishedAccent: string | null = null;
  appStore.subscribe(s => {
    const v = s.widgetSettings?.currentThemeAccent;
    publishedAccent = typeof v === 'string' && HEX_RE.test(v) ? v : null;
  })();

  // Resolution order for the accent:
  //   1. `currentThemeAccent` published by the media widget — already the
  //      *effective* accent (album-derived hex when theme=0, or the named
  //      palette's hex for theme 1..5). This is the "match the media
  //      widget" behavior.
  //   2. Hard-coded fallback until the shared setting is available.
  const accent = publishedAccent ?? FALLBACK_ACCENT;

  if (typeof text === 'string' && text) root.style.setProperty('--text-color', text);
  if (typeof bg === 'string' && bg) root.style.setProperty('--bg-color', bg);
  root.style.setProperty('--accent-color', accent);

  // "Transparency" follows the iCUE WidgetBuilder kit convention used by
  // the Screens Personalization "Widget Transparency" slider: 100 = fully
  // opaque, 0 = fully transparent. The property value maps directly to
  // --bg-opacity (a percent / 100). Default is 100 (declared in index.html)
  // so a fresh install renders opaque until the user dials it back.
  const t = typeof transparency === 'number' ? transparency : Number(transparency ?? 100);
  if (Number.isFinite(t)) {
    const clamped = Math.max(0, Math.min(100, t));
    root.style.setProperty('--bg-opacity', String(clamped / 100));
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
void refreshWidgetSettings();

let lastThemeKey = '';
appStore.subscribe(s => {
  const rawAccent = s.widgetSettings?.currentThemeAccent;
  const themeAccent = typeof rawAccent === 'string' && HEX_RE.test(rawAccent) ? rawAccent : '';
  const key = themeAccent;
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
