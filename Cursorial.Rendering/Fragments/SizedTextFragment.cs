using System.Buffers;
using System.Globalization;
using System.Text;

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
/// <b>Multi-line text.</b> The <see cref="Text"/> may contain <c>'\n'</c> line breaks. Each
/// line emits as its own OSC 66 sequence, with cursor moves between lines so the next line
/// starts at the original anchor column, <c>Scale</c> rows below the previous line. Bare CR
/// (<c>'\r'</c>) is treated as whitespace within a line; only LF starts a new logical line.
/// The fragment's <see cref="GetSize"/> reports the bounding rectangle of all lines.
/// </para>
/// <para>
/// <b>Coverage.</b> A glyph rendered with <c>Scale=s</c> and <c>Width=w</c> occupies an
/// <c>s × w</c> cell block per the protocol; the fragment's bounding rectangle is the widest
/// line's width by the number of lines (each line band being <c>scale</c> rows tall). Cells
/// in the bounding rectangle but outside any rendered line — i.e., the unfilled right edges of
/// shorter lines — receive the renderer's bg-only-space treatment from the cell-emit pass, so
/// the panel background continues uninterrupted behind the sized text.
/// </para>
/// <para>
/// <b>Style.</b> The <see cref="Style"/> on the fragment becomes the SGR backdrop for the
/// entire OSC 66 emission — foreground, background, and attributes apply to every line. SGR
/// state is terminal-global so we set it once at the start of <see cref="Emit"/>; the cursor
/// moves between lines don't disturb it.
/// </para>
/// </remarks>
public sealed class SizedTextFragment : IBufferFragment
{
    /// <summary>Pre-split lines of <see cref="Text"/>. Computed at construction time.</summary>
    private readonly string[] _lines;

    /// <summary>Construct a sized-text fragment at the requested sizing, text content, and style.</summary>
    public SizedTextFragment(in TextSizing sizing, string text, in Style style)
    {
        ArgumentNullException.ThrowIfNull(text);
        Sizing = sizing;
        Text = text;
        Style = style;

        // Split on LF. CR alone (no following LF) is left in the line as a literal character —
        // most terminals render it as a backspace, but for OSC 66 it falls through to the
        // payload and Kitty draws whatever character the font has there. CRLF is normalized
        // by dropping the CR before the LF.
        _lines = text.Length == 0
                     ? [""]
                     : text.Replace("\r\n", "\n").Split('\n');
    }

    /// <summary>The OSC 66 metadata block — scale, width, numerator/denominator, alignment.</summary>
    public TextSizing Sizing { get; }

    /// <summary>The text content to render. May contain <c>'\n'</c> line breaks.</summary>
    public string Text { get; }

    /// <summary>The style applied to the whole rendered region.</summary>
    public Style Style { get; }

    /// <summary>The text content split into individual lines, one OSC 66 emission per line.</summary>
    public ReadOnlySpan<string> Lines => _lines;

    /// <inheritdoc/>
    public Size GetSize()
    {
        // Cell footprint per the spec: each cluster occupies a (Scale × Width) block. Scale=0
        // is invalid; treat as 1. Width=0 ("auto") falls back to the cluster's natural width
        // (1 for narrow, 2 for wide). For multi-line text, the bounding rectangle is the
        // widest line by the number of lines, each scale-rows tall.
        int scale = Sizing.Scale == 0 ? 1 : Sizing.Scale;
        int maxLineColumns = 0;

        foreach (var line in _lines)
        {
            int lineColumns;
            if (Sizing.Width == 0)
            {
                // Auto width — sum natural cluster widths.
                lineColumns = GraphemeWidth.StringWidth(line) * scale;
            }
            else
            {
                // Fixed width per cluster — count clusters via StringInfo.
                int clusters = GraphemeWidth.ClusterCount(line);
                lineColumns = clusters * Sizing.Width * scale;
            }

            if (lineColumns > maxLineColumns)
                maxLineColumns = lineColumns;
        }

        return new Size(maxLineColumns, _lines.Length * scale);
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

        if (needsWidth && !capabilities.TextSizing.Width) return false;
        if (needsScale && !capabilities.TextSizing.Scale) return false;

        // If neither sub-feature is exercised, the fragment renders identically to plain text, and
        // a regular MonospaceFont would be the better choice.
        return needsScale || needsWidth;
    }

    /// <inheritdoc/>
    public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(capabilities);

        // Style is emitted as an absolute SGR (rather than a delta from whatever was active)
        // because the renderer's bracketing emits SGR-reset after our DECRC; there's no
        // continuity to preserve. One emission covers all lines — SGR is terminal-global and
        // persists across the cursor moves between lines.
        if (Style != Style.Default)
            SgrEncoder.WriteAbsolute(output, Style);

        int scale = Sizing.Scale == 0 ? 1 : Sizing.Scale;

        for (int i = 0; i < _lines.Length; i++)
        {
            // For lines after the first, move the cursor to the start of the next row band
            // (anchor column, anchor row + i × scale). OSC 66 advances the cursor
            // horizontally by the rendered width; vertical advancement is undefined across
            // line breaks within a single OSC 66 payload, so we emit one OSC 66 per line and
            // CUP explicitly between them.
            if (i > 0)
                CursorWriter.WriteMoveTo(output, column, row + i * scale);

            TextSizingWriter.WriteSplit(output, Sizing, _lines[i]);
        }
    }
}
