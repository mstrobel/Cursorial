using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// The CR7 bridge rung in the lowered lane (W2d): the emitted <c>__ConvertXamlValue</c> helper chains
/// <c>XamlConverters.BridgeConverterForType</c> after the pure ladder, so a bridged member type converts
/// identically in generated code — parity by construction (the same runtime probe both lanes consult;
/// typed emission joins the W2e route probe).
/// </summary>
public class ConversionBridgeLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\"";

    [Fact] // GC2 (audit): the NO-x:Class ResourceDictionary-root lane emits the SAME chained helper —
    // pre-fix a second, pre-W2d ladder-only copy served this lane (the one Cursorial.UI.Themes lowers
    // through), so a bridge-needing or BCL-converted entry value crashed at dictionary-build time
    public void Lowered_ResourceDictionaryRoot_BridgeAndBclRungsRun()
    {
        var host = @"
namespace GenApp
{
    public sealed class Wrapped(double value)
    {
        public double Value { get; } = value;
    }

    public sealed class RdWidget : Cursorial.UI.Controls.Control
    {
        public Wrapped? Payload { get; set; }
        public System.Guid Id { get; set; }
    }
}";
        var xaml =
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\">" +
              "<vm:RdWidget x:Key=\"W\" Payload=\"3.5\" Id=\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"/>" +
            "</ResourceDictionary>";

        var compilation = GeneratorHarness.ReferencedCompilation("RdBridgeHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(host));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.Contains("BridgeConverterForType", lowered); // the ONE shared helper body reached this lane

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var dict = (Cursorial.UI.ResourceDictionary)assembly
            .GetType("Cursorial.UI.Xaml.Generated.GeneratedXamlLoaders")!
            .GetMethod("BuildMyView", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .Invoke(null, null)!;

        var widget = dict["W"]!;
        var payload = widget.GetType().GetProperty("Payload")!.GetValue(widget)!;
        Assert.Equal(3.5, (double)payload.GetType().GetProperty("Value")!.GetValue(payload)!); // the CR7 bridge
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
                     (Guid)widget.GetType().GetProperty("Id")!.GetValue(widget)!);            // the BCL rung
    }

    [Fact] // GC1: a ctor-bridged member type lowers and RUNS through the chained helper
    public void Lowered_CtorBridge_RunsEndToEnd()
    {
        var host = @"
namespace GenApp
{
    public sealed class Wrapped(double value)
    {
        public double Value { get; } = value;
    }

    public sealed class BridgeWidget : Cursorial.UI.Controls.Control
    {
        public Wrapped? Payload { get; set; }
    }
}";
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.Bridge1\"><vm:BridgeWidget Payload=\"3.5\"/></StackPanel>";
        var view = "namespace GenApp { public partial class Bridge1 : Cursorial.UI.Controls.StackPanel { public Bridge1() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("BridgeLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(host), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("ERROR X5", lowered);
        Assert.Contains("BridgeConverterForType", lowered); // the chained helper — lane parity with the loader

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.Bridge1")!)!;
        var widget = root.Children[0];
        var payload = widget.GetType().GetProperty("Payload")!.GetValue(widget)!;

        Assert.Equal(3.5, (double)payload.GetType().GetProperty("Value")!.GetValue(payload)!);
    }
}
