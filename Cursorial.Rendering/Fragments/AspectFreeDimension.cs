namespace Cursorial.Rendering.Fragments;

/// <summary>
/// Identifies which placement dimension an image fragment leaves to the graphics protocol's own
/// aspect-ratio scaling rather than pinning to a cell count on the wire.
/// </summary>
/// <remarks>
/// When the caller sizes an image in exactly one dimension (e.g. <c>renderSize (cols, 0)</c>), the
/// other is derived from the image's native aspect ratio. Pinning that derived dimension to a whole
/// cell count and asking the terminal to stretch the image into it introduces a sub-cell rounding
/// error. Instead the fragment can <i>omit</i> that dimension on the wire and let the protocol scale
/// the image to its true aspect (Kitty: omit <c>c=</c> or <c>r=</c>), which is exact to the pixel.
/// The fragment still reports a whole-cell footprint from <c>GetSize</c> for layout; the rendered
/// image may diverge from it sub-cell.
/// </remarks>
public enum AspectFreeDimension
{
    /// <summary>Both dimensions are pinned — the placement specifies the full cell footprint.</summary>
    None,

    /// <summary>Columns are derived from aspect ratio; the column count is omitted on the wire.</summary>
    Columns,

    /// <summary>Rows are derived from aspect ratio; the row count is omitted on the wire.</summary>
    Rows,
}
