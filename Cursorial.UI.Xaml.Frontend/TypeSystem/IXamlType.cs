using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The abstract identity of a XAML-visible type, decoupled from <see cref="System.Type"/> so the
/// build-time generator can resolve types from Roslyn symbols while the runtime loader resolves them via
/// reflection (the two-backend type-system seam). A <see cref="XamlType"/>'s identity
/// (<see cref="XamlType.ClrType"/>) and a <see cref="XamlMember"/>'s value type
/// (<see cref="XamlMember.ValueType"/>) are both <see cref="IXamlType"/>s.
/// </summary>
/// <remarks>
/// The frontend reads only <see cref="Name"/> (diagnostics) and <see cref="IsCollection"/> (the
/// single-child Object-vs-Items decision); it never touches <see cref="UnderlyingSystemType"/>. That
/// member is the runtime loader's escape hatch — activation, <c>typeof</c> comparisons, and
/// assignability all need the concrete <see cref="System.Type"/>, which only a reflection backend
/// (<see cref="ReflectionXamlType"/>) can supply. A symbol backend returns <see langword="null"/>.
/// </remarks>
public interface IXamlType
{
    /// <summary>The type's simple name (the diagnostic surface — matches <c>System.Type.Name</c>).</summary>
    string Name { get; }

    /// <summary>
    /// True when the type is collection-shaped — an <see cref="System.Collections.IList"/>, an
    /// <see cref="System.Collections.Generic.IList{T}"/>, or a <c>ResourceDictionary</c>. Drives the
    /// parser's single-child Object-vs-Items decision (a lone child of a read-only collection member is
    /// an <c>Items</c> run, not an <c>Object</c> replacement). Computed by the backend so the frontend
    /// never reflects.
    /// </summary>
    bool IsCollection { get; }

    /// <summary>
    /// The underlying runtime <see cref="System.Type"/> on a reflection backend, or <see langword="null"/>
    /// on a symbol (generator) backend. The frontend never reads it; the loader uses it for activation /
    /// <c>typeof</c> comparisons / assignability.
    /// </summary>
    Type? UnderlyingSystemType { get; }
}
