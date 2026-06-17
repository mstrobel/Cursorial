using System.Linq;

using Cursorial.UI.Xaml.Generator;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X4.5 — the closed-type-set collector records every element name the parser visits (including
/// nested children, under error recovery), so the generated metadata provider can cover exactly the
/// types a document references.
/// </summary>
public class ClosedTypeSetTests
{
    private const string Ui = "https://cursorial.dev/ui";

    [Fact]
    public void Collects_NestedElementNames()
    {
        const string xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "  <Button/>" +
            "  <Border><TextBlock/></Border>" +
            "</StackPanel>";

        var names = ClosedTypeSet.CollectElementNames(xaml);
        var local = names.Where(n => n.Namespace == Ui).Select(n => n.LocalName).ToHashSet();

        Assert.Contains("StackPanel", local);
        Assert.Contains("Button", local);
        Assert.Contains("Border", local);
        Assert.Contains("TextBlock", local); // the deeply-nested child is captured too
    }

    [Fact]
    public void Deduplicates_RepeatedNames()
    {
        const string xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\">" +
            "  <Button/><Button/><Button/>" +
            "</StackPanel>";

        var names = ClosedTypeSet.CollectElementNames(xaml);
        Assert.Equal(1, names.Count(n => n.LocalName == "Button"));
    }
}
