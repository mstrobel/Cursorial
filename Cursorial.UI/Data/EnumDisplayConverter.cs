using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

using Cursorial.UI.Xaml;

namespace Cursorial.UI.Data;

public sealed class EnumDisplayConverter : TypeConverter, ITypeConverter, IValueConverter
{
    private Dictionary<string, object>? _reverseLookup;
    private Dictionary<object, string>? _displayLookup;

    public EnumDisplayConverter() {}

    public EnumDisplayConverter([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        
        if (!enumType.IsEnum) throw new ArgumentException("Type must be an enum type.", nameof(enumType));

        EnumType = enumType;
    }
    
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)]
    public required Type? EnumType { get; init; }

    object ITypeConverter.ConvertFromString(string text, in XamlValueContext context)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (EnsureLookups(out _, out _, out var reverseLookup) &&
            reverseLookup.TryGetValue(text, out var value))
        {
            return value;
        }
        
        return ValueConversion.Failed;
    }

    bool ITypeConverter.IsContextFree => true;

    object IValueConverter.Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not null &&
            (targetType == typeof(string) || targetType == typeof(object)) &&
            EnsureLookups(out _, out var displayLookup, out _) &&
            displayLookup.TryGetValue(value, out var text))
        {
            return text;
        }
        
        return UIProperty.UnsetValue;
    }

    object IValueConverter.ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string name &&
            targetType == EnumType &&
            EnsureLookups(out _, out _, out var reverseLookup) &&
            reverseLookup.TryGetValue(name, out var enumValue))
        {
            return enumValue;
        }
        
        return UIProperty.UnsetValue;
    }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) 
        => sourceType == typeof(string) || sourceType == EnumType;

    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
        => destinationType == typeof(string) ||
           destinationType == typeof(object) ||
           destinationType == EnumType;

    public override object ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (value is not null &&
            EnsureLookups(out var enumType, out var displayLookup, out var reverseLookup))
        {
            if (enumType.IsInstanceOfType(value) is true)
            {
                if ((destinationType == typeof(string) || destinationType == typeof(object)) &&
                    displayLookup.TryGetValue(value, out var name))
                {
                    return name;
                }
            }
            else if (value is string name &&
                     (destinationType == enumType || destinationType == typeof(object)) &&
                     reverseLookup.TryGetValue(name, out var enumValue))
            {
                return enumValue;
            }
        }

        throw GetConvertToException(value,  destinationType);
    }

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (EnsureLookups(out var enumType, out var displayLookup, out var reverseLookup))
        {
            if (value is string name &&
                reverseLookup.TryGetValue(name, out var enumValue))
            {
                return enumValue;
            }

            if (enumType.IsInstanceOfType(value) is true &&
                context?.PropertyDescriptor is { PropertyType: var propertyType })
            {
                if (propertyType?.IsAssignableFrom(typeof(string)) is not false  &&
                    displayLookup.TryGetValue(value, out var displayName))
                {
                    return displayName;
                }
            }
        }

        throw GetConvertFromException(value);
    }

    private bool EnsureLookups([MaybeNullWhen(false)] out Type enumType,
                               [MaybeNullWhen(false)] out Dictionary<object, string> displayLookup, 
                               [MaybeNullWhen(false)] out Dictionary<string, object> reverseLookup)
    {
        if (EnumType is not {} t)
        {
            enumType = null;
            _displayLookup = displayLookup = null!;
            _reverseLookup = reverseLookup = null!;
            return false;
        }

        enumType = t;
        displayLookup = _displayLookup;
        reverseLookup = _reverseLookup;

        if (displayLookup is not null && reverseLookup is not null)
            return true;

        var baseNames = new HashSet<string>();
        var uniqueNames = new HashSet<string>();
        var collisions = new HashSet<string>();

        var entries = Enum.GetNames(enumType)
                          .Select(n => t.GetField(n, BindingFlags.Public | BindingFlags.Static))
                          .Where(f => f is not null)
                          .Select(f => new
                                       {
                                           f!.Name,
                                           Field = f,
                                           Display = f.GetCustomAttribute<DisplayAttribute>(),
                                           Value = f.GetValue(null)!
                                       })
                          .ToList();

        displayLookup = new Dictionary<object, string>();
        reverseLookup = new Dictionary<string, object>();
        
        foreach (var entry in entries)
        {
            baseNames.Add(entry.Name);

            if (uniqueNames.Add(entry.Name))
            {
                reverseLookup[entry.Name] = entry.Value;
            }
            else
            {
                collisions.Add(entry.Name);
                uniqueNames.Remove(entry.Name);
                reverseLookup.Remove(entry.Name);
            }
        }

        foreach (var entry in entries)
        {
            if (entry.Display is {} d)
            {
                var name = d.Name;
                var shortName = d.ShortName;

                if (string.IsNullOrWhiteSpace(name) is false)
                {
                    if (collisions.Contains(name) is false && uniqueNames.Add(name))
                    {
                        reverseLookup[name] = entry.Value!;
                    }
                    else
                    {
                        collisions.Add(name);
                        uniqueNames.Remove(name);
                        reverseLookup.Remove(name);  // collision makes the existing mapping ambiguous
                    }
                }

                if (string.IsNullOrWhiteSpace(shortName) is false)
                {
                    if (collisions.Contains(shortName) is false && uniqueNames.Add(shortName))
                    {
                        reverseLookup[shortName] = entry.Value!;
                    }
                    else
                    {
                        collisions.Add(shortName);
                        uniqueNames.Remove(shortName);
                        reverseLookup.Remove(shortName); // collision makes the existing mapping ambiguous
                    }
                }
            }
        }

        // Add display name mappings first.
        foreach (var (name, value) in reverseLookup)
        {
            if (baseNames.Contains(name) is false)
                displayLookup.TryAdd(value, name);
        }

        // Add base names only if there isn't a display name mapping.
        foreach (var (name, value) in reverseLookup)
        {
            if (baseNames.Contains(name))
                displayLookup.TryAdd(value, name);
        }
        
        _displayLookup = displayLookup;
        _reverseLookup = reverseLookup;
        return true;
    }
}