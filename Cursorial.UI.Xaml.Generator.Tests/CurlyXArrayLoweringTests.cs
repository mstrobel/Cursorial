using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The curly <c>{x:Array Type=T, item, …}</c> form builds the SAME <c>IsArray</c> node the element form does
/// (frontend-only), so the generator's existing <c>EmitArray</c> lowers it with no new path — a typed <c>T[]</c>,
/// identical to <c>&lt;x:Array Type="T"&gt;…&lt;/x:Array&gt;</c>. Loader parity is proven in Section18_XArray.
/// </summary>
public class CurlyXArrayLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact]
    public void Lowered_CurlyXArray_BuildsTypedIntArray()
    {
        var xaml =
            "<StackPanel " + Ns + " x:Class=\"GenApp.ArrView\">" +
            "<Button Content=\"{x:Array Type=x:Int32, {x:Int32 7}, {x:Int32 42}}\"/>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class ArrView : Cursorial.UI.Controls.StackPanel { public ArrView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("ArrHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var stack = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.ArrView")!)!;
        var button = (Button)stack.Children[0];
        Assert.Equal([7, 42], Assert.IsType<int[]>(button.Content));
    }
}
