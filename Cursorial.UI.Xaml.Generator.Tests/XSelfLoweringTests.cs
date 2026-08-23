using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;

using Microsoft.CodeAnalysis.CSharp;

using Binding = Cursorial.UI.Data.Binding;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// <c>{x:Self}</c> in the generator (Level-0 slice) — parity with the loader (Section20_XSelf): a member value
/// resolves to the target's local var (UIProperty and CLR paths), a Binding <c>Source={x:Self}</c> anchors on the
/// target and installs inline, and the target-less positions FAIL the build (ERROR X5 — the loader Fatals).
/// </summary>
public class XSelfLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\"";

    private const string Host = @"
namespace GenApp
{
    public class SelfHost : Cursorial.UI.Controls.Control
    {
        public static readonly Cursorial.UI.StyledProperty<object?> PayloadProperty =
            Cursorial.UI.UIProperty.Register<SelfHost, object?>(nameof(Payload));
        public object? Payload { get => GetValue(PayloadProperty); set => SetValue(PayloadProperty, value); }
        public object? Bag { get; set; }
    }
}";

    private static (string Lowered, Microsoft.CodeAnalysis.CSharp.CSharpCompilation Compilation) Lower(string xaml, string view)
    {
        var compilation = GeneratorHarness.ReferencedCompilation("SelfLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Host), CSharpSyntaxTree.ParseText(view));
        return (GeneratorHarness.LowerView(compilation, xaml), compilation);
    }

    [Fact] // a UIProperty member value {x:Self} lowers to SetValue(..., the target var) — same instance at runtime
    public void Lowered_MemberValue_UIProperty_ResolvesToTarget()
    {
        var (lowered, compilation) = Lower(
            $"<vm:SelfHost {Ns} x:Class=\"GenApp.Sv1\" Payload=\"{{x:Self}}\"/>",
            "namespace GenApp { public partial class Sv1 : GenApp.SelfHost { public Sv1() => InitializeComponent(); } }");

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        dynamic host = System.Activator.CreateInstance(assembly.GetType("GenApp.Sv1")!)!;
        Assert.Same((object)host, (object)host.Payload);
    }

    [Fact] // a CLR member value {x:Self} lowers to a plain self-assign — same instance at runtime
    public void Lowered_MemberValue_ClrProperty_ResolvesToTarget()
    {
        var (lowered, compilation) = Lower(
            $"<vm:SelfHost {Ns} x:Class=\"GenApp.Sv2\" Bag=\"{{x:Self}}\"/>",
            "namespace GenApp { public partial class Sv2 : GenApp.SelfHost { public Sv2() => InitializeComponent(); } }");

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        dynamic host = System.Activator.CreateInstance(assembly.GetType("GenApp.Sv2")!)!;
        Assert.Same((object)host, (object)host.Bag);
    }

    [Fact] // {Binding Width, Source={x:Self}} anchors on the target and installs INLINE (loader parity)
    public void Lowered_BindingSource_ResolvesToBindingTarget()
    {
        var (lowered, compilation) = Lower(
            $"<StackPanel {Ns} x:Class=\"GenApp.Sv3\"><Border Height=\"{{Binding Width, Source={{x:Self}}}}\"/></StackPanel>",
            "namespace GenApp { public partial class Sv3 : Cursorial.UI.Controls.StackPanel { public Sv3() => InitializeComponent(); } }");

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.Sv3")!)!;
        var border = (Border)root.Children[0];
        var binding = (Binding)BindingOperations.GetBindingExpression(border, UIElement.HeightProperty)!.ParentBinding!;
        Assert.Same(border, binding.Source); // the Border, never the Binding
    }

    [Fact] // Setter.Value {x:Self} FAILS the build (ERROR X5) — the loader Fatals on the identical document
    public void Lowered_SetterValue_IsError()
    {
        var (lowered, _) = Lower(
            $"<StackPanel {Ns} x:Class=\"GenApp.Sv4\"><StackPanel.Resources>" +
            "<Style TargetType=\"vm:SelfHost\"><Setter Property=\"Payload\" Value=\"{x:Self}\"/></Style>" +
            "</StackPanel.Resources></StackPanel>",
            "namespace GenApp { public partial class Sv4 : Cursorial.UI.Controls.StackPanel { public Sv4() => InitializeComponent(); } }");

        Assert.Contains("ERROR X5", lowered);
        Assert.Contains("assignment target", lowered);
    }

    [Fact] // a descriptor-position Source={x:Self} (DataCondition.Binding) FAILS the build — loader parity. The
    // specific ERROR is recorded on LoweringResult.Errors (a CURG3002 build failure); its inline marker line lives
    // in the When validation buffer, which is DISCARDED with the dropped style (the established When pattern), so
    // the surviving source carries the generic fail-closed drop marker.
    public void Lowered_DescriptorBindingSource_IsError()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.Sv5\"><StackPanel.Resources>" +
            "<Style TargetType=\"Border\">" +
              "<Style.When><DataCondition Binding=\"{Binding Width, Source={x:Self}}\" Value=\"3\"/></Style.When>" +
              "<Setter Property=\"Occludes\" Value=\"{x:Boolean True}\"/>" +
            "</Style>" +
            "</StackPanel.Resources></StackPanel>";
        var view = "namespace GenApp { public partial class Sv5 : Cursorial.UI.Controls.StackPanel { public Sv5() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("SelfLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Host), CSharpSyntaxTree.ParseText(view));

        var document = Cursorial.UI.Xaml.XamlFrontend.Parse(xaml, new Cursorial.UI.Xaml.XamlParseOptions
        {
            MetadataProvider = new Cursorial.UI.Xaml.Generator.RoslynXamlMetadata(compilation),
            DiagnosticMode = Cursorial.UI.Xaml.XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });
        var result = Cursorial.UI.Xaml.Generator.LoweringEmitter.Emit(
            document, "MyView.xaml", "MyView.xaml", new Cursorial.UI.Xaml.Generator.XamlSymbolResolver(compilation))
            ?? throw new System.InvalidOperationException("no lowering emitted");

        Assert.Contains("TODO X5", result.Source);                        // the fail-closed style drop (never half-lowered)
        Assert.Contains(result.Errors, e => e.Message.Contains("descriptor")); // the specific CURG3002 — the build FAILS
    }
}
