using System;

namespace Cursorial.Markup;

/// <summary>
/// Declares the XAML <c>ITypeConverter</c> a type (or member) converts from string through — the WPF
/// <c>[TypeConverter]</c> analog. Honored <b>first</b> by the XAML converter resolution (before the
/// built-in converter ladder), so a consumer type ships its own converter in its own assembly. Cursorial's
/// own attribute (not the BCL <c>System.ComponentModel.TypeConverterAttribute</c>): the named converter
/// must implement <c>Cursorial.UI.Xaml.ITypeConverter</c>.
/// </summary>
/// <remarks>
/// The <see cref="ConverterType"/> form is the common one. The <see cref="ConverterTypeName"/> (assembly-
/// qualified) form is the late-bound escape hatch for a converter that lives in a higher layer than the
/// type it converts (the framework's own built-in converters stay on the ladder rather than using this).
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum, AllowMultiple = false, Inherited = true)]
public sealed class TypeConverterAttribute : Attribute
{
    /// <summary>Declares the converter by type.</summary>
    public TypeConverterAttribute(Type converterType)
        => ConverterType = converterType ?? throw new ArgumentNullException(nameof(converterType));

    /// <summary>Declares the converter by assembly-qualified type name (late-bound).</summary>
    public TypeConverterAttribute(string converterTypeName)
        => ConverterTypeName = converterTypeName ?? throw new ArgumentNullException(nameof(converterTypeName));

    /// <summary>The converter type, when declared by type; otherwise <see langword="null"/>.</summary>
    public Type? ConverterType { get; }

    /// <summary>The assembly-qualified converter type name, when declared by name; otherwise <see langword="null"/>.</summary>
    public string? ConverterTypeName { get; }
}
