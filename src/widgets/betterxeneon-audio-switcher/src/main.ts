import { mount } from 'svelte';
import { getIcueProperty, registerIcueLifecycle } from '@betterxeneon/shared';
import App from './App.svelte';
import { host } from './api.ts';
import { startPolling } from './polling.ts';
import { appStore, loadConfig } from './state.ts';
import './style.css';

const FALLBACK_ACCENT = '#0078d4';

function applyTheme(): void {
  const root = document.documentElement;
  const text = getIcueProperty<string>('textColor');
  const bg = getIcueProperty<string>('backgroundColor');
  const transparency = getIcueProperty<number>('transparency');

  const useSystem = getIcueProperty<boolean>('useSystemAccent');
  const customAccent = getIcueProperty<string>('accentColor');
  const systemAccent = (() => {
    let v: string | null = null;
    appStore.subscribe(s => { v = s.systemAccentColor; })();
    return v;
  })();
  const accent =
    useSystem === false && typeof customAccent === 'string' && customAccent
      ? customAccent
      : systemAccent ?? customAccent ?? FALLBACK_ACCENT;

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
