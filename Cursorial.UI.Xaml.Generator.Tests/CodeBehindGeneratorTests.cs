using System.Linq;

using Microsoft.CodeAnalysis;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X4.6 — the code-behind generator. For an <c>x:Class</c> document the generator emits a partial class
/// with a typed field per document-scope <c>x:Name</c> and an <c>InitializeComponent()</c> that loads the
/// XAML through the runtime loader and assigns the fields from the name scope. The end-to-end test compiles
/// a real code-behind against the generated partial, instantiates it, and asserts the typed fields are
/// populated — proving the symbol-backed parse → field-type resolution → runtime wiring chain.
/// </summary>
public class CodeBehindGeneratorTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    // The per-file output (code-behind / marker), excluding the per-compilation metadata provider (WS-X4.5).
    private static string OnlySource((string FileName, string Xaml) file)
        => GeneratorHarness.Run(file).Results
            .SelectMany(r => r.GeneratedSources)
            .Where(s => !s.HintName.Contains("__GeneratedXamlMetadata"))
            .Single().SourceText.ToString();

    [Fact]
    public void EmitsTypedFields_ForNamedElements()
    {
        var src = OnlySource(("View.xaml",
            $"<StackPanel {Ns} x:Class=\"App.View\"><Button x:Name=\"Ok\"/><Border x:Name=\"Frame\"/></StackPanel>"));

        Assert.Contains("namespace App", src);
        Assert.Contains("partial class View", src);
        Assert.Contains("internal global::Cursorial.UI.Controls.Button Ok = default!;", src);
        Assert.Contains("internal global::Cursorial.UI.Controls.Border Frame = default!;", src);
        Assert.Contains("void InitializeComponent()", src);
        Assert.Contains("LoadComponent(this", src);
        Assert.Contains("this.Ok = (global::Cursorial.UI.Controls.Button)", src);
    }

    [Fact] // a same-assembly view referenced via clr-namespace: instantiates at RUNTIME through the
    public void ClrNamespaceElements_ResolveThroughTheGeneratedProvider() // generated provider (the Cursorial.Samples bug)
    {
        var childXaml = $"<StackPanel {Ns} x:Class=\"GenApp.Views.ChildView\"><TextBlock Text=\"child\"/></StackPanel>";
        var mainXaml = $"<StackPanel {Ns} xmlns:v=\"clr-namespace:GenApp.Views;assembly=GeneratorTestAssembly\"" +
                       " x:Class=\"GenApp.Views.MainView\"><v:ChildView/></StackPanel>";
        const string codeBehind = @"
namespace GenApp.Views
{
    public partial class ChildView { public ChildView() => InitializeComponent(); }
    public partial class MainView { public MainView() => InitializeComponent(); }
}";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(
            codeBehind, ("Views/ChildView.xaml", childXaml), ("Views/MainView.xaml", mainXaml));
        Assert.DoesNotContain(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        // The compile resolved v:ChildView through the Roslyn resolver; the RUNTIME parse must
        // resolve it through the emitted provider (it used to answer only the default namespace).
        var assembly = GeneratorHarness.EmitAndLoad(compilation);
        var main = (Cursorial.UI.Controls.StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.Views.MainView")!)!;
        var child = Assert.IsAssignableFrom<Cursorial.UI.Controls.StackPanel>(main.Children[0]);
        Assert.Equal("GenApp.Views.ChildView", child.GetType().FullName);
    }

    [Fact] // a runtime-loaded Style authored with TargetType resolves under the generated provider
    public void StyleTargetType_ResolvesThroughTheGeneratedProvider() // (synthetic-member parity with reflection)
    {
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.StyledView\">" +
                   "<StackPanel.Resources><ResourceDictionary>" +
                   "<Style x:Key=\"S\" Selector=\"Button\" TargetType=\"Button\"><Setter Property=\"Content\" Value=\"styled\"/></Style>" +
                   "</ResourceDictionary></StackPanel.Resources>" +
                   "<Button/></StackPanel>";
        const string codeBehind = @"
namespace GenApp
{
    public partial class StyledView { public StyledView() => InitializeComponent(); }
}";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, ("StyledView.xaml", xaml));
        Assert.DoesNotContain(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

        var assembly = GeneratorHarness.EmitAndLoad(compilation);
        var view = (Cursorial.UI.Controls.StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.StyledView")!)!;
        Assert.IsType<Cursorial.UI.Style>(view.Resources["S"]);
    }

    [Fact] // the GENERATED half declares the base type from the root element — the hand-written
    public void EmitsRootElementAsBaseType() // half needs no base list (one-place root edits)
    {
        var src = OnlySource(("View.xaml", $"<StackPanel {Ns} x:Class=\"App.View\"><Button/></StackPanel>"));
        Assert.Contains("partial class View : global::Cursorial.UI.Controls.StackPanel", src);
    }

    [Fact] // the baked document identity is the machine-independent embedded-resource URI, never a build path
    public void ParsesWithCursorialSourceUri()
    {
        var src = OnlySource(("View.xaml", $"<StackPanel {Ns} x:Class=\"App.View\"><Button/></StackPanel>"));
        Assert.Contains("cursorial://GeneratorTestAssembly/View.xaml", src);
    }

    [Fact] // a class-less document gets the marker, not a code-behind partial
    public void NoXClass_EmitsMarker_NotPartial()
    {
        var src = OnlySource(("Plain.xaml", $"<StackPanel {Ns}><Button x:Name=\"Ok\"/></StackPanel>"));
        Assert.DoesNotContain("partial class", src);
        Assert.Contains("// source:", src);
    }

    [Fact] // an x:Name inside a template (deferred content) is a TEMPLATE-scope part — never a code-behind field
    public void ExcludesTemplateScopeNames()
    {
        var src = OnlySource(("View.xaml",
            $"<StackPanel {Ns} x:Class=\"App.View\">" +
            "<Button x:Name=\"Ok\"><Button.Template><ControlTemplate><Border x:Name=\"Part\"/></ControlTemplate></Button.Template></Button>" +
            "</StackPanel>"));

        Assert.Contains("Button Ok = default!;", src);      // document scope → field
        Assert.DoesNotContain("Part = default!", src);      // template scope → NOT a field
        Assert.DoesNotContain("this.Part =", src);          // … nor assigned (the name only appears in the inlined XAML)
    }

    [Fact] // the full chain: generate → compile a real code-behind → instantiate → typed fields populated
    public void EndToEnd_InitializeComponent_PopulatesTypedFields()
    {
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp
{
    public partial class MyView : StackPanel
    {
        public MyView() => InitializeComponent();
    }
}";
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.MyView\">" +
            "<Button x:Name=\"Ok\" Content=\"OK\"/>" +
            "<Border x:Name=\"Frame\"/>" +
            "</StackPanel>";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, ("MyView.xaml", xaml));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var assembly = GeneratorHarness.EmitAndLoad(compilation);
        var viewType = assembly.GetType("TestApp.MyView")!;
        var view = System.Activator.CreateInstance(viewType)!; // ctor → InitializeComponent → LoadComponent + field wiring

        // The generated fields are `internal` — bind NonPublic.
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        var ok = viewType.GetField("Ok", flags)!.GetValue(view);
        var frame = viewType.GetField("Frame", flags)!.GetValue(view);

        Assert.NotNull(ok);
        Assert.Equal("Button", ok!.GetType().Name);
        Assert.NotNull(frame);
        Assert.Equal("Border", frame!.GetType().Name);
    }
}
