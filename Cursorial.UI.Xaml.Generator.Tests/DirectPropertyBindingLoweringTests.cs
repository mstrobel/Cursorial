using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// DirectProperty&lt;TOwner, T&gt; compiled-binding emission. A DirectProperty derives straight from
/// UIProperty (not StyledProperty) and exposes no typed GetValue overload — the untyped one returns
/// <c>object?</c>, which cannot type the <c>Func&lt;TSource, TValue&gt;</c> getter. The lowered chain
/// therefore routes a direct hop through the typed, no-box <c>Extensions.GetDirect</c>/<c>SetDirect</c>,
/// invoked STATICALLY (an extension member can't be null-conditional). These pin that the emission
/// stays on the compiled lane, produces symbol-correct C#, and reads/writes the direct value live.
/// </summary>
public class DirectPropertyBindingLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    // Builds a StackPanel view whose single TextBlock binds `bindingPath` against `dataType` as x:DataType.
    private static (string Lowered, CSharpCompilation Compilation) Lower(string bindingPath, string dataType, string cls)
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.{cls}\" x:DataType=\"{dataType}\">" +
            $"<TextBlock x:Name=\"Label\" Text=\"{{Binding {bindingPath}}}\"/>" +
            "</StackPanel>";
        var view = $"namespace TestApp {{ public partial class {cls} : Cursorial.UI.Controls.StackPanel {{ public {cls}() => InitializeComponent(); }} }}";

        var compilation = GeneratorHarness.ReferencedCompilation("DirectPropHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        return (GeneratorHarness.LowerView(compilation, xaml), compilation);
    }

    [Fact] // a plain-member DirectProperty read (the with-wrapper lane) routes through GetDirect and compiles
    public void Lowered_PlainDirectProperty_EmitsGetDirect_AndBindsLive()
    {
        var (lowered, compilation) = Lower("Text", "ComboBox", "DirectView1");

        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::Cursorial.UI.Controls.ComboBox, string?>", lowered);
        Assert.Contains("global::Cursorial.UI.Extensions.GetDirect(", lowered);
        Assert.Contains("global::Cursorial.UI.Controls.ComboBox.TextProperty", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered); // NOT the reflective fallback
        Assert.Empty(GeneratorHarness.CompileErrors(lowered));

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.DirectView1")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        view.DataContext = new ComboBox { Text = "chosen" };
        Assert.Equal("chosen", label.Text); // GetDirect(comboBox, ComboBox.TextProperty) read the direct value
    }

    [Fact] // a type-qualified DirectProperty read `(Owner.Member)` routes through GetDirect too
    public void Lowered_QualifiedDirectProperty_EmitsGetDirect_AndBindsLive()
    {
        var (lowered, compilation) = Lower("(ComboBox.Text)", "ComboBox", "DirectView2");

        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::Cursorial.UI.Controls.ComboBox, string?>", lowered);
        Assert.Contains("global::Cursorial.UI.Extensions.GetDirect(", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered);
        Assert.Empty(GeneratorHarness.CompileErrors(lowered));

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.DirectView2")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        view.DataContext = new ComboBox { Text = "qualified" };
        Assert.Equal("qualified", label.Text);
    }

    [Fact] // a TwoWay DirectProperty binding emits SetDirect for the write-back and pushes live
    public void Lowered_TwoWayDirectProperty_EmitsSetDirect_AndWritesBack()
    {
        // Mode=TwoWay + a writable DirectProperty (ComboBox.Text has a setter) → the setter lane engages.
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.DirectView3\" x:DataType=\"ComboBox\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding Text, Mode=TwoWay}\"/>" +
            "</StackPanel>";
        var view = "namespace TestApp { public partial class DirectView3 : Cursorial.UI.Controls.StackPanel { public DirectView3() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("DirectPropHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("global::Cursorial.UI.Extensions.GetDirect(", lowered);
        Assert.Contains("global::Cursorial.UI.Extensions.SetDirect(", lowered);
        Assert.Empty(GeneratorHarness.CompileErrors(lowered));

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var host = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.DirectView3")!)!;
        var label = Assert.IsType<TextBlock>(host.Children[0]);

        var combo = new ComboBox { Text = "start" };
        host.DataContext = combo;
        Assert.Equal("start", label.Text);   // initial read via GetDirect

        label.Text = "pushed";
        Assert.Equal("pushed", combo.Text);   // write-back via SetDirect(comboBox, ComboBox.TextProperty, "pushed")
    }

    [Fact] // a read-only DirectProperty (Border.HasBorder, get-only) still compiles on the direct lane
    public void Lowered_ReadOnlyDirectProperty_EmitsGetDirect_StaysCompiled()
    {
        var (lowered, compilation) = Lower("HasBorder", "Border", "DirectView4");

        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::Cursorial.UI.Controls.Border, bool>", lowered);
        Assert.Contains("global::Cursorial.UI.Extensions.GetDirect(", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered);
        Assert.Empty(GeneratorHarness.CompileErrors(lowered));

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.DirectView4")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        view.DataContext = new Border { BorderPen = new Cursorial.Drawing.Media.Pen(brush: null) };
        Assert.Equal("True", label.Text); // HasBorder is true whenever a (non-null) border pen is present
    }

    [Fact] // a VALUE-typed direct hop mid-chain (DesiredSize is DirectProperty<UIElement, Size>) navigated
    // further must chain the next hop with `?.` — the ternary lifts to Size?, and `.Columns` off Size?
    // would not compile. Guards the prevNullable fix.
    public void Lowered_ValueTypedDirectHop_MidChain_ChainsThroughNullable()
    {
        var (lowered, compilation) = Lower("DesiredSize.Columns", "Border", "DirectView5");

        Assert.Contains("global::Cursorial.UI.Extensions.GetDirect(", lowered);
        Assert.Contains("global::Cursorial.UI.UIElement.DesiredSizeProperty", lowered);
        Assert.Contains(")?.Columns", lowered); // the mid-chain direct value lifts through `?.`
        Assert.Empty(GeneratorHarness.CompileErrors(lowered));

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)Activator.CreateInstance(assembly.GetType("TestApp.DirectView5")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        view.DataContext = new Border(); // unmeasured → DesiredSize is the default Size (Columns == 0)
        Assert.Equal("0", label.Text);
    }
}
