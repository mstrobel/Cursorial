# Inline presentation — applications below the shell prompt

Status: **implemented** (feature/inline-presentation). Companion to `docs/ui-layer-design.md`
§10 (hosting); this document is the normative reference for the inline mode's wire protocol,
sizing rules, and teardown contract.

## What it is

An **inline application** renders in a *region* of the main screen buffer that begins where the
shell left the cursor, instead of taking over the screen. The region spans the full terminal
width; its height tracks the root content's desired height frame-to-frame (capped by the builder's
`maxHeight` and the terminal height). The alternate screen is never entered and the screen is
never cleared. On exit the region is erased — the prompt resumes where the application started —
or the last frame is left standing, per `InlineExitBehavior`.

```csharp
var app = UIApplication.DefaultBuilder()
    .UseInline(maxHeight: 12, exitBehavior: InlineExitBehavior.Clear)
    .Build();
await app.RunAsync(() => new ChooserView());
// app.InlineExitBehavior is re-assignable at runtime — e.g. Retain on accept, Clear on cancel.
```

`UseAlternateScreen` is ignored inline. `Cursorial.Demo` → `inline` is the live showcase.

## Division of labor

| Piece | Owns |
|---|---|
| `FrameRenderer` (`FrameRendererOptions.Inline` + `RowOffset`) | Region-relative emission: every cursor address adds `RowOffset`; full redraws erase with **CUP(region top) + ED 0** instead of ED 2; SU/SD scroll detection is disabled (those scroll the whole screen, shell history included). A `RowOffset` change calls `Reset()` — a moved region invalidates every believed terminal position. |
| `UIApplication` frame loop | Origin discovery (DSR-CPR), the render gate, content-height fitting, growth scrolling, mouse translation, resize re-anchor, exit emission. |
| `WindowManager.MeasureRootContentHeight` | The `SizeToContent` probe applied to the root band: measure the root host at `(width, maxRows)`, clamp desired height to `[1, maxRows]`, re-invalidate when the fit held (the `FitWindowToContent` fit-held rule). |

## The origin protocol (DSR-CPR)

The region's absolute top row is discovered, never assumed:

1. **Startup** — entry bytes are `SGR reset · OSC 12 · CSI 6 n` (no 1049, no ED). Phase 6 emits
   *nothing* until the reply lands (layout and fitting still run). The reply
   (`CSI row ; col R`, routed as `DeviceResponseKind.CursorPositionReport`) sets
   `origin = row-1`, or `row` when `col > 1` — a mid-line prompt is never painted over.
   Only an *outstanding* query may anchor (the F3/CPR collision), and the loop holds at frame
   pace while waiting so the timeout can fire without input.
2. **Timeout fallback** (1 s, `InlineCprTimeout`) — a terminal that never answers DSR gets the
   blind reserve: `CUP(bottom row) + height × LF`, `origin = rows − height`. Worst case a blank
   band separates the prompt from the region.
3. **Resize re-anchor** — a `ResizeEvent` means the terminal rewrapped its main buffer and the
   origin is stale. The loop re-queries; the reply re-derives
   `origin = reported − believed cursor row` (the hardware cursor rides the region through the
   rewrap), clamped on-screen. Until it lands, emission holds; its own timeout falls back to
   clamping the old origin. Rewrap fidelity is inherently best-effort — Ctrl+L
   (`RequestFullRedraw`) remains the escape hatch.

## Sizing and the scroll rule

- Fitting runs **before Phase 5** on layout-dirty or resized frames, so the frame lays out and
  renders at the fitted height (no flicker frame). A height change resizes the region
  `CellBuffer` + viewport; the renderer's dimension check turns that into a full region repaint.
- The region grows and shrinks at its **bottom** edge; the origin never moves for a plain height
  change. A shrink needs no dedicated wipe: the inline full-redraw erase (CUP top + ED 0) sweeps
  the vacated rows in the same stroke.
- **Growth past the terminal bottom** (`origin + height > rows`) is made room for *before* the
  delta, in the same flush: `CUP(bottom row)` + k **literal line feeds** — never SU/SD, because LF
  pushes the departing top lines into the *scrollback* where the user's shell history belongs,
  while `CSI S` discards them on most terminals. The origin then moves up by k.

## Input

Mouse events arrive in screen coordinates and are translated (`row − origin`) in Phase 1. Events
outside the region are swallowed — that's the shell's estate — except mid-drag events (buttons
held, or a release), which clamp to the region edge so S3's capture never strands. Pixel
coordinates, when a terminal reports them, are not translated (cell coordinates are
authoritative). Keyboard input is unaffected.

## Teardown

The inline branch replaces the alt-screen-leave / clear-screen leg (everything else — renderer
close, show cursor, SGR reset, OSC 112, autowrap — is unchanged):

- **Clear** — `CUP(origin, 0) + ED 0`: the prompt resumes exactly where the application started.
- **Retain** — `CUP(origin + height − 1, 0) + LF + ED 0`: park on a fresh line below the frame
  (the LF scrolls one line when the region ends on the bottom row), sweep anything staler below.
  Fragment protocol erases are skipped (`FrameRenderer.Close(output, eraseFragments: false)`) so
  images / sized text survive in the retained frame.
- An application whose origin never resolved rendered nothing and emits neither.

## Known limitations (v1)

- **Resize rewrap** can leave artifacts above the region (the terminal moves main-buffer content
  in terminal-specific ways); the re-anchor + full repaint recovers the region itself.
- **Popups/windows are confined to the region** (the viewport *is* the region) — a dropdown
  taller than the region clips; cap-aware content should keep overlays modest or raise
  `maxHeight`.
- **The emergency signal path** (`TerminalSession.EmergencyRestoreAndDispose`) restores modes but
  does not move the cursor below the region — a SIGTERM'd inline app can leave the prompt
  mid-region. The documented `EmergencyRestoreBytes` seam is the follow-up.
- **`PauseIOAsync` handoff** (spawning `$EDITOR`) is not inline-aware yet: the child scrolls the
  main buffer arbitrarily; resuming needs a fresh reserve + re-anchor.
