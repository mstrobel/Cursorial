// ReSharper disable CheckNamespace

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Cursorial.UI;

/// <summary>
/// A value that may be explicitly set or deliberately unset (design doc §9.3) — distinct from
/// <c>null</c>/<c>default</c> so an animation track can tell "no <c>From</c> given ⇒ snapshot the
/// property at track start" from "<c>From</c> set to the default value".
/// </summary>
/// <typeparam name="T">The wrapped value type.</typeparam>
[TypeConverter(typeof(OptionalConverter))]
public readonly struct Optional<T>
{
    /// <summary>Whether a value was explicitly set.</summary>
    public bool HasValue { get; }

    /// <summary>The set value (meaningful only when <see cref="HasValue"/>).</summary>
    public T Value { get; }

    /// <summary>Wraps an explicit value.</summary>
    public Optional(T value)
    {
        Value = value;
        HasValue = true;
    }

    /// <summary>The unset value (<see cref="HasValue"/> false).</summary>
    public static Optional<T> Unset => default;

    /// <summary>Any value implicitly becomes a set <see cref="Optional{T}"/>.</summary>
    public static implicit operator Optional<T>(T value) => new(value);

    /// <summary>Returns <see cref="Value"/> when set, else <paramref name="fallback"/>.</summary>
    public T GetValueOrDefault(T fallback) => HasValue ? Value : fallback;

    /// <inheritdoc/>
    public override string ToString() => HasValue ? $"Optional({Value})" : "Optional.Unset";
}

public sealed class OptionalConverter : TypeConverter
{
    private readonly Type _targetType;
    private readonly Type _innerType;
    private readonly TypeConverter _innerConverter;
    private readonly ConstructorInfo? _constructor;
    private readonly PropertyInfo _hasValue;
    private readonly PropertyInfo _value;

    public OptionalConverter(Type targetType)
    {
        if (targetType.IsGenericType is false || targetType.GetGenericTypeDefinition() != typeof(Optional<>))
            throw new ArgumentException("Target type must be an Optional<T> instantiation.", nameof(targetType));

        _targetType = targetType;
        _innerType = targetType.GetGenericArguments()[0];
        _innerConverter = TypeDescriptor.GetConverter(_innerType);
        _constructor = _targetType.GetConstructor([_innerType]);

        _hasValue = _targetType.GetProperty(nameof(Optional<>.HasValue)) ??
                    throw new InvalidOperationException(
                        $"Could not resolve {nameof(Optional<>)}<{_innerType.Name}>.{nameof(Optional<>.HasValue)}");
        
        _value = _targetType.GetProperty(nameof(Optional<>.Value)) ??
                    throw new InvalidOperationException(
                        $"Could not resolve {nameof(Optional<>)}<{_innerType.Name}>.{nameof(Optional<>.Value)}");
    }

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return _innerConverter.CanConvertFrom(context, sourceType);
    }

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        var baseValue = _innerType.IsInstanceOfType(value) ? value : _innerConverter.ConvertFrom(context, culture, value);
        return _constructor!.Invoke([baseValue]);
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, [NotNullWhen(true)] Type? destinationType)
    {
        return _innerConverter.CanConvertTo(context, destinationType);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (_targetType.IsInstanceOfType(value))
            return _hasValue.GetValue(value) is true ? _value.GetValue(value) : null;

        return GetConvertToException(value, destinationType);
    }
}
