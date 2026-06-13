using System;
using System.Collections.Generic;
using System.Globalization;
using Cursorial.UI.Xaml;
using UIControls = Cursorial.UI.Controls;
using UIData = Cursorial.UI.Data;
using Cursorial.Rendering;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// A focused, hand-built <see cref="IXamlTypeMetadataProvider"/> for the X0 frontend matrix rows.
/// It covers exactly the types/members/converters the §§1–5 parse rows touch, using the real
/// <c>Cursorial.UI</c> CLR types so <c>RootType</c> assertions hold. It deliberately does NOT reflect
/// — the production reflection provider (<c>ReflectionXamlMetadata</c>) lands at X1; this fixture is
/// the frontend's "real provider" until then. Member resolution is reflection-free (a static table),
/// matching the XD16 stance that the parser never reflects.
/// </summary>
internal sealed class TestMetadata : IXamlTypeMetadataProvider
{
    public static readonly TestMetadata Instance = new();

    private const string Ui = XmlnsTestConstants.Ui;

    // A demonstration type for the using:/clr-namespace: rows (X17/X18).
    public sealed class MyControl { }

    private readonly Dictionary<string, Func<XamlType>> _byName;
    private readonly string[] _knownNames;

    private TestMetadata()
    {
        _byName = new Dictionary<string, Func<XamlType>>(StringComparer.Ordinal)
        {
            ["Button"] = () => Build(typeof(UIControls.Button), content: "Content", requiresInit: false),
            ["ButtonBase"] = () => Build(typeof(UIControls.ButtonBase), content: "Content"),
            ["Border"] = () => Build(typeof(UIControls.Border), content: "Child"),
            ["StackPanel"] = () => Build(typeof(UIControls.StackPanel), content: "Children", isCollectionContent: true),
            ["TextBlock"] = () => Build(typeof(UIControls.TextBlock), content: "Text"),
            ["Label"] = () => Build(typeof(UIControls.Label), content: "Content"),
            ["Grid"] = () => Build(typeof(UIControls.Grid), content: "Children", isCollectionContent: true),
            ["RowDefinition"] = () => Build(typeof(UIControls.RowDefinition)),
            ["ColumnDefinition"] = () => Build(typeof(UIControls.ColumnDefinition)),
            ["ControlTemplate"] = () => Build(typeof(UIControls.ControlTemplate), content: "Content", deferredContent: true),
            ["DataTemplate"] = () => Build(typeof(UIControls.DataTemplate), content: "Content", deferredContent: true),
            ["Style"] = () => Build(typeof(global::Cursorial.UI.Style)),
            ["Setter"] = () => Build(typeof(global::Cursorial.UI.Setter)),
            ["Binding"] = () => Build(typeof(UIData.Binding)),
            ["MyControl"] = () => Build(typeof(MyControl)),
        };

        _knownNames = new[]
        {
            "Button", "ButtonBase", "Border", "StackPanel", "TextBlock", "Label", "Grid",
            "RowDefinition", "ColumnDefinition", "ControlTemplate", "DataTemplate", "Style", "Setter", "Binding",
        };
    }

    public XamlTypeResolution TryGetType(string xmlNamespace, string localName)
    {
        // The using:/clr-namespace: forms map directly to CLR namespaces; our demo type lives under
        // the "DemoApp" namespace for the X17/X18 rows.
        if (XmlnsNamespacesTest.TryDecodeClr(xmlNamespace, out var clrNs))
        {
            if (clrNs == "DemoApp" && localName == "MyControl")
                return XamlTypeResolution.Resolved(Build(typeof(MyControl)));
            return XamlTypeResolution.NotFound();
        }

        if (!string.Equals(xmlNamespace, Ui, StringComparison.Ordinal))
            return XamlTypeResolution.NotFound();

        // Synthetic ambiguity for X20: a "Ambiguous" local name matches two full names.
        if (localName == "AmbiguousType")
            return XamlTypeResolution.Ambiguous(new[] { "Cursorial.UI.AmbiguousType", "Cursorial.UI.Controls.AmbiguousType" });

        if (_byName.TryGetValue(localName, out var factory))
            return XamlTypeResolution.Resolved(factory());

        return XamlTypeResolution.NotFound();
    }

    public string[] GetClrNamespaces(string xmlNamespace)
        => string.Equals(xmlNamespace, Ui, StringComparison.Ordinal)
            ? new[] { "Cursorial.UI", "Cursorial.UI.Controls", "Cursorial.UI.Data" }
            : Array.Empty<string>();

    public string[] GetKnownTypeNames(string xmlNamespace)
        => string.Equals(xmlNamespace, Ui, StringComparison.Ordinal) ? _knownNames : Array.Empty<string>();

    public string[] GetKnownMemberNames(Type clrType)
    {
        // The X0 member-not-found rows don't assert a did-you-mean suggestion, but surfacing the static
        // member table keeps this fixture honest with the production reflection provider.
        var members = TestMembers.For(clrType, contentProperty: null, deferredContent: false);
        var names = new string[members.Count];
        members.Keys.CopyTo(names, 0);
        return names;
    }

    // ── Type construction ────────────────────────────────────────────────────────────────────────

    private static XamlType Build(
        Type clr,
        string? content = null,
        bool isCollectionContent = false,
        bool deferredContent = false,
        bool requiresInit = false)
    {
        var members = TestMembers.For(clr, content, deferredContent);
        return new XamlType(
            clrType: clr,
            activate: null,
            contentProperty: content,
            isCollection: isCollectionContent,
            addItem: null,
            addDictionaryItem: null,
            dictionaryKeyType: null,
            requiresInitialize: requiresInit,
            memberResolver: name => members.TryGetValue(name, out var m) ? m : null);
    }
}

/// <summary>The xmlns URIs used by the X0 test corpus.</summary>
internal static class XmlnsTestConstants
{
    public const string Ui = "https://cursorial.dev/ui";
    public const string Intrinsics = "https://cursorial.dev/xaml";
}

/// <summary>Decodes the using:/clr-namespace: forms for the test provider (mirrors the frontend helper).</summary>
internal static class XmlnsNamespacesTest
{
    public static bool TryDecodeClr(string ns, out string clrNamespace)
    {
        clrNamespace = string.Empty;
        if (ns.StartsWith("using:", StringComparison.Ordinal))
        {
            clrNamespace = ns.Substring("using:".Length).Trim();
            return clrNamespace.Length > 0;
        }
        if (ns.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var body = ns.Substring("clr-namespace:".Length);
            int semi = body.IndexOf(';');
            clrNamespace = (semi < 0 ? body : body.Substring(0, semi)).Trim();
            return clrNamespace.Length > 0;
        }
        return false;
    }
}
