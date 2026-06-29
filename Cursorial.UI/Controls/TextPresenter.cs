using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Themes;

using CellStyle = Cursorial.Output.Style;

// ReSharper disable NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract

namespace Cursorial.UI.Controls;

/// <summary>
/// Renders the single line of its <see cref="UIElement.TemplatedParent"/> <see cref="TextBox"/> (the
/// <c>PART_TextPresenter</c>): the text or the <see cref="TextBox.Placeholder"/>, the selection highlight,
/// and the <b>real terminal caret</b> via S1's <see cref="ITerminalCaretService"/> (design doc §5.9 /
/// §3.9-TextBox). Owns the horizontal scroll offset (display columns) that keeps the caret in view.
/// <para>
/// A <b>clipped render boundary</b> (<see cref="UIElement.ClipToBounds"/>): a wide cluster straddling
/// either viewport edge is clipped per cell (no bleed into the field chrome), the offset change re-rasters
/// only this one-row zone (not the window scene), and a scrolled-out caret is dropped by the caret
/// service's zone-clip gate. Clears its caret publication on detach (belt-and-braces with the service's
/// stale-owner drop).
/// </para>
/// </summary>
public sealed class TextPresenter : UIElement
{
    private int _scrollOffset;    // horizontal scroll, in display columns
    private int _viewportColumns; // the last arranged width

    /// <summary>Creates the presenter as a clipped render boundary (the horizontal-scroll clip).</summary>
    public TextPresenter()
    {
        ClipToBounds = true;
    }

    /// <summary>The current horizontal scroll offset, in display columns (test observability).</summary>
    public int ScrollOffset => _scrollOffset;

    private TextBox? Owner => TemplatedParent as TextBox;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Desire the displayed text's width plus one column for the end caret; the field stretches when given
        // more and scrolls when given less (the arrange clamps; RefreshCaretAndScroll re-anchors the offset).
        var text = Owner?.DisplayText ?? string.Empty;
        return new Size(GraphemeWidth.StringWidth(text) + 1, 1);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewportColumns = finalSize.Columns;
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
    /// Re-anchors the scroll offset so the caret stays in view, then (re)publishes or clears the terminal
    /// caret. Called from arrange (viewport known) and from the owner on every model / focus change. The
    /// caller invalidates visual when content changed; this method only touches scroll + caret metadata.
    /// </summary>
    internal void RefreshCaretAndScroll()
    {
        var owner = Owner;
        if (owner is null)
            return;

        var layout = GraphemeLayout.Build(owner.DisplayText);
        var caretColumn = layout.ColumnOf(owner.ToDisplayIndex(owner.CaretIndex));
        var viewport = _viewportColumns;

        if (viewport > 0)
        {
            var slack = viewport > 4 ? 2 : 0; // keep a little context around the caret (spec: 2-column edge slack)
            if (caretColumn - slack < _scrollOffset)
                _scrollOffset = Math.Max(0, caretColumn - slack);
            else if (caretColumn + slack >= _scrollOffset + viewport)
                _scrollOffset = caretColumn + slack - viewport + 1;

            var maxScroll = Math.Max(0, layout.TotalColumns + 1 - viewport); // +1: room for the end caret
            _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
        }
        else
        {
            // No visible area (collapsed/zero-width) — reset rather than keep a stale offset, so the
            // published caret column is never negative (the zone clip would hide it anyway, but the
            // element-local column must stay a valid coordinate).
            _scrollOffset = 0;
        }

        PublishCaret(caretColumn - _scrollOffset);
    }

    private void PublishCaret(int localColumn)
    {
        if (UIApplication.Current?.CaretService is not { } service)
            return;

        // Publish only while the owning TextBox holds physical focus — only the active window's editor is
        // the application's focused element (§3.9-TextBox), so IsFocused subsumes the window-active gate.
        // The caret service clip-gates the publication (scrolled-out ⇒ hidden) and drops it on detach.
        if (IsAttachedToTree && Owner is { IsFocused: true })
            service.Publish(this, localColumn, 0, CursorShape.BlinkingBar);
        else
            service.Clear(this);
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var owner = Owner;
        if (owner is null)
            return;

        var viewport = context.Size.Columns;
        if (viewport <= 0 || context.Size.Rows <= 0)
            return;

        var foreground = owner.Foreground ?? ResolveBrush(ThemeKeys.TextBrush);
        var text = owner.DisplayText ?? string.Empty;

        if (text.Length == 0)
        {
            if (owner.Placeholder is { Length: > 0 } placeholder)
            {
                // MutedBrush carries the placeholder color on color tiers; Faint carries the de-emphasis on
                // the NoColor tier where MutedBrush resolves to Default (adoption-spec §5: placeholder → Faint).
                var muted = ResolveBrush(ThemeKeys.MutedBrush) ?? foreground;
                DrawText(context, 0, placeholder, muted, null, CellStyle.Default.WithAttributes(TextAttributes.Faint));
            }

            return;
        }

        var layout = GraphemeLayout.Build(text);
        // SelectionBounds are MODEL offsets — project them into the displayed text (identity for a TextBox).
        var (modelSelectionStart, modelSelectionEnd) = owner.SelectionBounds;
        var selectionStart = owner.ToDisplayIndex(modelSelectionStart);
        var selectionEnd = owner.ToDisplayIndex(modelSelectionEnd);

        // The visible char window covers the viewport — boundary at/before the left edge through the
        // boundary at/after the right edge; the boundary clip absorbs the straddle on each side.
        var firstChar = layout.CharIndexAtOrBeforeColumn(_scrollOffset);
        var lastChar = layout.CharIndexAtOrAfterColumn(_scrollOffset + viewport);

        var noColor = context.Capabilities.Color.Depth == ColorDepth.NoColor;
        var selectionBrush = owner.SelectionBrush ?? ResolveBrush(owner.IsFocused ? ThemeKeys.SelectionBrush : ThemeKeys.SelectionInactiveBrush);

        // Up to three runs — pre-selection, selection, post-selection — one DrawText call each.
        var selFrom = Math.Clamp(selectionStart, firstChar, lastChar);
        var selTo = Math.Clamp(selectionEnd, firstChar, lastChar);

        DrawRun(context, layout, text, firstChar, selFrom, foreground, selected: false, noColor, selectionBrush);
        DrawRun(context, layout, text, selFrom, selTo, foreground, selected: true, noColor, selectionBrush);
        DrawRun(context, layout, text, selTo, lastChar, foreground, selected: false, noColor, selectionBrush);
    }

    private void DrawRun(RenderContext context, in GraphemeLayout layout, string text, int from, int to,
                         IBrush? foreground, bool selected, bool noColor, IBrush? selectionBrush)
    {
        if (to <= from)
            return;

        var localColumn = layout.ColumnOf(from) - _scrollOffset;
        var span = text.AsSpan(from, to - from);

        if (selected)
        {
            if (noColor)
                DrawText(context, localColumn, span, foreground, null, CellStyle.Default.WithAttributes(TextAttributes.Inverse));
            else
                DrawText(context, localColumn, span, foreground, selectionBrush, CellStyle.Default);
        }
        else
        {
            DrawText(context, localColumn, span, foreground, null, CellStyle.Default);
        }
    }

    private static void DrawText(RenderContext context, int column, ReadOnlySpan<char> text,
                                 IBrush? foreground, IBrush? background, in CellStyle style)
    {
        if (foreground is { } brush)
            context.DrawText(column, 0, text, brush, background, style);
        else
            context.DrawText(column, 0, text, Color.Default, null, style);
    }

    private IBrush? ResolveBrush(string key)
        => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : null;
}
