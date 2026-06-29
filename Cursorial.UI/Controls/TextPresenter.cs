using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI.Themes;

using CellStyle = Cursorial.Output.Style;

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

    /// <summary>The current horizontal scroll offset, in display columns (test observability).</summary>
    public int ScrollOffset => _scrollColumn;

    /// <summary>The current vertical scroll offset, in visual lines — 0 for a single-line field (test observability).</summary>
    public int ScrollRow => _scrollRow;

    private TextBox? Owner => TemplatedParent as TextBox;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = Owner;
        if (owner is null)
            return new Size(1, 1);

        // Single line: desire the displayed text's width plus one column for the end caret; the field stretches
        // when given more and scrolls when given less.
        if (!owner.IsMultiLine)
            return new Size(GraphemeWidth.StringWidth(owner.DisplayText) + 1, 1);

        // Multi-line: lay out against the available width (an Unbounded width naturally disables wrapping, since
        // no line exceeds it), then reserve clamp(lineCount, MinLines, MaxLines) rows.
        var wrap = owner.TextWrapping != WrapMode.NoWrap;
        var wrapWidth = wrap ? Math.Max(1, availableSize.Columns) : 0;
        var layout = TextLayout.Build(owner.DisplayText, wrapWidth, wrap);
        var rows = ClampRows(layout.LineCount, owner.MinLines, owner.MaxLines);
        var width = wrap && !LayoutMath.IsUnbounded(availableSize.Columns)
            ? Math.Max(1, availableSize.Columns) // wrap fills the width
            : layout.MaxWidth + 1;               // NoWrap (or unknown width) desires the widest line + the end caret
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
        var layout = GraphemeLayout.Build(owner.DisplayText);
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
        var layout = TextLayout.Build(owner.DisplayText, wrapWidth, wrap);
        var (caretRow, caretColumn) = layout.Locate(owner.ToDisplayIndex(owner.CaretIndex));

        // Vertical scroll: keep the caret's visual line in view.
        if (_viewportRows > 0)
        {
            if (caretRow < _scrollRow)
                _scrollRow = caretRow;
            else if (caretRow >= _scrollRow + _viewportRows)
                _scrollRow = caretRow - _viewportRows + 1;

            var maxRow = Math.Max(0, layout.LineCount - _viewportRows);
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

    private void PublishCaret(int localColumn, int localRow)
    {
        if (UIApplication.Current?.CaretService is not { } service)
            return;

        // Publish only while the owning TextBox holds physical focus and the caret's row is within the viewport
        // (a scrolled-out caret row is hidden, mirroring the column clip). The caret service also clip-gates the
        // publication and drops it on detach.
        var visible = IsAttachedToTree && Owner is { IsFocused: true }
            && localRow >= 0 && localRow < Math.Max(1, _viewportRows);
        if (visible)
            service.Publish(this, localColumn, localRow, CursorShape.BlinkingBar);
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

        var foreground = owner.Foreground ?? ResolveBrush(ThemeKeys.TextBrush);
        var text = owner.DisplayText ?? string.Empty;

        if (text.Length == 0)
        {
            if (owner.Placeholder is { Length: > 0 } placeholder)
            {
                // MutedBrush carries the placeholder color on color tiers; Faint carries the de-emphasis on the
                // NoColor tier where MutedBrush resolves to Default (adoption-spec §5: placeholder → Faint).
                var muted = ResolveBrush(ThemeKeys.MutedBrush) ?? foreground;
                DrawText(context, 0, 0, placeholder, muted, null, CellStyle.Default.WithAttributes(TextAttributes.Faint));
            }

            return;
        }

        var noColor = context.Capabilities.Color.Depth == ColorDepth.NoColor;
        var selectionBrush = owner.SelectionBrush ?? ResolveBrush(owner.IsFocused ? ThemeKeys.SelectionBrush : ThemeKeys.SelectionInactiveBrush);
        // SelectionBounds are MODEL offsets — project them into the displayed text (identity for a TextBox).
        var (modelSelectionStart, modelSelectionEnd) = owner.SelectionBounds;
        var selectionStart = owner.ToDisplayIndex(modelSelectionStart);
        var selectionEnd = owner.ToDisplayIndex(modelSelectionEnd);

        if (!owner.IsMultiLine)
        {
            RenderSingleLine(context, text, viewportColumns, foreground, noColor, selectionBrush, selectionStart, selectionEnd);
            return;
        }

        var wrap = owner.TextWrapping != WrapMode.NoWrap;
        var wrapWidth = wrap ? Math.Max(1, viewportColumns) : 0;
        var layout = TextLayout.Build(text, wrapWidth, wrap);

        var lastRow = Math.Min(layout.LineCount, _scrollRow + viewportRows);
        for (var row = _scrollRow; row < lastRow; row++)
        {
            var localRow = row - _scrollRow;
            var lineStart = layout.LineContentStart(row);
            var lineEnd = layout.LineContentEnd(row);
            var glyphs = layout.LineGlyphs(row);

            // Visible char window within this line (NoWrap horizontal scroll; wrap keeps _scrollColumn 0). The
            // window is line-local (glyph columns), projected back to model offsets and clamped to the line.
            var firstChar = Math.Clamp(lineStart + glyphs.CharIndexAtOrBeforeColumn(_scrollColumn), lineStart, lineEnd);
            var lastChar = Math.Clamp(lineStart + glyphs.CharIndexAtOrAfterColumn(_scrollColumn + viewportColumns), lineStart, lineEnd);

            var selFrom = Math.Clamp(selectionStart, firstChar, lastChar);
            var selTo = Math.Clamp(selectionEnd, firstChar, lastChar);

            DrawLineRun(context, glyphs, lineStart, text, firstChar, selFrom, localRow, foreground, selected: false, noColor, selectionBrush);
            DrawLineRun(context, glyphs, lineStart, text, selFrom, selTo, localRow, foreground, selected: true, noColor, selectionBrush);
            DrawLineRun(context, glyphs, lineStart, text, selTo, lastChar, localRow, foreground, selected: false, noColor, selectionBrush);
        }
    }

    private void RenderSingleLine(RenderContext context, string text, int viewport, IBrush? foreground, bool noColor,
                                  IBrush? selectionBrush, int selectionStart, int selectionEnd)
    {
        var layout = GraphemeLayout.Build(text);
        // The visible char window covers the viewport — boundary at/before the left edge through the boundary
        // at/after the right edge; the boundary clip absorbs the straddle on each side.
        var firstChar = layout.CharIndexAtOrBeforeColumn(_scrollColumn);
        var lastChar = layout.CharIndexAtOrAfterColumn(_scrollColumn + viewport);

        // Up to three runs — pre-selection, selection, post-selection — one DrawText call each.
        var selFrom = Math.Clamp(selectionStart, firstChar, lastChar);
        var selTo = Math.Clamp(selectionEnd, firstChar, lastChar);

        DrawLineRun(context, layout, 0, text, firstChar, selFrom, 0, foreground, selected: false, noColor, selectionBrush);
        DrawLineRun(context, layout, 0, text, selFrom, selTo, 0, foreground, selected: true, noColor, selectionBrush);
        DrawLineRun(context, layout, 0, text, selTo, lastChar, 0, foreground, selected: false, noColor, selectionBrush);
    }

    // Draws one run [from, to) of a single visual line at row localRow. Columns are line-local — glyphs hold the
    // line's per-cluster columns and lineStart is the line's model offset (0 for single-line).
    private void DrawLineRun(RenderContext context, in GraphemeLayout glyphs, int lineStart, string text, int from, int to,
                             int localRow, IBrush? foreground, bool selected, bool noColor, IBrush? selectionBrush)
    {
        if (to <= from)
            return;

        var localColumn = glyphs.ColumnOf(from - lineStart) - _scrollColumn;
        var span = text.AsSpan(from, to - from);

        if (selected)
        {
            if (noColor)
                DrawText(context, localColumn, localRow, span, foreground, null, CellStyle.Default.WithAttributes(TextAttributes.Inverse));
            else
                DrawText(context, localColumn, localRow, span, foreground, selectionBrush, CellStyle.Default);
        }
        else
        {
            DrawText(context, localColumn, localRow, span, foreground, null, CellStyle.Default);
        }
    }

    private static void DrawText(RenderContext context, int column, int row, ReadOnlySpan<char> text,
                                 IBrush? foreground, IBrush? background, in CellStyle style)
    {
        if (foreground is { } brush)
            context.DrawText(column, row, text, brush, background, style);
        else
            context.DrawText(column, row, text, Color.Default, null, style);
    }

    private IBrush? ResolveBrush(string key)
        => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : null;
}
