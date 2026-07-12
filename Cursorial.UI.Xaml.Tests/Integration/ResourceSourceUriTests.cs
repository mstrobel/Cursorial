using Cursorial.UI;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// <c>ResourceDictionary.Source</c> URI resolution: the <c>cursorial://</c> embedded-resource
/// scheme (alias <c>embedded://</c>) and RELATIVE references, which resolve against the containing
/// document's own source URI — so linked dictionaries move with their documents instead of baking
/// machine paths (the Cursorial.Samples regression, 2026-07-12).
/// </summary>
public sealed class ResourceSourceUriTests
{
    private const string Ns = " xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // the default provider maps cursorial://<assembly>/<path> to <assembly>.<dotted path>
    public void EmbeddedProvider_ResolvesCursorialScheme()
    {
        var provider = new EmbeddedXamlResourceProvider();

        Assert.True(provider.TryGetXaml(new Uri("cursorial://Cursorial.UI.Xaml.Tests/TestData/EmbeddedProbe.xaml"), out var xaml));
        Assert.Contains("embedded-hit", xaml);
    }

    [Fact] // embedded:// is an accepted alias for the same lookup
    public void EmbeddedProvider_AcceptsEmbeddedSchemeAlias()
    {
        var provider = new EmbeddedXamlResourceProvider();

        Assert.True(provider.TryGetXaml(new Uri("embedded://Cursorial.UI.Xaml.Tests/TestData/EmbeddedProbe.xaml"), out var xaml));
        Assert.Contains("embedded-hit", xaml);
    }

    [Fact] // a relative URI is a clean miss (no scheme to interpret), never a throw
    public void EmbeddedProvider_MissesRelativeUris()
    {
        var provider = new EmbeddedXamlResourceProvider();
        Assert.False(provider.TryGetXaml(new Uri("TestData/EmbeddedProbe.xaml", UriKind.Relative), out _));
    }

    [Fact] // Source="Res.xaml" inside a document loaded AS cursorial://app/Views/Main.xaml resolves next to it
    public void RelativeSource_ResolvesAgainstTheDocumentUri()
    {
        Uri? requested = null;
        using var _ = XamlModule.UseResourceProvider(new RecordingProvider(uri =>
        {
            requested = uri;
            return "<ResourceDictionary" + Ns + "><x:String x:Key=\"K\">v</x:String></ResourceDictionary>";
        }));

        var root = (ResourceDictionary)new XamlLoader().Load(
            "<ResourceDictionary" + Ns + "><ResourceDictionary.MergedDictionaries>" +
            "<ResourceDictionary Source=\"Res.xaml\"/>" +
            "</ResourceDictionary.MergedDictionaries></ResourceDictionary>",
            new Uri("cursorial://SampleApp/Views/Main.xaml"));

        // Case-SENSITIVE: the authority names an assembly, and composition must preserve its
        // original casing (System.Uri lowercases hosts; Assembly.Load is case-sensitive on Linux).
        Assert.Equal("cursorial://SampleApp/Views/Res.xaml", requested?.OriginalString);
        Assert.Equal("v", root.MergedDictionaries[0]["K"]);
    }

    [Fact] // an absolute reference passes through untouched
    public void AbsoluteSource_PassesThrough()
    {
        Uri? requested = null;
        using var _ = XamlModule.UseResourceProvider(new RecordingProvider(uri =>
        {
            requested = uri;
            return "<ResourceDictionary" + Ns + "/>";
        }));

        new XamlLoader().Load(
            "<ResourceDictionary" + Ns + "><ResourceDictionary.MergedDictionaries>" +
            "<ResourceDictionary Source=\"embedded://Other/Theme.xaml\"/>" +
            "</ResourceDictionary.MergedDictionaries></ResourceDictionary>",
            new Uri("cursorial://SampleApp/Views/Main.xaml"));

        Assert.Equal("embedded://Other/Theme.xaml", requested?.OriginalString);
    }

    [Fact] // the design-time seam: LoadComponent re-sources the baked document from the hook
    public void LiveXamlSource_OverridesTheBakedDocument()
    {
        var loader = new XamlLoader();
        var baked = loader.Parse(
            "<StackPanel" + Ns + "><TextBlock Text=\"baked\"/></StackPanel>",
            new Uri("cursorial://App/Views/V.xaml"));

        var previous = XamlModule.LiveXamlSource;
        try
        {
            XamlModule.LiveXamlSource = uri =>
                uri.OriginalString.Contains("V.xaml", StringComparison.Ordinal)
                    ? "<StackPanel" + Ns + "><TextBlock Text=\"live\"/></StackPanel>"
                    : null;

            var component = new Cursorial.UI.Controls.StackPanel();
            loader.LoadComponent(component, baked);

            var text = Assert.IsType<Cursorial.UI.Controls.TextBlock>(component.Children[0]);
            Assert.Equal("live", text.Text);
        }
        finally
        {
            XamlModule.LiveXamlSource = previous;
        }
    }

    private sealed class RecordingProvider(Func<Uri, string?> resolve) : IXamlResourceProvider
    {
        public bool TryGetXaml(Uri uri, out string? xaml)
        {
            xaml = resolve(uri);
            return xaml is not null;
        }
    }
}
