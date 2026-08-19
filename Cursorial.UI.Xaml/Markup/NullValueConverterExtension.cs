using Cursorial.UI.Data;

namespace Cursorial.UI.Xaml.Markup;

public sealed class NullValueConverterExtension : MarkupExtension
{
    public object? IfNull { get; set; } = UIProperty.UnsetValue;

    public object? Else { get; set; } = UIProperty.UnsetValue;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new NullValueConverter { IfNull = IfNull, Else = Else, XamlConverter = XamlConverters.For };
}