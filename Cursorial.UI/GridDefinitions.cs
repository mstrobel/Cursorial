namespace Cursorial.UI;

/// <summary>
/// The base class for <see cref="ColumnDefinition"/>/<see cref="RowDefinition"/> (spec §2.3).
/// Definitions are owner-wired: adding one to a grid's definition collection sets an owner-grid
/// backpointer (a definition belongs to at most one collection — re-adding elsewhere throws), and
/// every sizing property setter invalidates the owner's measure, so post-attach mutation is fully
/// live (matrix L159).
/// </summary>
public abstract class GridDefinition
{
    private GridLength _length;
    private int _minSize;
    private int _maxSize = LayoutMath.Unbounded;

    private protected GridDefinition(GridLength length) => _length = length;

    /// <summary>The owning grid; set by the definition collection (at most one collection — L159 ②).</summary>
    internal Grid? Owner { get; set; }

    /// <summary>The track size the last arrange resolved (the post-layout readback backing field).</summary>
    internal int ActualSize { get; set; }

    /// <summary>The raw configured length.</summary>
    internal GridLength Length => _length;

    /// <summary>The configured minimum, sanitized to ≥ 0 for layout.</summary>
    internal int MinSize => Math.Max(0, _minSize);

    /// <summary>The configured maximum (Min wins a Min &gt; Max conflict via <see cref="LayoutMath.Clamp"/> — LD1/LD18).</summary>
    internal int MaxSize => _maxSize;

    /// <summary>The track length; equality-gated, invalidates the owner's measure on change.</summary>
    private protected GridLength LengthValue
    {
        get => _length;
        set
        {
            if (_length == value)
                return;

            _length = value;
            Owner?.InvalidateMeasure();
        }
    }

    /// <summary>The minimum track size; equality-gated, invalidates the owner's measure on change.</summary>
    private protected int MinSizeValue
    {
        get => _minSize;
        set
        {
            if (_minSize == value)
                return;

            _minSize = value;
            Owner?.InvalidateMeasure();
        }
    }

    /// <summary>The maximum track size; equality-gated, invalidates the owner's measure on change.</summary>
    private protected int MaxSizeValue
    {
        get => _maxSize;
        set
        {
            if (_maxSize == value)
                return;

            _maxSize = value;
            Owner?.InvalidateMeasure();
        }
    }
}

/// <summary>A <see cref="Grid"/> column track (spec §2.3). Default <see cref="Width"/> is <c>1*</c>.</summary>
public sealed class ColumnDefinition : GridDefinition
{
    /// <summary>Creates a star-sized (<c>1*</c>) column.</summary>
    public ColumnDefinition() : base(GridLength.Star())
    {
    }

    /// <summary>Creates a column with the given <paramref name="width"/>.</summary>
    public ColumnDefinition(GridLength width) : base(width)
    {
    }

    /// <summary>The column's width (default <c>1*</c>); setting it on an owned definition re-layouts live (L159 ①).</summary>
    public GridLength Width { get => LengthValue; set => LengthValue = value; }

    /// <summary>The minimum width in cells (default 0). Min beats Max (LD1).</summary>
    public int MinWidth { get => MinSizeValue; set => MinSizeValue = value; }

    /// <summary>The maximum width in cells (default <see cref="LayoutMath.Unbounded"/>).</summary>
    public int MaxWidth { get => MaxSizeValue; set => MaxSizeValue = value; }

    /// <summary>The resolved width of this column after the owner's last arrange (post-layout readback).</summary>
    public int ActualWidth => ActualSize;
}

/// <summary>A <see cref="Grid"/> row track (spec §2.3). Default <see cref="Height"/> is <c>1*</c>.</summary>
public sealed class RowDefinition : GridDefinition
{
    /// <summary>Creates a star-sized (<c>1*</c>) row.</summary>
    public RowDefinition() : base(GridLength.Star())
    {
    }

    /// <summary>Creates a row with the given <paramref name="height"/>.</summary>
    public RowDefinition(GridLength height) : base(height)
    {
    }

    /// <summary>The row's height (default <c>1*</c>); setting it on an owned definition re-layouts live (L159 ①).</summary>
    public GridLength Height { get => LengthValue; set => LengthValue = value; }

    /// <summary>The minimum height in cells (default 0). Min beats Max (LD1).</summary>
    public int MinHeight { get => MinSizeValue; set => MinSizeValue = value; }

    /// <summary>The maximum height in cells (default <see cref="LayoutMath.Unbounded"/>).</summary>
    public int MaxHeight { get => MaxSizeValue; set => MaxSizeValue = value; }

    /// <summary>The resolved height of this row after the owner's last arrange (post-layout readback).</summary>
    public int ActualHeight => ActualSize;
}
