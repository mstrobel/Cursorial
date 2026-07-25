using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

using Cursorial.UI;
using Cursorial.UI.Data;
using Cursorial.UI.Xaml;

namespace Cursorial.Gallery.Infrastructure;

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global")]
public sealed record EnumItem(string Name, object Value);

public sealed class EnumItemConverter : IValueConverter
{
    private Dictionary<object, EnumItem>? _reverseLookup;

    public Type? EnumType { get; init; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // ReSharper disable once CanSimplifyIsAssignableFrom
        if (value is EnumItem { Value: {} enumValue } && 
            targetType.IsAssignableFrom(enumValue.GetType()))
        {
            return enumValue;
        }

        if (value is {} o && o.GetType() is { IsEnum: true } enumType &&
            EnsureReverseLookup().TryGetValue(o, out var enumItem))
        {
            return enumItem;
        }

        return UIProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (EnumType is { IsEnum: true } type && type.IsInstanceOfType(value))
        {
            if (EnsureReverseLookup().TryGetValue(value, out EnumItem? item))
                return item;
        }
        else if (value is EnumItem item && targetType.IsInstanceOfType(item.Value))
        {
            return item.Value;
        }

        return UIProperty.UnsetValue;
    }

    private Dictionary<object, EnumItem> EnsureReverseLookup()
    {
        if (_reverseLookup is not {} reverseLookup)
        {
            reverseLookup = new Dictionary<object, EnumItem>();

            var source = new EnumItemsSource { EnumType = EnumType }.ProvideValue(null!);

            foreach (var item in (IEnumerable<EnumItem>) source)
                reverseLookup[item.Value] = item;
            
            _reverseLookup = reverseLookup;
        }
        
        return reverseLookup;
    }
}

[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public sealed class EnumItemConverterExtension : MarkupExtension
{
    public Type? EnumType { get; init; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (EnumType is { IsEnum: true } type)
            return new EnumItemConverter { EnumType = type };

        throw new InvalidOperationException("EnumType must be set to a valid enum type.");
    }
}

public class EnumItemsSource : MarkupExtension
{
    public Type? EnumType { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (EnumType is not { IsEnum: true } type)
            return UIProperty.UnsetValue;

        var values = Enum.GetValues(type);
        var names = Enum.GetNames(type);
        var items = new List<EnumItem>(values.Length);

        for (int i = 0, n = names.Length; i < n; i++)
        {
            var name = names[i];

            if (type.GetField(names[i]) is { IsPublic: true } field)
            {
                if (field.GetCustomAttribute<DisplayAttribute>() is { Name: { Length: > 0 } display })
                    name = display;
                else if (field.GetCustomAttribute<DisplayNameAttribute>() is
                         { DisplayName: { Length: > 0 } displayName })
                    name = displayName;
            }

            items.Add(new EnumItem(name, values.GetValue(i)!));
        }

        return items;
    }
}

public class EnumValuesSourceExtension : MarkupExtension
{
    public Type? EnumType { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (EnumType is not { IsEnum: true } type)
            return false;

        return Enum.GetValues(type);
    }
}