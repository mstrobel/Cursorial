using System;

namespace Cursorial.Markup;

/// <summary>
/// Declares the value serializer for a type or member — the WPF <c>[ValueSerializer]</c> analog, the
/// save-side counterpart to <see cref="TypeConverterAttribute"/> (string ⇄ value for round-tripping).
/// Defined for API completeness and consumer use; Cursorial has no XAML <i>save</i> path yet, so it is not
/// consumed by the loader (which only reads). A serializer type is the convention; the contract is
/// established when a serialization path lands.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum, AllowMultiple = false, Inherited = true)]
public sealed class ValueSerializerAttribute : Attribute
{
    /// <summary>Declares the value serializer by type.</summary>
    public ValueSerializerAttribute(Type valueSerializerType)
        => ValueSerializerType = valueSerializerType ?? throw new ArgumentNullException(nameof(valueSerializerType));

    /// <summary>Declares the value serializer by assembly-qualified type name (late-bound).</summary>
    public ValueSerializerAttribute(string valueSerializerTypeName)
        => ValueSerializerTypeName = valueSerializerTypeName ?? throw new ArgumentNullException(nameof(valueSerializerTypeName));

    /// <summary>The value-serializer type, when declared by type; otherwise <see langword="null"/>.</summary>
    public Type? ValueSerializerType { get; }

    /// <summary>The assembly-qualified value-serializer type name, when declared by name; otherwise <see langword="null"/>.</summary>
    public string? ValueSerializerTypeName { get; }
}
