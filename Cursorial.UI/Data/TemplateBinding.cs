namespace Cursorial.UI.Data;

/// <summary>
/// The one-way fast path to the templated parent (design doc §6.1). Parse-time restricted to
/// template bodies (Fork C). Two-way reach-in is
/// <c>new Binding { RelativeSource = RelativeSource.TemplatedParent, Mode = TwoWay }</c>.
/// <c>CreateExpression</c> validates inherited members: a <see cref="BindingMode"/> other than
/// <see cref="BindingMode.Default"/>/<see cref="BindingMode.OneWay"/>, or a non-default
/// <see cref="UpdateSourceTrigger"/>, throws; converter/fallback/null/format are honored but forfeit
/// the typed fast path (BD15).
/// </summary>
/// <remarks>
/// <b>Current limitation (the typed B2 fast path is deferred):</b> the binding resolves the source by
/// <see cref="UIProperty.Name"/> through the reflection binding path. The <see cref="Property"/>'s
/// <c>Name</c> must therefore match a public CLR property on the templated parent's runtime type — true
/// for the standard <c>StyledProperty</c> wrapper pattern. A <c>DirectProperty</c> whose <c>Name</c>
/// differs from its CLR member, or an attached property, will not resolve and the binding reports
/// <c>BindingFailure.SourceMissing</c> at runtime. The typed B2 path (binding straight off the
/// <see cref="UIProperty"/> identity, no name lookup) removes this restriction when it lands.
/// </remarks>
public sealed class TemplateBinding : BindingBase
{
    /// <summary>Binds the template part to <paramref name="property"/> of the templated parent.</summary>
    public TemplateBinding(UIProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        Property = property;
    }

    /// <summary>The source property on the templated parent.</summary>
    public UIProperty Property { get; }

    /// <summary>An optional forward converter (forfeits the typed fast path).</summary>
    public IValueConverter? Converter { get; init; }

    /// <summary>The converter parameter.</summary>
    public object? ConverterParameter { get; init; }

    internal override BindingExpressionBase CreateExpression(in BindingActivationContext context)
    {
        if (Mode is not (BindingMode.Default or BindingMode.OneWay))
        {
            throw new InvalidOperationException(
                $"TemplateBinding supports Mode Default or OneWay only (BD15); got {Mode}. " +
                "For two-way, use new Binding { RelativeSource = RelativeSource.TemplatedParent, Mode = TwoWay }.");
        }

        if (UpdateSourceTrigger != UpdateSourceTrigger.Default)
        {
            throw new InvalidOperationException(
                $"TemplateBinding does not support a non-default UpdateSourceTrigger (BD15); got {UpdateSourceTrigger}.");
        }

        // The one-way reach to the templated parent's source property (doc §6.1): functionally
        // equivalent to `new Binding { Path = Property.Name, RelativeSource = TemplatedParent, Mode =
        // OneWay }`. The descriptor validation above keeps the typed-fast-path forfeit rules (BD15);
        // the underlying expression rides the reflection binding over the relative-source anchor that
        // already resolves `TemplatedParent`. A converter/parameter forfeits the typed path but works.
        var binding = new Binding(Property.Name)
        {
            RelativeSource = RelativeSource.TemplatedParent,
            Mode = BindingMode.OneWay,
            Converter = Converter,
            ConverterParameter = ConverterParameter
        };

        return binding.CreateExpression(in context);
    }
}
