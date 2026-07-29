using Cursorial.UI.Data;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Cursorial.UI.Xaml.Markup;

public sealed class BooleanConverterExtension : MarkupExtension
{
    /// <inheritdoc cref="BooleanConverter.TrueValue"/>
    public object? TrueValue { get; set; }

    /// <inheritdoc cref="BooleanConverter.FalseValue"/>
    public object? FalseValue { get; set; }

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new BooleanConverter { TrueValue = TrueValue, FalseValue = FalseValue };
}