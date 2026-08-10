using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Media;
using Cursorial.Terminal;
using Cursorial.Text;

namespace Cursorial.Rendering;

/// <summary>
/// A 2D grid of <see cref="Cell"/> values plus cursor state — the back-buffer that a
/// <see cref="FrameRenderer"/> emits to a terminal. The buffer owns its storage and re-uses
/// it across frames; resizing reallocates and clears.
/// </summary>
/// <remarks>
/// <para>
/// Storage is a 1D <see cref="Cell"/> array indexed as <c>row * Columns + column</c>. This is
/// one allocation per resize, no per-cell heap pressure, and friendly to span-based access if
/// we expose row spans later.
/// </para>
/// <para>
/// <b>Wide cells.</b> <see cref="Set(int, int, string, in CellStyle)"/> computes grapheme width via
/// <see cref="GraphemeWidth"/>, stores the cluster at <c>(row, col)</c> as
/// <see cref="CellKind.WideLeft"/> when its width is 2, and writes the right-half marker
/// (<see cref="Cell.WideContinuation"/>) into <c>(row, col + 1)</c>. Overwrites also clean up:
/// if the previous occupant of <c>(row, col)</c> was a wide-left, its dangling continuation is
/// stripped to a blank single; if the previous occupant was a continuation, the wide-left to its
/// left is. Either way the blank left behind carries the <see cref="CellStyle"/> of the <b>pair</b> —
/// only the glyph and the kind are cleared. The buffer therefore never exposes orphan continuations
/// or partial wide cells.
/// </para>
/// <para>
/// <b>A continuation carries no style.</b> Only the wide-left half of a pair holds one, because it
/// is the half the <see cref="FrameRenderer"/> emits and terminals paint both columns from that one
/// SGR state. Storing the style twice made it a duplicate that every write had to keep in sync, and
/// every wide-glyph bug in this buffer's history has been a failure to do so. Consequences for
/// callers: a continuation read out of this buffer answers <c>default(Style)</c>, so anything that
/// needs the colors painting a continuation's column must read them off the wide-left one column
/// left (see <see cref="Cell.WideContinuation"/>); and when the buffer blanks an orphaned
/// continuation, it sources the style from that wide-left <em>before</em> the write that clobbers it.
/// </para>
/// <para>
/// <b>Cursor state.</b> <see cref="CursorRow"/>, <see cref="CursorColumn"/>,
/// <see cref="CursorVisible"/>, and <see cref="CursorShape"/> live alongside the cell grid and
/// are emitted by <see cref="FrameRenderer"/> as a separate concern from the cell content.
/// Don't try to encode the cursor as a cell-level attribute; the renderer needs explicit cursor
/// state to do its job efficiently.
/// </para>
/// </remarks>
public sealed class CellBuffer : ICellSurface
{
    /// <summary>
    /// A grapheme that is durable whitespace, meaning it will not be overwritten by any other
    /// whitespace grapheme when calling <see cref="Set(int, int, string?, in CellStyle)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In <c>Cursorial.Drawing</c>, opaque rectangles are filled with this grapheme. This
    /// prevents non-occluding lines and recangles from overwriting them.
    /// </para>
    /// <para>
    /// As a practical
    /// example of why this is useful, consider a horizontal line that paints across the same
    /// row as a block of text. The line would not occlude the <em>visible</em> text, but it
    /// <em>would</em> occlude any whitespace spans <em>within</em> the text. Painting an
    /// occluding background behind the text, even with a transparent brush, protects the
    /// text block's entire bounds from occlusion.
    /// </para>
    /// <para>
    /// As a consequence, however, if the text block in the previous example happened to
    /// inhabit a surface with less than full opacity, none of the content from underlying
    /// surfaces could be visible through the blank spaces after composition.
    /// </para>
    /// </remarks>
    public static readonly string DurableEmptyGrapheme = "\u00A0";

    private Cell[] _cells;
    private int _columns;
    private int _rows;

    private readonly CellStyle _defaultStyle;
    private readonly Stack<IBlendingMode> _blendStack = new();
    private readonly Dictionary<(int Column, int Row), FragmentEntry> _fragments = new();

    // Secondary index — maps each registered fragment's Key to the anchor it lives at, so
    // ContainsFragment / TryGetFragmentAnchor are O(1) instead of scanning. Last-write-wins
    // when two fragments share a Key at different anchors (an unusual case but legal — Key is
    // implementation-defined, and two anchors holding the same logical fragment is a valid
    // shape for repeating content).
    private readonly Dictionary<object, (int Column, int Row)> _fragmentsByKey = new();
    private readonly List<Rect> _dirtyRegions = [];

    // Regions the renderer must re-emit unconditionally on the next render — even where the cell
    // content is byte-identical to the front buffer. Distinct from _dirtyRegions: dirty marks
    // RESTRICT emission (opt-in), force-repaint marks EXPAND it. The motivating case is a removed
    // Cells-layer image (iTerm2/Sixel): its pixels have no protocol erase, so they linger on the
    // terminal until a cell overwrites them — but the front buffer recorded those cells as the
    // bg-only covered placeholder, so if the new content matches that placeholder a plain diff
    // emits nothing and the image persists. The compositor marks the vacated footprint here.
    private readonly List<Rect> _forceRepaintRegions = [];

    /// <summary>
    /// Construct a buffer of the given dimensions, initialized to blank cells.
    /// </summary>
    /// <param name="columns">Width of the buffer in cells.</param>
    /// <param name="rows">Height of the buffer in rows.</param>
    /// <param name="capabilities">
    /// The terminal the buffer will be emitted to, when known. Its reported default colors derive
    /// the buffer's blank style — see <see cref="DefaultStyle"/>.
    /// </param>
    /// <param name="defaultStyle">
    /// The style a blank cell carries on this buffer, overriding whatever
    /// <paramref name="capabilities"/> would have derived. <see langword="null"/> (the default) keeps
    /// the derivation. Supply it for a surface whose blank is not the terminal's default —
    /// <see cref="CellStyle.Transparent"/> for an intermediate compositing surface, whose unpainted cells
    /// must contribute nothing when it is blended onwards.
    /// </param>
    /// <remarks>
    /// The blank style is a <em>constructor argument</em> and <see cref="DefaultStyle"/> is get-only on
    /// purpose. An <c>init</c> setter runs after the constructor body, so
    /// <c>new CellBuffer(w, h) { DefaultStyle = Style.Transparent }</c> would fill the grid with the
    /// capabilities-derived blank and only then declare the blank transparent — contents and
    /// declaration disagreeing from birth, which is exactly the lie <see cref="DefaultStyle"/> exists
    /// to prevent. Taking it here makes that state unrepresentable.
    /// </remarks>
    public CellBuffer(int columns, int rows, TerminalCapabilities? capabilities = null, CellStyle? defaultStyle = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        _columns = columns;
        _rows = rows;
        _cells = new Cell[checked(columns * rows)];

        Capabilities = capabilities;
        _defaultStyle = defaultStyle ?? DeriveDefaultStyle(capabilities);

        // Strictly after the blank style is settled — the grid must already hold what DefaultStyle
        // claims about it by the time any caller can observe either.
        FillWithDefaultStyle();
    }

    private static CellStyle DeriveDefaultStyle(TerminalCapabilities? capabilities)
    {
        // If the default foreground or background color is known, use the actual color in RGB
        // form so we can take advantage of alpha blending.
        if (capabilities is { Output.Color: { DefaultForeground: var fg, DefaultBackground: var bg } } &&
            (fg is { Kind: ColorKind.Rgb } || bg is { Kind: ColorKind.Rgb }))
        {
            return CellStyle.Default with
                   {
                       Foreground = fg ?? Color.Default,
                       Background = bg ?? Color.Default
                   };
        }

        return CellStyle.Default;
    }

    /// <summary>Width of the buffer in cells.</summary>
    public int Columns => _columns;

    /// <summary>Height of the buffer in rows.</summary>
    public int Rows => _rows;

    /// <summary>The buffer's dimensions, in cells.</summary>
    public (int Columns, int Rows) Dimensions => (_columns, _rows);

    /// <summary>
    /// The style a blank cell carries on this buffer — what <see cref="Clear()"/> and
    /// <see cref="ClearCells(in Rect)"/> write, and what code that blanks cells of its own accord
    /// should use instead of <see cref="Cell.Blank"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whatever the constructor's <c>defaultStyle</c> argument said; failing that,
    /// <see cref="CellStyle.Default"/> unless the terminal reported its own default foreground /
    /// background, in which case those colors (in RGB form, so alpha blending has something real to
    /// blend against) are the buffer's notion of blank.
    /// </para>
    /// <para>
    /// Fixed for the buffer's lifetime, and true of the grid from construction onwards: the initial
    /// fill happens after it is settled, and <see cref="Resize"/> re-applies it. It is still only a
    /// statement about <em>blank</em> — a caller remains free to <see cref="Fill(in Cell)"/> the grid
    /// with something else, which says nothing about what a subsequent <see cref="Clear()"/> writes.
    /// </para>
    /// </remarks>
    public CellStyle DefaultStyle => _defaultStyle;

    /// <summary>Cursor row position (0-based). Used by the renderer at frame emission.</summary>
    public int CursorRow { get; set; }

    /// <summary>Cursor column position (0-based). Used by the renderer at frame emission.</summary>
    public int CursorColumn { get; set; }

    /// <summary>Whether the cursor should be visible after this frame is rendered.</summary>
    public bool CursorVisible { get; set; } = true;

    /// <summary>Cursor shape applied at frame emission.</summary>
    public CursorShape CursorShape { get; set; } = CursorShape.Default;

    /// <summary>Total cell count (Columns × Rows). Useful for sized-array operations.</summary>
    public int CellCount => _cells.Length;

    /// <summary>
    /// Represents the terminal capabilities associated with the cell buffer,
    /// including information about the terminal's identification, input capabilities,
    /// and output capabilities. This property can be used to determine the features
    /// supported by the terminal.
    /// </summary>
    public TerminalCapabilities? Capabilities { get; }
    
    /// <summary>
    /// The blending mode applied to each <see cref="Set(int, int, string?, in CellStyle)"/> and <see cref="Fill(in Cell)"/> call. The top
    /// of the buffer's blend stack, or <see cref="BlendingModes.Default"/> when the stack is
    /// empty. <see cref="Clear()"/> and the raw indexer setter do NOT consult this — they assign
    /// cells verbatim.
    /// </summary>
    public IBlendingMode CurrentBlendingMode =>
        _blendStack.Count > 0 ? _blendStack.Peek() : BlendingModes.Default;

    /// <summary>
    /// Push a blending mode onto the stack; subsequent <see cref="Set(int, int, string?, in CellStyle)"/> / <see cref="Fill(in Cell)"/>
    /// calls use it.
    /// </summary>
    public void PushBlendingMode(IBlendingMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        _blendStack.Push(mode);
    }

    /// <summary>
    /// Pop the most recently pushed blending mode. Throws <see cref="InvalidOperationException"/>
    /// if the stack is empty — pop/push pairing is the caller's responsibility.
    /// </summary>
    public IBlendingMode PopBlendingMode()
    {
        if (_blendStack.Count == 0)
            throw new InvalidOperationException("Blending-mode stack is empty; nothing to pop.");

        return _blendStack.Pop();
    }

    /// <summary>
    /// Direct access to a cell. Setting via the indexer bypasses the active blending mode — use
    /// <see cref="Set(int, int, string, in CellStyle)"/> for normal text content — but it does
    /// maintain the wide-pair invariant: overwriting either half of an existing pair strips the
    /// orphaned partner to a blank single that keeps its own style (unless the write replaces that
    /// half in kind, which keeps the pair whole — the cell-by-cell pair-copy pattern region blits
    /// use), storing a
    /// <see cref="CellKind.WideLeft"/> writes its continuation (degrading to a blank single at
    /// the right edge, as <see cref="Set(int, int, string, in CellStyle)"/> does), and storing a
    /// continuation with no <see cref="CellKind.WideLeft"/> to pair with stores a blank single
    /// instead. The buffer therefore never holds half a wide glyph — the invariant the
    /// <see cref="FrameRenderer"/>'s wide-cell emission contract depends on (a split pair makes
    /// the diff emit into a wide glyph's right half, which corrupts the glyph on the terminal
    /// while the front buffer believes it intact).
    /// </summary>
    public Cell this[int column, int row]
    {
        get
        {
            ValidateCoordinates(column, row);
            return _cells[row * _columns + column];
        }
        set
        {
            ValidateCoordinates(column, row);

            int index = row * _columns + column;
            var previous = _cells[index];

            // Pair hygiene on overwrite (the kind guards make this safe even on a buffer that is
            // ALREADY inconsistent — never blank an innocent neighbor).
            if (previous.Kind == CellKind.WideContinuation && value.Kind != CellKind.WideContinuation &&
                column > 0 && _cells[index - 1].Kind == CellKind.WideLeft)
            {
                OrphanLeftHalfToBlankSingle(index - 1);
            }

            if (previous.Kind == CellKind.WideLeft && value.Kind != CellKind.WideLeft &&
                column + 1 < _columns && _cells[index + 1].Kind == CellKind.WideContinuation)
            {
                // `previous` IS the wide-left, read before this write clobbers it — the only place
                // the orphan's style still exists.
                OrphanContinuationToBlankSingle(index + 1, previous.Style);
            }

            if (value.Kind == CellKind.WideLeft)
            {
                if (column + 1 >= _columns)
                {
                    // No room for the right half — degrade to a blank single, mirroring Set.
                    _cells[index] = new Cell(null, CellKind.Single, value.Style);
                    return;
                }

                WriteWidePair(index, column, value.Grapheme, value.Style);
                return;
            }

            if (value.Kind == CellKind.WideContinuation &&
                (column == 0 || _cells[index - 1].Kind != CellKind.WideLeft))
            {
                // A bare continuation with nothing to pair with (a region copy whose left edge
                // split a pair) — store a blank single so no half-glyph ever exists. The blank keeps
                // the caller's style: a continuation this buffer produced has none, so a copy that
                // cuts a pair has to hand us the leading half's (CellBuffer.Blit does).
                _cells[index] = new Cell(null, CellKind.Single, value.Style);
                return;
            }

            _cells[index] = value;
        }
    }

    /// <summary>
    /// Store a <see cref="CellKind.WideLeft"/> + continuation pair at <c>(column, column + 1)</c>
    /// (caller guarantees the right half is in-row). If the continuation column currently holds the
    /// <b>next</b> pair's <see cref="CellKind.WideLeft"/>, that pair's own continuation is stripped
    /// to a blank single first (keeping that pair's style) — overwriting a WideLeft with a
    /// continuation would otherwise orphan the cell two columns over.
    /// </summary>
    private void WriteWidePair(int index, int column, string? grapheme, in CellStyle style)
    {
        if (_cells[index + 1].Kind == CellKind.WideLeft && column + 2 < _columns &&
            _cells[index + 2].Kind == CellKind.WideContinuation)
        {
            // The orphan's style lives on the wide-left at index + 1, which the pair write below is
            // about to overwrite — read it now.
            OrphanContinuationToBlankSingle(index + 2, _cells[index + 1].Style);
        }

        _cells[index] = new Cell(grapheme, CellKind.WideLeft, style);
        // Kind alone. The continuation is a placeholder the renderer never emits — the wide-left's
        // SGR paints both columns — so a style here would be a duplicate to keep in sync, never read.
        _cells[index + 1] = Cell.WideContinuation;
    }

    /// <summary>
    /// Strip an orphaned <see cref="CellKind.WideLeft"/> down to a blank single, <b>keeping its
    /// <see cref="CellStyle"/></b> — only the glyph and the kind are cleared.
    /// </summary>
    /// <remarks>
    /// Resetting it to <see cref="Cell.Blank"/> would be wrong twice: it discards the style the cell
    /// legitimately carried (a selection tint, a panel fill, a themed run), punching a
    /// differently-styled hole mid-region; and <see cref="CellStyle.Default"/> may not even be this
    /// buffer's notion of blank — <see cref="Clear()"/> / <see cref="ClearCells"/> write
    /// <c>_defaultStyle</c>, and a surface based on <see cref="CellStyle.Transparent"/> (an intermediate
    /// group buffer) would get an <em>opaque</em> orphan that occludes what the surface must let
    /// through. Preserving the style also matches the degrade sites, which already do.
    /// </remarks>
    private void OrphanLeftHalfToBlankSingle(int index)
        => _cells[index] = _cells[index] with { Grapheme = null, Kind = CellKind.Single };

    /// <summary>
    /// Strip an orphaned <see cref="CellKind.WideContinuation"/> down to a blank single carrying
    /// <paramref name="style"/> — the style of the <see cref="CellKind.WideLeft"/> it was paired with.
    /// </summary>
    /// <remarks>
    /// Same obligation as <see cref="OrphanLeftHalfToBlankSingle"/> (the blank must keep the pair's
    /// colors, not become a default-styled — and on a transparent surface, opaque — hole), but the
    /// continuation has no style of its own to keep: this buffer stores <see cref="Cell.Kind"/> alone
    /// there. So the caller must pass the leading half's style, read <b>before</b> the write that
    /// clobbers it — every call site here is a write that is about to do exactly that.
    /// </remarks>
    private void OrphanContinuationToBlankSingle(int index, in CellStyle style)
        => _cells[index] = new Cell(null, CellKind.Single, style);

    /// <summary>
    /// Place <paramref name="grapheme"/> at <c>(column, row)</c> with the given <paramref name="style"/>,
    /// handling wide-cell width and adjacent-cell cleanup. Returns the number of cells the
    /// placement occupied (1 or 2).
    /// </summary>
    public int Set(int column, int row, string? grapheme, in CellStyle style)
    {
        ValidateCoordinates(column, row);

        int width = string.IsNullOrEmpty(grapheme) ? 1 : GraphemeWidth.ClusterWidth(grapheme.AsSpan());
        if (width < 1) width = 1; // Defensive — a "wide" zero-width is meaningless here.

        if (width == 2 && column + 1 >= _columns)
        {
            // No room for the right half. Degrade to single-cell blank.
            width = 1;
            grapheme = null;
        }

        int index = row * _columns + column;
        var previous = _cells[index];

        // Apply the active blending mode against the cell being overwritten. SourceOver / empty
        // stack is a no-op (the mode just returns source), but every other mode tints / darkens /
        // lightens based on the existing color at this position.
        var blended = BlendStyle(style, previous.Style);
        
        if (string.IsNullOrWhiteSpace(grapheme) &&
            !(previous.Grapheme.IsWhiteSpace() && previous.Grapheme != DurableEmptyGrapheme) &&
            style.Background.IsOpaque is false)
        {
            var foregroundUnderneath = Color.Composite(style.Background, previous.Style.Foreground, CurrentBlendingMode);
            grapheme = previous.Grapheme;
            blended = blended with { Foreground = foregroundUnderneath };
        }

        // Pair hygiene on overwrite — the same rule the indexer setter applies, and for the same
        // reason: the kind guards make this safe even on a buffer that is ALREADY inconsistent
        // (Fill / ClearCells write raw cells and can leave a bare continuation or a lone WideLeft
        // behind) — never blank an innocent neighbor. Where the indexer tests the incoming
        // value.Kind, this path reads it off `width`: 2 stores a WideLeft, 1 a Single, and Set never
        // stores a bare continuation — so the indexer's "value.Kind != WideContinuation" is
        // unconditionally true here and only the WideLeft half of the rule survives as `width != 2`.
        if (previous.Kind == CellKind.WideContinuation &&
            column > 0 && _cells[index - 1].Kind == CellKind.WideLeft)
        {
            OrphanLeftHalfToBlankSingle(index - 1);
        }

        if (previous.Kind == CellKind.WideLeft && width != 2 &&
            column + 1 < _columns && _cells[index + 1].Kind == CellKind.WideContinuation)
        {
            // `previous` IS the wide-left, read before this write clobbers it — the only place the
            // orphaned continuation's style still exists.
            OrphanContinuationToBlankSingle(index + 1, previous.Style);
        }

        if (width == 2)
        {
            WriteWidePair(index, column, grapheme, blended);
            return 2;
        }

        _cells[index] = new Cell(grapheme, CellKind.Single, blended);
        return 1;
    }

    /// <summary>
    /// Place <paramref name="grapheme"/> at <c>(column, row)</c> with <paramref name="style"/> applied to
    /// the <b>cell already there</b>, handling wide-cell width and adjacent-cell cleanup exactly as
    /// <see cref="Set(int, int, string?, in CellStyle)"/> does. Returns the number of cells the placement
    /// occupied (1 or 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base is the DESTINATION CELL — which is what makes a channel the delta declines to state mean
    /// "leave it": the grapheme lands carrying whatever colour, attribute or link the caller said nothing
    /// about. Contrast <c>Clear</c> / <c>ClearCells</c>, where a decline falls through to the surface's own
    /// blank instead, because a clear OVERWRITES.
    /// </para>
    /// <para>
    /// <b>"Leave it" is what the delta ASKS FOR, not a guarantee.</b> The fold puts the cell's own value
    /// into the style, and <see cref="Set(int, int, string?, in CellStyle)"/> then runs
    /// <see cref="CellStyle.BlendOver"/> on the whole style against that same cell — so a channel nobody
    /// stated is still handed to the blend, and can come back changed. Known routes: a non-default
    /// <see cref="CurrentBlendingMode"/>, whose colour math runs on every channel it is handed; a
    /// NON-OPAQUE value already in the cell, which <see cref="BlendingModes.SourceOver"/> resolves
    /// against the cell's own background rather than skipping; and the whitespace-rescue branch below,
    /// which can take a declined foreground from the NEIGHBOURING cell.
    /// </para>
    /// <para>
    /// The BACKGROUND is the one channel with an unconditional guarantee, by mechanism rather than luck:
    /// the fold spells its absence <see cref="Color.Default"/> (see <see cref="FoldOntoCell"/>), and
    /// <see cref="CellStyle.BlendOver"/>'s Background arm short-circuits exactly that value to the
    /// backdrop instead of compositing. That is also why a delta cannot ASK for the terminal's own
    /// default background — the value that would say so is the value that says nothing.
    /// </para>
    /// <para>
    /// The exact behaviour is a function of the channel, the mode, the cell's opacity, whether the
    /// grapheme is whitespace, and what the neighbour holds. <c>CellBufferDeltaWriteTests</c> pins it
    /// exhaustively — 16,740 (cell, delta) pairs per surface — and is the reference rather than this
    /// comment. Three attempts to state the conditions in prose were each wrong in a different
    /// direction; the tests are cheap to consult and cannot drift from the code.
    /// </para>
    /// </remarks>
    public int Set(int column, int row, string? grapheme, in PartialStyle style)
    {
        ValidateCoordinates(column, row);

        return Set(column, row, grapheme, FoldOntoCell(_cells[row * _columns + column].Style, style));
    }

    /// <summary>
    /// The style <see cref="Set(int, int, string?, in PartialStyle)"/> hands its
    /// <see cref="CellStyle"/> sibling: <paramref name="style"/> folded onto <paramref name="backdrop"/>,
    /// the style the destination cell already carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An absent BACKGROUND is spelled <see cref="Color.Default"/> rather than read back off the cell,
    /// because that is already <see cref="Set(int, int, string?, in CellStyle)"/>'s own word for "leave the
    /// background alone" — its blend keeps the backdrop verbatim for <see cref="Color.Default"/> instead of
    /// compositing. Handing it the cell's actual colour would say the same thing under the default blending
    /// mode and something else entirely under any other, which is the difference between a stamp and a
    /// stamp that darkens what it lands on.
    /// </para>
    /// <para>
    /// Exposed rather than kept private because a caller that has already read the cell for reasons of its
    /// own — <c>FigletFont</c> looks the same cell up to decide smushing, then adjusts the folded style
    /// before writing it — must fold by the same rule. The rule and the blend it is paired with belong
    /// together: a hand-copy of the neighbouring blend rule is what left <see cref="Set(int, int, string?, in CellStyle)"/>
    /// and <see cref="Fill(in Cell)"/> compositing underline colour differently (see the remarks on the
    /// private <c>BlendStyle</c> overload below).
    /// </para>
    /// </remarks>
    internal static CellStyle FoldOntoCell(in CellStyle backdrop, in PartialStyle style)
        => style.ApplyTo(backdrop with { Background = Color.Default });

    /// <summary>
    /// Write the grapheme clusters of <paramref name="text"/> across a single row starting at
    /// <c>(column, row)</c>, advancing the column by each cluster's width (1 for normal, 2 for
    /// wide) and applying the active blending mode per cell — exactly as
    /// <see cref="Set(int, int, string?, in CellStyle)"/> does for a single cluster. A cluster that
    /// would not fit in the remaining columns stops the write rather than being clipped to a
    /// partial glyph. The write is single-row by contract: it <b>stops at the first C0/C1 control
    /// character</b> (including newlines and tabs) rather than storing it as a junk cell — split
    /// text into lines (or use the drawing layer's multi-line text) for multi-row layout, and
    /// expand tabs upstream. Returns the number of columns written.
    /// </summary>
    public int Write(int column, int row, ReadOnlySpan<char> text, in CellStyle style)
        => WriteCore(column, row, text, style, delta: null);

    /// <summary>
    /// As <see cref="Write(int, int, ReadOnlySpan{char}, in CellStyle)"/>, but each cluster goes through
    /// <see cref="Set(int, int, string?, in PartialStyle)"/> — so the delta is folded onto EACH
    /// destination cell in turn, not resolved once for the run.
    /// </summary>
    /// <remarks>
    /// Which also means the qualification on what a declined channel actually keeps applies here PER
    /// CELL — see the remarks on <see cref="Set(int, int, string?, in PartialStyle)"/>.
    /// </remarks>
    public int Write(int column, int row, ReadOnlySpan<char> text, in PartialStyle style)
        => WriteCore(column, row, text, default, style);

    // One cluster walk for both overloads. `delta` selects which Set the cluster goes through, in the
    // shape MonospaceFont.PaintCore already uses; `style` is read only when it is null. Written this way
    // rather than as two loops because the walk carries four rules (control stop, minimum width, edge
    // stop, advance-by-Set's-return) that a second copy would be free to drift off one at a time.
    private int WriteCore(int column, int row, ReadOnlySpan<char> text, in CellStyle style, in PartialStyle? delta)
    {
        ValidateCoordinates(column, row);
        if (text.IsEmpty) return 0;

        int start = column;
        var clusters = text.GetGraphemeEnumerator();

        while (clusters.MoveNext())
        {
            var cluster = clusters.Current;

            // Stop at the first control character — this is a single-row, printable-text write.
            if (IsC0OrC1Control(cluster[0])) break;

            int width = GraphemeWidth.ClusterWidth(cluster);
            if (width < 1) width = 1;

            // Stop at the right edge rather than placing a degraded glyph.
            if (column + width > _columns) break;

            column += delta is { } d
                          ? Set(column, row, cluster.ToString(), d)
                          : Set(column, row, cluster.ToString(), style);
        }

        return column - start;
    }

    // C0 (U+0000–U+001F), DEL (U+007F), and C1 (U+0080–U+009F). Controls are grapheme-cluster
    // boundaries on both sides (UAX #29), so checking a cluster's first char classifies the cluster.
    internal static bool IsC0OrC1Control(char c) => c < 0x20 || (c >= 0x7F && c <= 0x9F);

    /// <summary>
    /// Reset every cell to <see cref="Cell.Blank"/>. Does NOT apply the active blending mode —
    /// clear is an explicit reset. Also removes every registered fragment.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_cells);

        _fragments.Clear();
        _fragmentsByKey.Clear();
        _dirtyRegions.Clear();
        _forceRepaintRegions.Clear();

        FillWithDefaultStyle();
    }

    // ---- Fragment sidecar -----------------------------------------------------------------

    /// <summary>
    /// All fragments currently registered against the buffer, keyed by anchor cell. The renderer
    /// iterates this collection after the regular cell-grid pass and emits each fragment's
    /// protocol bytes at its anchor. Order is iteration order of the underlying dictionary —
    /// fragments must not depend on each other's visual ordering at the cell layer.
    /// </summary>
    public FragmentDictionary Fragments => new(_fragments, 0, 0);

    internal Dictionary<(int Column, int Row), FragmentEntry> FragmentsInternal => _fragments;

    /// <summary>
    /// Register <paramref name="fragment"/> at the anchor cell <c>(column, row)</c>. Pure
    /// metadata registration — the cell grid is <b>not</b> modified, so anything the caller
    /// previously painted under the fragment's footprint continues to render in the cell pass
    /// and shows through wherever the fragment's protocol payload doesn't draw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="anchorStyle"/> is the style the renderer applies as the SGR backdrop
    /// when it positions the cursor at the anchor — useful when the fragment needs an SGR
    /// state to inherit. Fragments are free to emit their own SGR inside <c>Emit</c>; this
    /// style only governs the entry state.
    /// </para>
    /// <para>
    /// If a fragment is already registered at this anchor, it is replaced. Removing a
    /// fragment leaves the cells under it untouched — whatever was painted before the
    /// fragment was added remains. Callers who want a clean state when removing a fragment
    /// should explicitly repaint the region afterward.
    /// </para>
    /// <para>
    /// Returns <see langword="true"/> when the fragment was registered. The buffer always
    /// registers within its bounds (an out-of-range anchor throws); the <see langword="bool"/>
    /// return exists for parity with <see cref="CellBufferView.AddFragment"/>, which returns
    /// <see langword="false"/> when the anchor falls outside the view.
    /// </para>
    /// </remarks>
    public bool AddFragment(int column, int row, IBufferFragment fragment, in CellStyle anchorStyle = default)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ValidateCoordinates(column, row);

        var anchor = (column, row);

        // If something was already registered at this anchor, drop its Key from the secondary
        // index — otherwise that stale key would still resolve to this anchor after we replace
        // the entry below.
        if (_fragments.TryGetValue(anchor, out var existing))
            _fragmentsByKey.Remove(existing.Fragment.Key);

        _fragments[anchor] = new FragmentEntry(fragment, anchorStyle);
        _fragmentsByKey[fragment.Key] = anchor;
        return true;
    }

    /// <summary>
    /// Remove the fragment anchored at <c>(column, row)</c>. Returns true when a fragment was
    /// removed. Cells under the removed fragment retain whatever they held before — see
    /// <see cref="AddFragment"/> for the layering contract.
    /// </summary>
    public bool RemoveFragment(int column, int row)
    {
        ValidateCoordinates(column, row);

        if (!_fragments.TryGetValue((column, row), out var existing)) return false;

        _fragments.Remove((column, row));

        // Only drop the Key from the secondary index when it actually points at this anchor.
        // A stale Key entry (from a fragment that moved or was replaced) would otherwise be
        // erased here, breaking lookups for whatever's currently registered under the Key.
        if (_fragmentsByKey.TryGetValue(existing.Fragment.Key, out var indexedAnchor) &&
            indexedAnchor == (column, row))
        {
            _fragmentsByKey.Remove(existing.Fragment.Key);
        }

        var fragmentSize = existing.Fragment.GetSize();

        MarkDirty(column, row, fragmentSize.Columns, fragmentSize.Rows);

        return true;
    }

    /// <summary>
    /// Remove the fragment anchored at the specified position. Returns true when a fragment was
    /// removed. Cells under the removed fragment retain whatever they held before — see
    /// <see cref="AddFragment"/> for the layering contract.
    /// </summary>
    public bool RemoveFragment(CellPosition position) => RemoveFragment(position.Column, position.Row);

    /// <summary>
    /// Remove every registered fragment. Cells under the removed fragments retain whatever they held —
    /// see <see cref="AddFragment"/> for the layering contract. Marks each removed footprint dirty so a
    /// dirty-region renderer revisits it (and a graphics-overlay renderer emits the protocol erase).
    /// </summary>
    public void ClearFragments()
    {
        if (_fragments.Count == 0)
            return;

        foreach (var (anchor, entry) in _fragments)
        {
            var size = entry.Fragment.GetSize();
            MarkDirty(anchor.Column, anchor.Row, size.Columns, size.Rows);
        }

        _fragments.Clear();
        _fragmentsByKey.Clear();
    }

    /// <summary>
    /// True when a fragment with the given <paramref name="key"/> is currently registered on
    /// the buffer. Useful for "is this image already on screen?" checks without scanning the
    /// fragment dictionary. Comparison uses <see cref="object.Equals(object)"/>, so value-type
    /// keys (records, tuples, <see cref="uint"/>, …) compare by value.
    /// </summary>
    public bool ContainsFragment(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _fragmentsByKey.ContainsKey(key);
    }

    /// <summary>
    /// Look up the anchor of the fragment registered under <paramref name="key"/>. Returns
    /// <see langword="true"/> with the anchor when one is registered, <see langword="false"/>
    /// otherwise. Combine with the <see cref="Fragments"/> dictionary to fetch the full entry
    /// (<c>Fragments[anchor]</c>).
    /// </summary>
    public bool TryGetFragmentAnchor(object key, out (int Column, int Row) anchor)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _fragmentsByKey.TryGetValue(key, out anchor);
    }

    // ---- Dirty-region tracking ------------------------------------------------------------

    /// <summary>
    /// Rectangles the caller has marked as needing inspection in the next render. The renderer
    /// short-circuits to only the union of these regions when the list is non-empty —
    /// everything outside is assumed unchanged. An empty list lets the renderer fall back to
    /// its default full-buffer diff (safe for callers that don't bother tracking dirtiness).
    /// </summary>
    /// <remarks>
    /// Marking is an <em>optimization hint</em>: cells inside marked regions still benefit from
    /// the renderer's back-vs-front comparison (no emit when unchanged); cells outside any
    /// marked region are skipped entirely (no comparison, no emit). Consumers using dirty
    /// regions accept the responsibility of marking every position they've actually changed —
    /// missing a change leaves the terminal showing stale content for that cell.
    /// </remarks>
    public IReadOnlyList<Rect> DirtyRegions => _dirtyRegions;

    /// <summary>
    /// Mark a rectangular region as needing the renderer's attention on the next render. Empty
    /// rectangles (zero width or height) are dropped silently — callers can call <c>MarkDirty</c>
    /// with computed rectangles without pre-checking the result.
    /// </summary>
    public void MarkDirty(int column, int row, int columns, int rows)
        => MarkDirty(new Rect(column, row, columns, rows));

    /// <summary>Mark a <see cref="Rect"/> as needing the renderer's attention on the next render.</summary>
    public void MarkDirty(in Rect region)
    {
        if (region.IsEmpty) return;
        if (region.Row >= _rows || region.Column >= _columns) return;
        if (region.RowEnd <= 0 || region.ColumnEnd <= 0) return;

        // Clamp to buffer bounds — out-of-range marks waste renderer work and confuse the
        // bitmask computation.
        int col = Math.Max(0, region.Column);
        int row = Math.Max(0, region.Row);
        int colEnd = Math.Min(_columns, region.ColumnEnd);
        int rowEnd = Math.Min(_rows, region.RowEnd);
        _dirtyRegions.Add(new Rect(col, row, colEnd - col, rowEnd - row));
    }

    /// <summary>
    /// Drop all dirty-region marks. The <see cref="FrameRenderer"/> calls this automatically at
    /// the end of each render so consumers don't have to manage the lifecycle themselves;
    /// callers performing manual emission can invoke it explicitly when needed.
    /// </summary>
    public void ClearDirty() => _dirtyRegions.Clear();

    /// <summary>
    /// Rectangles the renderer must re-emit on the next render <em>regardless</em> of whether the
    /// cell content differs from the front buffer. Unlike <see cref="DirtyRegions"/> (which, when
    /// opted in, <i>restricts</i> emission to the marked area), this <i>expands</i> it: cells inside
    /// a force-repaint region are always eligible to emit even when they compare equal to the front
    /// buffer. Used to overwrite content the front buffer can't see is stale — chiefly the pixels of
    /// a removed <see cref="FragmentLayer.Cells"/> image, which has no protocol erase and lingers
    /// until a cell paints over it.
    /// </summary>
    public IReadOnlyList<Rect> ForceRepaintRegions => _forceRepaintRegions;

    /// <summary>
    /// Mark a rectangular region for unconditional re-emission on the next render — see
    /// <see cref="ForceRepaintRegions"/>. Empty rectangles are dropped silently.
    /// </summary>
    public void ForceRepaint(int column, int row, int columns, int rows)
        => ForceRepaint(new Rect(column, row, columns, rows));

    /// <summary>Mark a <see cref="Rect"/> for unconditional re-emission on the next render.</summary>
    public void ForceRepaint(in Rect region)
    {
        if (region.IsEmpty) return;
        if (region.Row >= _rows || region.Column >= _columns) return;
        if (region.RowEnd <= 0 || region.ColumnEnd <= 0) return;

        int col = Math.Max(0, region.Column);
        int row = Math.Max(0, region.Row);
        int colEnd = Math.Min(_columns, region.ColumnEnd);
        int rowEnd = Math.Min(_rows, region.RowEnd);
        _forceRepaintRegions.Add(new Rect(col, row, colEnd - col, rowEnd - row));
    }

    /// <summary>
    /// Drop all force-repaint marks. The <see cref="FrameRenderer"/> calls this automatically at the
    /// end of each render so consumers don't manage the lifecycle themselves.
    /// </summary>
    public void ClearForceRepaint() => _forceRepaintRegions.Clear();

    /// <summary>
    /// Replace every cell with <paramref name="cell"/>, blending its <see cref="CellStyle"/> against
    /// each position's existing cell through <see cref="CurrentBlendingMode"/>. When the active
    /// mode is <see cref="BlendingModes.Default"/>, the fast path is a single <c>Array.Fill</c>
    /// — every cell becomes <paramref name="cell"/> verbatim.
    /// </summary>
    public void Fill(in Cell cell)
    {
        var mode = CurrentBlendingMode;

        if (ReferenceEquals(mode, BlendingModes.Default))
        {
            Array.Fill(_cells, cell);
            return;
        }

        for (int i = 0; i < _cells.Length; i++)
            _cells[i] = cell with { Style = BlendStyle(cell.Style, _cells[i].Style, mode) };
    }

    /// <summary>
    /// Replace every cell inside <paramref name="region"/> with <paramref name="cell"/>. Under the
    /// default blending mode the cell is written verbatim (a replace — matching the whole-buffer
    /// <see cref="Fill(in Cell)"/>, so a transparent cell clears the region); under any other mode the
    /// cell's style is blended against each existing cell through <see cref="CurrentBlendingMode"/>.
    /// The rect is clamped to the buffer bounds — an out-of-buffer or empty rect is a no-op.
    /// </summary>
    public void Fill(in Rect region, in Cell cell)
    {
        if (region.IsEmpty) return;

        int row = Math.Max(0, region.Row);
        int col = Math.Max(0, region.Column);
        int rowEnd = Math.Min(_rows, region.RowEnd);
        int colEnd = Math.Min(_columns, region.ColumnEnd);
        if (row >= rowEnd || col >= colEnd) return;

        var mode = CurrentBlendingMode;
        // Default mode is a verbatim replace (consistent with Fill(in Cell)'s Array.Fill fast path),
        // so a transparent fill actually clears; non-default modes blend per cell.
        bool fast = ReferenceEquals(mode, BlendingModes.Default);

        for (int r = row; r < rowEnd; r++)
        {
            int rowStart = r * _columns;
            for (int c = col; c < colEnd; c++)
            {
                int idx = rowStart + c;
                _cells[idx] = fast
                    ? cell
                    : cell with { Style = BlendStyle(cell.Style, _cells[idx].Style, mode) };
            }
        }
    }

    /// <summary>
    /// Reset every cell inside <paramref name="region"/> to <see cref="Cell.Blank"/>. Does NOT
    /// apply blending. Out-of-buffer or empty rects are no-ops. Fragments and dirty regions are
    /// untouched — this is a cell-only operation. Use <see cref="Clear()"/> (parameterless) to
    /// wipe the whole buffer including fragments / dirty state, or remove fragments individually
    /// via <see cref="RemoveFragment(int, int)"/>.
    /// </summary>
    public void ClearCells(in Rect region)
    {
        if (region.IsEmpty) return;

        int row = Math.Max(0, region.Row);
        int col = Math.Max(0, region.Column);
        int rowEnd = Math.Min(_rows, region.RowEnd);
        int colEnd = Math.Min(_columns, region.ColumnEnd);
        if (row >= rowEnd || col >= colEnd) return;

        var blank = Cell.Blank with { Style = _defaultStyle };

        for (int r = row; r < rowEnd; r++)
        {
            int rowStart = r * _columns;
            for (int c = col; c < colEnd; c++)
                _cells[rowStart + c] = blank;
        }
    }

    private CellStyle BlendStyle(in CellStyle source, in CellStyle backdrop)
        => source.BlendOver(backdrop, CurrentBlendingMode);

    /// <remarks>
    /// The <see cref="Fill(in Cell)"/> paths hoist <see cref="CurrentBlendingMode"/> out of their loops, so
    /// they need the mode passed in — that is the ONLY reason this overload exists. The rule itself lives in
    /// <see cref="CellStyle.BlendOver"/> and is not restated here: a hand-copy of it drifted off the guarded
    /// underline rule and left <see cref="Set(int, int, string?, in CellStyle)"/> and <see cref="Fill(in Cell)"/> compositing underline colour
    /// differently. <c>CellBufferBlendingTests.SetAndFill_ResolveTheSameSourceOverTheSameBackdropIdentically</c>
    /// pins the two paths together.
    /// </remarks>
    private static CellStyle BlendStyle(in CellStyle source, in CellStyle backdrop, IBlendingMode mode)
        => source.BlendOver(backdrop, mode);

    // ---- View factories -------------------------------------------------------------------

    /// <summary>
    /// A <see cref="CellBufferView"/> spanning the entire buffer. The view translates 0-based
    /// view-local coordinates to backing-buffer coordinates and clips writes to a rectangle —
    /// useful for handing a widget its own coordinate space without exposing the surrounding
    /// surface. See <see cref="CellBufferView"/> for the full contract.
    /// </summary>
    public CellBufferView AsView() => new(this);

    /// <summary>
    /// A <see cref="CellBufferView"/> over the rectangle
    /// (<paramref name="offsetRow"/>, <paramref name="offsetColumn"/>) × (<paramref name="columns"/>,
    /// <paramref name="rows"/>). Negative offsets / dimensions are clamped, and the rect is
    /// clipped against the buffer's bounds.
    /// </summary>
    public CellBufferView View(int offsetColumn, int offsetRow, int columns, int rows)
        => new(this, offsetColumn, offsetRow, columns, rows);

    /// <summary>
    /// A <see cref="CellBufferView"/> over the rectangle <paramref name="region"/>. Negative offsets /
    /// dimensions are clamped, and the rect is clipped against the buffer's bounds.
    /// </summary>
    public CellBufferView View(in Rect region)
        => new(this, region.Column, region.Row, region.Columns, region.Rows);

    /// <summary>
    /// Copy <paramref name="view"/>'s contents into this buffer with <paramref name="region"/>'s
    /// top-left as the destination anchor. At most <paramref name="region"/>'s extent is copied, and
    /// at most what the view holds; anything falling outside this buffer is clipped away rather than
    /// wrapped or clamped. A raw cell copy — the styles arrive verbatim, without the active blending
    /// mode (compositing is <c>SceneCompositor</c>'s job, not a blit's).
    /// </summary>
    /// <remarks>
    /// Reads through the view's own coordinate mapping, so a windowed or re-based view contributes
    /// the cells it addresses, not its backing buffer's corner. Wide-glyph pairs the copy would split
    /// (a leading half whose continuation lands outside the copied rectangle, or a continuation whose
    /// leading half was cut) degrade to blank singles, mirroring <see cref="CellBufferView.Set(int, int, string?, in CellStyle)"/> —
    /// a blit can never strand half a pair in the destination.
    /// </remarks>
    public void Blit(CellBufferView view, in Rect region)
    {
        if (view.IsEmpty || region.IsEffectivelyEmpty)
            return;

        // Copy no more than the destination rect asks for AND no more than the view holds.
        int columns = Math.Min(region.Columns, view.Columns);
        int rows = Math.Min(region.Rows, view.Rows);

        // Clip the destination to this buffer, trimming the source by the same amount so the two
        // stay in step (a negative anchor skips the leading source cells rather than shifting them).
        int firstColumn = Math.Max(0, -region.Column);
        int firstRow = Math.Max(0, -region.Row);
        columns = Math.Min(columns, _columns - region.Column);
        rows = Math.Min(rows, _rows - region.Row);

        if (columns <= firstColumn || rows <= firstRow)
            return;

        for (int row = firstRow; row < rows; row++)
        for (int column = firstColumn; column < columns; column++)
        {
            var cell = view.Read(column, row);

            // A leading half whose continuation lies outside the COPIED RECTANGLE degrades: the
            // indexer's own degrade covers only the buffer's edge, so without this a pair-write
            // would land one column past the rectangle the caller asked for.
            if (cell.Kind == CellKind.WideLeft && column + 1 >= columns)
                cell = new Cell(null, CellKind.Single, cell.Style);

            // The mirror case — a continuation whose leading half the copy cut away. The indexer
            // already turns it into a blank single, but a continuation carries no style of its own
            // (this buffer stores Kind alone there), so that blank would be a default-styled hole
            // where the pair's background used to run. Hand it the leading half's style, which the
            // cut left outside the copied rectangle; the indexer still decides the kind.
            else if (cell.Kind == CellKind.WideContinuation && column == firstColumn)
                cell = cell with { Style = view.StyleOfLeadingHalf(column, row) };

            // Through the indexer, not _cells: it is what keeps wide pairs consistent on overwrite.
            this[region.Column + column, region.Row + row] = cell;
        }

        MarkDirty(region.Column + firstColumn, region.Row + firstRow, columns - firstColumn, rows - firstRow);
    }

    /// <summary>
    /// Resize the buffer to <paramref name="columns"/> × <paramref name="rows"/>. Contents are
    /// discarded; the new buffer is initialized to blank cells. The cursor state is preserved.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);

        if (columns == _columns && rows == _rows)
        {
            Clear();
            return;
        }

        _columns = columns;
        _rows = rows;
        _cells = new Cell[checked(columns * rows)];
        _fragments.Clear();
        _fragmentsByKey.Clear();
        _dirtyRegions.Clear();
        _forceRepaintRegions.Clear();

        FillWithDefaultStyle();
    }

    /// <summary>
    /// A fragment registered against a <see cref="CellBuffer"/>: the fragment itself and the
    /// anchor style the renderer uses as the SGR backdrop before invoking the fragment's emit
    /// callback.
    /// </summary>
    public readonly record struct FragmentEntry(IBufferFragment Fragment, CellStyle AnchorStyle);

    /// <summary>Internal: raw row span for renderer access. Not part of the public-API stability guarantee.</summary>
    internal ReadOnlySpan<Cell> GetRowSpan(int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows);
        return _cells.AsSpan(row * _columns, _columns);
    }

    private void ValidateCoordinates(int column, int row)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, _rows);
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, _columns);
    }
    
    // Every caller reaches here with a freshly zeroed array (a new allocation, or Array.Clear), which
    // already IS the blank when the blank is Style.Default — so the guard skips a redundant pass, not
    // a required one.
    private void FillWithDefaultStyle()
    {
        if (_defaultStyle != default)
            _cells.AsSpan().Fill(Cell.Blank with { Style = _defaultStyle });
    }
    
    public static implicit operator CellBufferView(CellBuffer buffer) => buffer.AsView();
}