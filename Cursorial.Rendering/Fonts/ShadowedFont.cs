using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// A decorator that wraps any <see cref="IGlyphFont"/> and paints an offset shadow under each
/// glyph before the foreground cells. The shadow uses a separate <see cref="CellStyle"/> so a
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
    /// <param name="displayName">The display name to use when describing this font to a user.</param>
    public ShadowedFont(IGlyphFont inner, (int Columns, int Rows) offset = default, in CellStyle shadowStyle = default, IBlendingMode? shadowBlendingMode = null, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        DisplayName = displayName ?? $"{inner.DisplayName} (Shadowed)";
        Inner = inner;
        Offset = offset == default ? (1, 1) : offset;
        ShadowStyle = EnsureCompatibleShadowStyle(shadowStyle.IsDefault ? CellStyle.DefaultShadow : shadowStyle);

        _shadowBlendingMode = shadowBlendingMode;
    }

    /// <summary>The default shadowed font, with a 1-cell offset and default shadow style.</summary>
    public static ShadowedFont Default { get; } = new(MonospaceFont.Default,
                                                      shadowStyle: CellStyle.DefaultShadow,
                                                      shadowBlendingMode: BlendingModes.Multiply);

    /// <summary>The underlying font that produces glyph cell patterns.</summary>
    public IGlyphFont Inner { get; }

    /// <summary>Shadow displacement in cells. The shadow paints at <c>(row + offset.Rows, column + offset.Columns)</c>.</summary>
    public (int Columns, int Rows) Offset { get; }

    /// <summary>Style applied to the shadow pass.</summary>
    public CellStyle ShadowStyle { get; }

    /// <summary>The blending mode to use when applying the shadow. Defaults to <see cref="BlendingModes.Default"/>.</summary>
    public IBlendingMode ShadowBlendingMode => _shadowBlendingMode ?? BlendingModes.Default;

    public string DisplayName { get; }

    /// <inheritdoc/>
    /// <remarks>A decorator's repertoire IS its inner face's — this wrapper adds a shadow pass,
    /// never a glyph. Inheriting the interface's optimistic default would have a shadowed FIGlet
    /// font claim every codepoint while drawing a blank gap for most of them.</remarks>
    public bool HasGlyph(uint codepoint) => Inner.HasGlyph(codepoint);

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>NOT a pure forward — but the baseline half of it is.</b> <see cref="PaintCore"/> paints
    /// the FOREGROUND glyph at the caller's anchor (<c>Inner.Paint(buffer, column, row, …)</c>)
    /// and the shadow one pass earlier at <c>row + Offset.Rows</c>. The glyph therefore does not
    /// move relative to the anchor, so the baseline row does not move either: this is
    /// <c>Inner.Baseline</c> unchanged. The extra row <see cref="Measure"/> reports lands BELOW
    /// the glyph, which is <see cref="Descender"/>'s business, not the baseline's.
    /// </para>
    /// <para>
    /// The mirror-image decorator — one that offsets the glyph DOWN and draws above it, as
    /// <see cref="DecoratedFont"/> does with <see cref="DecorationPosition.Above"/> — shifts the
    /// baseline by exactly that offset. Which is why neither decorator can simply inherit or
    /// blanket-forward these: the answer depends on which side of the glyph the extra rows go.
    /// </para>
    /// </remarks>
    public int Baseline => Inner.Baseline;

    /// <inheritdoc cref="Baseline"/>
    public int Ascender => Baseline;

    /// <summary>
    /// <see cref="Inner"/>'s descent plus the shadow's row offset — the shadow rows hang below
    /// the glyph, and <see cref="Measure"/> already counts them in the reported height.
    /// </summary>
    /// <remarks>
    /// A naive <c>Inner.Descender</c> forward would under-report by <c>|Offset.Rows|</c> and
    /// break the invariant that makes the descent mean anything:
    /// <c>Ascender + Descender == Measure(…).Rows</c>. Nothing in the framework reads this
    /// property directly today — a band's height comes from <see cref="Measure"/> and
    /// baseline-aligned placement from <see cref="Baseline"/> — so the invariant IS the whole
    /// contract here: it is what lets a caller reason about the rows below the baseline (how
    /// deep a band must be to clear this face's ink, say) without re-deriving the shadow's
    /// geometry from <see cref="Offset"/>.
    /// <para>
    /// A NEGATIVE row offset puts the shadow above the anchor instead, outside the box
    /// <see cref="Measure"/> describes (a pre-existing quirk of this decorator's anchoring, left
    /// alone here). <see cref="Math.Abs(int)"/> keeps the descent matched to the reported height
    /// in that case too — over-reserving a row below rather than under-reserving one, which is
    /// the safe direction to be wrong in: extra slack merely goes unused, whereas a short band
    /// clips ink.
    /// </para>
    /// </remarks>
    public int Descender => Inner.Descender + Math.Abs(Offset.Rows);

    /// <inheritdoc/>
    public CellStyle EnsureCompatibleStyle(in CellStyle style)
        => style with { Attributes = style.Attributes & ~ForbiddenAttributes };

    private CellStyle EnsureCompatibleShadowStyle(in CellStyle style) 
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
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in CellStyle style)
    {
        return PaintCore(buffer, column, row, text, style, delta: null, bounds: default);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The base style reaches BOTH passes. It used to reach neither — this overload had no base to pass, so
    /// <see cref="PaintCore"/> got <c>default</c> and the shadow derived its underline shape from it, while
    /// the glyph pass got whatever the caller's callback chose to restate. A delta plus a base makes the two
    /// passes agree by construction.
    /// </remarks>
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text,
                      in CellStyle baseStyle, in StyleDeltaTemplate delta, in Rect bounds)
    {
        return PaintCore(buffer, column, row, text, baseStyle, delta, bounds);
    }

    private Size PaintCore(CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in CellStyle style,
                           in StyleDeltaTemplate? delta, in Rect bounds)
    {
        if (buffer.IsEmpty || text.IsEmpty) return Size.Empty;

        var shadowStyle = ShadowStyle;
        var blendingMode = _shadowBlendingMode ?? (shadowStyle == CellStyle.DefaultShadow ? BlendingModes.Multiply : BlendingModes.Default);
        var pushBlendingMode = buffer.CurrentBlendingMode != blendingMode;

        // Paint the shadow first, then the glyph. The buffer's active blending mode applies to
        // both — shadow cells composite against whatever was underneath, glyph cells composite
        // against the shadow (and the original backdrop in the cells the shadow didn't touch).
        if (pushBlendingMode)
            buffer.PushBlendingMode(blendingMode);

        try
        {
            // The shadow is a single pass, so it resolves the template ONCE — at the anchor, like the
            // interface's own default template overload — and folds it onto the caller's base.
            var baseStyle = delta is null ? style : delta.Value.Resolve(column, row, bounds).ApplyTo(style);

            var effectiveShadowStyle = shadowStyle.WithUnderlineStyle(style.UnderlineStyle)
                                                  .WithAttributes((shadowStyle.Attributes | baseStyle.Attributes) & ~ForbiddenShadowAttributes)
                                                  .BlendOver(baseStyle);

            Inner.Paint(buffer, column + Offset.Columns, row + Offset.Rows, text, effectiveShadowStyle);
        }
        finally
        {
            if (pushBlendingMode)
                buffer.PopBlendingMode();
        }

        var painted = delta is { } d
                          ? Inner.Paint(buffer, column, row, text, in style, in d, in bounds)
                          : Inner.Paint(buffer, column, row, text, in style);

        if (painted.IsEmpty)
            return Size.Empty;

        return new Size(painted.Columns + Math.Abs(Offset.Columns),
                        painted.Rows + Math.Abs(Offset.Rows));
    }
}