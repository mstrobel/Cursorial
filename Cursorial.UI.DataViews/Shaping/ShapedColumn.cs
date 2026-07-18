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

    /// <summary>
    /// The §9.6 span-format lane: writes <paramref name="slot"/>'s display text into
    /// <paramref name="destination"/>, returning chars written or −1 when it doesn't fit (the
    /// caller grows and retries). The band cache formats through THIS into pooled char buffers —
    /// no per-cell string; the text is identical to <see cref="FormatSlot"/> (both lanes compile
    /// from the same format/culture pair).
    /// </summary>
    public abstract int FormatSlotTo(int slot, Span<char> destination);

    /// <summary>The key boxed — cold paths only (group captions, diagnostics).</summary>
    public abstract object? GetKeyBoxed(int slot);

    /// <summary>
    /// Whether <paramref name="slot"/>'s key is null (a null reference / <see cref="Nullable{T}"/>
    /// without a value) — the TopK population filter (§9.5: null keys never qualify). Always false
    /// for non-nullable value keys.
    /// </summary>
    internal abstract bool IsKeyNull(int slot);

    /// <summary>
    /// A rank predicate for the §9.5 TopBottom lane: <c>qualifies(slot, boxedThreshold)</c> =
    /// non-null key AND key ≥ threshold (top) / ≤ threshold (bottom), through the column's OWN sort
    /// comparison (ties include — consistent with
    /// <see cref="DataViewController.TryGetTopKThreshold"/>'s boxed result). Built ONCE per rule
    /// compile; the threshold re-derives per publish and flows in boxed.
    /// </summary>
    internal abstract Func<int, object?, bool> CreateRankPredicate(bool top);

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

    /// <summary>
    /// A compiled slot → double read over the key vector (null keys/non-numeric → NaN), or null for
    /// non-numeric key types. The conditional-formatting stats block (§2.7) and data-bar fractions
    /// read through it — typed, no per-cell boxing (invariant 2).
    /// </summary>
    internal abstract Func<int, double>? TryCreateDoubleReader();

    /// <summary>
    /// The distinct formatted values over <paramref name="slots"/> for the checklist popup (§3.4):
    /// typed dedupe + count, sorted by the column comparison, capped at <paramref name="maxCount"/>.
    /// A null-key entry surfaces FIRST as <c>(Formatted: "", Raw: null)</c> — the "(Blanks)" row.
    /// Cold path by design (one boxed key per DISTINCT value, not per row).
    /// </summary>
    public abstract IReadOnlyList<(string Formatted, object? Raw, int Count)> GetDistinctValues(
        ReadOnlySpan<int> slots, int maxCount);

    /// <summary>Whether the column has a compiled write-back setter (the editing lane — §3.2).</summary>
    public abstract bool IsEditable { get; }

    /// <summary>
    /// Parses <paramref name="text"/> to the key type and writes it through the compiled setter
    /// (the edit-commit path). False when the column is read-only or the text doesn't parse —
    /// the editor stays open for correction. For value-type rows the write lands IN THE BOX
    /// (<paramref name="row"/> must be a boxed <c>TRow</c> — the new-row lane's contract; the
    /// id-keyed lane uses the typed <see cref="ShapedColumn{TRow}.TrySetFromText(ref TRow,string)"/>
    /// + <c>RowStore.SetRow</c> instead, §9.6).
    /// </summary>
    public abstract bool TrySetFromText(object row, string text);

    /// <summary>Writes <paramref name="value"/> back INTO the box (the only way an object-typed
    /// surface can make a struct-row edit real — unboxing yields a copy; §9.6).</summary>
    private protected static void WriteBox<T>(object box, T value) where T : struct
        => System.Runtime.CompilerServices.Unsafe.Unbox<T>(box) = value;
}

/// <summary>
/// The row-typed column layer (§9.6): typed extraction and edit write-back without boxing the row —
/// the controller knows <typeparamref name="TRow"/> but not the key type, so the struct-row lanes
/// (attach-time extraction, the id-keyed edit commit) dispatch through here.
/// </summary>
internal abstract class ShapedColumn<TRow> : ShapedColumn
{
    /// <summary>The typed extract (the hot path — the untyped override is the INCC boundary).</summary>
    public abstract void ExtractKey(TRow row, int slot);

    /// <summary>
    /// Parses <paramref name="text"/> and writes through the compiled setter into
    /// <paramref name="row"/>: in place for reference rows; for value-type rows the compiled
    /// mutator writes the modified COPY back into the ref — the caller stores it via
    /// <c>RowStore.SetRow</c> (design doc §9.6: a setter on a copy is a silent no-op).
    /// False when the column is read-only or the text doesn't parse.
    /// </summary>
    public abstract bool TrySetFromText(ref TRow row, string text);
}

/// <summary>The typed column (see <see cref="ShapedColumn"/>).</summary>
internal sealed class ShapedColumn<TRow, TKey> : ShapedColumn<TRow>
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
    public override void ExtractKey(TRow row, int slot)
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

    /// <summary>The compiled span formatter (§9.6 — set by the factory; null falls back through the
    /// string lane, format-identical either way).</summary>
    internal SpanFormat<TKey>? SpanFormatter { get; init; }

    public override int FormatSlotTo(int slot, Span<char> destination)
    {
        if (SpanFormatter is { } spanFormat)
            return spanFormat(Keys[slot], destination);

        string text = _formatter(Keys[slot]);
        if (text.Length > destination.Length)
            return -1;
        text.CopyTo(destination);
        return text.Length;
    }

    public override object? GetKeyBoxed(int slot) => Keys[slot];

    internal override bool IsKeyNull(int slot) => Keys[slot] is null; // JIT-eliminated false for non-nullable value keys

    internal override Func<int, object?, bool> CreateRankPredicate(bool top)
        => (slot, boxedThreshold) =>
        {
            if (Keys[slot] is null || boxedThreshold is not TKey threshold)
                return false;
            int comparison = _comparison(Keys[slot], threshold);
            return top ? comparison >= 0 : comparison <= 0;
        };

    /// <summary>The compiled key comparison (exposed for the repair/aggregate kits).</summary>
    internal Comparison<TKey> KeyComparison => _comparison;

    /// <summary>The compiled write-back setter for REFERENCE rows (null = read-only column, or a
    /// value-type row — see <see cref="StructSetter"/>; the editing lane — §3.2).</summary>
    internal Action<TRow, TKey>? Setter { get; init; }

    /// <summary>The compiled write-back mutator for VALUE-TYPE rows (§9.6): takes the row COPY,
    /// writes the member, returns the modified copy — the caller stores it back. Null for
    /// reference rows or read-only columns.</summary>
    internal Func<TRow, TKey, TRow>? StructSetter { get; init; }

    /// <summary>Boxed-struct write-back (built only for value-type rows) — the new-row lane's
    /// mutate-the-box path; created via <see cref="ShapedColumn.WriteBox{T}"/> reflection once per
    /// closed column type (the struct constraint isn't expressible on the open <typeparamref name="TRow"/>).</summary>
    private static readonly Action<object, TRow>? BoxWriter =
        typeof(TRow).IsValueType
            ? (Action<object, TRow>)typeof(ShapedColumn)
                .GetMethod(nameof(WriteBox), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(typeof(TRow))
                .CreateDelegate(typeof(Action<object, TRow>))
            : null;

    public override bool IsEditable => Setter is not null || StructSetter is not null;

    /// <summary>Parses the edit text to the key type; false when the column is read-only or the
    /// text doesn't parse (the editor stays open for correction).</summary>
    private bool TryParseForSet(string text, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TKey value)
    {
        value = default!;
        if (Setter is null && StructSetter is null)
            return false;

        try
        {
            value = ShapingFilter.ConvertLiteral<TKey>(text.Length == 0 && default(TKey) is null ? null : text)!;
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return false; // unparseable — the editor stays open for correction
        }
    }

    public override bool TrySetFromText(object row, string text)
    {
        if (!TryParseForSet(text, out var value))
            return false;

        if (StructSetter is { } mutate)
        {
            // Value-type row: mutate a copy, write it back INTO the caller's box (unboxing alone
            // would edit a throwaway copy — the §9.6 silent-no-op trap).
            BoxWriter!(row, mutate((TRow)row, value));
            return true;
        }

        Setter!((TRow)row, value);
        return true;
    }

    public override bool TrySetFromText(ref TRow row, string text)
    {
        if (!TryParseForSet(text, out var value))
            return false;

        if (StructSetter is { } mutate)
        {
            row = mutate(row, value); // the caller stores the copy back (RowStore.SetRow)
            return true;
        }

        Setter!(row, value);
        return true;
    }

    internal override Func<int, double>? TryCreateDoubleReader()
    {
        var underlying = Nullable.GetUnderlyingType(typeof(TKey)) ?? typeof(TKey);
        if (!IsNumeric(underlying))
            return null;

        // slot => (double)this.Keys[slot] — through the field so growth re-allocation is observed
        // (the key-vector rule); a null Nullable<V> reads as NaN (stats/bars skip NaN).
        var slot = Expression.Parameter(typeof(int), "slot");
        var key = Expression.ArrayIndex(Expression.Field(Expression.Constant(this), nameof(Keys)), slot);

        Expression body;
        if (Nullable.GetUnderlyingType(typeof(TKey)) is not null)
        {
            body = Expression.Condition(
                Expression.Property(key, "HasValue"),
                Expression.Convert(Expression.Property(key, "Value"), typeof(double)),
                Expression.Constant(double.NaN));
        }
        else
        {
            body = typeof(TKey) == typeof(double) ? key : Expression.Convert(key, typeof(double));
        }

        return Expression.Lambda<Func<int, double>>(body, slot).Compile();
    }

    private static bool IsNumeric(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
           type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) ||
           type == typeof(double) || type == typeof(float) || type == typeof(decimal);

    public override IReadOnlyList<(string Formatted, object? Raw, int Count)> GetDistinctValues(
        ReadOnlySpan<int> slots, int maxCount)
    {
        // Typed dedupe (one dictionary entry per DISTINCT key; null keys counted separately —
        // a Dictionary cannot hold them and "(Blanks)" is its own row anyway).
        var counts = new Dictionary<TKey, int>();
        int nullCount = 0;
        foreach (int slot in slots)
        {
            var key = Keys[slot];
            if (key is null)
                nullCount++;
            else if (!counts.TryAdd(key, 1))
                counts[key]++;
        }

        var keys = new TKey[counts.Count];
        counts.Keys.CopyTo(keys, 0);
        Array.Sort(keys, _comparison);

        var result = new List<(string, object?, int)>(Math.Min(keys.Length + 1, maxCount));
        if (nullCount > 0)
            result.Add((string.Empty, null, nullCount)); // the "(Blanks)" row — always first (null sorts first)
        foreach (var key in keys)
        {
            if (result.Count >= maxCount)
                break;
            result.Add((_formatter(key), key, counts[key])); // one box per distinct value — cold
        }
        return result;
    }

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
