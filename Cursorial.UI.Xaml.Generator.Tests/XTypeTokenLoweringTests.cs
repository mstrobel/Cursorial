using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// <c>{x:Type}</c> in the GENERATOR previously folded to a provider-dependent <see cref="System.Type"/> that was
/// <see langword="null"/> under the symbol-only Roslyn provider — a SILENT DROP (<c>DataTemplate DataType</c> lost,
/// a Type-valued member unset). It now carries a <c>XamlTypeReference</c> token resolved per-lane (the loader→a
/// <see cref="System.Type"/>, the generator→<c>typeof(...)</c>), so the curly form lowers wherever the bare does.
/// </summary>
public class XTypeTokenLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\"";

    private const string Vms = @"
namespace GenApp
{
    public class ItemVm { public string Name { get; set; } = """"; }
    public class TypeHost : Cursorial.UI.Controls.StackPanel { public System.Type? Probe { get; set; } }
}";

    [Fact] // <DataTemplate DataType="{x:Type vm:ItemVm}"> — the CURLY form now flows ItemVm to the body root (was a
    // silent generator drop; the bare DataType="vm:ItemVm" always worked). The compiled binding proves resolution.
    public void Lowered_DataTemplate_CurlyXType_DataType_FlowsToBodyRoot()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.XtView1\">" +
            "<ContentControl>" +
              "<ContentControl.ContentTemplate>" +
                "<DataTemplate DataType=\"{x:Type vm:ItemVm}\">" +
                  "<TextBlock Text=\"{Binding Name}\"/>" +   // NO x:DataType on the root — flowed from the curly DataType
                "</DataTemplate>" +
              "</ContentControl.ContentTemplate>" +
            "</ContentControl>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class XtView1 : Cursorial.UI.Controls.StackPanel { public XtView1() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("XtHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Vms), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::GenApp.ItemVm,", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(\"Name\")", lowered);
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }

    [Fact] // a System.Type-valued member set via {x:Type} lowers to typeof(...) (the FoldedValueExpr token arm) —
    // parity with the bare Probe="Button" scalar-type arm; neither is a silent drop.
    public void Lowered_TypeValuedMember_CurlyXType_EmitsTypeof()
    {
        var xaml = $"<vm:TypeHost {Ns} x:Class=\"GenApp.XtView2\" Probe=\"{{x:Type Button}}\"/>";
        var view = "namespace GenApp { public partial class XtView2 : GenApp.TypeHost { public XtView2() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("XtHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Vms), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains(".Probe = typeof(global::Cursorial.UI.Controls.Button)", lowered);
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }
}
