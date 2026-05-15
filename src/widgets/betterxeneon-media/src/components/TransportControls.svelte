<script lang="ts">
  import { host } from '../api.ts';
  import { appStore } from '../state.ts';

  // Optimistic playback flip. The host poll is 1.5s by default; flipping the
  // icon synchronously on press keeps the touch UX snappy.
  function setOptimistic(playing: boolean): void {
    appStore.update(s => ({
      ...s,
      optimisticPlay: playing,
      optimisticUntil: performance.now() + 3000,
    }));
  }

  // Transport routes to whichever source the now-playing display is showing.
  // Rule: prefer LOCAL SMTC whenever it has a session — even if it's Spotify.
  // SMTC's Play/Pause works without hitting Spotify's Web API (no rate limit,
  // no network round-trip). Spotify Web API is only needed for true cross-
  // device control (controlling music playing on a phone, etc.), which is the
  // case when SMTC is silent, Spotify API is already the rendered source, or
  // local Spotify's SMTC commands have gone stale after sitting idle.
  let smtcHasSession = $derived($appStore.nowPlaying?.hasSession ?? false);
  let smtcIsSpotify = $derived($appStore.nowPlaying?.isSpotify ?? false);
  let smtcStatus = $derived($appStore.nowPlaying?.status ?? 'Closed');
  let smtcActivelyPlaying = $derived(smtcHasSession && smtcStatus === 'Playing');
  let spotifyHasPlayback = $derived($appStore.spotifyPlayback?.hasSession ?? false);
  let spotifyTransportAvailable = $derived(
    $appStore.spotifyAuthed && spotifyHasPlayback && (!smtcHasSession || smtcIsSpotify)
  );
  let useSpotifyTransport = $derived(
    spotifyTransportAvailable && (!smtcHasSession || (smtcIsSpotify && !smtcActivelyPlaying))
  );

  // In-flight guard. Prevents rapid touchscreen tap-spam from queueing a stack
  // of API calls (which iCUE's QtWebEngine seems to deliver as repeated clicks
  // when a tap is even slightly held). Each transport handler returns
  // immediately if a previous call hasn't completed.
  let prevInFlight = $state(false);
  let toggleInFlight = $state(false);
  let nextInFlight = $state(false);

  async function onPrevious(): Promise<void> {
    if (prevInFlight) return;
    prevInFlight = true;
    try {
      const canUseSmtc = smtcHasSession && ($appStore.nowPlaying?.canGoPrevious ?? false);
      if (!useSpotifyTransport && canUseSmtc) {
        try {
          await host.mediaPrevious();
          return;
        } catch {
          if (!spotifyTransportAvailable) throw new Error('SMTC previous failed');
        }
      }
      if (spotifyTransportAvailable) await host.spotifyPrevious();
      else await host.mediaPrevious();
    } catch { /* surfaced via banner */ }
    finally { prevInFlight = false; }
  }

  async function onToggle(): Promise<void> {
    if (toggleInFlight) return;
    // Capture the playing state BEFORE flipping the optimistic flag — the
    // derived `isPlaying` re-reads optimisticPlay reactively, so reading it
    // after setOptimistic gives the inverted value (we'd call Resume when
    // the user wanted Pause). Snapshot first, then flip the UI.
    const wasPlaying = isPlaying;
    toggleInFlight = true;
    setOptimistic(!wasPlaying);
    try {
      const canUseSmtc = smtcHasSession
        && (($appStore.nowPlaying?.canPlay ?? false) || ($appStore.nowPlaying?.canPause ?? false));
      if (!useSpotifyTransport && canUseSmtc) {
        try {
          await host.mediaToggle();
          return;
        } catch {
          if (!spotifyTransportAvailable) throw new Error('SMTC toggle failed');
        }
      }
      if (spotifyTransportAvailable) {
        if (wasPlaying) await host.spotifyPause();
        else await host.spotifyResume();
      } else {
        await host.mediaToggle();
      }
    } catch {
      // Revert if the call failed.
      appStore.update(s => ({ ...s, optimisticPlay: null, optimisticUntil: 0 }));
    } finally {
      toggleInFlight = false;
    }
  }

  async function onNext(): Promise<void> {
    if (nextInFlight) return;
    nextInFlight = true;
    try {
      const canUseSmtc = smtcHasSession && ($appStore.nowPlaying?.canGoNext ?? false);
      if (!useSpotifyTransport && canUseSmtc) {
        try {
          await host.mediaNext();
          return;
        } catch {
          if (!spotifyTransportAvailable) throw new Error('SMTC next failed');
        }
      }
      if (spotifyTransportAvailable) await host.spotifyNext();
      else await host.mediaNext();
    } catch { /* surfaced via banner */ }
    finally { nextInFlight = false; }
  }

  // Playing/paused: prefer Spotify Web API state when it's the active source
  // (it's authoritative across devices), otherwise SMTC.
  let isPlaying = $derived(
    $appStore.optimisticPlay !== null
      ? $appStore.optimisticPlay
      : useSpotifyTransport
        ? ($appStore.spotifyPlayback?.isPlaying ?? false)
        : smtcStatus === 'Playing'
  );

  // Capabilities: when Spotify is the source, assume all transport works
  // (Spotify Web API doesn't expose per-track capability flags). When SMTC,
  // honor the SMTC-reported flags.
  let canPrev = $derived(spotifyTransportAvailable ? true : ($appStore.nowPlaying?.canGoPrevious ?? false));
  let canNext = $derived(spotifyTransportAvailable ? true : ($appStore.nowPlaying?.canGoNext ?? false));
  let canToggle = $derived(spotifyTransportAvailable
    ? ($appStore.spotifyPlayback?.hasSession ?? false)
    : (($appStore.nowPlaying?.canPlay ?? false) || ($appStore.nowPlaying?.canPause ?? false)));

  // The central play/pause button uses Spotify green when source is Spotify.
  let isSpotify = $derived(smtcIsSpotify || useSpotifyTransport);
</script>

<div class="row" role="toolbar" aria-label="Playback controls">
  <button
    class="quad"
    type="button"
    onclick={onPrevious}
    aria-label="Previous track"
    disabled={!canPrev}
  >
    <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M6 5h2v14H6zM10 12l9-7v14z"/>
    </svg>
  </button>

  <button
    class="quad center"
    class:spotify={isSpotify}
    type="button"
    onclick={onToggle}
    aria-label={isPlaying ? 'Pause' : 'Play'}
    disabled={!canToggle}
  >
    {#if isPlaying}
      <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <rect x="6" y="5" width="4" height="14" rx="1"/>
        <rect x="14" y="5" width="4" height="14" rx="1"/>
      </svg>
    {:else}
      <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M7 5l13 7-13 7z"/>
      </svg>
    {/if}
  </button>

  <button
    class="quad"
    type="button"
    onclick={onNext}
    aria-label="Next track"
    disabled={!canNext}
  >
    <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M5 5l9 7-9 7zM16 5h2v14h-2z"/>
    </svg>
  </button>
</div>

<style>
  .row {
    display: grid;
    grid-template-columns: 1fr 1fr 1fr;
    gap: var(--gap);
    height: 100%;
    min-height: 0;
  }

  .quad {
    display: grid;
    place-items: center;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    color: var(--text-color);
    /* Override grid-item default of min-height/width: auto, which would
       otherwise force the quad to its SVG's min-content size and overflow
       the parent row track. */
    min-height: 0;
    min-width: 0;
    overflow: hidden;
    /* Tell the touch layer this is a tap target, not a scroll/drag region.
       Without this, QtWebEngine on the Edge will sometimes delay or eat the
       click waiting to see if it's actually a pan gesture. */
    touch-action: manipulation;
    transition: background 100ms, border-color 100ms;
  }

  .quad:hover { background: var(--surface-strong); }
  .quad:active { background: var(--surface-strong); }
  .quad:disabled { opacity: 0.35; }

  /* Prevent SVG children from receiving pointer events — QtWebEngine sometimes
     fires the click on the SVG instead of the button, and the inner element
     doesn't bubble it up cleanly under touch. Pinning hits to the button
     itself eliminates the "ghost held" feel. */
  .quad svg { pointer-events: none; }

  .quad.center {
    background: var(--accent-color);
    border-color: var(--accent-color);
    color: var(--bg-color);
  }

  .quad.center.spotify {
    background: var(--spotify-green);
    border-color: var(--spotify-green);
    color: #08130c;
  }

  .quad.center:hover,
  .quad.center:active {
    /* keep the accent fill on the primary action — just darken slightly */
    filter: brightness(0.88);
  }

  .quad svg {
    width: 38%;
    height: 38%;
  }
</style>
