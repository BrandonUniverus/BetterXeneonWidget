<script lang="ts">
  import { host } from '../api.ts';
  import { appStore } from '../state.ts';

  let imgError = $state(false);

  // Reset the error flag every time the cache-buster version changes so a new
  // track can fail or succeed independently.
  let lastVersion = $state(-1);
  $effect(() => {
    const v = $appStore.nowPlaying?.artVersion ?? 0;
    if (v !== lastVersion) {
      lastVersion = v;
      imgError = false;
    }
  });

  // --- Source reconciliation ----------------------------------------------
  //
  // SMTC sees only what's playing on this PC. The Spotify Web API sees what
  // the user's account is doing across all their devices. Pick the right one:
  //
  //   - SMTC is a non-Spotify session (browser, foobar, etc.) → use SMTC
  //                                          (we don't clobber YouTube just
  //                                          because Spotify is on a phone)
  //   - SMTC says local Spotify, currently playing            → use SMTC
  //   - SMTC says local Spotify but PAUSED, and Spotify Web
  //     API has active playback (could be on another device)  → use API
  //   - SMTC silent, Spotify Web API has playback             → use API
  //   - else                                                  → idle
  //
  // The device chip is only shown when we're rendering Spotify Web API data
  // (remote-source case). When SMTC is local Spotify, the API's reported
  // device might be a totally different machine — surfacing that name then
  // would be misleading.
  let smtcIsSpotify = $derived($appStore.nowPlaying?.isSpotify ?? false);
  let smtcHasSession = $derived($appStore.nowPlaying?.hasSession ?? false);
  let smtcStatus = $derived($appStore.nowPlaying?.status ?? 'Closed');
  let smtcActivelyPlaying = $derived(smtcHasSession && smtcStatus === 'Playing');
  let spotifyHasPlayback = $derived($appStore.spotifyPlayback?.hasSession ?? false);
  let useSpotifyAsSource = $derived(
    spotifyHasPlayback && (
      !smtcHasSession ||                              // SMTC silent → API
      (smtcIsSpotify && !smtcActivelyPlaying)         // local Spotify present but paused → API
    )
  );

  let title = $derived(useSpotifyAsSource
    ? ($appStore.spotifyPlayback?.title ?? '')
    : ($appStore.nowPlaying?.title || 'Nothing playing'));
  let artist = $derived(useSpotifyAsSource
    ? ($appStore.spotifyPlayback?.artist ?? '')
    : ($appStore.nowPlaying?.artist || ''));
  let album = $derived(useSpotifyAsSource
    ? ($appStore.spotifyPlayback?.album ?? '')
    : ($appStore.nowPlaying?.album || ''));

  // Album art: Spotify Web API gives a direct URL; SMTC art is served by the
  // host's /api/media/album-art endpoint with a cache-buster version.
  let smtcArtUrl = $derived(host.albumArtUrl($appStore.nowPlaying?.artVersion ?? 0));
  let smtcHasArt = $derived(($appStore.nowPlaying?.hasArt ?? false) && !imgError);
  let spotifyArtUrl = $derived($appStore.spotifyPlayback?.albumArtUrl ?? null);

  let artSrc = $derived(useSpotifyAsSource ? spotifyArtUrl : (smtcHasArt ? smtcArtUrl : null));

  // Idle = nothing in either source.
  let idle = $derived(!smtcHasSession && !spotifyHasPlayback);

  // Show the Spotify chip whenever Spotify is the active source (local or remote).
  let showSpotifyChip = $derived(smtcIsSpotify || useSpotifyAsSource);
  let deviceName = $derived($appStore.spotifyPlayback?.deviceName ?? '');
</script>

<section class="now-playing" class:idle>
  <div class="art-wrap">
    {#if artSrc}
      <img class="art" src={artSrc} alt="" onerror={() => (imgError = true)} />
    {:else}
      <div class="art placeholder" aria-hidden="true">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="12" cy="12" r="9"/>
          <circle cx="12" cy="12" r="3"/>
        </svg>
      </div>
    {/if}
  </div>

  <div class="meta">
    <div class="title" title={title}>{title}</div>
    {#if artist}
      <div class="artist" title={artist}>{artist}</div>
    {/if}
    {#if album && album !== artist && album !== title}
      <div class="album" title={album}>{album}</div>
    {/if}
    {#if showSpotifyChip}
      <div class="source" class:remote={useSpotifyAsSource}>
        <span class="source-dot" aria-hidden="true"></span>
        <span class="source-name">Spotify</span>
        <!-- Device name is only shown when we're actually rendering the
             Spotify Web API's snapshot (remote-source case). When the local
             SMTC track is the source, the API's device may be a totally
             different machine playing a totally different song — surfacing
             that name would be misleading. -->
        {#if useSpotifyAsSource && deviceName}
          <span class="source-divider" aria-hidden="true">·</span>
          <span class="source-device" title={deviceName}>{deviceName}</span>
        {/if}
      </div>
    {/if}
  </div>
</section>

<style>
  .now-playing {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: var(--gap);
    align-items: center;
    height: 100%;
    min-height: 0;
  }

  /* Square art slot. The IMG is absolutely positioned inside so its intrinsic
     dimensions don't bleed into grid auto-column sizing — without this the
     IMG drives the column to 300px wide (Spotify's thumb size) and overflows
     the row vertically. */
  .art-wrap {
    position: relative;
    aspect-ratio: 1;
    height: 100%;
    flex-shrink: 0;
    border-radius: var(--radius);
    overflow: hidden;
    background: var(--surface);
  }

  .art {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    object-fit: cover;
    display: block;
  }

  .art.placeholder {
    display: grid;
    place-items: center;
    color: var(--text-color);
    opacity: 0.35;
  }

  .art.placeholder svg {
    width: 50%;
    height: 50%;
  }

  .meta {
    display: flex;
    flex-direction: column;
    justify-content: center;
    gap: calc(var(--layout-unit) * 0.6);
    min-width: 0;
  }

  .title {
    font-size: var(--font-title);
    font-weight: 700;
    line-height: 1.1;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .artist {
    font-size: var(--font-body);
    opacity: 0.85;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .album {
    font-size: var(--font-label);
    opacity: 0.6;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .source {
    display: inline-flex;
    align-items: center;
    gap: calc(var(--layout-unit) * 0.8);
    font-size: var(--font-label);
    opacity: 0.78;
    margin-top: calc(var(--layout-unit) * 0.4);
    min-width: 0;
  }

  .source-dot {
    width: calc(var(--layout-unit) * 1.2);
    height: calc(var(--layout-unit) * 1.2);
    border-radius: 50%;
    background: var(--spotify-green);
    flex-shrink: 0;
  }

  /* When source is remote, fade the dot subtly to communicate "not local". */
  .source.remote .source-dot {
    background: color-mix(in srgb, var(--spotify-green) 70%, transparent);
    box-shadow: 0 0 0 1px var(--spotify-green) inset;
  }

  .source-name {
    font-weight: 600;
  }

  .source-divider {
    opacity: 0.45;
  }

  .source-device {
    opacity: 0.85;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
    max-width: 60%;
  }

  .now-playing.idle .title {
    opacity: 0.55;
    font-weight: 500;
  }
</style>
