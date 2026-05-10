import { get } from 'svelte/store';
import { getIcueProperty } from '@betterxeneon/shared';
import { appStore } from './state.ts';

/**
 * Watches user input on the document. After a configurable idle period the
 * widget switches to "listening" (media-mode) display, which hides the
 * transport row and Playlists button in large-cell layout.
 *
 * Click-handling rule (key UX detail):
 *   The first tap while in media mode is "wake up the widget" — it must NOT
 *   trigger whatever button was under the user's finger, because the
 *   buttons aren't visible and the user would be tapping blindly. After
 *   waking, the user's *next* tap acts normally.
 *
 *   Implementation: in capture-phase pointerdown, if we're currently in
 *   listening mode, we set a "swallow next click" flag, switch to browse,
 *   and let a capture-phase click handler block the resulting click. The
 *   flag is also cleared on pointerup or after 400ms so a tap that never
 *   completes (e.g. user drags off) doesn't poison the next legit click.
 *
 * Configurable via iCUE properties (read fresh each time so panel changes
 * take effect on the next interaction):
 *   - autoExpand        (switch, default true): master toggle
 *   - autoExpandAfterSec (slider 10-120, default 30): idle threshold
 */

let idleTimer: ReturnType<typeof setTimeout> | null = null;
let swallowClearTimer: ReturnType<typeof setTimeout> | null = null;
let swallowNextClick = false;
let attached = false;
// Last-seen iCUE values for refreshInactivityConfig — used to skip the
// timer-restart unless settings *actually* changed. Without this guard, the
// 1.5s polling tick in main.ts would call refreshInactivityConfig → which
// would call scheduleListening → which clears + restarts the idle timer
// before it ever reaches 30s, so media mode never engages.
let lastEnabled: boolean | null = null;
let lastDelayMs: number | null = null;

function isAutoExpandOn(): boolean {
  const v = getIcueProperty<boolean>('autoExpand');
  return v !== false; // default true when undefined
}

function getAutoExpandMs(): number {
  const sec = getIcueProperty<number>('autoExpandAfterSec') ?? 30;
  const n = typeof sec === 'number' ? sec : Number(sec);
  if (!Number.isFinite(n)) return 30_000;
  return Math.max(5_000, Math.min(300_000, Math.round(n * 1000)));
}

function clearIdleTimer(): void {
  if (idleTimer) {
    clearTimeout(idleTimer);
    idleTimer = null;
  }
}

function scheduleListening(): void {
  clearIdleTimer();
  const enabled = isAutoExpandOn();
  const delayMs = getAutoExpandMs();
  lastEnabled = enabled;
  lastDelayMs = delayMs;
  if (!enabled) return;
  idleTimer = setTimeout(() => {
    appStore.update(s => ({ ...s, displayMode: 'listening' }));
  }, delayMs);
}

function clearSwallowFlag(): void {
  swallowNextClick = false;
  if (swallowClearTimer) {
    clearTimeout(swallowClearTimer);
    swallowClearTimer = null;
  }
}

function onPointerDown(): void {
  const wasListening = get(appStore).displayMode === 'listening';
  if (wasListening) {
    // First tap while in media mode = wake the widget. Block the click that
    // would otherwise hit whatever button is under the finger.
    swallowNextClick = true;
    if (swallowClearTimer) clearTimeout(swallowClearTimer);
    // Failsafe: if no click event fires (drag-off, gesture canceled, etc.)
    // clear the flag so the next legit click isn't blocked.
    swallowClearTimer = setTimeout(clearSwallowFlag, 400);
    appStore.update(s => ({ ...s, displayMode: 'browse' }));
  }
  scheduleListening();
}

function onClickCapture(e: Event): void {
  if (!swallowNextClick) return;
  // Eat this click — it was the wake-up tap. The user's next click will
  // pass through normally.
  e.preventDefault();
  e.stopImmediatePropagation();
  clearSwallowFlag();
}

export function startInactivityTracker(): void {
  if (attached) return;
  attached = true;
  // Capture-phase listeners so we run before any element-level handlers.
  // pointerdown covers mouse + touch + pen consistently on Chromium.
  document.addEventListener('pointerdown', onPointerDown, { capture: true });
  document.addEventListener('click', onClickCapture, { capture: true });
  // Initial schedule — start the clock immediately on widget load.
  scheduleListening();
}

/**
 * Re-evaluate idle scheduling — but only restart the timer if the user
 * has actually changed the iCUE settings. Called periodically from main.ts
 * (every 1.5s) as a fallback because iCUE's onDataUpdated lifecycle event
 * isn't always reliable for property changes; without the equality check,
 * each call would restart the idle countdown and the widget would never
 * reach the threshold.
 */
export function refreshInactivityConfig(): void {
  if (!attached) return;
  const enabled = isAutoExpandOn();
  const delayMs = getAutoExpandMs();
  if (enabled !== lastEnabled || delayMs !== lastDelayMs) {
    scheduleListening();
    // If the user JUST disabled auto-expand and the widget is currently
    // in listening mode (because the timer fired earlier), flip it back
    // to browse so they're not stuck staring at hidden buttons.
    if (!enabled) {
      appStore.update(s => s.displayMode === 'listening'
        ? { ...s, displayMode: 'browse' }
        : s);
    }
  }
}
