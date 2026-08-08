using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Rendering.Fonts;

/// <summary>
/// A decorator that wraps any <see cref="IGlyphFont"/> and paints an offset shadow under each
/// glyph before the foreground cells. The shadow is a <see cref="PartialStyle"/> over the glyph's own
/// style, so a caller can dim or recolor it independently while everything it says nothing about —
/// the underline shape, the attribute flags it does not name — stays the glyph's. The underlying font
/// does all the actual layout work; this wrapper just paints twice with different anchors.
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

    /// <summary>Construct a shadow-decorated font from a whole <see cref="CellStyle"/>.</summary>
    /// <param name="inner">The underlying font that lays down the glyph cells.</param>
    /// <param name="offset">Shadow displacement in cells. Default is (1, 1) — one cell right, one cell down.</param>
    /// <param name="shadowStyle">
    /// Style applied to the shadow pass. Typical values use the same foreground as the glyph but with low
    /// alpha, or a darker tone. Only the channels a shadow can HAVE an opinion about survive — see
    /// <see cref="AsShadowDelta"/> — so prefer the <see cref="PartialStyle"/> overload, which says the same
    /// thing without the channels this one has to discard.
    /// </param>
    /// <param name="shadowBlendingMode">The blending mode to use when applying the shadow. Defaults to <see cref="BlendingModes.Default"/> — a whole style cannot carry one.</param>
    /// <param name="displayName">The display name to use when describing this font to a user.</param>
    public ShadowedFont(IGlyphFont inner, (int Columns, int Rows) offset = default, in CellStyle shadowStyle = default, IBlendingMode? shadowBlendingMode = null, string? displayName = null)
        : this(inner,
               offset,
               shadowStyle.IsDefault ? PartialStyle.DefaultShadow : AsShadowDelta(shadowStyle),
               shadowBlendingMode,
               displayName)
    {
    }

    /// <summary>Construct a shadow-decorated font from a shadow DELTA.</summary>
    /// <param name="inner">The underlying font that lays down the glyph cells.</param>
    /// <param name="offset">Shadow displacement in cells. Pass <c>default</c> for (1, 1) — one cell right, one cell down.</param>
    /// <param name="shadowStyle">
    /// The delta the shadow pass lays over the glyph's own style. Channels it states are the shadow's;
    /// channels it omits are the glyph's, which is how a shadow keeps the run's underline shape and
    /// attributes without restating them. Its <see cref="PartialStyle.Mode"/>, if any, is the mode the
    /// shadow composites under.
    /// </param>
    /// <param name="shadowBlendingMode">Overrides <paramref name="shadowStyle"/>'s own <see cref="PartialStyle.Mode"/>.</param>
    /// <param name="displayName">The display name to use when describing this font to a user.</param>
    public ShadowedFont(IGlyphFont inner, (int Columns, int Rows) offset, in PartialStyle shadowStyle, IBlendingMode? shadowBlendingMode = null, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        DisplayName = displayName ?? $"{inner.DisplayName} (Shadowed)";
        Inner = inner;
        Offset = offset == default ? (1, 1) : offset;
        ShadowStyle = EnsureCompatibleShadowStyle(shadowStyle);
        ShadowBlendingMode = shadowBlendingMode ?? ShadowStyle.Mode ?? BlendingModes.Default;
    }

    /// <summary>The default shadowed font, with a 1-cell offset and default shadow style.</summary>
    /// <remarks>
    /// The blending mode is not restated here because <see cref="PartialStyle.DefaultShadow"/> carries it.
    /// It used to have to be: the shadow's mode lived nowhere, so the paint recovered it by comparing the
    /// style it held against the default constant, and this had to pass the same constant AND the mode to
    /// stay honest about which one it wanted.
    /// </remarks>
    public static ShadowedFont Default { get; } = new(MonospaceFont.Default);

    /// <summary>The underlying font that produces glyph cell patterns.</summary>
    public IGlyphFont Inner { get; }

    /// <summary>Shadow displacement in cells. The shadow paints at <c>(row + offset.Rows, column + offset.Columns)</c>.</summary>
    public (int Columns, int Rows) Offset { get; }

    /// <summary>The delta the shadow pass lays over the glyph's style.</summary>
    public PartialStyle ShadowStyle { get; }

    /// <summary>
    /// The blending mode the shadow composites under — the constructor's override if one was given, else
    /// <see cref="ShadowStyle"/>'s own <see cref="PartialStyle.Mode"/>, else <see cref="BlendingModes.Default"/>.
    /// </summary>
    public IBlendingMode ShadowBlendingMode { get; }

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

    /// <summary>
    /// The face's own compatibility policy, still applied — a shadow may not INVERSE (it would light up
    /// the cells it is supposed to darken) and may not OVERLINE (the rule would run above the glyph it is
    /// cast from). As a delta this forces both off over whatever base the shadow lands on, which covers
    /// the caller's flags and the glyph's in one act; the paint used to mask a hand-built union instead.
    /// </summary>
    private static PartialStyle EnsureCompatibleShadowStyle(in PartialStyle shadow)
        => shadow.Then(PartialStyle.WithRemoved(ForbiddenShadowAttributes));

    /// <summary>
    /// The delta a whole-<see cref="CellStyle"/> shadow means. Deliberately NOT
    /// <see cref="PartialStyle.From"/>: that states every channel, and the three this drops are exactly the
    /// ones the old whole-style shadow could not help stating and its consumer then had to undo.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The BACKGROUND is discarded for <see cref="Color.Transparent"/>: a shadow never fills.</item>
    /// <item>The UNDERLINE COLOUR is discarded for the foreground: an underline in the shadow pass is part
    /// of the shadow, so it is shadow-coloured.</item>
    /// <item>The UNDERLINE SHAPE is discarded outright, becoming absent — the glyph's shape is the one a
    /// shadow of that glyph should draw.</item>
    /// </list>
    /// </remarks>
    private static PartialStyle AsShadowDelta(in CellStyle style) => new()
    {
        Foreground     = style.Foreground,
        UnderlineColor = style.Foreground,
        Background     = Color.Transparent,

        // Absent, not empty: a whole CellStyle cannot distinguish "no link" from "no opinion", so an
        // empty one has to mean the latter — the same conflation PartialStyle.From documents.
        Hyperlink      = style.Hyperlink.IsEmpty ? null : style.Hyperlink,

        // The attribute word of a shadow style means "these IN ADDITION to the glyph's", which is the
        // union the paint used to spell by hand. `Clear = Xor = w` is that union in the delta's encoding
        // — `(b & ~w) ^ w == b | w` — and it is why this cannot route through `Applying`, whose per-axis
        // guard would also impose the axes the word says nothing about.
        Clear          = style.Attributes,
        Xor            = style.Attributes,
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
    /// <remarks>
    /// <b>The box is settled HERE, once, for the whole decorated footprint</b> — shadow rows included —
    /// and both passes then go inward as stamps. Forwarding the caller's background instead would have
    /// the inner face fill the GLYPH's box a second time, after the shadow had already been laid into
    /// it: a drop shadow surviving only in the offset fringe, erased everywhere it was meant to show
    /// through the glyph's holes. A shadow never owns gaps; that is what makes it a shadow.
    /// </remarks>
    public Size Paint(in CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in PartialStyle style)
    {
        if (buffer.IsEmpty || text.IsEmpty) return Size.Empty;

        var ink = GlyphPaint.Ink(buffer, column, row, Measure(text), style);

        return PaintCore(buffer, column, row, text, default, delta: null, bounds: default, ink);
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
        return PaintCore(buffer, column, row, text, baseStyle, delta, bounds, ink: null);
    }

    // Exactly one of `delta` and `ink` is non-null; `style` is the base the FORMER folds onto.
    private Size PaintCore(CellBufferView buffer, int column, int row, ReadOnlySpan<char> text, in CellStyle style,
                           in StyleDeltaTemplate? delta, in Rect bounds, in PartialStyle? ink)
    {
        if (buffer.IsEmpty || text.IsEmpty) return Size.Empty;

        var blendingMode = ShadowBlendingMode;
        var pushBlendingMode = buffer.CurrentBlendingMode != blendingMode;

        // Paint the shadow first, then the glyph. The buffer's active blending mode applies to
        // both — shadow cells composite against whatever was underneath, glyph cells composite
        // against the shadow (and the original backdrop in the cells the shadow didn't touch).
        if (pushBlendingMode)
            buffer.PushBlendingMode(blendingMode);

        try
        {
            // The shadow is a single pass, so it resolves the template ONCE — at the anchor, like the
            // interface's own default template overload — and folds it onto the caller's base. The flat
            // path has no base of its own, so the shadow is derived from the channels the delta STATES,
            // over the ground style; the channels it declines to state are, by definition, ones the
            // caller had no shadow-worthy opinion about either.
            var baseStyle = ink is { } i    ? i.ApplyTo(CellStyle.Default)
                          : delta is null   ? style
                                            : delta.Value.Resolve(column, row, bounds).ApplyTo(style);

            // TWO operations, deliberately not one. The delta DESCRIBES the shadow — the channels it states
            // are the shadow's, the ones it omits (the underline shape, every attribute axis outside its own
            // word) are the glyph's, which is what retired the pair of hand-patches that used to copy those
            // two back off the base a channel at a time before this line could run.
            //
            // `BlendOver` then COMPOSITES, and `ApplyTo` cannot be asked to do it in the same breath: ApplyTo
            // combines a colour with the base's SAME channel, whereas a translucent shadow combines with what
            // is physically behind it, and on a terminal cell that is the BACKGROUND. Foreground-over-
            // background is the whole arithmetic of a shadow; foreground-over-foreground would be a tint.
            //
            // The delta's Mode is not ApplyTo's to consume for the same reason — it says how the shadow meets
            // the backdrop, which is the BUFFER's blend (pushed above), not a per-channel replace. So the
            // description is applied flat, and the mode is already on its way to the cells.
            var effectiveShadowStyle = (ShadowStyle with { Mode = null }).ApplyTo(baseStyle)
                                                                        .BlendOver(baseStyle);

            // FromInk, never From: the shadow stamps. See the flat overload's remarks.
            Inner.Paint(buffer, column + Offset.Columns, row + Offset.Rows, text,
                        PartialStyle.FromInk(effectiveShadowStyle));
        }
        finally
        {
            if (pushBlendingMode)
                buffer.PopBlendingMode();
        }

        var painted = ink is { } inkDelta
                          ? Inner.Paint(buffer, column, row, text, in inkDelta)
                          : Inner.Paint(buffer, column, row, text, in style, delta!.Value, in bounds);

        if (painted.IsEmpty)
            return Size.Empty;

        return new Size(painted.Columns + Math.Abs(Offset.Columns),
                        painted.Rows + Math.Abs(Offset.Rows));
    }
}