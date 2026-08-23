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

    [Fact] // a DataContext-relative Setter.Value binding stays reflective when NO x:DataType is in scope — unchanged
    public void Lowered_SetterValue_DataContextBinding_NoDataType_StaysReflective()
    {
        var lowered = LowerStyle(
            "<Style TargetType=\"Border\"><Setter Property=\"Occludes\" Value=\"{Binding Foo}\"/></Style>");

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"Foo\")", lowered);
    }

    private const string Vm = "namespace GenApp { public class Foo { public bool Flag { get; set; } } }";

    [Fact] // a DataContext-relative Setter.Value binding COMPILES when an x:DataType is in lexical scope (the
    // DataTemplate's DataType flows to the inline style's setter) — the author-asserted DataContext type.
    public void Lowered_SetterValue_DataContextBinding_WithDataType_IsCompiled()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\" x:Class=\"GenApp.DtsView\">" +
            "<ContentControl><ContentControl.ContentTemplate>" +
              "<DataTemplate DataType=\"vm:Foo\">" +
                "<Border><Border.Style>" +
                  "<Style TargetType=\"Border\"><Setter Property=\"Occludes\" Value=\"{Binding Flag}\"/></Style>" +
                "</Border.Style></Border>" +
              "</DataTemplate>" +
            "</ContentControl.ContentTemplate></ContentControl>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class DtsView : Cursorial.UI.Controls.StackPanel { public DtsView() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("SbHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Vm), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::GenApp.Foo,", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(\"Flag\")", lowered);
    }
}
