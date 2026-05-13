<script lang="ts">
  import { shortenDeviceNames } from '../name-shortener.ts';
  import { appStore } from '../state.ts';
  import OutputCard from './OutputCard.svelte';
  import SteelSeriesSwapRow from './SteelSeriesSwapRow.svelte';
</script>

<section class="view" aria-label="Pinned audio outputs">
  {#if $appStore.devices.length === 0}
    <div class="empty">
      <div class="empty-title">{$appStore.error ? 'Host offline' : 'Connecting…'}</div>
      {#if $appStore.error}
        <div class="empty-sub">Start <code>BetterXeneonWidget.Host</code> on 127.0.0.1:8976</div>
      {/if}
    </div>
  {:else if $appStore.devices.filter(d => $appStore.pinnedIds.includes(d.id)).length === 0}
    <div class="empty">
      <div class="empty-title">No outputs pinned</div>
      <div class="empty-sub">Open settings (gear) to pick which devices appear here.</div>
    </div>
  {:else}
    <!-- Renders nothing when Sonar isn't reachable, so the pinned cards
         keep their normal layout for non-SteelSeries setups. -->
    <SteelSeriesSwapRow />
    {#each $appStore.devices.filter(d => $appStore.pinnedIds.includes(d.id)) as device (device.id)}
      <OutputCard {device} displayName={shortenDeviceNames($appStore.devices).get(device.id) ?? device.name} />
    {/each}
  {/if}
</section>

<style>
  .view {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    gap: calc(var(--gap) * 0.7);
    overflow-y: auto;
    padding-right: calc(var(--layout-unit) * 0.5);
    scrollbar-width: thin;
    scrollbar-color: var(--surface-strong) transparent;
  }

  .empty {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: calc(var(--layout-unit) * 1);
    text-align: center;
    opacity: 0.6;
  }

  .empty-title {
    font-size: var(--font-title);
    font-weight: 700;
  }

  .empty-sub {
    font-size: var(--font-label);
    opacity: 0.8;
    max-width: 80%;
  }

  code {
    font-family: 'Cascadia Code', Consolas, ui-monospace, monospace;
    font-size: 0.92em;
    padding: 0 calc(var(--layout-unit) * 0.6);
    background: var(--surface);
    border-radius: var(--radius-sm);
  }
</style>
