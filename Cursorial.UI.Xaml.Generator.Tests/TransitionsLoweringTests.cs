using Cursorial.UI;
using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// Transitions + UIProperty tokens in the X4 generator (W2b lane parity with
/// <c>Section23_TransitionsMarkup</c>): a parse-resolved <c>UIProperty</c> token lowers to the static
/// registration field (no runtime lookup — the Roslyn lane's identity is the owner symbol + member name),
/// the attached collection fills through the <c>GetOrCreate{Name}</c> probe, and an unresolvable token
/// fails the BUILD with the same positioned diagnostic the loader throws (the parse-time route decision
/// serving both lanes identically).
/// </summary>
public class TransitionsLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static (string Lowered, CSharpCompilation Compilation) Lower(string xaml, string view)
    {
        var compilation = GeneratorHarness.ReferencedCompilation("TransLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        return (GeneratorHarness.LowerView(compilation, xaml), compilation);
    }

    [Fact] // GB1: the flagship — unqualified token against the host element lowers to the static field + runs
    public void Lowered_TransitionChild_UnqualifiedToken_RunsEndToEnd()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.Trans1\">" +
              "<Border>" +
                "<Transition.Transitions>" +
                  "<DoubleTransition Property=\"Opacity\" Duration=\"0:0:0.1\"/>" +
                "</Transition.Transitions>" +
              "</Border>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class Trans1 : Cursorial.UI.Controls.StackPanel { public Trans1() => InitializeComponent(); } }";

        var (lowered, compilation) = Lower(xaml, view);
        Assert.DoesNotContain("ERROR X5", lowered);
        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("UIElement.OpacityProperty", lowered); // the static registration field, no runtime lookup

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.Trans1")!)!;
        var border = Assert.IsType<Border>(root.Children[0]);

        var transitions = border.GetValue(Transition.TransitionsProperty);
        Assert.NotNull(transitions);
        var transition = Assert.IsType<DoubleTransition>(Assert.Single(transitions!));
        Assert.Same(UIElement.OpacityProperty, transition.Property);
        Assert.Equal(TimeSpan.FromSeconds(0.1), transition.Duration);
    }

    [Fact] // GB2: the owner-qualified form in a target-less resource position lowers identically
    public void Lowered_OwnerQualifiedToken_InResource()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.Trans2\"><StackPanel.Resources>" +
              "<TransitionCollection x:Key=\"fades\">" +
                "<DoubleTransition Property=\"UIElement.Opacity\"/>" +
              "</TransitionCollection>" +
            "</StackPanel.Resources></StackPanel>";
        var view = "namespace GenApp { public partial class Trans2 : Cursorial.UI.Controls.StackPanel { public Trans2() => InitializeComponent(); } }";

        var (lowered, compilation) = Lower(xaml, view);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.Trans2")!)!;
        var fades = Assert.IsType<TransitionCollection>(root.Resources!["fades"]);
        Assert.Same(UIElement.OpacityProperty, Assert.IsType<DoubleTransition>(Assert.Single(fades)).Property);
    }

    [Fact] // GB3: an unresolvable token is a PARSE-band diagnostic — the identical document fails both
    // lanes at the same position (the G4 blindness class, closed for this member family)
    public void Lowered_UnresolvableToken_IsParseDiagnostic()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.Trans3\">" +
              "<Border><Transition.Transitions>" +
                "<DoubleTransition Property=\"UIElement.NoSuchProperty\"/>" +
              "</Transition.Transitions></Border>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class Trans3 : Cursorial.UI.Controls.StackPanel { public Trans3() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("TransLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var document = Cursorial.UI.Xaml.XamlFrontend.Parse(xaml, new Cursorial.UI.Xaml.XamlParseOptions
        {
            MetadataProvider = new Cursorial.UI.Xaml.Generator.RoslynXamlMetadata(compilation),
            DiagnosticMode = Cursorial.UI.Xaml.XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });

        Assert.Contains(document.Diagnostics, d =>
            d.Code == "CUR2102" && d.Message.Contains("NoSuchProperty") && d.Line > 0 && d.Column > 0);
    }
}
