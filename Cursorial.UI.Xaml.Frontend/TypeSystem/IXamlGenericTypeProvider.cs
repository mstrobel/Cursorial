using System;
using System.Collections.Generic;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// A fully-namespace-qualified structured type name (W3 <c>x:TypeArguments</c>): the parser resolves
/// PREFIXES against the live reader scope (its job — the reader dies after parse) and hands the provider
/// this tree; the provider owns everything type-system: definition lookup (exact name, then the
/// backtick-arity form), argument closure, the intrinsic (<c>x:</c>) built-ins, and the Cursorial
/// array/nullable suffix extensions.
/// </summary>
public readonly struct QualifiedTypeName(
    string xmlNamespace,
    string name,
    IReadOnlyList<QualifiedTypeName> typeArguments,
    bool isArray,
    bool isNullable)
{
    /// <summary>The resolved xmlns URI (never a prefix).</summary>
    public string XmlNamespace { get; } = xmlNamespace;

    /// <summary>The local type name (no arity suffix — arity is implied by <see cref="TypeArguments"/>).</summary>
    public string Name { get; } = name;

    /// <summary>The type arguments (empty for a non-generic name).</summary>
    public IReadOnlyList<QualifiedTypeName> TypeArguments { get; } = typeArguments;

    /// <summary>The Cursorial <c>[]</c> suffix — a single-dimensional array of the named type.</summary>
    public bool IsArray { get; } = isArray;

    /// <summary>The Cursorial <c>?</c> suffix — a <c>Nullable&lt;T&gt;</c> of the named (value) type.</summary>
    public bool IsNullable { get; } = isNullable;
}

/// <summary>
/// The W3 generic-instantiation seam (design doc <c>xaml-conversion-routes.md</c> §1 W3): a metadata
/// provider that can CLOSE a generic type from a structured, namespace-qualified name. The reflection
/// provider resolves the arity-suffixed CLR definition and <c>MakeGenericType</c>s (the RUC lane — member
/// substitution is free); the Roslyn provider <c>Construct()</c>s the symbol so the generator emits the
/// closed <c>new T&lt;args&gt;()</c> form, AOT-clean by construction. Optional — a provider that does not
/// implement it keeps the pre-W3 behavior (<c>x:TypeArguments</c> is a positioned CUR1202).
/// </summary>
public interface IXamlGenericTypeProvider
{
    /// <summary>
    /// Resolves <paramref name="name"/> to a CLOSED <see cref="XamlType"/> — the named definition with
    /// every argument recursively resolved and applied, plus the array/nullable suffixes. Returns the
    /// standard resolution (not-found when the definition or any argument fails; the caller reports
    /// positioned diagnostics).
    /// </summary>
    XamlTypeResolution TryGetClosedType(in QualifiedTypeName name);
}
