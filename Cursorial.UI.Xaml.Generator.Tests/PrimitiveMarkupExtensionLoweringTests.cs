using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The lowered (generator) lane of the built-in-primitive markup-extension form: <c>{x:Boolean False}</c>
/// builds the same synthetic primitive-object node as <c>&lt;x:Boolean&gt;False&lt;/x:Boolean&gt;</c>, so it
/// lowers through the existing <c>EmitInitTextPrimitive</c> path — no new emitter branch. Guards parity with
/// the loader (invariant: whatever the runtime accepts, the emitter emits, and vice-versa).
/// </summary>
public class PrimitiveMarkupExtensionLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // {x:Boolean False} lowers (no fence) and sets the value, matching the element form
    public void Lowered_XBoolean_CurlyExtension_Works()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.PrimView\">" +
            "<Button x:Name=\"B\" IsEnabled=\"{x:Boolean False}\"/>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class PrimView : Cursorial.UI.Controls.StackPanel { public PrimView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("PrimHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO", lowered); // no silent drop / fence

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var instance = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.PrimView")!)!;
        var button = Assert.IsType<Button>(instance.Children[0]);
        Assert.False(button.IsEnabled); // {x:Boolean False} → false
    }
}
