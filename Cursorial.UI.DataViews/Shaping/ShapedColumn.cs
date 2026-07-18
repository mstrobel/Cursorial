using System.Linq.Expressions;
using System.Reflection;

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

    /// <summary>
    /// Builds the boolean expression for one filter condition over this column's key at slot
    /// <paramref name="slot"/> (the <see cref="ShapingFilter"/> compiler's leaf). Ordering operators
    /// run through the column comparison (sort-consistent null ordering); Contains/StartsWith are
    /// string-only; literals convert to the key type at build time (never per evaluation).
    /// </summary>
    internal abstract Expression BuildConditionExpression(ParameterExpression slot, FilterOperator op, object? value, object? secondValue);

    /// <summary>Builds the set-membership expression for the checklist filter (typed hash set baked at build).</summary>
    internal abstract Expression BuildSetExpression(ParameterExpression slot, IReadOnlyList<object?> values);
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
    private readonly CollationKeyStore? _collation;

    public ShapedColumn(Func<TRow, TKey> getter, Comparison<TKey> comparison, Func<TKey, string> formatter,
                        CollationKeyStore? collation = null)
    {
        _getter = getter;
        _comparison = comparison;
        _formatter = formatter;
        _collation = collation;
    }

    public override Type KeyType => typeof(TKey);

    /// <summary>The collation-key blob — non-null only for culture-mode string columns (§2.2).
    /// Sort-order-only: display/grouping captions/filters still read the string vector. Internal
    /// probe for tests (ordinal columns must never pay the blob).</summary>
    internal CollationKeyStore? Collation => _collation;

    public override void EnsureCapacity(int capacity)
    {
        if (Keys.Length < capacity)
            Array.Resize(ref Keys, Math.Max(capacity, Math.Max(16, Keys.Length * 2)));
        _collation?.EnsureCapacity(Keys.Length); // range arrays stay slot-aligned with the key vector
    }

    /// <summary>The typed extract (the hot path — the untyped override is the INCC boundary).</summary>
    public void ExtractKey(TRow row, int slot)
    {
        var key = _getter(row);
        Keys[slot] = key;

        // Culture string columns also refresh the slot's collation-key range here — the ONLY blob
        // write site, so the §2.6 invariant (owner thread, never mid-shape) holds automatically.
        // The cast is reference-only: _collation is non-null only when TKey == string.
        _collation?.Extract((string?)(object?)key, slot);
    }

    public override void ExtractKeyUntyped(object row, int slot) => ExtractKey((TRow)row, slot);

    /// <summary>Culture string columns compare through the sort-key blob — the grouping boundary
    /// walk and Min/Max must see EXACTLY the sort's order or group runs fracture (§2.5).</summary>
    public override int CompareSlots(int a, int b)
        => _collation is { } collation ? collation.CompareSlots(a, b) : _comparison(Keys[a], Keys[b]);

    public override string FormatSlot(int slot) => _formatter(Keys[slot]);

    public override object? GetKeyBoxed(int slot) => Keys[slot];

    /// <summary>The compiled key comparison (exposed for the repair/aggregate kits).</summary>
    internal Comparison<TKey> KeyComparison => _comparison;

    internal override Expression BuildCompareExpression(ParameterExpression a, ParameterExpression b, bool descending)
    {
        if (_collation is { } collation)
        {
            // Collation-key ordinal compare (§2.2): culture order at memcmp speed. The blob/range
            // arrays are read through Expression.Field on the store constant PER INVOCATION so
            // growth/compaction re-allocation is always observed (the key-vector rule); the static
            // helper owns the Span locals expression trees cannot.
            var store = Expression.Constant(collation);
            Expression slotA = a, slotB = b;
            if (descending)
                (slotA, slotB) = (slotB, slotA); // operand swap, same as the direct path

            return Expression.Call(
                typeof(CollationKeyStore).GetMethod(nameof(CollationKeyStore.CompareSortKeys),
                                                    BindingFlags.NonPublic | BindingFlags.Static)!,
                Expression.Field(store, nameof(CollationKeyStore.Blob)),
                Expression.Field(store, nameof(CollationKeyStore.Offsets)),
                Expression.Field(store, nameof(CollationKeyStore.Lengths)),
                slotA, slotB);
        }

        // this.Keys[a|b] — through the field so growth re-allocation is observed.
        var keysField = Expression.Field(Expression.Constant(this), nameof(Keys));
        Expression keyA = Expression.ArrayIndex(keysField, a);
        Expression keyB = Expression.ArrayIndex(keysField, b);

        // Descending = operand swap (never negate a comparison result — int.MinValue).
        if (descending)
            (keyA, keyB) = (keyB, keyA);

        return ShapingCodegen.BuildKeyCompare(typeof(TKey), keyA, keyB, Expression.Constant(_comparison));
    }

    internal override Expression BuildConditionExpression(ParameterExpression slot, FilterOperator op, object? value, object? secondValue)
    {
        var key = Expression.ArrayIndex(Expression.Field(Expression.Constant(this), nameof(Keys)), slot);

        if (op is FilterOperator.Contains or FilterOperator.StartsWith)
        {
            if (typeof(TKey) != typeof(string))
                throw new ArgumentException($"{op} applies to string columns only; the key type is '{typeof(TKey).Name}'.");
            string needle = value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;
            var method = typeof(string).GetMethod(op == FilterOperator.Contains ? nameof(string.Contains) : nameof(string.StartsWith),
                                                  [typeof(string), typeof(StringComparison)])!;
            // key != null && key.Op(needle, comparison)
            return Expression.AndAlso(
                Expression.NotEqual(key, Expression.Constant(null, typeof(string))),
                Expression.Call(key, method, Expression.Constant(needle), Expression.Constant(StringComparisonMode)));
        }

        // Ordering/equality run through the column comparison against a build-time-converted literal —
        // sort-consistent semantics (null-first) with zero per-evaluation conversion.
        var literal = Expression.Constant(ShapingFilter.ConvertLiteral<TKey>(value), typeof(TKey));
        var compare = Expression.Invoke(Expression.Constant(_comparison), key, literal);
        var zero = Expression.Constant(0);

        if (op == FilterOperator.Between)
        {
            var upper = Expression.Constant(ShapingFilter.ConvertLiteral<TKey>(secondValue), typeof(TKey));
            return Expression.AndAlso(
                Expression.GreaterThanOrEqual(compare, zero),
                Expression.LessThanOrEqual(Expression.Invoke(Expression.Constant(_comparison), key, upper), zero));
        }

        return op switch
        {
            FilterOperator.Equals => Expression.Equal(compare, zero),
            FilterOperator.NotEquals => Expression.NotEqual(compare, zero),
            FilterOperator.LessThan => Expression.LessThan(compare, zero),
            FilterOperator.LessThanOrEqual => Expression.LessThanOrEqual(compare, zero),
            FilterOperator.GreaterThan => Expression.GreaterThan(compare, zero),
            FilterOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(compare, zero),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
    }

    internal override Expression BuildSetExpression(ParameterExpression slot, IReadOnlyList<object?> values)
    {
        // A null member is legal for nullable/reference keys (the "(Blanks)" checkbox); for a
        // non-nullable value key null can never match, so it is skipped rather than mis-converted.
        bool nullable = default(TKey) is null;
        var set = new HashSet<TKey>();
        foreach (var value in values)
        {
            if (value is null && !nullable)
                continue;
            set.Add(ShapingFilter.ConvertLiteral<TKey>(value)!);
        }

        var key = Expression.ArrayIndex(Expression.Field(Expression.Constant(this), nameof(Keys)), slot);
        return Expression.Call(Expression.Constant(set), typeof(HashSet<TKey>).GetMethod(nameof(HashSet<int>.Contains))!, key);
    }

    /// <summary>The string-comparison mode string Contains/StartsWith filters honor (set by the column options; Ordinal-ish default pending the controller's option plumb).</summary>
    internal StringComparison StringComparisonMode { get; init; } = StringComparison.CurrentCultureIgnoreCase;
}
