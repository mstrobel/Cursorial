using System;

namespace Cursorial.Markup;

/// <summary>
/// Declares that a CLR namespace in the annotated assembly is exposed under a XAML xmlns URI — the
/// canonical, framework-decoratable form (in <c>Cursorial.Shared</c>, referenced by every layer). Both
/// XAML metadata providers (the reflection loader's <c>XamlSchemaContext</c> and the build-time symbol
/// resolver) discover it, so an assembly's xmlns map is declared once on the assembly rather than
/// hand-maintained in a central list. Multiple instances map several CLR namespaces to one URI (the
/// default Cursorial map points <c>https://cursorial.dev/ui</c> at <c>Cursorial.UI</c> +
/// <c>Cursorial.UI.Controls</c> + <c>Cursorial.UI.Data</c> + <c>Cursorial.UI.Input</c> +
/// <c>Cursorial.UI.Themes</c> + <c>Cursorial.Drawing.Media</c>). Matched by the providers on the
/// attribute's simple name (WPF parity: <c>[assembly: XmlnsDefinition(uri, clrNamespace)]</c>). Design doc §4.2.
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
