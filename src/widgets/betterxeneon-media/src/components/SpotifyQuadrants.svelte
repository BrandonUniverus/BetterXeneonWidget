<script lang="ts">
  import type { SpotifyPlaylist, SpotifyQueueItem } from '@betterxeneon/shared';
  import { host } from '../api.ts';
  import { refreshPlaylistsNow, refreshSpotifyNow } from '../polling.ts';
  import { appStore } from '../state.ts';

  // When `compactActions` is set, we render only the Queue + Playlists
  // buttons (no queue list, no Connect CTA mid-banner). Used by the
  // listening-mode layout where the expanded now-playing takes the rest
  // of the canvas.
  let { compactActions = false } = $props<{ compactActions?: boolean }>();

  // In-flight guards. Without these, a slightly-held tap on the touchscreen
  // delivers multiple click events to the same element and we'd queue
  // duplicate API calls (causing the "queue/library randomly stops working"
  // and "click acts like a hold" symptoms).
  let trackClickInFlight = new Set<string>();
  let playlistClickInFlight = new Set<string>();
  let modalOpenInFlight = $state(false);

  async function onConnect(): Promise<void> {
    if ($appStore.spotifyConnecting) return;
    appStore.update(s => ({ ...s, spotifyConnecting: true }));
    try {
      await host.spotifyConnect();
    } catch {
      appStore.update(s => ({ ...s, spotifyConnecting: false }));
    }
  }

  async function playTrack(t: SpotifyQueueItem): Promise<void> {
    if (trackClickInFlight.has(t.id)) return;
    trackClickInFlight.add(t.id);
    try { await host.playSpotifyTrack(t.id); }
    catch { /* surfaced via banner */ }
    finally { trackClickInFlight.delete(t.id); }
  }

  async function playPlaylist(p: SpotifyPlaylist): Promise<void> {
    if (playlistClickInFlight.has(p.id)) return;
    playlistClickInFlight.add(p.id);
    try { await host.playSpotifyPlaylist(p.id); }
    catch { /* surfaced via banner */ }
    finally { playlistClickInFlight.delete(p.id); }
  }

  function openQueueModal(): void {
    if (modalOpenInFlight) return;
    modalOpenInFlight = true;
    appStore.update(s => ({ ...s, modalView: 'queue' }));
    refreshSpotifyNow();
    setTimeout(() => { modalOpenInFlight = false; }, 250);
  }

  function openLibraryModal(): void {
    if (modalOpenInFlight) return;
    modalOpenInFlight = true;
    appStore.update(s => ({ ...s, modalView: 'library' }));
    // Library is the only place we display playlists — refresh just those,
    // not queue/recently. Server caches for 30 min and honors Spotify's
    // 429 backoff, so this is safe to call on every open.
    refreshPlaylistsNow();
    setTimeout(() => { modalOpenInFlight = false; }, 250);
  }

  // Bottom-row left list: just the upcoming queue. "Last played" was removed
  // because Spotify Web API's recently-played feed is laggy (sometimes 30s+
  // behind the actual change) and it confused the display every time you hit
  // Next.
  let topPlaylists = $derived($appStore.spotifyPlaylists);
  let authed = $derived($appStore.spotifyAuthed);
  let connecting = $derived($appStore.spotifyConnecting);
</script>

{#if compactActions}
  <!-- Listening-mode: just the Playlists button as a single full-height
       column. Queue button is dropped because the queue list (or lyrics)
       is rendered inline in ListeningView's metadata column. -->
  <div class="actions single">
    {#if authed}
      <button class="action" type="button" onclick={openLibraryModal} aria-label="Open Playlists">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <line x1="8" y1="6" x2="21" y2="6"/>
          <line x1="8" y1="12" x2="21" y2="12"/>
          <line x1="8" y1="18" x2="21" y2="18"/>
          <line x1="3" y1="6" x2="3.01" y2="6"/>
          <line x1="3" y1="12" x2="3.01" y2="12"/>
          <line x1="3" y1="18" x2="3.01" y2="18"/>
        </svg>
        <span>Playlists</span>
      </button>
    {:else}
      <button class="action connect-compact" type="button" onclick={onConnect} disabled={connecting}>
        <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
          <circle cx="12" cy="12" r="10"/>
          <path d="M16.7 16.5c-.2.3-.6.4-.9.2-2.5-1.5-5.6-1.9-9.3-1-.4.1-.7-.2-.8-.5-.1-.4.2-.7.5-.8 4-.9 7.5-.5 10.3 1.2.3.2.4.6.2.9zm1.2-2.7c-.2.4-.7.5-1.1.3-2.8-1.7-7.1-2.2-10.5-1.2-.4.1-.9-.1-1-.6-.1-.4.1-.9.6-1 3.8-1.2 8.5-.6 11.7 1.4.4.2.5.7.3 1.1zm.1-2.8C14.9 9 9.4 8.7 6.2 9.7c-.5.2-1.1-.1-1.2-.6-.2-.5.1-1.1.6-1.2 3.7-1.1 9.7-.9 13.4 1.3.5.3.6.9.4 1.4-.3.4-.9.6-1.4.4z" fill="#0a0a0c"/>
        </svg>
        <span>{connecting ? 'Opening…' : 'Connect'}</span>
      </button>
    {/if}
  </div>
{:else if !authed}
  <button class="connect" type="button" onclick={onConnect} disabled={connecting}>
    <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <circle cx="12" cy="12" r="10"/>
      <path d="M16.7 16.5c-.2.3-.6.4-.9.2-2.5-1.5-5.6-1.9-9.3-1-.4.1-.7-.2-.8-.5-.1-.4.2-.7.5-.8 4-.9 7.5-.5 10.3 1.2.3.2.4.6.2.9zm1.2-2.7c-.2.4-.7.5-1.1.3-2.8-1.7-7.1-2.2-10.5-1.2-.4.1-.9-.1-1-.6-.1-.4.1-.9.6-1 3.8-1.2 8.5-.6 11.7 1.4.4.2.5.7.3 1.1zm.1-2.8C14.9 9 9.4 8.7 6.2 9.7c-.5.2-1.1-.1-1.2-.6-.2-.5.1-1.1.6-1.2 3.7-1.1 9.7-.9 13.4 1.3.5.3.6.9.4 1.4-.3.4-.9.6-1.4.4z" fill="#0a0a0c"/>
    </svg>
    <div class="connect-text">
      <span class="connect-headline">{connecting ? 'Opening browser…' : 'Connect Spotify'}</span>
      <span class="connect-sub">{connecting ? 'Sign in, then return here' : 'Tap to sign in for Queue + Playlists'}</span>
    </div>
  </button>
{:else}
  <div class="row">
    <!-- LEFT 50%: single queue list (was split with playlists; the button on
         the right opens the full playlists modal so this gets the full
         left-half width for song titles + artists) -->
    <div class="quadrant">
      <div class="section-label">Up next</div>
      <div class="scroll">
        {#if $appStore.spotifyQueue.length === 0}
          <div class="empty">Queue empty</div>
        {:else}
          {#each $appStore.spotifyQueue as t (t.id)}
            <button class="track" type="button" onclick={() => playTrack(t)}>
              <span class="badge queue" aria-hidden="true">↑</span>
              <span class="text">
                <span class="title">{t.title}</span><span class="dim"> — {t.artist}</span>
              </span>
            </button>
          {/each}
        {/if}
      </div>
    </div>

    <!-- RIGHT 50%: stacked Queue / Playlists buttons -->
    <div class="actions">
      <button class="action" type="button" onclick={openQueueModal} aria-label="Open Queue">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <line x1="3" y1="6" x2="14" y2="6"/>
          <line x1="3" y1="12" x2="14" y2="12"/>
          <line x1="3" y1="18" x2="9" y2="18"/>
          <polygon points="17,15 22,18 17,21" fill="currentColor" stroke="none"/>
        </svg>
        <span>Queue</span>
      </button>
      <button class="action" type="button" onclick={openLibraryModal} aria-label="Open Playlists">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <line x1="8" y1="6" x2="21" y2="6"/>
          <line x1="8" y1="12" x2="21" y2="12"/>
          <line x1="8" y1="18" x2="21" y2="18"/>
          <line x1="3" y1="6" x2="3.01" y2="6"/>
          <line x1="3" y1="12" x2="3.01" y2="12"/>
          <line x1="3" y1="18" x2="3.01" y2="18"/>
        </svg>
        <span>Playlists</span>
      </button>
    </div>
  </div>
{/if}

<style>
  .row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
    gap: var(--gap);
    height: 100%;
    min-height: 0;
  }

  .quadrant {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.4);
    padding: calc(var(--layout-unit) * 0.8) calc(var(--layout-unit) * 1.2);
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    min-height: 0;
    min-width: 0;
    overflow: hidden;
  }

  .section-label {
    font-size: calc(var(--font-label) * 0.85);
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    opacity: 0.55;
    line-height: 1;
    flex: 0 0 auto;
  }

  /* Inner scroll region — fills remaining space inside the quadrant. */
  .scroll {
    flex: 1 1 auto;
    overflow-y: auto;
    overflow-x: hidden;
    min-height: 0;
    touch-action: pan-y;
    -webkit-overflow-scrolling: touch;
    scrollbar-width: thin;
    scrollbar-color: rgba(255, 255, 255, 0.18) transparent;
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.2);
  }

  .scroll::-webkit-scrollbar { width: 4px; }
  .scroll::-webkit-scrollbar-thumb {
    background: rgba(255, 255, 255, 0.18);
    border-radius: 2px;
  }
  .scroll::-webkit-scrollbar-track { background: transparent; }

  .track {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    align-items: center;
    gap: calc(var(--layout-unit) * 0.7);
    width: 100%;
    padding: calc(var(--layout-unit) * 0.6) calc(var(--layout-unit) * 0.4);
    background: transparent;
    border: 0;
    border-radius: var(--radius-sm);
    color: var(--text-color);
    text-align: left;
    font-size: var(--font-label);
    line-height: 1.15;
    touch-action: manipulation;
    transition: background 100ms;
    min-width: 0;
  }

  .track:hover { background: var(--surface-strong); }
  .track:active { background: var(--surface-strong); }
  .track > * { pointer-events: none; }

  .badge {
    display: inline-grid;
    place-items: center;
    width: calc(var(--layout-unit) * 2.2);
    height: calc(var(--layout-unit) * 2.2);
    border-radius: 50%;
    font-size: calc(var(--font-label) * 0.85);
    font-weight: 700;
    line-height: 1;
    flex-shrink: 0;
  }

  .badge.queue  { background: color-mix(in srgb, var(--spotify-green) 22%, transparent); color: var(--spotify-green); }
  .badge.recent { background: rgba(255,255,255,0.10); color: rgba(255,255,255,0.7); }
  .badge.list   { background: color-mix(in srgb, var(--spotify-green) 18%, transparent); color: var(--spotify-green); }

  .text {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }

  .title { font-weight: 600; }
  .dim   { opacity: 0.6; font-weight: 500; }

  .empty {
    font-size: calc(var(--font-label) * 0.95);
    opacity: 0.55;
    padding: calc(var(--layout-unit) * 0.4);
    text-align: center;
  }

  /* Right pane — large Queue / Playlists buttons. */
  .actions {
    display: grid;
    grid-template-rows: 1fr 1fr;
    gap: var(--gap);
    min-height: 0;
    min-width: 0;
  }

  /* Listening-mode single-button variant — Playlists fills the whole
     right column. */
  .actions.single {
    display: grid;
    grid-template-rows: 1fr;
    height: 100%;
  }

  .connect-compact {
    background: var(--spotify-green);
    color: #08130c;
    border-color: var(--spotify-green);
  }
  .connect-compact:hover { background: var(--spotify-green); filter: brightness(0.95); }
  .connect-compact:active { background: var(--spotify-green); filter: brightness(0.9); }
  .connect-compact svg { color: #08130c; }

  .action {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: calc(var(--layout-unit) * 1.2);
    padding: 0 calc(var(--layout-unit) * 2);
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    color: var(--text-color);
    font-size: var(--font-body);
    font-weight: 700;
    min-height: 0;
    min-width: 0;
    touch-action: manipulation;
    transition: background 100ms;
  }

  .action:hover { background: var(--surface-strong); }
  .action:active { background: var(--surface-strong); }
  .action > * { pointer-events: none; }

  .action svg {
    width: calc(var(--layout-unit) * 3.4);
    height: calc(var(--layout-unit) * 3.4);
    color: var(--spotify-green);
    flex-shrink: 0;
  }

  /* --- Connect CTA (unauthed state) ---------------------------------
     Card-style: dark surface BG with a green pill on the right, instead of
     a single full-bleed neon-green block. Easier on the eyes and matches
     the rest of the widget's quadrant aesthetic. */
  .connect {
    display: grid;
    grid-template-columns: auto 1fr auto;
    align-items: center;
    gap: var(--gap);
    width: 100%;
    height: 100%;
    padding: 0 calc(var(--layout-unit) * 2.5);
    background: var(--surface);
    color: var(--text-color);
    border: 1px solid color-mix(in srgb, var(--spotify-green) 30%, var(--border));
    border-radius: var(--radius);
    text-align: left;
    transition: background 100ms, transform 80ms, border-color 100ms;
  }

  .connect:hover { background: var(--surface-strong); }
  .connect:active { transform: scale(0.995); background: var(--surface-strong); }
  .connect:disabled { opacity: 0.7; }
  .connect:disabled:active { transform: none; }

  .connect svg {
    width: clamp(24px, calc(var(--layout-unit) * 5.5), 64px);
    height: clamp(24px, calc(var(--layout-unit) * 5.5), 64px);
    color: var(--spotify-green);
    flex-shrink: 0;
  }

  .connect-text {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.2);
    min-width: 0;
  }

  .connect-headline {
    font-size: var(--font-body);
    font-weight: 700;
  }

  .connect-sub {
    font-size: var(--font-label);
    opacity: 0.65;
  }

  .connect::after {
    content: 'Sign in';
    grid-column: 3;
    padding: calc(var(--layout-unit) * 1) calc(var(--layout-unit) * 2.4);
    background: var(--spotify-green);
    color: #08130c;
    border-radius: 999px;
    font-size: var(--font-label);
    font-weight: 700;
    letter-spacing: 0.02em;
    flex-shrink: 0;
  }

  .connect:disabled::after {
    content: 'Working…';
    background: color-mix(in srgb, var(--spotify-green) 60%, var(--surface));
    color: var(--text-color);
  }
</style>
