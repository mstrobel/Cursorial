using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

public class ScratchRdBridgeTests
{
    [Fact]
    public void Scratch_RdLowering_BridgeMember()
    {
        const string host = @"
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
        var xaml =
            "<ResourceDictionary xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\">\n" +
            "  <vm:BridgeWidget x:Key=\"W\" Payload=\"3.5\"/>\n" +
            "</ResourceDictionary>";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(host, loweringFull: true, ("Palette.xaml", xaml));

        foreach (var d in diagnostics)
            Console.WriteLine($"DIAG: {d.Id} {d.GetMessage()}");

        var generated = compilation.SyntaxTrees
            .Where(t => t.FilePath.Contains("Palette"))
            .Select(t => t.ToString())
            .ToList();

        foreach (var g in generated)
            Console.WriteLine("===== GENERATED =====\n" + g);

        Assert.NotEmpty(generated);
        var src = string.Join("\n", generated);
        Console.WriteLine("HAS __ConvertXamlValue: " + src.Contains("__ConvertXamlValue"));
        Console.WriteLine("HAS BridgeConverterForType: " + src.Contains("BridgeConverterForType"));
    }
}
