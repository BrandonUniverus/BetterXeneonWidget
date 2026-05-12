<script lang="ts">
  import type { AudioDevice } from '@betterxeneon/shared';
  import { throttle } from '@betterxeneon/shared';
  import { host } from '../api.ts';
  import { appStore } from '../state.ts';
  import VolumeSlider from './VolumeSlider.svelte';

  let { device, displayName }: { device: AudioDevice; displayName: string } = $props();

  let releaseTimer: ReturnType<typeof setTimeout> | null = null;
  let iconError = $state(false);
  const iconUrl = $derived(host.iconUrlForDevice(device.id));

  const isActiveDefault = $derived(
    device.isDefault || $appStore.pendingDefaultId === device.id
  );

  /**
   * True when *some other* card is mid-switch — meaning a user clicked "make
   * default" on another device and we're waiting for Windows to acknowledge.
   * We lock this card down so the user can't queue a second switch on top of
   * the first one (which is racey and confusing — Windows arbitrates between
   * concurrent default-device calls in an unhelpful way). The card that *is*
   * switching shows its own "switching…" pill and stays clickable for a UX
   * sanity check (no-op cancel path).
   */
  const isLockedByOtherSwitch = $derived(
    $appStore.pendingDefaultId !== null && $appStore.pendingDefaultId !== device.id
  );

  // Throttle host calls during drag — UI updates immediately, network at most every ~80ms.
  const sendVolume = throttle((level: number) => {
    void host.setDeviceVolume(device.id, level).catch(() => {/* surfaced via banner */});
  }, 80);

  function onVolumeChange(level: number): void {
    appStore.update(s => ({
      ...s,
      devices: s.devices.map(d => d.id === device.id ? { ...d, volume: level } : d),
    }));
    sendVolume(level);
  }

  function onAdjustStart(): void {
    appStore.update(s => ({ ...s, adjustingDeviceId: device.id }));
    if (releaseTimer) clearTimeout(releaseTimer);
  }

  function onAdjustEnd(): void {
    sendVolume.flush();
    if (releaseTimer) clearTimeout(releaseTimer);
    // 3000ms grace — covers 2 polling cycles (1500ms each) so Windows audio
    // has time to acknowledge our setVolume before polling can overwrite the
    // local value with a stale read from the host.
    releaseTimer = setTimeout(() => {
      appStore.update(s => s.adjustingDeviceId === device.id ? { ...s, adjustingDeviceId: null } : s);
    }, 3000);
  }

  function toggleMute(): void {
    const next = !device.muted;
    appStore.update(s => ({
      ...s,
      devices: s.devices.map(d => d.id === device.id ? { ...d, muted: next } : d),
    }));
    void host.setDeviceMute(device.id, next).catch(() => {/* surfaced via banner */});
  }

  function setMaxVolume(): void {
    appStore.update(s => ({
      ...s,
      devices: s.devices.map(d => d.id === device.id ? { ...d, volume: 100, muted: false } : d),
    }));
    sendVolume.flush();
    void host.setDeviceVolume(device.id, 100).catch(() => {/* surfaced via banner */});
    if (device.muted) {
      void host.setDeviceMute(device.id, false).catch(() => {/* surfaced via banner */});
    }
  }

  async function makeDefault(): Promise<void> {
    if (isActiveDefault) return;
    // Guard: another card is mid-switch. The disabled attribute already blocks
    // clicks for mouse users, but keyboard/touch can sometimes bypass it on
    // older platforms — belt and braces.
    if (isLockedByOtherSwitch) return;
    appStore.update(s => ({ ...s, pendingDefaultId: device.id }));
    try {
      await host.setDefaultAudioDevice(device.id);
    } catch {
      appStore.update(s => ({ ...s, pendingDefaultId: null }));
    }
  }
</script>

<div class="card" class:active={isActiveDefault} class:muted={device.muted} class:locked={isLockedByOtherSwitch} aria-disabled={isLockedByOtherSwitch}>
  <div class="header">
    <button
      class="name-button"
      type="button"
      onclick={makeDefault}
      disabled={isLockedByOtherSwitch}
      aria-label={`Set ${displayName} as default`}
    >
      <span class="indicator" aria-hidden="true">
        {#if isActiveDefault}
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="20 6 9 17 4 12"/>
          </svg>
        {/if}
      </span>
      {#if !iconError}
        <img class="device-icon" src={iconUrl} alt="" onerror={() => iconError = true} />
      {/if}
      <span class="name">{displayName}</span>
      {#if $appStore.pendingDefaultId === device.id && !device.isDefault}
        <span class="pending">switching…</span>
      {/if}
    </button>
  </div>

  <VolumeSlider
    value={device.volume}
    muted={device.muted}
    disabled={isLockedByOtherSwitch}
    onChange={onVolumeChange}
    onToggleMute={toggleMute}
    onMaxVolume={setMaxVolume}
    onAdjustStart={onAdjustStart}
    onAdjustEnd={onAdjustEnd}
  />
</div>

<style>
  .card {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 1);
    padding: calc(var(--layout-unit) * 1.8) calc(var(--layout-unit) * 2.5);
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    transition: border-color 120ms, background 120ms;
  }

  .card.active {
    border-color: var(--accent-color);
  }

  /* Card is dimmed and pointer-blocked while a *different* card is switching.
     Opacity tells the user something's happening; pointer-events:none guards
     against clicks slipping through the button's disabled attr (especially
     on touch devices where focus rings + tap-targets can be finicky). */
  .card.locked {
    opacity: 0.45;
    pointer-events: none;
    transition: opacity 150ms ease;
  }

  .header {
    display: flex;
    align-items: center;
    gap: var(--gap);
    min-width: 0;
  }

  .name-button {
    display: flex;
    align-items: center;
    gap: calc(var(--layout-unit) * 1.2);
    flex: 1 1 auto;
    min-width: 0;
    color: inherit;
    text-align: left;
    padding: calc(var(--layout-unit) * 0.6) 0;
  }

  .indicator {
    width: calc(var(--layout-unit) * 3.2);
    height: calc(var(--layout-unit) * 3.2);
    border-radius: 50%;
    display: grid;
    place-items: center;
    flex-shrink: 0;
    border: 1.5px solid color-mix(in srgb, var(--text-color) 30%, transparent);
    color: var(--text-color);
  }

  .card.active .indicator {
    background: var(--accent-color);
    border-color: var(--accent-color);
    color: var(--bg-color);
  }

  .indicator svg {
    width: 75%;
    height: 75%;
  }

  .device-icon {
    width: clamp(20px, calc(var(--layout-unit) * 4.5), 48px);
    height: clamp(20px, calc(var(--layout-unit) * 4.5), 48px);
    flex-shrink: 0;
    object-fit: contain;
    image-rendering: -webkit-optimize-contrast;
  }

  .name {
    font-size: var(--font-body);
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    flex: 1 1 auto;
    min-width: 0;
  }

  .pending {
    font-size: var(--font-label);
    opacity: 0.6;
    font-style: italic;
    flex-shrink: 0;
  }
</style>
