using Cursorial.Output;
using Cursorial.Rendering.Media;

namespace Cursorial.Rendering;

public static class Extensions
{
    /// <summary>
    /// The blank a delta means on a surface whose own blank is <paramref name="surfaceBlank"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base is the SURFACE'S BLANK — <c>DefaultStyle</c> — and explicitly <em>not</em> necessarily
    /// <see cref="CellStyle.Default"/>, nor the cells being cleared. A clear OVERWRITES: whatever was
    /// there is going away by definition, so there is nothing underneath for a declined channel to
    /// inherit, and the only thing left for it to fall through to is what this surface calls empty.
    /// </para>
    /// <para>
    /// This is the opposite base from <see cref="CellBuffer.Set(int, int, string?, in PartialStyle)"/>,
    /// and getting the two backwards is invisible on a surface whose blank happens to equal
    /// <see cref="CellStyle.Default"/> — and wrong on one whose blank is
    /// <see cref="CellStyle.Transparent"/> (a scene or compositing scratch buffer), where inheriting
    /// <see cref="CellStyle.Default"/> punches an <em>opaque</em> hole through a surface built to let
    /// content past.
    /// </para>
    /// </remarks>
    private static CellStyle BlankFor(in PartialStyle blankStyle, in CellStyle surfaceBlank)
        => blankStyle.ApplyTo(surfaceBlank);

    /// <summary>
    /// True when the surface's own <see cref="CellBuffer.Clear()"/> has just written exactly
    /// <paramref name="blankCell"/>, so re-filling with it would be a no-op pass over the grid.
    /// </summary>
    /// <remarks>
    /// The test is against <paramref name="surfaceBlank"/> — the surface's <c>DefaultStyle</c> — not
    /// against <see cref="CellStyle.Default"/>, which is only <em>accidentally</em> right on a buffer whose
    /// blank happens to be the default. It is wrong in both directions elsewhere: a scene surface's blank
    /// is <see cref="CellStyle.Transparent"/>, so a transparent clear was re-filling the whole grid for
    /// nothing, while an opaque default clear was skipped and left the surface transparent.
    /// <para>
    /// The whole cell is compared, not just its style: <see cref="CellBuffer.Clear()"/> writes a
    /// <em>glyphless</em> blank, so a caller's blank that carries one (<see cref="Cell.DurableEmpty"/>,
    /// whose NBSP is what makes an opaque fill non-occludable) is not what clear wrote, however default
    /// its style — and skipping the fill silently dropped that grapheme.
    /// </para>
    /// </remarks>
    private static bool ClearAlreadyWrote(in Cell blankCell, in CellStyle surfaceBlank)
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

        public void Clear(in CellStyle blankStyle)
        {
            Clear(target, Cell.Blank with { Style = blankStyle });
        }

        /// <summary>
        /// Clear the whole buffer to <paramref name="blankStyle"/> applied to <b>this surface's own
        /// blank</b> (<see cref="CellBuffer.DefaultStyle"/>) — explicitly <em>not</em> necessarily
        /// <see cref="CellStyle.Default"/>, and not the cells being cleared. See <see cref="BlankFor"/>.
        /// </summary>
        public void Clear(in PartialStyle blankStyle)
        {
            Clear(target, BlankFor(blankStyle, target.DefaultStyle));
        }

        public void ClearCells(in Rect rect, in Cell blankCell)
        {
            target.ClearCells(rect);
            if (ClearAlreadyWrote(blankCell, target.DefaultStyle)) return;
            target.Fill(rect, blankCell);
        }

        public void ClearCells(in Rect rect, in CellStyle blankStyle)
        {
            ClearCells(target, rect, Cell.Blank with { Style = blankStyle });
        }

        /// <summary>
        /// Clear <paramref name="rect"/> to <paramref name="blankStyle"/> applied to <b>this surface's own
        /// blank</b> (<see cref="CellBuffer.DefaultStyle"/>) — explicitly <em>not</em> necessarily
        /// <see cref="CellStyle.Default"/>, and not the cells being cleared. See <see cref="BlankFor"/>.
        /// </summary>
        public void ClearCells(in Rect rect, in PartialStyle blankStyle)
        {
            ClearCells(target, rect, BlankFor(blankStyle, target.DefaultStyle));
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

        public void Clear(in CellStyle blankStyle)
        {
            Clear(target, Cell.Blank with { Style = blankStyle });
        }

        /// <summary>
        /// Clear the whole view to <paramref name="blankStyle"/> applied to <b>this surface's own blank</b>
        /// (<see cref="CellBufferView.DefaultStyle"/>, which is the backing buffer's) — explicitly
        /// <em>not</em> necessarily <see cref="CellStyle.Default"/>, and not the cells being cleared. See
        /// <see cref="BlankFor"/>.
        /// </summary>
        public void Clear(in PartialStyle blankStyle)
        {
            Clear(target, BlankFor(blankStyle, target.DefaultStyle));
        }

        public void ClearCells(in Rect rect, in Cell blankCell)
        {
            target.View(rect).Clear(blankCell);
        }

        public void ClearCells(in Rect rect, in CellStyle blankStyle)
        {
            target.View(rect).Clear(blankStyle);
        }

        /// <summary>
        /// Clear <paramref name="rect"/> to <paramref name="blankStyle"/> applied to <b>this surface's own
        /// blank</b> (<see cref="CellBufferView.DefaultStyle"/>, which is the backing buffer's) — explicitly
        /// <em>not</em> necessarily <see cref="CellStyle.Default"/>, and not the cells being cleared. See
        /// <see cref="BlankFor"/>.
        /// </summary>
        public void ClearCells(in Rect rect, in PartialStyle blankStyle)
        {
            target.View(rect).Clear(blankStyle);
        }
    }
}
