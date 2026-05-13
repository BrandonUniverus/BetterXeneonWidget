<script lang="ts">
  let {
    value,
    muted = false,
    disabled = false,
    onChange,
    onToggleMute,
    onMaxVolume,
    onAdjustStart,
    onAdjustEnd,
  }: {
    value: number;
    muted?: boolean;
    /** When true, the slider rejects pointer + keyboard input and the
     *  flanking buttons are inert. Used to lock all controls on a card
     *  while a different card is mid-default-device-switch. */
    disabled?: boolean;
    onChange: (v: number) => void;
    onToggleMute?: () => void;
    onMaxVolume?: () => void;
    onAdjustStart?: () => void;
    onAdjustEnd?: () => void;
  } = $props();

  let trackEl: HTMLDivElement;
  let dragging = $state(false);

  function valueFromEvent(e: PointerEvent): number {
    const rect = trackEl.getBoundingClientRect();
    if (rect.width <= 0) return value;
    const ratio = (e.clientX - rect.left) / rect.width;
    return Math.round(Math.max(0, Math.min(1, ratio)) * 100);
  }

  function handleDown(e: PointerEvent): void {
    if (disabled || !trackEl) return;
    e.preventDefault();
    dragging = true;
    try { trackEl.setPointerCapture(e.pointerId); } catch { /* best-effort */ }
    onAdjustStart?.();
    onChange(valueFromEvent(e));
  }
  function handleMove(e: PointerEvent): void {
    if (disabled || !dragging) return;
    onChange(valueFromEvent(e));
  }
  function handleUp(e: PointerEvent): void {
    if (!dragging) return;
    dragging = false;
    onChange(valueFromEvent(e));
    if (trackEl?.hasPointerCapture(e.pointerId)) {
      try { trackEl.releasePointerCapture(e.pointerId); } catch { /* ignore */ }
    }
    onAdjustEnd?.();
  }
  function handleKey(e: KeyboardEvent): void {
    if (disabled) return;
    let next = value;
    switch (e.key) {
      case 'ArrowLeft': case 'ArrowDown': next = Math.max(0, value - 1); break;
      case 'ArrowRight': case 'ArrowUp': next = Math.min(100, value + 1); break;
      case 'PageDown': next = Math.max(0, value - 10); break;
      case 'PageUp': next = Math.min(100, value + 10); break;
      case 'Home': next = 0; break;
      case 'End': next = 100; break;
      default: return;
    }
    e.preventDefault();
    onAdjustStart?.();
    onChange(next);
    onAdjustEnd?.();
  }
</script>

<div class="volume-row" class:muted class:disabled>
  <button class="end-btn" type="button" onclick={onToggleMute} {disabled} aria-label={muted ? 'Unmute' : 'Mute'}>
    {#if muted}
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M11 5L6 9H2v6h4l5 4z" fill="currentColor"/>
        <line x1="23" y1="9" x2="17" y2="15"/>
        <line x1="17" y1="9" x2="23" y2="15"/>
      </svg>
    {:else}
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M11 5L6 9H2v6h4l5 4z" fill="currentColor"/>
      </svg>
    {/if}
  </button>

  <div
    class="track"
    class:dragging
    bind:this={trackEl}
    role="slider"
    tabindex="0"
    aria-label="Volume"
    aria-valuemin="0"
    aria-valuemax="100"
    aria-valuenow={value}
    style:--vol={muted ? 0 : value}
    onpointerdown={handleDown}
    onpointermove={handleMove}
    onpointerup={handleUp}
    onpointercancel={handleUp}
    onkeydown={handleKey}
  >
    <div class="thumb" aria-hidden="true">
      <svg viewBox="0 0 12 24" preserveAspectRatio="xMidYMid meet" class="grip">
        <rect x="2.5" y="6" width="1.4" height="12" rx="0.7"/>
        <rect x="5.3" y="6" width="1.4" height="12" rx="0.7"/>
        <rect x="8.1" y="6" width="1.4" height="12" rx="0.7"/>
      </svg>
    </div>
  </div>

  <button class="end-btn" type="button" onclick={onMaxVolume} {disabled} aria-label="Max volume">
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M11 5L6 9H2v6h4l5 4z" fill="currentColor"/>
      <path d="M15.54 8.46a5 5 0 0 1 0 7.07"/>
      <path d="M19.07 4.93a10 10 0 0 1 0 14.14"/>
    </svg>
  </button>
</div>

<style>
  .volume-row {
    display: flex;
    align-items: center;
    gap: clamp(6px, calc(var(--layout-unit) * 1.5), 18px);
    height: var(--tap-min);
    width: 100%;
  }

  .end-btn {
    flex: 0 0 auto;
    width: var(--tap-min);
    height: var(--tap-min);
    display: grid;
    place-items: center;
    border-radius: var(--radius);
    background: var(--surface);
    color: var(--text-color);
    transition: background 120ms;
  }
  .end-btn:active { background: var(--surface-strong); }
  .end-btn svg { width: 55%; height: 55%; }
  .volume-row.muted .end-btn:first-of-type { color: var(--error); }

  /* Continuous track with continuous fill — fill always ends EXACTLY at the
     thumb's left edge (no segment-gap dead space). */
  .track {
    position: relative;
    flex: 1 1 auto;
    height: var(--tap-min);
    cursor: pointer;
    touch-action: none;
    user-select: none;
    -webkit-user-select: none;
    outline: none;
    --thumb-w: clamp(28px, calc(var(--layout-unit) * 7), 76px);
    --track-h: clamp(20px, calc(var(--layout-unit) * 4.6), 56px);
  }
  .track:focus-visible {
    outline: 2px solid var(--accent-color);
    outline-offset: 2px;
    border-radius: 9999px;
  }

  /* Track background — full pill */
  .track::before {
    content: '';
    position: absolute;
    left: 0;
    right: 0;
    top: 50%;
    height: var(--track-h);
    transform: translateY(-50%);
    background: var(--surface-strong);
    border-radius: 9999px;
    pointer-events: none;
  }
  /* Fill — extends past the thumb's left edge to its CENTER, so the visual
     joint disappears underneath the thumb (thumb becomes the "tail" of the
     fill, no visible end-cap). */
  .track::after {
    content: '';
    position: absolute;
    left: 0;
    top: 50%;
    height: var(--track-h);
    width: calc(var(--vol, 0) / 100 * (100% - var(--thumb-w)) + var(--thumb-w) / 2);
    transform: translateY(-50%);
    background: var(--accent-color);
    border-radius: 9999px;
    transition: width 60ms linear;
    pointer-events: none;
    z-index: 0;
  }
  .track.dragging::after { transition: none; }
  .track.muted::after { background: transparent; }

  /* Thumb — wide pill with grip dashes. Same position math as the fill so
     left-edge of thumb == right-edge of fill, always. */
  .thumb {
    position: absolute;
    top: 50%;
    left: calc(var(--vol, 0) / 100 * (100% - var(--thumb-w)));
    width: var(--thumb-w);
    height: clamp(28px, calc(var(--layout-unit) * 6.4), 72px);
    transform: translateY(-50%);
    background: var(--text-color);
    border-radius: clamp(6px, calc(var(--layout-unit) * 1.5), 16px);
    box-shadow: 0 calc(var(--layout-unit) * 0.4) calc(var(--layout-unit) * 1.4) rgba(0, 0, 0, 0.45);
    transition: left 60ms linear;
    pointer-events: none;
    display: grid;
    place-items: center;
    /* Stack ABOVE the fill so the overlap is hidden under the thumb */
    z-index: 1;
  }
  .track.dragging .thumb {
    transition: none;
    transform: translateY(-50%) scale(1.04);
  }
  .grip {
    width: 50%;
    height: 50%;
    fill: color-mix(in srgb, var(--accent-color) 70%, var(--bg-color));
    opacity: 0.85;
  }
  .volume-row.muted .thumb { opacity: 0.55; }
</style>
