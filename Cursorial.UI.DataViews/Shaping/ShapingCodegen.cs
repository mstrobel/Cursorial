using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Cursorial.UI.DataViews.Shaping;

/// <summary>
/// Formats <paramref name="value"/> into <paramref name="destination"/>; returns the chars written,
/// or <b>−1 when the destination is too small</b> (the caller grows its buffer and retries — never
/// a partial write the caller must detect). The band cache's per-cell formatting lane (design doc
/// §2.2 panel amendment): no per-cell string allocation. Built by
/// <see cref="ShapingCodegen.CreateSpanFormatter{TKey}"/>.
/// </summary>
internal delegate int SpanFormat<in TKey>(TKey value, Span<char> destination);

/// <summary>
/// The engine's single code-generation site (design doc §2.2 / invariant 8): every typed delegate the
/// shaping pipeline runs hot — key getters, key comparisons, the fused multi-column slot comparer,
/// formatters — is compiled here from LINQ expression trees, specialized to the row/key types so
/// nothing boxes on the compare/format paths. Requires dynamic code (the loader assembly already
/// does); an interpreted fallback is a future seam behind this class.
/// </summary>
[RequiresDynamicCode("The shaping engine compiles expression trees specialized to the row type.")]
internal static class ShapingCodegen
{
    // ── Getters ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Compiles a row→key lambda (a caller expression, or one built by <see cref="BuildPropertyPathLambda"/>).</summary>
    public static Func<TRow, TKey> CompileGetter<TRow, TKey>(Expression<Func<TRow, TKey>> selector)
        => selector.Compile();

    /// <summary>
    /// Builds a null-propagating row→member lambda from a dotted property path (<c>"Customer.Name"</c>):
    /// a null reference-typed hop yields the key type's default instead of throwing (WPF binding parity —
    /// an unresolvable path is an absent value, not a crash).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Reflects members off an intermediate Expression.Type (a BCL Type that carries no DAM); the shaping engine compiles typed trees (RequiresDynamicCode) — the property type IS the runtime row member type.")]
    public static LambdaExpression BuildPropertyPathLambda(Type rowType, string propertyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(propertyPath);

        var row = Expression.Parameter(rowType, "row");
        Expression body = row;

        foreach (var segment in propertyPath.Split('.'))
        {
            var member = (MemberInfo?)body.Type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance)
                         ?? body.Type.GetField(segment, BindingFlags.Public | BindingFlags.Instance)
                         ?? throw new ArgumentException(
                             $"'{body.Type.Name}' has no public instance property or field '{segment}' (path '{propertyPath}').",
                             nameof(propertyPath));

            Expression access = Expression.MakeMemberAccess(body, member);

            // Null-propagate through reference-typed intermediates: row.Customer is null ⇒ default(TKey).
            if (!body.Type.IsValueType && body != row)
            {
                body = Expression.Condition(
                    Expression.Equal(body, Expression.Constant(null, body.Type)),
                    Expression.Default(access.Type),
                    access);
            }
            else
            {
                body = access;
            }
        }

        return Expression.Lambda(body, row);
    }

    // ── Key comparisons ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ascending, nulls-first comparison for a key type: <see cref="IComparable{T}"/> value types
    /// call <c>CompareTo</c> directly (no interface dispatch, no box); <see cref="Nullable{T}"/> and
    /// reference keys order null first; strings honor <paramref name="stringComparison"/> (default
    /// CurrentCulture — the framework's formatting convention; Ordinal is the perf opt-in).
    /// </summary>
    public static Comparison<TKey> CreateKeyComparison<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TKey>(StringComparison stringComparison = StringComparison.CurrentCulture)
    {
        if (typeof(TKey) == typeof(string))
        {
            var comparer = StringComparer.FromComparison(stringComparison);
            return (Comparison<TKey>)(object)new Comparison<string?>((a, b) =>
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a is null) return -1;
                if (b is null) return 1;
                return comparer.Compare(a, b);
            });
        }

        var type = typeof(TKey);
        var underlying = Nullable.GetUnderlyingType(type);

        if (underlying is not null &&
            typeof(IComparable<>).MakeGenericType(underlying).IsAssignableFrom(underlying))
        {
            // Nullable<V>: nulls first, then V.CompareTo — compiled so V never boxes.
            var a = Expression.Parameter(type, "a");
            var b = Expression.Parameter(type, "b");
            var body = Expression.Condition(
                Expression.Property(a, "HasValue"),
                Expression.Condition(
                    Expression.Property(b, "HasValue"),
                    Expression.Call(
                        Expression.Property(a, "Value"),
                        underlying.GetMethod(nameof(IComparable<int>.CompareTo), [underlying])!,
                        Expression.Property(b, "Value")),
                    Expression.Constant(1)),
                Expression.Condition(
                    Expression.Property(b, "HasValue"),
                    Expression.Constant(-1),
                    Expression.Constant(0)));
            return Expression.Lambda<Comparison<TKey>>(body, a, b).Compile();
        }

        if (typeof(IComparable<TKey>).IsAssignableFrom(type))
        {
            if (type.IsValueType)
            {
                // Direct CompareTo — the JIT devirtualizes the constrained call; no boxing.
                var a = Expression.Parameter(type, "a");
                var b = Expression.Parameter(type, "b");
                var body = Expression.Call(a, type.GetMethod(nameof(IComparable<int>.CompareTo), [type])!, b);
                return Expression.Lambda<Comparison<TKey>>(body, a, b).Compile();
            }

            // Reference IComparable<T>: nulls first, then CompareTo.
            return (a, b) =>
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a is null) return -1;
                if (b is null) return 1;
                return ((IComparable<TKey>)a).CompareTo(b);
            };
        }

        // Last resort — Comparer<TKey>.Default (may box non-generic IComparable value types; documented cold).
        return Comparer<TKey>.Default.Compare;
    }

    /// <summary>
    /// The compare expression for one key pair, used when fusing the multi-column comparer: primitives
    /// and <see cref="IComparable{T}"/> value types inline <c>CompareTo</c>; everything else calls the
    /// column's compiled <see cref="Comparison{T}"/> (<paramref name="comparisonConstant"/>) — one
    /// delegate hop, still no boxing.
    /// </summary>
    internal static Expression BuildKeyCompare([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type keyType, Expression keyA, Expression keyB, Expression comparisonConstant)
    {
        if (keyType.IsValueType && Nullable.GetUnderlyingType(keyType) is null &&
            typeof(IComparable<>).MakeGenericType(keyType).IsAssignableFrom(keyType))
        {
            return Expression.Call(keyA, keyType.GetMethod(nameof(IComparable<int>.CompareTo), [keyType])!, keyB);
        }

        // Invoke the column's Comparison<TKey> delegate (covers nullable/string/reference orderings).
        return Expression.Invoke(comparisonConstant, keyA, keyB);
    }

    /// <summary>
    /// Fuses the active sort levels into one compiled <c>Comparison&lt;int&gt;</c> over slot ids:
    /// per level, compare the column's typed keys (descending = operand swap); first non-zero wins;
    /// ties fall through to the tiebreak — the store's monotonic <b>insertion sequence</b> when
    /// <paramref name="sequenceOwner"/> is supplied (equal keys keep insertion order regardless of
    /// slot reuse — the DevExpress-stable tie semantics; panel finding 2026-07-18), else the raw
    /// slot id. Either way a total order: the sort needs no stability guarantee and binary-search
    /// positions are unique (design doc §2.2).
    /// </summary>
    /// <param name="levels">The sort levels (columns + directions), outermost first.</param>
    /// <param name="sequenceOwner">
    /// The store whose <see cref="RowStore.Sequences"/> vector supplies the tiebreak, indexed by
    /// slot; read through the field per call so growth re-allocation is observed.
    /// </param>
    public static Comparison<int> BuildSlotComparison(
        IReadOnlyList<(ShapedColumn Column, bool Descending)> levels, RowStore? sequenceOwner = null)
    {
        ArgumentNullException.ThrowIfNull(levels);

        var a = Expression.Parameter(typeof(int), "a");
        var b = Expression.Parameter(typeof(int), "b");

        // return-style chain: c = cmp0; if (c != 0) return c; …; return <tiebreak>;
        var result = Expression.Label(typeof(int));
        var body = new List<Expression>();
        var c = Expression.Variable(typeof(int), "c");

        foreach (var (column, descending) in levels)
        {
            body.Add(Expression.Assign(c, column.BuildCompareExpression(a, b, descending)));
            body.Add(Expression.IfThen(
                Expression.NotEqual(c, Expression.Constant(0)),
                Expression.Return(result, c)));
        }

        Expression tiebreak;
        if (sequenceOwner is null)
        {
            tiebreak = Expression.Subtract(a, b); // ids are non-negative — no overflow
        }
        else
        {
            // The field binds statically through the non-generic RowStore seam — the FieldInfo is
            // extracted from a compiler-authored expression (ldtoken-rooted), so trimming can never
            // remove it and no name probe runs (the Gallery AOT deficit).
            var sequences = Expression.Field(Expression.Constant(sequenceOwner, typeof(RowStore)), SequencesField);
            tiebreak = Expression.Call(
                Expression.ArrayIndex(sequences, a),
                typeof(long).GetMethod(nameof(long.CompareTo), [typeof(long)])!,
                Expression.ArrayIndex(sequences, b));
        }

        body.Add(Expression.Label(result, tiebreak));

        var block = Expression.Block([c], body);
        return Expression.Lambda<Comparison<int>>(block, a, b).Compile();
    }

    private static readonly FieldInfo SequencesField =
        (FieldInfo)((MemberExpression)((Expression<Func<RowStore, long[]>>)(static s => s.Sequences)).Body).Member;

    /// <summary>
    /// The AOT instantiation seed (never executes — the env guard is runtime-opaque so ILC compiles
    /// the body): statically closes the engine's generic surface over (TRow, TKey) so runtime
    /// MakeGenericType/expression construction finds native artifacts. See DataGridAotSupport.
    /// </summary>
    internal static void SeedAot<TRow, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TKey>() where TRow : notnull
    {
        if (Environment.GetEnvironmentVariable("CURSORIAL_AOT_SEED_EXECUTE") is null)
            return;

        _ = typeof(System.Linq.Expressions.Expression<Func<TRow, TKey>>);
        _ = typeof(Func<TRow, TKey>);
        // The REAL construction, not just typeof: forces the Lambda<TDelegate> method instantiation
        // and registers the constructed Expression<Func<TRow,TKey>> through the same path the
        // engine's runtime MakeGenericType lookup resolves.
        _ = System.Linq.Expressions.Expression.Lambda<Func<TRow, TKey>>(
                System.Linq.Expressions.Expression.Default(typeof(TKey)),
                System.Linq.Expressions.Expression.Parameter(typeof(TRow), "r"))
            .Compile(); // through Compile: the interpreter's typed-delegate creation thunks ride along
        _ = typeof(ShapedColumn<TRow, TKey>);
        // The reflectively-closed core itself — one reference transitively seeds the typed column
        // ctor, the compiled selector path, comparison, and formatter for this (TRow, TKey).
        _ = CreateColumnCore<TRow, TKey>(new object(), null!, StringComparison.Ordinal, null, null);
        Func<StringComparison, Comparison<TKey>> comparison = CreateKeyComparison<TKey>;
        Func<string?, CultureInfo?, Func<TKey, string>> formatter = CreateFormatter<TKey>;
        Func<string?, CultureInfo?, SpanFormat<TKey>> spanFormatter = CreateSpanFormatter<TKey>;
        GC.KeepAlive((comparison, formatter, spanFormatter));
    }

    // ── Formatters ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether <paramref name="format"/> is a usable <c>ToString</c> spec for <paramref name="type"/>.
    /// A mistyped standard specifier (e.g. <c>"N2x"</c>, <c>"Q0"</c>) throws
    /// <see cref="FormatException"/> at format time; validating ONCE per formatter build (against a
    /// sample value — numeric/date format validity is value-independent) lets a bad user- or
    /// metadata-supplied format degrade to the unformatted <c>ToString</c> instead of crashing the
    /// render/summary path. Null/empty formats and non-<see cref="IFormattable"/> types are always
    /// valid (the format is unused). Reference <see cref="IFormattable"/> types can't be sampled here
    /// and are reported valid (the codegen null-guards them; a throw there is the caller's contract).
    /// </summary>
    internal static bool IsFormatStringValid([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type type, string? format, CultureInfo? culture = null)
    {
        if (string.IsNullOrEmpty(format))
            return true;

        var valueType = Nullable.GetUnderlyingType(type) ?? type;
        if (!valueType.IsValueType || !typeof(IFormattable).IsAssignableFrom(valueType))
            return true;

        try
        {
            var sample = (IFormattable)Activator.CreateInstance(valueType)!;
            sample.ToString(format, culture ?? CultureInfo.CurrentCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The display formatter for a key type: <see cref="IFormattable"/> keys call
    /// <c>ToString(format, culture)</c> directly on the typed value (no box for value types);
    /// <see cref="Nullable{T}"/>/reference nulls format as <c>""</c>. Default culture is
    /// <see cref="CultureInfo.CurrentCulture"/> (the framework convention).
    /// </summary>
    public static Func<TKey, string> CreateFormatter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TKey>(string? format = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var type = typeof(TKey);

        // An invalid ToString spec (a mistyped standard specifier) throws FormatException at format
        // time — which would crash the summary/render path. Degrade a bad format to the unformatted
        // ToString once here instead (design §2.5 — user/metadata formats are never trusted blind).
        if (!IsFormatStringValid(type, format, culture))
            format = null;

        if (type == typeof(string))
            return (Func<TKey, string>)(object)new Func<string?, string>(static v => v ?? string.Empty);

        var underlying = Nullable.GetUnderlyingType(type);
        var valueType = underlying ?? type;

        // Enum members with [Display(Name=…)] render their designated text (a closure over the cached
        // per-type map; parsing/filtering still uses the raw member name). Only when a member declares
        // one — a plain enum keeps the fast ToString/span lanes below.
        if (valueType.IsEnum && EnumDisplay.HasDisplayNames(valueType))
        {
            if (underlying is null)
                return value => EnumDisplay.TextOf(valueType, value!);
            return value => value is null ? string.Empty : EnumDisplay.TextOf(valueType, value);
        }

        var v = Expression.Parameter(type, "v");

        Expression formatted;
        if (typeof(IFormattable).IsAssignableFrom(valueType))
        {
            var toStringMethod = valueType.GetMethod(nameof(IFormattable.ToString), [typeof(string), typeof(IFormatProvider)])!;
            Expression value = underlying is null ? v : Expression.Property(v, "Value");
            formatted = Expression.Call(value, toStringMethod,
                Expression.Constant(format, typeof(string)),
                Expression.Constant(culture, typeof(IFormatProvider)));
            if (!valueType.IsValueType)
            {
                // A null reference-typed IFormattable key formats as "" (audit W2-16 — the
                // non-IFormattable branch below always guarded; the span lane returns "" for the
                // same cell, and the doc pins the two lanes text-identical).
                formatted = Expression.Condition(
                    Expression.Equal(value, Expression.Constant(null, valueType)),
                    Expression.Constant(string.Empty),
                    formatted);
            }
        }
        else
        {
            // Non-IFormattable: plain ToString on the (unwrapped) value.
            Expression value = underlying is null ? v : Expression.Property(v, "Value");
            formatted = Expression.Call(value, valueType.GetMethod(nameof(ToString), Type.EmptyTypes)!);
            if (!valueType.IsValueType)
            {
                formatted = Expression.Condition(
                    Expression.Equal(value, Expression.Constant(null, valueType)),
                    Expression.Constant(string.Empty),
                    formatted);
            }
        }

        if (underlying is not null)
        {
            formatted = Expression.Condition(
                Expression.Property(v, "HasValue"),
                formatted,
                Expression.Constant(string.Empty));
        }

        return Expression.Lambda<Func<TKey, string>>(formatted, v).Compile();
    }

    // ── Span formatters (§2.2 panel amendment — the band cache's per-cell lane) ──────────────────

    /// <summary>
    /// Creates the allocation-free cell formatter for the band cache (design doc §2.2 panel
    /// amendment: <c>Func&lt;TKey,string&gt;</c> allocates per cell per band fill — 1–5k strings per
    /// re-anchor/tick under a feed). Lanes: <see cref="ISpanFormattable"/> keys (incl.
    /// <see cref="Nullable{T}"/> of such — unwrapped, null → 0 chars) format via a
    /// constrained-generic helper (a constrained call, no boxing — expression trees cannot take
    /// <see cref="Span{T}"/> parameters, so the closed generic resolves through
    /// <see cref="System.Reflection.MethodInfo.MakeGenericMethod"/> ONCE here, never per cell);
    /// <c>string</c> keys copy (null → 0 chars); everything else falls back to
    /// <see cref="CreateFormatter{TKey}"/>-then-copy (documented cold — allocates per call).
    /// Nothing consumes this yet — the band cache wires it in (§3.2).
    /// </summary>
    public static SpanFormat<TKey> CreateSpanFormatter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TKey>(string? format = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var type = typeof(TKey);

        // Degrade an invalid ToString spec to the unformatted lane (CreateFormatter parity) — an
        // uncaught FormatException here would crash per-cell rendering.
        if (!IsFormatStringValid(type, format, culture))
            format = null;

        if (type == typeof(string))
        {
            // String cells copy; null → 0 chars (the null-key display convention — CreateFormatter parity).
            return (SpanFormat<TKey>)(object)new SpanFormat<string?>(static (value, destination) =>
            {
                if (value is null)
                    return 0;
                if (value.Length > destination.Length)
                    return -1;
                value.CopyTo(destination);
                return value.Length;
            });
        }

        var underlying = Nullable.GetUnderlyingType(type);
        // An enum whose members declare [Display(Name)] formats through the string lane (the map
        // lookup isn't ISpanFormattable) — route it to the ToString-then-copy fallback below; a plain
        // enum keeps the fast constrained span path.
        var enumType = underlying ?? type;
        bool enumDisplay = enumType.IsEnum && EnumDisplay.HasDisplayNames(enumType);

        if (!enumDisplay && underlying is not null && typeof(ISpanFormattable).IsAssignableFrom(underlying))
        {
            return (SpanFormat<TKey>)typeof(ShapingCodegen)
                .GetMethod(nameof(CreateNullableSpanFormatterCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(underlying)
                .Invoke(null, [format, culture])!;
        }

        if (!enumDisplay && typeof(ISpanFormattable).IsAssignableFrom(type))
        {
            return (SpanFormat<TKey>)typeof(ShapingCodegen)
                .GetMethod(nameof(CreateSpanFormatterCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type)
                .Invoke(null, [format, culture])!;
        }

        // Non-ISpanFormattable fallback: ToString-then-copy. Documented COLD — allocates the string
        // per call; such keys don't belong in hot bands (the shipped key types all span-format).
        var formatter = CreateFormatter<TKey>(format, culture);
        return (value, destination) =>
        {
            string text = formatter(value);
            if (text.Length > destination.Length)
                return -1;
            text.CopyTo(destination);
            return text.Length;
        };
    }

    /// <summary>The constrained lane: <c>value.TryFormat</c> compiles to a constrained call — no box
    /// for value-typed keys. A reference-typed <see cref="ISpanFormattable"/> null → 0 chars (the
    /// null check is JIT-eliminated for structs).</summary>
    private static SpanFormat<TValue> CreateSpanFormatterCore<TValue>(string? format, CultureInfo culture)
        where TValue : ISpanFormattable
    {
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        return (value, destination) => value is null
                                           ? 0
                                           : value.TryFormat(destination, out int written, format, culture)
                                               ? written
                                               : -1;
    }

    /// <summary>The <see cref="Nullable{T}"/> lane: unwrap without boxing; null → 0 chars.</summary>
    private static SpanFormat<TValue?> CreateNullableSpanFormatterCore<TValue>(string? format, CultureInfo culture)
        where TValue : struct, ISpanFormattable
        => (value, destination) => value.HasValue
            ? value.GetValueOrDefault().TryFormat(destination, out int written, format, culture) ? written : -1
            : 0;

    // ── Setters (the editing lane — design doc §3.2) ─────────────────────────────────────────────

    /// <summary>
    /// Builds the write-back lambda for a selector whose body is a member chain ending in a writable
    /// property/field (<c>row.Customer.Name = value</c>, null-propagating through reference
    /// intermediates — a broken chain is a no-op write, the binding-engine convention). Null when
    /// the selector isn't a settable chain (computed keys are read-only — the editor won't open).
    /// </summary>
    public static LambdaExpression? TryBuildSetter(LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (TryGetSettableLeaf(selector) is not { } leaf)
            return null;

        var row = selector.Parameters[0];
        var value = Expression.Parameter(leaf.Type, "value");
        Expression assign = GuardedAssign(leaf, value);

        // Force a void body so the lambda infers Action<TRow,TKey> — a bare Assign returns the
        // assigned value and would infer Func<TRow,TKey,TKey> (the InvalidCastException trap).
        return Expression.Lambda(Expression.Block(typeof(void), assign), row, value);
    }

    /// <summary>
    /// The VALUE-TYPE-row analog of <see cref="TryBuildSetter"/> (design doc §9.6): compiles
    /// <c>(row, value) =&gt; { row.Member = value; return row; }</c> — a <c>Func&lt;TRow,TKey,TRow&gt;</c>
    /// mutating the by-value parameter copy and returning it, because an <c>Action</c> setter on a
    /// struct row mutates a throwaway copy (the silent-no-op trap). The caller stores the returned
    /// copy back (<c>RowStore.SetRow</c> / the boxed-row write-back). Null when the selector isn't a
    /// settable member chain.
    /// </summary>
    public static LambdaExpression? TryBuildStructSetter(LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (TryGetSettableLeaf(selector) is not { } leaf)
            return null;

        var row = selector.Parameters[0];
        var value = Expression.Parameter(leaf.Type, "value");
        Expression assign = GuardedAssign(leaf, value);

        // { row.Member = value; return row; } — the block's value is the (mutated) row parameter.
        return Expression.Lambda(Expression.Block(assign, row), row, value);
    }

    /// <summary>The shared setter validation: unwraps Convert nodes, requires a writable
    /// property/field leaf on a member chain rooted at the row parameter.</summary>
    private static MemberExpression? TryGetSettableLeaf(LambdaExpression selector)
    {
        // Unwrap Convert nodes; require a MemberExpression chain rooted at the row parameter.
        Expression body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;

        if (body is not MemberExpression leaf)
            return null;

        bool writable = leaf.Member switch
        {
            PropertyInfo property => property.SetMethod is { IsPublic: true },
            FieldInfo field => !field.IsInitOnly,
            _ => false,
        };
        if (!writable)
            return null;

        // Validate the chain roots at the parameter.
        for (Expression? walk = leaf.Expression; walk is not null; walk = (walk as MemberExpression)?.Expression)
        {
            if (walk == selector.Parameters[0])
                break;
            if (walk is not MemberExpression)
                return null;
        }

        return leaf;
    }

    /// <summary>Null-guards reference intermediates: <c>if (row.Customer != null) row.Customer.Name = value</c>.</summary>
    private static Expression GuardedAssign(MemberExpression leaf, ParameterExpression value)
    {
        Expression assign = Expression.Assign(leaf, value);
        if (leaf.Expression is MemberExpression { Type.IsValueType: false } owner)
            assign = Expression.IfThen(Expression.NotEqual(owner, Expression.Constant(null, owner.Type)), assign);
        return assign;
    }

    // ── Column factory ───────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the typed column for a row selector lambda (closed generics resolved at runtime).</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2076", Justification = "The key TKey is the selector's LambdaExpression.ReturnType (a BCL Type, no DAM); the engine is RequiresDynamicCode and CreateColumnCore<TRow,TKey> flows DAM to the reflection sites for the seeded instantiations.")]
    public static ShapedColumn CreateColumn<TRow>(
        object identity, LambdaExpression selector,
        StringComparison stringComparison = StringComparison.CurrentCulture,
        string? format = null, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var keyType = selector.ReturnType;

        try
        {
            return (ShapedColumn)typeof(ShapingCodegen)
                .GetMethod(nameof(CreateColumnCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeof(TRow), keyType)
                .Invoke(null, [identity, selector, stringComparison, format, culture])!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Unwrap: the reflective closing is plumbing — the INNER failure (an AOT artifact miss,
            // a selector type mismatch) is the diagnosable error and must surface as itself.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

    private static ShapedColumn CreateColumnCore<TRow, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TKey>(
        object identity, LambdaExpression selector, StringComparison stringComparison, string? format,
        CultureInfo? culture) where TRow : notnull
        => new ShapedColumn<TRow, TKey>(
               ((Expression<Func<TRow, TKey>>) selector).Compile(),
               CreateKeyComparison<TKey>(stringComparison),
               CreateFormatter<TKey>(format, culture),
               // Culture-mode string columns get the §2.2 collation-key blob (culture order at memcmp
               // speed); Ordinal/OrdinalIgnoreCase skip it — ordinal compare is already memcmp-speed.
               typeof(TKey) == typeof(string) && CollationKeyStore.IsCultureBased(stringComparison)
                   ? new CollationKeyStore(stringComparison)
                   : null)
           {
               Identity = identity,
               // The §9.6 span-format lane: the band cache formats into pooled char buffers through
               // this (no per-cell string); the string _formatter stays the cold/diagnostic lane.
               SpanFormatter = CreateSpanFormatter<TKey>(format, culture),
               // The editing write-back lane (§3.2): a settable member chain compiles the typed setter;
               // computed keys stay read-only (the editor won't open on them). Value-type rows compile
               // the copy-returning mutator instead (§9.6 — an Action on a struct copy is a silent no-op).
               Setter = !typeof(TRow).IsValueType && TryBuildSetter(selector) is {} setter
                            ? (Action<TRow, TKey>) setter.Compile()
                            : null,
               StructSetter = typeof(TRow).IsValueType && TryBuildStructSetter(selector) is {} mutator
                                  ? (Func<TRow, TKey, TRow>) mutator.Compile()
                                  : null,
           };
}
