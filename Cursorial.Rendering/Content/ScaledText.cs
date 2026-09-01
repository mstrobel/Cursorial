using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

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
/// passed to each <see cref="CellBuffer.Set(int, int, ReadOnlySpan{char}, in CellStyle)"/> call — so the cell buffer's blending stack
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

    protected internal override bool IsCachedFragmentStale(Size availableSpace, in BrushedStyle style,
                                                           OutputCapabilities? capabilities = null)
    {
        // The style is baked into the emission (the OSC 66 SGR backdrop), so a style change —
        // a selection highlight arriving or leaving — must rebuild even at an unchanged size.
        //
        // CACHE KEY: resolved value, never the template. The IContent lane carries a style delta
        // now, so the carrier is resolved at the fragment's anchor BEFORE this comparison — a
        // uniform carrier resolves identically at every cell, which is what lets the anchor
        // resolution take the flat form here, where no anchor is in scope. A carrier whose
        // brushes are not IsUniform has no single resolved value and rebuilds unconditionally.
        return base.IsCachedFragmentStale(availableSpace, style, capabilities) ||
               style.IsUniform is false ||
               ExistingFragment is not SizedTextFragment { Style: var existingStyle } ||
               existingStyle != style.ResolveFlat();
    }

    protected override Size MeasureOverride(Size availableSpace, OutputCapabilities capabilities, out bool canCreateFragment)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var text = Text;

        // OSC 66 path: scaled glyphs at Sizing.Scale × text-width × 1 row. Wrap if it wouldn't fit.
        var probeFragment = new SizedTextFragment(Sizing, text, CellStyle.Default);
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

    protected override IContent BuildPlaceholder(Size size, OutputCapabilities capabilities, in BrushedStyle style)
    {
        var rtb = new RichTextBuilder();

        var alignment = Sizing.Horizontal switch
                        {
                            TextSizingHorizontalAlignment.Right  => TextAlignment.Right,
                            TextSizingHorizontalAlignment.Center => TextAlignment.Center,
                            _                                    => TextAlignment.Left
                        };

        // NoWrap, deliberately: the caller (a formatter piece, an editor segment) has already
        // wrapped — and the default WordWrap's packer DROPS LEADING WHITESPACE, which collapsed
        // the spaces to the right of an editor's caret split and shifted every following glyph
        // left of where the caret math placed it. The STYLE rides the figlet block, so a
        // selection backdrop reaches the glyph cells (it used to be dropped entirely). It rides
        // UNRESOLVED — the carrier becomes the FigletBlock's own carrier, so a non-uniform brush
        // reaches the FIGlet paint arm's per-cell sampling instead of being flattened here.
        var rt = rtb.Figlet(Text, FallbackFont, style).Build();
        var tf = new TextFormatter { Alignment = alignment, Trim = TextTrimming.None, Wrap = WrapMode.NoWrap };
        var ft = tf.Format(rt, size.Columns, maxRows: null, capabilities);

        return ft;
    }

    // CACHE KEY: resolved value, never the template. Compared below to decide whether the
    // realization is stale; same forward rule as IsCachedFragmentStale — the delta resolves at
    // the anchor before the comparison, and !IsUniform rebuilds unconditionally.
    private CellStyle _placeholderStyle;

    // The Mode is not in _placeholderStyle (it is resolved away over Default), so it is tracked
    // separately: a run that changes ONLY its blend mode must still rebuild the realization, which
    // bakes the mode into its FIGlet carrier. Blend modes are singletons, so reference identity suffices.
    private IBlendingMode? _placeholderMode;

    protected override Rect PaintPlaceholder(in CellBufferView buffer, in Rect bounds, in BrushedStyle style, OutputCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        if (buffer.IsEmpty) return bounds.WithSize(Size.Empty);

        var placeholderSize = DesiredSize ?? bounds.Size;

        // The carrier resolved at the piece's anchor — the value seams below (the staleness
        // compare, the uniform backdrop fill) need one CellStyle, and for the uniform carrier
        // every cell resolves to this same value.
        var resolved = style.Resolve(bounds.Column, bounds.Row, bounds).ApplyTo(CellStyle.Default);

        // The realization bakes the style into its glyph runs — a style change (the selection
        // backdrop appearing or moving) must REBUILD, not reuse (the old ??= cached the first
        // style forever, which is why figlet selection highlighting never showed). A non-uniform
        // carrier has no single resolved value to key the realization on, so it rebuilds
        // unconditionally — the forward rule the cache-key census states.
        if (RealizedPlaceholder is null || style.IsUniform is false || _placeholderStyle != resolved ||
            !ReferenceEquals(_placeholderMode, style.Mode))
        {
            RealizedPlaceholder = BuildPlaceholder(placeholderSize, capabilities, style);
            _placeholderStyle = resolved;
            _placeholderMode = style.Mode;
        }

        var rect = new Rect(bounds.Position, placeholderSize);

        // A selection-style backdrop covers the WHOLE piece rect — glyph ink is sparse, and a
        // highlight that only tints ink cells is unreadable. Background-or-attribute styles fill
        // first; the glyphs paint over the fill carrying the same style. A uniform carrier fills
        // with its one resolved value; a non-uniform one resolves per cell over the piece rect.
        if (style.IsUniform)
        {
            if (resolved != CellStyle.Default)
                buffer.ClearCells(rect, resolved);
        }
        else
        {
            for (int r = rect.Row; r < rect.RowEnd; r++)
            for (int c = rect.Column; c < rect.ColumnEnd; c++)
                buffer.ClearCells(new Rect(c, r, 1, 1), style.Resolve(c, r, rect).ApplyTo(CellStyle.Default));
        }

        if (RealizedPlaceholder is FormattedText ft)
            return ft.Paint(buffer, rect, capabilities, BrushResolver);

        if (RealizedPlaceholder is {} p)
            return p.Paint(buffer, rect, style, capabilities);

        return bounds;
    }

    protected override IBufferFragment? CreateFragment(in CellBufferView buffer, in Rect bounds, in BrushedStyle style,
                                                       OutputCapabilities capabilities)
    {
        // The fragment's Style is a CACHE-KEY value and the whole emission's SGR backdrop — one
        // SGR, so one sample: the carrier resolves at the fragment's anchor. (A non-uniform
        // carrier never caches — IsCachedFragmentStale rebuilds it unconditionally — so the
        // anchor sample is a per-paint value, not a stale key.)
        // The Mode is carried, NOT consumed by ApplyTo(Default): it has no operand over the abstract
        // default, and its job is the emitted FOREGROUND — applied at FrameRenderer's StyleOverride-over-
        // AnchorStyle blend, which (the two being equal) is fg over its own background.
        var fragment = new SizedTextFragment(Sizing, Text,
                                             style.Resolve(bounds.Column, bounds.Row, bounds).ApplyTo(CellStyle.Default),
                                             style.Mode);
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