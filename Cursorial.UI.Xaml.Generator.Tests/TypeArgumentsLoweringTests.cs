using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// W3 <c>x:TypeArguments</c> in the X4 generator (lane parity with <c>Section26_TypeArguments</c>): the
/// shared frontend resolves the element CLOSED through <c>RoslynXamlMetadata</c>'s
/// <c>IXamlGenericTypeProvider</c> (symbol <c>Construct()</c>), members substitute at build time, and
/// the emitter renders the closed <c>new T&lt;args&gt;()</c> form — generic instantiation with ZERO
/// runtime type construction, AOT-clean by construction.
/// </summary>
public class TypeArgumentsLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\"";

    private const string Host = @"
namespace GenApp
{
    public sealed class GenericWidget<T> : Cursorial.UI.Controls.Control
    {
        public T? Payload { get; set; }
    }
}";

    [Fact] // GT1: a closed generic element lowers to `new GenericWidget<double>()` and RUNS with the
    // substituted member converted through the ladder
    public void Lowered_ClosedElement_RunsWithSubstitutedMember()
    {
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.Gen1\"><vm:GenericWidget x:TypeArguments=\"x:Double\" Payload=\"0.5\"/></StackPanel>";
        var view = "namespace GenApp { public partial class Gen1 : Cursorial.UI.Controls.StackPanel { public Gen1() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("TypeArgsHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Host), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("ERROR X5", lowered);
        Assert.Contains("GenericWidget<double>", lowered); // the CLOSED emission — no runtime construction

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.Gen1")!)!;
        var widget = root.Children[0];

        Assert.Equal("GenericWidget`1", widget.GetType().Name);
        Assert.Equal(typeof(double), widget.GetType().GetGenericArguments()[0]);
        Assert.Equal(0.5, widget.GetType().GetProperty("Payload")!.GetValue(widget)); // substituted + converted
    }

    [Fact] // GT2: an unresolvable closing is the SAME positioned parse diagnostic as the loader lane
    public void Lowered_UnresolvableClosing_IsParseDiagnostic()
    {
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.Gen2\"><vm:GenericWidget x:TypeArguments=\"x:NoSuchType\"/></StackPanel>";
        var view = "namespace GenApp { public partial class Gen2 : Cursorial.UI.Controls.StackPanel { public Gen2() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("TypeArgsHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Host), CSharpSyntaxTree.ParseText(view));
        var document = Cursorial.UI.Xaml.XamlFrontend.Parse(xaml, new Cursorial.UI.Xaml.XamlParseOptions
        {
            MetadataProvider = new Cursorial.UI.Xaml.Generator.RoslynXamlMetadata(compilation),
            DiagnosticMode = Cursorial.UI.Xaml.XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });

        Assert.Contains(document.Diagnostics, d =>
            d.Code == "CUR2002" && d.Message.Contains("Cannot close 'GenericWidget'") && d.Line > 0 && d.Column > 0);
    }

    [Fact] // GT3: nested closing lowers — GenericWidget<List<string>> via scg:List(x:String)
    public void Lowered_NestedClosing_Runs()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:vm=\"using:GenApp\" xmlns:scg=\"using:System.Collections.Generic\" x:Class=\"GenApp.Gen3\">" +
              "<vm:GenericWidget x:TypeArguments=\"scg:List(x:String)\"/>" +
            "</StackPanel>";
        var view = "namespace GenApp { public partial class Gen3 : Cursorial.UI.Controls.StackPanel { public Gen3() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("TypeArgsHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(Host), CSharpSyntaxTree.ParseText(view));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.Gen3")!)!;
        Assert.Equal(typeof(List<string>), root.Children[0].GetType().GetGenericArguments()[0]);
    }
}
