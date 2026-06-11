using System.Diagnostics;
using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Text;

namespace Cursorial.UI;

/// <summary>
/// The <see cref="UIElement.Render"/> drawing surface: Cursorial.Drawing's vocabulary re-exposed in
/// <b>element-local</b> integer cell coordinates. Coordinate translation is performed by this type
/// at the call site (an origin add) — <em>not</em> via <c>DrawingContext.PushTranslate</c>, which
/// the v1 Drawing push stack does not apply to formatted text, content, pen strokes, shadows, or
/// titled boxes (the push-stack coverage gap, design doc §5.5 / terminal deviation ③). Every
/// forwarded call, including <see cref="DrawFormattedText(FormattedText, in Rect, IBrush)"/>,
/// <see cref="DrawContent"/>, stroke paths, and shadows, is translated uniformly here.
/// </summary>
/// <remarks>
/// <para>
/// <b>One instance is reused per zone raster</b>, its internal (origin, size) re-pointed per
/// element — do not capture it beyond the <see cref="UIElement.Render"/> call. Negotiated
/// <see cref="Capabilities"/> are supplied automatically to the text/content calls that need them.
/// </para>
/// <para>
/// <b>Figures.</b> The zone painter holds an <em>ambient per-element figure</em> open around each
/// <see cref="UIElement.Render"/> call, so pen strokes from different elements never junction-merge
/// (Drawing's no-nesting figure contract, discharged once here for every control author). A user
/// figure via <see cref="BeginFigure()"/> closes the ambient figure, opens the user figure, and —
/// on dispose of the returned scope — closes it and reopens a fresh ambient figure. Consequence:
/// strokes drawn before / inside / after a user figure form three separate junction groups. Scopes
/// do not nest.
/// </para>
/// <para>
/// There is deliberately no <c>PushClip</c>/<c>PushTranslate</c> surface: per-element clipping is a
/// render-boundary concern (<see cref="UIElement.ClipToBounds"/>); zone content hard-clips at the
/// scene's extent.
/// </para>
/// </remarks>
public sealed class RenderContext
{
    private DrawingContext? _inner;
    private UIElement? _boundary; // the zone boundary being rastered (diagnostic routing)
    private OutputCapabilities _capabilities = OutputCapabilities.None;
    private int _originColumn;
    private int _originRow;
    private int _bandShiftRow;
    private Size _size;
    private bool _userFigureActive;
    private int _userFigureToken;

    internal RenderContext()
    {
    }

    /// <summary>The element's arranged content size (its <c>Bounds.Size</c>).</summary>
    public Size Size => _size;

    /// <summary>The element-local bounds: <c>(0, 0, Size)</c>.</summary>
    public Rect Bounds => new(0, 0, _size.Columns, _size.Rows);

    /// <summary>The negotiated output capabilities — auto-supplied to the text/content calls below.</summary>
    public OutputCapabilities Capabilities => _capabilities;

    // ───────────────────────────── zone-raster lifecycle (RenderTree-internal) ─────────────────────────────

    /// <summary>
    /// Arms the context for one zone raster over a fresh <see cref="DrawingContext"/>.
    /// <paramref name="bandShiftRow"/> (≤ 0; <c>−bandStart</c> of a banded scroll zone, doc §5.7)
    /// is the row shift mapping content coordinates onto the band scene: the four push-stack-covered
    /// paths ride the <see cref="RenderTree"/>'s <c>PushTranslate</c> and stay untouched here; the
    /// uncovered paths (strokes, formatted text, content, shadows) fold it manually below and drop
    /// — with a DEBUG diagnostic — when they straddle the band's top edge (<c>K</c> sizes those
    /// edges outside the viewport clip).
    /// </summary>
    internal void Begin(DrawingContext inner, OutputCapabilities capabilities, int bandShiftRow = 0, UIElement? boundary = null)
    {
        _inner = inner;
        _capabilities = capabilities;
        _bandShiftRow = bandShiftRow;
        _boundary = boundary;
        _userFigureActive = false;
    }

    /// <summary>Disarms the context at the end of a zone raster (captured references throw thereafter).</summary>
    internal void End()
    {
        _inner = null;
        _bandShiftRow = 0;
        _boundary = null;
        _userFigureActive = false;
    }

    /// <summary>Re-points the origin/size at the element about to render (no per-element allocation).</summary>
    internal void PointAt(int originColumn, int originRow, Size size)
    {
        Debug.Assert(originColumn >= 0 && originRow >= 0,
            "Zone-raster origins are non-negative by construction at P1 (child Bounds are non-negative; " +
            "RenderOffset* never enters the raster — it promotes a boundary).");
        _originColumn = originColumn;
        _originRow = originRow;
        _size = size;
    }

    /// <summary>Opens the ambient per-element figure (called by the zone painter before <see cref="UIElement.Render"/>).</summary>
    internal void OpenAmbientFigure() => Inner.BeginFigure();

    /// <summary>Closes the ambient figure — and any user figure leaked without disposal — after <see cref="UIElement.Render"/>.</summary>
    internal void CloseAmbientFigure()
    {
        // A user scope the element failed to dispose: the ambient figure is already closed, so this
        // EndFigure closes the user figure instead; either way exactly one figure is open here.
        _userFigureActive = false;
        Inner.EndFigure();
    }

    private DrawingContext Inner
        => _inner ?? throw new InvalidOperationException(
            "This RenderContext is not active. It is valid only inside Render(...) during a zone raster " +
            "and must not be captured beyond that call.");

    private Rect Translate(in Rect rect)
        => new(
            Math.Min(rect.Column + _originColumn, LayoutMath.MaxExtent),
            Math.Min(rect.Row + _originRow, LayoutMath.MaxExtent),
            rect.Columns, rect.Rows);

    /// <summary>
    /// Translation for the push-stack-<b>uncovered</b> paths (strokes, formatted text, content,
    /// shadows), which fold the banded-zone row shift manually: returns <see langword="false"/>
    /// when the call lands fully above the band scene (silent skip) or straddles its top edge
    /// (dropped with a DEBUG diagnostic — the doc §5.7 pinned mechanism; <c>K</c> keeps those edges
    /// outside the visible viewport clip). Identity-fast when no band shift is active.
    /// </summary>
    private bool TryTranslateUncovered(in Rect rect, out Rect translated, string operation)
    {
        if (_bandShiftRow == 0)
        {
            translated = Translate(rect);
            return true;
        }

        var row = rect.Row + _originRow + _bandShiftRow;
        if (row < 0)
        {
            translated = default;
            if (row + rect.Rows > 0)
                EmitBandStraddleDiagnostic(operation);
            return false;
        }

        translated = new Rect(
            Math.Min(rect.Column + _originColumn, LayoutMath.MaxExtent),
            Math.Min(row, LayoutMath.MaxExtent),
            rect.Columns, rect.Rows);
        return true;
    }

    /// <summary>The line-endpoint sibling of <see cref="TryTranslateUncovered"/> (rows only — v1 bands the vertical axis).</summary>
    private bool TryTranslateLineRows(int y0, int y1, out int row0, out int row1, string operation)
    {
        row0 = y0 + _originRow + _bandShiftRow;
        row1 = y1 + _originRow + _bandShiftRow;
        if (_bandShiftRow == 0 || (row0 >= 0 && row1 >= 0))
            return true;

        if (row0 >= 0 || row1 >= 0)
            EmitBandStraddleDiagnostic(operation); // one endpoint above the band: drop (cannot be partially expressed)

        return false;
    }

    private void EmitBandStraddleDiagnostic(string operation)
        => LayoutDiagnostics.Emit(
            LayoutDiagnosticKind.BandStraddlingDrawDropped, _boundary,
            $"{operation} straddles the banded scroll scene's top edge and was dropped (doc §5.7 — " +
            "the band padding K keeps these edges outside the visible viewport clip).");

    // ───────────────────────────── cells and fills ─────────────────────────────

    /// <summary>Writes one cell at element-local (<paramref name="column"/>, <paramref name="row"/>).</summary>
    public void Set(int column, int row, string? grapheme, in Style style)
        => Inner.Set(column + _originColumn, row + _originRow, grapheme, in style);

    /// <summary>Background-only fill: lower layers' glyphs show through (a deliberate glyph-transparent scrim).</summary>
    public void FillRectangle(in Rect region, IBrush brush)
        => Inner.FillRectangle(Translate(region), brush);

    /// <inheritdoc cref="FillRectangle(in Rect, IBrush)"/>
    public void FillRectangle(in Rect region, Color color)
        => Inner.FillRectangle(Translate(region), color);

    /// <summary>
    /// Glyph-occluding fill — the opaque-surface path (<c>Panel.Background</c> uses this; design doc
    /// §5.5 pinned surface rule). A translucent brush still frosts, but glyphs beneath are hidden.
    /// Borders over an opaque fill need <c>overwrite: true</c> (the Drawing recipe).
    /// </summary>
    public void FillOpaque(in Rect region, IBrush brush)
        => Inner.FillOpaque(Translate(region), brush);

    /// <inheritdoc cref="FillOpaque(in Rect, IBrush)"/>
    public void FillOpaque(in Rect region, Color color)
        => Inner.FillOpaque(Translate(region), color);

    // ───────────────────────────── text and content ─────────────────────────────

    /// <summary>Draws one line of text at element-local coordinates; returns the columns written.</summary>
    public int DrawText(int column, int row, ReadOnlySpan<char> text,
                        IBrush foreground, IBrush? background = null, in Style baseStyle = default)
        => Inner.DrawText(column + _originColumn, row + _originRow, text, foreground, background, baseStyle);

    /// <inheritdoc cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in Style)"/>
    public int DrawText(int column, int row, ReadOnlySpan<char> text,
                        Color foreground, Color? background = null, in Style baseStyle = default)
        => Inner.DrawText(column + _originColumn, row + _originRow, text, foreground, background, baseStyle);

    /// <summary>Paints a laid-out document into element-local <paramref name="bounds"/>, brushed; capabilities auto-supplied.</summary>
    /// <remarks>
    /// <paramref name="brush"/> colors the cells that <b>inherited</b> the document foreground
    /// (unset, or equal to the document's default); a run's own explicit foreground (markup color)
    /// wins over the brush — the Drawing layer's document-brush contract. Use this overload when
    /// the element supplies the base color (themed text over a <see cref="FormattedText"/> reused
    /// across styles); use the two-argument overload when the document's own runs/markup carry all
    /// the color and nothing should be imposed from outside.
    /// </remarks>
    public void DrawFormattedText(FormattedText text, in Rect bounds, IBrush brush)
    {
        var inner = Inner;
        if (TryTranslateUncovered(bounds, out var translated, nameof(DrawFormattedText)))
            inner.DrawFormattedText(text, translated, brush, _capabilities);
    }

    /// <summary>Paints a laid-out document into element-local <paramref name="bounds"/>; capabilities auto-supplied.</summary>
    /// <remarks>
    /// Renders with the document's <b>own</b> colors (markup spans, run styles) only. See the
    /// brushed overload's remarks for when to supply an external brush instead.
    /// </remarks>
    public void DrawFormattedText(FormattedText text, in Rect bounds)
    {
        var inner = Inner;
        if (TryTranslateUncovered(bounds, out var translated, nameof(DrawFormattedText)))
            inner.DrawFormattedText(text, translated, _capabilities);
    }

    /// <summary>Paints embedded content (images, sized text) into element-local <paramref name="bounds"/>; capabilities auto-supplied.</summary>
    public void DrawContent(in Rect bounds, IContent content)
    {
        var inner = Inner;
        if (TryTranslateUncovered(bounds, out var translated, nameof(DrawContent)))
            inner.DrawContent(translated, content, _capabilities);
    }

    // ───────────────────────────── strokes, boxes, panels, shadows ─────────────────────────────

    /// <summary>Strokes a line between element-local endpoints (axis-aligned → box glyphs; diagonal → braille).</summary>
    public void DrawLine(int x0, int y0, int x1, int y1, in Pen pen, bool overwrite = false)
    {
        var inner = Inner;
        if (TryTranslateLineRows(y0, y1, out var row0, out var row1, nameof(DrawLine)))
            inner.DrawLine(x0 + _originColumn, row0, x1 + _originColumn, row1, pen, overwrite);
    }

    /// <inheritdoc cref="DrawLine(int, int, int, int, in Pen, bool)"/>
    public void DrawLine(int x0, int y0, int x1, int y1, Color color, bool overwrite = false)
    {
        var inner = Inner;
        if (TryTranslateLineRows(y0, y1, out var row0, out var row1, nameof(DrawLine)))
            inner.DrawLine(x0 + _originColumn, row0, x1 + _originColumn, row1, color, overwrite);
    }

    /// <summary>Strokes the outline of an element-local <paramref name="rect"/>.</summary>
    public void DrawBox(in Rect rect, in Pen pen, bool overwrite = false)
    {
        var inner = Inner;
        if (TryTranslateUncovered(rect, out var translated, nameof(DrawBox)))
            inner.DrawBox(translated, pen, overwrite);
    }

    /// <inheritdoc cref="DrawBox(in Rect, in Pen, bool)"/>
    public void DrawBox(in Rect rect, Color color, bool overwrite = false)
    {
        var inner = Inner;
        if (TryTranslateUncovered(rect, out var translated, nameof(DrawBox)))
            inner.DrawBox(translated, color, overwrite);
    }

    /// <summary>Strokes an outline with an optional background-only fill.</summary>
    public void DrawRectangle(in Rect rect, in Pen pen, IBrush? fill = null, bool overwrite = false)
    {
        var inner = Inner;
        if (TryTranslateUncovered(rect, out var translated, nameof(DrawRectangle)))
            inner.DrawRectangle(translated, pen, fill, overwrite);
    }

    /// <summary>Strokes a titled box outline.</summary>
    public void DrawTitledBox(in Rect rect, in PanelTitle title, in Pen pen, bool overwrite = false)
    {
        var inner = Inner;
        if (TryTranslateUncovered(rect, out var translated, nameof(DrawTitledBox)))
            inner.DrawTitledBox(translated, title, pen, overwrite);
    }

    /// <summary>
    /// Fill + titled border in one call. The fill is Drawing's <b>background-only</b>
    /// <c>FillRectangle</c> — for an opaque surface use <see cref="FillOpaque(in Rect, IBrush)"/>
    /// followed by <see cref="DrawTitledBox"/> with <c>overwrite: true</c> (the <c>Panel.Background</c>
    /// path does the opaque fill for you).
    /// </summary>
    public void DrawPanel(in Rect rect, in Pen pen, IBrush? fill = null, PanelTitle title = default, bool overwrite = false)
    {
        var inner = Inner;
        if (TryTranslateUncovered(rect, out var translated, nameof(DrawPanel)))
            inner.DrawPanel(translated, pen, fill, title, overwrite);
    }

    /// <summary>
    /// Paints a drop shadow cast by the element-local <paramref name="element"/> rect. Shadows paint
    /// <em>outside</em> the rect — a render boundary cannot paint its own (it would fall outside its
    /// scene); boundary-level shadows are the parent zone's job (design doc §5.5).
    /// </summary>
    public void DrawDropShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor)
    {
        var inner = Inner;
        if (TryTranslateUncovered(element, out var translated, nameof(DrawDropShadow)))
            inner.DrawDropShadow(translated, geometry, shadowColor);
    }

    /// <summary>Paints an inner shadow inside the element-local <paramref name="element"/> rect.</summary>
    public void DrawInnerShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor)
    {
        var inner = Inner;
        if (TryTranslateUncovered(element, out var translated, nameof(DrawInnerShadow)))
            inner.DrawInnerShadow(translated, geometry, shadowColor);
    }

    // ───────────────────────────── user figures ─────────────────────────────

    /// <summary>
    /// Begins a user figure (junction grouping + pen-gradient bounds union over the figure's own
    /// strokes). Closes the ambient per-element figure; disposing the returned scope closes the user
    /// figure and reopens a fresh ambient one — strokes before / inside / after form three separate
    /// junction groups. One user figure at a time (scopes do not nest).
    /// </summary>
    /// <exception cref="InvalidOperationException">A user figure is already open.</exception>
    public RenderFigureScope BeginFigure() => BeginFigureCore(null);

    /// <summary>Begins a user figure with explicit element-local brush bounds (see <see cref="BeginFigure()"/>).</summary>
    /// <exception cref="InvalidOperationException">A user figure is already open.</exception>
    public RenderFigureScope BeginFigure(in Rect bounds) => BeginFigureCore(TranslateFigureBounds(bounds));

    // Figure bounds are pen-gradient metadata, not a draw: under a band shift a straddling rect is
    // clamped to the scene's top edge (slightly compressed gradient sampling at a band edge — the
    // doc §5.7 "may clip imperfectly" allowance) rather than dropped.
    private Rect TranslateFigureBounds(in Rect bounds)
    {
        if (_bandShiftRow == 0)
            return Translate(bounds);

        var row = bounds.Row + _originRow + _bandShiftRow;
        var rows = bounds.Rows;
        if (row < 0)
        {
            rows = Math.Max(0, rows + row);
            row = 0;
        }

        return new Rect(
            Math.Min(bounds.Column + _originColumn, LayoutMath.MaxExtent),
            Math.Min(row, LayoutMath.MaxExtent),
            bounds.Columns, rows);
    }

    private RenderFigureScope BeginFigureCore(Rect? bounds)
    {
        var inner = Inner;
        if (_userFigureActive)
            throw new InvalidOperationException("Figure scopes do not nest; dispose the current scope before beginning another.");

        inner.EndFigure();          // close the ambient per-element figure
        if (bounds is { } b)
            inner.BeginFigure(b);   // open the user figure (closed via EndUserFigure, not its own scope)
        else
            inner.BeginFigure();

        _userFigureActive = true;
        return new RenderFigureScope(this, ++_userFigureToken);
    }

    /// <summary>Closes the user figure identified by <paramref name="token"/> and reopens the ambient figure (double-dispose safe).</summary>
    internal void EndUserFigure(int token)
    {
        if (_inner is not { } inner || !_userFigureActive || token != _userFigureToken)
            return;

        _userFigureActive = false;
        inner.EndFigure();      // close the user figure
        inner.BeginFigure();    // reopen a fresh ambient per-element figure
    }
}

/// <summary>
/// The disposable scope returned by <see cref="RenderContext.BeginFigure()"/>: dispose (normally via
/// <c>using</c>) to close the user figure and reopen the ambient per-element figure. Carries a token,
/// so a double dispose (or a copied-then-disposed value) is a safe no-op.
/// </summary>
public readonly struct RenderFigureScope : IDisposable
{
    private readonly RenderContext? _context;
    private readonly int _token;

    internal RenderFigureScope(RenderContext context, int token)
    {
        _context = context;
        _token = token;
    }

    /// <summary>Closes the user figure and reopens the ambient figure (no-op if already closed).</summary>
    public void Dispose() => _context?.EndUserFigure(_token);
}
