import type { AudioDevice, AudioSession } from '@betterxeneon/shared';
import { get, writable } from 'svelte/store';
import { host } from './api.ts';

export type View = 'outputs' | 'apps' | 'settings';

export interface AppStateValue {
  connected: boolean;
  error: string | null;
  devices: AudioDevice[];
  sessions: AudioSession[];
  pinnedIds: string[];
  view: View;
  systemAccentColor: string | null;
  // True while the user is actively dragging a slider — suppresses
  // polling-driven volume overwrites for the affected target.
  adjustingDeviceId: string | null;
  adjustingSessionId: string | null;
  pendingDefaultId: string | null;
  // Has the host's config been loaded? Used to gate first-run auto-pin so
  // we don't auto-pin a device while config is still in flight.
  configLoaded: boolean;
  // Has the user (or a previous first-run) ever set pins? When false, the
  // first poll cycle auto-pins the system default.
  configInitialized: boolean;
  /**
   * Per-session timestamp (Date.now()) of the last poll where the session's
   * Peak crossed an audibility threshold. Used to sort Apps by most recent
   * sound and to dim rows that haven't produced sound recently. Updated in
   * polling.ts; not persisted across reloads.
   */
  lastSoundAtBySessionId: Record<string, number>;
}

const initial: AppStateValue = {
  connected: false,
  error: null,
  devices: [],
  sessions: [],
  pinnedIds: [],
  view: 'outputs',
  systemAccentColor: null,
  adjustingDeviceId: null,
  adjustingSessionId: null,
  pendingDefaultId: null,
  configLoaded: false,
  configInitialized: false,
  lastSoundAtBySessionId: {},
};

export const appStore = writable<AppStateValue>(initial);
(globalThis as Record<string, unknown>).__bxw_store = appStore;

/**
 * Fetches saved pins from the host. Resolves once config is loaded so the
 * polling auto-pin logic knows whether first-run kick-in is needed.
 */
export async function loadConfig(): Promise<void> {
  try {
    const cfg = await host.getConfig();
    appStore.update(s => ({
      ...s,
      pinnedIds: cfg.pinnedIds,
      configInitialized: cfg.initialized,
      configLoaded: true,
    }));
  } catch {
    appStore.update(s => ({ ...s, configLoaded: true }));
  }
}

export function pinDevice(id: string): void {
  let next: string[] = [];
  appStore.update(s => {
    if (s.pinnedIds.includes(id)) { next = s.pinnedIds; return s; }
    next = [...s.pinnedIds, id];
    return { ...s, pinnedIds: next, configInitialized: true };
  });
  void host.setPinnedIds(next).catch(() => {/* surfaced via banner */});
}

export function unpinDevice(id: string): void {
  let next: string[] = [];
  appStore.update(s => {
    next = s.pinnedIds.filter(x => x !== id);
    return { ...s, pinnedIds: next, configInitialized: true };
  });
  void host.setPinnedIds(next).catch(() => {/* surfaced via banner */});
}

export function togglePin(id: string): void {
  if (get(appStore).pinnedIds.includes(id)) unpinDevice(id);
  else pinDevice(id);
}
