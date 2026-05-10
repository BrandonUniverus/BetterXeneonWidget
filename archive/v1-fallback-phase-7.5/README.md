# Better Media v2 — fallback build (Phase 7.5)

Snapshot of the last v2 build that's stable enough to reach for if the
current dev branch is misbehaving.

## When to reach for this

If a freshly-built v2 widget is flickering, glitching, or otherwise
broken on the Edge — install the `.icuewidget` from this folder. It's
the last build verified clean during the v2 rebuild.

## What's in this build

- Vanilla JS rebuild (no Svelte, no framework)
- SMTC + Spotify status polled every 1.5s
- Album art `<img>` (foreground)
- Full-width transport row: Prev / Play-Pause / Next / Connect-or-Playlists
- Source line under the artist (`[•] Spotify · Album`)
- Smooth 200ms-tick progress bar with M:SS time labels
- Synced lyrics from LRClib with `♪` markers, transform-based scroll
  (no `scrollTo`, no `mask-image`)
- Aspect-ratio responsive layout (≥ 2:1 → wide-strip, otherwise vertical)

## What's NOT in this build (intentional)

- No iCUE settings panel (no Behavior or Appearance options)
- No theme color customization (text/accent/bg/transparency)
- No album-art-as-background blurred layer
- No listening-mode auto-expand
- No layout-anchor (content vertically centers when no lyrics — known
  cosmetic issue, not a flicker bug)

These features were added in commits after Phase 7.5 and each one
brought back a periodic flicker on the Edge that we couldn't fully
isolate. The full experimental tree is preserved on the
`feature/flicker-fix-attempts` branch in this repo if you want to
inspect what was tried.

## Files

- `com-betterxeneon-media-v2.icuewidget` — installable widget package.
  Double-click to import into iCUE.
- `source/` — frozen source files (HTML / manifest / translations /
  icon / package script) for reference.

## Provenance

- Source commit: `865efb9` on `feature/flicker-fix`
- Phase: 7.5 (`media-v2 phase 7.5: full-width transport + source line + Connect button`)
- Date archived: 2026-05-10
