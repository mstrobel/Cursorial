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

    private static string OnlySource((string FileName, string Xaml) file)
        => GeneratorHarness.Run(file).Results.SelectMany(r => r.GeneratedSources).Single().SourceText.ToString();

    [Fact]
    public void EmitsTypedFields_ForNamedElements()
    {
        var src = OnlySource(("View.xaml",
            $"<StackPanel {Ns} x:Class=\"App.View\"><Button x:Name=\"Ok\"/><Border x:Name=\"Frame\"/></StackPanel>"));

        Assert.Contains("namespace App", src);
        Assert.Contains("partial class View", src);
        Assert.Contains("internal global::Cursorial.UI.Controls.Button Ok = null!;", src);
        Assert.Contains("internal global::Cursorial.UI.Controls.Border Frame = null!;", src);
        Assert.Contains("void InitializeComponent()", src);
        Assert.Contains("LoadComponent(this", src);
        Assert.Contains("this.Ok = (global::Cursorial.UI.Controls.Button)", src);
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

        Assert.Contains("Button Ok = null!;", src);        // document scope → field
        Assert.DoesNotContain("Part = null!", src);         // template scope → NOT a field
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
