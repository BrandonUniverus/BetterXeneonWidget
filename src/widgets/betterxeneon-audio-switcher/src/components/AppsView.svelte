<script lang="ts">
  import { shortenDeviceNames } from '../name-shortener.ts';
  import { appStore } from '../state.ts';
  import AppRow from './AppRow.svelte';

  // Threshold below which a session is considered "not currently producing
  // sound" for sorting purposes. Sessions above this go to the top of the
  // list — followed by sessions that *recently* made sound (sorted by how
  // recently), then everything else alphabetically. Same threshold as the
  // one used in polling.ts so a session that just stopped doesn't oscillate
  // around the boundary.
  const SOUND_THRESHOLD = 0.01;
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
          // 1. Currently producing sound (peak above threshold) bubbles up.
          const aAudible = a.peak > SOUND_THRESHOLD;
          const bAudible = b.peak > SOUND_THRESHOLD;
          if (aAudible && !bAudible) return -1;
          if (!aAudible && bAudible) return 1;

          // 2. Among silent sessions, the one that made sound most recently
          //    comes first. Sessions that have never made sound get 0, so
          //    they sort to the bottom.
          const aLast = $appStore.lastSoundAtBySessionId[a.id] ?? 0;
          const bLast = $appStore.lastSoundAtBySessionId[b.id] ?? 0;
          if (aLast !== bLast) return bLast - aLast;

          // 3. Final tiebreaker: alphabetical for stable ordering.
          return a.displayName.localeCompare(b.displayName);
        }) as session (session.id)}
      <AppRow
        {session}
        deviceLabel={shortenDeviceNames($appStore.devices).get(session.deviceId) ?? session.deviceName}
        lastSoundAt={$appStore.lastSoundAtBySessionId[session.id] ?? 0}
      />
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
