import { readable, type Readable } from 'svelte/store';

declare global {
  // Set by iCUE before widget scripts run.
  var iCUE_initialized: boolean | undefined;
  var uniqueId: string | undefined;
  var icueEvents: IcueEventHandlers | undefined;

  interface IcueEventHandlers {
    onICUEInitialized?: () => void;
    onDataUpdated?: () => void;
    onUpdateRequested?: () => void;
  }
}

export type IcueLifecycleHandlers = {
  onReady?: () => void;
  onPropertyChanged?: () => void;
};

/**
 * Wires up icueEvents the way iCUE's bridge expects (bare `=`, no `var`/`let`/`const`)
 * AND falls back to firing handlers immediately when running outside iCUE
 * (browser dev / iframe inside our loader).
 *
 * Per the kit's lifecycle reference: never wrap `icueEvents` in a declarator —
 * the runtime uses property descriptors that won't see lexically-scoped vars.
 */
export function registerIcueLifecycle(handlers: IcueLifecycleHandlers): void {
  const ready = handlers.onReady ?? (() => {});
  const updated = handlers.onPropertyChanged ?? (() => {});

  // Bare assignment intentional — required by iCUE.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  (globalThis as any).icueEvents = {
    onICUEInitialized: ready,
    onDataUpdated: updated,
  };

  if (typeof globalThis.iCUE_initialized !== 'undefined' && globalThis.iCUE_initialized) {
    ready();
    updated();
  } else {
    // Browser / loader-iframe path: fire updated so the UI can render with defaults.
    queueMicrotask(updated);
  }
}

const icuePropertyOverrides = new Map<string, unknown>();

/**
 * Reads a property iCUE injected as a global, ignoring host-backed overrides.
 * Use this when watching the actual iCUE panel value for persistence.
 */
export function getRawIcueProperty<T = unknown>(name: string): T | undefined {
  const w = globalThis as Record<string, unknown>;
  if (Object.prototype.hasOwnProperty.call(w, name)) {
    const value = w[name];
    if (value !== undefined && value !== null && value !== '') return value as T;
  }
  try {
    const value = Function(
      `return typeof ${name} !== 'undefined' ? ${name} : undefined`
    )() as T | undefined;
    if (value !== undefined && value !== null) return value;
  } catch {
    /* ignore */
  }
  return undefined;
}

/**
 * Host-backed settings can override volatile iCUE globals after a restart.
 * The active widget still watches raw iCUE values separately so user changes
 * can be pushed back to the host.
 */
export function setIcuePropertyOverrides(values: Record<string, unknown>): void {
  for (const [key, value] of Object.entries(values)) {
    if (value === undefined || value === null || value === '') {
      icuePropertyOverrides.delete(key);
    } else {
      icuePropertyOverrides.set(key, value);
    }
  }
}

/**
 * Reads a property iCUE injected as a global. Host-backed overrides win so
 * settings restored from the host survive iCUE/widget instance resets.
 */
export function getIcueProperty<T = unknown>(name: string): T | undefined {
  if (icuePropertyOverrides.has(name)) {
    return icuePropertyOverrides.get(name) as T;
  }
  return getRawIcueProperty<T>(name);
}

/**
 * Reactive iCUE option. Returns a Svelte store that re-fires whenever the
 * underlying global value changes. iCUE injects properties as plain globals
 * with no notifier, so we poll once a second; cheap and reliable.
 *
 * Why this exists: components used to call `getIcueProperty()` inside
 * `$derived(...)`, which only re-runs when its tracked dependencies change —
 * and globals aren't tracked. Result: toggling an iCUE property had no effect
 * until something else forced a re-render (e.g. resize). Subscribing to this
 * store gives proper reactivity.
 */
const _icueOptionStores = new Map<string, Readable<unknown>>();

export function icueOption<T = unknown>(name: string): Readable<T | undefined> {
  let store = _icueOptionStores.get(name) as Readable<T | undefined> | undefined;
  if (store) return store;
  store = readable<T | undefined>(getIcueProperty<T>(name), set => {
    let last = getIcueProperty<T>(name);
    set(last);
    const id = setInterval(() => {
      const cur = getIcueProperty<T>(name);
      if (cur !== last) {
        last = cur;
        set(cur);
      }
    }, 1000);
    return () => clearInterval(id);
  });
  _icueOptionStores.set(name, store);
  return store;
}

/**
 * Returns a stable storage key for this widget instance. Uses iCUE's `uniqueId`
 * when available; falls back to a per-origin random key for browser dev.
 */
export function widgetStorageKey(suffix?: string): string {
  const base = globalThis.uniqueId ?? fallbackKey();
  return suffix ? `${base}:${suffix}` : base;
}

function fallbackKey(): string {
  const k = '__betterxeneon_dev_uniqueid__';
  const w = globalThis as Record<string, unknown>;
  let v = w[k] as string | undefined;
  if (!v) {
    v = `dev-${Math.random().toString(36).slice(2, 10)}`;
    w[k] = v;
  }
  return v;
}
