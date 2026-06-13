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

        // The typed fast-path expression lands in stage B2 (the untyped→typed bridge); the descriptor
        // validation above is the v1 contract (matrix B140/B141).
        throw new NotImplementedException(
            "TemplateBinding installation lands in stage B2 (the typed fast path / untyped→typed bridge).");
    }
}
