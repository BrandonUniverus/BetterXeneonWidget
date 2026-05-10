<script lang="ts">
  import { appStore, type View } from '../state.ts';

  function setView(v: View): void {
    appStore.update(s => ({ ...s, view: v }));
  }
</script>

<nav class="tabs" aria-label="View switcher">
  <div class="primary">
    <button class="tab" class:active={$appStore.view === 'outputs'} type="button" onclick={() => setView('outputs')}>Outputs</button>
    <button class="tab" class:active={$appStore.view === 'apps'} type="button" onclick={() => setView('apps')}>Apps</button>
  </div>
  <button
    class="gear"
    class:active={$appStore.view === 'settings'}
    type="button"
    onclick={() => setView($appStore.view === 'settings' ? 'outputs' : 'settings')}
    aria-label="Pin manager"
  >
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <circle cx="12" cy="12" r="3"/>
      <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>
    </svg>
  </button>
</nav>

<style>
  .tabs {
    display: flex;
    align-items: center;
    gap: var(--gap);
    flex: 0 0 auto;
    padding-top: calc(var(--layout-unit) * 0.5);
  }

  .primary {
    display: flex;
    gap: calc(var(--layout-unit) * 1);
    flex: 1 1 auto;
  }

  .tab {
    flex: 1 1 auto;
    height: var(--tap-min);
    padding: 0 calc(var(--layout-unit) * 2.5);
    border-radius: var(--radius);
    background: var(--surface);
    border: 1px solid var(--border);
    color: var(--text-color);
    font-size: var(--font-body);
    font-weight: 600;
    transition: background 120ms, border-color 120ms;
  }

  .tab.active {
    background: var(--accent-color);
    border-color: var(--accent-color);
    color: var(--bg-color);
  }

  .gear {
    width: var(--tap-min);
    height: var(--tap-min);
    display: grid;
    place-items: center;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    color: var(--text-color);
    flex-shrink: 0;
    transition: background 120ms, border-color 120ms;
  }

  .gear.active {
    background: var(--accent-color);
    border-color: var(--accent-color);
    color: var(--bg-color);
  }

  .gear svg {
    width: 50%;
    height: 50%;
  }
</style>
