using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// A parse-time placeholder for an <c>{x:Type T}</c> type token, carried as a typed constant (the twin of
/// <see cref="XamlStaticReference"/>). Folding <c>{x:Type}</c> to a runtime <see cref="System.Type"/> is
/// provider-dependent — it is <see langword="null"/> under the symbol-only generator provider, which silently
/// dropped the type — so instead each lane resolves the TOKEN itself: the loader to a <see cref="System.Type"/>
/// (via <c>ResolveTypeToken</c>), the generator to a <c>typeof(...)</c>. Keeps the node graph well-formed
/// without a <c>Cursorial.UI</c> dependency.
/// </summary>
public readonly struct XamlTypeReference : IEquatable<XamlTypeReference>
{
    /// <summary>Creates a type reference from a type token (e.g. <c>Button</c> or <c>vm:Foo</c>).</summary>
    public XamlTypeReference(string typeName)
        => TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));

    /// <summary>The type token as written (possibly xmlns-prefixed).</summary>
    public string TypeName { get; }

    /// <inheritdoc/>
    public bool Equals(XamlTypeReference other) => string.Equals(TypeName, other.TypeName, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is XamlTypeReference other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => TypeName.GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => $"{{x:Type {TypeName}}}";
}
