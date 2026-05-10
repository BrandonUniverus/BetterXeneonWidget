<script lang="ts">
  import { appStore } from '../state.ts';
</script>

<header class="banner">
  <div class="title">Audio Switcher</div>
  <div class="status" class:online={$appStore.connected} class:offline={!$appStore.connected}>
    <span class="dot" aria-hidden="true"></span>
    {$appStore.connected ? 'Connected' : $appStore.error ? 'Offline' : 'Connecting…'}
  </div>
</header>

<style>
  .banner {
    display: flex;
    align-items: center;
    justify-content: space-between;
    flex: 0 0 auto;
    gap: var(--gap);
  }

  .title {
    font-size: var(--font-title);
    font-weight: 700;
    letter-spacing: 0.02em;
  }

  .status {
    display: inline-flex;
    align-items: center;
    gap: calc(var(--layout-unit) * 1);
    font-size: var(--font-label);
    opacity: 0.7;
    flex-shrink: 0;
  }

  .dot {
    width: calc(var(--layout-unit) * 1.4);
    height: calc(var(--layout-unit) * 1.4);
    border-radius: 50%;
    background: var(--success);
    box-shadow: 0 0 calc(var(--layout-unit) * 1.5) currentColor;
  }

  .status.offline .dot {
    background: var(--error);
    box-shadow: none;
  }
</style>
