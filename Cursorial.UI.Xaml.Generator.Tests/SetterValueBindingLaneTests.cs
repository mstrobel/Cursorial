using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// A binding-valued <c>Setter.Value</c> now tries the COMPILED descriptor lane first (mirroring
/// <c>DataCondition.Binding</c>), with the Style's <c>TargetType</c> as the RelativeSource=Self root — so a
/// Self-anchored setter binding lowers to a typed <c>CompiledBinding</c> instead of being forced reflective. A
/// DataContext-relative binding (no source type in scope) still stays reflective.
/// </summary>
public class SetterValueBindingLaneTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static string LowerStyle(string styleBody)
    {
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.SbView\"><StackPanel.Resources>{styleBody}</StackPanel.Resources></StackPanel>";
        var view = "namespace GenApp { public partial class SbView : Cursorial.UI.Controls.StackPanel { public SbView() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("SbHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        return GeneratorHarness.LowerView(compilation, xaml);
    }

    [Fact] // a RelativeSource=Self Setter.Value binding compiles against the Style's TargetType (was reflective)
    public void Lowered_SetterValue_SelfAnchoredBinding_IsCompiled()
    {
        var lowered = LowerStyle(
            "<Style TargetType=\"Border\"><Setter Property=\"Occludes\" " +
            "Value=\"{Binding Width, RelativeSource={RelativeSource Self}}\"/></Style>");

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::Cursorial.UI.Controls.Border,", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(\"Width\")", lowered); // NOT the reflective form
    }

    [Fact] // a DataContext-relative Setter.Value binding stays reflective (no source type in scope) — unchanged
    public void Lowered_SetterValue_DataContextBinding_StaysReflective()
    {
        var lowered = LowerStyle(
            "<Style TargetType=\"Border\"><Setter Property=\"Occludes\" Value=\"{Binding Foo}\"/></Style>");

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"Foo\")", lowered);
    }
}
