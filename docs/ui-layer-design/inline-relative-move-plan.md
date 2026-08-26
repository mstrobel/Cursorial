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

Consequence: the fix makes *rendering* survive the clear, but *mouse hit-testing* is stale until
re-anchored. This is a genuine residual imperfection, not a full cure. Mitigations, in order of
preference:
- **Re-anchor the origin via DSR-CPR opportunistically** — on terminal focus-in (a clear is often
  followed by the user clicking back), and/or lazily before the first mouse event after a
  suspected desync. Cheap, bounded, and the hardware cursor rides the region so the reply
  re-derives `origin = reported − believed cursor row` (the existing re-anchor formula).
- Accept a stale-mouse window between clear and the next re-anchor (rendering is already correct;
  only clicks in that window mis-map).

**This caveat is why the task can't be "relative and done."** The honest end state is *relative
rendering + an origin kept solely for mouse, re-anchored on focus-in*.

### 4b. "Rigid translation" is terminal-specific — best-effort, not provable
The invariance argument assumes a clear moves content **and** cursor by the same delta. Real
terminals differ:
- If clear homes the cursor to `(0,0)` and the region was parked at its bottom, `CUU(height−1)`
  clamps at row 0 and the region repaints at the top — **works** (the region *is* now at top).
- If clear leaves the cursor mid-screen with region rows wiped, the next repaint lands wherever the
  cursor sits — **may be wrong**.
- kitty `cmd+K` scrolls the current line to the top; whether the region *below* the parked cursor
  survives or scrolls into cleared history needs empirical confirmation on the support matrix
  (kitty, Ghostty, WezTerm, Apple Terminal, Windows Terminal).

Consequence: relative is **strictly better** than absolute (it can't be *more* wrong), but it is
best-effort. Frame the acceptance criterion as "the reported kitty `cmd+K` case is fixed and no
supported terminal regresses," not "provably correct under all clears."

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

1. **Relative emission in `FrameRenderer`** (behind an option, e.g. `FrameRendererOptions` gains a
   relative-inline flag, or it becomes the inline default). `MoveTo` relative; frame-start
   `CR`+`CUU(height−1)`+`ED 0`; frame-end park at bottom-left. Stop using `RowOffset` for
   rendering; keep the field for mouse. Adapt the headless inline byte assertions (CUP → CUU/CR+CUF).
2. **Exit + fragments + caret** relative (Clear/Retain, fragment placement, caret band).
3. **Mouse-origin heal** — re-anchor the origin via DSR-CPR on focus-in (and/or lazily pre-mouse).
   Document the residual stale-mouse window (caveat 4a).
4. **Re-validate** growth/scroll/resize under the relative model; update `inline-presentation.md`
   (the origin protocol section becomes "origin for mouse only").

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
