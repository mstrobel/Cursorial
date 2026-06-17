using System.Linq;

using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5.5 — the full-lowering opt-in, end-to-end through the real <c>XamlSourceGenerator</c> driver.
/// <c>build_property.CursorialXamlLowering=full</c> switches each <c>x:Class</c> document from the X4.6
/// runtime-loader code-behind to the X5 straight-line lowering (reflection-free). These tests prove the
/// switch fires (lowered construction, no <c>LoadComponent</c>), the lowered tree compiles + runs + binds
/// live, the default (no opt-in) still emits the loader code-behind, and a member the lowering can't emit
/// surfaces a <c>CURG3001</c> build warning (never a silent drop).
/// </summary>
public class LoweringGeneratorTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    // The generated per-file source for an x:Class view (the auto-generated partial; not the metadata provider).
    private static string GeneratedView(Compilation updated, string className)
        => updated.SyntaxTrees
                  .Select(t => t.ToString())
                  .Single(s => s.Contains("auto-generated") && s.Contains("partial class " + className));

    [Fact] // the opt-in lowers to straight-line C#, the tree compiles + instantiates, and a binding resolves live
    public void LoweringOptIn_GeneratesStraightLineView_BindsLive()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:GenApp\" x:Class=\"GenApp.MainView\" x:DataType=\"t:GenVm\">" +
            "<Button x:Name=\"Ok\" Content=\"{Binding Caption}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using System.ComponentModel;
using Cursorial.UI.Controls;
namespace GenApp
{
    public sealed class GenVm : INotifyPropertyChanged
    {
        private string _caption = string.Empty;
        public string Caption
        {
            get => _caption;
            set { _caption = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public partial class MainView : StackPanel { public MainView() => InitializeComponent(); }
}";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, loweringFull: true, ("MainView.xaml", xaml));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // The generated partial is the X5 lowering (straight-line construction + the compiled binding), NOT the
        // X4.6 loader code-behind (no LoadComponent call).
        var view = GeneratedView(compilation, "MainView");
        Assert.Contains("new global::Cursorial.UI.Controls.Button()", view);
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::GenApp.GenVm, string>", view);
        Assert.DoesNotContain("LoadComponent", view);

        // It compiles, instantiates, and the lowered compiled binding resolves on a DataContext set.
        var assembly = GeneratorHarness.EmitAndLoad(compilation);
        var viewType = assembly.GetType("GenApp.MainView")!;
        var instance = (StackPanel)System.Activator.CreateInstance(viewType)!;
        var button = Assert.IsType<Button>(instance.Children[0]);

        var vmType = assembly.GetType("GenApp.GenVm")!;
        var vm = System.Activator.CreateInstance(vmType)!;
        var caption = vmType.GetProperty("Caption")!;
        caption.SetValue(vm, "Hello");

        instance.DataContext = vm;
        Assert.Equal("Hello", button.Content);

        caption.SetValue(vm, "Goodbye");
        Assert.Equal("Goodbye", button.Content);
    }

    [Fact] // without the opt-in the same document keeps the loader-backed code-behind (LoadComponent), not lowering
    public void Default_NoOptIn_EmitsLoaderCodeBehind()
    {
        var xaml = $"<StackPanel {Ns} x:Class=\"GenApp.PlainView\"><Button x:Name=\"Ok\" Content=\"OK\"/></StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class PlainView : StackPanel { public PlainView() => InitializeComponent(); } }";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, ("PlainView.xaml", xaml));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var view = GeneratedView(compilation, "PlainView");
        Assert.Contains("LoadComponent", view);                                   // the loader code-behind path
        Assert.DoesNotContain("new global::Cursorial.UI.Controls.Button()", view); // not the lowering
    }

    [Fact] // a member the lowering can't emit (here a {StaticResource}) surfaces a CURG3001 warning — never silent
    public void LoweringOptIn_UnsupportedFeature_EmitsCurg3001Warning()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.GapView\">" +
            "<Button x:Name=\"Ok\" Foreground=\"{StaticResource Accent}\"/>" + // resource lowering not built yet
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class GapView : StackPanel { public GapView() => InitializeComponent(); } }";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, loweringFull: true, ("GapView.xaml", xaml));

        // The dropped member is visible as a CURG3001 build warning at the .xaml (not silently lost).
        var gap = Assert.Single(diagnostics, d => d.Id == "CURG3001");
        Assert.Equal(DiagnosticSeverity.Warning, gap.Severity);
        Assert.Contains("StaticResource", gap.GetMessage());

        // The rest of the view still lowered (the TODO is a comment) and compiles.
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("// TODO X5", GeneratedView(compilation, "GapView"));
    }
}
