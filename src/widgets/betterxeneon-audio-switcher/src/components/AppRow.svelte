<script lang="ts">
  import type { AudioSession } from '@betterxeneon/shared';
  import { throttle } from '@betterxeneon/shared';
  import { host } from '../api.ts';
  import { appStore } from '../state.ts';
  import VolumeSlider from './VolumeSlider.svelte';

  let { session, deviceLabel, lastSoundAt }: {
    session: AudioSession;
    deviceLabel: string;
    /** Date.now() of the last poll when this session crossed the audibility
     *  threshold. 0 = never produced sound. Drives the silent-row dim. */
    lastSoundAt: number;
  } = $props();

  let releaseTimer: ReturnType<typeof setTimeout> | null = null;
  let iconError = $state(false);
  const iconUrl = $derived(host.iconUrlForSession(session.id));

  const isInactive = $derived(session.state !== 'Active');

  // "Silent" = not currently producing sound AND hasn't produced sound in the
  // recent past. Threshold is slightly longer than a default poll interval
  // (1500ms) so a session briefly between buffers doesn't flicker between
  // bright and dim. Sessions that have never produced sound (lastSoundAt=0)
  // fall through as silent because Date.now() - 0 is huge.
  const SOUND_THRESHOLD = 0.01;
  const QUIET_GRACE_MS = 2500;
  const isSilent = $derived(
    session.peak <= SOUND_THRESHOLD && (Date.now() - lastSoundAt) > QUIET_GRACE_MS
  );

  const sendVolume = throttle((level: number) => {
    void host.setSessionVolume(session.id, level).catch(() => {/* surfaced via banner */});
  }, 80);

  function onVolumeChange(level: number): void {
    appStore.update(s => ({
      ...s,
      sessions: s.sessions.map(se => se.id === session.id ? { ...se, volume: level } : se),
    }));
    sendVolume(level);
  }

  function onAdjustStart(): void {
    appStore.update(s => ({ ...s, adjustingSessionId: session.id }));
    if (releaseTimer) clearTimeout(releaseTimer);
  }

  function onAdjustEnd(): void {
    sendVolume.flush();
    if (releaseTimer) clearTimeout(releaseTimer);
    // 3000ms grace — covers 2 polling cycles so Windows audio has time to
    // acknowledge before polling can overwrite the local value.
    releaseTimer = setTimeout(() => {
      appStore.update(s => s.adjustingSessionId === session.id ? { ...s, adjustingSessionId: null } : s);
    }, 3000);
  }

  function toggleMute(): void {
    const next = !session.muted;
    appStore.update(s => ({
      ...s,
      sessions: s.sessions.map(se => se.id === session.id ? { ...se, muted: next } : se),
    }));
    void host.setSessionMute(session.id, next).catch(() => {/* surfaced via banner */});
  }

  function setMaxVolume(): void {
    appStore.update(s => ({
      ...s,
      sessions: s.sessions.map(se => se.id === session.id ? { ...se, volume: 100, muted: false } : se),
    }));
    sendVolume.flush();
    void host.setSessionVolume(session.id, 100).catch(() => {/* surfaced via banner */});
    if (session.muted) {
      void host.setSessionMute(session.id, false).catch(() => {/* surfaced via banner */});
    }
  }
</script>

<div class="row" class:inactive={isInactive} class:silent={isSilent} class:muted={session.muted}>
  <div class="header">
    {#if !iconError}
      <img class="app-icon" src={iconUrl} alt="" onerror={() => iconError = true} />
    {/if}
    <div class="name" title={session.displayName}>{session.displayName}</div>
    <div class="device-label" title={session.deviceName}>{deviceLabel}</div>
  </div>

  <VolumeSlider
    value={session.volume}
    muted={session.muted}
    onChange={onVolumeChange}
    onToggleMute={toggleMute}
    onMaxVolume={setMaxVolume}
    onAdjustStart={onAdjustStart}
    onAdjustEnd={onAdjustEnd}
  />
</div>

<style>
  .row {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.8);
    padding: calc(var(--layout-unit) * 1.5) calc(var(--layout-unit) * 2.5);
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
  }

  /* Inactive sessions: dim only the name/device label so the controls (slider,
     buttons) stay vivid and usable. Previously a row-wide opacity made every
     control look washed-out vs the OutputCard view — user complaint. */
  .row.inactive .name,
  .row.inactive .device-label {
    opacity: 0.5;
  }

  /* Silent sessions (peak below audible threshold AND no sound in recent
     past): dim the entire row, controls included, so the user's eye is drawn
     to whatever's actually making noise. The row is still fully interactive
     — opacity only — so adjusting volume on a quiet background app still
     works. Uses a transition so the dim-in/brighten when sound starts and
     stops doesn't look abrupt. */
  .row {
    transition: opacity 250ms ease;
  }
  .row.silent {
    opacity: 0.5;
  }

  .header {
    display: flex;
    align-items: center;
    gap: var(--gap);
    min-width: 0;
  }

  .app-icon {
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

  .device-label {
    font-size: var(--font-label);
    opacity: 0.55;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    flex: 0 1 auto;
    max-width: 35%;
  }
</style>
