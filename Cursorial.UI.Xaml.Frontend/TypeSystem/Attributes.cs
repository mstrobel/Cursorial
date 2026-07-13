using System;
using System.Diagnostics.CodeAnalysis;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

// XmlnsDefinitionAttribute moved to Cursorial.Shared (Cursorial.Markup namespace) so every assembly can declare
// its xmlns→CLR map without depending on the XAML frontend, and both metadata providers discover it uniformly
// (the Cursorial.Shared markup-attribute pattern — #96/#108).

/// <summary>
/// Advertises an assembly's <see cref="IXamlTypeMetadataProvider"/> for trim/AOT-clean loading — pure
/// PULL metadata (matrix X186 as amended): the loader's lazy default consults the ENTRY assembly's
/// attribute (<c>XamlLoaderOptions.DefaultMetadataProvider</c>) when the reflection provider is disabled
/// for trimming/AOT (reflection stays the open-world ambient default otherwise), and hosts adopt a
/// specific assembly's provider via <c>XamlLoaderOptions.TryDiscoverMetadataProvider</c>. Nothing
/// registers at load time, so loading an assembly never repoints another host's default. The X4
/// generator emits one per compilation.
/// </summary>
/// <remarks>
/// The <see cref="DynamicallyAccessedMembersAttribute"/> annotation makes discovery trim-safe by
/// construction: the trimmer keeps the advertised type's <c>Instance</c> field (public static fields) and
/// its constructors, which discovery reads reflectively.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class XamlMetadataProviderAttribute : Attribute
{
    private const DynamicallyAccessedMemberTypes DiscoveredMembers =
        DynamicallyAccessedMemberTypes.PublicParameterlessConstructor |
        DynamicallyAccessedMemberTypes.NonPublicConstructors |
        DynamicallyAccessedMemberTypes.PublicFields;

    /// <summary>Advertises the provider type.</summary>
    public XamlMetadataProviderAttribute([DynamicallyAccessedMembers(DiscoveredMembers)] Type providerType)
        => ProviderType = providerType ?? throw new ArgumentNullException(nameof(providerType));

    /// <summary>The provider type (must implement <see cref="IXamlTypeMetadataProvider"/>).</summary>
    [DynamicallyAccessedMembers(DiscoveredMembers)]
    public Type ProviderType { get; }
}
