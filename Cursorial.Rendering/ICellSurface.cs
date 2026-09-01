using Cursorial.Input;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Rendering;

/// <summary>
/// The shared cell-drawing contract implemented by both <see cref="CellBuffer"/> and
/// <see cref="CellBufferView"/>. Its purpose is <b>compile-time parity enforcement</b>: it pins
/// the two types' drawing surfaces to the same shape so they can't silently drift as the API
/// evolves. It is deliberately <c>internal</c> — it is not part of the public API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Avoid boxing.</b> <see cref="CellBufferView"/> is a <c>readonly struct</c>; assigning one to
/// an <see cref="ICellSurface"/>-typed variable, field, or parameter boxes it and allocates. The
/// interface exists as a contract, not as a runtime polymorphism mechanism — prefer the concrete
/// type at call sites. Code that genuinely needs to be generic over both surfaces should take a
/// generic type parameter constrained to <see cref="ICellSurface"/>
/// (<c>void Draw&lt;TSurface&gt;(TSurface surface) where TSurface : ICellSurface</c>): the JIT
/// specializes the method per value type, so a <see cref="CellBufferView"/> flows through with no
/// boxing.
/// </para>
/// <para>
/// Scope is the members that match <em>exactly</em> across both types. Members that legitimately
/// differ are intentionally excluded — e.g. <c>AddFragment</c> returns <c>void</c> on
/// <see cref="CellBuffer"/> but <c>bool</c> on <see cref="CellBufferView"/>, and
/// <see cref="CellBuffer.Fill(in Rect, in Cell)"/> has no view-local counterpart.
/// </para>
/// </remarks>
internal interface ICellSurface
{
    /// <summary>Width of the surface in cells.</summary>
    int Columns { get; }

    /// <summary>Height of the surface in cells.</summary>
    int Rows { get; }

    /// <summary>Cursor row, in this surface's coordinate space.</summary>
    int CursorRow { get; set; }

    /// <summary>Cursor column, in this surface's coordinate space.</summary>
    int CursorColumn { get; set; }

    /// <summary>Whether the cursor is visible.</summary>
    bool CursorVisible { get; set; }

    /// <summary>The cursor shape the renderer should emit.</summary>
    CursorShape CursorShape { get; set; }

    /// <summary>
    /// The style a blank cell carries on <b>this</b> surface — what <see cref="Clear"/> writes, and
    /// what code that blanks or clips cells on its own must use.
    /// </summary>
    /// <remarks>
    /// Explicitly <em>not</em> necessarily <see cref="CellStyle.Default"/>: a buffer constructed against a
    /// terminal that reported its own default colors carries those instead. Reaching for
    /// <see cref="Cell.Blank"/> (i.e. <see cref="CellStyle.Default"/>) rather than asking the surface
    /// punches a differently-styled hole into whatever region the blanked cells sat in — and because
    /// <see cref="CellStyle.Default"/> is <em>opaque</em>, on a surface whose blank is translucent it also
    /// occludes content the surface was supposed to let through.
    /// </remarks>
    CellStyle DefaultStyle { get; }

    /// <summary>Raw cell read / write at <c>(column, row)</c> — bypasses wide-cell handling and blending.</summary>
    Cell this[int column, int row] { get; set; }

    /// <summary>Place a single grapheme cluster, handling wide-cell width. Returns columns occupied.</summary>
    int Set(int column, int row, ReadOnlySpan<char> grapheme, in CellStyle style);

    /// <summary>
    /// Place a single grapheme cluster styled by a DELTA over the <b>destination cell</b>. Returns
    /// columns occupied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declining a channel is not a guarantee that the channel survives.</b> A declined channel is
    /// folded back into the style as the cell's own value, and the write's ordinary blend then runs
    /// over the whole style — so a channel nobody stated can still be rewritten by that blend, by the
    /// blending mode, or by <see cref="CellBuffer"/>'s whitespace-rescue branch. Treat the delta as
    /// "what this write ASKS FOR", not as a mask over the cell.
    /// </para>
    /// <para>
    /// The one channel with an unconditional guarantee is the <b>background</b>: declining it folds to
    /// <see cref="Color.Default"/>, which the blend short-circuits to the backdrop rather than
    /// compositing. That is a mechanism, not a coincidence, and it is the reason a delta cannot ask
    /// for the terminal's own default background — the same value means "say nothing" here.
    /// </para>
    /// <para>
    /// The exact per-channel behavior is a function of the channel, the blending mode, whether the
    /// cell's own value is opaque, whether the grapheme is whitespace, and what the neighboring cell
    /// holds. It is pinned exhaustively by <c>CellBufferDeltaWriteTests</c> rather than restated here:
    /// three attempts to write it as prose were each wrong in a different direction, and a doc comment
    /// that is confidently wrong costs more than one that defers.
    /// </para>
    /// </remarks>
    int Set(int column, int row, ReadOnlySpan<char> grapheme, in PartialStyle style);

    /// <summary>Lay out a string's grapheme clusters across a single row, stopping at the first
    /// C0/C1 control character. Returns columns written.</summary>
    int Write(int column, int row, ReadOnlySpan<char> text, in CellStyle style);

    /// <summary>
    /// As <see cref="Write(int, int, ReadOnlySpan{char}, in CellStyle)"/>, with the delta folded onto
    /// each destination cell in turn. Returns columns written.
    /// </summary>
    /// <remarks>
    /// Every cluster goes through <see cref="Set(int, int, ReadOnlySpan{char}, in PartialStyle)"/>, so the
    /// qualification on what a declined channel keeps applies here per cell — see its remarks.
    /// </remarks>
    int Write(int column, int row, ReadOnlySpan<char> text, in PartialStyle style);

    /// <summary>Fill every cell with <paramref name="cell"/>, applying the active blending mode.</summary>
    void Fill(in Cell cell);

    /// <summary>Fill every cell inside <paramref name="region"/> with <paramref name="cell"/>, applying the active blending mode.</summary>
    void Fill(in Rect region, in Cell cell);

    /// <summary>Push a blending mode onto the compositing stack.</summary>
    void PushBlendingMode(IBlendingMode mode);

    /// <summary>Pop the top blending mode off the compositing stack.</summary>
    IBlendingMode PopBlendingMode();

    /// <summary>Reset every cell to blank.</summary>
    void Clear();

    /// <summary>All fragments currently registered against the surface, keyed by anchor cell.</summary>
    FragmentDictionary Fragments { get; }

    /// <summary>
    /// Register a fragment at <c>(column, row)</c>. Returns <see langword="true"/> when registered,
    /// <see langword="false"/> when the anchor falls outside the surface and the fragment is dropped.
    /// </summary>
    bool AddFragment(int column, int row, IBufferFragment fragment, in CellStyle anchorStyle = default);

    /// <summary>Remove the fragment anchored at <c>(column, row)</c>. Returns true when one was removed.</summary>
    bool RemoveFragment(int column, int row);

    /// <summary>Remove the fragment anchored at <paramref name="position"/>. Returns true when one was removed.</summary>
    bool RemoveFragment(CellPosition position);

    /// <summary>True when a fragment with the given <paramref name="key"/> is currently registered.</summary>
    bool ContainsFragment(object key);

    /// <summary>Look up the anchor of the fragment registered under <paramref name="key"/>.</summary>
    bool TryGetFragmentAnchor(object key, out (int Column, int Row) anchor);

    /// <summary>
    /// Regions marked dirty since the last render, in this surface's coordinate space. On
    /// <see cref="CellBuffer"/> this is the live backing list; on <see cref="CellBufferView"/> it is
    /// a transformed snapshot clipped to the view (see <see cref="CellBuffer.DirtyRegions"/>).
    /// </summary>
    IReadOnlyList<Rect> DirtyRegions { get; }

    /// <summary>Mark a rectangular region (surface-local coordinates) as needing the renderer's attention.</summary>
    void MarkDirty(int column, int row, int columns, int rows);

    /// <summary>Mark a <see cref="Rect"/> (surface-local coordinates) as needing the renderer's attention.</summary>
    void MarkDirty(in Rect region);

    /// <summary>
    /// Create a windowed sub-view over the rectangle at <c>(offsetColumn, offsetRow)</c> of the
    /// given size, expressed in this surface's coordinate space.
    /// </summary>
    CellBufferView View(int offsetColumn, int offsetRow, int columns, int rows);

    /// <summary>Create a windowed sub-view over <paramref name="region"/>, in this surface's coordinate space.</summary>
    CellBufferView View(in Rect region);

    /// <summary>Blits the contents of <paramref name="view"/> into the back buffer.</summary>
    void Blit(CellBufferView view, in Rect region);
}
