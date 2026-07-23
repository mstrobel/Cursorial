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

    [Fact] // the generated half declares the base type in strict mode too — same one-place-edit
    public void Strict_EmitsRootElementAsBaseType() // contract as the X4 code-behind path
    {
        // The template preamble rides along: the strict pipeline must skip the ignorable design
        // attributes rather than lowering them.
        var xaml = $"<StackPanel {Ns} xmlns:d=\"https://cursorial.dev/xaml/design\"" +
                   " xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"" +
                   " mc:Ignorable=\"d\" d:DesignWidth=\"40\"" +
                   " x:Class=\"GenApp.BaseView\"><Button x:Name=\"Ok\"/></StackPanel>";
        // The code-behind deliberately declares NO base list — the generated half owns it.
        const string codeBehind = @"
namespace GenApp
{
    public partial class BaseView { public BaseView() => InitializeComponent(); }
}";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, loweringFull: true, ("BaseView.xaml", xaml));
        Assert.Empty(diagnostics);
        Assert.Contains("partial class BaseView : global::Cursorial.UI.Controls.StackPanel", GeneratedView(compilation, "BaseView"));
    }

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

    [Fact] // a member the lowering can't emit (here a Converter-bearing binding) surfaces a CURG3001 warning — never silent
    public void LoweringOptIn_UnsupportedFeature_EmitsCurg3001Warning()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.GapView\">" +
            "<ComboBox x:Name=\"Ok\" Text=\"{DynamicResource TextKey}\"/>" + // {DynamicResource} on a DIRECT property — non-bindable, fenced (loader Fatals too)
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class GapView : StackPanel { public GapView() => InitializeComponent(); } }";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, loweringFull: true, ("GapView.xaml", xaml));

        // The dropped member is visible as a CURG3001 build warning at the .xaml (not silently lost).
        var gap = Assert.Single(diagnostics, d => d.Id == "CURG3001");
        Assert.Equal(DiagnosticSeverity.Warning, gap.Severity);
        Assert.Contains("DynamicResource", gap.GetMessage());

        // The rest of the view still lowered (the TODO is a comment) and compiles.
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("// TODO X5", GeneratedView(compilation, "GapView"));
    }

    [Fact] // B3 — an x:DataType {Binding} that stays reflective (here an indexer path) emits a CURG2002 INFO naming
           // why; a compilable single-hop sibling compiles and gets no info; both still work (no CURG3001 gap).
    public void LoweringOptIn_ReflectiveFallback_EmitsCurg2002Info()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:GenApp\" x:Class=\"GenApp.InfoView\" x:DataType=\"t:InfoVm\">" +
            "<Button x:Name=\"Indexed\" Content=\"{Binding Tags[0]}\"/>" + // indexer ⇒ reflective ⇒ CURG2002
            "<Button x:Name=\"Flat\" Content=\"{Binding Title}\"/>" +      // single-hop ⇒ compiled ⇒ no info
            "</StackPanel>";

        const string codeBehind = @"
using System.Collections.Generic;
using Cursorial.UI.Controls;
namespace GenApp
{
    public sealed class InfoVm { public List<string> Tags { get; } = new(); public string Title { get; set; } = string.Empty; }
    public partial class InfoView : StackPanel { public InfoView() => InitializeComponent(); }
}";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, loweringFull: true, ("InfoView.xaml", xaml));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // Exactly one CURG2002 Info (the indexer Indexed button), naming the reason.
        var info = Assert.Single(diagnostics, d => d.Id == "CURG2002");
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Contains("stays reflective", info.GetMessage());
        Assert.Contains("indexer", info.GetMessage());

        // The single-hop binding compiled; the indexer one still works reflectively (no CURG3001 dropped-member gap).
        var view = GeneratedView(compilation, "InfoView");
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<", view);        // Flat → compiled
        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"Tags[0]\")", view);    // Indexed → reflective, working
        Assert.DoesNotContain(diagnostics, d => d.Id == "CURG3001");
    }

    [Fact] // B3/P1D — a multi-hop compiled binding survives a NULL intermediate (returns default(leaf), no NRE) and
           // a TwoWay multi-hop write-back through a reference chain reaches the leaf.
    public void LoweringOptIn_MultiHop_NullIntermediateSafe_AndTwoWayWriteBack()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:GenApp\" x:Class=\"GenApp.DeepView\" x:DataType=\"t:RootVm\">" +
            "<TextBlock x:Name=\"Edit\" Text=\"{Binding Child.Name, Mode=TwoWay}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp
{
    public sealed class ChildVm { public string Name { get; set; } = string.Empty; }
    public sealed class RootVm { public ChildVm? Child { get; set; } }
    public partial class DeepView : StackPanel { public DeepView() => InitializeComponent(); }
}";

        var (compilation, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, loweringFull: true, ("DeepView.xaml", xaml));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // A TwoWay multi-hop binding emits a NULL-GUARDED reverse setter through the reference chain (a broken
        // chain is a no-op write, never an NRE).
        var view = GeneratedView(compilation, "DeepView");
        Assert.Contains("if ((__s.Child) is { } __o) __o.Name = __v", view);

        var assembly = GeneratorHarness.EmitAndLoad(compilation);
        var instance = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.DeepView")!)!;
        var edit = Assert.IsType<TextBlock>(instance.Children[0]);

        var rootType = assembly.GetType("GenApp.RootVm")!;
        var root = System.Activator.CreateInstance(rootType)!; // Child is null

        // A null intermediate (Child == null) yields default(string) — no NRE (the pinned compiled-multi-hop
        // null semantics, B188).
        instance.DataContext = root;
        Assert.True(string.IsNullOrEmpty(edit.Text));

        // With Child set, the null-safe chain resolves the leaf live.
        var childType = assembly.GetType("GenApp.ChildVm")!;
        var child = System.Activator.CreateInstance(childType)!;
        childType.GetProperty("Name")!.SetValue(child, "Ada");
        rootType.GetProperty("Child")!.SetValue(root, child);
        instance.DataContext = null;
        instance.DataContext = root; // re-resolve with Child present
        Assert.Equal("Ada", edit.Text);
    }

    [Fact] // P1-REVIEW Fix E — a STATIC member path emits NO false "It works" CURG2002 (a static won't resolve
           // through an instance DataContext, so the reflective binding is inert — not a "works-but-reflective" case).
    public void LoweringOptIn_StaticMemberPath_EmitsNoCurg2002()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:GenApp\" x:Class=\"GenApp.StaticBindView\" x:DataType=\"t:SVm\">" +
            "<Button Content=\"{Binding Shared}\"/>" + // Shared is a STATIC member
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp
{
    public sealed class SVm { public static string Shared { get; set; } = string.Empty; }
    public partial class StaticBindView : StackPanel { public StaticBindView() => InitializeComponent(); }
}";

        var (_, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, loweringFull: true, ("StaticBindView.xaml", xaml));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, d => d.Id == "CURG2002"); // no false "stays reflective … it works"
    }

    [Fact] // P1-REVIEW Fix E — a NOT-FOUND member path gets CURG2001 (the path validator) but NOT a contradictory CURG2002
    public void LoweringOptIn_NotFoundPath_EmitsCurg2001NotCurg2002()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:GenApp\" x:Class=\"GenApp.TypoView\" x:DataType=\"t:TVm\">" +
            "<Button Content=\"{Binding Titel}\"/>" + // typo — the VM has Title
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp
{
    public sealed class TVm { public string Title { get; set; } = string.Empty; }
    public partial class TypoView : StackPanel { public TypoView() => InitializeComponent(); }
}";

        var (_, diagnostics) = GeneratorHarness.RunWithCodeBehind(codeBehind, loweringFull: true, ("TypoView.xaml", xaml));
        Assert.Contains(diagnostics, d => d.Id == "CURG2001");       // the path validator flags the typo
        Assert.DoesNotContain(diagnostics, d => d.Id == "CURG2002"); // and no contradictory "it works" info
    }
}
