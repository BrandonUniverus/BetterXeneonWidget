<script lang="ts">
  import type { SteelSeriesStatus } from '@betterxeneon/shared';
  import { host } from '../api.ts';

  // SteelSeries Sonar lets you pick which physical output device receives
  // its mix ("ALL OUTPUT DEVICES" in the Sound Device Manager). This row
  // mirrors what the user's Sonar hotkey does — one tap cycles to the next
  // device. Hidden until the host successfully reaches Sonar at least once
  // so users without Sonar see nothing at all.

  let status = $state<SteelSeriesStatus | null>(null);
  let swapping = $state(false);
  let lastError = $state<string | null>(null);
  const devices = $derived(status?.devices ?? []);
  const currentDevice = $derived(
    devices.find(d => d.id === status?.currentDeviceId)
      ?? devices.find(d => d.isCurrent)
      ?? devices[0]
      ?? null
  );
  const currentIndex = $derived(currentDevice ? devices.findIndex(d => d.id === currentDevice.id) : -1);
  const nextDevice = $derived(
    devices.length > 1
      ? devices[(currentIndex >= 0 ? currentIndex + 1 : 1) % devices.length]
      : null
  );
  const isTwoDeviceSwitch = $derived(devices.length === 2);
  const secondDeviceActive = $derived(isTwoDeviceSwitch && currentIndex === 1);

  // Poll every 4s. Sonar state only changes via this widget OR the user's
  // hotkey OR the Sonar UI — we don't need a tight cadence here. After a
  // swap we immediately re-fetch so the row reflects the new device
  // without waiting on the timer.
  const POLL_MS = 4000;

  async function refresh(): Promise<void> {
    try {
      status = await host.getSteelSeriesStatus();
    } catch {
      // Host offline — leave previous status visible (degrades to a stale
      // pill rather than disappearing every time the host blinks).
    }
  }

  // Kick an initial fetch immediately, then run the periodic refresh.
  // Using $effect.root so the interval is cleaned up if the component is
  // unmounted (e.g. on Outputs ↔ Apps tab switch and back).
  $effect(() => {
    void refresh();
    const id = setInterval(() => { void refresh(); }, POLL_MS);
    return () => clearInterval(id);
  });

  async function onSwap(): Promise<void> {
    if (swapping) return;
    swapping = true;
    lastError = null;
    try {
      const result = await host.swapSteelSeriesOutput();
      if (result.ok) {
        // Optimistically reflect the new device before the next poll lands.
        if (status && result.newDeviceId) {
          status = {
            ...status,
            currentDeviceId: result.newDeviceId,
            currentDeviceName: result.newDeviceName,
            devices: status.devices.map(d => ({
              ...d,
              isCurrent: d.id === result.newDeviceId,
            })),
          };
        }
        await refresh();
      } else {
        lastError = result.error ?? 'Swap failed';
      }
    } catch (e) {
      lastError = e instanceof Error ? e.message : String(e);
    } finally {
      swapping = false;
    }
  }

  function shortName(name: string | null | undefined): string {
    const full = name ?? '';
    const paren = full.match(/\(([^)]*)\)/)?.[1]?.trim();
    const cleaned = full
      .replace(/^Headphones\s*/i, '')
      .replace(/^Speakers\s*/i, '')
      .replace(/\s*\([^)]*\)\s*$/g, '')
      .trim();
    return cleaned || paren || full;
  }
</script>

{#if status?.available && devices.length > 1}
  <button
    class="sonar-switch"
    class:swapping
    class:second-active={secondDeviceActive}
    type="button"
    onclick={onSwap}
    disabled={swapping}
    aria-label="Swap SteelSeries Sonar output device"
    title={lastError ?? 'Cycle Sonar to the next output device'}
  >
    <span class="label" aria-hidden="true">
      <span class="badge">SS</span>
      <span class="title">Sonar</span>
    </span>

    <span class="switch-body">
      {#if isTwoDeviceSwitch}
        <span class="toggle-track" aria-hidden="true">
          <span class="toggle-thumb"></span>
          {#each devices as device}
            <span class="toggle-option" class:active={device.id === currentDevice?.id}>{shortName(device.name)}</span>
          {/each}
        </span>
      {:else}
        <span class="route" aria-hidden="true">
          <span class="current">{currentDevice?.name ?? status.currentDeviceName ?? 'Unknown'}</span>
          <span class="arrow">-&gt;</span>
          <span class="next">{nextDevice?.name ?? 'Next'}</span>
        </span>
      {/if}

      {#if lastError}
        <span class="error">{lastError}</span>
      {/if}
    </span>

    <span class="action" aria-hidden="true">
      {#if swapping}
        <span class="spinner"></span>
      {:else}
        <span>Next</span>
      {/if}
    </span>
  </button>
{/if}

<style>
  .sonar-switch {
    display: flex;
    align-items: center;
    gap: calc(var(--layout-unit) * 1.5);
    padding: calc(var(--layout-unit) * 1.2) calc(var(--layout-unit) * 1.4);
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    width: 100%;
    text-align: left;
    color: inherit;
    cursor: pointer;
    transition: background 120ms, border-color 120ms, opacity 120ms;
  }
  .sonar-switch:hover { background: var(--surface-strong); border-color: var(--accent-color); }
  .sonar-switch:active { transform: translateY(1px); }
  .sonar-switch[disabled] { cursor: progress; opacity: 0.75; }

  .label {
    flex: 0 0 auto;
    display: flex;
    align-items: center;
    gap: calc(var(--layout-unit) * 0.9);
    min-width: 0;
  }

  .badge {
    width: clamp(26px, calc(var(--layout-unit) * 3.9), 38px);
    height: clamp(26px, calc(var(--layout-unit) * 3.9), 38px);
    display: inline-grid;
    place-items: center;
    border-radius: 7px;
    background: color-mix(in srgb, var(--accent-color) 82%, var(--text-color));
    color: var(--bg-color);
    font-weight: 700;
    font-size: var(--font-label);
  }

  .title {
    font-size: var(--font-label);
    font-weight: 700;
    opacity: 0.68;
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }

  .switch-body {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.6);
    min-width: 0;
  }

  .toggle-track {
    position: relative;
    display: grid;
    grid-template-columns: 1fr 1fr;
    align-items: center;
    min-height: clamp(34px, calc(var(--layout-unit) * 5), 48px);
    padding: calc(var(--layout-unit) * 0.45);
    border-radius: 999px;
    background: color-mix(in srgb, var(--bg-color) 54%, transparent);
    border: 1px solid color-mix(in srgb, var(--text-color) 12%, transparent);
    overflow: hidden;
  }

  .toggle-thumb {
    position: absolute;
    top: calc(var(--layout-unit) * 0.45);
    bottom: calc(var(--layout-unit) * 0.45);
    left: calc(var(--layout-unit) * 0.45);
    width: calc(50% - calc(var(--layout-unit) * 0.45));
    border-radius: 999px;
    background: var(--accent-color);
    transition: left 180ms ease;
  }
  .sonar-switch.second-active .toggle-thumb {
    left: 50%;
  }

  .toggle-option {
    position: relative;
    z-index: 1;
    display: block;
    min-width: 0;
    padding: 0 calc(var(--layout-unit) * 1.2);
    text-align: center;
    font-size: var(--font-label);
    font-weight: 700;
    color: color-mix(in srgb, var(--text-color) 70%, transparent);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .toggle-option.active {
    color: var(--bg-color);
  }

  .route {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
    align-items: center;
    gap: calc(var(--layout-unit) * 0.8);
    font-size: var(--font-body);
    font-weight: 600;
  }
  .current, .next {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .current { color: var(--accent-color); }
  .arrow { opacity: 0.45; }

  .error {
    color: #ff7b8a;
    font-size: var(--font-label);
    line-height: 1.2;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .action {
    flex: 0 0 auto;
    min-width: clamp(48px, calc(var(--layout-unit) * 8), 76px);
    height: clamp(32px, calc(var(--layout-unit) * 4.8), 46px);
    display: grid;
    place-items: center;
    padding: 0 calc(var(--layout-unit) * 1.3);
    border-radius: 999px;
    background: color-mix(in srgb, var(--accent-color) 18%, transparent);
    color: var(--accent-color);
    border: 1px solid color-mix(in srgb, var(--accent-color) 36%, transparent);
    font-size: var(--font-label);
    font-weight: 800;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }
  .spinner {
    width: 18px;
    height: 18px;
    border-radius: 50%;
    border: 2px solid color-mix(in srgb, var(--accent-color) 28%, transparent);
    border-top-color: var(--accent-color);
    animation: spin 900ms linear infinite;
  }
  @keyframes spin { to { transform: rotate(360deg); } }
</style>
