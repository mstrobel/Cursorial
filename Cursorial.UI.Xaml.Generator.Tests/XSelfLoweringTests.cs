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

    [Fact] // review: ConverterParameter={x:Self} was SILENTLY omitted (literal-only shaping read) — now ERROR X5
    public void Lowered_NestedShapingArg_IsError()
    {
        var (lowered, _) = Lower(
            $"<StackPanel {Ns} x:Class=\"GenApp.Sv6\"><Border Height=\"{{Binding Width, ConverterParameter={{x:Self}}}}\"/></StackPanel>",
            "namespace GenApp { public partial class Sv6 : Cursorial.UI.Controls.StackPanel { public Sv6() => InitializeComponent(); } }");

        Assert.Contains("ERROR X5", lowered);
        Assert.Contains("ConverterParameter", lowered);
    }

    [Fact] // review: {x:Self} on an INIT-ONLY member fails with an ACCURATE lane-limitation error (the loader
    // resolves it via reflection; the old error claimed a "target-less position", which was factually wrong)
    public void Lowered_InitOnlyMember_IsAccurateError()
    {
        var host = @"
namespace GenApp
{
    public class FrozenHost : Cursorial.UI.Controls.Control { public object? Frozen { get; init; } }
}";
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.Sv7\"><vm:FrozenHost Frozen=\"{{x:Self}}\"/></StackPanel>";
        var view = "namespace GenApp { public partial class Sv7 : Cursorial.UI.Controls.StackPanel { public Sv7() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("SelfLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(host), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("ERROR X5", lowered);
        Assert.Contains("inside its own initializer", lowered);   // the accurate reason
        Assert.DoesNotContain("this position has none", lowered); // NOT the wrong-position message
    }

    [Fact] // review: {z:Self} with an intrinsics-BOUND prefix is the intrinsic (ns-gated, not spelling-gated)
    public void Lowered_IntrinsicsBoundPrefixAlias_ResolvesAsSelf()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:z=\"https://cursorial.dev/xaml\" x:Class=\"GenApp.Sv8\">" +
            "<Border Height=\"{Binding Width, Source={z:Self}}\"/></StackPanel>";
        var view = "namespace GenApp { public partial class Sv8 : Cursorial.UI.Controls.StackPanel { public Sv8() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("SelfLowHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.Sv8")!)!;
        var border = (Border)root.Children[0];
        var binding = (Binding)BindingOperations.GetBindingExpression(border, UIElement.HeightProperty)!.ParentBinding!;
        Assert.Same(border, binding.Source);
    }

    [Fact] // review: a LEGITIMATE custom extension named Self ({vm:Self}, non-intrinsics ns) activates as a
    // custom extension — never intercepted by the intrinsic's ns-gated matching (the loader agrees, Section20)
    public void Lowered_CustomSelfExtension_IsNotIntercepted()
    {
        var ext = @"
namespace GenApp
{
    public sealed class SelfExtension : Cursorial.UI.Xaml.MarkupExtension
    {
        public static readonly object Sentinel = new();
        public override object? ProvideValue(System.IServiceProvider sp) => Sentinel;
    }
}";
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.Sv9\"><Border Height=\"{{Binding Width, Source={{vm:Self}}}}\"/></StackPanel>";
        var view = "namespace GenApp { public partial class Sv9 : Cursorial.UI.Controls.StackPanel { public Sv9() => InitializeComponent(); } }";
        var compilation = GeneratorHarness.ReferencedCompilation("SelfLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(ext), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("ERROR X5", lowered);
        Assert.Contains("SelfExtension", lowered); // the custom extension is activated, not the intrinsic anchor

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.Sv9")!)!;
        var border = (Border)root.Children[0];
        var binding = (Binding)BindingOperations.GetBindingExpression(border, UIElement.HeightProperty)!.ParentBinding!;
        var sentinel = assembly.GetType("GenApp.SelfExtension")!.GetField("Sentinel")!.GetValue(null);
        Assert.Same(sentinel, binding.Source); // the extension's value, NOT the Border
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
