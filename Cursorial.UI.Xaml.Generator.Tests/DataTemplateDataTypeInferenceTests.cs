using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// A DataTemplate's <c>DataType</c> flows to its body ROOT as the compiled-binding source type, so bindings
/// inside the template resolve against it without repeating <c>x:DataType</c> on the root — unless the root
/// states its own <c>x:DataType</c>, which still wins (EmitObject: <c>ForObject(...) ?? dataType</c>).
/// </summary>
public class DataTemplateDataTypeInferenceTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\"";

    private const string Vms = @"
namespace GenApp
{
    public class ItemVm  { public string Name  { get; set; } = """"; }
    public class OtherVm { public string Title { get; set; } = """"; }
}";

    [Fact] // <DataTemplate DataType="vm:ItemVm"> flows ItemVm to the body root — {Binding Name} compiles against ItemVm
    public void Lowered_DataTemplate_DataType_FlowsToBodyRoot_NoXDataTypeNeeded()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.DtView1\">" +
            "<ContentControl>" +
              "<ContentControl.ContentTemplate>" +
                "<DataTemplate DataType=\"vm:ItemVm\">" +
                  "<TextBlock Text=\"{Binding Name}\"/>" +   // NO x:DataType on the root
                "</DataTemplate>" +
              "</ContentControl.ContentTemplate>" +
            "</ContentControl>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class DtView1 : Cursorial.UI.Controls.StackPanel { public DtView1() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("DtHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Vms), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The binding resolved against ItemVm (the flowed DataType) — a COMPILED binding, not the reflective fallback.
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::GenApp.ItemVm,", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(\"Name\")", lowered);
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }

    [Fact] // an explicit x:DataType on the body root OVERRIDES the DataTemplate's DataType ("unless explicitly stated")
    public void Lowered_DataTemplate_ExplicitRootXDataType_Wins()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.DtView2\">" +
            "<ContentControl>" +
              "<ContentControl.ContentTemplate>" +
                "<DataTemplate DataType=\"vm:ItemVm\">" +
                  "<TextBlock x:DataType=\"vm:OtherVm\" Text=\"{Binding Title}\"/>" +  // root states its own type
                "</DataTemplate>" +
              "</ContentControl.ContentTemplate>" +
            "</ContentControl>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class DtView2 : Cursorial.UI.Controls.StackPanel { public DtView2() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("DtHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Vms), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The root's own x:DataType (OtherVm) won over the DataTemplate's DataType (ItemVm).
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::GenApp.OtherVm,", lowered);
        Assert.DoesNotContain("CompiledBinding<global::GenApp.ItemVm", lowered);
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }
}
