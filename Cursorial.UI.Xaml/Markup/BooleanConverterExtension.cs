using Cursorial.UI.Data;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Cursorial.UI.Xaml.Markup;

public sealed class BooleanConverterExtension : MarkupExtension
{
    /// <inheritdoc cref="BooleanConverter.TrueValue"/>
    public object? TrueValue { get; set; }

    /// <inheritdoc cref="BooleanConverter.FalseValue"/>
    public object? FalseValue { get; set; }

    /// <inheritdoc cref="BooleanConverter.FallbackValue"/>
    public object? FallbackValue { get; set; } = global::Cursorial.UI.UIProperty.UnsetValue; // the converter's own default sentinel — an UNSPECIFIED fallback must stay UnsetValue, not become null

    /// <inheritdoc/>
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new BooleanConverter
           {
               TrueValue = TrueValue,
               FalseValue = FalseValue,
               FallbackValue = FallbackValue
           };
}