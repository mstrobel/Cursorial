using System.Globalization;

namespace Cursorial.UI.DataViews;

/// <summary>The sizing mode of a <see cref="DataGridLength"/>.</summary>
public enum DataGridLengthUnit
{
    /// <summary>A fixed width in terminal cells.</summary>
    Cell,
    /// <summary>Size to content: header caption ∨ widest formatted cell in the CURRENT band (design
    /// doc §1 — never an O(N) sweep; monotonic-grow within a shape so scrolling doesn't jitter).</summary>
    Auto,
    /// <summary>A weighted share of the width remaining after Cell/Auto columns.</summary>
    Star,
}

/// <summary>
/// A DataGrid column width (design doc §1 — the panel-mandated geometry model): fixed cells,
/// <c>Auto</c> (band-limited best fit), or <c>*</c> star shares. The terminal analog of WPF's
/// <c>DataGridLength</c>, in whole cells.
/// </summary>
public readonly struct DataGridLength : IEquatable<DataGridLength>
{
    private DataGridLength(DataGridLengthUnit unit, double value)
    {
        Unit = unit;
        Value = value;
    }

    /// <summary>The sizing mode.</summary>
    public DataGridLengthUnit Unit { get; }

    /// <summary>Cells for <see cref="DataGridLengthUnit.Cell"/>; the weight for Star; unused for Auto.</summary>
    public double Value { get; }

    /// <summary>Size to content within the band (the default for auto-generated columns).</summary>
    public static DataGridLength Auto { get; } = new(DataGridLengthUnit.Auto, 0);

    /// <summary>A fixed width in cells.</summary>
    public static DataGridLength Cells(int cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cells);
        return new DataGridLength(DataGridLengthUnit.Cell, cells);
    }

    /// <summary>A star share (weight ≥ 0; 0 collapses).</summary>
    public static DataGridLength Star(double weight = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        return new DataGridLength(DataGridLengthUnit.Star, weight);
    }

    /// <summary>Parses <c>"12"</c> (cells), <c>"Auto"</c>, <c>"*"</c> / <c>"2*"</c> (the XAML converter's target).</summary>
    public static DataGridLength Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        text = text.Trim();

        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return Auto;
        if (text == "*")
            return Star();
        if (text.EndsWith('*'))
            return Star(double.Parse(text[..^1], CultureInfo.InvariantCulture));
        return Cells(int.Parse(text, CultureInfo.InvariantCulture));
    }

    public bool Equals(DataGridLength other) => Unit == other.Unit && Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is DataGridLength other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Unit, Value);
    public static bool operator ==(DataGridLength left, DataGridLength right) => left.Equals(right);
    public static bool operator !=(DataGridLength left, DataGridLength right) => !left.Equals(right);

    public override string ToString() => Unit switch
    {
        DataGridLengthUnit.Auto => "Auto",
        DataGridLengthUnit.Star => Value == 1 ? "*" : $"{Value.ToString(CultureInfo.InvariantCulture)}*",
        _ => Value.ToString(CultureInfo.InvariantCulture),
    };
}
