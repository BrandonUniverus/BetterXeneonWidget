<script lang="ts">
  import { shortenDeviceNames } from '../name-shortener.ts';
  import { appStore, togglePin } from '../state.ts';
</script>

<section class="view" aria-label="Pin manager">
  <div class="hint">Tap a device to pin or unpin it from the Outputs view.</div>

  {#each $appStore.devices as device (device.id)}
    {@const pinned = $appStore.pinnedIds.includes(device.id)}
    {@const shortName = shortenDeviceNames($appStore.devices).get(device.id) ?? device.name}
    <button class="row" class:pinned type="button" onclick={() => togglePin(device.id)}>
      <span class="check" aria-hidden="true">
        {#if pinned}
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="20 6 9 17 4 12"/>
          </svg>
        {/if}
      </span>
      <span class="names">
        <span class="short">{shortName}</span>
        <span class="full" title={device.name}>{device.name}</span>
      </span>
      {#if device.isDefault}
        <span class="badge">default</span>
      {/if}
    </button>
  {/each}
</section>

<style>
  .view {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    gap: calc(var(--gap) * 0.5);
    overflow-y: auto;
    padding-right: calc(var(--layout-unit) * 0.5);
    scrollbar-width: thin;
    scrollbar-color: var(--surface-strong) transparent;
  }

  .hint {
    font-size: var(--font-label);
    opacity: 0.55;
    padding: 0 calc(var(--layout-unit) * 0.5) calc(var(--layout-unit) * 1);
  }

  .row {
    display: flex;
    align-items: center;
    gap: var(--gap);
    width: 100%;
    min-height: var(--tap-min);
    padding: calc(var(--layout-unit) * 1.4) calc(var(--layout-unit) * 2);
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    color: var(--text-color);
    text-align: left;
    transition: background 120ms, border-color 120ms;
  }

  .row.pinned {
    border-color: var(--accent-color);
    background: color-mix(in srgb, var(--accent-color) 12%, var(--surface));
  }

  .check {
    width: calc(var(--layout-unit) * 3.5);
    height: calc(var(--layout-unit) * 3.5);
    border-radius: 6px;
    border: 1.5px solid color-mix(in srgb, var(--text-color) 30%, transparent);
    display: grid;
    place-items: center;
    flex-shrink: 0;
    color: var(--bg-color);
  }

  .row.pinned .check {
    background: var(--accent-color);
    border-color: var(--accent-color);
  }

  .check svg {
    width: 70%;
    height: 70%;
  }

  .names {
    flex: 1 1 auto;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.3);
  }

  .short {
    font-size: var(--font-body);
    font-weight: 600;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .full {
    font-size: calc(var(--font-label) * 0.95);
    opacity: 0.5;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .badge {
    font-size: var(--font-label);
    text-transform: uppercase;
    letter-spacing: 0.08em;
    opacity: 0.6;
    flex-shrink: 0;
    padding: calc(var(--layout-unit) * 0.4) calc(var(--layout-unit) * 1);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
  }
</style>
