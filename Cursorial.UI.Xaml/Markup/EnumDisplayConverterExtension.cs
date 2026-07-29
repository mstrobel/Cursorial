using Cursorial.UI.Data;

namespace Cursorial.UI.Xaml.Markup;

public sealed class EnumDisplayConverterExtension : MarkupExtension
{
    public EnumDisplayConverterExtension() {}

    public EnumDisplayConverterExtension(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        EnumType = enumType;
    }

    [ConstructorArgument("enumType")]
    public required Type EnumType { get; init; }

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new EnumDisplayConverter { EnumType = EnumType };
}