using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;
using Cursorial.Text;

namespace Cursorial.Rendering.Content;

/// <summary>
/// An image drawn at the configured cell footprint. Picks a rendering protocol at paint time
/// based on the terminal's negotiated capabilities — Kitty graphics → iTerm2 inline images
/// → cell-rectangle placeholder when neither is supported.
/// </summary>
/// <remarks>
/// <para>
/// <b>Protocol selection.</b> Kitty's graphics protocol is preferred when both Kitty and iTerm2
/// are available because it supports both PNG and arbitrary placement IDs (useful for layered
/// images in a future iteration). The iTerm2 path is the practical fallback on iTerm2 itself
/// and on WezTerm. Sixel will join the chain once a sixel encoder is in place; today there's
/// no upstream PNG → sixel converter in this repo so Sixel is silently skipped even on
/// terminals that report support.
/// </para>
/// <para>
/// <b>Format compatibility.</b> Kitty only accepts PNG via the encoded-bytes transmission
/// path; if the supplied <see cref="ImageData.Format"/> is JPEG or GIF and Kitty is the only
/// supported protocol, this content treats Kitty as unsupported and falls through. Callers
/// w`ho need cross-protocol compatibility should either re-encode upstream or supply PNG.
/// </para>
/// <para>
/// <b>Fallback rendering.</b> When no graphics protocol is available, the content paints a
/// cell-rectangle placeholder filled with the configured <see cref="PlaceholderStyle"/>'s
/// background and a centered "[image]" label so the layout's reserved region remains visually
/// obvious. Callers wanting a richer fallback (Braille downscale, ASCII art, etc.) can build
/// their own <see cref="IContent"/> on top of <see cref="KittyImageFragment"/> /
/// <see cref="ITerm2ImageFragment"/> directly.
/// </para>
/// </remarks>
public sealed class Image : IContent
{
    private readonly ImageData _data;

    /// <summary>Construct an image content from the supplied data.</summary>
    public Image(ImageData data, in Style placeholderStyle = default, string? placeholderText = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        PlaceholderStyle = placeholderStyle;
        PlaceholderText = placeholderText ?? "[image]";
    }

    /// <summary>The image payload + cell footprint.</summary>
    public ImageData Data => _data;

    /// <summary>Style applied to the placeholder rectangle when no graphics protocol is supported.</summary>
    public Style PlaceholderStyle { get; init; }

    /// <summary>Text to display when no graphics protocol is supported. For icons, could be an emoji.</summary>
    public string PlaceholderText { get; init; }

    /// <inheritdoc/>
    public Size Paint(CellBuffer buffer, int column, int row, in Style style, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(capabilities);

        IBufferFragment? fragment = ChooseFragment(capabilities);
        if (fragment is not null)
        {
            buffer.AddFragment(column, row, fragment, style);
            return fragment.GetSize();
        }

        return PaintPlaceholder(buffer, column, row, style);
    }

    private IBufferFragment? ChooseFragment(OutputCapabilities capabilities)
    {
        // No bytes → no transmittable payload, regardless of capability. This is the case
        // Icon hits when its resource URI didn't resolve: it constructs an Image with empty
        // bytes plus the configured fallback glyph, expecting the placeholder path. Without
        // this guard, a graphics-capable terminal would receive an empty fragment.
        if (_data.Bytes.IsEmpty) return null;

        // Kitty first — supports PNG natively, has the most predictable cell-footprint semantics.
        if (capabilities.Graphics.KittyGraphics && _data.Format == ImageFormat.Png)
            return new KittyImageFragment(_data);

        // iTerm2 second — accepts PNG / JPEG / GIF; format hint passes through unchanged.
        if (capabilities.Graphics.ITerm2InlineImages)
            return new ITerm2ImageFragment(_data);

        return null;
    }

    private Size PaintPlaceholder(CellBuffer buffer, int column, int row, in Style style)
    {
        // Effective placeholder style: the content's PlaceholderStyle wins, falling back to the
        // caller-supplied style when no placeholder was configured. The caller's style is what
        // would have been the SGR backdrop for a real image fragment, so reusing it produces a
        // visually coherent "where the image would have been" affordance.
        var fillStyle = PlaceholderStyle == Style.Default ? style : PlaceholderStyle;
        var cellSize = _data.CellSize;
        var rowSpan = cellSize.Rows;
        
        if (rowSpan is 0)
            rowSpan = Math.Max(1, cellSize.Columns / 2);

        int rowEnd = Math.Min(buffer.Rows, row + rowSpan);
        int colEnd = Math.Min(buffer.Columns, column + cellSize.Columns);

        for (var r = rowEnd - 1; r >= row; r--)
        {
            for (var c = column; c < colEnd; c++)
                buffer.Set(c, r, " ", fillStyle);
        }

        // Center some custom text in the placeholder when there's room. Bog-standard ASCII, so
        // it always fits — callers wanting localization should supply their own placeholder
        // content via a custom IContent.
        var label = PlaceholderText;
        var labelLength = GraphemeWidth.StringWidth(label);

        if (cellSize.Columns >= labelLength && rowSpan >= 1)
        {
            var labelRow = row + rowSpan / 2;
            var labelCol = column + (cellSize.Columns - labelLength) / 2;

            if (labelRow >= 0 && labelRow < buffer.Rows)
            {
                var advance = 0;
                var enumerator = label.GetGraphemeEnumerator();

                while (enumerator.MoveNext())
                {
                    var cluster = enumerator.Current;
                    var width = GraphemeWidth.ClusterWidth(cluster);
                    
                    if (labelCol + advance + width > buffer.Columns) break;
                    
                    buffer.Set(labelCol + advance, labelRow, enumerator.Current.ToString(), fillStyle);
                    
                    advance += width;
                }
            }
        }

        var paintedCols = Math.Min(cellSize.Columns, Math.Max(0, buffer.Columns - column));
        var paintedRows = Math.Min(rowSpan, Math.Max(0, buffer.Rows - row));

        return new Size(paintedCols, paintedRows);
    }
}
