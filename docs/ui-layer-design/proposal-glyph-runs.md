# Proposal: scaled text and FIGlet as first-class styled runs

*Status: **Phase 1 landed** (2026-08-02, `feat/glyph-runs`) — `GlyphSource` on runs,
capability-resolved at layout (`ResolveFor`: unsupported sizing → fallback face, tier parity),
band geometry with paragraph-level `VerticalTextAlignment`, per-piece painting (direct face /
OSC 66 fragment per piece), and the block sugar collapse (`FormattedFigletBlock` /
`FormattedSizedTextBlock` and their painters deleted; block forms are one-run paragraphs).
Phase 2 (interaction) and Phase 3's editor adoption landed 2026-08-02: metrics-aware
`TextLayout`/`GraphemeLayout`, the `TextElement.Sizing` inherited attached property, sized
`TextBox` editing end-to-end (band measure/scroll, bottom-row caret anchor, atomic glyph caret
units, scaled hit-testing, selection as split OSC 66 fragments with SGR backdrops), and the
budget gate pinned as a test (an untouched editor's fragment never re-emits on another's
selection move). The kitty multiple-cursors glyph-height caret band lands alongside.*

## The ask

Scaled text (OSC 66) and FIGlet headlines should be usable **anywhere text is** — inline in a
paragraph, inside a `TextBox` — and participate in selection, highlighting, caret movement, and
editing. Today both are block-level islands: `FormattedFigletBlock` and `FormattedSizedTextBlock`
sit beside paragraphs, unaddressable by `TextEditing`, invisible to selection, and unusable
inline. The instinct behind the ask: **decompose both into styled runs** and let the ordinary
run pipeline carry them.

## Why this is now tractable

The metrics round removed the structural blocker. Layout no longer assumes one glyph source per
block — the tokenizer, packer, splitter, and trimmer all measure through `GlyphMetrics` in cell
units end to end, and `LogicalStart`/brush scopes are cells too. That means "what does this text
occupy" is already answerable per cluster for all three glyph sources. What remains is
*attribution* (whose metrics apply to which run) and *painting/interaction*.

## Design

### 1. Per-run glyph source

```csharp
/// <summary>A run's glyph source: how its clusters measure and paint. Null font = the
/// monospace identity; a non-default Sizing renders via OSC 66 (font fallback below).</summary>
public sealed record GlyphSource(IGlyphFont? Font, TextSizing Sizing)
{
    public static GlyphSource Default { get; } = new(null, default);
    public GlyphMetrics GetMetrics() => ...; // identity / GetScaledMetrics / font.GetMetrics()
}
```

- `TextRun` (the RichText inline) gains an optional `GlyphSource` — authored as
  `rtb.Run("BIG", sizing: …)` / `<Run Sizing="2">` / a future `FontSize`-style styled property
  on `TextElement` that `TextBlock`/`TextBox` forward per run.
- `FormattedTextRun` carries the source through layout so the painter and `TextEditing` see it.
- The Tokenizer resolves metrics **per run** instead of per call: `_metrics` becomes
  `run.Source.GetMetrics()` for the duration of `EmitRun`. Everything downstream already speaks
  cells, so packing, splitting, soft hyphens, and ellipses need no further change. A mixed-size
  ellipsis takes the metrics of the run it visually joins (`LastTextRunStyle` already finds that
  run; extend it to return the source).

### 2. Line geometry with mixed sources

- `LineDraft`/`FormattedLine` gain `Rows` = max of their runs' `LineRows` (1 today). The
  paragraph's `Size.Rows` becomes the sum of line rows; `ApplyDocumentRowCap` and the painter's
  per-line advance use `line.Rows` instead of `1`. This is mechanical — the row budget code is
  already row-count-based.
- Vertical placement inside a taller line is governed by a **paragraph-level vertical text
  alignment** (maintainer decision, 2026-08-02): `TextParagraph` gains a
  `VerticalTextAlignment` (Top/Center/Bottom/**Baseline**, default Bottom — OSC 66's default,
  and the only honest rule for a band mixing a face with sized text, whose cell blocks the
  terminal fills and which therefore expose no baseline), carried onto `FormattedParagraph` and
  applied by the painter when placing each run within its line band. Block-level for authors —
  one rule per paragraph keeps mixed lines coherent; `TextSizing.Vertical` remains the escape
  hatch for a scaled run that must deviate.

  **Amended 2026-08-05 (vertical metrics).** Two corrections to the sketch above, both landed:

  1. *Bottom is not baseline alignment.* The original wording — "default Bottom, terminal text
     is baseline-bottom" — conflated the band's bottom ROW with the face's BASELINE row. They
     coincide only for a zero-descent face. Against `standard.flf` (6 rows, baseline 5, one
     descender row) bottom-aligning a one-cell run drops it UNDER the glyph bodies rather than
     beside them. Faces now declare vertical metrics (`IGlyphFont.Baseline` — a row COUNT, not a
     0-based index — parsed from the FLF header's baseline field), `GlyphMetrics`/`FormattedRun`
     mirror them, and `VerticalTextAlignment.Baseline` (declared last, so `default` stays
     `Bottom`) lines the runs' baselines up. `Bottom` keeps its literal meaning and its default
     slot.
  2. *One per-run escape hatch exists, and it is the formatter's, not the author's.*
     `FormattedRun.VerticalAlignment` is a nullable override the painter prefers over the
     paragraph rule. Author content never carries one — the authoring surface is still
     block-level, as decided. The formatter sets it only on runs IT synthesizes whose glyph
     source differs from the run they visually join: today exactly one, the last-resort trim
     indicator painted by the terminal's own font beside a face that can draw no indicator of
     its own. Having substituted a foreign face against what the author asked for, the formatter
     owns where that face's single cell lands, under every paragraph rule.
- Justify/alignment padding stays identity-width spaces (cells are cells).

### 3. Painting

- **Scaled runs**: the paragraph painter knows each run piece's cell rect after layout; it
  attaches one `SizedTextFragment` per painted piece (position, text, sizing, resolved style).
  This is the same emission model the block form uses — chunk-per-escape is *already* how the
  writer handles long text — just anchored per run piece instead of per block. Fallback when
  OSC 66 is unsupported: paint through the fallback font as the block path does; the metrics
  already made room for it.
- **FIGlet runs**: paint via `face.Paint` at the piece's rect. A figlet run forces its line to
  face height — legal everywhere, sensible in headlines; nothing to forbid.
- The compositor/diff needs no changes: fragments and cells both re-emit per frame from the
  retained buffer.

### 4. Selection, caret, editing (`TextEditing`)

- Hit-testing and position mapping are **already cell-based**; with per-run metrics, mapping a
  buffer cell to a logical position inside a scaled run is `cellOffset / clusterWidth` on the
  run's own metrics — the same walk `SplitWordAtChar` does today. Snapping: positions inside a
  cluster's cell block round to the cluster boundary (exactly wide-glyph behavior, scaled up).
- **Selection highlight**: cell-level styling. For scaled runs the fragment must split at
  selection boundaries so the selected sub-range emits with the selection style — the emission
  already splits at byte caps, so boundary splitting is the same machinery with different cut
  points. For FIGlet, the face paints cell-by-cell (`GlyphStyleProvider`), so selection is a
  per-cell style provider — the gradient hook, reused.
- **Caret**: the hardware caret is one cell; it anchors at the leading cell of the caret
  cluster, **bottom row** of the line band (that anchor also keeps IME/accessibility tracking
  sane). On terminals supporting the kitty *multiple-cursors protocol*
  (`CSI > SHAPE;COORD_TYPE:COORDS SP q`, maintainer suggestion 2026-08-02), the caret grows to
  **glyph height**: one rectangle-form escape sets a beam cursor on every row of the band's
  leading column (`CSI > 2;4:top:col:bottom:col SP q`), and the protocol guarantees extra
  cursors share the main cursor's color/opacity/blink — so the OSC 12 accent theming and blink
  phase stay coherent for free. Extra cursors are screen-fixed (unaffected by IND/RI), which
  suits the frame model exactly: the caret service re-emits the band through the out-of-band
  control-sequence channel as the caret moves and issues the universal clear
  (`CSI > 0;4 SP q`-style) on hide. Support is negotiated with the protocol's query
  (`CSI > SP q`) during host open like the other kitty opt-ins; non-supporting terminals keep
  the single bottom-row caret.
- **Editing**: insertion/deletion operate on the source text; runs re-tokenize per edit as they
  do now. A `TextBox` line containing a scaled run becomes `LineRows` tall — the editing
  surface re-measures like any wrap change. Scaled and FIGlet glyphs are **atomic caret
  units** exactly as monospace grapheme clusters are (maintainer decision, 2026-08-02): the
  caret lands only on glyph boundaries, selection extends whole glyphs, and deletion removes a
  whole glyph — there is no "inside" a glyph, whatever its cell footprint. This is the wide-
  cluster rule scaled up, so `TextEditing` needs no new concepts, only per-run metrics.

### 5. What stays block-level

The block forms remain (they're the right authoring surface for headlines and get block
alignment/margins), but both become thin sugar: a `SizedTextBlock` is a one-run paragraph with a
`GlyphSource`; `FormatSizedTextBlock`/`FormatFigletBlock` collapse into `FormatParagraph` and
two `FormattedBlock` types disappear from the painter. That deletion — one paint path, one trim
path, one trimmed-flag path — is the real payoff beyond the new capability.

## Phasing

1. **Runs + layout** — `GlyphSource` on `TextRun`/`FormattedTextRun`, per-run Tokenizer metrics,
   `FormattedLine.Rows`, paragraph painter (scaled fragments per piece, figlet per piece).
   Block forms rewritten as sugar; matrix tests move over intact. **Landed** — with one addition
   the sketch missed: sources resolve against the terminal at LAYOUT time (`GlyphSource.ResolveFor`),
   because measurement must agree with the paint-time fallback tier, and one hazard worth
   recording: a font-sourced piece must paint through `IGlyphFont.Paint` directly — routing it
   through `ScaledText` recurses (its placeholder path formats a figlet block, which is itself a
   font-sourced run).
2. **Interaction** — `TextEditing` per-run metrics, selection splitting, caret placement
   (incl. the multiple-cursors capability negotiation + glyph-height caret band in
   `TerminalCaretService`).
3. **Adoption** — `TextBox`/`RichTextPresenter` surface the authoring API; theme/styling hooks
   (a `Sizing` setter on `TextElement`, so `<Setter Property="Sizing" …>` works via B15).

## Resolved (maintainer, 2026-08-02)

- **Vertical placement**: paragraph-level vertical text alignment (folded into §2 above), not
  per-run and not tallest-run-dictated. *(Still the authoring rule after the 2026-08-05
  amendment: the one per-run override is internal to the formatter, for runs it synthesizes.)*
- **FIGlet in `TextBox`**: fully editable — glyphs are atomic caret/selection/deletion units
  exactly like monospace clusters (folded into §4 above). No measure-only mode needed.
- **Fragment-count budget test**: agreed as a Phase 2 entry gate — measure re-emit cost with a
  selection dragged across a long scaled paragraph before building selection splitting on it.
