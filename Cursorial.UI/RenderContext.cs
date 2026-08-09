using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.UI;

/// <summary>
/// The <see cref="UIElement.Render"/> drawing surface: Cursorial.Drawing's vocabulary re-exposed in
/// <b>element-local</b> integer cell coordinates. This type is a thin veneer over
/// <see cref="DrawingContext"/> — it performs no coordinate arithmetic of its own. Each element
/// render runs under <b>one</b> pushed translate scope (the element's zone-local origin) on Drawing's
/// clip/translate state stack, composing with the <see cref="RenderTree"/>'s ambient banded-zone
/// scope, so every forwarded call — cells, fills, text, content, strokes, shadows, panels — maps and
/// clips per cell through one mechanism (the P2.5 ① push-stack full-coverage guarantee).
/// </summary>
/// <remarks>
/// <para>
/// <b>One instance is reused per zone raster</b>, its per-element scope re-pushed per element — do
/// not capture it beyond the <see cref="UIElement.Render"/> call. Negotiated
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
    private OutputCapabilities _capabilities = OutputCapabilities.None;
    private DrawingStateScope _elementScope; // the one per-element push (origin translate)
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
    /// Arms the context for one zone raster over a fresh <see cref="DrawingContext"/>. A banded
    /// scroll zone's row shift (doc §5.7) is the <see cref="RenderTree"/>'s ambient
    /// <c>PushTranslate</c> — already on the stack when the per-element scopes push on top of it.
    /// </summary>
    internal void Begin(DrawingContext inner, OutputCapabilities capabilities)
    {
        _inner = inner;
        _capabilities = capabilities;
        _elementScope = default;
        _userFigureActive = false;
    }

    /// <summary>Disarms the context at the end of a zone raster (captured references throw thereafter).</summary>
    internal void End()
    {
        _elementScope.Dispose(); // pop the last element's scope (no-op when none was pushed)
        _elementScope = default;
        _inner = null;
        _userFigureActive = false;
    }

    /// <summary>
    /// Re-points the context at the element about to render: pops the previous element's scope and
    /// pushes one translate scope at the element's zone-local origin (no per-element allocation —
    /// the scope is a struct over Drawing's state stack).
    /// </summary>
    internal void PointAt(int originColumn, int originRow, Size size)
    {
        // Origins may be NEGATIVE since the P2.6 signed-margin batch (matrix LD19): a child with a
        // negative margin arranges above/left of its parent, and Drawing's push-stack translate is
        // negative-capable — cells above/left of the zone clip per cell (the P2.5 ① coverage).
        // (RenderOffset* still never enters the raster — it promotes a boundary.)
        //
        // Pop-then-push, in that order. If PushTranslate ever threw between the two, the stale
        // _elementScope's later double-dispose in End() is a safe no-op (DrawingContext.PopTo is
        // depth-gated and idempotent). Do NOT "fix" this by pushing first and disposing after: the
        // new push would compose onto the un-popped previous translate, and the old scope's pop —
        // same depth token as the new scope's — would then remove both.
        _elementScope.Dispose();
        _elementScope = Inner.PushTranslate(originColumn, originRow);
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

    // ───────────────────────────── cells and fills ─────────────────────────────

    /// <summary>Writes one cell at element-local (<paramref name="column"/>, <paramref name="row"/>).</summary>
    public void Set(int column, int row, string? grapheme, in CellStyle style)
        => Inner.Set(column, row, grapheme, in style);

    /// <summary>Blits the contents of <paramref name="view"/> into the back buffer at <paramref name="region"/>.</summary>
    public void Blit(CellBufferView view, in Rect region)
        => Inner.Blit(view, region);

    /// <inheritdoc cref="DrawingContext.FillRectangle(in Rect, in BrushedStyle)"/>
    public void FillRectangle(in Rect region, in BrushedStyle style)
        => Inner.FillRectangle(region, style);

    /// <summary>Background-only fill: lower layers' glyphs show through (a deliberate glyph-transparent scrim).</summary>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public void FillRectangle(in Rect region, IBrush brush)
        => Inner.FillRectangle(region, brush);

    /// <inheritdoc cref="FillRectangle(in Rect, IBrush)"/>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public void FillRectangle(in Rect region, Color color)
        => Inner.FillRectangle(region, color);

    /// <inheritdoc cref="DrawingContext.FillOpaque(in Rect, in BrushedStyle, bool)"/>
    /// <remarks>
    /// <b><paramref name="overwrite"/> defaults to <see langword="false"/> here</b>, narrower than the
    /// drawing layer's <see langword="true"/> — matching the <see cref="Color"/> overload beside it, the
    /// newest of this veneer's fills and the one an element actually reaches for. The narrower default is
    /// load-bearing: <c>TextPresenter</c>'s inverse band fill paints OVER a glyph face, and
    /// <see cref="CellBuffer.Set"/> rescues the ink underneath only on the non-overwriting path.
    /// (The <see cref="IBrush"/> overloads below still default to <see langword="true"/> — an
    /// inconsistency older than this overload, preserved rather than silently changed.)
    /// </remarks>
    public void FillOpaque(in Rect region, in BrushedStyle style, bool overwrite = true)
        => Inner.FillOpaque(region, style, overwrite);

    /// <inheritdoc cref="FillOpaque(in Rect, in BrushedStyle, bool)"/>
    /// <param name="region">The rectangle to fill, in element-local coordinates.</param>
    /// <param name="style">The per-cell delta every occluder cell takes.</param>
    /// <param name="brushBounds">The sampling region for <paramref name="style"/>'s brushes.</param>
    /// <param name="overwrite">Whether to overwrite existing non-whitespace content. Default is <c>false</c>.</param>
    public void FillOpaque(in Rect region, in BrushedStyle style, in Rect brushBounds, bool overwrite = true)
        => Inner.FillOpaque(region, style, brushBounds, overwrite);

    /// <inheritdoc cref="DrawingContext.FillOpaque(in Rect, Color, TextAttributes, bool)"/>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public void FillOpaque(in Rect region, Color color, TextAttributes attributes = default, bool overwrite = true)
        => Inner.FillOpaque(region, color, attributes, overwrite);

    /// <inheritdoc cref="DrawingContext.FillOpaque(in Rect, IBrush, TextAttributes, bool)"/>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public void FillOpaque(in Rect region, IBrush brush, TextAttributes attributes = default, bool overwrite = true)
        => Inner.FillOpaque(region, brush, attributes, overwrite);

    /// <inheritdoc cref="DrawingContext.FillOpaque(in Rect, IBrush, in Rect, TextAttributes, bool)"/>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public void FillOpaque(in Rect region, IBrush brush, in Rect brushBounds, TextAttributes attributes = default, bool overwrite = true)
        => Inner.FillOpaque(region, brush, brushBounds, attributes, overwrite);

    /// <inheritdoc cref="DrawingContext.PaintRectangle(in Rect, in BrushedStyle, bool)"/>
    public void PaintRectangle(in Rect region, in BrushedStyle style, bool overwrite = false)
        => Inner.PaintRectangle(region, style, overwrite);

    /// <inheritdoc cref="DrawingContext.PaintRectangle(in Rect, in BrushedStyle, in Rect, bool)"/>
    public void PaintRectangle(in Rect region, in BrushedStyle style, in Rect brushBounds, bool overwrite = false)
        => Inner.PaintRectangle(region, style, brushBounds, overwrite);

    /// <inheritdoc cref="DrawingContext.PaintRectangle(in Rect, Color, TextAttributes, bool)"/>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public void PaintRectangle(in Rect region, Color color, TextAttributes attributes = default, bool overwrite = false)
        => Inner.PaintRectangle(region, color, attributes, overwrite);

    /// <inheritdoc cref="DrawingContext.PaintRectangle(in Rect, IBrush, TextAttributes, bool)"/>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public void PaintRectangle(in Rect region, IBrush brush, TextAttributes attributes = default, bool overwrite = false)
        => Inner.PaintRectangle(region, brush, region, attributes, overwrite);

    /// <inheritdoc cref="DrawingContext.PaintRectangle(in Rect, IBrush, in Rect, TextAttributes, bool)"/>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public void PaintRectangle(in Rect region, IBrush brush, in Rect brushBounds, TextAttributes attributes = default, bool overwrite = false)
        => Inner.PaintRectangle(region, brush, brushBounds, attributes, overwrite);

    // ───────────────────────────── text and content ─────────────────────────────

    /// <summary>
    /// Draws text at element-local coordinates. <c>\r\n</c> | <c>\n</c> | <c>\r</c> are line
    /// breaks — each subsequent line continues at the original start <paramref name="column"/> one
    /// row down, the brush sampling the full multi-line extent; a tab becomes one space and other
    /// C0/C1 controls are skipped (DEBUG diagnostics via <c>DrawingDiagnostics</c>). Returns the
    /// text's bounding box: widest line's advance × line count — per line the full local width
    /// (clusters a band/zone edge clips away still advance the count).
    /// </summary>
    /// <remarks>
    /// <paramref name="baseStyle"/> is a per-cell DELTA over <paramref name="legacyBaseStyle"/> — attributes,
    /// underline, hyperlink and blending mode included, not just the two colour channels the brush
    /// overload can carry. An absent <c>Background</c> here is <b>no opinion</b> (the base's survives);
    /// on the brush overload an omitted background is <see cref="Brushes.Transparent"/>. See
    /// <see cref="DrawingContext.DrawText(int, int, ReadOnlySpan{char}, in BrushedStyle, in CellStyle)"/>.
    /// </remarks>
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         in BrushedStyle baseStyle, in CellStyle legacyBaseStyle = default)
        => Inner.DrawText(column, row, text, baseStyle, legacyBaseStyle);

    /// <inheritdoc cref="DrawingContext.DrawText(int, int, ReadOnlySpan{char}, in BrushedStyle, in Rect, in CellStyle)"/>
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         in BrushedStyle baseStyle, in Rect sampleBounds, in CellStyle legacyBaseStyle = default)
        => Inner.DrawText(column, row, text, baseStyle, sampleBounds, legacyBaseStyle);

    /// <inheritdoc cref="DrawText(int, int, ReadOnlySpan{char}, in BrushedStyle, in CellStyle)"/>
    /// <remarks>An omitted <paramref name="background"/> is <see cref="Brushes.Transparent"/>, which
    /// overwrites <paramref name="legacyBaseStyle"/>'s background rather than deferring to it.</remarks>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         IBrush foreground, IBrush? background, in CellStyle legacyBaseStyle = default)
        => Inner.DrawText(column, row, text, foreground, background, legacyBaseStyle);

    /// <inheritdoc cref="DrawText(int, int, ReadOnlySpan{char}, in BrushedStyle, in CellStyle)"/>
    /// <remarks>An omitted <paramref name="background"/> is <see cref="Brushes.Transparent"/>.</remarks>
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         IBrush foreground, IBrush? background = null)
        => Inner.DrawText(column, row, text,
                          new BrushedStyle
                          {
                              Foreground = foreground,
                              Background = background ?? Brushes.Transparent
                          });

    /// <inheritdoc cref="DrawText(int, int, ReadOnlySpan{char}, IBrush, IBrush?, in CellStyle)"/>
    [Obsolete("Use the BrushedStyle overload instead.", DiagnosticId = "CUR0001")]
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         Color foreground, Color? background = null, in CellStyle legacyBaseStyle = default)
        => Inner.DrawText(column, row, text, foreground, background, legacyBaseStyle);

    /// <summary>Paints a laid-out document into element-local <paramref name="bounds"/>, brushed; capabilities auto-supplied.</summary>
    /// <remarks>
    /// <paramref name="brush"/> colors the cells that <b>inherited</b> the document foreground
    /// (unset, or equal to the document's default); a run's own explicit foreground (markup color)
    /// wins over the brush — the Drawing layer's document-brush contract. Use this overload when
    /// the element supplies the base color (themed text over a <see cref="FormattedText"/> reused
    /// across styles); use the two-argument overload when the document's own runs/markup carry all
    /// the color and nothing should be imposed from outside.
    /// </remarks>
    public void DrawFormattedText(FormattedText text, in Rect bounds, IBrush brush, TextAttributes baseAttributes = default, UnderlineStyle baseUnderlineShape = UnderlineStyle.Single)
    {
        Inner.DrawFormattedText(text, bounds, brush, _capabilities, baseAttributes, baseUnderlineShape);
    }

    /// <summary>Paints a laid-out document into element-local <paramref name="bounds"/>; capabilities auto-supplied.</summary>
    /// <remarks>
    /// Renders with the document's <b>own</b> colors (markup spans, run styles) only. See the
    /// brushed overload's remarks for when to supply an external brush instead. <paramref name="baseAttributes"/>
    /// (default none) union-merges an inherited <see cref="TextAttributes"/> — an ancestor's
    /// <c>TextElement.TextAttributes</c> — onto every painted cell at paint time.
    /// </remarks>
    public void DrawFormattedText(FormattedText text, in Rect bounds, TextAttributes baseAttributes = default, UnderlineStyle baseUnderlineShape = UnderlineStyle.Single)
    {
        Inner.DrawFormattedText(text, bounds, _capabilities, baseAttributes, baseUnderlineShape);
    }

    /// <summary>Paints embedded content (images, sized text) into element-local <paramref name="bounds"/>; capabilities auto-supplied.</summary>
    public void DrawContent(in Rect bounds, IContent content)
    {
        Inner.DrawContent(bounds, content, _capabilities);
    }

    /// <summary>Paints embedded content with an explicit <paramref name="style"/> — the SGR backdrop for
    /// protocol-backed content (a selection background riding an OSC 66 emission).</summary>
    /// <remarks>
    /// Obsolete because of the <b>carrier</b>, not the operation: a whole
    /// <see cref="Cursorial.Output.CellStyle"/> is the shape the text-pipeline migration retires, and this is
    /// the UI layer naming the back-buffer format that <c>RenderContext</c> exists to stop naming. It still
    /// works and still forwards to <c>DrawingContext.DrawContent(in Rect, IContent, OutputCapabilities, in
    /// CellStyle)</c>, which is a later phase's problem; it is marked now so nothing new binds to it. It has
    /// no callers in this repository.
    /// </remarks>
    [Obsolete("The CellStyle backdrop is the carrier this migration retires; a BrushedStyle overload will replace it.",
              DiagnosticId = "CUR0001")]
    public void DrawContent(in Rect bounds, IContent content, in Cursorial.Output.CellStyle style)
    {
        Inner.DrawContent(bounds, content, _capabilities, style);
    }

    /// <summary>
    /// Restyles cells in place — graphemes preserved, each cell's style becoming whatever
    /// <paramref name="style"/> yields from it. Channels the delta does not carry pass through, so the
    /// caller states exactly which of background, attributes and the rest the tint has an opinion about.
    /// </summary>
    /// <remarks>
    /// This method provides selection-highlight primitive for glyph-rendered (FIGlet) text: geometry never shifts.
    /// </remarks>
    public void TintCells(in Rect bounds, in PartialStyle style)
    {
        Inner.TintCells(bounds, style);
    }

    /// <summary>Paints text through a glyph font at an element-local anchor (which may be negative —
    /// a scrolled editor); the font clips like a cell write. Optional brush colors per cell.</summary>
    public void DrawGlyphText(Cursorial.Rendering.Fonts.IGlyphFont face, int column, int row, string text,
                              IBrush? foreground, in Cursorial.Output.CellStyle style, in Rect brushBounds)
    {
        if (foreground is null)
        {
            // No brush: the style goes down flat, and its background has to be read through the sentinel
            // it arrived in — Color.Default is a CellStyle's only word for "no opinion", so it STAMPS
            // (a FIGlet line keeps showing the band underneath through its holes, which is what the
            // presenter's inverse pre-fill is there to be seen through), and anything else is a real
            // background and BOXES.
            Inner.DrawGlyphText(face, column, row, text,
                                style.Background.IsDefault ? PartialStyle.FromInk(style) : PartialStyle.From(style));
            return;
        }

        // The brush owns a FOREGROUND and says nothing else — the base style flows through as the fold's
        // backdrop, and the brush goes to the face unsampled instead of being wrapped in a closure the face
        // had to invoke blind.
        Inner.DrawGlyphText(face, column, row, text, style,
                            new BrushedStyle { Foreground = foreground }, brushBounds);
    }

    /// <summary>Paints a cell-rendered <see cref="IChart"/> into element-local <paramref name="area"/> (the chart clips to it).</summary>
    public void DrawChart(IChart chart, in Rect area)
    {
        ArgumentNullException.ThrowIfNull(chart);
        chart.Render(Inner, area);
    }

    // ───────────────────────────── strokes, boxes, panels, shadows ─────────────────────────────

    /// <summary>Strokes a line between element-local endpoints (axis-aligned → box glyphs; diagonal → braille).</summary>
    public void DrawLine(int x0, int y0, int x1, int y1, in Pen pen, bool overwrite = false, Arm? armHint = null)
    {
        Inner.DrawLine(x0, y0, x1, y1, pen, overwrite, armHint);
    }

    /// <inheritdoc cref="DrawLine(int, int, int, int, in Pen, bool, Arm?)"/>
    public void DrawLine(int x0, int y0, int x1, int y1, Color color, bool overwrite = false, Arm? armHint = null)
    {
        Inner.DrawLine(x0, y0, x1, y1, color, overwrite, armHint);
    }

    /// <summary>Strokes the outline of an element-local <paramref name="rect"/>.</summary>
    public void DrawBox(in Rect rect, in Pen pen, bool overwrite = false)
    {
        Inner.DrawBox(rect, pen, overwrite);
    }

    /// <inheritdoc cref="DrawBox(in Rect, in Pen, bool)"/>
    public void DrawBox(in Rect rect, Color color, bool overwrite = false)
    {
        Inner.DrawBox(rect, color, overwrite);
    }

    /// <summary>Strokes an outline with an optional background-only fill.</summary>
    public void DrawRectangle(in Rect rect, in Pen pen, IBrush? fill = null, bool overwrite = false)
    {
        Inner.DrawRectangle(rect, pen, fill, overwrite);
    }

    /// <summary>Strokes a titled box outline.</summary>
    public void DrawTitledBox(in Rect rect, in PanelTitle title, in Pen pen, bool overwrite = false)
    {
        Inner.DrawTitledBox(rect, title, pen, overwrite);
    }

    /// <summary>
    /// Fill + titled border in one call. The fill is Drawing's <b>background-only</b>
    /// <c>FillRectangle</c> — for an opaque surface use <see cref="FillOpaque(in Rect, IBrush, TextAttributes, bool)"/>
    /// followed by <see cref="DrawTitledBox"/> with <c>overwrite: true</c> (the <c>Panel.Background</c>
    /// path does the opaque fill for you).
    /// </summary>
    public void DrawPanel(in Rect rect, in Pen pen, IBrush? fill = null, PanelTitle title = default, bool overwrite = false)
    {
        Inner.DrawPanel(rect, pen, fill, title, overwrite);
    }

    /// <summary>
    /// Paints a drop shadow cast by the element-local <paramref name="element"/> rect. Shadows paint
    /// <em>outside</em> the rect — a render boundary cannot paint its own (it would fall outside its
    /// scene); boundary-level shadows are the parent zone's job (design doc §5.5).
    /// </summary>
    public void DrawDropShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor)
    {
        Inner.DrawDropShadow(element, geometry, shadowColor);
    }

    /// <summary>Paints an inner shadow inside the element-local <paramref name="element"/> rect.</summary>
    public void DrawInnerShadow(in Rect element, in ShadowGeometry geometry, Color shadowColor)
    {
        Inner.DrawInnerShadow(element, geometry, shadowColor);
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

    // Figure bounds are pen-gradient metadata: DrawingContext.BeginFigure takes its bounds in
    // current-local coordinates and maps them through the ambient state (element origin + band
    // shift) — a rect straddling the band's top edge samples exactly, no clamping needed.
    /// <summary>Begins a user figure with explicit element-local brush bounds (see <see cref="BeginFigure()"/>).</summary>
    /// <exception cref="InvalidOperationException">A user figure is already open.</exception>
    public RenderFigureScope BeginFigure(in Rect bounds) => BeginFigureCore(bounds);

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
