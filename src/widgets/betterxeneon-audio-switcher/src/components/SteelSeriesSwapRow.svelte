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
        // Re-fetch to pick up any divergence (e.g. Sonar reordered devices).
        void refresh();
      } else {
        lastError = result.error ?? 'Swap failed';
      }
    } catch (e) {
      lastError = e instanceof Error ? e.message : String(e);
    } finally {
      swapping = false;
    }
  }
</script>

{#if status?.available && status.devices.length > 1}
  <button
    class="row"
    class:swapping
    type="button"
    onclick={onSwap}
    disabled={swapping}
    aria-label="Swap SteelSeries Sonar output device"
    title={lastError ?? 'Cycle Sonar to the next output device'}
  >
    <span class="badge" aria-hidden="true">SS</span>
    <span class="label">
      <span class="title">Sonar output</span>
      <span class="device">{status.currentDeviceName ?? '—'}</span>
    </span>
    <span class="action" aria-hidden="true">
      {#if swapping}
        <svg viewBox="0 0 24 24" class="spinner">
          <circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="2.5" stroke-dasharray="40 60" stroke-linecap="round"/>
        </svg>
      {:else}
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
          <path d="M3 7h14l-3-3"/>
          <path d="M21 17H7l3 3"/>
        </svg>
      {/if}
    </span>
  </button>
{/if}

<style>
  .row {
    display: flex;
    align-items: center;
    gap: var(--gap);
    padding: calc(var(--layout-unit) * 1.4) calc(var(--layout-unit) * 2.2);
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    width: 100%;
    text-align: left;
    color: inherit;
    cursor: pointer;
    transition: background 120ms, border-color 120ms, opacity 120ms;
  }
  .row:hover { background: var(--surface-strong); border-color: var(--accent-color); }
  .row:active { transform: translateY(1px); }
  .row[disabled] { cursor: progress; opacity: 0.75; }

  .badge {
    flex: 0 0 auto;
    width: clamp(28px, calc(var(--layout-unit) * 4.5), 44px);
    height: clamp(28px, calc(var(--layout-unit) * 4.5), 44px);
    display: grid;
    place-items: center;
    border-radius: 8px;
    background: var(--accent-color);
    color: var(--bg-color);
    font-weight: 700;
    font-size: var(--font-label);
    letter-spacing: 0.04em;
  }

  .label {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }
  .title {
    font-size: var(--font-label);
    opacity: 0.6;
    text-transform: uppercase;
    letter-spacing: 0.06em;
  }
  .device {
    font-size: var(--font-body);
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .action {
    flex: 0 0 auto;
    width: clamp(24px, calc(var(--layout-unit) * 4), 36px);
    height: clamp(24px, calc(var(--layout-unit) * 4), 36px);
    display: grid;
    place-items: center;
    opacity: 0.75;
  }
  .action svg { width: 100%; height: 100%; }
  .spinner { animation: spin 900ms linear infinite; transform-origin: center; }
  @keyframes spin { to { transform: rotate(360deg); } }
</style>
