using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI.Themes;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;

// ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

namespace Cursorial.UI.Controls;

/// <summary>
/// Renders the text of its <see cref="UIElement.TemplatedParent"/> <see cref="TextBox"/> (the
/// <c>PART_TextPresenter</c>): the text or the <see cref="TextBox.Placeholder"/>, the selection highlight,
/// and the <b>real terminal caret</b> via S1's <see cref="ITerminalCaretService"/> (design doc §5.9 /
/// §3.9-TextBox). A single-line field owns a horizontal scroll offset; a multi-line field
/// (<see cref="TextBox.IsMultiLine"/>) lays out visual lines via <see cref="TextLayout"/> and owns both a
/// vertical (line) and horizontal (column) scroll offset that keep the caret in view, and measures its height
/// from the line count clamped to <see cref="TextBox.MinLines"/>/<see cref="TextBox.MaxLines"/>.
/// <para>
/// A <b>clipped render boundary</b> (<see cref="UIElement.ClipToBounds"/>): a wide cluster or a scrolled line
/// straddling a viewport edge is clipped per cell (no bleed into the field chrome), a scroll change re-rasters
/// only this zone (not the window scene), and a scrolled-out caret is dropped by the caret service's zone-clip
/// gate. Clears its caret publication on detach.
/// </para>
/// </summary>
public sealed class TextPresenter : UIElement
{
    private int _scrollColumn;     // horizontal scroll, in display columns
    private int _scrollRow;        // vertical scroll, in visual lines (multi-line)
    private int _viewportColumns;  // the last arranged width
    private int _viewportRows = 1; // the last arranged height, in rows

    /// <summary>Creates the presenter as a clipped render boundary (the scroll clip).</summary>
    public TextPresenter()
    {
        ClipToBounds = true;
    }

    /// <summary>
    /// The editing glyph source (proposal-glyph-runs Phase 2/3): resolved from the attached
    /// <see cref="TextElement.SizingProperty"/> against the terminal at use — so wrap widths,
    /// caret columns, hit-testing, and paint all agree on the tier (scaled on OSC 66 terminals,
    /// the bundled fallback face elsewhere). Identity for a normal (unsized) editor.
    /// </summary>
    internal GlyphSource EditingSource
    {
        get
        {
            var sizing = TextElement.GetSizing(this);
            var font = TextElement.GetFont(this);

            if (sizing.IsNormal && font is null)
                return GlyphSource.Default;

            var source = new GlyphSource(font, sizing);
            return UIApplication.Current is {} app ? source.ResolveFor(app.EffectiveCapabilities.Output) : source;
        }
    }

    /// <summary>Null for the identity source (the zero-cost fast path every plain editor takes).</summary>
    internal GlyphMetrics? EditingMetrics
    {
        get
        {
            var source = EditingSource;
            return ReferenceEquals(source, GlyphSource.Default) ? null : source.Metrics;
        }
    }

    /// <summary>Rows one visual line's band occupies (1 for plain editors).</summary>
    private int LineRows => EditingMetrics?.LineRows ?? 1;

    /// <summary>The current horizontal scroll offset, in display columns (test observability).</summary>
    public int ScrollOffset => _scrollColumn;

    /// <summary>The current vertical scroll offset, in visual lines — 0 for a single-line field (test observability).</summary>
    public int ScrollRow => _scrollRow;

    private TextBox? Owner => TemplatedParent as TextBox;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        int caretAffordance = 1;

        // if (UIApplication.Current?.Capabilities.Output.Cursor.ShapeControl is not false)
        //     caretAffordance = 0;

        var owner = Owner;
        if (owner is null)
            return new Size(1, 1);

        var metrics = EditingMetrics;
        var lineRows = metrics?.LineRows ?? 1;

        // Single line: desire the displayed text's width plus one column for the end caret; the field stretches
        // when given more and scrolls when given less. A sized editor's line is a LineRows-tall band.
        if (!owner.IsMultiLine)
        {
            var textWidth = metrics?.StringWidth(owner.DisplayText) ?? GraphemeWidth.StringWidth(owner.DisplayText);
            return new Size(textWidth + caretAffordance, lineRows);
        }

        // Multi-line: lay out against the available width (an Unbounded width naturally disables wrapping, since
        // no line exceeds it), then reserve clamp(lineCount, MinLines, MaxLines) bands of LineRows rows.
        var wrap = owner.TextWrapping != WrapMode.NoWrap;
        var wrapWidth = wrap ? Math.Max(1, availableSize.Columns) : 0;
        var layout = TextLayout.Build(owner.DisplayText, wrapWidth, owner.TextWrapping, metrics);
        var rows = ClampRows(layout.LineCount, owner.MinLines, owner.MaxLines) * lineRows;
        var width = wrap && !LayoutMath.IsUnbounded(availableSize.Columns)
            ? Math.Max(1, availableSize.Columns) // wrap fills the width
            : layout.MaxWidth + caretAffordance; // NoWrap (or unknown width) desires the widest line + the end caret
        return new Size(width, rows);
    }

    private static int ClampRows(int lineCount, int minLines, int maxLines)
    {
        var min = Math.Max(1, minLines);
        var max = maxLines <= 0 ? int.MaxValue : Math.Max(min, maxLines);
        return Math.Clamp(lineCount, min, max);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewportColumns = finalSize.Columns;
        _viewportRows = Math.Max(1, finalSize.Rows);
        RefreshCaretAndScroll();
        return finalSize;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        UIApplication.Current?.CaretService.Clear(this);
        base.OnDetachedFromTree(in e);
    }

    /// <summary>
    /// Re-anchors the scroll offset(s) so the caret stays in view, then (re)publishes or clears the terminal
    /// caret. Called from arrange (viewport known) and from the owner on every model / focus change.
    /// </summary>
    internal void RefreshCaretAndScroll()
    {
        var owner = Owner;
        if (owner is null)
            return;

        if (owner.IsMultiLine)
            RefreshMultiLine(owner);
        else
            RefreshSingleLine(owner);
    }

    private void RefreshSingleLine(TextBox owner)
    {
        var layout = GraphemeLayout.Build(owner.DisplayText, EditingMetrics);
        var caretColumn = layout.ColumnOf(owner.ToDisplayIndex(owner.CaretIndex));
        var viewport = _viewportColumns;

        if (viewport > 0)
        {
            var slack = viewport > 4 ? 2 : 0; // keep a little context around the caret (spec: 2-column edge slack)
            if (caretColumn - slack < _scrollColumn)
                _scrollColumn = Math.Max(0, caretColumn - slack);
            else if (caretColumn + slack >= _scrollColumn + viewport)
                _scrollColumn = caretColumn + slack - viewport + 1;

            var maxScroll = Math.Max(0, layout.TotalColumns + 1 - viewport); // +1: room for the end caret
            _scrollColumn = Math.Clamp(_scrollColumn, 0, maxScroll);
        }
        else
        {
            _scrollColumn = 0;
        }

        _scrollRow = 0;
        PublishCaret(caretColumn - _scrollColumn, 0);
    }

    private void RefreshMultiLine(TextBox owner)
    {
        var wrap = owner.TextWrapping != WrapMode.NoWrap;
        var wrapWidth = wrap ? Math.Max(1, _viewportColumns) : 0;
        var layout = TextLayout.Build(owner.DisplayText, wrapWidth, owner.TextWrapping, EditingMetrics);
        var (caretRow, caretColumn) = layout.Locate(owner.ToDisplayIndex(owner.CaretIndex), owner.CaretLineEndAffinity);

        // Vertical scroll: keep the caret's visual line in view. Scroll math runs in LINES; the
        // viewport holds viewportRows / LineRows whole bands.
        var visibleLines = Math.Max(1, _viewportRows / LineRows);
        if (_viewportRows > 0)
        {
            if (caretRow < _scrollRow)
                _scrollRow = caretRow;
            else if (caretRow >= _scrollRow + visibleLines)
                _scrollRow = caretRow - visibleLines + 1;

            var maxRow = Math.Max(0, layout.LineCount - visibleLines);
            _scrollRow = Math.Clamp(_scrollRow, 0, maxRow);
        }
        else
        {
            _scrollRow = 0;
        }

        // Horizontal scroll: only NoWrap can overflow a line; wrapping pins to the left edge.
        if (wrap || _viewportColumns <= 0)
        {
            _scrollColumn = 0;
        }
        else
        {
            var slack = _viewportColumns > 4 ? 2 : 0;
            if (caretColumn - slack < _scrollColumn)
                _scrollColumn = Math.Max(0, caretColumn - slack);
            else if (caretColumn + slack >= _scrollColumn + _viewportColumns)
                _scrollColumn = caretColumn + slack - _viewportColumns + 1;

            var maxScroll = Math.Max(0, layout.MaxWidth + 1 - _viewportColumns);
            _scrollColumn = Math.Clamp(_scrollColumn, 0, maxScroll);
        }

        PublishCaret(caretColumn - _scrollColumn, caretRow - _scrollRow);
    }

    /// <summary>The arranged height in rows — the multi-line page size (owner/test observability).</summary>
    internal int ViewportRows => _viewportRows;

    /// <summary>The arranged content width in columns — the soft-wrap budget (test observability).</summary>
    internal int ViewportColumns => _viewportColumns;

    private TextLayout CurrentLayout(TextBox owner)
    {
        var wrap = owner.TextWrapping != WrapMode.NoWrap;
        return TextLayout.Build(owner.DisplayText, wrap ? Math.Max(1, _viewportColumns) : 0, owner.TextWrapping,
                                EditingMetrics);
    }

    /// <summary>
    /// The model offset on the visual line <paramref name="delta"/> rows from the caret's line, landing at
    /// <paramref name="desiredColumn"/> (or the caret's current column when negative). The current line is
    /// resolved with <paramref name="endAffinity"/> (the soft-wrap affinity carried across a vertical run, so a
    /// caret clamped to a wrapped line's end stays on that line). Returns the new offset, the column to keep
    /// sticky across the run (Up / Down / PageUp / PageDown), and the end-affinity of the landing offset.
    /// </summary>
    internal (int Offset, int Column, bool EndAffinity) MoveVertical(int caretOffset, int delta, int desiredColumn, bool endAffinity)
    {
        var owner = Owner;
        if (owner is null)
            return (caretOffset, desiredColumn, endAffinity);

        var layout = CurrentLayout(owner);
        var (line, column) = layout.Locate(owner.ToDisplayIndex(caretOffset), endAffinity);
        var wantColumn = desiredColumn >= 0 ? desiredColumn : column;
        var targetLine = Math.Clamp(line + delta, 0, layout.LineCount - 1);
        var targetOffset = layout.OffsetAt(targetLine, wantColumn);
        return (owner.ToModelIndex(targetOffset), wantColumn, layout.IsLineEndBoundary(targetLine, targetOffset));
    }

    /// <summary>The model offset at the start of the caret's visual line (per-line Home; always start-affinity).</summary>
    internal int LineStart(int caretOffset, bool endAffinity)
    {
        var owner = Owner;
        if (owner is null)
            return caretOffset;

        var layout = CurrentLayout(owner);
        var (line, _) = layout.Locate(owner.ToDisplayIndex(caretOffset), endAffinity);
        return owner.ToModelIndex(layout.LineContentStart(line));
    }

    /// <summary>
    /// The model offset at the end of the caret's visual line content (per-line End), plus the end-affinity that
    /// keeps the caret at that visual line's end rather than aliasing to the next soft-wrapped line's start.
    /// </summary>
    internal (int Offset, bool EndAffinity) LineEnd(int caretOffset, bool endAffinity)
    {
        var owner = Owner;
        if (owner is null)
            return (caretOffset, false);

        var layout = CurrentLayout(owner);
        var (line, _) = layout.Locate(owner.ToDisplayIndex(caretOffset), endAffinity);
        var end = layout.LineContentEnd(line);
        return (owner.ToModelIndex(end), layout.IsLineEndBoundary(line, end));
    }

    /// <summary>The model offset under a presenter-local pointer position (multi-line hit testing).</summary>
    internal int OffsetFromPoint(int localColumn, int localRow)
    {
        var owner = Owner;
        if (owner is null)
            return 0;

        var wrap = owner.TextWrapping != WrapMode.NoWrap;
        var layout = CurrentLayout(owner);
        // A pointer row anywhere within a band selects that band's line (glyphs are atomic).
        var line = Math.Clamp(localRow / LineRows + _scrollRow, 0, layout.LineCount - 1);
        var column = Math.Max(0, localColumn) + (wrap ? 0 : _scrollColumn);
        return owner.ToModelIndex(layout.OffsetAt(line, column));
    }

    private void PublishCaret(int localColumn, int localLine)
    {
        if (UIApplication.Current?.CaretService is not {} service)
            return;

        // The caret anchors at the BOTTOM row of its line's band (identity: the line itself) —
        // the hardware cursor is one cell, and the bottom row keeps IME/accessibility tracking
        // sane for sized editors (proposal-glyph-runs §4). Publish only while the owning TextBox
        // holds physical focus and the band is within the viewport (a scrolled-out caret is
        // hidden, mirroring the column clip). The caret service also clip-gates the publication
        // and drops it on detach.
        int lineRows = LineRows;
        int localRow = localLine * lineRows + (lineRows - 1);
        var visible = IsAttachedToTree && Owner is { IsFocused: true }
            && localLine >= 0 && localRow < Math.Max(1, _viewportRows);
        if (visible)
            service.Publish(this, localColumn, localRow, CursorShape.BlinkingBar, rows: lineRows);
        else
            service.Clear(this);
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var owner = Owner;
        if (owner is null)
            return;

        var viewportColumns = context.Size.Columns;
        var viewportRows = context.Size.Rows;
        if (viewportColumns <= 0 || viewportRows <= 0)
            return;

        var text = owner.DisplayText ?? string.Empty;

        if (text.Length == 0)
        {
            if (owner.Placeholder is { Length: > 0 } placeholder)
            {
                // The placeholder is NOT the line base and deliberately does not ride it: it resolves
                // its own brush (muted, or the presenter's own on a tier with no muted colour to
                // carry the de-emphasis) and its own attribute word.
                var foreground = ResolveForegroundBrush();

                // MutedBrush carries the placeholder color on color tiers; Faint carries the de-emphasis on the
                // NoColor tier where MutedBrush resolves to Default (adoption-spec §5: placeholder → Faint).
                IBrush? muted;
                var baseStyle = CellStyle.Default.WithAttributes(TextElement.ComposeAttributes(owner).Flags);

                var lowFidelity = UIApplication.Current?.ActualThemeVariant.Tier < ColorDepth.Ansi256;

                if (lowFidelity)
                {
                    // WEIGHT IS AN AXIS, AND FAINT WINS. This used to be `attributes |= Faint`, which
                    // produced `Bold | Faint` from one property plus a tier check — a TextWeight="Bold"
                    // TextBox at Ansi16/NoColor. That state is not renderable: Bold and Faint share the
                    // SGR 22 reset, so reaching it emits ESC[1m from a Faint predecessor and ESC[2m from
                    // a Bold one, and the placeholder's weight ends up depending on what was painted
                    // before it. `Weighted` IMPOSES Faint and clears the inherited Bold, because the
                    // de-emphasis IS the placeholder treatment down here — on a tier with no muted colour
                    // to carry it, Faint is the entire signal that this is a prompt and not real content,
                    // so it must not be the side that loses.
                    baseStyle = PartialStyle.Weighted(TextWeight.Faint).ApplyTo(baseStyle);
                    muted = foreground;
                }
                else
                {
                    muted = ResolveBrush(ThemeKeys.MutedBrush) ?? foreground;
                }

                DrawText(context, 0, 0, placeholder, muted, null, baseStyle);
            }

            return;
        }

        IBrush? selectionBrush;
        
        var noColor = context.Capabilities.Color.Depth == ColorDepth.NoColor;
        if (noColor)
            selectionBrush = owner.IsFocused ? owner.SelectionBrush ?? ResolveBrush(ThemeKeys.SelectionBrush) : Brushes.Default;
        else
            selectionBrush = owner.SelectionBrush ?? ResolveBrush(owner.IsFocused ? ThemeKeys.SelectionBrush : ThemeKeys.SelectionInactiveBrush);
        
        var inverse = TextElement.GetInverse(this);
        // SelectionBounds are MODEL offsets — project them into the displayed text (identity for a TextBox).
        var (modelSelectionStart, modelSelectionEnd) = owner.SelectionBounds;
        var selectionStart = owner.ToDisplayIndex(modelSelectionStart);
        var selectionEnd = owner.ToDisplayIndex(modelSelectionEnd);

        // ONE line base for the whole render pass. It used to be resolved per LANE — once in the face
        // painter and once per run in the cell painter — with the foreground brush threaded alongside
        // it, which is two chances for the two lanes to disagree about the same ground state. This
        // file has already produced three separate invisible-selection defects out of exactly that
        // shape, so the base is built once here and handed down as a single carrier.
        var lineBase = ResolveLineBaseStyle(noColor);

        if (!owner.IsMultiLine)
        {
            RenderSingleLine(context, text, viewportColumns, lineBase, noColor, selectionBrush, selectionStart, selectionEnd, inverse);
            return;
        }

        var wrap = owner.TextWrapping != WrapMode.NoWrap;
        var wrapWidth = wrap ? Math.Max(1, viewportColumns) : 0;
        var layout = TextLayout.Build(text, wrapWidth, owner.TextWrapping, EditingMetrics);

        // Bands: each visual line paints LineRows tall; only whole bands that fit the viewport.
        int lineRows = LineRows;
        var lastRow = Math.Min(layout.LineCount, _scrollRow + Math.Max(1, viewportRows / lineRows));
        for (var row = _scrollRow; row < lastRow; row++)
        {
            var localRow = (row - _scrollRow) * lineRows;
            var lineStart = layout.LineContentStart(row);
            var lineEnd = layout.LineContentEnd(row);
            var glyphs = layout.LineGlyphs(row);

            // Visible char window within this line (NoWrap horizontal scroll; wrap keeps _scrollColumn 0). The
            // window is line-local (glyph columns), projected back to model offsets and clamped to the line.
            var firstChar = Math.Clamp(lineStart + glyphs.CharIndexAtOrBeforeColumn(_scrollColumn), lineStart, lineEnd);
            var lastChar = Math.Clamp(lineStart + glyphs.CharIndexAtOrAfterColumn(_scrollColumn + viewportColumns), lineStart, lineEnd);

            var selFrom = Math.Clamp(selectionStart, firstChar, lastChar);
            var selTo = Math.Clamp(selectionEnd, firstChar, lastChar);

            if (EditingSource is { Font: {} font, Sizing.IsNormal: true } faceSource && font != MonospaceFont.Default)
            {
                DrawFaceLine(context, glyphs, lineStart, text, firstChar, lastChar, selFrom, selTo, localRow,
                             lineBase, noColor, selectionBrush, inverse, faceSource);
            }
            else
            {
                DrawLineRun(context, glyphs, lineStart, text, firstChar, selFrom, localRow, lineBase, selected: false, noColor, selectionBrush, inverse);
                DrawLineRun(context, glyphs, lineStart, text, selFrom, selTo, localRow, lineBase, selected: true, noColor, selectionBrush, inverse);
                DrawLineRun(context, glyphs, lineStart, text, selTo, lastChar, localRow, lineBase, selected: false, noColor, selectionBrush, inverse);
            }
        }
    }

    private void RenderSingleLine(RenderContext context, string text, int viewport, in StyleDeltaTemplate lineBase,
                                  bool noColor, IBrush? selectionBrush, int selectionStart, int selectionEnd, bool inverse)
    {
        var layout = GraphemeLayout.Build(text, EditingMetrics);
        // The visible char window covers the viewport — boundary at/before the left edge through the boundary
        // at/after the right edge; the boundary clip absorbs the straddle on each side.
        var firstChar = layout.CharIndexAtOrBeforeColumn(_scrollColumn);
        var lastChar = layout.CharIndexAtOrAfterColumn(_scrollColumn + viewport);

        // Up to three runs — pre-selection, selection, post-selection — one DrawText call each.
        var selFrom = Math.Clamp(selectionStart, firstChar, lastChar);
        var selTo = Math.Clamp(selectionEnd, firstChar, lastChar);

        if (EditingSource is { Font: not null, Sizing.IsNormal: true } faceSource)
        {
            DrawFaceLine(context, layout, 0, text, firstChar, lastChar, selFrom, selTo, 0,
                         lineBase, noColor, selectionBrush, inverse, faceSource);
            return;
        }

        DrawLineRun(context, layout, 0, text, firstChar, selFrom, 0, lineBase, selected: false, noColor, selectionBrush, inverse);
        DrawLineRun(context, layout, 0, text, selFrom, selTo, 0, lineBase, selected: true, noColor, selectionBrush, inverse);
        DrawLineRun(context, layout, 0, text, selTo, lastChar, 0, lineBase, selected: false, noColor, selectionBrush, inverse);
    }

    /// <summary>
    /// Renders a FIGlet-sourced visual line: words paint WHOLE (their internal kerning intact —
    /// glyph geometry never depends on the caret or selection), word gaps stay rigid blanks, and
    /// the selection is a cell TINT over the selected span rather than a paint split — so a
    /// non-rectangular face's diagonal ink highlights as a clean rectangle without shifting.
    /// </summary>
    private void DrawFaceLine(RenderContext context, in GraphemeLayout glyphs, int lineStart, string text,
                              int from, int to, int selFrom, int selTo, int localRow,
                              in StyleDeltaTemplate lineBase, bool noColor, IBrush? selectionBrush, bool inverse,
                              GlyphSource source)
    {
        if (to <= from)
            return;

        int lineRows = source.Metrics.LineRows;

        var baseStyle = ResolveLineBaseValue(lineBase);

        int viewport = Math.Max(1, _viewportColumns);

        var figlet = source.Font is FigletFont;

        // Word tokens paint at their layout columns (whitespace-rigid advances make those
        // additive across gaps, so paint and caret math agree exactly). A horizontally
        // scrolled editor legally produces NEGATIVE anchors — the glyph-text primitive clips
        // like a cell write, so partially visible words paint their visible tail (the old
        // rect-based detour THREW on the first off-screen word).
        int i = from;
        while (i < to)
        {
            if (figlet && char.IsWhiteSpace(text[i])) { i++; continue; }

            int wordStart = i;
            while (i < to && (!figlet || !char.IsWhiteSpace(text[i]))) i++;

            int wordColumn = glyphs.ColumnOf(wordStart - lineStart) - _scrollColumn;
            int wordWidth = glyphs.ColumnOf(i - lineStart) - glyphs.ColumnOf(wordStart - lineStart);

            if (wordColumn + wordWidth <= 0) continue; // fully scrolled off to the left
            if (wordColumn >= viewport) break;         // this and everything after: off to the right

            // THE UNPACK, and the only place the face lane needs one: the primitive takes a brush and
            // a whole style, and does the per-cell sampling itself, so the base's brush channel goes
            // in one argument and its value channels in the other.
            var brushBounds = new Rect(Math.Max(0, wordColumn), localRow, Math.Max(1, wordWidth), lineRows);
            context.DrawGlyphText(source.Font!, wordColumn, localRow, text[wordStart..i],
                                  lineBase.Foreground, baseStyle, brushBounds);
        }

        // Special case for .caps-nocolor: if the text presentation area is inverted by default, then rather than
        // letting the glyphs carry the 'inverse' attribute, we paint the entire content-covered area with the inverse
        // attribute, then 'un-invert' only the selection rectangle. This is necessary for figlet fonts that only
        // write sparsely; otherwise the inversion would be partial (non-rectangular).
        //
        // Note that in the next 'if' block down, the selection block FLIPS whatever Inverse state each cell is
        // actually in, ensuring the highlighting contrasts the content regardless of whether the presenter is
        // light-on-dark or dark-on-light.
        if (inverse)
        {
            var startColumn = glyphs.ColumnOf(from - lineStart) - _scrollColumn;
            var endColumn = glyphs.ColumnOf(to - lineStart) - _scrollColumn;

            // The line's OWN attributes ride the fill (masked to the ones that may spread), not
            // TextAttributes.Inverse alone. FillOpaque writes space-bearing cells; CellBuffer.Set
            // rescues the grapheme and composites the foreground back, but NOT the attributes — so a
            // bare Inverse here silently stripped Bold / Italic / … from every cell the face inked.
            context.FillOpaque(new Rect(startColumn, localRow, endColumn - startColumn, lineRows),
                               Color.Transparent,
                               (baseStyle.Attributes & FillAttributes) | TextAttributes.Inverse);
        }

        // Selection: tint the selected span in place — graphemes untouched. Clamped to the
        // viewport (a scrolled selection can start left of it or run past its right edge).
        if (selTo > selFrom && (!noColor || Owner?.IsFocused is true))
        {
            int selStart = Math.Max(0, glyphs.ColumnOf(selFrom - lineStart) - _scrollColumn);
            int selEnd = Math.Min(viewport, glyphs.ColumnOf(selTo - lineStart) - _scrollColumn);

            if (selEnd > selStart)
            {
                var selRect = new Rect(selStart, localRow, selEnd - selStart, lineRows);

                // The tint is a DELTA against whatever each cell already holds, so this call site does
                // not need to know the presenter's inverse state at all — which is the point: the
                // toggle reads the CELL, not a presenter-level bool standing in for it.
                context.TintCells(selRect,
                                  ResolveSelectionStyle(noColor, selectionBrush,
                                                        selStart + (selEnd - selStart) / 2, localRow, selRect));
            }
        }
    }

    /// <summary>
    /// Attributes safe to spread across cells the presenter did not ink. An ALLOWLIST, never a
    /// denylist: a new <see cref="TextAttributes"/> member must default to NOT spreading, because the
    /// failure mode of spreading something unnoticed is far louder than the failure mode of not
    /// spreading it.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><see cref="TextAttributes.Bold"/> / <see cref="TextAttributes.Faint"/> /
    /// <see cref="TextAttributes.Italic"/> are invisible on a blank cell, so spreading them is free and
    /// keeps the SGR state uniform across the band.</item>
    /// <item><see cref="TextAttributes.Inverse"/> is visible on a blank and is the entire reason the
    /// fill exists.</item>
    /// <item><see cref="TextAttributes.Underline"/> / <see cref="TextAttributes.Strikethrough"/> /
    /// <see cref="TextAttributes.Overline"/> are visible on blanks and MUST be excluded — spreading
    /// them would rule every row of a multi-row FIGlet band.</item>
    /// <item><see cref="TextAttributes.Blink"/> is excluded deliberately even though it is only
    /// conditionally ink-dependent: as long as there is a mechanism to accomplish what the author
    /// wants, the common case should steer into the pit of success. The asymmetry of harm decides it —
    /// excluded-but-wanted is a subtle inconsistency, included-but-unwanted strobes the whole content
    /// rect.</item>
    /// <item><see cref="TextAttributes.Hidden"/> is a no-op on blanks by definition, so it is safe.</item>
    /// </list>
    /// The mask lives HERE, in the policy, and deliberately not in <c>FillOpaque</c>: the primitive must
    /// keep painting exactly what it is handed — an author who passes <c>Blink</c> asked for it — and
    /// masking there would remove the very escape hatch this ruling depends on.
    /// </remarks>
    private const TextAttributes FillAttributes =
        TextAttributes.Bold | TextAttributes.Faint | TextAttributes.Italic |
        TextAttributes.Inverse | TextAttributes.Hidden;

    /// <summary>
    /// THE selection rule, in one place: <b>no usable selection brush ⇒ TOGGLE
    /// <see cref="TextAttributes.Inverse"/>; a usable brush ⇒ paint its background and CLEAR
    /// <see cref="TextAttributes.Inverse"/>.</b> All three painting lanes (plain cells, sized runs,
    /// glyph-face tints) route through here — each used to hand-roll it, and they drifted: the sized
    /// lane's spelling left the selection INVISIBLE on a colour tier whose theme resolves no
    /// <c>SelectionBrush</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why CLEAR on the brush path.</b> If <see cref="TextAttributes.Inverse"/> survives, the
    /// terminal swaps the channels at render time: the brush's <em>background</em> colour comes out as
    /// the visible foreground and the band the caller meant to paint never appears — and the visible
    /// foreground then comes from the cell's background channel, which is <see cref="Color.Transparent"/>
    /// here, so the text renders as the backdrop. Clearing is what makes the brush's declared channel the
    /// one you see. (It used to happen by accident, as a side effect of <c>TintCells</c> clearing
    /// Inverse unconditionally, and nobody could say why.)
    /// </para>
    /// <para>
    /// <b>Why TOGGLE on the no-brush path.</b> Forcing the flag depends on a presenter-level bool being
    /// an accurate proxy for every cell's state, and when the proxy is wrong the selection is
    /// <em>invisible</em> — forcing Inverse on cells that are already inverse changes nothing. A toggle
    /// reads each cell's actual state instead, which is what "the selection flips the content" always
    /// meant; the old ternaries were hand-rolled XORs standing in for an operation the old API could not
    /// express.
    /// </para>
    /// <para>
    /// <paramref name="noColor"/> is not a selection concept here — it only decides whether a brush is
    /// USABLE. Neither is a brush that samples to the terminal default: <c>Brushes.Default</c> is a legal
    /// <see cref="TextBox.SelectionBrush"/>, and a default background is indistinguishable from no
    /// selection at all, so it falls through to the toggle rather than painting nothing. (A
    /// present-but-default background is a real opinion to a <see cref="PartialStyle"/> where it was
    /// silence to a <see cref="CellStyle"/>, so the guard has to be stated rather than inherited.)
    /// </para>
    /// </remarks>
    private static PartialStyle ResolveSelectionStyle(bool noColor, IBrush? selectionBrush,
                                                      int column, int row, in Rect bounds)
    {
        var background = noColor ? null : selectionBrush?.ColorAt(column, row, bounds);

        return background is { IsDefault: false } color
                   ? PartialStyle.WithBackground(color).Removing(TextAttributes.Inverse)
                   : PartialStyle.WithToggled(TextAttributes.Inverse);
    }

    /// <summary>The presenter's foreground BRUSH — what the line base rides on, and the placeholder's
    /// fallback when its own muted brush does not resolve.</summary>
    private IBrush? ResolveForegroundBrush() => Owner?.Foreground ?? ResolveBrush(ThemeKeys.TextBrush);

    /// <summary>
    /// A visual line's GROUND STATE: the style every run, fill and tint on that line is then a delta
    /// against. A <see cref="StyleDeltaTemplate"/> rather than a <see cref="CellStyle"/> for one
    /// reason — <b>capability</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ownership rule this presenter otherwise follows (an operation that OWNS its cells takes a
    /// whole <see cref="CellStyle"/>; one that MODIFIES cells somebody else wrote takes a delta) says
    /// a ground state may be a whole style, and that argument is sound as far as it goes. It is not
    /// decisive, because it answers the wrong question: a <see cref="CellStyle"/> can only carry a
    /// RESOLVED <see cref="Color"/>, so a base built as one can never be brushed. The rule says the
    /// base may own its cells; it does not say the base must be colour-resolved.
    /// </para>
    /// <para>
    /// What that bought before was a parallel parameter: the brush travelled BESIDE the base through
    /// every signature between here and the paint, and each painting lane re-decided "is there a
    /// brush?" at the end of it. One carrier ends both.
    /// </para>
    /// </remarks>
    private StyleDeltaTemplate ResolveLineBaseStyle(bool noColor)
    {
        var attr = TextElement.ComposeAttributes(this);

        // Inverse is the PRESENTER's axis, not the run's, and each lane imposes it the way its own
        // geometry needs: the face lane as one whole-band fill (so a sparse FIGlet inverts as a clean
        // rectangle rather than per glyph hole), the cell lane per run. So the base states it OFF —
        // except on NoColor, where the theme's caps rule makes Inverse the only content signal there
        // is and the base carries it. Verbatim from the value form this replaced.
        var flags = noColor ? attr.Flags : attr.Flags & ~TextAttributes.Inverse;

        var template = new StyleDeltaTemplate
        {
            Foreground = ResolveForegroundBrush(),

            // Sentinels, not authored fills, and both position-independent — which is what lets
            // ResolveLineBaseValue sample them at a fixed point. Transparent lets whatever the field's
            // chrome painted show through under the glyph; Default is NoColor's "say nothing".
            Background = noColor ? Brushes.Default : Brushes.Transparent,
        };

        // The attribute WORD is folded per AXIS rather than unioned in wholesale, for the reason
        // DrawingContext.CreateBrushResolver gives at length: Bold and Faint share the SGR 22 reset,
        // so `Bold | Faint` is not two attributes but a state the encoder cannot spell. Setting's
        // guard rejects the axis-owning flags outright, which is the type refusing to let this be
        // written the short way.
        if ((flags & TextAttributes.Bold) != 0)
            template = template.Weighing(TextWeight.Bold);
        else if ((flags & TextAttributes.Faint) != 0)
            template = template.Weighing(TextWeight.Faint);

        if ((flags & TextAttributes.Italic) != 0)
            template = template.Posturing(TextStyle.Italic);

        var booleans = flags & PartialStyle.Booleans;
        if (booleans != default)
            template = template.Applying(booleans);

        // A shape implies the flag (PartialStyle.ApplyTo derives it structurally), so stating the
        // shape is the whole of "underline, in this style" — and stating it only when the fold
        // delivered the flag is what keeps the two from disagreeing.
        if ((flags & TextAttributes.Underline) != 0)
            template = template with { UnderlineShape = attr.UnderlineShape };

        return template;
    }

    /// <summary>
    /// The base's VALUE form, for the primitives that take a whole <see cref="CellStyle"/>. The
    /// foreground is deliberately dropped: it reaches those primitives as the BRUSH argument, which
    /// they sample per cell — resolving it here would pin one colour for the whole run, and a
    /// gradient base would paint flat.
    /// </summary>
    /// <remarks>
    /// The sample point is <c>(0, 0)</c> against an empty box because every channel left after the
    /// foreground is removed is position-independent by construction (see
    /// <see cref="ResolveLineBaseStyle"/>). A base that grew a position-dependent BACKGROUND would
    /// need the real bounds threaded in here — and the fact that the presenter's primitives take one
    /// background <em>colour</em>, not a brush, is what would surface it.
    /// </remarks>
    private static CellStyle ResolveLineBaseValue(in StyleDeltaTemplate lineBase)
        => (lineBase with { Foreground = null }).Resolve(0, 0, default).ApplyTo(CellStyle.Default);

    // Draws one run [from, to) of a single visual line at row localRow. Columns are line-local — glyphs hold the
    // line's per-cluster columns and lineStart is the line's model offset (0 for single-line).
    private void DrawLineRun(RenderContext context, in GraphemeLayout glyphs, int lineStart, string text, int from, int to,
                             int localRow, in StyleDeltaTemplate lineBase, bool selected, bool noColor,
                             IBrush? selectionBrush, bool inverse)
    {
        if (to <= from)
            return;

        var localColumn = glyphs.ColumnOf(from - lineStart) - _scrollColumn;
        var baseStyle = ResolveLineBaseValue(lineBase);

        if (EditingSource is { PaintsAsCells: false } source)
        {
            // A non-cell editor run paints per selection segment — the "fragment splits at
            // selection boundaries" model: [pre][selected][post] as separate pieces, the
            // selected piece's backdrop carrying the highlight.
            var runWidth = glyphs.ColumnOf(to - lineStart) - glyphs.ColumnOf(from - lineStart);
            var rect = new Rect(localColumn, localRow, Math.Max(0, runWidth), source.Metrics.LineRows);

            // ADD, never replace — backdrop starts as baseStyle (the folded attributes), not Default.
            // The selection delta then applies to the style the run WOULD have painted in, which is
            // what makes the toggle contrast: an inverse run's selection un-inverts, a plain run's
            // inverts. The old spelling toggled off `baseStyle` instead, so an inverse run's selection
            // came out identical to its surroundings.
            var backdrop = inverse ? baseStyle.AddAttributes(TextAttributes.Inverse) : baseStyle;

            if (selected && (!noColor || Owner?.IsFocused is true))
            {
                backdrop = ResolveSelectionStyle(noColor, selectionBrush, localColumn + runWidth / 2, localRow, rect)
                    .ApplyTo(backdrop);
            }

            var runText = text[from..to];

            // The piece paints through the SAME run pipeline the paragraph painter uses — a
            // one-run FormattedText whose Source dispatches it: a pure FIGlet source paints the
            // face DIRECTLY (never ScaledText's placeholder detour, whose re-formatting dropped
            // leading whitespace and whose realization cache swallowed the selection style); a
            // sized source rides the OSC 66 fragment. Glyph ink is sparse, so a selection
            // backdrop fills the rect first; the glyphs then paint over it in the same style.
            if (backdrop != baseStyle)
            {
                if (backdrop.Background is { IsDefault: false } bg)
                    context.FillOpaque(rect, bg, backdrop.Attributes);
                else
                    context.FillOpaque(rect, Color.Default, backdrop.Attributes);
            }

            var pieceRun = new FormattedTextRun(runText, backdrop, null) { Source = source };
            var pieceLine = new FormattedLine([pieceRun], runWidth, false, rect.Rows);
            var piecePara = new FormattedParagraph([pieceLine], new Size(runWidth, rect.Rows), TextAlignment.Left, false);
            var piece = new FormattedText([piecePara], piecePara.Size, runWidth);

            // The document carries no colour of its own, so the base's brush IS the run's foreground;
            // with no brush the document's own (unset) colours stand, which is the two-argument form.
            if (lineBase.Foreground is {} pieceBrush)
                context.DrawFormattedText(piece, rect, pieceBrush);
            else
                context.DrawFormattedText(piece, rect);

            return;
        }

        var span = text.AsSpan(from, to - from);

        // ADD, never replace: baseStyle already carries the presenter's folded attributes, so a
        // WithAttributes here would wipe every OTHER axis (Bold, Italic, …) the fold delivered.
        // This is the style the run paints in when it is NOT selected — and the base the selection
        // delta toggles against, so the highlight contrasts its surroundings either way round.
        var style = inverse ? baseStyle.AddAttributes(TextAttributes.Inverse) : baseStyle;

        if (selected && (!noColor || Owner?.IsFocused is true))
        {
            var runWidth = glyphs.ColumnOf(to - lineStart) - glyphs.ColumnOf(from - lineStart);
            var rect = new Rect(localColumn, localRow, Math.Max(0, runWidth), 1);
            var selection = ResolveSelectionStyle(noColor, selectionBrush, localColumn + runWidth / 2, localRow, rect);

            // The cell lane hands DrawText the BRUSH rather than the delta's sample, so a gradient
            // still colours per cell (DrawText overwrites CellStyle.Background from its brush argument
            // either way); the delta's Background says only WHETHER the rule found one usable.
            DrawText(context, localColumn, localRow, span, lineBase.Foreground,
                     selection.Background is null ? null : selectionBrush, selection.ApplyTo(style));
        }
        else
        {
            DrawText(context, localColumn, localRow, span, lineBase.Foreground, null, style);
        }
    }

    /// <summary>
    /// THE UNPACK for the cell lane: the brush channel of the line base in one argument, its value
    /// channels in the other, and <see cref="RenderContext.DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in CellStyle)"/>
    /// doing the per-cell sampling it already does.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Brushes.Default"/> is not a fallback colour.</b> It samples to
    /// <see cref="Color.Default"/> everywhere, which is exactly what the primitive's colour overload
    /// substitutes — the two calls are identical in the foreground. What it buys is the BACKGROUND:
    /// this used to branch two ways because the base was a value and the brush travelled beside it,
    /// and the no-brush leg called the colour overload with a hardcoded <c>null</c> background. Since
    /// <c>DrawText</c> writes its background brush over the base style's, that leg silently repainted
    /// a selected run's highlight as <see cref="Color.Transparent"/> — the fourth invisible selection
    /// in this file, and the only one that was a side effect of the parallel parameter rather than of
    /// the selection rule. One overload, one background, no leg to get wrong.
    /// </remarks>
    private static void DrawText(RenderContext context, int column, int row, ReadOnlySpan<char> text,
                                 IBrush? foreground, IBrush? background, in CellStyle style)
        => context.DrawText(column, row, text, foreground ?? Brushes.Default, background, style);

    private IBrush? ResolveBrush(string key)
        => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : null;
}
