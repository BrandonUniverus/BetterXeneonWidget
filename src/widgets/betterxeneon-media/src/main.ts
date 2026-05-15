import { mount } from 'svelte';
import { getIcueProperty, registerIcueLifecycle } from '@betterxeneon/shared';
import App from './App.svelte';
import { host } from './api.ts';
import { refreshInactivityConfig, startInactivityTracker } from './inactivity.ts';
import { startPolling } from './polling.ts';
import { startSettingsPersistence } from './settings-persistence.ts';
import { appStore } from './state.ts';
import './style.css';

const FALLBACK_ACCENT = '#1db954';

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
document.addEventListener('selectstart', e => e.preventDefault());

startPolling();
startInactivityTracker();
const app = mount(App, { target });
void loadSystemAccent();
startSettingsPersistence(() => {
  applyTheme();
  refreshInactivityConfig();
});

registerIcueLifecycle({
  onReady: () => { applyTheme(); refreshInactivityConfig(); },
  onPropertyChanged: () => { applyTheme(); refreshInactivityConfig(); },
});

// iCUE doesn't reliably fire onDataUpdated for every property change in
// every QtWebEngine build, so we re-apply the theme on a 1.5s interval as
// a belt-and-suspenders mechanism. applyTheme reads getIcueProperty fresh
// and is a handful of CSS variable writes — negligible cost.
setInterval(() => {
  applyTheme();
  refreshInactivityConfig();
}, 1500);

export default app;
