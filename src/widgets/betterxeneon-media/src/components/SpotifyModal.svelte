<script lang="ts">
  import type { SpotifyPlaylist, SpotifyQueueItem } from '@betterxeneon/shared';
  import { host } from '../api.ts';
  import { appStore } from '../state.ts';

  function close(): void {
    appStore.update(s => ({ ...s, modalView: 'none' }));
  }

  // In-flight guards (per-id) so a slightly-held tap doesn't double-fire.
  let trackInFlight = new Set<string>();
  let playlistInFlight = new Set<string>();

  async function playTrack(t: SpotifyQueueItem): Promise<void> {
    if (trackInFlight.has(t.id)) return;
    trackInFlight.add(t.id);
    try {
      await host.playSpotifyTrack(t.id);
      // Closing the modal after the play action lands keeps focus on
      // now-playing. The next poll will reflect the new state.
      close();
    } catch { /* surfaced via banner */ }
    finally { trackInFlight.delete(t.id); }
  }

  async function playPlaylist(p: SpotifyPlaylist): Promise<void> {
    if (playlistInFlight.has(p.id)) return;
    playlistInFlight.add(p.id);
    try {
      await host.playSpotifyPlaylist(p.id);
      close();
    } catch { /* surfaced via banner */ }
    finally { playlistInFlight.delete(p.id); }
  }

  let view = $derived($appStore.modalView);
  let title = $derived(view === 'queue' ? 'Queue' : 'Playlists');
  let queueItems = $derived($appStore.spotifyQueue);
  let recentItems = $derived($appStore.spotifyRecentlyPlayed);
  let playlists = $derived($appStore.spotifyPlaylists);

  let nowPlayingTitle = $derived(
    $appStore.spotifyPlayback?.title ?? $appStore.nowPlaying?.title ?? ''
  );
  let nowPlayingArtist = $derived(
    $appStore.spotifyPlayback?.artist ?? $appStore.nowPlaying?.artist ?? ''
  );
  let nowPlayingArtUrl = $derived($appStore.spotifyPlayback?.albumArtUrl ?? null);
</script>

<div class="overlay" role="dialog" aria-label={title} aria-modal="true">
  <header class="head">
    <h2 class="title">{title}</h2>
    <button class="close" type="button" onclick={close} aria-label="Close">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <line x1="6" y1="6" x2="18" y2="18"/>
        <line x1="18" y1="6" x2="6" y2="18"/>
      </svg>
    </button>
  </header>

  <div class="body">
    {#if view === 'queue'}
      {#if nowPlayingTitle}
        <div class="section">
          <div class="section-label">Now playing</div>
          <div class="row np" aria-disabled="true">
            <span class="art" aria-hidden="true">
              {#if nowPlayingArtUrl}
                <img src={nowPlayingArtUrl} alt="" />
              {/if}
            </span>
            <span class="text">
              <span class="row-title accent">{nowPlayingTitle}</span>
              <span class="row-sub">{nowPlayingArtist}</span>
            </span>
          </div>
        </div>
      {/if}

      <div class="section">
        <div class="section-label">Next up</div>
        {#if queueItems.length === 0}
          <div class="empty">Queue is empty</div>
        {:else}
          {#each queueItems as t (t.id)}
            <button class="row" type="button" onclick={() => playTrack(t)}>
              <span class="art" aria-hidden="true">
                {#if t.albumArtUrl}
                  <img src={t.albumArtUrl} alt="" />
                {/if}
              </span>
              <span class="text">
                <span class="row-title">{t.title}</span>
                <span class="row-sub">{t.artist}</span>
              </span>
            </button>
          {/each}
        {/if}
      </div>

      {#if recentItems.length > 0}
        <div class="section">
          <div class="section-label">Recently played</div>
          {#each recentItems as t (t.id)}
            <button class="row" type="button" onclick={() => playTrack(t)}>
              <span class="art" aria-hidden="true">
                {#if t.albumArtUrl}
                  <img src={t.albumArtUrl} alt="" />
                {/if}
              </span>
              <span class="text">
                <span class="row-title">{t.title}</span>
                <span class="row-sub">{t.artist}</span>
              </span>
            </button>
          {/each}
        </div>
      {/if}
    {:else if view === 'library'}
      <div class="section">
        <div class="section-label">Your playlists</div>
        {#if playlists.length === 0}
          <div class="empty">Loading…</div>
        {:else}
          {#each playlists as p (p.id)}
            <button class="row" type="button" onclick={() => playPlaylist(p)}>
              <span class="art square" aria-hidden="true">
                {#if p.imageUrl}
                  <img src={p.imageUrl} alt="" />
                {/if}
              </span>
              <span class="text">
                <span class="row-title">{p.name}</span>
                <span class="row-sub">{p.trackCount} tracks</span>
              </span>
            </button>
          {/each}
        {/if}
      </div>
    {/if}
  </div>
</div>

<style>
  .overlay {
    position: absolute;
    inset: 0;
    z-index: 10;
    display: grid;
    grid-template-rows: auto 1fr;
    background: var(--bg-color);
    /* Match the rounded outer shape iCUE renders for the widget container. */
    border-radius: var(--radius);
  }

  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: calc(var(--layout-unit) * 1.6) calc(var(--layout-unit) * 2.2)
            calc(var(--layout-unit) * 1) calc(var(--layout-unit) * 2.2);
    border-bottom: 1px solid var(--border);
    flex: 0 0 auto;
  }

  .title {
    margin: 0;
    font-size: var(--font-title);
    font-weight: 700;
    color: var(--spotify-green);
  }

  .close {
    width: var(--tap-min);
    height: var(--tap-min);
    display: grid;
    place-items: center;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    color: var(--text-color);
    touch-action: manipulation;
    transition: background 100ms;
  }

  .close:active { background: var(--surface-strong); }
  .close svg { width: 50%; height: 50%; pointer-events: none; }

  .body {
    overflow-y: auto;
    overflow-x: hidden;
    padding: calc(var(--layout-unit) * 1) calc(var(--layout-unit) * 2);
    /* Touch: vertical scroll only, with momentum. */
    touch-action: pan-y;
    -webkit-overflow-scrolling: touch;
    /* Hide scrollbar but keep scrolling functional. */
    scrollbar-width: thin;
    scrollbar-color: rgba(255, 255, 255, 0.18) transparent;
  }

  .body::-webkit-scrollbar { width: 6px; }
  .body::-webkit-scrollbar-thumb {
    background: rgba(255, 255, 255, 0.18);
    border-radius: 3px;
  }
  .body::-webkit-scrollbar-track { background: transparent; }

  .section {
    margin-bottom: calc(var(--layout-unit) * 1.2);
  }

  .section-label {
    font-size: var(--font-label);
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    opacity: 0.6;
    padding: calc(var(--layout-unit) * 0.6) 0 calc(var(--layout-unit) * 0.4);
  }

  .row {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: calc(var(--layout-unit) * 1.4);
    align-items: center;
    width: 100%;
    padding: calc(var(--layout-unit) * 0.8) calc(var(--layout-unit) * 0.6);
    background: transparent;
    border: 0;
    border-radius: var(--radius-sm);
    color: var(--text-color);
    text-align: left;
    touch-action: manipulation;
    transition: background 100ms;
  }

  .row:hover,
  .row:active {
    background: var(--surface);
  }

  /* Pin the click target to the button itself — children stealing the
     click was a real source of phantom missed-taps on the touchscreen. */
  .row > * { pointer-events: none; }

  .row.np { cursor: default; }

  .art {
    position: relative;
    width: calc(var(--layout-unit) * 5);
    height: calc(var(--layout-unit) * 5);
    background: linear-gradient(135deg, #2a2a36, #1a1a22);
    border-radius: var(--radius-sm);
    flex-shrink: 0;
    overflow: hidden;
  }

  .art img {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }

  .text {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.2);
    min-width: 0;
  }

  .row-title {
    font-size: var(--font-body);
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .row-title.accent {
    color: var(--spotify-green);
  }

  .row-sub {
    font-size: var(--font-label);
    opacity: 0.65;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .empty {
    padding: calc(var(--layout-unit) * 2) 0;
    opacity: 0.55;
    font-size: var(--font-label);
    text-align: center;
  }
</style>
