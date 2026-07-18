using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace Cursorial.UI.DataViews.Shaping.Expressions;

/// <summary>
/// Lowers a criteria AST to a typed row predicate (design doc §9.1): field refs inline the column
/// selectors (parameter-rebound — the compiled predicate reads the row directly, no per-evaluation
/// lookup), numeric operands promote (int→long→decimal→double), and the comparison semantics are
/// THE ENGINE'S (§9.1 panel-unification — both lowering paths must agree): string comparisons run
/// the involved column's SortMode, nullable relational operators use null-first total order
/// (<c>[A] &lt; 5</c> is TRUE for a null A, exactly like the Condition lane), <c>= null</c>/
/// <c>&lt;&gt; null</c> test null-ness, <c>Like</c> compiles its wildcard pattern (<c>%</c> any
/// run, <c>_</c> one char, <c>[%]</c>/<c>[_]</c> literals) to a regex honoring the column mode,
/// and <c>Between</c> is inclusive. Bind/type errors are positioned diagnostics — never throws
/// past <see cref="Compile"/>.
/// </summary>
[RequiresDynamicCode("Compiles expression trees specialized to the row type.")]
public static class CriteriaCompiler
{
    /// <summary>One bindable field: the canonical name (FieldName), an optional display alias (the header), the row→value selector, and the column's string-comparison mode (the engine's semantic authority — comparisons must match the Condition lane).</summary>
    public readonly record struct FieldBinding(string Name, string? DisplayName, LambdaExpression Selector,
                                               StringComparison StringMode = StringComparison.CurrentCulture);

    /// <summary>The compile product: a boolean row lambda (null on failure) + diagnostics.</summary>
    public readonly record struct Result(LambdaExpression? Predicate, IReadOnlyList<CriteriaDiagnostic> Diagnostics)
    {
        public bool IsValid => Predicate is not null && Diagnostics.Count == 0;
    }

    /// <summary>Compiles <paramref name="root"/> into <c>Expression&lt;Func&lt;rowType,bool&gt;&gt;</c>.</summary>
    public static Result Compile(CriteriaNode root, Type rowType, IReadOnlyList<FieldBinding> fields)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rowType);
        ArgumentNullException.ThrowIfNull(fields);

        var state = new State(rowType, fields);
        var body = state.Build(root);
        if (body is null)
            return new Result(null, state.Diagnostics);

        if (body.Type != typeof(bool))
        {
            if (Nullable.GetUnderlyingType(body.Type) == typeof(bool))
            {
                body = Expression.Equal(body, Expression.Constant(true, typeof(bool?))); // null ⇒ false
            }
            else
            {
                state.Error(root, $"The expression is not boolean (it is '{Friendly(body.Type)}').");
                return new Result(null, state.Diagnostics);
            }
        }

        var lambda = Expression.Lambda(typeof(Func<,>).MakeGenericType(rowType, typeof(bool)), body, state.Row);
        return new Result(lambda, state.Diagnostics);
    }

    private static string Friendly(Type type) => Nullable.GetUnderlyingType(type) is { } u ? $"{u.Name}?" : type.Name;

    private sealed class State(Type rowType, IReadOnlyList<FieldBinding> fields)
    {
        public readonly ParameterExpression Row = Expression.Parameter(rowType, "row");
        public readonly List<CriteriaDiagnostic> Diagnostics = [];

        // The string-comparison mode per built FIELD expression (instance-keyed — each Build of a
        // field ref produces a fresh rebound body). Comparisons consult it so the compiled lane and
        // the engine's Condition lane share ONE semantic authority (§9.1 panel-unification).
        private readonly Dictionary<Expression, StringComparison> _fieldModes = new(ReferenceEqualityComparer.Instance);

        public void Error(CriteriaNode at, string message)
        {
            if (Diagnostics.Count < 8)
                Diagnostics.Add(new CriteriaDiagnostic(message, at.Start, at.Length));
        }

        public Expression? Build(CriteriaNode node)
        {
            switch (node)
            {
                case CriteriaLiteralNode literal:
                    return literal.Value is null
                        ? Expression.Constant(null, typeof(object)) // typeless — comparison sites specialize
                        : Expression.Constant(literal.Value);

                case CriteriaFieldNode field:
                {
                    var binding = Resolve(field.Name, out bool ambiguous);
                    if (binding is null)
                    {
                        Error(field, ambiguous
                            ? $"'[{field.Name}]' is ambiguous — more than one column shows that header; use the field name."
                            : $"Unknown field '[{field.Name}]'.");
                        return null;
                    }
                    // Inline the selector body with its parameter rebound to OUR row parameter.
                    var built = new RebindVisitor(binding.Value.Selector.Parameters[0], Row).Visit(binding.Value.Selector.Body)!;
                    if (Unwrap(built.Type) == typeof(string))
                        _fieldModes[built] = binding.Value.StringMode;
                    return built;
                }

                case CriteriaNotNode not:
                {
                    var operand = Build(not.Operand);
                    if (operand is null)
                        return null;
                    if (operand.Type != typeof(bool))
                    {
                        Error(not, "'Not' requires a boolean operand.");
                        return null;
                    }
                    return Expression.Not(operand);
                }

                case CriteriaNegateNode negate:
                {
                    var operand = Build(negate.Operand);
                    if (operand is null)
                        return null;
                    if (!IsNumeric(operand.Type))
                    {
                        Error(negate, "Unary '-' requires a numeric operand.");
                        return null;
                    }
                    return Expression.Negate(operand);
                }

                case CriteriaBinaryNode binary:
                    return BuildBinary(binary);

                case CriteriaInNode inNode:
                {
                    var operand = Build(inNode.Operand);
                    if (operand is null)
                        return null;
                    Expression? chain = null;
                    foreach (var item in inNode.Items)
                    {
                        var candidate = Build(item);
                        if (candidate is null)
                            return null;
                        var equal = Comparison(CriteriaBinaryOperator.Equal, operand, candidate, inNode);
                        if (equal is null)
                            return null;
                        chain = chain is null ? equal : Expression.OrElse(chain, equal);
                    }
                    return chain ?? Expression.Constant(false);
                }

                case CriteriaBetweenNode between:
                {
                    var operand = Build(between.Operand);
                    var low = Build(between.Low);
                    var high = Build(between.High);
                    if (operand is null || low is null || high is null)
                        return null;
                    var ge = Comparison(CriteriaBinaryOperator.GreaterThanOrEqual, operand, low, between);
                    var le = Comparison(CriteriaBinaryOperator.LessThanOrEqual, operand, high, between);
                    return ge is null || le is null ? null : Expression.AndAlso(ge, le);
                }

                case CriteriaFunctionNode function:
                    return BuildFunction(function);

                default:
                    Error(node, "Unsupported expression node.");
                    return null;
            }
        }

        private FieldBinding? Resolve(string name) => Resolve(name, out _);

        /// <summary>Exact FieldName wins; else a UNIQUE display alias; an ambiguous alias is a diagnostic, not a guess (§9.1 panel).</summary>
        private FieldBinding? Resolve(string name, out bool ambiguous)
        {
            ambiguous = false;
            foreach (var field in fields)
            {
                if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                    return field;
            }
            FieldBinding? match = null;
            foreach (var field in fields)
            {
                if (string.Equals(field.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (match is not null)
                    {
                        ambiguous = true;
                        return null;
                    }
                    match = field;
                }
            }
            return match;
        }

        // ── Binary lowering ──────────────────────────────────────────────────────────────────────

        private Expression? BuildBinary(CriteriaBinaryNode node)
        {
            if (node.Operator is CriteriaBinaryOperator.And or CriteriaBinaryOperator.Or)
            {
                var left = Build(node.Left);
                var right = Build(node.Right);
                if (left is null || right is null)
                    return null;
                if (left.Type != typeof(bool) || right.Type != typeof(bool))
                {
                    Error(node, $"'{node.Operator}' requires boolean operands.");
                    return null;
                }
                return node.Operator == CriteriaBinaryOperator.And
                    ? Expression.AndAlso(left, right)
                    : Expression.OrElse(left, right);
            }

            if (node.Operator is CriteriaBinaryOperator.Add or CriteriaBinaryOperator.Subtract
                              or CriteriaBinaryOperator.Multiply or CriteriaBinaryOperator.Divide
                              or CriteriaBinaryOperator.Modulo)
            {
                var left = Build(node.Left);
                var right = Build(node.Right);
                if (left is null || right is null)
                    return null;

                // String '+' is concatenation (the display-text convenience).
                if (node.Operator == CriteriaBinaryOperator.Add &&
                    (left.Type == typeof(string) || right.Type == typeof(string)))
                {
                    return Expression.Call(
                        typeof(string).GetMethod(nameof(string.Concat), [typeof(object), typeof(object)])!,
                        Expression.Convert(left, typeof(object)), Expression.Convert(right, typeof(object)));
                }

                if (!PromoteNumeric(ref left, ref right))
                {
                    Error(node, $"'{Symbol(node.Operator)}' requires numeric operands.");
                    return null;
                }
                return node.Operator switch
                {
                    CriteriaBinaryOperator.Add => Expression.Add(left, right),
                    CriteriaBinaryOperator.Subtract => Expression.Subtract(left, right),
                    CriteriaBinaryOperator.Multiply => Expression.Multiply(left, right),
                    CriteriaBinaryOperator.Divide => Expression.Divide(left, right),
                    _ => Expression.Modulo(left, right),
                };
            }

            if (node.Operator == CriteriaBinaryOperator.Like)
                return BuildLike(node);

            var l = Build(node.Left);
            var r = Build(node.Right);
            if (l is null || r is null)
                return null;
            return Comparison(node.Operator, l, r, node);
        }

        /// <summary>The comparison lowering shared by =/&lt;&gt;/relational, In items, and Between bounds.</summary>
        private Expression? Comparison(CriteriaBinaryOperator op, Expression left, Expression right, CriteriaNode at)
        {
            // Null tests: `= null` / `<> null` check null-ness directly (relational-vs-null is an error).
            bool leftNull = IsNullLiteral(left);
            bool rightNull = IsNullLiteral(right);
            if (leftNull || rightNull)
            {
                if (op is not (CriteriaBinaryOperator.Equal or CriteriaBinaryOperator.NotEqual))
                {
                    Error(at, "Relational operators cannot compare against null — use = null or <> null.");
                    return null;
                }
                var operand = leftNull ? right : left;
                Expression isNull;
                if (!operand.Type.IsValueType)
                    isNull = Expression.Equal(operand, Expression.Constant(null, operand.Type));
                else if (Nullable.GetUnderlyingType(operand.Type) is not null)
                    isNull = Expression.Not(Expression.Property(operand, "HasValue"));
                else
                    isNull = Expression.Constant(false); // a non-nullable value is never null
                return op == CriteriaBinaryOperator.Equal ? isNull : Expression.Not(isNull);
            }

            // Strings: the involved COLUMN's comparison mode (the engine's Condition lane runs the
            // column comparison — both lowering paths must agree or the same expression filters
            // differently by path; §9.1 panel-unification). No field involved ⇒ CurrentCulture.
            if (Unwrap(left.Type) == typeof(string) && Unwrap(right.Type) == typeof(string))
            {
                var mode = _fieldModes.TryGetValue(left, out var lm) ? lm
                         : _fieldModes.TryGetValue(right, out var rm) ? rm
                         : StringComparison.CurrentCulture;
                var compare = Expression.Call(
                    typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string), typeof(StringComparison)])!,
                    left, right, Expression.Constant(mode));
                var zero = Expression.Constant(0);
                return op switch
                {
                    CriteriaBinaryOperator.Equal => Expression.Equal(compare, zero),
                    CriteriaBinaryOperator.NotEqual => Expression.NotEqual(compare, zero),
                    CriteriaBinaryOperator.LessThan => Expression.LessThan(compare, zero),
                    CriteriaBinaryOperator.LessThanOrEqual => Expression.LessThanOrEqual(compare, zero),
                    CriteriaBinaryOperator.GreaterThan => Expression.GreaterThan(compare, zero),
                    _ => Expression.GreaterThanOrEqual(compare, zero),
                };
            }

            if (!Unify(ref left, ref right))
            {
                Error(at, $"Cannot compare '{Friendly(left.Type)}' with '{Friendly(right.Type)}'.");
                return null;
            }

            // Nullable operands: null-first TOTAL-ORDER semantics — `[A] < 5` is TRUE for a null A
            // (null sorts first), exactly the engine's Condition-lane comparison. One semantic
            // authority across both lowering paths (§9.1 panel-unification).
            if (Nullable.GetUnderlyingType(left.Type) is { } underlying &&
                typeof(IComparable<>).MakeGenericType(underlying).IsAssignableFrom(underlying))
            {
                var compare = Expression.Condition(
                    Expression.Property(left, "HasValue"),
                    Expression.Condition(
                        Expression.Property(right, "HasValue"),
                        Expression.Call(Expression.Property(left, "Value"),
                                        underlying.GetMethod(nameof(IComparable<int>.CompareTo), [underlying])!,
                                        Expression.Property(right, "Value")),
                        Expression.Constant(1)),
                    Expression.Condition(
                        Expression.Property(right, "HasValue"),
                        Expression.Constant(-1),
                        Expression.Constant(0)));
                var zeroConstant = Expression.Constant(0);
                return op switch
                {
                    CriteriaBinaryOperator.Equal => Expression.Equal(compare, zeroConstant),
                    CriteriaBinaryOperator.NotEqual => Expression.NotEqual(compare, zeroConstant),
                    CriteriaBinaryOperator.LessThan => Expression.LessThan(compare, zeroConstant),
                    CriteriaBinaryOperator.LessThanOrEqual => Expression.LessThanOrEqual(compare, zeroConstant),
                    CriteriaBinaryOperator.GreaterThan => Expression.GreaterThan(compare, zeroConstant),
                    _ => Expression.GreaterThanOrEqual(compare, zeroConstant),
                };
            }

            try
            {
                return op switch
                {
                    CriteriaBinaryOperator.Equal => Expression.Equal(left, right),
                    CriteriaBinaryOperator.NotEqual => Expression.NotEqual(left, right),
                    CriteriaBinaryOperator.LessThan => Expression.LessThan(left, right),
                    CriteriaBinaryOperator.LessThanOrEqual => Expression.LessThanOrEqual(left, right),
                    CriteriaBinaryOperator.GreaterThan => Expression.GreaterThan(left, right),
                    _ => Expression.GreaterThanOrEqual(left, right),
                };
            }
            catch (InvalidOperationException)
            {
                // No comparison operator on the type (e.g. relational over bool/Guid); fall back to
                // IComparable for relational when available, else diagnose.
                if (typeof(IComparable).IsAssignableFrom(Unwrap(left.Type)) &&
                    op is not (CriteriaBinaryOperator.Equal or CriteriaBinaryOperator.NotEqual))
                {
                    var compare = Expression.Call(
                        Expression.Convert(left, typeof(IComparable)),
                        typeof(IComparable).GetMethod(nameof(IComparable.CompareTo))!,
                        Expression.Convert(right, typeof(object)));
                    var zero = Expression.Constant(0);
                    return op switch
                    {
                        CriteriaBinaryOperator.LessThan => Expression.LessThan(compare, zero),
                        CriteriaBinaryOperator.LessThanOrEqual => Expression.LessThanOrEqual(compare, zero),
                        CriteriaBinaryOperator.GreaterThan => Expression.GreaterThan(compare, zero),
                        _ => Expression.GreaterThanOrEqual(compare, zero),
                    };
                }
                Error(at, $"'{Friendly(left.Type)}' does not support this comparison.");
                return null;
            }
        }

        private Expression? BuildLike(CriteriaBinaryNode node)
        {
            var left = Build(node.Left);
            if (left is null)
                return null;
            if (Unwrap(left.Type) != typeof(string))
            {
                Error(node, "Like requires a string operand.");
                return null;
            }
            if (node.Right is not CriteriaLiteralNode { Value: string pattern })
            {
                Error(node, "Like requires a literal string pattern ('%' any run, '_' one character).");
                return null;
            }

            // Translate once at compile; the regex bakes into the tree as a constant. Escapes: [%]
            // and [_] match the literal characters (the DevExpress criteria escape). Case follows
            // the column's comparison mode (§9.1 panel — one semantic authority).
            var mode = _fieldModes.TryGetValue(left, out var likeMode) ? likeMode : StringComparison.CurrentCulture;
            bool ignoreCase = mode is StringComparison.CurrentCultureIgnoreCase
                                   or StringComparison.InvariantCultureIgnoreCase
                                   or StringComparison.OrdinalIgnoreCase;
            var translated = new System.Text.StringBuilder("^");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '[' && i + 2 < pattern.Length && pattern[i + 2] == ']' && (pattern[i + 1] is '%' or '_'))
                {
                    translated.Append(Regex.Escape(pattern[i + 1].ToString()));
                    i += 2;
                }
                else if (c == '%')
                    translated.Append(".*");
                else if (c == '_')
                    translated.Append('.');
                else
                    translated.Append(Regex.Escape(c.ToString()));
            }
            translated.Append('$');
            var regex = new Regex(translated.ToString(),
                (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant | RegexOptions.Compiled);

            return Expression.Call(
                Expression.Constant(regex),
                typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string)])!,
                Expression.Coalesce(left, Expression.Constant(string.Empty)));
        }

        // ── Functions ────────────────────────────────────────────────────────────────────────────

        private Expression? BuildFunction(CriteriaFunctionNode node)
        {
            var arguments = new Expression[node.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                var built = Build(node.Arguments[i]);
                if (built is null)
                    return null;
                arguments[i] = built;
            }

            Expression? Str(int index) // null-guarded string argument
            {
                if (index >= arguments.Length || Unwrap(arguments[index].Type) != typeof(string))
                {
                    Error(node, $"{node.Name} requires a string argument {index + 1}.");
                    return null;
                }
                return Expression.Coalesce(arguments[index], Expression.Constant(string.Empty));
            }

            switch (node.Name.ToUpperInvariant())
            {
                case "CONTAINS" or "STARTSWITH" or "ENDSWITH":
                {
                    if (arguments.Length != 2)
                    {
                        Error(node, $"{node.Name} takes (text, fragment).");
                        return null;
                    }
                    var text = Str(0);
                    var fragment = Str(1);
                    if (text is null || fragment is null)
                        return null;
                    string method = node.Name.ToUpperInvariant() switch
                    {
                        "CONTAINS" => nameof(string.Contains),
                        "STARTSWITH" => nameof(string.StartsWith),
                        _ => nameof(string.EndsWith),
                    };
                    return Expression.Call(text, typeof(string).GetMethod(method, [typeof(string), typeof(StringComparison)])!,
                                           fragment, Expression.Constant(StringComparison.CurrentCultureIgnoreCase));
                }

                case "UPPER" or "LOWER":
                {
                    var text = Str(0);
                    if (text is null || arguments.Length != 1)
                        return null;
                    return Expression.Call(text, typeof(string).GetMethod(
                        node.Name.ToUpperInvariant() == "UPPER" ? nameof(string.ToUpper) : nameof(string.ToLower),
                        Type.EmptyTypes)!);
                }

                case "LEN":
                {
                    var text = Str(0);
                    return text is null || arguments.Length != 1 ? null : Expression.Property(text, nameof(string.Length));
                }

                case "TRIM":
                {
                    var text = Str(0);
                    return text is null || arguments.Length != 1
                        ? null
                        : Expression.Call(text, typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!);
                }

                case "ISNULLOREMPTY":
                {
                    if (arguments.Length != 1 || Unwrap(arguments[0].Type) != typeof(string))
                    {
                        Error(node, "IsNullOrEmpty takes one string argument.");
                        return null;
                    }
                    return Expression.Call(typeof(string).GetMethod(nameof(string.IsNullOrEmpty))!, arguments[0]);
                }

                case "ISNULL":
                {
                    if (arguments.Length != 2)
                    {
                        Error(node, "IsNull takes (value, fallback).");
                        return null;
                    }
                    var a = arguments[0];
                    var b = arguments[1];
                    if (!a.Type.IsValueType || Nullable.GetUnderlyingType(a.Type) is not null)
                    {
                        if (!Unify(ref a, ref b))
                        {
                            Error(node, "IsNull's operands have incompatible types.");
                            return null;
                        }
                        return Expression.Coalesce(a, b);
                    }
                    return a; // a non-nullable value is never null — the fallback is dead
                }

                case "ABS":
                {
                    if (arguments.Length != 1 || !IsNumeric(arguments[0].Type))
                    {
                        Error(node, "Abs takes one numeric argument.");
                        return null;
                    }
                    var value = arguments[0];
                    return Expression.Call(typeof(Math).GetMethod(nameof(Math.Abs), [Unwrap(value.Type)])!, DropNullable(value));
                }

                case "ROUND":
                {
                    if (arguments.Length is not (1 or 2) || !IsNumeric(arguments[0].Type))
                    {
                        Error(node, "Round takes (value[, digits]).");
                        return null;
                    }
                    var value = DropNullable(arguments[0]);
                    if (Unwrap(value.Type) != typeof(double) && Unwrap(value.Type) != typeof(decimal))
                        value = Expression.Convert(value, typeof(double));
                    var digits = arguments.Length == 2
                        ? (Expression)Expression.Convert(DropNullable(arguments[1]), typeof(int))
                        : Expression.Constant(0);
                    return Expression.Call(typeof(Math).GetMethod(nameof(Math.Round), [value.Type, typeof(int)])!, value, digits);
                }

                default:
                    Error(node, $"Unknown function '{node.Name}'.");
                    return null;
            }
        }

        // ── Type plumbing ────────────────────────────────────────────────────────────────────────

        private static bool IsNullLiteral(Expression e) => e is ConstantExpression { Value: null };

        private static Type Unwrap(Type t) => Nullable.GetUnderlyingType(t) ?? t;

        private static Expression DropNullable(Expression e)
            => Nullable.GetUnderlyingType(e.Type) is null ? e : Expression.Coalesce(e, Expression.Default(Unwrap(e.Type)));

        private static readonly Type[] NumericLadder =
            [typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
             typeof(long), typeof(ulong), typeof(decimal), typeof(double)];

        private static bool IsNumeric(Type t) => Array.IndexOf(NumericLadder, Unwrap(t)) >= 0 || Unwrap(t) == typeof(float);

        /// <summary>Numeric promotion to the widest of the pair (float folds into double; decimal×double → double).</summary>
        private static bool PromoteNumeric(ref Expression left, ref Expression right)
        {
            if (!IsNumeric(left.Type) || !IsNumeric(right.Type))
                return false;

            var l = Unwrap(left.Type) == typeof(float) ? typeof(double) : Unwrap(left.Type);
            var r = Unwrap(right.Type) == typeof(float) ? typeof(double) : Unwrap(right.Type);
            Type target;
            if (l == typeof(double) || r == typeof(double))
                target = typeof(double);
            else if (l == typeof(decimal) || r == typeof(decimal))
                target = typeof(decimal);
            else if (l == typeof(ulong) || r == typeof(ulong))
                target = typeof(decimal); // ulong × signed meets safely in decimal
            else if (l == typeof(long) || r == typeof(long) || l == typeof(uint) || r == typeof(uint))
                target = typeof(long);
            else
                target = typeof(int);

            bool nullable = Nullable.GetUnderlyingType(left.Type) is not null ||
                            Nullable.GetUnderlyingType(right.Type) is not null;
            var final = nullable ? typeof(Nullable<>).MakeGenericType(target) : target;
            if (left.Type != final)
                left = Expression.Convert(left, final);
            if (right.Type != final)
                right = Expression.Convert(right, final);
            return true;
        }

        /// <summary>Unifies operand types for comparison: numeric promotion, enum↔string, exact types, nullable lifting.</summary>
        private bool Unify(ref Expression left, ref Expression right)
        {
            if (PromoteNumeric(ref left, ref right))
                return true;

            var l = Unwrap(left.Type);
            var r = Unwrap(right.Type);

            // Enum vs string-literal: parse the name at compile.
            if (l.IsEnum && right is ConstantExpression { Value: string enumName })
            {
                if (!Enum.TryParse(l, enumName, ignoreCase: true, out var parsed))
                    return false;
                right = Expression.Constant(parsed, left.Type);
                return true;
            }
            if (r.IsEnum && left is ConstantExpression { Value: string enumName2 })
            {
                if (!Enum.TryParse(r, enumName2, ignoreCase: true, out var parsed))
                    return false;
                left = Expression.Constant(parsed, right.Type);
                return true;
            }

            // DateOnly vs DateTime literals (the tokenizer may produce either).
            if (l == typeof(DateOnly) && right is ConstantExpression { Value: DateTime dt })
            {
                right = Expression.Constant(DateOnly.FromDateTime(dt), typeof(DateOnly));
            }
            else if (l == typeof(DateTime) && right is ConstantExpression { Value: DateOnly dateOnly })
            {
                right = Expression.Constant(dateOnly.ToDateTime(TimeOnly.MinValue), typeof(DateTime));
            }

            if (Unwrap(left.Type) != Unwrap(right.Type))
                return false;

            // Lift both when either is nullable.
            if (Nullable.GetUnderlyingType(left.Type) is not null && Nullable.GetUnderlyingType(right.Type) is null)
                right = Expression.Convert(right, left.Type);
            else if (Nullable.GetUnderlyingType(right.Type) is not null && Nullable.GetUnderlyingType(left.Type) is null)
                left = Expression.Convert(left, right.Type);
            return true;
        }

        private static string Symbol(CriteriaBinaryOperator op) => op switch
        {
            CriteriaBinaryOperator.Add => "+",
            CriteriaBinaryOperator.Subtract => "-",
            CriteriaBinaryOperator.Multiply => "*",
            CriteriaBinaryOperator.Divide => "/",
            CriteriaBinaryOperator.Modulo => "%",
            _ => op.ToString(),
        };
    }

    /// <summary>Substitutes a lambda's parameter with the compiler's row parameter (selector inlining).</summary>
    private sealed class RebindVisitor(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
