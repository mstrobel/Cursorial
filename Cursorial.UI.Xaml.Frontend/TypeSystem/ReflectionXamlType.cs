using System;
using System.Collections;
using System.Collections.Generic;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The reflection-backed <see cref="IXamlType"/>: the default identity for a <see cref="XamlType"/> or
/// <see cref="XamlMember"/> built from a runtime <see cref="System.Type"/>. The <see cref="XamlType"/> /
/// <see cref="XamlMember"/> convenience constructors that accept a <see cref="System.Type"/> wrap it in
/// one of these, so the runtime loader's reflection provider keeps passing <c>typeof(T)</c> unchanged.
/// The generator supplies its own symbol-backed <see cref="IXamlType"/> instead.
/// </summary>
public sealed class ReflectionXamlType : IXamlType
{
    private bool? _isCollection;

    /// <summary>Wraps a runtime CLR type as an <see cref="IXamlType"/>.</summary>
    public ReflectionXamlType(Type systemType)
        => UnderlyingSystemType = systemType ?? throw new ArgumentNullException(nameof(systemType));

    /// <summary>The wrapped runtime type (never <see langword="null"/> for a reflection backend).</summary>
    public Type UnderlyingSystemType { get; }

    /// <inheritdoc/>
    public string Name => UnderlyingSystemType.Name;

    /// <inheritdoc/>
    public bool IsCollection => _isCollection ??= ComputeIsCollection(UnderlyingSystemType);

    // Mirrors the parser's historical IsCollectionMember predicate verbatim (string/object are never
    // collections; ResourceDictionary is matched by name because the frontend cannot reference it).
    private static bool ComputeIsCollection(Type type)
    {
        if (type == typeof(string) || type == typeof(object))
            return false;
        if (typeof(IList).IsAssignableFrom(type))
            return true;
        if (string.Equals(type.Name, "ResourceDictionary", StringComparison.Ordinal))
            return true;
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IList<>))
                return true;
        }
        return false;
    }
}
