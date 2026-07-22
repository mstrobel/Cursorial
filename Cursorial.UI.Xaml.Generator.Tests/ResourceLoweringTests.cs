using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5.4 — resource lowering. <c>{DynamicResource}</c> lowers to a live
/// <c>ResourceExtensions.SetResourceReference</c> producer; <c>{StaticResource}</c> resolves eagerly at the
/// end of <c>InitializeComponent</c> against the now-attached element; <c>&lt;X.Resources&gt;</c> populates the
/// element's <c>ResourceDictionary</c> with its <c>x:Key</c>'d entries. These tests lower a real view, compile
/// + instantiate it, and assert the resources resolve live through the runtime engine via <c>UITestHost</c>.
/// </summary>
public class ResourceLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // {DynamicResource} → SetResourceReference; the producer resolves against the root's Resources on attach
    public void Lowered_DynamicResource_ResolvesLive()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.DynView\">" +
            "<Button x:Name=\"Ok\" Foreground=\"{DynamicResource Accent}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class DynView : StackPanel { public DynView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The DynamicResource lowered to a static SetResourceReference call (no eager resolution, no TODO).
        Assert.Contains("global::Cursorial.UI.ResourceExtensions.SetResourceReference(", lowered);
        Assert.Contains("ForegroundProperty, \"Accent\")", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.DynView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);

        // The live producer resolves "Accent" against the root's Resources once the tree is shown (attached).
        var brush = new SolidColorBrush(Color.FromRgb(0x30, 0x50, 0xc0));
        view.Resources["Accent"] = brush;

        using var host = UIHeadlessHost.Create();
        host.ShowRoot(view);

        Assert.Same(brush, button.Foreground);
    }

    [Fact] // <X.Resources> populates the dict via Add(key,value); {StaticResource} resolves it eagerly at end-of-Init
    public void Lowered_StaticResource_ResolvesFromElementResources()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.StaticView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"Accent\"/>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\" Foreground=\"{StaticResource Accent}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class StaticView : StackPanel { public StaticView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The get-object Resources dictionary is read into a local, then populated via Add(key, value) — the
        // same entry machinery the top-level <ResourceDictionary> uses. A same-document {StaticResource}
        // (define-before-use) resolves to the entry's var directly — the load-time snapshot, the same instance
        // FindResource would find, with no runtime ancestor walk.
        Assert.Contains(".Resources;", lowered);            // read into a local
        Assert.Contains(".Add(\"Accent\", ", lowered);       // populated on that local
        Assert.DoesNotContain("global::Cursorial.UI.ResourceExtensions.FindResource(", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.StaticView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);

        // The keyed brush from <StackPanel.Resources> resolved onto Foreground — the SAME instance as the entry
        // (eager snapshot, like the loader), proving both the dictionary population and StaticResource resolution.
        var resourceBrush = Assert.IsType<SolidColorBrush>(view.Resources["Accent"]);
        Assert.Same(resourceBrush, button.Foreground);
    }

    [Fact] // an init-only CLR property (SolidColorBrush.Color) is set via the construction object initializer (not CS8852)
    public void Lowered_InitOnlyClrProperty_SetViaObjectInitializer()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.InitView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"#3050C0\"/>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\" Foreground=\"{StaticResource Accent}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class InitView : StackPanel { public InitView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The init-only Color is set in the construction object initializer (a post-construction `.Color =` is CS8852).
        Assert.Contains("new global::Cursorial.Drawing.Media.SolidColorBrush { Color =", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.InitView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);

        // The converter ran (#3050C0 → Color) and the init-only Color was set — the resolved brush carries it.
        var brush = Assert.IsType<SolidColorBrush>(button.Foreground);
        Assert.Equal(Color.FromRgb(0x30, 0x50, 0xC0), brush.Color);
    }

    [Fact] // {DynamicResource} on a non-styled / non-bindable target stays a // TODO X5 (matches the runtime reject)
    public void Lowered_DynamicResource_NonStyledTarget_StaysTodo()
    {
        // Width is a styled double? — use a CLR/plain target instead: Grid.Row is attached (not a StyledProperty<T>).
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.DynBadView\">" +
            "<Button x:Name=\"Ok\" Grid.Row=\"{DynamicResource RowKey}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class DynBadView : StackPanel { public DynBadView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("SetResourceReference", lowered);
        Assert.Contains("TODO X5", lowered);
        // The TODO is a comment — the rest compiles.
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }

    [Fact] // Inline <X.Resources> now routes through the top-level entry machinery: an UNKEYED Style keys by
           // its implicit "Style:<TargetType>" form (the loader's TryGetImplicitKey) — previously a // TODO.
    public void Lowered_InlineResources_ImplicitStyleKey_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.ImplStyleView\">" +
            "<StackPanel.Resources>" +
              "<Style TargetType=\"Button\"><Setter Property=\"TextElement.Foreground\" Value=\"Red\"/></Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ImplStyleView : StackPanel { public ImplStyleView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains(".Add(\"Style:Button\", ", lowered); // the implicit key, raw target-type text

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.ImplStyleView")!)!;
        Assert.True(view.Resources.TryGetValue("Style:Button", out var styled));
        Assert.IsType<Cursorial.UI.Style>(styled);

        // The loader keys it identically.
        var runtime = (StackPanel)new Cursorial.UI.Xaml.XamlLoader(
            new Cursorial.UI.Xaml.XamlLoaderOptions { MetadataProvider = Cursorial.UI.Xaml.ReflectionXamlMetadata.Instance })
            .Load(xaml.Replace(" x:Class=\"GenApp.ImplStyleView\"", ""));
        Assert.True(runtime.Resources.TryGetValue("Style:Button", out _));
    }

    [Fact] // An UNKEYED DataTemplate keys by new DataTemplateKey(typeof(DataType)) — the Shell.xaml
           // view-resolution-template pattern, previously a // TODO.
    public void Lowered_InlineResources_ImplicitDataTemplateKey_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.ImplTplView\">" +
            "<StackPanel.Resources>" +
              "<DataTemplate DataType=\"Button\"><TextBlock Text=\"x\"/></DataTemplate>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ImplTplView : StackPanel { public ImplTplView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::Cursorial.UI.DataTemplateKey(typeof(global::Cursorial.UI.Controls.Button))", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.ImplTplView")!)!;
        Assert.True(view.Resources.TryGetValue(new Cursorial.UI.DataTemplateKey(typeof(Button)), out var tpl));
        Assert.IsType<DataTemplate>(tpl);

        var runtime = (StackPanel)new Cursorial.UI.Xaml.XamlLoader(
            new Cursorial.UI.Xaml.XamlLoaderOptions { MetadataProvider = Cursorial.UI.Xaml.ReflectionXamlMetadata.Instance })
            .Load(xaml.Replace(" x:Class=\"GenApp.ImplTplView\"", ""));
        Assert.True(runtime.Resources.TryGetValue(new Cursorial.UI.DataTemplateKey(typeof(Button)), out _));
    }

    [Fact] // An {x:Type} key in inline Resources resolves to a typeof(...) dictionary key (control-theme shape),
           // previously fenced by the plain-string-key-only inline path.
    public void Lowered_InlineResources_XTypeKey_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.XTypeKeyView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"{x:Type Button}\" Color=\"Red\"/>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class XTypeKeyView : StackPanel { public XTypeKeyView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains(".Add(typeof(global::Cursorial.UI.Controls.Button), ", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.XTypeKeyView")!)!;
        Assert.True(view.Resources.TryGetValue(typeof(Button), out var brush));
        Assert.IsType<SolidColorBrush>(brush);
    }

    [Fact] // A nested <ResourceDictionary Source="rel.xaml"/> inside <X.Resources> FOLDS into the host's
           // Resources with the relative URI resolved against the document — previously the RD was pre-built
           // generically (Source unresolved) and dropped, losing every shared resource.
    public void Lowered_InlineResources_NestedSourceDictionary_FoldsWithResolvedUri()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.MergeView\">" +
            "<StackPanel.Resources>" +
              "<ResourceDictionary Source=\"Shared.xaml\"/>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        // Folded into the host Resources local, Source assigned as a resolved Uri (not routed through the
        // scalar converter, not dropped).
        Assert.Contains(".Source = new global::System.Uri(", lowered);
        Assert.Contains("Shared.xaml", lowered);
    }

    [Fact] // A non-same-dictionary {StaticResource {x:Type …}} on a UIElement resolves through the end-of-tree
           // FindResource anchor with a typeof(...) key — previously fenced because the anchor was string-only.
    public void Lowered_StaticResource_MarkupExtensionKey_ResolvesViaFindResource()
    {
        // The {x:Type Button} key is NOT a document entry — it lives in an ambient tier (App.Resources) — so it
        // takes the deferred FindResource anchor rather than a same-dictionary var hit.
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.MeKeyView\">" +
            "<Button x:Name=\"Ok\" Foreground=\"{StaticResource {x:Type Button}}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class MeKeyView : StackPanel { public MeKeyView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("FindResource(", lowered);
        Assert.Contains("typeof(global::Cursorial.UI.Controls.Button)", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(20, 5) });
        try
        {
            var brush = new SolidColorBrush { Color = Colors.Red };
            host.Application.Resources[typeof(Button)] = brush;

            var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.MeKeyView")!)!;
            var button = Assert.IsType<Button>(view.Children[0]);
            Assert.Same(brush, button.Foreground); // resolved through the deferred FindResource anchor, ambient tier
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact] // Lexical shadowing: an inner <X.Resources> redefinition of a key must NOT leak to an outer/sibling
           // scope. A sibling of the inner element resolves {StaticResource} to the OUTER definition — the flat,
           // never-popped var map used to return the inner (last-writer) var (a confirmed fail-open).
    public void Lowered_StaticResource_LexicalShadowing_ResolvesOuterNotInner()
    {
        // Outer K = Red (StackPanel scope); inner K = Blue (Border scope). The Button is a SIBLING of the
        // Border, so only the OUTER K is in its scope.
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.ShadowView\">" +
            "<StackPanel.Resources><SolidColorBrush x:Key=\"K\" Color=\"Red\"/></StackPanel.Resources>" +
            "<Border><Border.Resources><SolidColorBrush x:Key=\"K\" Color=\"Blue\"/></Border.Resources></Border>" +
            "<Button x:Name=\"Ok\" Foreground=\"{StaticResource K}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ShadowView : StackPanel { public ShadowView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.ShadowView")!)!;
        var loweredFg = (SolidColorBrush)Assert.IsType<Button>(view.Children[1]).Foreground!;

        // The loader oracle resolves the OUTER K (Red).
        var runtime = (StackPanel)new Cursorial.UI.Xaml.XamlLoader(
            new Cursorial.UI.Xaml.XamlLoaderOptions { MetadataProvider = Cursorial.UI.Xaml.ReflectionXamlMetadata.Instance })
            .Load(xaml.Replace(" x:Class=\"GenApp.ShadowView\"", ""));
        var runtimeFg = (SolidColorBrush)Assert.IsType<Button>(runtime.Children[1]).Foreground!;

        Assert.Equal(Colors.Red, loweredFg.Color);        // NOT the inner Blue
        Assert.Equal(runtimeFg.Color, loweredFg.Color);   // and identical to the loader
    }
}
