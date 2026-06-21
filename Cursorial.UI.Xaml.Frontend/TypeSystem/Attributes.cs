using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// Declares that a CLR namespace in the annotated assembly is exposed under a XAML xmlns URI.
/// Multiple instances map several CLR namespaces to one URI (the default Cursorial map points
/// <c>https://cursorial.dev/ui</c> at <c>Cursorial.UI</c> + <c>Cursorial.UI.Controls</c> +
/// <c>Cursorial.UI.Data</c>). Design doc §4.2.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class XmlnsDefinitionAttribute : Attribute
{
    /// <summary>Creates an xmlns→CLR-namespace mapping.</summary>
    public XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace)
    {
        XmlNamespace = xmlNamespace ?? throw new ArgumentNullException(nameof(xmlNamespace));
        ClrNamespace = clrNamespace ?? throw new ArgumentNullException(nameof(clrNamespace));
    }

    /// <summary>The XAML xmlns URI.</summary>
    public string XmlNamespace { get; }

    /// <summary>The CLR namespace exposed under it.</summary>
    public string ClrNamespace { get; }

    /// <summary>The assembly the CLR namespace lives in (defaults to the annotated assembly).</summary>
    public string? AssemblyName { get; set; }
}

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
