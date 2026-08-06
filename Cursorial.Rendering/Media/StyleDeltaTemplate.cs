using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Text;

namespace Cursorial.Rendering.Media;

/// <summary>
/// A <see cref="PartialStyle"/> whose colour channels are BRUSHES, resolved per cell. The form an
/// operation is authored in; <see cref="Resolve"/> produces the value form for a given cell.
/// </summary>
/// <remarks>
/// The shapes correspond one-to-one: <see cref="IBrush"/> here where <see cref="PartialStyle"/> has
/// <see cref="Color"/>, and <see langword="null"/> means "absent" in both. Attributes are carried in
/// the same encoding and exposed the same way — as
/// <see cref="SetAttributes"/> / <see cref="UnsetAttributes"/> / <see cref="ToggledAttributes"/>,
/// never as the mask pair behind them.
/// </remarks>
public readonly record struct StyleDeltaTemplate
{
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

    /// <summary>Attributes this template forces ON, whatever the base had.</summary>
    public TextAttributes SetAttributes => Clear & Xor;

    /// <summary>Attributes this template forces OFF, whatever the base had.</summary>
    public TextAttributes UnsetAttributes => Clear & ~Xor;

    /// <summary>Attributes this template INVERTS — on becomes off, off becomes on.</summary>
    public TextAttributes ToggledAttributes => Xor & ~Clear;

    /// <inheritdoc cref="PartialStyle.Weight"/>
    public TextWeight? Weight => Resolved.Weight;

    /// <inheritdoc cref="PartialStyle.Posture"/>
    public TextStyle? Posture => Resolved.Posture;

    // The attribute half of a template is brush-free, so it can be read through a resolved delta
    // rather than duplicating the projections. Colour channels resolve to null here, which the
    // attribute projections do not consult.
    private PartialStyle Resolved => new() { Clear = Clear, Xor = Xor, UnderlineShape = UnderlineShape };

    // ---- fluent composition: attributes only, for the reason PartialStyle gives ----

    /// <summary>Force <paramref name="flags"/> ON in addition to whatever this template already does.</summary>
    public StyleDeltaTemplate Setting(TextAttributes flags) => Composed(PartialStyle.WithSet(flags));

    /// <summary>Force <paramref name="flags"/> OFF in addition to whatever this template already does.</summary>
    public StyleDeltaTemplate Clearing(TextAttributes flags) => Composed(PartialStyle.WithCleared(flags));

    /// <summary>INVERT <paramref name="flags"/> in addition to whatever this template already does.</summary>
    public StyleDeltaTemplate Toggling(TextAttributes flags) => Composed(PartialStyle.WithToggled(flags));

    /// <summary>Impose a weight in addition to whatever this template already does.</summary>
    public StyleDeltaTemplate Weighing(TextWeight w) => Composed(PartialStyle.Weighted(w));

    /// <summary>Impose a posture in addition to whatever this template already does.</summary>
    public StyleDeltaTemplate Posturing(TextStyle p) => Composed(PartialStyle.Postured(p));

    /// <summary>Remove the underline in addition to whatever this template already does.</summary>
    public StyleDeltaTemplate RemovingUnderline() =>
        Composed(PartialStyle.WithoutUnderline()) with { UnderlineShape = null };

    // One implementation of the attribute algebra, borrowed from PartialStyle rather than repeated.
    private StyleDeltaTemplate Composed(in PartialStyle op)
    {
        var folded = Resolved.Then(op);
        return this with { Clear = folded.Clear, Xor = folded.Xor };
    }

    /// <summary>
    /// True when every present brush is position-independent, so <see cref="Resolve"/> returns the
    /// same value for every cell and a fill loop can hoist it. The common case.
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

    public PartialStyle Resolve(int column, int row, in Rect bounds)
    {
        // Nullability carries straight through — an absent brush resolves to an absent colour — so
        // Resolve is one object initializer with no presence bookkeeping and no branching. The
        // underline shape needs no special case either: PartialStyle.ApplyTo derives the flag from
        // the shape's presence, and removal is carried by the mask pair, copied verbatim.
        return new PartialStyle
               {
                   Foreground     = Foreground?.ColorAt(column, row, bounds),
                   Background     = Background?.ColorAt(column, row, bounds),
                   UnderlineColor = UnderlineColor?.ColorAt(column, row, bounds),
                   Hyperlink      = Hyperlink,
                   UnderlineShape = UnderlineShape,
                   Clear          = Clear,
                   Xor            = Xor,
                   Mode           = Mode,
               };    
    }
}
