using Cursorial.UI;
using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// Emitter parity for the universal self-skip: a same-keyed <c>Style.BasedOn="{StaticResource K}"</c> resolves to
/// the OUTER (enclosing) K, matching the loader (Section19_BasedOnSkipSelf) — not a self-cycle, not a dropped style.
/// </summary>
public class BasedOnSkipSelfLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact]
    public void Lowered_SelfKeyBasedOn_ResolvesOuterAmbientStyle()
    {
        var xaml =
            "<StackPanel " + Ns + " x:Class=\"GenApp.BoView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"K\" TargetType=\"Button\"><Setter Property=\"Width\" Value=\"3\"/></Style>" +
            "</StackPanel.Resources>" +
            "<Button><Button.Resources>" +
              "<Style x:Key=\"K\" TargetType=\"Button\" BasedOn=\"{StaticResource K}\"><Setter Property=\"Height\" Value=\"9\"/></Style>" +
            "</Button.Resources></Button>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class BoView : Cursorial.UI.Controls.StackPanel { public BoView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("BoHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered); // the style is NOT dropped

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.BoView")!)!;
        var outer = (Style)root.Resources["K"];
        var button = (Button)root.Children[0];
        var derived = (Style)button.Resources["K"];

        Assert.NotSame(outer, derived);
        Assert.Same(outer, derived.BasedOn); // BasedOn skipped self, resolved the OUTER K (parity with the loader)
    }
}
