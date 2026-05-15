import { getRawIcueProperty, setIcuePropertyOverrides } from '@betterxeneon/shared';
import { host } from './api.ts';

type SettingValue = string | number | boolean;

const SETTING_DEFAULTS = {
  pollIntervalMs: 1500,
  useSystemAccent: true,
  textColor: '#ffffff',
  accentColor: '#1db954',
  backgroundColor: '#0a0a0c',
  transparency: 0,
  useAlbumArtBackground: true,
  autoExpand: true,
  autoExpandAfterSec: 30,
  showLyrics: true,
  lyricSyncOffsetMs: 0,
} satisfies Record<string, SettingValue>;

type SettingKey = keyof typeof SETTING_DEFAULTS;

const SETTING_KEYS = Object.keys(SETTING_DEFAULTS) as SettingKey[];

let started = false;
let loaded = false;
let saveTimer: ReturnType<typeof setTimeout> | null = null;
let hostSettings: Record<string, unknown> = {};
let effectiveSettings: Partial<Record<SettingKey, SettingValue>> = {};
let lastRawSettings: Partial<Record<SettingKey, SettingValue>> = {};

export function startSettingsPersistence(onChanged: () => void): void {
  if (started) return;
  started = true;
  lastRawSettings = readRawSettings();
  void loadHostSettings(onChanged);
  setInterval(() => captureIcueChanges(onChanged), 500);
}

async function loadHostSettings(onChanged: () => void): Promise<void> {
  try {
    hostSettings = await host.getWidgetSettings();
  } catch {
    setTimeout(() => void loadHostSettings(onChanged), 1500);
    return;
  }

  const raw = readRawSettings();
  const overrides: Record<string, unknown> = {};
  let shouldSeedHost = false;

  for (const key of SETTING_KEYS) {
    if (Object.prototype.hasOwnProperty.call(hostSettings, key)) {
      const stored = normalizeSettingValue(key, hostSettings[key]);
      if (stored === undefined) continue;
      effectiveSettings[key] = stored;
      overrides[key] = stored;
      continue;
    }

    const rawValue = raw[key];
    if (rawValue === undefined) continue;
    effectiveSettings[key] = rawValue;
    shouldSeedHost = true;
  }

  if (Object.keys(overrides).length > 0) {
    setIcuePropertyOverrides(overrides);
    onChanged();
  }

  lastRawSettings = raw;
  loaded = true;

  if (shouldSeedHost) {
    saveSettings();
  }
}

function captureIcueChanges(onChanged: () => void): void {
  if (!loaded) return;

  const raw = readRawSettings();
  const overrides: Record<string, unknown> = {};
  let changed = false;

  for (const key of SETTING_KEYS) {
    const rawValue = raw[key];
    if (rawValue === undefined) continue;
    if (lastRawSettings[key] === rawValue) continue;

    lastRawSettings[key] = rawValue;
    effectiveSettings[key] = rawValue;
    overrides[key] = rawValue;
    changed = true;
  }

  if (!changed) return;
  setIcuePropertyOverrides(overrides);
  onChanged();
  saveSettings();
}

function saveSettings(): void {
  if (saveTimer) clearTimeout(saveTimer);
  saveTimer = setTimeout(async () => {
    saveTimer = null;
    try {
      hostSettings = { ...hostSettings, ...effectiveSettings };
      await host.setWidgetSettings(hostSettings);
    } catch {
      /* host offline; the next setting change will try again */
    }
  }, 300);
}

function readRawSettings(): Partial<Record<SettingKey, SettingValue>> {
  const values: Partial<Record<SettingKey, SettingValue>> = {};
  for (const key of SETTING_KEYS) {
    const value = normalizeSettingValue(key, getRawIcueProperty(key));
    if (value !== undefined) values[key] = value;
  }
  return values;
}

function normalizeSettingValue(key: SettingKey, value: unknown): SettingValue | undefined {
  if (value === undefined || value === null || value === '') return undefined;

  const defaultValue = SETTING_DEFAULTS[key];
  if (typeof defaultValue === 'boolean') {
    if (value === true || value === 'true') return true;
    if (value === false || value === 'false') return false;
    return undefined;
  }

  if (typeof defaultValue === 'number') {
    const n = typeof value === 'number' ? value : Number(value);
    return Number.isFinite(n) ? n : undefined;
  }

  return typeof value === 'string' ? value : String(value);
}
