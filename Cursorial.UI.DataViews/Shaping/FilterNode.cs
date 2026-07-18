using System.Collections.ObjectModel;
using System.Linq.Expressions;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>The comparison operators a <see cref="FilterConditionNode"/> supports (the auto-filter grammar's targets).</summary>
public enum FilterOperator
{
    Equals,
    NotEquals,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    /// <summary>String contains (the bare-text auto-filter default; uses the column's string comparison).</summary>
    Contains,
    /// <summary>String starts-with (per-column auto-filter option).</summary>
    StartsWith,
    /// <summary>Inclusive range over <see cref="FilterConditionNode.Value"/>..<see cref="FilterConditionNode.SecondValue"/>.</summary>
    Between,
}

/// <summary>
/// The engine's criteria tree (design doc §2.4 — panel-amended 2026-07-18): the ONE public filter
/// model every surface targets — programmatic API, per-column checklist value sets, auto-filter row
/// conditions, and (later) the Filter Builder / expression-language parser. And/Or/Not groups over
/// condition, value-set, and custom-predicate leaves. Trees are immutable; the controller compiles
/// the active tree against its typed key vectors into one <c>Func&lt;int,bool&gt;</c> per shape
/// (no boxing at evaluation — literals convert to the key type at compile time).
/// </summary>
public abstract class FilterNode
{
    private protected FilterNode() { }

    /// <summary>All children must match.</summary>
    public static FilterNode And(params FilterNode[] children) => Group(FilterGroupOperator.And, children);

    /// <summary>Any child matches.</summary>
    public static FilterNode Or(params FilterNode[] children) => Group(FilterGroupOperator.Or, children);

    /// <summary>Inverts a subtree.</summary>
    public static FilterNode Not(FilterNode child) => new FilterNotNode(child);

    /// <summary>A typed comparison against one column's value.</summary>
    public static FilterNode Condition(object columnKey, FilterOperator op, object? value, object? secondValue = null)
        => new FilterConditionNode(columnKey, op, value, secondValue);

    /// <summary>The checklist filter: the column's value is one of <paramref name="values"/>.</summary>
    public static FilterNode InSet(object columnKey, IEnumerable<object?> values)
        => new FilterValueSetNode(columnKey, values);

    /// <summary>An arbitrary row predicate (compiled; the escape hatch the expression language also lowers to).</summary>
    public static FilterNode Custom(LambdaExpression rowPredicate) => new FilterPredicateNode(rowPredicate);

    private static FilterNode Group(FilterGroupOperator op, FilterNode[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Length == 0)
            throw new ArgumentException("A filter group needs at least one child.", nameof(children));
        return children.Length == 1 ? children[0] : new FilterGroupNode(op, children);
    }
}

/// <summary>The group operator of a <see cref="FilterGroupNode"/>.</summary>
public enum FilterGroupOperator
{
    And,
    Or,
}

/// <summary>An And/Or group (the Filter Builder's group nodes).</summary>
public sealed class FilterGroupNode : FilterNode
{
    internal FilterGroupNode(FilterGroupOperator op, FilterNode[] children)
    {
        foreach (var child in children)
            ArgumentNullException.ThrowIfNull(child, nameof(children));
        Operator = op;
        Children = new ReadOnlyCollection<FilterNode>((FilterNode[])children.Clone());
    }

    public FilterGroupOperator Operator { get; }
    public IReadOnlyList<FilterNode> Children { get; }
}

/// <summary>A negated subtree.</summary>
public sealed class FilterNotNode : FilterNode
{
    internal FilterNotNode(FilterNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        Child = child;
    }

    public FilterNode Child { get; }
}

/// <summary>A typed comparison leaf: <c>[column] op value</c> (or <c>Between value..secondValue</c>).</summary>
public sealed class FilterConditionNode : FilterNode
{
    internal FilterConditionNode(object columnKey, FilterOperator op, object? value, object? secondValue)
    {
        ArgumentNullException.ThrowIfNull(columnKey);
        if (op == FilterOperator.Between && secondValue is null)
            throw new ArgumentException("Between requires a second value.", nameof(secondValue));
        ColumnKey = columnKey;
        Operator = op;
        Value = value;
        SecondValue = secondValue;
    }

    /// <summary>The column identity (matches a shaped column's <c>Identity</c> — the grid column or field name).</summary>
    public object ColumnKey { get; }
    public FilterOperator Operator { get; }
    public object? Value { get; }
    public object? SecondValue { get; }
}

/// <summary>The checklist leaf: column value ∈ set (null is a legal member — the (Blanks) checkbox).</summary>
public sealed class FilterValueSetNode : FilterNode
{
    internal FilterValueSetNode(object columnKey, IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(columnKey);
        ArgumentNullException.ThrowIfNull(values);
        ColumnKey = columnKey;
        Values = new ReadOnlyCollection<object?>(values.ToArray());
    }

    public object ColumnKey { get; }
    public IReadOnlyList<object?> Values { get; }
}

/// <summary>An arbitrary compiled row predicate (<c>Expression&lt;Func&lt;TRow,bool&gt;&gt;</c>).</summary>
public sealed class FilterPredicateNode : FilterNode
{
    internal FilterPredicateNode(LambdaExpression rowPredicate)
    {
        ArgumentNullException.ThrowIfNull(rowPredicate);
        if (rowPredicate.Parameters.Count != 1 || rowPredicate.ReturnType != typeof(bool))
            throw new ArgumentException("The predicate must be a single-parameter lambda returning bool.", nameof(rowPredicate));
        RowPredicate = rowPredicate;
    }

    public LambdaExpression RowPredicate { get; }
}
