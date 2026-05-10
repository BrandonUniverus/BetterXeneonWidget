<script lang="ts">
  import { getIcueProperty } from '@betterxeneon/shared';
  import { host } from '../api.ts';
  import { refreshPlaylistsNow } from '../polling.ts';
  import { appStore } from '../state.ts';

  // Reactive iCUE option — poll the global into $state. (Plain
  // getIcueProperty() calls inside $derived don't re-run when the global
  // changes, so toggling the iCUE setting required a resize before. The
  // svelte/store readable detour had its own interop quirks.)
  let showLyricsValue = $state<boolean | undefined>(getIcueProperty<boolean>('showLyrics'));
  let lyricSyncOffsetMs = $state<number>(Number(getIcueProperty<number>('lyricSyncOffsetMs') ?? 0) || 0);
  $effect(() => {
    const id = setInterval(() => {
      const v = getIcueProperty<boolean>('showLyrics');
      if (v !== showLyricsValue) showLyricsValue = v;
      const o = Number(getIcueProperty<number>('lyricSyncOffsetMs') ?? 0) || 0;
      if (o !== lyricSyncOffsetMs) lyricSyncOffsetMs = o;
    }, 1000);
    return () => clearInterval(id);
  });

  // Large-cell layout. Always the active view in large mode. The
  // `hideControls` prop is what flips the widget into "media mode" — the
  // ambient state where transport row + Playlists button vanish, leaving
  // just art + metadata + lyrics. Tap-to-show is handled at App.svelte
  // (any pointerdown puts displayMode back to 'browse', which makes the
  // parent pass hideControls=false).
  let { hideControls = false } = $props<{ hideControls?: boolean }>();

  // ---------- Now-playing source reconciliation ---------------------------

  let imgError = $state(false);
  let lastVersion = $state(-1);
  $effect(() => {
    const v = $appStore.nowPlaying?.artVersion ?? 0;
    if (v !== lastVersion) {
      lastVersion = v;
      imgError = false;
    }
  });

  let smtcIsSpotify = $derived($appStore.nowPlaying?.isSpotify ?? false);
  let smtcHasSession = $derived($appStore.nowPlaying?.hasSession ?? false);
  let smtcStatus = $derived($appStore.nowPlaying?.status ?? 'Closed');
  let smtcActivelyPlaying = $derived(smtcHasSession && smtcStatus === 'Playing');
  let spotifyHasPlayback = $derived($appStore.spotifyPlayback?.hasSession ?? false);
  let useSpotifyAsSource = $derived(
    spotifyHasPlayback && (
      !smtcHasSession ||
      (smtcIsSpotify && !smtcActivelyPlaying)
    )
  );

  let title = $derived(useSpotifyAsSource
    ? ($appStore.spotifyPlayback?.title ?? '')
    : ($appStore.nowPlaying?.title || 'Nothing playing'));
  let artist = $derived(useSpotifyAsSource
    ? ($appStore.spotifyPlayback?.artist ?? '')
    : ($appStore.nowPlaying?.artist || ''));

  let smtcArtUrl = $derived(host.albumArtUrl($appStore.nowPlaying?.artVersion ?? 0));
  let smtcHasArt = $derived(($appStore.nowPlaying?.hasArt ?? false) && !imgError);
  let spotifyArtUrl = $derived($appStore.spotifyPlayback?.albumArtUrl ?? null);
  let artSrc = $derived(useSpotifyAsSource ? spotifyArtUrl : (smtcHasArt ? smtcArtUrl : null));

  let idle = $derived(!smtcHasSession && !spotifyHasPlayback);
  let showSpotifyChip = $derived(smtcIsSpotify || useSpotifyAsSource);
  let deviceName = $derived($appStore.spotifyPlayback?.deviceName ?? '');

  // ---------- Transport — local SMTC first, Spotify Web API only for remote -
  // Same rule as TransportControls. Local SMTC handles play/pause without
  // hitting Spotify's Web API — no rate limits, no network round-trip. Web
  // API is only needed when Spotify is playing on a different device.
  let useSpotifyTransport = $derived(
    $appStore.spotifyAuthed && !smtcHasSession && spotifyHasPlayback
  );

  let isPlaying = $derived(
    $appStore.optimisticPlay !== null
      ? $appStore.optimisticPlay
      : useSpotifyTransport
        ? ($appStore.spotifyPlayback?.isPlaying ?? false)
        : smtcStatus === 'Playing'
  );

  let canPrev = $derived(useSpotifyTransport ? true : ($appStore.nowPlaying?.canGoPrevious ?? false));
  let canNext = $derived(useSpotifyTransport ? true : ($appStore.nowPlaying?.canGoNext ?? false));
  let canToggle = $derived(useSpotifyTransport
    ? ($appStore.spotifyPlayback?.hasSession ?? false)
    : (($appStore.nowPlaying?.canPlay ?? false) || ($appStore.nowPlaying?.canPause ?? false)));

  let prevInFlight = $state(false);
  let toggleInFlight = $state(false);
  let nextInFlight = $state(false);

  function setOptimistic(playing: boolean): void {
    appStore.update(s => ({
      ...s,
      optimisticPlay: playing,
      optimisticUntil: performance.now() + 3000,
    }));
  }

  async function onPrevious(): Promise<void> {
    if (prevInFlight) return;
    prevInFlight = true;
    try {
      if (useSpotifyTransport) await host.spotifyPrevious();
      else await host.mediaPrevious();
    } catch { /* surfaced via banner */ }
    finally { prevInFlight = false; }
  }

  async function onToggle(): Promise<void> {
    if (toggleInFlight) return;
    const wasPlaying = isPlaying;
    toggleInFlight = true;
    setOptimistic(!wasPlaying);
    try {
      if (useSpotifyTransport) {
        if (wasPlaying) await host.spotifyPause();
        else await host.spotifyResume();
      } else {
        await host.mediaToggle();
      }
    } catch {
      appStore.update(s => ({ ...s, optimisticPlay: null, optimisticUntil: 0 }));
    } finally {
      toggleInFlight = false;
    }
  }

  async function onNext(): Promise<void> {
    if (nextInFlight) return;
    nextInFlight = true;
    try {
      if (useSpotifyTransport) await host.spotifyNext();
      else await host.mediaNext();
    } catch { /* surfaced via banner */ }
    finally { nextInFlight = false; }
  }

  function openPlaylists(): void {
    appStore.update(s => ({ ...s, modalView: 'library' }));
    refreshPlaylistsNow();
  }

  // ---------- "Up next" line ----------------------------------------------
  // Single-line "Up next: Title — Artist" below the source chip. Gets the
  // full metadata column width (~470px in large), so it actually fits.
  let nextTrack = $derived($appStore.spotifyQueue[0] ?? null);

  // ---------- Lyrics (with iCUE on/off setting) ---------------------------

  type LyricLine = { ms: number; text: string };

  function parseLrc(text: string): LyricLine[] {
    const out: LyricLine[] = [];
    for (const raw of text.split('\n')) {
      const re = /\[(\d+):(\d+)(?:\.(\d+))?\]/g;
      const stamps: number[] = [];
      let lastIdx = 0;
      let m: RegExpExecArray | null;
      while ((m = re.exec(raw)) !== null) {
        const minutes = parseInt(m[1] ?? '0', 10);
        const seconds = parseInt(m[2] ?? '0', 10);
        const fracStr = m[3] ?? '0';
        const frac = fracStr.length === 3
          ? parseInt(fracStr, 10)
          : parseInt(fracStr, 10) * 10;
        stamps.push((minutes * 60 + seconds) * 1000 + frac);
        lastIdx = m.index + m[0].length;
      }
      const lineText = raw.slice(lastIdx).trim();
      if (stamps.length === 0 || !lineText) continue;
      for (const ms of stamps) out.push({ ms, text: lineText });
    }
    out.sort((a, b) => a.ms - b.ms);
    return out;
  }

  /**
   * Synthesize ♪ markers in long instrumental gaps. LRClib only stamps line
   * START times — never durations — so a 6-second "gap" between two lines
   * is often just the singer holding the last word, not silence. A naive
   * gap threshold pops ♪ over normal singing.
   *
   * The actual signal we want is "true silence after singing should
   * plausibly have ended." We estimate per-line sung duration from
   * character count (sung pop lyrics ~3-4 chars/sec, ≈280ms/char), pin it
   * to a sensible floor + ceiling, then only insert markers in what's left
   * AFTER that estimated singing finishes.
   *
   * Skipped if LRClib already provided a marker line in the gap.
   */
  const NOTE = '♪';

  // Sung-line duration estimate. Floor = 1500ms (so a one-syllable held
  // note still gets some duration). Ceiling = 90% of the visible gap (so
  // we never claim singing fills the entire gap and skip a real
  // instrumental). 280ms/char tracks pop-vocal pacing reasonably across
  // tempos — slow ballads end up underestimated, but a too-short estimate
  // just means we MIGHT add a marker; a too-long one means we MISS one.
  function estimateLineDurationMs(text: string, gapMs: number): number {
    const chars = text.replace(/\s+/g, ' ').trim().length;
    const naive = Math.max(1500, chars * 280);
    return Math.min(naive, Math.max(1500, gapMs * 0.9));
  }

  function isExistingMarker(text: string): boolean {
    // Just whitespace + musical-note glyphs (♪♫♬♭♮♯) — punctuation alone
    // isn't enough to count as a marker (don't want to misclassify e.g.
    // "(repeat)" as instrumental).
    if (/^[\s♪-♯]+$/.test(text)) return true;
    return /^\(?\s*(instrumental|interlude|music|guitar solo|outro|intro)\s*\)?$/i.test(text);
  }

  function addInstrumentalMarkers(lines: LyricLine[], songDurationMs: number): LyricLine[] {
    if (lines.length === 0) return lines;
    // True-silence threshold AFTER singing is estimated to end. 8s is well
    // past any sustained note but short enough to catch most transitions
    // and bridges. (Was 5s gap-from-line-start, which fired on normal
    // sustained singing.)
    const SILENCE_THRESHOLD_MS = 8_000;
    const SPACING_MS = 8_000;
    const LEAD_MS = 1_000;
    const TAIL_MS = 1_500;
    const out: LyricLine[] = [];

    // Insert markers between [start, end) where `start` is when singing
    // ENDED (not when the previous line began).
    const insertGap = (start: number, end: number) => {
      if (end - start < SILENCE_THRESHOLD_MS) return;
      let t = start + LEAD_MS;
      while (t + 250 < end - TAIL_MS) {
        out.push({ ms: t, text: NOTE });
        t += SPACING_MS;
      }
    };

    // Lead-in before the first lyric (no previous line, so no estimate).
    insertGap(0, lines[0]?.ms ?? 0);

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i]!;
      out.push(line);
      const next = lines[i + 1];
      if (!next) {
        // Trailing gap to song end. Estimate the final line's sung
        // duration so we don't drop a ♪ over a held outro note.
        if (songDurationMs > 0) {
          const trailingGap = songDurationMs - line.ms;
          const sungEnds = line.ms + estimateLineDurationMs(line.text, trailingGap);
          insertGap(sungEnds, songDurationMs);
        }
        break;
      }
      if (isExistingMarker(line.text) || isExistingMarker(next.text)) continue;
      const gap = next.ms - line.ms;
      const sungEnds = line.ms + estimateLineDurationMs(line.text, gap);
      insertGap(sungEnds, next.ms);
    }

    out.sort((a, b) => a.ms - b.ms);
    return out;
  }

  // iCUE setting: when off, never show lyrics regardless of LRClib data.
  // Subscribes to the reactive option so toggling in iCUE takes effect
  // immediately (not "only after a resize").
  let lyricsEnabled = $derived(showLyricsValue !== false);

  let lyricsObj = $derived($appStore.lyrics);
  let parsedLyrics = $derived.by<LyricLine[]>(() => {
    if (!lyricsEnabled || !lyricsObj?.found || !lyricsObj.syncedLyrics) return [];
    const raw = parseLrc(lyricsObj.syncedLyrics);
    // Use SMTC's duration if known, else Spotify Web playback duration.
    const dur = $appStore.nowPlaying?.durationMs || $appStore.spotifyPlayback?.durationMs || 0;
    return addInstrumentalMarkers(raw, dur);
  });
  let hasSyncedLyrics = $derived(parsedLyrics.length > 0);

  // Smooth interpolated playback position. The host samples both SMTC and
  // /me/player every poll (~1.5s); we tick locally so the highlighter
  // doesn't jump. Prefer local SMTC timeline when available — it works even
  // when Spotify Web API is rate-limited or for non-Spotify sources, and
  // costs zero API calls.
  let nowTick = $state(performance.now());
  $effect(() => {
    if (!hasSyncedLyrics) return;
    const id = setInterval(() => { nowTick = performance.now(); }, 200);
    return () => clearInterval(id);
  });

  // Anchor + monotonic clamp. Each poll re-anchors only when the new server
  // estimate is meaningfully different from our local extrapolation — this
  // is what makes the lyrics highlight stop bouncing back and forth. The
  // raw computation re-bases on every poll, so a slightly-behind sample
  // would yank the displayed time backward by 100-500ms each tick. Instead:
  //   - On every poll, compute a freshly-anchored value (pos at fetchedAt).
  //   - Track a "displayed" position that's the max of (last displayed +
  //     elapsed) and (server anchor + elapsed). Never goes backward.
  //   - Hard re-sync if the server says we're WAY off (track change, seek):
  //     >2.5s gap forward, or any gap backward >2s (likely a seek-back).
  let anchorPosMs = $state(0);
  let anchorAt = $state(0); // performance.now() at anchor time
  let lastDisplayedMs = $state(0);
  let lastTrackKey = $state('');

  $effect(() => {
    const np = $appStore.nowPlaying;
    const pb = $appStore.spotifyPlayback;
    let serverPos = 0, serverAt = 0, isPlaying = false, key = '';
    // SMTC path — works on the new host (positionMs/durationMs populated).
    // Older host versions (before May 2026) don't include these fields, so
    // require positionMs to be a real number. Otherwise fall through to
    // Spotify Web playback so we still get something while the user updates.
    const npPos = typeof np?.positionMs === 'number' ? np.positionMs : null;
    if (np?.hasSession && npPos !== null) {
      serverPos = npPos;
      serverAt = $appStore.nowPlayingFetchedAt;
      isPlaying = np.status === 'Playing';
      key = `smtc|${np.title}|${np.artist}`;
    } else if (pb?.hasSession) {
      serverPos = pb.progressMs;
      serverAt = $appStore.spotifyPlaybackFetchedAt;
      isPlaying = pb.isPlaying;
      key = `spot|${pb.trackId}`;
    } else {
      anchorPosMs = 0;
      anchorAt = 0;
      lastDisplayedMs = 0;
      lastTrackKey = '';
      return;
    }

    if (key !== lastTrackKey) {
      // Track change: re-sync hard.
      anchorPosMs = serverPos;
      anchorAt = serverAt;
      lastDisplayedMs = serverPos;
      lastTrackKey = key;
      return;
    }

    // Compute what the server thinks the position is right now, vs what
    // we'd locally extrapolate to. Re-anchor only on big jumps (seek).
    const localExtrap = isPlaying && anchorAt > 0
      ? anchorPosMs + (serverAt - anchorAt)
      : anchorPosMs;
    const delta = serverPos - localExtrap;
    if (delta > 2500 || delta < -2000) {
      anchorPosMs = serverPos;
      anchorAt = serverAt;
      // Reset the monotonic floor too. Without this, a seek-back (or a
      // song looping back to 0:00 with the same title) leaves the
      // highlighter stuck at the previous high-water mark — the clamp
      // wins forever and the user sees ♪ markers from the outro while
      // the song is actually back in the verse.
      lastDisplayedMs = serverPos;
    }
  });

  let interpolatedPositionMs = $derived.by(() => {
    if (anchorAt === 0) return 0;
    const np = $appStore.nowPlaying;
    const pb = $appStore.spotifyPlayback;
    const playing = (np?.hasSession && np.status === 'Playing')
      || (!np?.hasSession && pb?.isPlaying);
    const drift = playing ? Math.max(0, nowTick - anchorAt) : 0;
    const candidate = anchorPosMs + drift;
    // Monotonic floor: never go backward while playing on the same track.
    return playing ? Math.max(lastDisplayedMs, candidate) : candidate;
  });

  // Track the displayed value so the next render can guarantee monotonicity.
  $effect(() => {
    const v = interpolatedPositionMs;
    if (v > lastDisplayedMs) lastDisplayedMs = v;
  });

  let currentLineIdx = $derived.by(() => {
    if (!hasSyncedLyrics) return -1;
    // User-tunable offset for residual sync issues — positive value pushes
    // lyrics earlier (use if highlighter is consistently behind), negative
    // pushes them later. Defaults to 0; iCUE setting "Lyric sync offset (ms)".
    const pos = interpolatedPositionMs + lyricSyncOffsetMs;
    let idx = -1;
    for (let i = 0; i < parsedLyrics.length; i++) {
      const line = parsedLyrics[i];
      if (line && line.ms <= pos) idx = i;
      else break;
    }
    return idx;
  });

  // ---------- Size-aware lyric rendering ----------------------------------
  // size-medium: only 3 lines visible (prev / current / next)
  // size-large : full scroll with mask gradient on edges
  let isMedium = $state(typeof window !== 'undefined' ? window.innerWidth < 700 : false);
  $effect(() => {
    const handler = () => { isMedium = window.innerWidth < 700; };
    window.addEventListener('resize', handler);
    return () => window.removeEventListener('resize', handler);
  });

  let mediumLines = $derived.by(() => {
    const i = currentLineIdx;
    if (i < 0) return null;
    return {
      prev: i > 0 ? parsedLyrics[i - 1]?.text ?? '' : '',
      current: parsedLyrics[i]?.text ?? '',
      next: i + 1 < parsedLyrics.length ? parsedLyrics[i + 1]?.text ?? '' : '',
    };
  });

  // Auto-scroll the current line into the middle of the lyrics container
  // (large mode only — medium just renders 3 fixed lines).
  let lyricsEl = $state<HTMLDivElement | null>(null);
  let lineEls = $state<(HTMLElement | null)[]>([]);

  $effect(() => {
    if (isMedium) return;
    const i = currentLineIdx;
    const line = lineEls[i];
    const container = lyricsEl;
    if (!line || !container) return;
    const containerH = container.clientHeight;
    const lineCenter = line.offsetTop + line.offsetHeight / 2;
    const target = lineCenter - containerH / 2;
    container.scrollTo({ top: target, behavior: 'smooth' });
  });

  // ---------- Queue list (fallback when no synced lyrics or lyrics off) ----
  let queueItems = $derived($appStore.spotifyQueue);

  async function playTrack(id: string): Promise<void> {
    try { await host.playSpotifyTrack(id); } catch { /* surfaced via banner */ }
  }

  // Show the queue list when:
  //   - lyrics setting is off, OR
  //   - LRClib has nothing for this track
  let showQueueFallback = $derived(!hasSyncedLyrics);

  // ---------- Connect CTA (when not authed) -------------------------------
  let needsConnect = $derived(!$appStore.spotifyAuthed);
  let connecting = $derived($appStore.spotifyConnecting);

  async function onConnect(): Promise<void> {
    if ($appStore.spotifyConnecting) return;
    appStore.update(s => ({ ...s, spotifyConnecting: true }));
    try {
      await host.spotifyConnect();
    } catch {
      appStore.update(s => ({ ...s, spotifyConnecting: false }));
    }
  }
</script>

<section class="listening" class:idle class:medium={isMedium}>
  <div class="art" class:placeholder={!artSrc} style:background-image={artSrc ? `url('${artSrc}')` : 'none'}>
    {#if !artSrc}
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round">
        <circle cx="12" cy="12" r="9"/>
        <circle cx="12" cy="12" r="3"/>
      </svg>
    {/if}
  </div>

  <div class="meta">
    <div class="header">
      <div class="header-top">
        <div class="title" title={title}>{title}</div>
        {#if nextTrack}
          <div class="next-line" title="{nextTrack.title} — {nextTrack.artist}">
            <span class="next-label">Up next</span>
            <span class="next-divider" aria-hidden="true">·</span>
            <span class="next-text">
              <span class="next-title">{nextTrack.title}</span>
              <span class="next-dim"> — {nextTrack.artist}</span>
            </span>
          </div>
        {/if}
      </div>
      {#if artist}
        <div class="artist" title={artist}>{artist}</div>
      {/if}
      {#if showSpotifyChip}
        <div class="source" class:remote={useSpotifyAsSource}>
          <span class="source-dot" aria-hidden="true"></span>
          <span class="source-name">Spotify</span>
          {#if useSpotifyAsSource && deviceName}
            <span class="source-divider" aria-hidden="true">·</span>
            <span class="source-device" title={deviceName}>{deviceName}</span>
          {/if}
        </div>
      {/if}
    </div>

    <div class="content">
      {#if hasSyncedLyrics && isMedium && mediumLines}
        <!-- Compact 3-line view for medium mode. Previous line gets a
             text-shadow stroke so it's legible against bright/light album
             art that bleeds through. -->
        <div class="lyrics-three">
          <div class="lyric-line prev outlined">{mediumLines?.prev ?? ''}</div>
          <div class="lyric-line current">{mediumLines?.current ?? ''}</div>
          <div class="lyric-line next">{mediumLines?.next ?? ''}</div>
        </div>
      {:else if hasSyncedLyrics}
        <!-- Large mode: scrollable full lyrics with auto-center on current.
             Renders even when not Spotify-authed since LRClib is independent
             of Spotify auth and SMTC gives us title+artist for the lookup. -->
        <div class="lyrics" bind:this={lyricsEl}>
          {#each parsedLyrics as line, i (i)}
            <div class="lyric-line"
                 class:current={i === currentLineIdx}
                 class:past={i < currentLineIdx}
                 bind:this={lineEls[i]}>
              {line.text}
            </div>
          {/each}
        </div>
      {:else if showQueueFallback && queueItems.length > 0}
        <div class="queue">
          <div class="queue-list">
            {#each queueItems.slice(0, isMedium ? 3 : 8) as t (t.id)}
              <button class="queue-item" type="button" onclick={() => playTrack(t.id)}>
                <span class="q-title">{t.title}</span>
                <span class="q-dim">— {t.artist}</span>
              </button>
            {/each}
          </div>
        </div>
      {/if}
    </div>

    {#if !hideControls}
      <!-- Transport row + Playlists. Hidden when hideControls=true (media
           mode). Tap anywhere on the widget brings them back. -->
      <div class="transport" role="toolbar" aria-label="Playback controls">
        <button class="t-btn" type="button" onclick={onPrevious} aria-label="Previous track" disabled={!canPrev}>
          <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
            <path d="M6 5h2v14H6zM10 12l9-7v14z"/>
          </svg>
        </button>
        <button class="t-btn center" class:spotify={useSpotifyTransport} type="button" onclick={onToggle} aria-label={isPlaying ? 'Pause' : 'Play'} disabled={!canToggle}>
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
        <button class="t-btn" type="button" onclick={onNext} aria-label="Next track" disabled={!canNext}>
          <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
            <path d="M5 5l9 7-9 7zM16 5h2v14h-2z"/>
          </svg>
        </button>
        {#if $appStore.spotifyAuthed}
          <button class="t-btn playlists" type="button" onclick={openPlaylists} aria-label="Open Playlists">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <line x1="8" y1="6" x2="21" y2="6"/>
              <line x1="8" y1="12" x2="21" y2="12"/>
              <line x1="8" y1="18" x2="21" y2="18"/>
              <line x1="3" y1="6" x2="3.01" y2="6"/>
              <line x1="3" y1="12" x2="3.01" y2="12"/>
              <line x1="3" y1="18" x2="3.01" y2="18"/>
            </svg>
          </button>
        {:else}
          <!-- Not authed: same slot as Playlists, but a Spotify Connect
               action. Same icon as the old big CTA card so it's recognizable. -->
          <button class="t-btn connect" type="button" onclick={onConnect} disabled={connecting} aria-label={connecting ? 'Opening browser to connect Spotify' : 'Connect Spotify'} title={connecting ? 'Opening browser…' : 'Connect Spotify'}>
            <svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
              <circle cx="12" cy="12" r="10"/>
              <path d="M16.7 16.5c-.2.3-.6.4-.9.2-2.5-1.5-5.6-1.9-9.3-1-.4.1-.7-.2-.8-.5-.1-.4.2-.7.5-.8 4-.9 7.5-.5 10.3 1.2.3.2.4.6.2.9zm1.2-2.7c-.2.4-.7.5-1.1.3-2.8-1.7-7.1-2.2-10.5-1.2-.4.1-.9-.1-1-.6-.1-.4.1-.9.6-1 3.8-1.2 8.5-.6 11.7 1.4.4.2.5.7.3 1.1zm.1-2.8C14.9 9 9.4 8.7 6.2 9.7c-.5.2-1.1-.1-1.2-.6-.2-.5.1-1.1.6-1.2 3.7-1.1 9.7-.9 13.4 1.3.5.3.6.9.4 1.4-.3.4-.9.6-1.4.4z" fill="#0a0a0c"/>
            </svg>
          </button>
        {/if}
      </div>
    {/if}
  </div>
</section>

<style>
  .listening {
    display: grid;
    grid-template-columns: minmax(0, auto) minmax(0, 1fr);
    gap: calc(var(--gap) * 1.4);
    align-items: stretch;
    height: 100%;
    min-height: 0;
  }

  /* Medium: tighter gap, smaller art */
  .listening.medium {
    gap: var(--gap);
    /* Cap art width so the metadata column gets meaningful real estate. */
    grid-template-columns: minmax(0, 38%) minmax(0, 1fr);
  }
  .listening.medium .art {
    /* Override: in medium, width is the binding constraint. Opt out of
       grid-item stretch (align-self) so the square computed from width
       isn't blown vertical by the row track. */
    align-self: center;
    width: 100%;
    height: auto;
    max-height: 100%;
  }

  .art {
    height: 100%;
    aspect-ratio: 1;
    border-radius: calc(var(--radius) * 1.4);
    background-color: var(--surface);
    background-size: cover;
    background-position: center;
    box-shadow: 0 calc(var(--layout-unit) * 1) calc(var(--layout-unit) * 4) rgba(0, 0, 0, 0.45);
    flex-shrink: 0;
    contain: layout style;
    display: grid;
    place-items: center;
    color: var(--text-color);
  }
  .art.placeholder { opacity: 0.45; }
  .art svg { width: 35%; height: 35%; }

  .meta {
    display: grid;
    /* When transport is shown: header / content / transport.
       When hidden (media mode): just header / content. The third
       `auto` row collapses with no children present. */
    grid-template-rows: auto minmax(0, 1fr) auto;
    gap: calc(var(--layout-unit) * 0.6);
    min-width: 0;
    min-height: 0;
    height: 100%;
  }

  .header {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.4);
    min-width: 0;
  }

  /* Top row: title on the left, "Up next" pinned to the right, baseline-
     aligned so the small chip sits in line with the title's text. */
  .header-top {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: var(--gap);
    min-width: 0;
  }
  .header-top .title {
    flex: 1 1 auto;
    min-width: 0;
  }

  /* "Up next" — chip lives in the top-right of the header. Keeps the
     full text where the column is wide enough; ellipsises gracefully on
     narrower cells. */
  .next-line {
    display: flex;
    align-items: baseline;
    gap: calc(var(--layout-unit) * 0.6);
    font-size: var(--font-label);
    min-width: 0;
    max-width: 55%;
    overflow: hidden;
    flex: 0 1 auto;
    text-align: right;
    justify-content: flex-end;
  }

  .next-label {
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    opacity: 0.55;
    flex-shrink: 0;
  }

  .next-divider {
    opacity: 0.35;
    flex-shrink: 0;
  }

  .next-text {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
  }

  .next-title { font-weight: 600; }
  .next-dim { opacity: 0.6; }

  /* Medium: hide "Up next" — header is already squeezed by the 3-line
     lyrics layout. */
  .listening.medium .next-line { display: none; }

  .title {
    font-size: var(--font-title);
    font-weight: 800;
    line-height: 1.05;
    letter-spacing: -0.01em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .artist {
    font-size: var(--font-body);
    font-weight: 600;
    opacity: 0.85;
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
    margin-top: calc(var(--layout-unit) * 0.2);
    min-width: 0;
  }
  .source-dot {
    width: calc(var(--layout-unit) * 1.2);
    height: calc(var(--layout-unit) * 1.2);
    border-radius: 50%;
    background: var(--spotify-green);
    flex-shrink: 0;
  }
  .source.remote .source-dot {
    background: color-mix(in srgb, var(--spotify-green) 70%, transparent);
    box-shadow: 0 0 0 1px var(--spotify-green) inset;
  }
  .source-name { font-weight: 600; }
  .source-divider { opacity: 0.45; }
  .source-device {
    opacity: 0.85;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    min-width: 0;
    max-width: 60%;
  }

  .content {
    min-height: 0;
    min-width: 0;
    overflow: hidden;
    display: flex;
    flex-direction: column;
  }

  /* ---- Large-mode lyrics: scrolling list with mask + auto-scroll ----- */
  .lyrics {
    flex: 1 1 auto;
    overflow-y: auto;
    overflow-x: hidden;
    min-height: 0;
    padding: 0 calc(var(--layout-unit) * 0.6);
    scrollbar-width: none;
    mask-image: linear-gradient(to bottom, transparent, #000 18%, #000 82%, transparent);
  }
  .lyrics::-webkit-scrollbar { display: none; }

  .lyric-line {
    font-size: var(--font-body);
    line-height: 1.5;
    opacity: 0.35;
    transition: opacity 280ms ease, color 280ms ease, transform 280ms ease;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .lyric-line.current {
    opacity: 1;
    color: var(--spotify-green);
    font-weight: 700;
    transform: scale(1.04);
    transform-origin: left center;
  }
  .lyric-line.past { opacity: 0.55; }

  /* ---- Medium-mode lyrics: 3 fixed lines, no scroll ----- */
  .lyrics-three {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.4);
    justify-content: center;
    flex: 1 1 auto;
    min-height: 0;
    overflow: hidden;
  }

  .lyrics-three .lyric-line {
    transition: opacity 280ms ease, color 280ms ease;
  }

  .lyrics-three .prev {
    color: #ffffff;
    opacity: 1;
    font-weight: 600;
  }
  .lyrics-three .prev.outlined {
    /* Stroke effect so the previous line is readable on bright albums.
       text-shadow with 8 offsets approximates a 1px outline; pair with
       -webkit-text-stroke for browsers that support it. */
    text-shadow:
      -1px -1px 0 rgba(0, 0, 0, 0.85),
       1px -1px 0 rgba(0, 0, 0, 0.85),
      -1px  1px 0 rgba(0, 0, 0, 0.85),
       1px  1px 0 rgba(0, 0, 0, 0.85),
       0   -1px 0 rgba(0, 0, 0, 0.85),
       0    1px 0 rgba(0, 0, 0, 0.85),
      -1px  0   0 rgba(0, 0, 0, 0.85),
       1px  0   0 rgba(0, 0, 0, 0.85);
    -webkit-text-stroke: 0.5px rgba(0, 0, 0, 0.6);
  }

  .lyrics-three .current {
    color: var(--spotify-green);
    opacity: 1;
    font-weight: 700;
    transform: none;
  }

  .lyrics-three .next {
    opacity: 0.45;
    font-weight: 500;
  }

  /* ---- Queue fallback ---------------------------------------------- */
  .queue {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.3);
    min-height: 0;
    flex: 1 1 auto;
    overflow: hidden;
  }

  .queue-list {
    display: flex;
    flex-direction: column;
    gap: calc(var(--layout-unit) * 0.2);
    min-height: 0;
    flex: 1 1 auto;
    overflow-y: auto;
    overflow-x: hidden;
    touch-action: pan-y;
    scrollbar-width: thin;
    scrollbar-color: rgba(255, 255, 255, 0.18) transparent;
  }
  .queue-list::-webkit-scrollbar { width: 4px; }
  .queue-list::-webkit-scrollbar-thumb { background: rgba(255, 255, 255, 0.18); border-radius: 2px; }

  .queue-item {
    display: flex;
    align-items: baseline;
    gap: calc(var(--layout-unit) * 0.6);
    width: 100%;
    padding: calc(var(--layout-unit) * 0.5) calc(var(--layout-unit) * 0.4);
    background: transparent;
    border: 0;
    border-radius: var(--radius-sm);
    color: var(--text-color);
    text-align: left;
    font-size: var(--font-label);
    line-height: 1.2;
    touch-action: manipulation;
    transition: background 100ms;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .queue-item:hover { background: var(--surface); }
  .queue-item:active { background: var(--surface-strong); }
  .queue-item > * { pointer-events: none; }
  .q-title { font-weight: 600; }
  .q-dim { opacity: 0.55; font-weight: 500; }

  /* ---- Transport row (4 buttons: prev | play | next | playlists) ---- */
  .transport {
    display: grid;
    grid-template-columns: 1fr 1.2fr 1fr 0.9fr;
    gap: var(--gap);
    height: clamp(40px, calc(var(--layout-unit) * 14), 80px);
    min-height: 0;
    margin-top: calc(var(--layout-unit) * 0.4);
  }

  .t-btn {
    display: grid;
    place-items: center;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    color: var(--text-color);
    min-height: 0;
    min-width: 0;
    overflow: hidden;
    touch-action: manipulation;
    transition: background 100ms, border-color 100ms;
  }
  .t-btn:hover { background: var(--surface-strong); }
  .t-btn:active { background: var(--surface-strong); }
  .t-btn:disabled { opacity: 0.35; }
  .t-btn svg { width: 38%; height: 38%; pointer-events: none; }

  .t-btn.center {
    background: var(--accent-color);
    border-color: var(--accent-color);
    color: var(--bg-color);
  }
  .t-btn.center.spotify {
    background: var(--spotify-green);
    border-color: var(--spotify-green);
    color: #08130c;
  }
  .t-btn.center:hover, .t-btn.center:active { filter: brightness(0.88); }

  .t-btn.playlists {
    color: var(--spotify-green);
  }
  .t-btn.playlists svg { width: 50%; height: 50%; }

  /* Spotify Connect button — same shape as a transport button, but the
     full-color Spotify wordmark icon so it reads as the action it is. */
  .t-btn.connect {
    color: var(--spotify-green);
  }
  .t-btn.connect svg { width: 56%; height: 56%; }

  .listening.idle .title {
    opacity: 0.55;
    font-weight: 500;
  }
</style>
