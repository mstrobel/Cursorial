using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace Cursorial.UI.DataViews.Shaping.Expressions;

/// <summary>
/// The criteria-language facade (design doc §9.1): parse (the validation strip), compile to a
/// <see cref="FilterNode"/> (the grid's one filter shape), and the Filter-Builder bridge —
/// recognizing the tree-shaped subset as structural nodes (round-trippable to the builder) with a
/// compiled-predicate fallback for everything else.
/// </summary>
[RequiresDynamicCode("Compiles expression trees specialized to the row type.")]
public static class CriteriaExpression
{
    /// <summary>Field bindings from the grid's columns: (canonical name, display alias, selector, the column identity for structural nodes, the column's string mode — §9.1's one semantic authority).</summary>
    public readonly record struct Field(string Name, string? DisplayName, LambdaExpression Selector, object ColumnKey,
                                        StringComparison StringMode = StringComparison.CurrentCulture);

    /// <summary>Parses only (the text editor's per-keystroke validation lane — no type binding).</summary>
    public static CriteriaParser.Result Parse(string text) => CriteriaParser.Parse(text);

    /// <summary>The compile product: a FilterNode (null on failure) + diagnostics.</summary>
    public readonly record struct FilterResult(FilterNode? Filter, IReadOnlyList<CriteriaDiagnostic> Diagnostics)
    {
        public bool IsValid => Filter is not null && Diagnostics.Count == 0;
    }

    /// <summary>
    /// Parses + lowers <paramref name="text"/> to a <see cref="FilterNode"/>: the structural subset
    /// (And/Or over field-vs-literal comparisons and In lists) becomes real tree nodes (the Filter
    /// Builder can re-edit them); anything else compiles whole into <see cref="FilterNode.Custom"/>.
    /// </summary>
    public static FilterResult ToFilterNode(string text, Type rowType, IReadOnlyList<Field> fields)
    {
        var parsed = Parse(text);
        if (parsed.Root is null || parsed.Diagnostics.Count > 0)
            return new FilterResult(null, parsed.Diagnostics);

        // Structural first — builder interop beats an opaque predicate when both are possible.
        if (TryToStructuralNode(parsed.Root, fields, out var structural))
            return new FilterResult(structural, []);

        var compiled = CriteriaCompiler.Compile(parsed.Root, rowType,
            fields.Select(f => new CriteriaCompiler.FieldBinding(f.Name, f.DisplayName, f.Selector, f.StringMode)).ToArray());
        return compiled.Predicate is null
            ? new FilterResult(null, compiled.Diagnostics)
            : new FilterResult(FilterNode.Custom(compiled.Predicate), compiled.Diagnostics);
    }

    // ── The structural (builder-shaped) lowering ─────────────────────────────────────────────────

    private static bool TryToStructuralNode(CriteriaNode node, IReadOnlyList<Field> fields, [NotNullWhen(true)] out FilterNode? result)
    {
        result = null;
        switch (node)
        {
            case CriteriaBinaryNode { Operator: CriteriaBinaryOperator.And or CriteriaBinaryOperator.Or } group:
            {
                if (!TryToStructuralNode(group.Left, fields, out var left) ||
                    !TryToStructuralNode(group.Right, fields, out var right))
                {
                    return false;
                }

                // Flatten same-operator children into an n-ary group: the parser is left-associative
                // (A And B And C ⇒ And(And(A,B),C)), but the Filter Builder edits n-ary groups (the
                // DevExpress tree shape) — and the flattening is semantics-preserving.
                var target = group.Operator == CriteriaBinaryOperator.And ? FilterGroupOperator.And : FilterGroupOperator.Or;
                var children = new List<FilterNode>();
                Absorb(left);
                Absorb(right);
                result = target == FilterGroupOperator.And
                    ? FilterNode.And(children.ToArray())
                    : FilterNode.Or(children.ToArray());
                return true;

                void Absorb(FilterNode child)
                {
                    if (child is FilterGroupNode nested && nested.Operator == target)
                        children.AddRange(nested.Children);
                    else
                        children.Add(child);
                }
            }

            case CriteriaNotNode not:
            {
                if (!TryToStructuralNode(not.Operand, fields, out var inner))
                    return false;
                result = FilterNode.Not(inner);
                return true;
            }

            case CriteriaBinaryNode comparison when TryFieldVsLiteral(comparison, fields, out var key, out var op, out var value):
                result = FilterNode.Condition(key, op, value);
                return true;

            case CriteriaBetweenNode between
                when ResolveField(between.Operand, fields) is { } key &&
                     between.Low is CriteriaLiteralNode { Value: not null } low &&
                     between.High is CriteriaLiteralNode { Value: not null } high &&
                     KeyTypeOf(between.Operand, fields) is { } betweenKeyType &&
                     IsExactForKey(low.Value, betweenKeyType) && IsExactForKey(high.Value, betweenKeyType):
                result = FilterNode.Condition(key, FilterOperator.Between, low.Value, high.Value);
                return true;

            case CriteriaInNode inNode when ResolveField(inNode.Operand, fields) is { } key &&
                                            KeyTypeOf(inNode.Operand, fields) is { } inKeyType:
            {
                var values = new List<object?>(inNode.Items.Count);
                foreach (var item in inNode.Items)
                {
                    if (item is not CriteriaLiteralNode literal || !IsExactForKey(literal.Value, inKeyType))
                        return false;
                    values.Add(literal.Value);
                }
                result = FilterNode.InSet(key, values);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryFieldVsLiteral(
        CriteriaBinaryNode node, IReadOnlyList<Field> fields,
        [NotNullWhen(true)] out object? columnKey, out FilterOperator op, out object? value)
    {
        columnKey = null;
        value = null;
        op = default;

        FilterOperator? Map(CriteriaBinaryOperator o, bool flipped) => o switch
        {
            CriteriaBinaryOperator.Equal => FilterOperator.Equals,
            CriteriaBinaryOperator.NotEqual => FilterOperator.NotEquals,
            CriteriaBinaryOperator.LessThan => flipped ? FilterOperator.GreaterThan : FilterOperator.LessThan,
            CriteriaBinaryOperator.LessThanOrEqual => flipped ? FilterOperator.GreaterThanOrEqual : FilterOperator.LessThanOrEqual,
            CriteriaBinaryOperator.GreaterThan => flipped ? FilterOperator.LessThan : FilterOperator.GreaterThan,
            CriteriaBinaryOperator.GreaterThanOrEqual => flipped ? FilterOperator.LessThanOrEqual : FilterOperator.GreaterThanOrEqual,
            _ => null,
        };

        // [Field] op literal — the canonical shape…
        if (ResolveField(node.Left, fields) is { } leftKey && node.Right is CriteriaLiteralNode rightLiteral)
        {
            var mapped = Map(node.Operator, flipped: false);
            if (mapped is null || (rightLiteral.Value is null && mapped is not (FilterOperator.Equals or FilterOperator.NotEquals)))
                return false;
            if (KeyTypeOf(node.Left, fields) is { } keyType && !IsExactForKey(rightLiteral.Value, keyType))
                return false; // lossy literal — the compiled path keeps exact semantics
            columnKey = leftKey;
            op = mapped.Value;
            value = rightLiteral.Value;
            return true;
        }

        // …and the flipped literal op [Field].
        if (ResolveField(node.Right, fields) is { } rightKey && node.Left is CriteriaLiteralNode leftLiteral)
        {
            var mapped = Map(node.Operator, flipped: true);
            if (mapped is null || leftLiteral.Value is null)
                return false;
            if (KeyTypeOf(node.Right, fields) is { } keyType && !IsExactForKey(leftLiteral.Value, keyType))
                return false;
            columnKey = rightKey;
            op = mapped.Value;
            value = leftLiteral.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a literal survives the Condition lane's literal conversion EXACTLY (§9.1 panel):
    /// <c>ConvertLiteral</c> rides <c>Convert.ChangeType</c>, which ROUNDS — <c>[IntCol] = 2.5</c>
    /// would silently become <c>= 2</c>. A literal that does not round-trip forces the compiled
    /// path (always semantically exact).
    /// </summary>
    private static bool IsExactForKey(object? value, Type keyType)
    {
        if (value is null)
            return true; // null-ness is type-independent

        var target = Nullable.GetUnderlyingType(keyType) ?? keyType;
        var source = value.GetType();
        if (source == target)
            return true;
        if (target == typeof(string))
            return source == typeof(string);
        if (target.IsEnum)
            return source == typeof(string) && Enum.TryParse(target, (string)value, ignoreCase: true, out _);
        if (target == typeof(DateOnly))
            return value is DateTime { TimeOfDay.Ticks: 0 } or DateOnly;
        if (target == typeof(DateTime))
            return value is DateOnly or DateTime;

        // Numerics: convert forward and back — a lossy narrowing fails the round-trip.
        try
        {
            object converted = Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            object roundTripped = Convert.ChangeType(converted, source, CultureInfo.InvariantCulture);
            return Equals(roundTripped, value);
        }
        catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException)
        {
            return false;
        }
    }

    private static Type? KeyTypeOf(CriteriaNode node, IReadOnlyList<Field> fields)
    {
        if (node is not CriteriaFieldNode field)
            return null;
        foreach (var candidate in fields)
        {
            if (string.Equals(candidate.Name, field.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.DisplayName, field.Name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Selector.ReturnType;
            }
        }
        return null;
    }

    private static object? ResolveField(CriteriaNode node, IReadOnlyList<Field> fields)
    {
        if (node is not CriteriaFieldNode field)
            return null;
        foreach (var candidate in fields)
        {
            if (string.Equals(candidate.Name, field.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.DisplayName, field.Name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.ColumnKey;
            }
        }
        return null;
    }

    // ── FilterNode → text (the Builder ⇄ editor loop's other half) ───────────────────────────────

    /// <summary>
    /// Renders a structural tree back to criteria text (<see cref="FilterPredicateNode"/> is the one
    /// shape that cannot round-trip — it renders a placeholder comment the editor flags).
    /// </summary>
    public static string ToText(FilterNode node, Func<object, string> fieldNameOf)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(fieldNameOf);
        var builder = new StringBuilder();
        Render(node, builder, fieldNameOf, parenthesize: false);
        return builder.ToString();
    }

    private static void Render(FilterNode node, StringBuilder builder, Func<object, string> fieldNameOf, bool parenthesize)
    {
        switch (node)
        {
            case FilterGroupNode group:
            {
                if (parenthesize)
                    builder.Append('(');
                string keyword = group.Operator == FilterGroupOperator.And ? " And " : " Or ";
                for (int i = 0; i < group.Children.Count; i++)
                {
                    if (i > 0)
                        builder.Append(keyword);
                    // Children parenthesize when they are groups of the OTHER operator (precedence).
                    Render(group.Children[i], builder, fieldNameOf,
                           parenthesize: group.Children[i] is FilterGroupNode child && child.Operator != group.Operator);
                }
                if (parenthesize)
                    builder.Append(')');
                return;
            }

            case FilterNotNode not:
                builder.Append("Not ");
                Render(not.Child, builder, fieldNameOf, parenthesize: not.Child is FilterGroupNode);
                return;

            case FilterConditionNode condition:
            {
                builder.Append('[').Append(fieldNameOf(condition.ColumnKey)).Append(']');
                if (condition.Operator == FilterOperator.Between)
                {
                    builder.Append(" Between ");
                    RenderLiteral(condition.Value, builder);
                    builder.Append(" And ");
                    RenderLiteral(condition.SecondValue, builder);
                    return;
                }
                builder.Append(condition.Operator switch
                {
                    FilterOperator.Equals => " = ",
                    FilterOperator.NotEquals => " <> ",
                    FilterOperator.LessThan => " < ",
                    FilterOperator.LessThanOrEqual => " <= ",
                    FilterOperator.GreaterThan => " > ",
                    FilterOperator.GreaterThanOrEqual => " >= ",
                    FilterOperator.Contains => " Like ",
                    FilterOperator.StartsWith => " Like ",
                    _ => " = ",
                });
                if (condition.Operator == FilterOperator.Contains)
                    RenderLiteral($"%{condition.Value}%", builder);
                else if (condition.Operator == FilterOperator.StartsWith)
                    RenderLiteral($"{condition.Value}%", builder);
                else
                    RenderLiteral(condition.Value, builder);
                return;
            }

            case FilterValueSetNode set:
            {
                builder.Append('[').Append(fieldNameOf(set.ColumnKey)).Append("] In (");
                for (int i = 0; i < set.Values.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    RenderLiteral(set.Values[i], builder);
                }
                builder.Append(')');
                return;
            }

            case FilterPredicateNode:
                builder.Append("/* custom predicate — not text-representable */");
                return;
        }
    }

    private static void RenderLiteral(object? value, StringBuilder builder)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string s:
                builder.Append('\'').Append(s.Replace("'", "''")).Append('\'');
                break;
            case bool b:
                builder.Append(b ? "true" : "false");
                break;
            case DateOnly d:
                builder.Append('#').Append(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('#');
                break;
            case DateTime dt:
                builder.Append('#').Append(dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('#');
                break;
            default:
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
