using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Xaml;
using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// A hand-built, reflection-free <see cref="IXamlTypeMetadataProvider"/> over the real <c>Cursorial.UI</c>
/// CLR types — the dual-provider drift-gate partner (matrix X174 / XD16). It hardcodes the activation,
/// content-property, collection, and member tables the X174 subset touches; the X5 generated provider is
/// shaped exactly like this (no reflection). It deliberately produces the <b>same</b> trees the
/// reflection provider does.
/// </summary>
internal sealed class HandBuiltMetadata : IXamlTypeMetadataProvider
{
    public static readonly HandBuiltMetadata Instance = new();

    private const string Ui = "https://cursorial.dev/ui";

    public XamlTypeResolution TryGetType(string xmlNamespace, string localName)
    {
        if (!string.Equals(xmlNamespace, Ui, StringComparison.Ordinal))
            return XamlTypeResolution.NotFound();

        return localName switch
        {
            "StackPanel" => XamlTypeResolution.Resolved(StackPanel),
            "Button" => XamlTypeResolution.Resolved(Button),
            "Border" => XamlTypeResolution.Resolved(Border),
            _ => XamlTypeResolution.NotFound(),
        };
    }

    public string[] GetClrNamespaces(string xmlNamespace) => new[] { "Cursorial.UI.Controls" };

    public string[] GetKnownTypeNames(string xmlNamespace) => new[] { "StackPanel", "Button", "Border" };

    public string[] GetKnownMemberNames(Type clrType)
    {
        if (clrType == typeof(UIControls.StackPanel)) return new[] { "Children" };
        if (clrType == typeof(UIControls.Button)) return new[] { "Content", "Width", "Margin" };
        if (clrType == typeof(UIControls.Border)) return new[] { "Child" };
        return Array.Empty<string>();
    }

    private static readonly XamlType StackPanel = BuildStackPanel();
    private static readonly XamlType Button = BuildButton();
    private static readonly XamlType Border = BuildBorder();

    private static XamlType BuildStackPanel()
    {
        var members = new Dictionary<string, XamlMember>(StringComparer.Ordinal)
        {
            ["Children"] = new XamlMember("Children", typeof(UIElementCollection),
                get: target => ((UIControls.StackPanel)target).Children),
        };
        return new XamlType(
            clrType: typeof(UIControls.StackPanel),
            activate: () => new UIControls.StackPanel(),
            contentProperty: "Children",
            isCollection: true,
            memberResolver: name => members.GetValueOrDefault(name));
    }

    private static XamlType BuildButton()
    {
        var members = new Dictionary<string, XamlMember>(StringComparer.Ordinal)
        {
            ["Content"] = new XamlMember("Content", typeof(object), property: UIControls.ContentControl.ContentProperty),
            ["Width"] = new XamlMember("Width", typeof(int?), property: UIElement.WidthProperty, converter: XamlConverters.For(typeof(int?))),
            ["Margin"] = new XamlMember("Margin", typeof(Margins), property: UIElement.MarginProperty, converter: XamlConverters.For(typeof(Margins))),
        };
        return new XamlType(
            clrType: typeof(UIControls.Button),
            activate: () => new UIControls.Button(),
            contentProperty: "Content",
            memberResolver: name => members.GetValueOrDefault(name));
    }

    private static XamlType BuildBorder()
    {
        var members = new Dictionary<string, XamlMember>(StringComparer.Ordinal)
        {
            ["Child"] = new XamlMember("Child", typeof(UIElement),
                setClr: (target, value) => ((UIControls.Border)target).Child = (UIElement?)value,
                get: target => ((UIControls.Border)target).Child),
        };
        return new XamlType(
            clrType: typeof(UIControls.Border),
            activate: () => new UIControls.Border(),
            contentProperty: "Child",
            memberResolver: name => members.GetValueOrDefault(name));
    }
}
