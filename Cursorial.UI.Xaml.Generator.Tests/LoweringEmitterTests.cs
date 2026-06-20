using System.Reflection;

using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Xaml;
using Cursorial.UI.Xaml.Generator;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5 — the full-lowering spine. The lowered <c>InitializeComponent</c> constructs the tree as
/// straight-line C# (no runtime loader / no reflection). These tests emit it, compile it against a real
/// code-behind, instantiate, and assert the resulting tree matches what the runtime loader builds from the
/// same XAML — the lowered/loaded equivalence gate.
/// </summary>
public class LoweringEmitterTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private static string Lower(string xaml, CSharpCompilation compilation) => GeneratorHarness.LowerView(compilation, xaml);

    [Fact]
    public void Lowered_BuildsTree_MatchingRuntimeLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.MyView\">" +
            "<Button x:Name=\"Ok\" Content=\"OK\" Width=\"20\"/>" + // Width=int? exercises X5.1 value lowering
            "<Border x:Name=\"Frame\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class MyView : StackPanel { public MyView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        // The lowered code is reflection-free C#; compile it with the code-behind and instantiate.
        var withLowering = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind),
            CSharpSyntaxTree.ParseText(lowered));
        var assembly = GeneratorHarness.EmitAndLoad(withLowering);
        var viewType = assembly.GetType("TestApp.MyView")!;
        var view = (StackPanel)System.Activator.CreateInstance(viewType)!;

        // The runtime loader builds the reference tree from the same XAML (reflection provider).
        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        // Same shape + values as the loader.
        Assert.Equal(runtime.Children.Count, view.Children.Count);
        Assert.Equal(2, view.Children.Count);

        var loweredOk = Assert.IsType<Button>(view.Children[0]);
        var runtimeOk = Assert.IsType<Button>(runtime.Children[0]);
        Assert.Equal(runtimeOk.Content, loweredOk.Content);
        Assert.Equal("OK", loweredOk.Content);
        // X5.1 — the converted typed value (Width=int?) matches the loader.
        Assert.Equal(runtimeOk.Width, loweredOk.Width);
        Assert.Equal(20, loweredOk.Width);
        Assert.IsType<Border>(view.Children[1]);

        // The typed x:Name fields point at the constructed elements.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        Assert.Same(loweredOk, viewType.GetField("Ok", flags)!.GetValue(view));
        Assert.Same(view.Children[1], viewType.GetField("Frame", flags)!.GetValue(view));
    }

    [Fact] // X5.2 — an attached property (Grid.Row) lowers to SetValue and matches the loader
    public void Lowered_AttachedProperty_MatchesLoader()
    {
        var xaml = $"<Grid {Ns} x:Class=\"TestApp.GridView\"><Button x:Name=\"Cell\" Grid.Row=\"1\"/></Grid>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class GridView : Grid { public GridView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));

        var view = (Grid)System.Activator.CreateInstance(assembly.GetType("TestApp.GridView")!)!;
        var cell = Assert.IsType<Button>(view.Children[0]);
        Assert.Equal(1, Grid.GetRow(cell));

        var runtime = (Grid)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);
        Assert.Equal(Grid.GetRow((Button)runtime.Children[0]), Grid.GetRow(cell));
    }

    [Fact] // X5.2 — an event wires to a code-behind handler (the loader no-ops events; lowering wires them)
    public void Lowered_WiresEventHandler()
    {
        var xaml = $"<Button {Ns} x:Class=\"TestApp.ClickView\" Click=\"OnClick\"/>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp
{
    public partial class ClickView : Button
    {
        public ClickView() => InitializeComponent();
        public bool Clicked;
        private void OnClick(object? sender, ClickEventArgs e) => Clicked = true;
    }
}";
        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.Contains("Click += this.OnClick", lowered); // the wiring is emitted

        // Compiles ⇒ the handler exists and its signature matches the event delegate; instantiating wires it.
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(System.Activator.CreateInstance(assembly.GetType("TestApp.ClickView")!));
    }

    [Fact] // X5.3 — {x:Static} lowers to a resolved static reference, matching the loader's reflected value
    public void Lowered_XStatic_MatchesLoader()
    {
        var xaml = $"<Button {Ns} x:Class=\"TestApp.StaticView\" Foreground=\"{{x:Static Brushes.TrueBlack}}\"/>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class StaticView : Button { public StaticView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.Contains("global::Cursorial.Drawing.Media.Brushes.TrueBlack", lowered); // x:Static resolved

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (Button)System.Activator.CreateInstance(assembly.GetType("TestApp.StaticView")!)!;

        var runtime = (Button)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        Assert.Same(runtime.Foreground, view.Foreground); // both resolved to the Brushes.TrueBlack singleton
        Assert.Same(Cursorial.Drawing.Media.Brushes.TrueBlack, view.Foreground);
    }

    [Fact] // X5.3 — {x:Null} lowers to a null SetValue
    public void Lowered_XNull_SetsNull()
    {
        var xaml = $"<Button {Ns} x:Class=\"TestApp.NullView\" Foreground=\"{{x:Null}}\"/>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class NullView : Button { public NullView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.Contains("ForegroundProperty, null)", lowered); // null SetValue emitted

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (Button)System.Activator.CreateInstance(assembly.GetType("TestApp.NullView")!)!;
        Assert.Null(view.Foreground);
    }

    [Fact] // B3a — an x:DataType-scoped, single-hop {Binding} lowers to a typed CompiledBinding that resolves live
    public void Lowered_Binding_WithDataType_CompilesAndResolvesLive()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.BindView\" x:DataType=\"t:BindVm\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding Caption}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using System.ComponentModel;
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class BindVm : INotifyPropertyChanged
    {
        private string _caption = string.Empty;
        public string Caption
        {
            get => _caption;
            set { _caption = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public partial class BindView : StackPanel { public BindView() => InitializeComponent(); }
}";

        // The view-model must be in the compilation AT LOWERING TIME so x:DataType (t:BindVm) resolves and the
        // compiled lane is taken — add the code-behind syntax tree before lowering.
        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        // The compiled lane was taken — a typed CompiledBinding<TData,TLeaf> installed, no reflective TODO.
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::TestApp.BindVm, string>", lowered);
        Assert.Contains("global::Cursorial.UI.Data.BindingOperations.Install", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("TestApp.BindView")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        var vmType = assembly.GetType("TestApp.BindVm")!;
        var vm = System.Activator.CreateInstance(vmType)!;
        var caption = vmType.GetProperty("Caption")!;
        caption.SetValue(vm, "Hello");

        // The inherited DataContext drives the child TextBlock's compiled binding synchronously (no host pump).
        view.DataContext = vm;
        Assert.Equal("Hello", label.Text);

        // INPC pushes the live update through the compiled chain.
        caption.SetValue(vm, "Goodbye");
        Assert.Equal("Goodbye", label.Text);
    }

    [Fact] // B3/P1D — a multi-hop x:DataType path now COMPILES (a null-safe whole-chain getter + per-hop steps) and
    // resolves live, with INPC on the leaf hop pushing updates through the compiled chain.
    public void Lowered_Binding_MultiHop_CompilesAndBindsLive()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.ReflectiveView\" x:DataType=\"t:OuterVm\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding Inner.Caption, Mode=OneWay}\"/>" + // multi-hop ⇒ compiled (P1D)
            "</StackPanel>";

        const string codeBehind = @"
using System.ComponentModel;
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class InnerVm : INotifyPropertyChanged
    {
        private string _caption = string.Empty;
        public string Caption
        {
            get => _caption;
            set { _caption = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Caption))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public sealed class OuterVm { public InnerVm Inner { get; } = new(); }
    public partial class ReflectiveView : StackPanel { public ReflectiveView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        // The multi-hop binding COMPILED (typed CompiledBinding over the OuterVm root) with a null-safe getter +
        // per-hop steps — not a reflective Binding, not a silent TODO.
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::TestApp.OuterVm, string>", lowered);
        Assert.Contains("static __s => (__s.Inner?.Caption)", lowered);       // null-safe whole-chain getter
        Assert.Contains("new global::Cursorial.UI.Data.CompiledPathStep(\"Inner\"", lowered);  // per-hop step
        Assert.Contains("new global::Cursorial.UI.Data.CompiledPathStep(\"Caption\"", lowered);
        Assert.DoesNotContain("new global::Cursorial.UI.Data.Binding(", lowered); // not the reflective lane
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("TestApp.ReflectiveView")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        var outerType = assembly.GetType("TestApp.OuterVm")!;
        var outer = System.Activator.CreateInstance(outerType)!;
        var inner = outerType.GetProperty("Inner")!.GetValue(outer)!;
        var caption = inner.GetType().GetProperty("Caption")!;
        caption.SetValue(inner, "Nested");

        // The compiled multi-hop chain walks Inner.Caption and resolves live on the DataContext set.
        view.DataContext = outer;
        Assert.Equal("Nested", label.Text);

        // INPC on the leaf hop (InnerVm.Caption) pushes through the compiled chain's per-hop subscription.
        caption.SetValue(inner, "Updated");
        Assert.Equal("Updated", label.Text);
    }

    [Fact] // B3b — a genuinely-uncompilable path (an indexer hop) still gracefully falls back to a faithful
    // reflective `new Binding(...)` (not a silent drop) and resolves live through the engine.
    public void Lowered_Binding_IndexerPath_FallsBackToReflectiveBinding()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.IndexerView\" x:DataType=\"t:ListVm\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding Tags[0], Mode=OneWay}\"/>" + // indexer ⇒ reflective fallback
            "</StackPanel>";

        const string codeBehind = @"
using System.Collections.Generic;
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class ListVm { public List<string> Tags { get; } = new() { ""first"" }; }
    public partial class IndexerView : StackPanel { public IndexerView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        // The reflective fallback was emitted (a faithful new Binding with the carried Mode), NOT compiled, NOT a TODO.
        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"Tags[0]\")", lowered);
        Assert.Contains("Mode = global::Cursorial.UI.Data.BindingMode.OneWay", lowered);
        Assert.DoesNotContain("CompiledBinding", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("TestApp.IndexerView")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        var vmType = assembly.GetType("TestApp.ListVm")!;
        view.DataContext = System.Activator.CreateInstance(vmType)!;
        Assert.Equal("first", label.Text); // the reflective indexer binding resolves Tags[0]
    }

    [Fact] // P1-REVIEW Fix A — a path through a Nullable<T> hop bails to the reflective lane: the compiled step
    // pattern `is global::System.DateTime? __t` would not parse, and `.Value` would NRE. The lowered code COMPILES.
    public void Lowered_Binding_NullableHop_FallsBackToReflective()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.NullableView\" x:DataType=\"t:NVm\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding When.HasValue, Mode=OneWay}\"/>" + // When is DateTime? → Nullable hop
            "</StackPanel>";

        const string codeBehind = @"
using System;
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class NVm { public DateTime? When { get; set; } }
    public partial class NullableView : StackPanel { public NullableView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        // Bailed to the reflective lane (NOT a compiled binding with a broken `is global::System.DateTime? __t` step).
        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"When.HasValue\")", lowered);
        Assert.DoesNotContain("CompiledBinding", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        // Critically: the lowered code COMPILES (the Nullable step pattern would be CS1003/CS1525 if not bailed).
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }

    [Fact] // audit fix — an init-only leaf compiles a null setter (degrades to OneWay), never an `__s.P = v` (CS8852)
    public void Lowered_Binding_InitOnlyLeaf_CompilesWithNullSetter()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.InitOnlyView\" x:DataType=\"t:InitVm\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding Caption}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class InitVm { public string Caption { get; init; } = ""hi""; }
    public partial class InitOnlyView : StackPanel { public InitOnlyView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        // The compiled lane is taken with a NULL setter (the init-only leaf is read-only for write-back) — and
        // critically the lowered code compiles (an `__s.Caption = __v` assignment would be CS8852).
        Assert.Contains("new global::Cursorial.UI.Data.CompiledBinding<global::TestApp.InitVm, string>", lowered);
        Assert.Contains("static __s => __s.Caption, null,", lowered); // getter, then a null setter
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }

    [Fact] // audit fix — a static-member path can't be `__s.Member` (CS0176); it bails to the reflective fallback
    public void Lowered_Binding_StaticMemberPath_FallsBackToReflective()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:t=\"using:TestApp\" x:Class=\"TestApp.StaticPathView\" x:DataType=\"t:StaticVm\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding Shared}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class StaticVm { public static string Shared => ""S""; }
    public partial class StaticPathView : StackPanel { public StaticPathView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        // Not a compiled `__s.Shared` (would be CS0176) — the reflective fallback handles it, and it compiles.
        Assert.DoesNotContain("CompiledBinding", lowered);
        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"Shared\")", lowered);
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }

    [Fact] // a class-less document has no code-behind to lower
    public void NoXClass_EmitsNothing()
    {
        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var document = XamlFrontend.Parse($"<StackPanel {Ns}><Button/></StackPanel>",
            new XamlParseOptions { MetadataProvider = new RoslynXamlMetadata(compilation), FoldConstants = false });
        Assert.Null(LoweringEmitter.Emit(document, "x.xaml", new XamlSymbolResolver(compilation)));
    }

    [Fact] // {RelativeSource FindAncestor} lowers to an anchor matching the runtime loader (cross-pipeline parity)
    public void Lowered_FindAncestorBinding_MatchesRuntimeLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.AncView\" Spacing=\"3\">" +
            "<Border x:Name=\"Frame\" Width=\"{Binding Spacing, RelativeSource={RelativeSource FindAncestor, AncestorType={x:Type StackPanel}, AncestorLevel=2}}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class AncView : StackPanel { public AncView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("AncHost");
        var lowered = Lower(xaml, compilation);
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel) System.Activator.CreateInstance(assembly.GetType("TestApp.AncView")!)!;

        var runtime = (StackPanel) new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        var loweredRs = RelSource((Border) view.Children[0]);
        var runtimeRs = RelSource((Border) runtime.Children[0]);

        Assert.Equal(RelativeSourceMode.FindAncestor, loweredRs.Mode);     // the generator emitted the anchor (not a TODO)
        Assert.Equal(runtimeRs.Mode, loweredRs.Mode);                      // …matching the loader
        Assert.Equal(typeof(StackPanel), loweredRs.AncestorType);
        Assert.Equal(runtimeRs.AncestorType, loweredRs.AncestorType);
        Assert.Equal(2, loweredRs.AncestorLevel);
        Assert.Equal(runtimeRs.AncestorLevel, loweredRs.AncestorLevel);
    }

    private static RelativeSource RelSource(Border border)
        => ((Binding) BindingOperations.GetBindingExpression(border, UIElement.WidthProperty)!.ParentBinding!).RelativeSource!;
}
