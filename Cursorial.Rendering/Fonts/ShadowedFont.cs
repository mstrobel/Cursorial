using Cursorial.Output;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// A decorator that wraps any <see cref="IGlyphFont"/> and paints an offset shadow under each
/// glyph before the foreground cells. The shadow uses a separate <see cref="Style"/> so a
/// caller can dim or recolor it independently of the glyph itself; the underlying font does
/// all the actual layout work — this wrapper just paints twice with different anchors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Compositing.</b> The shadow goes down first; its style flows through the cell buffer's
/// active blending mode like any other <see cref="CellBuffer.Set"/> call. Then the foreground
/// glyph is painted over the same region with its own style. Overlapping cells (where the
/// shadow and foreground both light up) get the foreground style; cells touched only by the
/// shadow keep the shadow style. The most common configuration is a low-alpha or dark-tone
/// shadow color, so the unique shadow cells look like a real drop-shadow.
/// </para>
/// <para>
/// <b>Measure.</b> Reported size includes the shadow offset — a text rendered with a (1,1)
/// shadow occupies one extra column and one extra row beyond what the underlying font
/// reports. This matters for layout code that's allocating space for the painted region.
/// </para>
/// </remarks>
public sealed class ShadowedFont : IGlyphFont
{
    private const TextAttributes ForbiddenAttributes = TextAttributes.Inverse;

    private const TextAttributes ForbiddenShadowAttributes = TextAttributes.Inverse |
                                                             TextAttributes.Overline;

    private readonly IBlendingMode? _shadowBlendingMode;

    /// <summary>Construct a shadow-decorated font.</summary>
    /// <param name="inner">The underlying font that lays down the glyph cells.</param>
    /// <param name="offset">Shadow displacement in cells. Default is (1, 1) — one cell right, one cell down.</param>
    /// <param name="shadowStyle">Style applied to the shadow pass. Caller-supplied; typical values use the same foreground as the glyph but with low alpha, or a darker tone.</param>
    /// <param name="shadowBlendingMode">The blending mode to use when applying the shadow. Defaults to <see cref="BlendingModes.Default"/>.</param>
    public ShadowedFont(IGlyphFont inner, (int Columns, int Rows) offset = default, in Style shadowStyle = default, IBlendingMode? shadowBlendingMode = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        Inner = inner;
        Offset = offset == default ? (1, 1) : offset;
        ShadowStyle = EnsureCompatibleShadowStyle(shadowStyle.IsDefault ? Style.DefaultShadow : shadowStyle);

        _shadowBlendingMode = shadowBlendingMode;
    }

    /// <summary>The default shadowed font, with a 1-cell offset and default shadow style.</summary>
    public static ShadowedFont Default { get; } = new(MonospaceFont.Default,
                                                      shadowStyle: Style.DefaultShadow,
                                                      shadowBlendingMode: BlendingModes.Multiply);

    /// <summary>The underlying font that produces glyph cell patterns.</summary>
    public IGlyphFont Inner { get; }

    /// <summary>Shadow displacement in cells. The shadow paints at <c>(row + offset.Rows, column + offset.Columns)</c>.</summary>
    public (int Columns, int Rows) Offset { get; }

    /// <summary>Style applied to the shadow pass.</summary>
    public Style ShadowStyle { get; }

    /// <summary>The blending mode to use when applying the shadow. Defaults to <see cref="BlendingModes.Default"/>.</summary>
    public IBlendingMode ShadowBlendingMode => _shadowBlendingMode ?? BlendingModes.Default;

    /// <inheritdoc/>
    public Style EnsureCompatibleStyle(in Style style) 
        => style with { Attributes = style.Attributes & ~ForbiddenAttributes };

    private Style EnsureCompatibleShadowStyle(in Style style) 
        => style with
           {
               Attributes = style.Attributes & ~ForbiddenShadowAttributes,
               UnderlineColor = style.Foreground,
               Background = Color.Transparent
           };

    /// <inheritdoc/>
    public Size Measure(ReadOnlySpan<char> text)
    {
        var inner = Inner.Measure(text);
        if (inner.IsEmpty) return Size.Empty;

        return new Size(inner.Columns + Math.Abs(Offset.Columns),
                        inner.Rows + Math.Abs(Offset.Rows));
    }

    /// <inheritdoc/>
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in Style style)
    {
        if (buffer.IsEmpty || text.IsEmpty) return Size.Empty;

        var shadowStyle = ShadowStyle;
        var blendingMode = _shadowBlendingMode ?? (shadowStyle == Style.DefaultShadow ? BlendingModes.Multiply : BlendingModes.Default);
        var pushBlendingMode = buffer.CurrentBlendingMode != blendingMode;

        // Paint the shadow first, then the glyph. The buffer's active blending mode applies to
        // both — shadow cells composite against whatever was underneath, glyph cells composite
        // against the shadow (and the original backdrop in the cells the shadow didn't touch).
        if (pushBlendingMode)
            buffer.PushBlendingMode(blendingMode);

        try
        {
            var effectiveShadowStyle = shadowStyle.WithUnderlineStyle(style.UnderlineStyle)
                                                  .WithAttributes((shadowStyle.Attributes | style.Attributes) & ~ForbiddenShadowAttributes)
                                                  .BlendOver(style);

            Inner.Paint(buffer, column + Offset.Columns, row + Offset.Rows, text, effectiveShadowStyle);
        }
        finally
        {
            if (pushBlendingMode)
                buffer.PopBlendingMode();
        }

        var painted = Inner.Paint(buffer, column, row, text, style);
        if (painted.IsEmpty)
            return Size.Empty;

        return new Size(painted.Columns + Math.Abs(Offset.Columns),
                        painted.Rows + Math.Abs(Offset.Rows));
    }
}