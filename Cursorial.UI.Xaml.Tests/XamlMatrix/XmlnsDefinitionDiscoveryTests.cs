using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

// #108 — the default xmlns→CLR map is DISCOVERED from [assembly: XmlnsDefinition] declarations (in Cursorial.Shared's
// Cursorial.Markup namespace, applied on Cursorial.UI + Cursorial.Drawing), not hand-maintained in XamlSchemaContext.
// A fresh context runs discovery over the seed assemblies in its constructor, so it sees exactly the declared map.
public sealed class XmlnsDefinitionDiscoveryTests
{
    private const string Ui = "https://cursorial.dev/ui";

    [Fact] // The default map is the union of the [XmlnsDefinition] declarations on the seed assemblies (UI + Drawing).
    public void DefaultMap_IsDiscoveredFromAssemblyAttributes()
    {
        var context = new XamlSchemaContext();
        var namespaces = context.GetClrNamespaces(Ui);

        // Cursorial.UI declares these five; Cursorial.Drawing declares Drawing.Media — all discovered, none hardcoded.
        Assert.Contains("Cursorial.UI", namespaces);
        Assert.Contains("Cursorial.UI.Controls", namespaces);
        Assert.Contains("Cursorial.UI.Data", namespaces);
        Assert.Contains("Cursorial.UI.Input", namespaces);
        Assert.Contains("Cursorial.UI.Themes", namespaces);
        Assert.Contains("Cursorial.Drawing.Media", namespaces);
    }

    [Theory] // Types in each discovered namespace resolve under the default URI (incl. the cross-assembly Drawing decl).
    [InlineData("Button", "Cursorial.UI.Controls.Button")]              // Cursorial.UI declaration
    [InlineData("SolidColorBrush", "Cursorial.Drawing.Media.SolidColorBrush")] // Cursorial.Drawing declaration
    [InlineData("ThemeKeys", "Cursorial.UI.Themes.ThemeKeys")]          // Cursorial.UI.Themes declaration
    public void DiscoveredNamespace_ResolvesTypes(string localName, string expectedFullName)
    {
        var context = new XamlSchemaContext();
        var type = context.Resolve(Ui, localName, out var ambiguous);

        Assert.Empty(ambiguous);
        Assert.NotNull(type);
        Assert.Equal(expectedFullName, type!.FullName);
    }
}
