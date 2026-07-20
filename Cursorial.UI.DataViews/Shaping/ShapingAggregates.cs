using System.Linq.Expressions;
using System.Reflection;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>The aggregate kinds the engine computes (design doc §2.5; the mockup's summary menu set).</summary>
public enum AggregateKind
{
    Count,
    Sum,
    Min,
    Max,
    Average,
}

/// <summary>
/// A compiled per-column aggregator: computes one <see cref="AggregateKind"/> over a slot range of a
/// view permutation, reading the column's typed key vector directly (no boxing per row — design doc
/// invariant 2). Results surface as <see cref="AggregateValue"/> (a small tagged struct: double or
/// decimal lane, or a boxed Min/Max key for non-numeric columns — one box per GROUP, cold).
/// </summary>
internal abstract class ColumnAggregator
{
    /// <summary>The aggregate this instance computes.</summary>
    public required AggregateKind Kind { get; init; }

    /// <summary>Computes the aggregate over <paramref name="view"/>[start..start+count).</summary>
    public abstract AggregateValue Aggregate(int[] view, int start, int count);

    /// <summary>Formats a computed value for display (the column's format string + culture).</summary>
    public abstract string Format(AggregateValue value);

    /// <summary>
    /// Compares two values THIS aggregator produced (the §9.5 order-groups-by-summary projection).
    /// Empty (zero rows / all-null) orders first — the engine's nulls-first convention; numeric
    /// lanes compare in their accumulator type; Min/Max values compare through the column's key
    /// comparison (sort-consistent).
    /// </summary>
    public abstract int CompareValues(in AggregateValue a, in AggregateValue b);

    /// <summary>The shared empty-first arbitration for <see cref="CompareValues"/>.</summary>
    private protected static bool TryCompareEmpty(in AggregateValue a, in AggregateValue b, out int result)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            result = a.IsEmpty ? (b.IsEmpty ? 0 : -1) : 1;
            return true;
        }
        result = 0;
        return false;
    }

    /// <summary>
    /// Builds the aggregator for <paramref name="column"/>: numeric keys (incl. nullable — nulls are
    /// skipped, the DevExpress ignore-null default) get Sum/Average via widened accumulators;
    /// every comparable key gets Min/Max via the column comparison; Count is universal.
    /// </summary>
    public static ColumnAggregator Create(ShapedColumn column, AggregateKind kind, string? format = null)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (kind == AggregateKind.Count)
            return new CountAggregator { Kind = kind };

        var keyType = column.KeyType;
        var underlying = Nullable.GetUnderlyingType(keyType) ?? keyType;
        bool numeric = IsNumeric(underlying);

        if (kind is AggregateKind.Sum or AggregateKind.Average && !numeric)
            throw new ArgumentException($"{kind} requires a numeric key; '{keyType.Name}' is not.", nameof(kind));

        return (ColumnAggregator)typeof(ColumnAggregator)
            .GetMethod(nameof(CreateCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(column.GetType().GetGenericArguments()[0], keyType)
            .Invoke(null, [column, kind, format])!;
    }

    private static bool IsNumeric(Type type)
        => type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
           type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) ||
           type == typeof(double) || type == typeof(float) || type == typeof(decimal);

    private static ColumnAggregator CreateCore<TRow, TKey>(ShapedColumn<TRow, TKey> column, AggregateKind kind,
                                                           string? format) where TRow : notnull
        => kind switch
           {
               AggregateKind.Min or AggregateKind.Max => new MinMaxAggregator<TRow, TKey>(column, format)
                                                         { Kind = kind },
               _ => NumericAggregator<TRow, TKey>.Create(column, kind, format)
           };

    private sealed class CountAggregator : ColumnAggregator
    {
        public override AggregateValue Aggregate(int[] view, int start, int count) => AggregateValue.FromDouble(count);
        public override string Format(AggregateValue value) => ((long)value.AsDouble).ToString(System.Globalization.CultureInfo.CurrentCulture);
        public override int CompareValues(in AggregateValue a, in AggregateValue b) => a.AsDouble.CompareTo(b.AsDouble);
    }

    private sealed class MinMaxAggregator<TRow, TKey>(ShapedColumn<TRow, TKey> column, string? format)
        : ColumnAggregator where TRow : notnull
    {
        private readonly Func<TKey, string> _formatter =
            ShapingCodegen.CreateFormatter<TKey>(format);

        public override AggregateValue Aggregate(int[] view, int start, int count)
        {
            if (count == 0)
                return AggregateValue.Empty;

            var keys = column.Keys;
            var compare = column.KeyComparison;
            bool max = Kind == AggregateKind.Max;

            TKey best = keys[view[start]];
            for (int i = start + 1; i < start + count; i++)
            {
                TKey candidate = keys[view[i]];
                int c = compare(candidate, best);
                if (max ? c > 0 : c < 0)
                    best = candidate;
            }
            return AggregateValue.FromBoxed(best); // one box per group — cold
        }

        public override string Format(AggregateValue value)
            => value.IsEmpty ? string.Empty : _formatter((TKey)value.Boxed!);

        public override int CompareValues(in AggregateValue a, in AggregateValue b)
            => TryCompareEmpty(a, b, out int empty)
                ? empty
                : column.KeyComparison((TKey)a.Boxed!, (TKey)b.Boxed!); // one unbox per compare — cold (per sibling pair per publish)
    }

    /// <summary>
    /// Sum/Average over numeric keys via a compiled accumulator loop: decimal keys accumulate in
    /// decimal, everything else in double (the widened lanes; no per-row boxing).
    /// </summary>
    private sealed class NumericAggregator<TRow, TKey> : ColumnAggregator where TRow : notnull
    {
        private const double Epsilon = 0.000001;

        private readonly ShapedColumn<TRow, TKey> _column;
        private readonly Func<TKey[], int[], int, int, (double Sum, int Count)>? _doubleLoop;
        private readonly Func<TKey[], int[], int, int, (decimal Sum, int Count)>? _decimalLoop;
        private readonly Func<TKey, string> _formatter;
        private readonly bool _decimalLane;

        private NumericAggregator(ShapedColumn<TRow, TKey> column, string? format, bool decimalLane,
                                  Func<TKey[], int[], int, int, (double, int)>? doubleLoop,
                                  Func<TKey[], int[], int, int, (decimal, int)>? decimalLoop)
        {
            _column = column;
            _formatter = ShapingCodegen.CreateFormatter<TKey>(format);
            _decimalLane = decimalLane;
            _doubleLoop = doubleLoop;
            _decimalLoop = decimalLoop;
        }

        public static NumericAggregator<TRow, TKey> Create(ShapedColumn<TRow, TKey> column, AggregateKind kind, string? format)
        {
            var underlying = Nullable.GetUnderlyingType(typeof(TKey)) ?? typeof(TKey);
            bool decimalLane = underlying == typeof(decimal);
            return decimalLane
                ? new NumericAggregator<TRow, TKey>(column, format, true, null, CompileLoop<decimal>())
                  { Kind = kind }
                : new NumericAggregator<TRow, TKey>(column, format, false, CompileLoop<double>(), null)
                  { Kind = kind };
        }

        /// <summary>
        /// Compiles: <c>(keys, view, start, count) => { acc = 0; n = 0; for i in [start, start+count):
        /// { k = keys[view[i]]; if (hasValue(k)) { acc += widen(k); n++; } } return (acc, n); }</c>.
        /// Nulls skip (ignore-null); NaN doubles propagate (documented).
        /// </summary>
        private static Func<TKey[], int[], int, int, (TAcc, int)> CompileLoop<TAcc>()
        {
            var keyType = typeof(TKey);
            var underlying = Nullable.GetUnderlyingType(keyType);

            var keys = Expression.Parameter(typeof(TKey[]), "keys");
            var view = Expression.Parameter(typeof(int[]), "view");
            var start = Expression.Parameter(typeof(int), "start");
            var count = Expression.Parameter(typeof(int), "count");

            var acc = Expression.Variable(typeof(TAcc), "acc");
            var n = Expression.Variable(typeof(int), "n");
            var i = Expression.Variable(typeof(int), "i");
            var end = Expression.Variable(typeof(int), "end");
            var key = Expression.Variable(keyType, "k");

            var breakLabel = Expression.Label("break");

            Expression value = underlying is null ? key : Expression.Property(key, "Value");
            Expression widened = value.Type == typeof(TAcc) ? value : Expression.Convert(value, typeof(TAcc));

            Expression accumulate = Expression.Block(
                Expression.AddAssign(acc, widened),
                Expression.PostIncrementAssign(n));

            if (underlying is not null)
                accumulate = Expression.IfThen(Expression.Property(key, "HasValue"), accumulate);

            var loop = Expression.Block(
                [acc, n, i, end],
                Expression.Assign(acc, Expression.Default(typeof(TAcc))),
                Expression.Assign(n, Expression.Constant(0)),
                Expression.Assign(i, start),
                Expression.Assign(end, Expression.Add(start, count)),
                Expression.Loop(
                    Expression.IfThenElse(
                        Expression.LessThan(i, end),
                        Expression.Block(
                            [key],
                            Expression.Assign(key, Expression.ArrayIndex(keys, Expression.ArrayIndex(view, i))),
                            accumulate,
                            Expression.PostIncrementAssign(i)),
                        Expression.Break(breakLabel)),
                    breakLabel),
                Expression.New(
                    typeof(ValueTuple<TAcc, int>).GetConstructor([typeof(TAcc), typeof(int)])!,
                    acc, n));

            return Expression.Lambda<Func<TKey[], int[], int, int, (TAcc, int)>>(loop, keys, view, start, count).Compile();
        }

        public override AggregateValue Aggregate(int[] view, int start, int count)
        {
            if (_decimalLane)
            {
                var (sum, n) = _decimalLoop!(_column.Keys, view, start, count);
                if (Kind == AggregateKind.Sum)
                    return AggregateValue.FromDecimal(sum);
                return n == 0 ? AggregateValue.Empty : AggregateValue.FromDecimal(sum / n);
            }
            else
            {
                var (sum, n) = _doubleLoop!(_column.Keys, view, start, count);
                if (Kind == AggregateKind.Sum)
                    return AggregateValue.FromDouble(sum);
                return n == 0 ? AggregateValue.Empty : AggregateValue.FromDouble(sum / n);
            }
        }

        public override string Format(AggregateValue value)
        {
            if (value.IsEmpty)
                return string.Empty;

            // Sum/Average format through the COLUMN's key formatter when the widened value converts
            // back losslessly enough for display (decimal lane exact; double lane may be fractional
            // for integer keys — Average of ints is legitimately fractional, format as the widened
            // value with the column's format string).
            var underlying = Nullable.GetUnderlyingType(typeof(TKey)) ?? typeof(TKey);
            if (_decimalLane)
            {
                decimal d = value.AsDecimal;
                if (underlying == typeof(decimal))
                    return FormatViaColumn(d);
                return d.ToString(System.Globalization.CultureInfo.CurrentCulture);
            }

            double v = value.AsDouble;
            if (Kind == AggregateKind.Sum && IsIntegral(underlying) && v is >= long.MinValue and <= long.MaxValue &&
                Math.Abs(v - Math.Floor(v)) < Epsilon)
            {
                return FormatIntegral((long)v, underlying);
            }
            if (underlying == typeof(double) || underlying == typeof(float))
                return FormatViaColumn(v);
            return v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        }

        public override int CompareValues(in AggregateValue a, in AggregateValue b)
        {
            if (TryCompareEmpty(a, b, out int empty))
                return empty;
            return _decimalLane ? a.AsDecimal.CompareTo(b.AsDecimal) : a.AsDouble.CompareTo(b.AsDouble);
        }

        private static bool IsIntegral(Type t)
            => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
               t == typeof(sbyte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort);

        private string FormatViaColumn<TValue>(TValue value)
        {
            // The widened value IS the key type (or its nullable underlying) — round-trip through it.
            var underlying = Nullable.GetUnderlyingType(typeof(TKey));
            object boxed = value!;
            TKey key = underlying is null ? (TKey)boxed : (TKey)Activator.CreateInstance(typeof(TKey), boxed)!;
            return _formatter(key);
        }

        private string FormatIntegral(long value, Type underlying)
        {
            object narrowed = Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);
            var nullableUnderlying = Nullable.GetUnderlyingType(typeof(TKey));
            TKey key = nullableUnderlying is null ? (TKey)narrowed : (TKey)Activator.CreateInstance(typeof(TKey), narrowed)!;
            return _formatter(key);
        }
    }
}

/// <summary>A computed aggregate: a double or decimal lane value, a boxed Min/Max key, or empty.</summary>
internal readonly struct AggregateValue
{
    private readonly double _double;
    private readonly decimal _decimal;
    private readonly byte _lane; // 0 empty, 1 double, 2 decimal, 3 boxed

    private AggregateValue(double d, decimal m, object? boxed, byte lane)
    {
        _double = d;
        _decimal = m;
        Boxed = boxed;
        _lane = lane;
    }

    public static AggregateValue Empty => default;
    public static AggregateValue FromDouble(double value) => new(value, 0, null, 1);
    public static AggregateValue FromDecimal(decimal value) => new(0, value, null, 2);
    public static AggregateValue FromBoxed(object? value) => new(0, 0, value, 3);

    public bool IsEmpty => _lane == 0;
    public double AsDouble => _lane == 2 ? (double)_decimal : _double;
    public decimal AsDecimal => _lane == 1 ? (decimal)_double : _decimal;
    public object? Boxed { get; }
}
