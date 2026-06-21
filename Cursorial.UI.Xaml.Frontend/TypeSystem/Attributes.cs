using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

// XmlnsDefinitionAttribute moved to Cursorial.Shared (Cursorial.Markup namespace) so every assembly can declare
// its xmlns→CLR map without depending on the XAML frontend, and both metadata providers discover it uniformly
// (the Cursorial.Shared markup-attribute pattern — #96/#108).

/// <summary>
/// Registers a generated <see cref="IXamlTypeMetadataProvider"/> for trim/AOT-clean loading
/// (the X5 endgame). Present now as the seam; the generated provider is deferred (matrix XD16/X186).
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class XamlMetadataProviderAttribute : Attribute
{
    /// <summary>Registers the provider type.</summary>
    public XamlMetadataProviderAttribute(Type providerType)
        => ProviderType = providerType ?? throw new ArgumentNullException(nameof(providerType));

    /// <summary>The provider type (must implement <see cref="IXamlTypeMetadataProvider"/>).</summary>
    public Type ProviderType { get; }
}
