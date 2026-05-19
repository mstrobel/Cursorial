using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Fragments;

namespace Cursorial.Rendering.Content;

/// <summary>
/// Text rendered at a larger-than-cell size. Picks the rendering path at paint time based on
/// what the terminal supports: OSC 66 text sizing via <see cref="SizedTextFragment"/> when the
/// terminal honors the Kitty text-sizing protocol, falling back to a configured
/// <see cref="IGlyphFont"/> (bundled FIGlet face by default) when it doesn't.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fallback font selection.</b> When no explicit fallback is supplied, one is chosen from
/// the bundled <see cref="FigletFonts"/> based on the requested <see cref="Sizing"/>'s scale —
/// scale 2 maps to <see cref="FigletFonts.Standard"/>, scale 3+ to <see cref="FigletFonts.Big"/>,
/// scale 1 (or a sizing that's effectively normal) to a plain
/// <see cref="MonospaceFont"/>. The mapping is a reasonable default for "make this bigger";
/// callers wanting fine control should pass an explicit <c>fallbackFont</c>.
/// </para>
/// <para>
/// <b>Style.</b> The style is applied uniformly across the painted region. When the OSC 66
/// path fires, it becomes the SGR backdrop for the fragment. When the font path fires, it's
/// passed to each <see cref="CellBuffer.Set"/> call — so the cell buffer's blending stack
/// composes it against whatever's already painted underneath.
/// </para>
/// </remarks>
public sealed class ScaledText : IContent
{
    private int? _lastBufferWidth;
    private bool? _wouldWrap;

    /// <summary>
    /// Construct a scaled-text content. Pass <paramref name="fallbackFont"/> to override the
    /// default sizing → bundled-font mapping.
    /// </summary>
    public ScaledText(string text, in TextSizing sizing = default, IGlyphFont? fallbackFont = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        Text = text;
        Sizing = sizing;

        FallbackFont = fallbackFont ?? PickDefaultFallback(sizing);
    }

    /// <summary>The text to render.</summary>
    public string Text { get; }

    /// <summary>
    /// Requested OSC 66 sizing metadata. Drives both the fragment path and the fallback font
    /// choice when no explicit fallback is supplied.
    /// </summary>
    public TextSizing Sizing { get; }

    /// <summary>The font used when OSC 66 isn't supported.</summary>
    public IGlyphFont FallbackFont { get; }

    /// <inheritdoc/>
    public Size Measure(Size availableSpace, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var text = Text;

        // OSC 66 path: scaled glyphs at Sizing.Scale × text-width × 1 row. Wrap if it wouldn't fit.
        var probeFragment = new SizedTextFragment(Sizing, text, Style.Default);
        if (probeFragment.IsSupported(capabilities) && FallbackFont is not FigletFont)
        {
            int scaledWidth = text.Length * Math.Max((int) Sizing.Scale, 1);
            if (scaledWidth <= availableSpace.Columns)
                return probeFragment.GetSize();
        }

        // Fallback-font path: ask the font what footprint the text wants. If the font's width
        // doesn't fit, monospace fallback kicks in at Paint and the footprint becomes
        // (text.Length, 1).
        var fontMeasure = FallbackFont.Measure(text);
        if (fontMeasure.Columns <= availableSpace.Columns)
            return fontMeasure;

        return new Size(Math.Min(text.Length, availableSpace.Columns), 1);
    }

    /// <inheritdoc/>
    public Rect Paint(CellBuffer buffer, Rect bounds, in Style style, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (buffer.Columns != _lastBufferWidth)
        {
            _lastBufferWidth = buffer.Columns;
            _wouldWrap = null;
        }

        var text = Text;

        // Try the OSC 66 path first.
        var fragment = new SizedTextFragment(Sizing, text, style);
        if (fragment.IsSupported(capabilities) && FallbackFont is not FigletFont)
        {
            _wouldWrap ??= text.Length * Math.Max((int) Sizing.Scale, 1) > bounds.Columns;

            if (_wouldWrap is true)
                goto monospaceFallback;

            buffer.AddFragment(bounds.Column, bounds.Row, fragment, style);
            var size = fragment.GetSize();
            return new Rect(bounds.Column, bounds.Row,
                            Math.Min(size.Columns, bounds.Columns),
                            Math.Min(size.Rows, bounds.Rows));
        }

        _wouldWrap ??= FallbackFont.Measure(text).Columns > bounds.Columns;

        if (_wouldWrap is true)
            goto monospaceFallback;

        // Fall back to cell-grid font rendering.
        var painted = FallbackFont.Paint(buffer, bounds.Column, bounds.Row, text, style);
        return new Rect(bounds.Column, bounds.Row, painted.Columns, painted.Rows);

    monospaceFallback:
        // If the text is too wide for the bounds, use monospace fallback.
        var monoPainted = MonospaceFont.Default.Paint(buffer, bounds.Column, bounds.Row, text, style);
        return new Rect(bounds.Column, bounds.Row, monoPainted.Columns, monoPainted.Rows);
    }

    private static IGlyphFont PickDefaultFallback(TextSizing sizing, bool isMultiLine = false)
    {
        if (isMultiLine)
            return MonospaceFont.Default;

        // Scale 0 / 1 with no width override → plain monospace; the sizing was a no-op anyway.
        // Scale 2 → Quarter-block underlined monospace. 
        // Scale 2 → Standard (6 rows tall, the canonical FIGlet feel).
        // Scale 3+ → Big (8 rows tall, suited to outsized titles).
        int effectiveScale = sizing.Scale == 0 ? 1 : sizing.Scale;

        return effectiveScale switch
               {
                   > 6    => FigletFonts.Big,
                   6      => FigletFonts.Standard,
                   5      => FigletFonts.Small,
                   3 or 4 => FigletFonts.Mini,
                   2      => DecoratedFont.HalfBlockUnderline,
                   _      => MonospaceFont.Default
               };
    }
}