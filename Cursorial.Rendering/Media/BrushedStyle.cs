using System.Diagnostics;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.Rendering.Media;

/// <summary>
/// A <see cref="PartialStyle"/> whose color channels are BRUSHES, resolved per cell. The form an
/// operation is authored in; <see cref="Resolve(int, int, in Rect, in Rect)"/> produces the value
/// form for a given cell.
/// </summary>
/// <remarks>
/// The shapes correspond one-to-one: <see cref="IBrush"/> here where <see cref="PartialStyle"/> has
/// <see cref="Color"/>, and <see langword="null"/> means "absent" in both. Attributes are carried in
/// the same encoding and exposed the same way — as
/// <see cref="AppliedAttributes"/> / <see cref="RemovedAttributes"/> / <see cref="ToggledAttributes"/>,
/// never as the mask pair behind them.
/// </remarks>
public readonly record struct BrushedStyle
{
    public static readonly BrushedStyle Identity = default;

    public IBrush?    Foreground     { get; init; }
    public IBrush?    Background     { get; init; }
    public IBrush?    UnderlineColor { get; init; }
    public Hyperlink? Hyperlink      { get; init; }

    /// <inheritdoc cref="PartialStyle.UnderlineShape"/>
    public UnderlineStyle? UnderlineShape { get; init; }

    public IBlendingMode? Mode { get; init; }

    // Storage, not interface — see PartialStyle. Set through the fluent methods below.
    internal TextAttributes Clear { get; init; }
    internal TextAttributes Xor   { get; init; }

    /// <summary>Attributes this style forces ON, whatever the base had.</summary>
    public TextAttributes AppliedAttributes => Clear & Xor;

    /// <summary>Attributes this style forces OFF, whatever the base had.</summary>
    public TextAttributes RemovedAttributes => Clear & ~Xor;

    /// <summary>Attributes this style INVERTS — on becomes off, off becomes on.</summary>
    public TextAttributes ToggledAttributes => Xor & ~Clear;

    /// <inheritdoc cref="PartialStyle.Weight"/>
    public TextWeight? Weight => Resolved.Weight;

    /// <inheritdoc cref="PartialStyle.Posture"/>
    public TextStyle? Posture => Resolved.Posture;

    // The attribute half of a style is brush-free, so it can be read through a resolved delta
    // rather than duplicating the projections. Color channels resolve to null here, which the
    // attribute projections do not consult.
    [DebuggerHidden]
    private PartialStyle Resolved => new() { Clear = Clear, Xor = Xor, UnderlineShape = UnderlineShape };

    // ---- fluent composition: attributes only, for the reason PartialStyle gives ----

    /// <summary>Copy this style while adjusting only the <see cref="Foreground"/>.</summary>
    public BrushedStyle WithForeground(IBrush? foreground) => this with { Foreground = foreground };

    /// <summary>Copy this style while adjusting only the <see cref="Background"/>.</summary>
    public BrushedStyle WithBackground(IBrush? background) => this with { Background = background };

    /// <summary>Copy this style while adjusting only the <see cref="UnderlineColor"/>.</summary>
    public BrushedStyle WithUnderlineColor(IBrush? underlineColor) => this with { UnderlineColor = underlineColor };

    /// <summary>Copy this style while adjusting only the <see cref="UnderlineShape"/>.</summary>
    public BrushedStyle WithUnderlineShape(UnderlineStyle? shape) => this with { UnderlineShape = shape };

    /// <summary>Copy this style while adjusting only the <see cref="Mode"/>.</summary>
    public BrushedStyle WithMode(IBlendingMode? mode) => this with { Mode = mode };

    /// <summary>Copy this style while adjusting only the <see cref="Hyperlink"/>.</summary>
    public BrushedStyle WithHyperlink(Hyperlink? hyperlink) => this with { Hyperlink = hyperlink };

    /// <summary>Force <paramref name="flags"/> ON in addition to whatever this style already does.</summary>
    public BrushedStyle Applying(TextAttributes flags) => Composed(PartialStyle.WithApplied(flags));

    /// <summary>Force <paramref name="flags"/> UNSET (no opinion) in addition to whatever this style already does.</summary>
    public BrushedStyle Ignoring(TextAttributes flags) => WithFlags(Resolved.Ignoring(flags));

    /// <summary>Force <paramref name="flags"/> to keep their current status while ignoring all other booleans.</summary>
    public BrushedStyle Keeping(TextAttributes flags) => WithFlags(Resolved.Ignoring(PartialStyle.Booleans & ~PartialStyle.Require(flags)));

    /// <summary>Force <paramref name="flags"/> to IGNORED (no opinion) with all others booleans keeping their current status.</summary>
    public BrushedStyle Dropping(TextAttributes flags) => WithFlags(Resolved.Ignoring(PartialStyle.Booleans & ~PartialStyle.Require(flags)));

    /// <summary>Force <paramref name="flags"/> OFF in addition to whatever this style already does.</summary>
    public BrushedStyle Removing(TextAttributes flags) => Composed(PartialStyle.WithRemoved(flags));

    /// <summary>INVERT <paramref name="flags"/> in addition to whatever this style already does.</summary>
    public BrushedStyle Toggling(TextAttributes flags) => Composed(PartialStyle.WithToggled(flags));

    /// <summary>Impose a weight in addition to whatever this style already does.</summary>
    public BrushedStyle Weighing(TextWeight w) => Composed(PartialStyle.Weighted(w));

    /// <summary>Impose a posture in addition to whatever this style already does.</summary>
    public BrushedStyle Posturing(TextStyle p) => Composed(PartialStyle.Postured(p));

    /// <summary>Add an underline in addition to whatever this delta already does.</summary>
    public BrushedStyle Underlining(UnderlineStyle? shape = null, IBrush? color = null)
        => Composed(PartialStyle.WithUnderline()) with
           {
               UnderlineShape = shape ?? UnderlineShape,
               UnderlineColor = color ?? UnderlineColor,
           };

    /// <summary>Remove the underline in addition to whatever this style already does.</summary>
    public BrushedStyle RemovingUnderline() =>
        Composed(PartialStyle.WithoutUnderline()) with { UnderlineShape = null };

    /// <summary>
    /// Fold a flag WORD onto this style <b>per axis</b> — one implementation, shared by the fill
    /// primitives' colour-plus-attributes overloads, the paint preference handed to
    /// <c>DrawingContext.DrawFormattedText</c>, and the element-attribute leg of
    /// <c>DrawingContext.CreateBrushResolver</c>.
    /// </summary>
    /// <param name="attributes">The flag word. <see langword="default"/> is the identity.</param>
    /// <param name="underlineShape">
    /// The shape the Underline bit carries — the destination's own, re-stated, since the flag has no
    /// "on, and no further opinion" form of its own.
    /// </param>
    /// <remarks>
    /// <para>
    /// Unioning the word in wholesale is not merely inconvenient, it is unavailable:
    /// <see cref="Applying"/> routes through <c>PartialStyle.Require</c>, which THROWS
    /// for Bold / Faint / Italic / Underline because each owns an axis. The unguarded sibling
    /// (<c>WithAdded</c>) was deleted deliberately — it existed to do the forbidden thing, and the state
    /// it bought is not renderable.
    /// </para>
    /// <para>
    /// Weight is why. Bold and Faint share the SGR 22 reset, so a cell carrying both is not "two
    /// attributes" — it is a state the encoder cannot spell: reaching it emits ESC[1m from a Faint
    /// predecessor and ESC[2m from a Bold one, and the terminal keeps whichever arrived last.
    /// (<c>PartialStyle.Weight</c> reports Bold for it either way, so the accessor and the frame disagree
    /// in silence.) An imposed Bold therefore CLEARS the Faint underneath rather than unioning into
    /// something unrenderable.
    /// </para>
    /// </remarks>
    public BrushedStyle Imposing(TextAttributes attributes, UnderlineStyle underlineShape = UnderlineStyle.Single)
    {
        if (attributes == default) return this;

        var newStyle = this;

        if ((attributes & TextAttributes.Bold) != 0)
            // A word carrying BOTH is already malformed — TextElement.ComposeAttributes cannot produce
            // one, but a caller that ORs its own flags onto the composed word can. Bold wins: the choice
            // is arbitrary, being FIXED is not, since the alternative is a cell whose appearance depends
            // on what was painted before it.
            newStyle = newStyle.Weighing(TextWeight.Bold);
        else if ((attributes & TextAttributes.Faint) != 0)
            newStyle = newStyle.Weighing(TextWeight.Faint);

        // Italic's axis is one-sided — nothing shares its reset — so imposing it and unioning it are the
        // same operation, and the axis-typed form is the one that says which axis it is.
        if ((attributes & TextAttributes.Italic) != 0)
            newStyle = newStyle.Posturing(TextStyle.Italic);

        // The genuine booleans keep unioning: no reset is shared, so an existing Overline survives an
        // imposed Strikethrough. `Applying` is the union for flags that have no axis of their own.
        var booleans = attributes & PartialStyle.Booleans;
        if (booleans != default)
            newStyle = newStyle.Applying(booleans);

        // Underline's axis has no "on, and no further opinion" form — the flag travels with a SHAPE,
        // which implies it once resolved. Re-stating a shape is that form: the underline arrives and the
        // shape is left exactly where it was, which is what the flag-word union did. A shape the brushed
        // style already states wins, since that is an opinion the word does not have.
        if ((attributes & TextAttributes.Underline) != 0)
            newStyle = newStyle with { UnderlineShape = newStyle.UnderlineShape ?? underlineShape };

        return newStyle;
    }


    /// <summary>
    /// Composes this delta with the attributes of the provided <see cref="PartialStyle"/>.
    /// </summary>
    /// <remarks>
    /// One implementation of the attribute algebra, borrowed from PartialStyle rather than repeated.
    /// </remarks>
    public BrushedStyle Composed(in PartialStyle op)
    {
        var folded = Resolved.Then(op);
        return this with { Clear = folded.Clear, Xor = folded.Xor };
    }

    private BrushedStyle WithFlags(in PartialStyle op)
    {
        return this with { Clear = op.Clear, Xor = op.Xor };
    }

    /// <summary>
    /// This delta, then <paramref name="next"/> — one delta equivalent to applying both in order.
    /// The attribute algebra composes exactly: <c>Clear = C₁ | C₂</c>, <c>Xor = (X₁ &amp; ~C₂) ^ X₂</c>.
    /// </summary>
    /// <remarks>
    /// Law: <c>a.Then(b).Resolved</c> ≡ <c>b.Resolved.ApplyTo(a.Resolved)</c>, for every <c>a</c>,
    /// <c>b</c>, <c>s</c>.
    /// </remarks>
    public BrushedStyle Then(in BrushedStyle next)
    {
        return new BrushedStyle
               {
                   Foreground = next.Foreground ?? Foreground,
                   Background = next.Background ?? Background,
                   UnderlineColor = next.UnderlineColor ?? UnderlineColor,
                   Hyperlink = next.Hyperlink ?? Hyperlink,
                   // NOT strictly `next.UnderlineShape ?? UnderlineShape`. A null shape is ambiguous on its own —
                   // it means "no opinion" for a delta that leaves the underline alone, and "definitely no shape"
                   // for one that REMOVES it. Check for removal, and if removed, coalesce to null. Otherwise, apply
                   // `next.UnderlineShape ?? UnderlineShape`. Makes chains like `Underlining(Curly).Underlining()`
                   // coherent without leaving remnants on `Underlining(Curly).RemoveUnderlining()`.
                   // ...and the fallback is only THIS style's shape when this one did not itself remove
                   // the underline. A removal RESETS the shape, so a later shapeless add inherits that reset
                   // value rather than the base's — falling back unconditionally would let the base's shape
                   // survive an intermediate removal, which application-in-order does not do.
                   UnderlineShape = RemovesUnderline(next)
                                        ? null
                                        : next.UnderlineShape
                                          ?? (RemovesUnderline(this) ? default(UnderlineStyle) : UnderlineShape),
                   Clear = Clear | next.Clear,
                   Xor = (Xor & ~next.Clear) ^ next.Xor,
                   Mode = next.Mode ?? Mode
               };

        bool RemovesUnderline(in BrushedStyle t) => t.RemovedAttributes.HasFlag(TextAttributes.Underline);
    }

    /// <summary>
    /// The brushed form of <see cref="PartialStyle.From"/>: EVERY channel stated, the colours as solid
    /// brushes — so resolving the result at any cell reproduces <paramref name="style"/> whole, and a
    /// stated background selects BOX mode at a glyph paint. The adapter for a caller holding a whole
    /// <see cref="CellStyle"/> base that must survive a paint whose unstated channels fall through to the
    /// destination cells: restate the base under the delta, <c>BrushedStyle.From(base).Then(delta)</c>.
    /// Use <see cref="FromInk"/> for the stamp.
    /// </summary>
    /// <remarks>
    /// The hyperlink caveat is <see cref="PartialStyle.From"/>'s, unchanged: a null
    /// <see cref="CellStyle.Hyperlink"/> is indistinguishable from "no opinion", so the result never
    /// REMOVES a link the destination cell carries.
    /// </remarks>
    public static BrushedStyle From(in CellStyle style)
    {
        // One implementation of the attribute and underline-shape encoding — the flat adapter's — read
        // back out of the delta rather than restated here.
        var delta = PartialStyle.From(style);

        return new BrushedStyle
               {
                   Foreground     = new SolidColorBrush(style.Foreground),
                   Background     = new SolidColorBrush(style.Background),
                   UnderlineColor = new SolidColorBrush(style.UnderlineColor),
                   Hyperlink      = style.Hyperlink,
                   UnderlineShape = delta.UnderlineShape,
                   Clear          = delta.Clear,
                   Xor            = delta.Xor,
               };
    }

    /// <summary>
    /// Lift a resolved <see cref="PartialStyle"/> delta to a carrier: each stated
    /// (non-<see langword="null"/>) colour becomes a <see cref="SolidColorBrush"/>, and the blend
    /// <see cref="PartialStyle.Mode"/>, underline shape, hyperlink and attribute masks carry across; an
    /// absent channel stays absent. Lossless — a <see cref="PartialStyle"/> already spells absence as
    /// <see langword="null"/>, so unlike <see cref="FromStated(in CellStyle)"/> no
    /// <see cref="Cursorial.Media.Color.Default"/>-as-absent sentinel is read, and a STATED
    /// <c>Color.Default</c> survives as a stated-default brush.
    /// </summary>
    public static BrushedStyle From(in PartialStyle delta)
        => new()
        {
            Foreground     = delta.Foreground     is { } fg ? BrushFor(fg) : null,
            Background     = delta.Background     is { } bg ? BrushFor(bg) : null,
            UnderlineColor = delta.UnderlineColor is { } uc ? BrushFor(uc) : null,
            Hyperlink      = delta.Hyperlink,
            UnderlineShape = delta.UnderlineShape,
            Mode           = delta.Mode,
            Clear          = delta.Clear,
            Xor            = delta.Xor
        };

    /// <summary>
    /// <see cref="From(in CellStyle)"/> minus the background: the INK of <paramref name="style"/> and no opinion
    /// about the surrounding cells, which selects STAMP mode at a glyph paint.
    /// </summary>
    public static BrushedStyle FromInk(in CellStyle style) => From(style) with { Background = null };

    /// <summary>
    /// The producer-boundary adapter from a resolved <see cref="CellStyle"/> to the delta it MEANS:
    /// each channel stated iff the style states one. A colour becomes a <see cref="SolidColorBrush"/>
    /// when it is not <see cref="Cursorial.Media.Color.Default"/> and stays ABSENT when it is; the
    /// attribute word folds on per axis — <see cref="Imposing"/> for weight, posture and the booleans,
    /// an outright <see cref="Underlining"/> with the style's shape for a stated underline — rather
    /// than through <see cref="From"/>'s clear-everything mask. Whatever the result declines to
    /// state falls through to what the paint composes underneath — the document default's rung, or the
    /// destination cells — and an absent background still selects STAMP at a glyph paint, a stated one
    /// (Transparent included) BOX.
    /// </summary>
    /// <remarks>
    /// This is where the <see cref="Cursorial.Media.Color.Default"/>-as-absent sentinel is read: once, at
    /// the point the whole style is given up, instead of once per consumer downstream — past this
    /// boundary absence is spelled <see langword="null"/>. Use <see cref="From"/> / <see cref="FromInk"/>
    /// for a caller whose whole style must survive verbatim over arbitrarily-styled cells; the
    /// hyperlink caveat is <see cref="PartialStyle.From"/>'s, unchanged.
    /// </remarks>
    public static BrushedStyle FromStated(in CellStyle style)
    {
        // One implementation of the per-axis attribute fold — Imposing's — except Underline, which the
        // style states OUTRIGHT (flag in the masks plus its shape, as From spells it) rather than as
        // Imposing's shape-implies-flag rider: a stated flag survives composition over a delta that
        // removed the underline, where an implied one would die as the removal's remnant.
        var delta = Identity.Imposing(style.Attributes & ~TextAttributes.Underline);

        if ((style.Attributes & TextAttributes.Underline) != 0)
            delta = delta.Underlining(style.UnderlineStyle);

        return delta with
               {
                   Foreground     = style.Foreground.IsDefault     ? null : new SolidColorBrush(style.Foreground),
                   Background     = style.Background.IsDefault     ? null : new SolidColorBrush(style.Background),
                   UnderlineColor = style.UnderlineColor.IsDefault ? null : new SolidColorBrush(style.UnderlineColor),
                   Hyperlink      = style.Hyperlink,
               };
    }

    /// <summary>
    /// The carrier as a resolved <see cref="CellStyle"/> over an empty base — the boundary form for
    /// resolved-value consumers (the CACHE KEY census, the document-foreground comparison). Exact for a
    /// uniform carrier; a non-uniform one is sampled at its bounds' origin, which is the only answer a
    /// caller with no cell in hand can get.
    /// </summary>
    internal CellStyle ResolveFlat()
        => Resolve(0, 0, new Rect(0, 0, 1, 1)).ApplyTo(CellStyle.Default);

    /// <summary>
    /// True when every present brush is position-independent, so <see cref="Resolve(int,int,in Rect,in Rect)"/>
    /// returns the same value for every cell and a fill loop can hoist it. The common case.
    /// </summary>
    public bool IsUniform =>
        Foreground is null or { IsUniform: true } &&
        Background is null or { IsUniform: true } &&
        UnderlineColor is null or { IsUniform: true };

    /// <summary>True when resolving this produces the identity delta, whatever the cell.</summary>
    /// <remarks>
    /// Equality with <c>default</c> for the same reason as <see cref="PartialStyle.IsIdentity"/>: the
    /// generated comparison covers every field, so one added later cannot be left out.
    /// </remarks>
    public bool IsIdentity => this == default;
    
    /// <summary>
    /// Resolves the style as it should be applied at the specified (<paramref name="column"/>,
    /// <paramref name="row"/>) position. Brushes are sampled at that location relative to the
    /// <paramref name="bounds"/> rectangle.
    /// </summary>
    /// <param name="column">The column/X-axis coordinate at which to sample brushes.</param>
    /// <param name="row">The row/Y-axis coordinate at which to sample brushes.</param>
    /// <param name="bounds">The bounding box defining the brush sampling region.</param>
    /// <returns>
    /// A partial style derived from this style with its brushes resolved at the specified
    /// (<paramref name="column"/>, <paramref name="row"/>) position.
    /// </returns>
    public PartialStyle Resolve(int column, int row, in Rect bounds)
        => Resolve(column, row, in bounds, in bounds);
    
    /// <summary>
    /// Resolves the style as it should be applied at the specified (<paramref name="column"/>,
    /// <paramref name="row"/>) position. Brushes are sampled at that location relative to the
    /// <paramref name="fillBounds"/> or <paramref name="contentBounds"/> rectangle, depending
    /// on whether the brush applies to the entire region (<see cref="Background"/>) or just
    /// the content (<see cref="Foreground"/>, <see cref="UnderlineColor"/>).
    /// </summary>
    /// <param name="column">The column/X-axis coordinate at which to sample brushes.</param>
    /// <param name="row">The row/Y-axis coordinate at which to sample brushes.</param>
    /// <param name="fillBounds">The bounding box defining the background sampling region.</param>
    /// <param name="contentBounds">The bounding box defining the foreground and underline sampling region.</param>
    /// <returns>
    /// A partial style derived from this style with its brushes resolved at the specified
    /// (<paramref name="column"/>, <paramref name="row"/>) position.
    /// </returns>
    public PartialStyle Resolve(int column, int row, in Rect contentBounds, in Rect fillBounds)
    {
        // Nullability carries straight through — an absent brush resolves to an absent color — so
        // Resolve is one object initializer with no presence bookkeeping and no branching. The
        // underline shape needs no special case either: PartialStyle.ApplyTo derives the flag from
        // the shape's presence, and removal is carried by the mask pair, copied verbatim.
        return new PartialStyle
               {
                   Foreground     = Foreground?.ColorAt(column, row, contentBounds),
                   Background     = Background?.ColorAt(column, row, fillBounds),
                   UnderlineColor = UnderlineColor?.ColorAt(column, row, contentBounds),
                   Hyperlink      = Hyperlink,
                   UnderlineShape = UnderlineShape,
                   Clear          = Clear,
                   Xor            = Xor,
                   Mode           = Mode
               };    
    }

    private static IBrush? BrushFor(Color? color)
        => color switch
           {
               null                                                            => null,
               { Kind: ColorKind.Default }                                     => Brushes.Default,
               { Kind: ColorKind.Rgb } c                                       => new SolidColorBrush(c),
               { PaletteIndex: var i } when MarkupColor.IsThemePaletteIndex(i) => SolidColorBrush.FromPalette(i),
               { PaletteIndex: var p }                                         => ColorPalette.Ansi256[p],
           };
}
