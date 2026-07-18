using System.Globalization;
using System.Linq.Expressions;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// Compiles a <see cref="FilterNode"/> criteria tree (design doc §2.4) into one
/// <c>Func&lt;int,bool&gt;</c> over slot ids: condition/set leaves evaluate against the typed key
/// vectors (literals converted to key types HERE, at build — nothing boxes or converts per
/// evaluation); <see cref="FilterPredicateNode"/> leaves read the row through the store accessor.
/// One compile per filter change; evaluation is the visibility pass + per-tick row rechecks.
/// </summary>
internal static class ShapingFilter
{
    /// <summary>
    /// Compiles <paramref name="root"/> for a row store: <paramref name="columnLookup"/> resolves a
    /// leaf's column key to its shaped column; <paramref name="rowAccessor"/> is the typed
    /// <c>Func&lt;int,TRow&gt;</c> slot→row read used by custom-predicate leaves (null forbids them).
    /// </summary>
    public static Func<int, bool> Compile(
        FilterNode root,
        Func<object, ShapedColumn?> columnLookup,
        Delegate? rowAccessor)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(columnLookup);

        var slot = Expression.Parameter(typeof(int), "slot");
        var body = Build(root, slot, columnLookup, rowAccessor);
        return Expression.Lambda<Func<int, bool>>(body, slot).Compile();
    }

    private static Expression Build(
        FilterNode node, ParameterExpression slot,
        Func<object, ShapedColumn?> columnLookup, Delegate? rowAccessor)
    {
        switch (node)
        {
            case FilterGroupNode group:
            {
                Expression? acc = null;
                foreach (var child in group.Children)
                {
                    var expr = Build(child, slot, columnLookup, rowAccessor);
                    acc = acc is null ? expr
                        : group.Operator == FilterGroupOperator.And
                            ? Expression.AndAlso(acc, expr)
                            : Expression.OrElse(acc, expr);
                }
                return acc!;
            }

            case FilterNotNode not:
                return Expression.Not(Build(not.Child, slot, columnLookup, rowAccessor));

            case FilterConditionNode condition:
                return ResolveColumn(columnLookup, condition.ColumnKey)
                    .BuildConditionExpression(slot, condition.Operator, condition.Value, condition.SecondValue);

            case FilterValueSetNode valueSet:
                return ResolveColumn(columnLookup, valueSet.ColumnKey)
                    .BuildSetExpression(slot, valueSet.Values);

            case FilterPredicateNode predicate:
            {
                if (rowAccessor is null)
                    throw new InvalidOperationException("Custom filter predicates require a row accessor.");

                // rowAccessor is Func<int,TRow>; the predicate lambda takes TRow. Both invoke typed.
                var rowType = rowAccessor.GetType().GetGenericArguments()[1];
                if (predicate.RowPredicate.Parameters[0].Type != rowType)
                    throw new ArgumentException(
                        $"Custom predicate parameter type '{predicate.RowPredicate.Parameters[0].Type.Name}' " +
                        $"does not match the row type '{rowType.Name}'.");

                var row = Expression.Invoke(Expression.Constant(rowAccessor), slot);
                return Expression.Invoke(predicate.RowPredicate, row);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(node), node.GetType().Name, "Unknown filter node.");
        }
    }

    private static ShapedColumn ResolveColumn(Func<object, ShapedColumn?> lookup, object columnKey)
        => lookup(columnKey)
           ?? throw new ArgumentException($"The filter references an unshaped column '{columnKey}'.");

    /// <summary>
    /// Converts a filter literal to the key type at BUILD time (the cold site): pass-through when
    /// already typed; parse/convert with the current culture otherwise (enum names, DateOnly/
    /// DateTime/Guid strings, numerics). Null maps to the key type's null when it has one; a null
    /// literal against a non-nullable value key is an authoring error surfaced here.
    /// </summary>
    internal static TKey? ConvertLiteral<TKey>(object? value)
    {
        if (value is TKey typed)
            return typed;

        if (value is null)
        {
            if (default(TKey) is null)
                return default;
            throw new ArgumentException($"A null filter value cannot match the non-nullable key type '{typeof(TKey).Name}'.");
        }

        var targetType = Nullable.GetUnderlyingType(typeof(TKey)) ?? typeof(TKey);

        object converted;
        if (targetType.IsEnum)
        {
            converted = value is string enumText
                ? Enum.Parse(targetType, enumText, ignoreCase: true)
                : Enum.ToObject(targetType, value);
        }
        else if (targetType == typeof(DateOnly))
        {
            converted = value switch
            {
                string s => DateOnly.Parse(s, CultureInfo.CurrentCulture),
                DateTime dt => DateOnly.FromDateTime(dt),
                _ => throw new ArgumentException($"Cannot convert '{value.GetType().Name}' to DateOnly."),
            };
        }
        else if (targetType == typeof(TimeOnly))
        {
            converted = value switch
            {
                string s => TimeOnly.Parse(s, CultureInfo.CurrentCulture),
                TimeSpan ts => TimeOnly.FromTimeSpan(ts),
                _ => throw new ArgumentException($"Cannot convert '{value.GetType().Name}' to TimeOnly."),
            };
        }
        else if (targetType == typeof(Guid))
        {
            converted = value is string g ? Guid.Parse(g) : throw new ArgumentException("Guid literals must be strings.");
        }
        else
        {
            converted = Convert.ChangeType(value, targetType, CultureInfo.CurrentCulture);
        }

        return (TKey)converted;
    }
}
