using Cursorial.Animation;
using Cursorial.UI;
using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// Animation markup in the X4 generator (the W1 sweep fixes, lane parity with
/// <c>Section22_AnimationMarkup</c>): storyboard content lowering runs end-to-end with easing / repeat /
/// Optional attribute values converting through the SAME runtime ladder rows the loader uses
/// (<c>__ConvertXamlValue</c> → <c>XamlConverters.For</c>; the baked BCL <c>OptionalConverter</c> path is
/// bypassed for closed <c>Optional&lt;T&gt;</c> so ladder-only inner grammars like <c>Color</c> work), and
/// the parameterless-ctor / abstract-type fence records a CURG3002-grade ERROR where the loader Fatals
/// CUR3001 — the identical document must fail the build, not emit uncompilable or crashing code.
/// </summary>
public class AnimationLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" xmlns:vm=\"using:GenApp\"";

    private static (string Lowered, CSharpCompilation Compilation) Lower(string xaml, string view, string? host = null)
    {
        var compilation = GeneratorHarness.ReferencedCompilation("AnimLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(view));
        if (host is not null)
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(host));
        return (GeneratorHarness.LowerView(compilation, xaml), compilation);
    }

    [Fact] // GA1: the full attribute surface lowers AND runs — easing/repeat/Optional through the runtime ladder
    public void Lowered_StoryboardTrackAttributes_RunEndToEnd()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.Anim1\"><StackPanel.Resources>" +
            "<Storyboard x:Key=\"pulse\">" +
              "<DoubleTrack TargetPath=\"Opacity\" From=\"0.2\" To=\"1\" Duration=\"0:0:0.3\" Easing=\"QuadInOut\" Repeat=\"Forever\"/>" +
            "</Storyboard>" +
            "</StackPanel.Resources></StackPanel>";
        var view = "namespace GenApp { public partial class Anim1 : Cursorial.UI.Controls.StackPanel { public Anim1() => InitializeComponent(); } }";

        var (lowered, compilation) = Lower(xaml, view);
        Assert.DoesNotContain("ERROR X5", lowered);
        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("OptionalConverter", lowered); // Optional routes through the ladder, not the baked BCL converter

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.Anim1")!)!;
        var storyboard = Assert.IsType<Storyboard>(root.Resources!["pulse"]);
        var track = Assert.IsType<DoubleTrack>(Assert.Single(storyboard.Children));

        Assert.Equal(0.2, track.From.Value);
        Assert.Equal(1.0, track.To.Value);
        Assert.Equal(TimeSpan.FromSeconds(0.3), track.Duration);
        Assert.Same(Easings.QuadInOut, track.Easing);   // the catalog singleton — identical to the loader lane
        Assert.True(track.Repeat.IsForever);
    }

    [Fact] // GA2: Optional<Color> converts its INNER type through the ladder in the lowered lane too (#RRGGBBAA)
    public void Lowered_OptionalColor_ConvertsThroughLadder()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.Anim2\"><StackPanel.Resources>" +
            "<Storyboard x:Key=\"tint\">" +
              "<ColorTrack TargetPath=\"Background\" From=\"#FF000080\"/>" +
            "</Storyboard>" +
            "</StackPanel.Resources></StackPanel>";
        var view = "namespace GenApp { public partial class Anim2 : Cursorial.UI.Controls.StackPanel { public Anim2() => InitializeComponent(); } }";

        var (lowered, compilation) = Lower(xaml, view);
        Assert.DoesNotContain("ERROR X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.Anim2")!)!;
        var track = Assert.IsType<ColorTrack>(Assert.Single(((Storyboard)root.Resources!["tint"]!).Children));

        Assert.True(track.From.HasValue);
        var from = track.From.Value;
        Assert.Equal((255, 0, 0, 128), (from.Red, from.Green, from.Blue, from.Alpha));
    }

    [Fact] // GA3: a parameterless-ctor-less element type FAILS the build (CURG3002) — the loader Fatals CUR3001;
    // pre-fence this emitted `new NoCtorWidget()` and died as an unpositioned CS7036 in generated code
    public void Lowered_CtorlessElement_IsError()
    {
        var host = @"
namespace GenApp
{
    public class NoCtorWidget : Cursorial.UI.Controls.Control
    {
        public NoCtorWidget(int seed) { }
    }
}";
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.Anim3\"><vm:NoCtorWidget/></StackPanel>";
        var view = "namespace GenApp { public partial class Anim3 : Cursorial.UI.Controls.StackPanel { public Anim3() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("AnimLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(host), CSharpSyntaxTree.ParseText(view));
        var document = Cursorial.UI.Xaml.XamlFrontend.Parse(xaml, new Cursorial.UI.Xaml.XamlParseOptions
        {
            MetadataProvider = new Cursorial.UI.Xaml.Generator.RoslynXamlMetadata(compilation),
            DiagnosticMode = Cursorial.UI.Xaml.XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });
        var result = Cursorial.UI.Xaml.Generator.LoweringEmitter.Emit(
            document, "MyView.xaml", "MyView.xaml", new Cursorial.UI.Xaml.Generator.XamlSymbolResolver(compilation))
            ?? throw new InvalidOperationException("no lowering emitted");

        Assert.Contains(result.Errors, e => e.Message.Contains("parameterless"));
        var full = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(result.Source));
        Assert.Empty(full.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)); // the typed placeholder keeps the emitted C# compiling
    }

    [Fact] // GA4: an abstract element type equally FAILS the build (the loader cannot activate it either)
    public void Lowered_AbstractElement_IsError()
    {
        var host = @"
namespace GenApp
{
    public abstract class AbstractWidget : Cursorial.UI.Controls.Control
    {
    }
}";
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.Anim4\"><vm:AbstractWidget/></StackPanel>";
        var view = "namespace GenApp { public partial class Anim4 : Cursorial.UI.Controls.StackPanel { public Anim4() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("AnimLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(host), CSharpSyntaxTree.ParseText(view));
        var document = Cursorial.UI.Xaml.XamlFrontend.Parse(xaml, new Cursorial.UI.Xaml.XamlParseOptions
        {
            MetadataProvider = new Cursorial.UI.Xaml.Generator.RoslynXamlMetadata(compilation),
            DiagnosticMode = Cursorial.UI.Xaml.XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });
        var result = Cursorial.UI.Xaml.Generator.LoweringEmitter.Emit(
            document, "MyView.xaml", "MyView.xaml", new Cursorial.UI.Xaml.Generator.XamlSymbolResolver(compilation))
            ?? throw new InvalidOperationException("no lowering emitted");

        Assert.Contains(result.Errors, e => e.Message.Contains("abstract"));
        var full = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(result.Source));
        Assert.Empty(full.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact] // GA6 (audit): a parameterless-ctor-less CUSTOM MARKUP EXTENSION fails the build (the
    // element-instantiation fence's ME twin) — pre-fence it emitted `new NoCtorExtension()` and died as an
    // unpositioned CS7036 in generated code while the loader Fatals with a positioned diagnostic
    public void Lowered_CtorlessCustomExtension_IsError()
    {
        var host = @"
namespace GenApp
{
    public class NoCtorExtension : Cursorial.UI.Xaml.MarkupExtension
    {
        public NoCtorExtension(int seed) { }
        public override object? ProvideValue(System.IServiceProvider services) => null;
    }
}";
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.Anim6\"><Border Background=\"{{vm:NoCtor}}\"/></StackPanel>";
        var view = "namespace GenApp { public partial class Anim6 : Cursorial.UI.Controls.StackPanel { public Anim6() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("AnimLowHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(host), CSharpSyntaxTree.ParseText(view));
        var document = Cursorial.UI.Xaml.XamlFrontend.Parse(xaml, new Cursorial.UI.Xaml.XamlParseOptions
        {
            MetadataProvider = new Cursorial.UI.Xaml.Generator.RoslynXamlMetadata(compilation),
            DiagnosticMode = Cursorial.UI.Xaml.XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });
        var result = Cursorial.UI.Xaml.Generator.LoweringEmitter.Emit(
            document, "MyView.xaml", "MyView.xaml", new Cursorial.UI.Xaml.Generator.XamlSymbolResolver(compilation))
            ?? throw new InvalidOperationException("no lowering emitted");

        Assert.Contains(result.Errors, e => e.Message.Contains("parameterless"));
    }

    [Fact] // GA5: Storyboard implicit content lowers through the shared frontend's content-property
    // classification — parity with the loader's XA1
    public void Lowered_StoryboardImplicitContent_RunsEndToEnd()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.Anim5\"><StackPanel.Resources>" +
            "<Storyboard x:Key=\"s\"><DoubleTrack TargetPath=\"Opacity\" To=\"1\"/><Int32Track TargetPath=\"Row\" To=\"3\"/></Storyboard>" +
            "</StackPanel.Resources></StackPanel>";
        var view = "namespace GenApp { public partial class Anim5 : Cursorial.UI.Controls.StackPanel { public Anim5() => InitializeComponent(); } }";

        var (lowered, compilation) = Lower(xaml, view);
        Assert.DoesNotContain("ERROR X5", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var root = (StackPanel)Activator.CreateInstance(assembly.GetType("GenApp.Anim5")!)!;
        var storyboard = Assert.IsType<Storyboard>(root.Resources!["s"]);
        Assert.Equal(2, storyboard.Children.Count);
        Assert.IsType<DoubleTrack>(storyboard.Children[0]);
        Assert.IsType<Int32Track>(storyboard.Children[1]);
    }
}
