namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X4.1 — the generator skeleton round-trips a <c>CursorialXaml</c> additional file to a generated
/// marker (proving analyzer-load + the item-metadata filter before the codegen passes land). The
/// codegen-output tests (diagnostics, x:Name fields + InitializeComponent, the generated metadata
/// provider) join this project at WS-X4.4–X4.6.
/// </summary>
public class XamlSourceGeneratorTests
{
    [Fact]
    public void Generator_EmitsMarker_ForCursorialXamlFile()
    {
        var result = GeneratorHarness.Run(("View.xaml", "<DockPanel xmlns=\"...\" x:Class=\"App.View\"/>"));

        Assert.Empty(result.Diagnostics);
        var generated = Assert.Single(result.Results);
        var tree = Assert.Single(generated.GeneratedSources);
        Assert.Contains("View", tree.HintName);
        Assert.Contains("source: View.xaml", tree.SourceText.ToString());
    }

    [Fact]
    public void Generator_IgnoresNonXamlAndUnflaggedFiles()
    {
        // A .xaml file WITHOUT the CursorialXaml item metadata is ignored (only the flagged channel is ours).
        var result = GeneratorHarness.Run(); // no files
        Assert.Empty(result.Results.SelectMany(r => r.GeneratedSources));
    }

    [Fact]
    public void Generator_EmitsOnePerFile()
    {
        var result = GeneratorHarness.Run(
            ("Alpha.xaml", "<Border/>"),
            ("Beta.xaml", "<StackPanel/>"));

        var sources = result.Results.SelectMany(r => r.GeneratedSources).ToList();
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, s => s.HintName.Contains("Alpha"));
        Assert.Contains(sources, s => s.HintName.Contains("Beta"));
    }
}
