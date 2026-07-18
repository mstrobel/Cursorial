using System.Linq.Expressions;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// One shaped column: the typed key vector (slot-aligned with the row store) plus the compiled kit —
/// getter, key comparison, formatter (design doc §2.2). The non-generic base is what the controller
/// and the compiled multi-column comparer compose over; all hot-path typed state lives in
/// <see cref="ShapedColumn{TRow,TKey}"/> so keys never box.
/// </summary>
internal abstract class ShapedColumn
{
    /// <summary>The column's stable identity (the grid column / consumer key selector it was built from).</summary>
    public required object Identity { get; init; }

    /// <summary>The CLR key type (the getter's return type).</summary>
    public abstract Type KeyType { get; }

    /// <summary>Ensures the key vector covers slots [0, <paramref name="capacity"/>).</summary>
    public abstract void EnsureCapacity(int capacity);

    /// <summary>Extracts + stores the key for <paramref name="slot"/> (boxes only through the untyped row — never the key).</summary>
    public abstract void ExtractKeyUntyped(object row, int slot);

    /// <summary>Compares two slots by this column's key (ascending, nulls first).</summary>
    public abstract int CompareSlots(int a, int b);

    /// <summary>Formats <paramref name="slot"/>'s key for display.</summary>
    public abstract string FormatSlot(int slot);

    /// <summary>The key boxed — cold paths only (group captions, diagnostics).</summary>
    public abstract object? GetKeyBoxed(int slot);

    /// <summary>
    /// Builds the expression comparing this column's keys at slots <paramref name="a"/>/<paramref name="b"/>
    /// (ints), used by <see cref="ShapingCodegen.BuildSlotComparison"/> to fuse all sort levels into one
    /// compiled <see cref="Comparison{T}"/> of int.
    /// </summary>
    internal abstract Expression BuildCompareExpression(ParameterExpression a, ParameterExpression b, bool descending);
}

/// <summary>The typed column (see <see cref="ShapedColumn"/>).</summary>
internal sealed class ShapedColumn<TRow, TKey> : ShapedColumn
{
    // Read via Expression.Field by compiled comparers so vector growth (re-allocation) is always
    // observed — never capture the array itself into a compiled tree.
    internal TKey[] Keys = [];

    private readonly Func<TRow, TKey> _getter;
    private readonly Comparison<TKey> _comparison;
    private readonly Func<TKey, string> _formatter;

    public ShapedColumn(Func<TRow, TKey> getter, Comparison<TKey> comparison, Func<TKey, string> formatter)
    {
        _getter = getter;
        _comparison = comparison;
        _formatter = formatter;
    }

    public override Type KeyType => typeof(TKey);

    public override void EnsureCapacity(int capacity)
    {
        if (Keys.Length < capacity)
            Array.Resize(ref Keys, Math.Max(capacity, Math.Max(16, Keys.Length * 2)));
    }

    /// <summary>The typed extract (the hot path — the untyped override is the INCC boundary).</summary>
    public void ExtractKey(TRow row, int slot) => Keys[slot] = _getter(row);

    public override void ExtractKeyUntyped(object row, int slot) => ExtractKey((TRow)row, slot);

    public override int CompareSlots(int a, int b) => _comparison(Keys[a], Keys[b]);

    public override string FormatSlot(int slot) => _formatter(Keys[slot]);

    public override object? GetKeyBoxed(int slot) => Keys[slot];

    /// <summary>The compiled key comparison (exposed for the repair/aggregate kits).</summary>
    internal Comparison<TKey> KeyComparison => _comparison;

    internal override Expression BuildCompareExpression(ParameterExpression a, ParameterExpression b, bool descending)
    {
        // this.Keys[a|b] — through the field so growth re-allocation is observed.
        var keysField = Expression.Field(Expression.Constant(this), nameof(Keys));
        Expression keyA = Expression.ArrayIndex(keysField, a);
        Expression keyB = Expression.ArrayIndex(keysField, b);

        // Descending = operand swap (never negate a comparison result — int.MinValue).
        if (descending)
            (keyA, keyB) = (keyB, keyA);

        return ShapingCodegen.BuildKeyCompare(typeof(TKey), keyA, keyB, Expression.Constant(_comparison));
    }
}
