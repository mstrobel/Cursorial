using System.Linq.Expressions;

namespace Cursorial.UI.Data;

/// <summary>
/// The non-generic discovery anchor for the compiled lane (design doc §6.7). <see cref="From"/> is a
/// sibling of <see cref="Binding.Compiled"/> placed on the type the author is creating, so IDE
/// autocomplete on <c>CompiledBinding.</c> surfaces the factory.
/// </summary>
public static class CompiledBinding
{
    /// <summary>
    /// Analyzes <paramref name="path"/> into a <see cref="CompiledBinding{TSource,TValue}"/> — the
    /// same lambda-analysis factory as <see cref="Binding.Compiled"/>, exposed on
    /// <see cref="CompiledBinding"/> for discoverability. The lambda is the SOLE path source (Fork C
    /// contract). Each call re-analyzes the tree — cache the result in a <c>static readonly</c> field.
    /// </summary>
    /// <seealso cref="Binding.Compiled{TSource,TValue}"/>
    public static CompiledBinding<TSource, TValue> From<TSource, TValue>(Expression<Func<TSource, TValue>> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return CompiledBindingFactory.Analyze(path);
    }
}

/// <summary>
/// One hop of a compiled chain (design doc §6.1/§6.7): the member name matched against
/// <c>PropertyChangedEventArgs.PropertyName</c> for INPC re-wiring, and an object-typed step getter
/// used <em>only</em> for subscription rewiring (the typed whole-chain getter does the value
/// reads). A constant-index indexer hop carries <see cref="MemberName"/> <c>"Item[]"</c> (the INPC
/// convention) and <see cref="GetStep"/> applies the captured index.
/// </summary>
/// <param name="MemberName">The member name (or <c>"Item[]"</c> for indexer hops).</param>
/// <param name="GetStep">An object-typed reader for subscription rewiring.</param>
public readonly record struct CompiledPathStep(string MemberName, Func<object?, object?> GetStep);

/// <summary>
/// The second producer of the engine contract (design doc §6.7): typed end-to-end, zero reflection,
/// AOT-clean when generator-produced. Constructible by <c>Binding.Compiled</c> (runtime lambda
/// analysis — the reflective-fallback v1 producer), by the X4 generator, or by hand. Inherits the
/// full <see cref="AnchoredBinding"/> surface — a compiled binding can be Self-, TemplatedParent-,
/// ElementName-, or FindAncestor-anchored; the typed root check (<c>root is TSource</c>) covers
/// anchor/type mismatch.
/// </summary>
/// <seealso cref="Binding.Compiled{TSource,TValue}"/>
/// <seealso cref="CompiledBinding.From{TSource,TValue}"/>
public sealed class CompiledBinding<TSource, TValue> : AnchoredBinding
{
    /// <summary>
    /// Builds a compiled descriptor. A <see langword="null"/> <paramref name="setter"/> makes the
    /// binding one-way only.
    /// </summary>
    public CompiledBinding(
        Func<TSource, TValue> getter,
        Action<TSource, TValue>? setter,
        ReadOnlyMemory<CompiledPathStep> steps,
        string pathText)
    {
        ArgumentNullException.ThrowIfNull(getter);
        ArgumentNullException.ThrowIfNull(pathText);
        Getter = getter;
        Setter = setter;
        Steps = steps;
        PathText = pathText;
    }

    /// <summary>The whole-chain typed read (the hot path).</summary>
    public Func<TSource, TValue> Getter { get; }

    /// <summary>The leaf setter; <see langword="null"/> ⇒ one-way only.</summary>
    public Action<TSource, TValue>? Setter { get; }

    /// <summary>Per-hop wiring info for INPC subscription.</summary>
    public ReadOnlyMemory<CompiledPathStep> Steps { get; }

    /// <summary>The path text (diagnostics — e.g. <c>"Customer.Address.City"</c>).</summary>
    public string PathText { get; }

    /// <summary>An optional forward/back converter.</summary>
    public IValueConverter? Converter { get; init; }

    /// <summary>The converter parameter.</summary>
    public object? ConverterParameter { get; init; }

    internal override BindingExpressionBase CreateExpression(in BindingActivationContext context)
    {
        ValidateAnchors();
        return new CompiledBindingExpression<TSource, TValue>(this, in context);
    }
}
