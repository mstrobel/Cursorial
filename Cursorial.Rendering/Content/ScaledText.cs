using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Text;

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
public sealed class ScaledText : FragmentContent
{
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

    /// <summary>
    /// An optional brush resolver to use for the primary and fallback placeholder content. Especially useful
    /// for figlet font fallback if painted with brush resources or gradient brushes.
    /// </summary>
    public BrushedTextResolver? BrushResolver { get; set; }

    protected internal override bool IsFragmentNeeded(in CellBufferView buffer, Size availableSpace, in Style style,
                                                      OutputCapabilities? capabilities = null)
    {
        return base.IsFragmentNeeded(in buffer, availableSpace, style, capabilities) ||
               ExistingFragment is not SizedTextFragment { Style: var existingStyle } ||
               existingStyle != style;
    }

    protected override Size MeasureOverride(Size availableSpace, OutputCapabilities capabilities, out bool canCreateFragment)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var text = Text;

        // OSC 66 path: scaled glyphs at Sizing.Scale × text-width × 1 row. Wrap if it wouldn't fit.
        var probeFragment = new SizedTextFragment(Sizing, text, Style.Default);
        if (probeFragment.IsSupported(capabilities))
        {
            canCreateFragment = true;
            return probeFragment.GetSize().ClampTo(availableSpace);
        }

        canCreateFragment = false;

        // Fallback-font path: ask the font what footprint the text wants, PER LINE — the
        // formatter now hands multi-line text through (its wrap points), and a single Measure
        // over the joined string would count the '\n' characters as glyphs and claim one row.
        // If the font's width doesn't fit, monospace fallback kicks in at Paint and each line's
        // footprint becomes (line length, 1).
        int columns = 0, rows = 0, monospaceColumns = 0;

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var fontMeasure = FallbackFont.Measure(line);
            columns = Math.Max(columns, fontMeasure.Columns);
            rows += Math.Max(1, fontMeasure.Rows);
            monospaceColumns = Math.Max(monospaceColumns, line.Length);
        }

        if (columns <= availableSpace.Columns)
            return new Size(columns, Math.Max(1, rows));

        return new Size(Math.Min(monospaceColumns, availableSpace.Columns), Math.Max(1, rows));
    }

    protected override IContent BuildPlaceholder(Size size, OutputCapabilities capabilities, in Style style)
    {
        var rtb = new RichTextBuilder(/*style*/);

        var alignment = Sizing.Horizontal switch
                        {
                            TextSizingHorizontalAlignment.Right  => TextAlignment.Right,
                            TextSizingHorizontalAlignment.Center => TextAlignment.Center,
                            _                                    => TextAlignment.Left
                        };

        var rt = rtb.Figlet(Text, FallbackFont).Build();
        var tf = new TextFormatter { Alignment = alignment, Trim = TextTrimming.None };
        var ft = tf.Format(rt, size.Columns, maxRows: null, capabilities);

        return ft;
    }

    protected override Rect PaintPlaceholder(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (buffer.IsEmpty) return bounds.WithSize(Size.Empty);
        
        var placeholderSize = DesiredSize ?? bounds.Size;

        RealizedPlaceholder ??= BuildPlaceholder(placeholderSize, capabilities, style);
        
        if (RealizedPlaceholder is FormattedText ft)
            return ft.Paint(buffer, new Rect(bounds.Position, placeholderSize), capabilities, BrushResolver);

        if (RealizedPlaceholder is {} p)
            return p.Paint(buffer, new Rect(bounds.Position, placeholderSize), style, capabilities);
        
        return bounds;
    }

    protected override IBufferFragment? CreateFragment(in CellBufferView buffer, in Rect bounds, in Style style,
                                                       OutputCapabilities capabilities)
    {
        var fragment = new SizedTextFragment(Sizing, Text, style);
        if (fragment.IsSupported(capabilities))
            return fragment;

        return null;
    }

    internal static IGlyphFont PickDefaultFallback(TextSizing sizing, bool isMultiLine = false)
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