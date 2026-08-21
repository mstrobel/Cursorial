extern alias frontend;

using Cursorial.UI.Data;

using DynamicallyAccessedMembers = frontend::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembersAttribute; 
using DynamicallyAccessedMemberTypes = frontend::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes; 

namespace Cursorial.UI.Xaml.Markup;

public sealed class EnumDisplayConverterExtension : MarkupExtension
{
    public EnumDisplayConverterExtension() {}

    public EnumDisplayConverterExtension(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        EnumType = enumType;
    }

    [ConstructorArgument("enumType")]
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)]
    public Type? EnumType { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (EnumType is not {} enumType)
        {
            throw new InvalidOperationException(
                $"{nameof(EnumType)} is must be set on {nameof(EnumDisplayConverterExtension)}.");
        }

        return new EnumDisplayConverter { EnumType = enumType };
    }
}