using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// An explicit <c>Selector="…"</c> lowers to a <c>Selectors</c> fluent chain built from extension methods
/// in <c>Cursorial.UI</c> (.Class / .Child / .Descendant / .OfType / …). The generated file is otherwise
/// fully <c>global::</c>-qualified, so it must emit <c>using global::Cursorial.UI;</c> for the chain to
/// resolve — without it the lowered code parses but does not compile.
/// </summary>
public class SelectorLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact]
    public void Lowered_ExplicitSelector_ChainCompiles()
    {
        // Descendant + child + class + type tokens exercise the whole Selectors extension chain.
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.SelectorView\">" +
            "<StackPanel.Styles>" +
              "<Style Selector=\"StackPanel Button.accent &gt; Border\">" +
                "<Setter Property=\"MinWidth\" Value=\"10\"/>" +
              "</Style>" +
            "</StackPanel.Styles>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SelectorView : StackPanel { public SelectorView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
                                          .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));

        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The selector is baked to the fluent chain (not a runtime parse), and the one using it needs is present.
        Assert.Contains("global::Cursorial.UI.Selectors.OfType", lowered);
        Assert.Contains("using global::Cursorial.UI;", lowered);

        // The point of the fix: the extension-method chain (.Class/.Child/.Descendant/.OfType) must resolve,
        // i.e. the lowered code must COMPILE. EmitAndLoad throws with the compiler diagnostics if it doesn't.
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }
}
