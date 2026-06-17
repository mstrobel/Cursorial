using System.Reflection;

using Cursorial.UI.Controls;
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

    private static string Lower(string xaml, CSharpCompilation compilation)
    {
        var document = XamlFrontend.Parse(xaml, new XamlParseOptions
        {
            MetadataProvider = new RoslynXamlMetadata(compilation),
            DiagnosticMode = XamlDiagnosticMode.CollectAll,
            FoldConstants = false,
        });
        return LoweringEmitter.Emit(document, "MyView.xaml", new XamlSymbolResolver(compilation))
            ?? throw new System.InvalidOperationException("no lowering emitted");
    }

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

    [Fact] // B3a — a {Binding} with no x:DataType in scope can't compile; it stays a // TODO X5 (no silent wrong code)
    public void Lowered_Binding_WithoutDataType_StaysTodo()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.NoDataTypeView\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding Caption}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class NoDataTypeView : StackPanel { public NoDataTypeView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        Assert.DoesNotContain("CompiledBinding", lowered);
        Assert.Contains("TODO X5", lowered);
        // It still compiles (the TODO is a comment; the rest of the tree is valid).
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
}
