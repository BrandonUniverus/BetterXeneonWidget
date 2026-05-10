import { tick } from 'svelte';
import { getIcueProperty } from '@betterxeneon/shared';
import { host } from './api.ts';
import { appStore } from './state.ts';

let timer: ReturnType<typeof setTimeout> | null = null;
let stopped = false;
const STARTED_KEY = '__bxw_polling_started__';

export function startPolling(): void {
  const g = globalThis as Record<string, unknown>;
  if (g[STARTED_KEY]) return;
  g[STARTED_KEY] = true;
  stopped = false;
  void poll();
}

export function stopPolling(): void {
  stopped = true;
  (globalThis as Record<string, unknown>)[STARTED_KEY] = false;
  if (timer) clearTimeout(timer);
  timer = null;
}

async function poll(): Promise<void> {
  if (stopped) return;
  try {
    const [devices, sessions] = await Promise.all([
      host.listAudioDevices(),
      host.listSessions(),
    ]);

    let firstRunPinId: string | null = null;

    appStore.update(s => {
      let next: typeof s = { ...s, connected: true, error: null };

      next.devices = s.adjustingDeviceId === null
        ? devices
        : devices.map(d =>
            d.id === s.adjustingDeviceId
              ? { ...d, volume: s.devices.find(x => x.id === d.id)?.volume ?? d.volume }
              : d
          );

      next.sessions = s.adjustingSessionId === null
        ? sessions
        : sessions.map(se =>
            se.id === s.adjustingSessionId
              ? { ...se, volume: s.sessions.find(x => x.id === se.id)?.volume ?? se.volume }
              : se
          );

      if (s.pendingDefaultId && devices.some(d => d.id === s.pendingDefaultId && d.isDefault)) {
        next.pendingDefaultId = null;
      }

      // First-run auto-pin: only when host's config has been loaded AND it
      // wasn't previously initialized AND the user has no pins. Pins the
      // system default device.
      if (next.configLoaded && !next.configInitialized && next.pinnedIds.length === 0 && devices.length > 0) {
        const def = devices.find(d => d.isDefault) ?? devices[0];
        if (def) {
          next.pinnedIds = [def.id];
          next.configInitialized = true;
          firstRunPinId = def.id;
        }
      }

      return next;
    });

    if (firstRunPinId) {
      void host.setPinnedIds([firstRunPinId]).catch(() => {/* surfaced via banner */});
    }

    await tick();
  } catch (e) {
    appStore.update(s => ({
      ...s,
      connected: false,
      error: e instanceof Error ? e.message : String(e),
    }));
  } finally {
    if (!stopped) {
      const interval = clampInterval(getIcueProperty<number>('pollIntervalMs') ?? 1500);
      timer = setTimeout(() => void poll(), interval);
    }
  }
}

function clampInterval(value: unknown): number {
  const n = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(n)) return 1500;
  return Math.max(500, Math.min(5000, Math.round(n)));
}
