<script lang="ts">
  import { getIcueProperty } from '@betterxeneon/shared';
  import NowPlaying from './components/NowPlaying.svelte';
  import TransportControls from './components/TransportControls.svelte';
  import SpotifyQuadrants from './components/SpotifyQuadrants.svelte';
  import SpotifyModal from './components/SpotifyModal.svelte';
  import ListeningView from './components/ListeningView.svelte';
  import { appStore } from './state.ts';

  // Reactive iCUE option — poll the global directly into $state. Doing this
  // via a svelte/store readable + $effect.subscribe had interop issues with
  // the $derived that consumed it. Plain $state + setInterval is bulletproof.
  let useAlbumArtBg = $state<boolean | undefined>(getIcueProperty<boolean>('useAlbumArtBackground'));
  $effect(() => {
    const id = setInterval(() => {
      const v = getIcueProperty<boolean>('useAlbumArtBackground');
      if (v !== useAlbumArtBg) useAlbumArtBg = v;
    }, 1000);
    return () => clearInterval(id);
  });
  // Album art background follows the same source reconciliation as the
  // foreground now-playing views. A non-Spotify local SMTC session wins over
  // Spotify Web API state so a paused YouTube video doesn't randomly inherit
  // stale Spotify art behind it.
  let albumArtBgUrl = $derived.by(() => {
    const np = $appStore.nowPlaying;
    const playback = $appStore.spotifyPlayback;
    const smtcHasSession = np?.hasSession ?? false;
    const smtcIsSpotify = np?.isSpotify ?? false;
    const smtcActivelyPlaying = smtcHasSession && np?.status === 'Playing';
    const useSpotifyArt = !!playback?.hasSession
      && (!smtcHasSession || (smtcIsSpotify && !smtcActivelyPlaying));

    if (useSpotifyArt && playback?.albumArtUrl) return playback.albumArtUrl;
    if (np?.hasSession && np.hasArt) {
      return `http://127.0.0.1:8976/api/media/album-art?v=${np.artVersion}`;
    }
    return null;
  });
  let showArtBg = $derived(useAlbumArtBg !== false && !!albumArtBgUrl);
  let artBgSrc = $state<string | null>(null);
  $effect(() => {
    if (albumArtBgUrl) artBgSrc = albumArtBgUrl;
  });

  // Bottom Spotify row visible in medium mode whenever Spotify is "the source"
  // somewhere — local SMTC reports Spotify (whether or not authed → Connect
  // CTA when not), or authed + remote Spotify session.
  let showSpotifyRow = $derived(
    !!$appStore.nowPlaying?.isSpotify
    || (!!$appStore.spotifyAuthed && !!$appStore.spotifyPlayback?.hasSession)
  );

  // 'listening' displayMode means the inactivity timer fired. In large mode
  // we use it to hide controls (media mode); medium mode ignores it.
  let mediaMode = $derived($appStore.displayMode === 'listening');

  // Size class — switches layout. We measure the actual root element via
  // ResizeObserver since iCUE's QtWebEngine doesn't reliably fire window
  // resize when the cell changes on the dashboard.
  //
  // Switching on width alone breaks: Xeneon Edge has Small horizontal
  // (840×344) AND Medium horizontal (840×696) — same width, very different
  // shapes. The wide-strip listening layout (art + metadata + lyrics)
  // only makes sense at ≥ 2:1. The kit's actual sizes:
  //
  //              Horizontal    Aspect       Vertical    Aspect
  //   Small      840×344       2.44  large  696×416     1.67  medium
  //   Medium     840×696       1.21  medium 696×840     0.83  medium
  //   Large      1688×696      2.43  large  696×1688    0.41  medium
  //   X-Large    2536×696      3.64  large  696×2536    0.27  medium
  //
  // Aspect-ratio threshold of 2.0 cleanly separates "wide strip" from
  // "square / vertical".
  let rootEl = $state<HTMLElement | null>(null);
  let measuredWidth = $state(840);
  let measuredHeight = $state(344);
  $effect(() => {
    if (!rootEl) return;
    const obs = new ResizeObserver(entries => {
      for (const entry of entries) {
        measuredWidth = entry.contentRect.width;
        measuredHeight = entry.contentRect.height;
      }
    });
    obs.observe(rootEl);
    return () => obs.disconnect();
  });
  let isLarge = $derived(measuredWidth / Math.max(1, measuredHeight) >= 2.0);
  let sizeClass = $derived(isLarge ? 'size-large' : 'size-medium');
</script>

<main class="root {sizeClass}" class:two-row={!showSpotifyRow} bind:this={rootEl}>
  <div class="bg" aria-hidden="true"></div>
  {#if artBgSrc}
    <img class="art-bg" class:show={showArtBg} aria-hidden="true" src={artBgSrc} alt="">
  {/if}

  {#if !$appStore.connected}
    <div class="placeholder" class:error={$appStore.error}>
      <div class="placeholder-title">{$appStore.error ? 'Host offline' : 'Connecting…'}</div>
      {#if $appStore.error}
        <div class="placeholder-sub">Start <code>BetterXeneonWidget.Host</code> on 127.0.0.1:8976</div>
      {/if}
    </div>
  {:else if isLarge}
    <!-- Large: ListeningView is the only layout. Controls fade out when
         media mode kicks in, then reappear on the next tap. -->
    <section class="listening-pane">
      <ListeningView hideControls={mediaMode} />
    </section>
  {:else}
    <!-- Medium: original 3-row layout — now-playing card, transport, then
         the bottom row with the queue list + Queue/Playlists buttons. No
         media mode in medium; controls are always visible here. -->
    <section class="np-row">
      <NowPlaying />
    </section>

    <section class="transport-row">
      <TransportControls />
    </section>

    {#if showSpotifyRow}
      <section class="spotify-row">
        <SpotifyQuadrants />
      </section>
    {/if}
  {/if}

  {#if $appStore.modalView !== 'none'}
    <SpotifyModal />
  {/if}
</main>

<style>
  .root {
    position: relative;
    isolation: isolate;
    width: 100%;
    height: 100%;
    display: grid;
    gap: var(--gap);
    padding: var(--pad);
  }

  /* Medium: 3-row browse layout. */
  .root.size-medium {
    grid-template-rows: minmax(0, 1.1fr) minmax(0, 0.75fr) minmax(0, 1.4fr);
  }
  .root.size-medium.two-row {
    grid-template-rows: minmax(0, 1.6fr) minmax(0, 1fr);
  }

  /* Large: single full-bleed listening pane. */
  .root.size-large {
    grid-template-rows: 1fr;
    grid-template-columns: 1fr;
  }

  .bg {
    position: absolute;
    inset: 0;
    background-color: var(--bg-color);
    opacity: var(--bg-opacity, 1);
    pointer-events: none;
    z-index: -2;
  }

  /* Album art background — use an <img> rather than swapping a CSS
     background-image under a heavy filter; the repo's Edge flicker tests
     isolated that production combo as the risky rasterization path. */
  .art-bg {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    object-fit: cover;
    filter: blur(40px) brightness(0.45) saturate(1.1);
    transform: scale(1.2); /* hide blur edges */
    pointer-events: none;
    z-index: -1;
    opacity: 0;
    transition: opacity 300ms ease;
  }

  .art-bg.show {
    opacity: 1;
  }

  .np-row,
  .transport-row,
  .spotify-row,
  .listening-pane {
    min-height: 0;
    min-width: 0;
  }

  .placeholder {
    grid-column: 1 / -1;
    grid-row: 1 / -1;
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
