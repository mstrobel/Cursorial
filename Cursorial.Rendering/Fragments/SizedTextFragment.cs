using System.Buffers;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Text;

namespace Cursorial.Rendering.Fragments;

/// <summary>
/// A fragment that paints text at non-default size via the Kitty OSC 66 text-sizing protocol.
/// Suitable for titles, headings, and any UI affordance where a normal monospace cell is too
/// small. Renders correctly only when the terminal honors OSC 66 — applications that need a
/// fallback path for non-supporting terminals should drive this through an
/// <c>IContent.ScaledText</c> wrapper that chains to a FIGlet font.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coverage.</b> A glyph rendered with <c>Scale=s</c> and <c>Width=w</c> occupies an <c>s × w</c>
/// cell block per the protocol; the fragment's <see cref="GetSize"/> multiplies that block by the
/// number of clusters in <see cref="Text"/>. The buffer marks every cell in the bounding
/// rectangle as <see cref="CellKind.CoveredByFragment"/> so the renderer's normal cell-emission
/// pass skips the region.
/// </para>
/// <para>
/// <b>Style.</b> The <see cref="Style"/> on the fragment becomes the SGR backdrop for the OSC 66
/// emission — foreground, background, and attributes apply to the whole rendered region. There
/// is no per-cell styling within a single fragment; if you need that, attach multiple fragments
/// at adjacent anchor cells. The terminal paints the entire bounding rectangle with the SGR's
/// background, so composition with whatever was underneath in the cell grid is "the fragment
/// wins" — the caller is responsible for the visual coherence of the boundary.
/// </para>
/// </remarks>
public sealed class SizedTextFragment : IBufferFragment
{
    /// <summary>Construct a sized-text fragment at the requested sizing, text content, and style.</summary>
    public SizedTextFragment(in TextSizing sizing, string text, in Style style)
    {
        ArgumentNullException.ThrowIfNull(text);
        Sizing = sizing;
        Text = text;
        Style = style;
    }

    /// <summary>The OSC 66 metadata block — scale, width, numerator/denominator, alignment.</summary>
    public TextSizing Sizing { get; }

    /// <summary>The text content to render.</summary>
    public string Text { get; }

    /// <summary>The style applied to the whole rendered region.</summary>
    public Style Style { get; }

    /// <inheritdoc/>
    public Size GetSize()
    {
        // Cell footprint per the spec: each cluster occupies a (Scale x Width) block.
        // Scale=0 is invalid; treat as 1. Width=0 ("auto") falls back to the cluster's natural
        // width (1 for narrow, 2 for wide).
        int scale = Sizing.Scale == 0 ? 1 : Sizing.Scale;
        int totalColumns;

        if (Sizing.Width == 0)
        {
            // Auto width — sum natural cluster widths.
            totalColumns = GraphemeWidth.StringWidth(Text) * scale;
        }
        else
        {
            // Fixed width per cluster — count clusters via StringInfo.
            int clusters = new System.Globalization.StringInfo(Text).LengthInTextElements;
            totalColumns = clusters * Sizing.Width * scale;
        }

        // Vertical: scale tells us how many cell rows tall the block is.
        return new Size(totalColumns, scale);
    }

    /// <inheritdoc/>
    public bool IsSupported(OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        // Scale > 1 requires the s-key; Width > 0 requires the w-key. A fragment whose sizing
        // is fully default would emit an empty metadata block, which any terminal will ignore —
        // but a no-op fragment isn't useful, so we still report unsupported in that case so
        // higher-level fallback fires.
        bool needsScale = Sizing.Scale != 0 && Sizing.Scale != 1 ||
                          Sizing.Numerator != 0 ||
                          Sizing.Denominator != 0;

        bool needsWidth = Sizing.Width != 0;

        if (needsScale && !capabilities.TextSizing.Scale) return false;
        if (needsWidth && !capabilities.TextSizing.Width) return false;

        // If neither sub-feature is exercised, the fragment renders identically to plain text, and
        // a regular MonospaceFont would be the better choice.
        return needsScale || needsWidth;
    }

    /// <inheritdoc/>
    public void Emit(int row, int column, IBufferWriter<byte> output, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(capabilities);

        // Style is emitted as an absolute SGR (rather than a delta from whatever was active)
        // because the renderer's bracketing emits SGR-reset after our DECRC; there's no
        // continuity to preserve.
        if (Style != Style.Default)
            SgrEncoder.WriteAbsolute(output, Style);

        TextSizingWriter.WriteSplit(output, Sizing, Text);
    }
}
