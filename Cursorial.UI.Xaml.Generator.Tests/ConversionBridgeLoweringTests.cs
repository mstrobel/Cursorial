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
