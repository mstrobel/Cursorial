using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// An explicitly-applied (keyed) style needs a TargetType to resolve UNQUALIFIED Setter properties
/// (Property="Background" → Control.BackgroundProperty), but TargetType ALONE synthesizes a type-selector,
/// which is selector-matched and trips SD17 when applied via UIElement.Style / ItemContainerStyle. The
/// documented resolution: pair it with the self-anchor <c>Selector="^"</c> — the explicit selector wins as
/// the matcher (SD17-legal), and TargetType degrades to the frontend's Setter-property resolution hint only.
/// </summary>
public class ExplicitStyleTargetTypeTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // Selector="^" + TargetType="Button": the ^ selector is the matcher; TargetType resolves the
    // unqualified Setter property (Background → Control.BackgroundProperty) without becoming a type-selector.
    public void Lowered_ExplicitStyle_SelfAnchor_WithTargetType_ResolvesUnqualifiedSetter()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.SelfStyleView\">" +
            "<StackPanel.Resources><ResourceDictionary>" +
              "<Style x:Key=\"MyButtonStyle\" Selector=\"^\" TargetType=\"Button\">" +
                "<Setter Property=\"Background\" Value=\"Transparent\"/>" +
              "</Style>" +
            "</ResourceDictionary></StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        var view = "namespace TestApp { public partial class SelfStyleView : Cursorial.UI.Controls.StackPanel { public SelfStyleView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("SelfStyleHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The unqualified Setter property resolved against TargetType="Button" → Control.BackgroundProperty.
        Assert.Contains("new global::Cursorial.UI.Setter(global::Cursorial.UI.Controls.Control.BackgroundProperty", lowered);
        // The matcher is the ^ self-anchor (Nesting), NOT a Button type-selector synthesized from TargetType.
        Assert.Contains("global::Cursorial.UI.Selectors.Nesting()", lowered);
        Assert.DoesNotContain("OfType(null, typeof(global::Cursorial.UI.Controls.Button))", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        // And it compiles + builds the keyed style.
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var instance = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.SelfStyleView")!)!;
        var style = Assert.IsType<Cursorial.UI.Style>(instance.Resources["MyButtonStyle"]);
        Assert.NotNull(style.Selector); // ^-rooted (SD17-legal for explicit application), not selector-less
    }
}
