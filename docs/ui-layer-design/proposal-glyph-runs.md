# Proposal: scaled text and FIGlet as first-class styled runs

*Status: design sketch (maintainer-requested, 2026-08-02). Builds on the `GlyphMetrics` layer
landed in the review-fixes round; nothing here is implemented.*

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
- Vertical placement inside a taller line: identity runs sit on the line's **bottom** row
  (terminal text is baseline-bottom), which matches OSC 66's default vertical alignment and
  looks right next to scaled neighbors. `TextSizing.Vertical` can override per run later.
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
- **Caret**: the terminal caret is one cell; place it at the leading cell of the caret cluster,
  bottom row of the line band. Kitty renders the hardware cursor fine over OSC 66 cells.
- **Editing**: insertion/deletion operate on the source text; runs re-tokenize per edit as they
  do now. A `TextBox` line containing a scaled run becomes `LineRows` tall — the editing
  surface re-measures like any wrap change.

### 5. What stays block-level

The block forms remain (they're the right authoring surface for headlines and get block
alignment/margins), but both become thin sugar: a `SizedTextBlock` is a one-run paragraph with a
`GlyphSource`; `FormatSizedTextBlock`/`FormatFigletBlock` collapse into `FormatParagraph` and
two `FormattedBlock` types disappear from the painter. That deletion — one paint path, one trim
path, one trimmed-flag path — is the real payoff beyond the new capability.

## Phasing

1. **Runs + layout** — `GlyphSource` on `TextRun`/`FormattedTextRun`, per-run Tokenizer metrics,
   `FormattedLine.Rows`, paragraph painter (scaled fragments per piece, figlet per piece).
   Block forms rewritten as sugar; matrix tests move over intact.
2. **Interaction** — `TextEditing` per-run metrics, selection splitting, caret placement.
3. **Adoption** — `TextBox`/`RichTextPresenter` surface the authoring API; theme/styling hooks
   (a `Sizing` setter on `TextElement`, so `<Setter Property="Sizing" …>` works via B15).

## Open questions for the maintainer

- Is bottom-row alignment the right default for identity runs beside scaled ones, or should the
  line's tallest run dictate a configurable line alignment?
- Should FIGlet runs be allowed inside `TextBox` editing, or measure-only there (selection yes,
  caret snapping to run boundaries)? Editing inside a 6-row glyph is odd UX either way.
- Fragment count: a selection dragged across a long scaled paragraph re-emits O(pieces)
  fragments per frame. Almost certainly fine (they're one escape each), but worth a budget test
  before Phase 2.
