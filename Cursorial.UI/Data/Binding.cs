using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Cursorial.UI.Data;

/// <summary>
/// The reflection-lane binding descriptor (design doc §6.1) — the workhorse <c>{Binding}</c>.
/// Construction-immutable and instance-shareable; the parsed <see cref="BindingPath"/> is computed
/// once per descriptor and cached (matrix B16).
/// </summary>
public sealed class Binding : AnchoredBinding
{
    /// <summary>
    /// A sentinel value that, if returned by a value converter, indicates that no value should be forwarded to
    /// the binding target.
    /// </summary>
    public static readonly object DoNothing = new UIProperty.SentinelValue($"{nameof(Binding)}.{nameof(DoNothing)}");

    /// <summary>Creates a binding with an empty path (the source object itself).</summary>
    public Binding()
    {
    }

    /// <summary>Creates a binding with the given path text.</summary>
    public Binding(string path) => Path = path;

    /// <summary>
    /// Creates a binding with a property path based on (<paramref name="property"/>).(<paramref
    /// name="additionalProperties"/>[0])[.(<paramref name="additionalProperties"/>[<em>...n</em>])
    /// </summary>
    public Binding(UIProperty property, params UIProperty[] additionalProperties) 
        => Path = new PropertyPath(property, additionalProperties);

    /// <summary>The binding path (design doc §6.3). A bare string implicitly converts to a lazily-resolved
    /// <see cref="PropertyPath"/>; the XAML loader supplies the xmlns-preprocessed form so a prefixed
    /// attached-property qualification (<c>(prefix:Type.Member)</c>) binds the document's namespaces.
    /// <c>""</c> or <c>"."</c> = the source object itself.</summary>
    public PropertyPath Path { get; init; } = PropertyPath.Empty;

    /// <summary>An optional forward/back value converter.</summary>
    public IValueConverter? Converter { get; init; }

    /// <summary>The converter parameter (passed to both <c>Convert</c> and <c>ConvertBack</c>).</summary>
    public object? ConverterParameter { get; init; }

    /// <summary>The type resolver for attached-property segments; <see langword="null"/> ⇒ <see cref="DefaultPathTypeResolver"/>.</summary>
    public IPathTypeResolver? TypeResolver { get; init; }

    /// <summary>
    /// The compiled-binding factory (design doc §6.7) — the lambda is the SOLE path source (Fork C
    /// contract). Analyzes member-access and constant-index hops only; method calls and operators
    /// throw <see cref="FormatException"/> naming the offending node. Each call re-analyzes the tree
    /// — cache the result in a <c>static readonly</c> field.
    /// </summary>
    /// <seealso cref="CompiledBinding{TSource,TValue}"/>
    /// <seealso cref="CompiledBinding.From{TSource,TValue}"/>
    public static CompiledBinding<TSource, TValue> Compiled<TSource, TValue>(Expression<Func<TSource, TValue>> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return CompiledBindingFactory.Analyze(path);
    }

    /// <summary>The parsed path (the <see cref="PropertyPath"/> caches per resolver; matrix B16). A path
    /// preprocessed at load carries its resolved owners already; a bare-string path parses lazily against
    /// <see cref="TypeResolver"/> (or the registry default).</summary>
    internal BindingPath GetPath() => Path.ToBindingPath(TypeResolver);

    internal override BindingExpressionBase CreateExpression(in BindingActivationContext context)
    {
        ValidateAnchors();
        return new ReflectionBindingExpression(this, in context);
    }
}

/// <summary>
/// Analyzes an <see cref="Expression{Func}"/> into a <see cref="CompiledBinding{TSource,TValue}"/>
/// (design doc §6.7) — member-access and constant-index hops only.
/// </summary>
internal static class CompiledBindingFactory
{
    public static CompiledBinding<TSource, TValue> Analyze<TSource, TValue>(
        Expression<Func<TSource, TValue>> path,
        BindingMode mode = BindingMode.Default,
        object? source = null,
        string? elementName = null,
        IValueConverter? converter = null,
        object? converterParameter = null,
        CultureInfo? converterCulture = null,
        string? stringFormat = null,
        object? fallbackValue = null,
        RelativeSource? relativeSource = null,
        object? targetNullValue = null,
        UpdateSourceTrigger updateSourceTrigger = UpdateSourceTrigger.Default,
        bool trace = false)
    {
        var steps = new List<CompiledPathStep>();
        var members = new List<string>();
        Analyze(path.Body, path.Parameters[0], steps, members);
        steps.Reverse();
        members.Reverse();

        var getter = path.Compile();
        var setter = BuildSetter<TSource, TValue>(path.Body);
        var pathText = string.Join(".", members);
        return new CompiledBinding<TSource, TValue>(getter, setter, steps.ToArray(), pathText)
               {
                   Mode =  mode,
                   Source = source,
                   ElementName = elementName,
                   Converter = converter,
                   ConverterParameter = converterParameter,
                   FallbackValue = BindingBase.ResolveFallbackValue(fallbackValue),
                   RelativeSource = relativeSource,
                   ConverterCulture = converterCulture,
                   StringFormat = stringFormat,
                   TargetNullValue = BindingBase.ResolveFallbackValue(targetNullValue),
                   UpdateSourceTrigger =  updateSourceTrigger,
                   Trace = trace
               };
    }

    private static void Analyze(Expression node, ParameterExpression root, List<CompiledPathStep> steps, List<string> members)
    {
        switch (node)
        {
            case ParameterExpression p when ReferenceEquals(p, root):
                return;
            case MemberExpression { Member: PropertyInfo property } member:
            {
                var name = property.Name;
                steps.Add(new CompiledPathStep(name, BuildMemberStep(property)));
                members.Add(name);
                Analyze(member.Expression!, root, steps, members);
                return;
            }
            case MemberExpression { Member: FieldInfo field } member:
            {
                steps.Add(new CompiledPathStep(field.Name, BuildFieldStep(field)));
                members.Add(field.Name);
                Analyze(member.Expression!, root, steps, members);
                return;
            }
            case IndexExpression { Arguments: [ConstantExpression constant] } index:
            {
                steps.Add(new CompiledPathStep("Item[]", BuildIndexStep(index.Indexer!, constant.Value)));
                members.Add($"[{constant.Value}]");
                Analyze(index.Object!, root, steps, members);
                return;
            }
            case MethodCallExpression { Method.Name: "get_Item", Arguments: [ConstantExpression constant] } call:
            {
                steps.Add(new CompiledPathStep("Item[]", BuildMethodIndexStep(call.Method, constant.Value)));
                members.Add($"[{constant.Value}]");
                Analyze(call.Object!, root, steps, members);
                return;
            }
            // A property-system read: GetValue(SomeClass.SomeProperty) — typed or untyped overload.
            // The hop carries the UIProperty identity itself, so subscription observes it directly
            // (and attached/no-wrapper properties become bindable without a CLR twin).
            case MethodCallExpression { Method.Name: "GetValue", Object: {} receiver } call
                when typeof(UIObject).IsAssignableFrom(receiver.Type) &&
                     call.Arguments is [var propertyArg, ..] &&
                     EvaluatePropertyArgument(propertyArg) is { } uiProperty:
            {
                steps.Add(new CompiledPathStep(uiProperty.Name, BuildUIPropertyStep(uiProperty)) { UIProperty = uiProperty });
                members.Add(uiProperty.Name);
                Analyze(receiver, root, steps, members);
                return;
            }
            case MethodCallExpression call:
                throw new FormatException(
                    $"Compiled binding path may contain member-access, constant-index, and UIObject " +
                    $"GetValue hops only; the method call '{call.Method.Name}' is not supported (design doc §6.7).");
            case BinaryExpression binary:
                throw new FormatException(
                    $"Compiled binding path may contain member-access and constant-index hops only; " +
                    $"the operator '{binary.NodeType}' is not supported (design doc §6.7).");
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                Analyze(convert.Operand, root, steps, members);
                return;
            default:
                throw new FormatException(
                    $"Compiled binding path may contain member-access and constant-index hops only; " +
                    $"the node '{node.NodeType}' is not supported (design doc §6.7).");
        }
    }

    private static Func<object?, object?> BuildMemberStep(PropertyInfo property) =>
        instance => instance is null ? null : property.GetValue(instance);

    private static Func<object?, object?> BuildFieldStep(FieldInfo field) =>
        instance => instance is null ? null : field.GetValue(instance);

    private static Func<object?, object?> BuildIndexStep(PropertyInfo indexer, object? index) =>
        instance => instance is null ? null : indexer.GetValue(instance, [index]);

    private static Func<object?, object?> BuildMethodIndexStep(MethodInfo getItem, object? index) =>
        instance => instance is null ? null : getItem.Invoke(instance, [index]);

    private static Action<TSource, TValue>? BuildSetter<TSource, TValue>(Expression body)
    {
        // The leaf is settable only when it is a writable property/field member access.
        return body switch
        {
            MemberExpression { Member: PropertyInfo { CanWrite: true } } => CompileSetter<TSource, TValue>(body),
            MemberExpression { Member: FieldInfo { IsInitOnly: false } } => CompileSetter<TSource, TValue>(body),
            MethodCallExpression { Method.Name: "GetValue", Object: {} receiver } call
                when typeof(UIObject).IsAssignableFrom(receiver.Type) &&
                     call.Arguments is [var propertyArg, ..] &&
                     EvaluatePropertyArgument(propertyArg) is { IsReadOnly: false } uiProperty
                => CompileUIPropertySetter<TSource, TValue>(receiver, uiProperty),
            _ => null
        };
    }

    private static Action<TSource, TValue>? CompileSetter<TSource, TValue>(Expression body)
    {
        try
        {
            var sourceParam = Expression.Parameter(typeof(TSource), "s");
            var valueParam = Expression.Parameter(typeof(TValue), "v");
            // Re-root the member chain on the fresh source parameter.
            var rebased = Rebase(body, sourceParam);
            var assign = Expression.Assign(rebased, valueParam);
            return Expression.Lambda<Action<TSource, TValue>>(assign, sourceParam, valueParam).Compile();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Func<object?, object?> BuildUIPropertyStep(UIProperty property) =>
        instance => instance is null ? null : ((UIObject)instance).GetValue(property);

    /// <summary>
    /// Evaluates a <c>GetValue</c> argument to its <see cref="UIProperty"/>: the static registration
    /// field or property (<c>TextBox.TextProperty</c>), or a closure-captured local. Anything else is
    /// not a compile-time property identity, and the call falls through to the unsupported-hop error.
    /// </summary>
    private static UIProperty? EvaluatePropertyArgument(Expression argument) => argument switch
    {
        ConstantExpression { Value: UIProperty constant } => constant,
        MemberExpression { Member: FieldInfo { IsStatic: true } field } => field.GetValue(null) as UIProperty,
        MemberExpression { Member: PropertyInfo { GetMethod.IsStatic: true } property } => property.GetValue(null) as UIProperty,
        MemberExpression { Member: FieldInfo field, Expression: ConstantExpression { Value: {} closure } } => field.GetValue(closure) as UIProperty,
        _ => null,
    };

    private static Action<TSource, TValue>? CompileUIPropertySetter<TSource, TValue>(Expression receiver, UIProperty property)
    {
        try
        {
            // Compile only the RECEIVER chain; the write goes through the untyped SetValue — no
            // MethodInfo lookup, and the boxing matches what any object-typed property write costs.
            var sourceParam = Expression.Parameter(typeof(TSource), "s");
            var rebased = Rebase(receiver, sourceParam);
            var receiverGetter = Expression.Lambda<Func<TSource, object?>>(
                Expression.Convert(rebased, typeof(object)), sourceParam).Compile();

            return (source, value) =>
            {
                if (receiverGetter(source) is UIObject target)
                    target.SetValue(property, value);
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Expression Rebase(Expression node, ParameterExpression newRoot) => node switch
    {
        ParameterExpression => newRoot,
        MemberExpression member => Expression.MakeMemberAccess(Rebase(member.Expression!, newRoot), member.Member),
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert =>
            Expression.Convert(Rebase(convert.Operand, newRoot), convert.Type),
        _ => node
    };
}
