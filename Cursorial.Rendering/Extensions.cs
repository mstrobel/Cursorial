using Cursorial.Output;

namespace Cursorial.Rendering;

public static class Extensions
{
    /// <summary>
    /// True when the surface's own <see cref="CellBuffer.Clear()"/> has just written exactly
    /// <paramref name="blankCell"/>, so re-filling with it would be a no-op pass over the grid.
    /// </summary>
    /// <remarks>
    /// The test is against <paramref name="surfaceBlank"/> — the surface's <c>DefaultStyle</c> — not
    /// against <see cref="Style.Default"/>, which is only <em>accidentally</em> right on a buffer whose
    /// blank happens to be the default. It is wrong in both directions elsewhere: a scene surface's blank
    /// is <see cref="Style.Transparent"/>, so a transparent clear was re-filling the whole grid for
    /// nothing, while an opaque default clear was skipped and left the surface transparent.
    /// <para>
    /// The whole cell is compared, not just its style: <see cref="CellBuffer.Clear()"/> writes a
    /// <em>glyphless</em> blank, so a caller's blank that carries one (<see cref="Cell.DurableEmpty"/>,
    /// whose NBSP is what makes an opaque fill non-occludable) is not what clear wrote, however default
    /// its style — and skipping the fill silently dropped that grapheme.
    /// </para>
    /// </remarks>
    private static bool ClearAlreadyWrote(in Cell blankCell, in Style surfaceBlank)
        => blankCell == Cell.Blank with { Style = surfaceBlank };

    extension(CellBuffer target)
    {
        public Size Size => target.Dimensions;
        public Rect Bounds => new(0, 0, target.Dimensions);

        public void Clear(in Cell blankCell)
        {
            target.Clear();
            if (ClearAlreadyWrote(blankCell, target.DefaultStyle)) return;
            target.Fill(blankCell);
        }

        public void Clear(in Style blankStyle)
        {
            Clear(target, Cell.Blank with { Style = blankStyle });
        }

        public void ClearCells(in Rect rect, in Cell blankCell)
        {
            target.ClearCells(rect);
            if (ClearAlreadyWrote(blankCell, target.DefaultStyle)) return;
            target.Fill(rect, blankCell);
        }

        public void ClearCells(in Rect rect, in Style blankStyle)
        {
            ClearCells(target, rect, Cell.Blank with { Style = blankStyle });
        }
    }

    extension(CellBufferView target)
    {
        public Size Size => target.Dimensions;

        public void Clear(in Cell blankCell)
        {
            target.Clear();
            if (ClearAlreadyWrote(blankCell, target.DefaultStyle)) return;
            target.Fill(blankCell);
        }

        public void Clear(in Style blankStyle)
        {
            Clear(target, Cell.Blank with { Style = blankStyle });
        }

        public void ClearCells(in Rect rect, in Cell blankCell)
        {
            target.View(rect).Clear(blankCell);
        }

        public void ClearCells(in Rect rect, in Style blankStyle)
        {
            target.View(rect).Clear(blankStyle);
        }
    }
}
