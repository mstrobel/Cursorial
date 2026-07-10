using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Section 21 — source-position tracking (<see cref="XamlLoaderOptions.TrackSourceInfo"/> →
/// <see cref="XamlSourceRegistry"/>). A designer/tooling opt-in: every instantiated object
/// remembers its document URI and 1-based element position; default-off loads register nothing.
/// </summary>
public sealed class Section21_SourceInfo : XamlTestBase
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static readonly Uri Source = new("cursorial://test/tracked.xaml");

    private static XamlLoader TrackingLoader() => new(new XamlLoaderOptions
    {
        DiagnosticMode = XamlDiagnosticMode.CollectAll,
        TrackSourceInfo = true,
    });

    [Fact] // the root and nested elements carry their tag positions + the document URI
    public void X220_TrackedLoad_StampsRootAndChildren()
    {
        var root = (StackPanel)TrackingLoader().Load(
            "<StackPanel " + Ns + ">\n" +
            "    <Border>\n" +
            "        <TextBlock Text=\"hi\"/>\n" +
            "    </Border>\n" +
            "</StackPanel>", Source);

        var rootInfo = XamlSourceRegistry.TryGetSourceInfo(root);
        Assert.NotNull(rootInfo);
        Assert.Equal(Source, rootInfo!.Source);
        Assert.Equal(1, rootInfo.Line);

        var border = (Border)root.GetVisualChild(0);
        var borderInfo = XamlSourceRegistry.TryGetSourceInfo(border);
        Assert.Equal(2, borderInfo!.Line);

        var text = Assert.IsType<TextBlock>(border.Child);
        var textInfo = XamlSourceRegistry.TryGetSourceInfo(text);
        Assert.Equal(3, textInfo!.Line);
        Assert.True(textInfo.Column > borderInfo.Column); // deeper indent, later column
    }

    [Fact] // default options register nothing — production loads carry no tracking
    public void X221_DefaultLoad_RegistersNothing()
    {
        var root = (StackPanel)new XamlLoader().Load("<StackPanel " + Ns + "><TextBlock Text=\"x\"/></StackPanel>", Source);

        Assert.Null(XamlSourceRegistry.TryGetSourceInfo(root));
        Assert.Null(XamlSourceRegistry.TryGetSourceInfo(root.GetVisualChild(0)));
    }

    [Fact] // non-element objects (resources) are tracked too — a designer can trace a brush
    public void X222_ResourceObjects_AreTracked()
    {
        var root = (Border)TrackingLoader().Load(
            "<Border " + Ns + ">\n" +
            "    <Border.Resources>\n" +
            "        <SolidColorBrush x:Key=\"Accent\" Color=\"#3050c0\"/>\n" +
            "    </Border.Resources>\n" +
            "</Border>", Source);

        var brush = root.Resources["Accent"];
        Assert.NotNull(brush);
        var info = XamlSourceRegistry.TryGetSourceInfo(brush!);
        Assert.NotNull(info);
        Assert.Equal(3, info!.Line);
    }
}
