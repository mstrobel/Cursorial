using System.Reflection;

using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;
using Cursorial.UI.Xaml.Generator;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5 — the full-lowering spine. The lowered <c>InitializeComponent</c> constructs the tree as
/// straight-line C# (no runtime loader / no reflection). These tests emit it, compile it against a real
/// code-behind, instantiate, and assert the resulting tree matches what the runtime loader builds from the
/// same XAML — the lowered/loaded equivalence gate.
/// </summary>
public class LoweringEmitterTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static string Lower(string xaml, CSharpCompilation compilation)
    {
        var document = XamlFrontend.Parse(xaml, new XamlParseOptions
        {
            MetadataProvider = new RoslynXamlMetadata(compilation),
            DiagnosticMode = XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });
        return LoweringEmitter.Emit(document, "MyView.xaml")
            ?? throw new System.InvalidOperationException("no lowering emitted");
    }

    [Fact]
    public void Lowered_BuildsTree_MatchingRuntimeLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.MyView\">" +
            "<Button x:Name=\"Ok\" Content=\"OK\"/>" +
            "<Border x:Name=\"Frame\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class MyView : StackPanel { public MyView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        // The lowered code is reflection-free C#; compile it with the code-behind and instantiate.
        var withLowering = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind),
            CSharpSyntaxTree.ParseText(lowered));
        var assembly = GeneratorHarness.EmitAndLoad(withLowering);
        var viewType = assembly.GetType("TestApp.MyView")!;
        var view = (StackPanel)System.Activator.CreateInstance(viewType)!;

        // The runtime loader builds the reference tree from the same XAML (reflection provider).
        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        // Same shape + values as the loader.
        Assert.Equal(runtime.Children.Count, view.Children.Count);
        Assert.Equal(2, view.Children.Count);

        var loweredOk = Assert.IsType<Button>(view.Children[0]);
        var runtimeOk = Assert.IsType<Button>(runtime.Children[0]);
        Assert.Equal(runtimeOk.Content, loweredOk.Content);
        Assert.Equal("OK", loweredOk.Content);
        Assert.IsType<Border>(view.Children[1]);

        // The typed x:Name fields point at the constructed elements.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        Assert.Same(loweredOk, viewType.GetField("Ok", flags)!.GetValue(view));
        Assert.Same(view.Children[1], viewType.GetField("Frame", flags)!.GetValue(view));
    }

    [Fact] // a class-less document has no code-behind to lower
    public void NoXClass_EmitsNothing()
    {
        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var document = XamlFrontend.Parse($"<StackPanel {Ns}><Button/></StackPanel>",
            new XamlParseOptions { MetadataProvider = new RoslynXamlMetadata(compilation), FoldConstants = false });
        Assert.Null(LoweringEmitter.Emit(document, "x.xaml"));
    }
}
