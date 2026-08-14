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

    /// <summary>Writes one cell as a per-cell DELTA folded onto the destination — the form an element
    /// reaches for when it has an opinion about some channels and none about the rest, rather than
    /// stamping a whole <see cref="CellStyle"/>.</summary>
    public void Set(int column, int row, string? grapheme, in PartialStyle style)
        => Inner.Set(column, row, grapheme, in style);

    /// <summary>Blits the contents of <paramref name="view"/> into the back buffer at <paramref name="region"/>.</summary>
    public void Blit(CellBufferView view, in Rect region)
        => Inner.Blit(view, region);

    /// <inheritdoc cref="DrawingContext.FillRectangle(in Rect, in BrushedStyle)"/>
    public void FillRectangle(in Rect region, in BrushedStyle style)
        => Inner.FillRectangle(region, style);

    /// <inheritdoc cref="DrawingContext.FillOpaque(in Rect, in BrushedStyle, bool)"/>
    /// <remarks>
    /// <b><paramref name="overwrite"/> defaults to <see langword="false"/> here</b>, narrower than the
    /// drawing layer's <see langword="true"/> — the default an element actually reaches for. The narrower
    /// default is load-bearing: <c>TextPresenter</c>'s inverse band fill paints OVER a glyph face, and
    /// <see cref="CellBuffer.Set(int, int, string, in PartialStyle)"/> rescues the ink underneath only
    /// on the non-overwriting path.
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

    /// <inheritdoc cref="DrawingContext.PaintRectangle(in Rect, in BrushedStyle, bool)"/>
    public void PaintRectangle(in Rect region, in BrushedStyle style, bool overwrite = false)
        => Inner.PaintRectangle(region, style, overwrite);

    /// <inheritdoc cref="DrawingContext.PaintRectangle(in Rect, in BrushedStyle, in Rect, bool)"/>
    public void PaintRectangle(in Rect region, in BrushedStyle style, in Rect brushBounds, bool overwrite = false)
        => Inner.PaintRectangle(region, style, brushBounds, overwrite);

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
    /// <paramref name="baseStyle"/> is a per-cell DELTA over the draw's ground state — attributes,
    /// underline, hyperlink and blending mode included, not just the two color channels the brush
    /// overload can carry. An absent <c>Background</c> here is <b>no opinion</b> (the channel
    /// resolves to its default); on the brush overload an omitted background is
    /// <see cref="Brushes.Transparent"/>. A caller holding a whole <see cref="CellStyle"/> ground
    /// state restates it UNDER the delta — <c>BrushedStyle.FromStated(base).Then(delta)</c>. See
    /// <see cref="DrawingContext.DrawText(int, int, ReadOnlySpan{char}, in BrushedStyle)"/>.
    /// </remarks>
    public Size DrawText(int column, int row, ReadOnlySpan<char> text, in BrushedStyle baseStyle)
        => Inner.DrawText(column, row, text, baseStyle);

    /// <inheritdoc cref="DrawingContext.DrawText(int, int, ReadOnlySpan{char}, in BrushedStyle, in Rect)"/>
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         in BrushedStyle baseStyle, in Rect sampleBounds)
        => Inner.DrawText(column, row, text, baseStyle, sampleBounds);

    /// <inheritdoc cref="DrawText(int, int, ReadOnlySpan{char}, in BrushedStyle)"/>
    /// <remarks>An omitted <paramref name="background"/> is <see cref="Brushes.Transparent"/>.</remarks>
    public Size DrawText(int column, int row, ReadOnlySpan<char> text,
                         IBrush foreground, IBrush? background = null)
        => Inner.DrawText(column, row, text,
                          new BrushedStyle
                          {
                              Foreground = foreground,
                              Background = background ?? Brushes.Transparent
                          });

    /// <summary>
    /// Draws one line of text truncated to <paramref name="maxWidth"/> cells on a grapheme-cluster
    /// boundary, appending <paramref name="ellipsis"/> when content was cut. Text that fits paints
    /// whole with no ellipsis; text that does not keeps the longest prefix whose display width fits
    /// in <c>maxWidth − StringWidth(ellipsis)</c> cells and paints the ellipsis immediately after
    /// it. <paramref name="maxWidth"/> = <see cref="int.MaxValue"/> is the documented no-limit
    /// spelling: the text paints whole without being measured at all. Returns the painted bounding
    /// box (the text's own box when it fit; prefix + ellipsis width × 1 row when truncated).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a hot-loop primitive, not the text pipeline: one measured walk and at most two
    /// <see cref="DrawText(int, int, ReadOnlySpan{char}, in BrushedStyle)"/> calls, no layout
    /// object — a DataGrid repaints every visible cell through it. Wrapping, rich content, and trim
    /// modes other than a trailing ellipsis belong to
    /// <see cref="DrawFormattedText(FormattedText, in Rect, in BrushedStyle)"/>. Line breaks are
    /// not interpreted: the text is treated as a single line.
    /// </para>
    /// <para>
    /// <paramref name="baseStyle"/> is the same per-cell delta <c>DrawText</c> takes — a caller
    /// inking a cell over an opaque fill states <see cref="Brushes.Transparent"/> as the background
    /// itself. The cut is by grapheme clusters against the column budget: a char-index cut reads a
    /// display-column count as a UTF-16 length, which can split a surrogate pair or an emoji
    /// sequence. The ellipsis is measured, not assumed one cell wide — a custom
    /// <paramref name="ellipsis"/> (or an empty one: truncate with no indicator) reserves exactly
    /// its own display width.
    /// </para>
    /// </remarks>
    public Size DrawTruncated(int column, int row, ReadOnlySpan<char> text, int maxWidth,
                              in BrushedStyle baseStyle, string ellipsis = TextFormatter.DefaultEllipsis)
    {
        if (maxWidth < int.MaxValue && GraphemeWidth.StringWidth(text) > maxWidth)
        {
            int ellipsisWidth = GraphemeWidth.StringWidth(ellipsis);

            // Keep whole grapheme clusters while they fit the post-ellipsis budget.
            var enumerator = text.GetGraphemeEnumerator();
            int width = 0;
            int end = 0;

            while (enumerator.MoveNext())
            {
                int next = width + GraphemeWidth.ClusterWidth(enumerator.Current);

                if (next > maxWidth - ellipsisWidth)
                    break;

                width = next;
                end = enumerator.ElementIndex + enumerator.Current.Length;
            }

            Inner.DrawText(column, row, text[..end], baseStyle);
            Inner.DrawText(column + width, row, ellipsis, baseStyle);
            return new Size(width + ellipsisWidth, 1);
        }

        return Inner.DrawText(column, row, text, baseStyle);
    }

    /// <summary>Paints a laid-out document into element-local <paramref name="bounds"/>; capabilities auto-supplied.</summary>
    /// <remarks>
    /// <paramref name="preference"/> is the element-side opinion folded onto every painted cell. Its
    /// <see cref="BrushedStyle.Foreground"/> colors the cells that <b>inherited</b> the document
    /// foreground (unset, or equal to the document's default); a run's own explicit foreground
    /// (markup color) wins over the brush — the Drawing layer's document-brush contract. Its
    /// attribute channels merge an ancestor's <c>TextElement.TextAttributes</c> onto the paint per
    /// axis — build them with <see cref="BrushedStyle.Imposing"/>, which routes each flag through
    /// the axis that owns it. State a Foreground when the element supplies the base color (themed
    /// text over a <see cref="FormattedText"/> reused across styles); pass
    /// <see langword="default"/> to render with the document's <b>own</b> colors (markup spans,
    /// run styles) and attributes, imposing nothing from outside.
    /// </remarks>
    public void DrawFormattedText(FormattedText text, in Rect bounds, in BrushedStyle preference = default)
    {
        Inner.DrawFormattedText(text, bounds, _capabilities, preference);
    }

    /// <summary>Paints embedded content (images, sized text) into element-local <paramref name="bounds"/>; capabilities auto-supplied.</summary>
    public void DrawContent(in Rect bounds, IContent content)
    {
        Inner.DrawContent(bounds, content, _capabilities);
    }

    /// <summary>Paints embedded content with an explicit <paramref name="style"/> delta — for
    /// protocol-backed content it resolves at the fragment's anchor into the SGR backdrop (a
    /// selection background riding an OSC 66 emission).</summary>
    /// <remarks>
    /// The replacement the retired <see cref="Cursorial.Output.CellStyle"/> overload's obsoletion
    /// promised: the carrier is the delta the text pipeline speaks, and the UI layer stops naming
    /// the back-buffer format here.
    /// </remarks>
    public void DrawContent(in Rect bounds, IContent content, in BrushedStyle style)
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
    public void DrawGlyphText(Rendering.Fonts.IGlyphFont face, int column, int row, string text,
                              IBrush? foreground, in CellStyle style, in Rect brushBounds)
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
    /// <c>FillRectangle</c> — for an opaque surface use <see cref="FillOpaque(in Rect, in BrushedStyle, bool)"/>
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
