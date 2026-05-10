<script lang="ts">
  import StatusBanner from './components/StatusBanner.svelte';
  import OutputsView from './components/OutputsView.svelte';
  import AppsView from './components/AppsView.svelte';
  import SettingsView from './components/SettingsView.svelte';
  import TabBar from './components/TabBar.svelte';
  import { appStore } from './state.ts';
</script>

<main class="root">
  <div class="bg" aria-hidden="true"></div>

  <StatusBanner />

  <div class="view-area">
    {#if !$appStore.connected}
      <div class="placeholder" class:error={$appStore.error}>
        <div class="placeholder-title">{$appStore.error ? 'Host offline' : 'Connecting…'}</div>
        {#if $appStore.error}
          <div class="placeholder-sub">Start <code>BetterXeneonWidget.Host</code> on 127.0.0.1:8976</div>
        {/if}
      </div>
    {:else if $appStore.view === 'outputs'}
      <OutputsView />
    {:else if $appStore.view === 'apps'}
      <AppsView />
    {:else}
      <SettingsView />
    {/if}
  </div>

  <TabBar />
</main>

<style>
  .root {
    position: relative;
    isolation: isolate;
    width: 100%;
    height: 100%;
    display: flex;
    flex-direction: column;
    gap: var(--gap);
    padding: var(--pad);
  }

  .bg {
    position: absolute;
    inset: 0;
    background-color: var(--bg-color);
    opacity: var(--bg-opacity, 1);
    pointer-events: none;
    z-index: -1;
  }

  .view-area {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    min-height: 0;
  }

  .placeholder {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    text-align: center;
    gap: calc(var(--gap) * 0.6);
    opacity: 0.7;
    font-size: var(--font-body);
  }

  .placeholder-title {
    font-size: var(--font-title);
    font-weight: 700;
    opacity: 0.95;
  }

  .placeholder-sub {
    opacity: 0.7;
    font-size: var(--font-label);
  }

  .placeholder.error .placeholder-title {
    color: var(--error);
  }

  code {
    font-family: 'Cascadia Code', Consolas, ui-monospace, monospace;
    font-size: 0.92em;
    padding: 0 calc(var(--layout-unit) * 0.6);
    background: var(--surface);
    border-radius: var(--radius-sm);
  }
</style>
