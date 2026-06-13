using System.Collections.Generic;
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
    private BindingPath? _parsedPath;
    private IPathTypeResolver? _parsedWithResolver;

    /// <summary>Creates a binding with an empty path (the source object itself).</summary>
    public Binding()
    {
    }

    /// <summary>Creates a binding with the given path text.</summary>
    public Binding(string path) => Path = path;

    /// <summary>The path text. <c>""</c> or <c>"."</c> = the source object itself.</summary>
    public string Path { get; init; } = "";

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

    /// <summary>The parsed path (cached per descriptor; matrix B16).</summary>
    internal BindingPath GetPath()
    {
        var resolver = TypeResolver ?? DefaultPathTypeResolver.Instance;
        if (_parsedPath is { } cached && ReferenceEquals(_parsedWithResolver, resolver))
            return cached;

        _parsedPath = BindingPath.Parse(Path, resolver);
        _parsedWithResolver = resolver;
        return _parsedPath;
    }

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
    public static CompiledBinding<TSource, TValue> Analyze<TSource, TValue>(Expression<Func<TSource, TValue>> path)
    {
        var steps = new List<CompiledPathStep>();
        var members = new List<string>();
        Analyze(path.Body, path.Parameters[0], steps, members);
        steps.Reverse();
        members.Reverse();

        var getter = path.Compile();
        var setter = BuildSetter<TSource, TValue>(path.Body);
        var pathText = string.Join(".", members);
        return new CompiledBinding<TSource, TValue>(getter, setter, steps.ToArray(), pathText);
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
            case MethodCallExpression call:
                throw new FormatException(
                    $"Compiled binding path may contain member-access and constant-index hops only; " +
                    $"the method call '{call.Method.Name}' is not supported (design doc §6.7).");
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
            _ => null,
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

    private static Expression Rebase(Expression node, ParameterExpression newRoot) => node switch
    {
        ParameterExpression => newRoot,
        MemberExpression member => Expression.MakeMemberAccess(Rebase(member.Expression!, newRoot), member.Member),
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert =>
            Expression.Convert(Rebase(convert.Operand, newRoot), convert.Type),
        _ => node,
    };
}
