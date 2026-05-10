<script lang="ts">
  import { shortenDeviceNames } from '../name-shortener.ts';
  import { appStore } from '../state.ts';
  import AppRow from './AppRow.svelte';
</script>

<section class="view" aria-label="App audio sessions">
  {#if (() => {
    const sessions = $appStore.pinnedIds.length > 0
      ? $appStore.sessions.filter(s => $appStore.pinnedIds.includes(s.deviceId))
      : $appStore.sessions;
    return sessions.length === 0;
  })()}
    <div class="empty">
      <div class="empty-title">Nothing playing</div>
      <div class="empty-sub">
        {$appStore.pinnedIds.length > 0 ? 'No apps are routing to your pinned outputs.' : 'No active audio sessions.'}
      </div>
    </div>
  {:else}
    {#each [...($appStore.pinnedIds.length > 0
        ? $appStore.sessions.filter(s => $appStore.pinnedIds.includes(s.deviceId))
        : $appStore.sessions)].sort((a, b) => {
          if (a.state === 'Active' && b.state !== 'Active') return -1;
          if (a.state !== 'Active' && b.state === 'Active') return 1;
          return a.displayName.localeCompare(b.displayName);
        }) as session (session.id)}
      <AppRow {session} deviceLabel={shortenDeviceNames($appStore.devices).get(session.deviceId) ?? session.deviceName} />
    {/each}
  {/if}
</section>

<style>
  .view {
    flex: 1 1 auto;
    display: flex;
    flex-direction: column;
    gap: calc(var(--gap) * 0.6);
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
</style>
