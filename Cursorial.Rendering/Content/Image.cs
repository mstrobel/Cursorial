using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fragments;

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
    public Image(ImageData data, in Style placeholderStyle = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        PlaceholderStyle = placeholderStyle;
    }

    /// <summary>The image payload + cell footprint.</summary>
    public ImageData Data => _data;

    /// <summary>Style applied to the placeholder rectangle when no graphics protocol is supported.</summary>
    public Style PlaceholderStyle { get; }

    /// <summary>Text to display when no graphics protocol is supported. For icons, could be an emoji.</summary>
    public string PlaceholderText => "[image]";

    /// <inheritdoc/>
    public Size Paint(CellBuffer buffer, int row, int column, in Style style, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(capabilities);

        IBufferFragment? fragment = ChooseFragment(capabilities);
        if (fragment is not null)
        {
            buffer.AddFragment(row, column, fragment, style);
            return fragment.GetSize();
        }

        return PaintPlaceholder(buffer, row, column, style);
    }

    private IBufferFragment? ChooseFragment(OutputCapabilities capabilities)
    {
        // Kitty first — supports PNG natively, has the most predictable cell-footprint semantics.
        if (capabilities.Graphics.KittyGraphics && _data.Format == ImageFormat.Png)
            return new KittyImageFragment(_data);

        // iTerm2 second — accepts PNG / JPEG / GIF; format hint passes through unchanged.
        if (capabilities.Graphics.ITerm2InlineImages)
            return new ITerm2ImageFragment(_data);

        return null;
    }

    private Size PaintPlaceholder(CellBuffer buffer, int row, int column, in Style style)
    {
        // Effective placeholder style: the content's PlaceholderStyle wins, falling back to the
        // caller-supplied style when no placeholder was configured. The caller's style is what
        // would have been the SGR backdrop for a real image fragment, so reusing it produces a
        // visually-coherent "where the image would have been" affordance.
        var fillStyle = PlaceholderStyle == Style.Default ? style : PlaceholderStyle;
        int rowEnd = Math.Min(buffer.Rows, row + _data.CellSize.Rows);
        int colEnd = Math.Min(buffer.Columns, column + _data.CellSize.Columns);

        for (int r = row; r < rowEnd; r++)
            for (int c = column; c < colEnd; c++)
                buffer.Set(r, c, " ", fillStyle);

        // Center a "[image]" label in the placeholder when there's room. Bog-standard ASCII so
        // it always fits — callers wanting localization should supply their own placeholder
        // content via a custom IContent.
        const string label = "[image]";
        if (_data.CellSize.Columns >= label.Length && _data.CellSize.Rows >= 1)
        {
            int labelRow = row + _data.CellSize.Rows / 2;
            int labelCol = column + (_data.CellSize.Columns - label.Length) / 2;
            if (labelRow >= 0 && labelRow < buffer.Rows)
                for (int i = 0; i < label.Length; i++)
                {
                    int c = labelCol + i;
                    if (c < 0 || c >= buffer.Columns) continue;
                    buffer.Set(labelRow, c, label[i].ToString(), fillStyle);
                }
        }

        int paintedCols = Math.Min(_data.CellSize.Columns, Math.Max(0, buffer.Columns - column));
        int paintedRows = Math.Min(_data.CellSize.Rows, Math.Max(0, buffer.Rows - row));
        return new Size(paintedCols, paintedRows);
    }
}
