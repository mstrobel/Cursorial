namespace Cursorial.UI;

/// <summary>The sizing mode of a <see cref="GridLength"/> (design doc §5.4).</summary>
public enum GridUnitType : byte
{
    /// <summary>A fixed number of cells.</summary>
    Cell,

    /// <summary>Sized to content (the maximum desired size of the track's non-spanning children).</summary>
    Auto,

    /// <summary>A weighted share of the space remaining after Cell and Auto tracks (the pinned integer Hamilton distribution).</summary>
    Star,
}

/// <summary>
/// The length of a <see cref="Grid"/> track (design doc §5.4): a fixed cell count
/// (<see cref="GridUnitType.Cell"/>), content-sized (<see cref="GridUnitType.Auto"/>), or a
/// weighted share of the remainder (<see cref="GridUnitType.Star"/>). An <see langword="int"/>
/// converts implicitly to a fixed length (<c>12</c> ⇒ 12 cells). A star track under an unbounded
/// measure constraint behaves as Auto (the WPF rule).
/// </summary>
/// <param name="Cells">The fixed cell count; meaningful only for <see cref="GridUnitType.Cell"/>.</param>
/// <param name="UnitType">The sizing mode.</param>
/// <param name="StarWeight">The proportional weight; meaningful only for <see cref="GridUnitType.Star"/>.
/// The <see cref="Star"/> factory validates eagerly (throws on negative / non-finite); this
/// constructor deliberately does not (it must stay <c>with</c>-expression friendly), so values that
/// bypass the factory are absorbed by the <see cref="Grid"/> instead — non-finite or negative
/// weights are treated as 0 at distribution time.</param>
public readonly record struct GridLength(int Cells, GridUnitType UnitType, double StarWeight = 1.0)
{
    /// <summary>The content-sized length.</summary>
    public static GridLength Auto { get; } = new(0, GridUnitType.Auto);

    /// <summary>A star length with the given <paramref name="weight"/> (default 1).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> is negative or not finite.</exception>
    public static GridLength Star(double weight = 1.0)
    {
        if (!double.IsFinite(weight) || weight < 0)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "Star weights must be finite and non-negative.");

        return new GridLength(0, GridUnitType.Star, weight);
    }

    /// <summary>A fixed length of <paramref name="cells"/> cells.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cells"/> is negative.</exception>
    public static GridLength FromCells(int cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cells);
        return new GridLength(cells, GridUnitType.Cell);
    }

    /// <summary>Implicit fixed-cell conversion: <c>12</c> ⇒ a fixed 12-cell track.</summary>
    public static implicit operator GridLength(int cells) => FromCells(cells);

    /// <summary>Whether this is a fixed cell length.</summary>
    public bool IsCell => UnitType == GridUnitType.Cell;

    /// <summary>Whether this is a content-sized length.</summary>
    public bool IsAuto => UnitType == GridUnitType.Auto;

    /// <summary>Whether this is a weighted-share length.</summary>
    public bool IsStar => UnitType == GridUnitType.Star;

    /// <inheritdoc/>
    public override string ToString() => UnitType switch
    {
        GridUnitType.Auto => "Auto",
        GridUnitType.Star => $"{StarWeight}*",
        _ => $"{Cells}",
    };
}
