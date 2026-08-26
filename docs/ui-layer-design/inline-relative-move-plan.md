# Inline mode: relative-move rendering — action plan

**Status:** proposed (task #13). This plans the shift from absolute-origin inline rendering to a
relative-move ("floating region") model, and — the point of writing it down — enumerates the
caveats and consequences so the trade-off is a decision, not a surprise.

Read alongside `inline-presentation.md` (the current absolute-origin design this revises).

## 1. The problem

The inline renderer positions every cell with an **absolute** address:
`FrameRenderer.MoveTo(col, row)` emits `CUP(col, row + RowOffset)`, where `RowOffset` is the
region's absolute top screen row, discovered once via DSR-CPR at startup.

`RowOffset` is only re-derived on events the framework can *see* — a resize (re-query CPR), a
plain height change (origin unchanged). An **externally-caused vertical shift the framework cannot
observe** desyncs it:

- **Manual clear** (kitty `cmd+K`, `Ctrl+L` in some shells): the region physically moves to the
  top of the screen, but `RowOffset` still holds the old row. The next frame's absolute `CUP`
  repaints at the phantom original rows — the reported bug ("output moves to the top temporarily,
  then the diff resumes at the original spot").
- Any other undetectable content shift (a program above the region printing, a terminal-side
  reflow) has the same failure shape.

## 2. The proposed model — a floating region

Adopt the bubbletea/gum "standard renderer" model: the region has **no fixed screen address**. It
floats relative to the cursor's *physical* position.

- Track the region **height** (rows) — already known (the fitted buffer height).
- **Frame start:** return to the region's top-left from wherever the cursor physically is, using
  a *relative* move: `CR` (column 0) + `CUU(height − 1)` (up from the region's bottom edge, where
  the previous frame parked the cursor).
- **Repaint** top-down. Between-cell moves are relative: row via `CUU`/`CUD` deltas, column via
  `CR` + `CUF(col)` (carriage-return-relative, which is deterministic regardless of tracked-column
  drift).
- **Frame end:** leave the cursor at a *defined* anchor — the region's bottom-left — so the next
  frame's `CUU(height − 1)` is correct.

**Why it survives the clear:** relative positioning is invariant under a *rigid translation* of
`(content + cursor)` together. A clear/scroll that moves the region and the cursor by the same
delta leaves their relative offset intact, so `CUU(height − 1)` from the cursor still lands on the
region's (new) top. Absolute addressing has no such invariance.

## 3. What changes

| Site | Now (absolute) | Relative model |
|---|---|---|
| `FrameRenderer.MoveTo` | `CUP(col, row + RowOffset)` | row delta `CUU`/`CUD` from `_cursorRow`; column `CR`+`CUF(col)` |
| Inline frame start (full redraw) | `CUP(0,0)` + `ED 0` | `CR` + `CUU(height−1)` + `ED 0` |
| Inline frame end | (cursor left wherever) | park at region **bottom-left** (the anchor) |
| Exit — Clear | `CUP(origin,0)` + `ED 0` | `CR` + `CUU(height−1)` + `ED 0` |
| Exit — Retain | `CUP(origin+height−1,0)` + `LF` + `ED 0` | `CR` (already at bottom) + `LF` + `ED 0` |
| Fragments (sized text / images) | placed at `row + RowOffset` | placed relative to the frame-start anchor |
| Caret band + real caret | absolute | relative |
| Scroll detection | disabled inline (unchanged) | disabled inline (unchanged) |

The `RowOffset` field does **not** disappear — see caveat 1.

## 4. Caveats and consequences (the load-bearing section)

### 4a. Mouse coordinates are screen-absolute — the origin cannot be fully retired
SGR mouse reports (DECSET 1006) carry **absolute screen** row/col. Mapping a click to a region
cell is `row − origin`. Relative rendering never computes an origin, so **mouse mapping still needs
one**, and it goes stale on an undetectable clear exactly as today.

The re-anchor *mechanism* is always the same DSR-CPR round-trip — `origin = reported − believed
cursor region-row` (the existing formula, valid because the hardware cursor rides the region). What
needs pinning down is **when** to pay for it; you cannot CPR every frame. A manual clear is
*unobservable* (the terminal never reports it), so "suspected desync" is a heuristic, not a
detection:

- **Known shifts — corrected directly, no heuristic.** A **resize** re-queries CPR (already in the
  design); a **self-inflicted scroll** past the screen bottom is one the app emitted, so it
  decrements the origin by k (caveat 4c) and stays fresh.
- **Focus-in — re-anchor eagerly.** The user was away (clearing / scrolling / resizing the window)
  and came back: the reported "clear then click back" path, and the strongest, cheapest signal.
  This handler should **also force a full redraw** — see 4b, where it heals region-clearing
  terminals with the same trigger.
- **Origin freshness TTL — what "suspected" concretely means.** Stamp the origin on every anchor;
  treat it as suspect once older than a freshness window. A mouse event against a stale-past-TTL
  origin triggers a **lazy pre-mouse re-anchor** before the coordinate is trusted. This catches a
  clear during an idle stretch with *no* focus transition, while bounding cost — a burst of rapid
  clicks reuses one fresh anchor; only the first click after a quiet gap pays the round-trip.
- **Rejected trigger: "the coordinate mapped outside the region."** Unreliable both ways —
  all-motion tracking (DECSET 1003) makes out-of-region events legitimate, and a stale origin can
  mis-map *within* range (wrong cell, silently). It neither fires reliably on real desync nor
  avoids false positives.

**Residual hole:** a clear *while focused*, followed by continued in-region clicking inside the TTL,
is uncovered short of a CPR poll per click — the honest stale-mouse window (rendering stays correct;
only those clicks mis-map).

**This caveat is why the task can't be "relative and done."** The honest end state is *relative
rendering + an origin kept solely for mouse, re-anchored on focus-in and a freshness TTL*.

### 4b. Terminal clear behavior is terminal-specific — confirmed to diverge (two failure modes)
The invariance argument assumes a clear moves content **and** cursor by the same delta. It does not
hold uniformly. **Empirical results** (curio filter — an edit box on the current line, a dropdown in
the region below it; manual clear):

| Terminal | Kept on clear | Region below the current line |
|---|---|---|
| kitty, Ghostty | current line + everything below it | **retained** — edit box *and* dropdown stay visible |
| Apple Terminal, WezTerm | only the current line | **cleared** — edit box stays, dropdown gone |
| Windows Terminal | *unconfirmed* | *unconfirmed* |

These are **two different failure modes** needing different cures:

- **Retained (kitty/Ghostty)** — the region survives, but absolute addressing repaints it at the
  *stale* rows (the reported bug). Content and cursor moved together, so **relative moves fix it**:
  `CUU(height−1)` from the ridden cursor lands on the region's new top.
- **Cleared (Apple Terminal/WezTerm)** — the region is wiped, and the diff renderer's front buffer
  still believes it painted, so an unchanged back buffer diffs to **nothing** — the region stays
  blank until a full redraw (or until a content change happens to re-emit those cells). Relative
  moves don't help; there is nothing to translate. This needs a **forced full redraw** (`Reset()`),
  which requires noticing the clear — the same unobservable-desync problem as 4a, healed by the same
  trigger: **focus-in should re-anchor the mouse origin *and* force a full redraw.** One handler,
  both cures.

Consequence: relative is **strictly better** than absolute on retaining terminals (it cannot be
*more* wrong) and **neutral** on clearing ones (blank until a redraw, same as today) — but pairing it
with a focus-in `Reset()` recovers the clearing terminals too. Acceptance criterion: "the reported
kitty `cmd+K` case is fixed, the clearing terminals recover on focus-in, and no supported terminal
regresses" — not "provably correct under all clears."

### 4c. Growth past the terminal bottom
When the region grows past the screen bottom the terminal scrolls; today the loop makes room
(`LF` push) and moves `origin` up by k. Under the relative model the cursor **rides** the scroll
naturally, so the moves stay valid — but the height/anchor bookkeeping and the make-room logic
must be re-derived in relative terms. Net: relative is likely *simpler* here (no origin arithmetic)
but the code path must be re-validated, not assumed.

### 4d. Fragments and the caret band
Sized-text (OSC 66) and image fragments are committed to the terminal at a position and tracked for
overpaint. They must move to the relative anchor too, or a floated region strands them. The
standing caret band and the real hardware caret likewise. These are the same code that `Reset()`
currently re-emits on a `RowOffset` change — they need the relative equivalent.

### 4e. `Reset()` / first-frame semantics
`RowOffset` set → `Reset()` → full redraw is the current re-sync. In the relative model there is no
render-`RowOffset`; a full redraw still needs a clean anchor (park-at-bottom must be re-established,
e.g. the first frame assumes the cursor is at the region top after the host's initial reserve).

### 4f. Byte cost
`CUU`/`CUD` + `CR`+`CUF` per move vs one `CUP` is comparable — often the same or a few bytes more
per repositioning. Negligible against a frame's cell payload; not a concern.

### 4g. Blast radius
This touches `FrameRenderer` (emission + fragments + caret) **and** the `UIApplication` frame loop
(anchor, exit emission, mouse re-anchor) — a delicate, stateful, *visual* subsystem where the
likely failure mode is "subtly wrong in a re-anchor/scroll/resize edge case," and it cannot be
eyeballed in CI. This argues for phasing + heavy headless-test coverage before it reaches a real
terminal.

## 5. Implementation phases

1. **✅ DONE — Relative emission in `FrameRenderer`** (behind `FrameRendererOptions.RelativeInline`,
   surfaced as `UseInline(relativeMoves: true)`; off by default). The single `MoveTo` seam emits a
   `CUU`/`CUD` row delta + column-absolute `CHA` (chosen over `CR`+`CUF` — one deterministic
   sequence); the frame-start park assertion (`_cursorRow = height−1`) makes the full-redraw climb
   `MoveTo(0,0) → CUU(height−1)` and the first delta correct; the frame ends parked at bottom-left.
   `RowOffset` is untouched by relative rendering (still used for mouse). The existing absolute byte
   assertions were **kept** (the flag is opt-in, not the default yet) and new relative assertions
   added instead. Tests: `FrameRendererInlineTests` (bottom-up climb, frame-end park, incremental
   relative-only, **RowOffset-independence** = the renderer-level clear-survival proof) +
   `InlinePresentationTests` (end-to-end relative climb, and a second frame never re-addressing the
   stale origin — the direct anti-regression). Rendering 1831 / UI 3911 green.
2. **Exit + fragments + caret** relative (Clear/Retain, fragment placement, caret band). ← next
3. **Desync heal** — a focus-in handler that *both* re-anchors the mouse origin via DSR-CPR **and**
   forces a full redraw (`Reset()`, for the region-clearing terminals — 4b); plus a lazy pre-mouse
   re-anchor gated on an origin **freshness TTL** (4a). Document the residual stale-mouse window.
4. **Re-validate** growth/scroll/resize under the relative model; update `inline-presentation.md`
   (the origin protocol section becomes "origin for mouse only"). Extend the terminal-matrix note
   (4b) to Windows Terminal, the one unconfirmed clear behavior.

## 6. Test strategy

The headless host (`UIHeadlessHost` + `SyntheticTerminalHost`) captures emitted bytes.

- **Port** the existing inline byte assertions from absolute `CUP` to the relative sequences.
- **The regression that proves the fix:** after rendering a frame, simulate an external clear by
  moving the synthetic terminal's cursor to `(0,0)` (or a translated position) *without* telling the
  framework, then render the next frame and assert the relative moves land the region at the new
  position — where absolute `CUP` would have painted the stale rows.
- **Terminal-matrix note:** the empirical kitty/Ghostty/WezTerm/Apple-Terminal/Windows-Terminal
  clear-behavior check (caveat 4b) is a *manual* verification step, like the access-key gate — it
  can't be exercised headlessly.

## 7. Recommendation

Do it, phased, with phase 1 fully headless-tested before it reaches a terminal — and go in
eyes-open on caveat 4a (mouse stays absolute-anchored) and 4b (best-effort under terminal-specific
clears). The reported bug is real and this is the right shape of fix; the residual mouse-staleness
is the price, healed on focus-in.
