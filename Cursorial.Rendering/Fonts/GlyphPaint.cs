using Cursorial.Rendering.Media;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// The part of a delta-driven glyph paint that every face does identically: settling
/// stamp-versus-box.
/// </summary>
/// <remarks>
/// Shared rather than repeated because the four faces have four separate paint paths — one of them
/// (<see cref="ShadowedFont"/>) paints twice — and "does a background fill the gaps?" must not have
/// four answers. The other half — folding the delta onto the cell about to be written — moved into
/// the write itself (<see cref="CellBuffer.Set(int, int, string?, in PartialStyle)"/>), next to the
/// blend rule it is paired with.
/// </remarks>
internal static class GlyphPaint
{
    /// <summary>
    /// Settle stamp-versus-box for one paint, and return the delta the INK pass should use.
    /// </summary>
    /// <remarks>
    /// With no background stated there is nothing to own and the caller stamps <paramref name="style"/>
    /// unchanged. With one, <paramref name="box"/> is filled here — and the delta comes back MINUS its
    /// background, so the ink pass inherits the fill it just laid down instead of compositing the same
    /// colour onto itself a second time (which is not a no-op under a non-default blending mode: see
    /// <see cref="ShadowedFont"/>, which paints its shadow under <c>Multiply</c>).
    /// </remarks>
    public static PartialStyle Ink(in CellBufferView buffer, int column, int row, in Size box, in PartialStyle style)
    {
        if (style.Background is null)
            return style;

        FillBox(buffer, column, row, box, style);

        return style with { Background = null };
    }

    // The gap half of box mode. Background and blending ONLY: the ink's attributes are deliberately
    // not spread across cells the face never inked, because the ones that are visible on a blank —
    // Underline, Strikethrough, Overline — would rule a line clean across a multi-row glyph box.
    // Which attributes may safely spread is a question with a different answer per caller, and it
    // already has an owner in TextPresenter.FillAttributes, an allowlist this layer cannot see.
    private static void FillBox(in CellBufferView buffer, int column, int row, in Size box, in PartialStyle style)
    {
        var fill = new PartialStyle { Background = style.Background, Mode = style.Mode };

        int rowStart = Math.Max(row, buffer.LocalRowStart);
        int rowEnd   = Math.Min(row + box.Rows, buffer.LocalRowEnd);
        int colStart = Math.Max(column, buffer.LocalColumnStart);
        int colEnd   = Math.Min(column + box.Columns, buffer.LocalColumnEnd);

        for (int r = rowStart; r < rowEnd; r++)
        for (int c = colStart; c < colEnd; c++)
        {
            // A null grapheme through Set, not a raw cell write: Set is the blending-aware path fonts
            // are required to use, and it is also the one that keeps wide-pair hygiene when the box
            // lands on half of a wide cell. The DELTA overload, so the fold onto the cell underneath
            // happens inside the write rather than being restated per face.
            buffer.Set(c, r, null, fill);
        }
    }
}
