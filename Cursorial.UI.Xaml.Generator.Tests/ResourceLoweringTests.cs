using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5.4 — resource lowering. <c>{DynamicResource}</c> lowers to a live
/// <c>ResourceExtensions.SetResourceReference</c> producer; <c>{StaticResource}</c> resolves eagerly at its
/// use site — a same-document visible entry to the entry's local var, an external key through
/// <c>ResourceScopes.ResolveStatic</c> (the lexical chain + application tail); <c>&lt;X.Resources&gt;</c>
/// populates the element's <c>ResourceDictionary</c> with its <c>x:Key</c>'d entries. These tests lower a real
/// view, compile + instantiate it, and assert the resources resolve identically to the runtime loader.
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
    public void Lowered_StaticResource_MarkupExtensionKey_ResolvesViaResolveStatic()
    {
        // The {x:Type Button} key is NOT a document entry — it lives in an ambient tier (App.Resources) — so a
        // UIElement-member StaticResource resolves eagerly via ResolveStatic (the lexical chain + app tail),
        // NOT the retired end-of-tree FindResource anchor.
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
        Assert.Contains("global::Cursorial.UI.ResourceScopes.ResolveStatic(typeof(global::Cursorial.UI.Controls.Button)", lowered);
        Assert.DoesNotContain("FindResource(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(20, 5) });
        try
        {
            var brush = new SolidColorBrush { Color = Colors.Red };
            host.Application.Resources[typeof(Button)] = brush;

            var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.MeKeyView")!)!;
            var button = Assert.IsType<Button>(view.Children[0]);
            Assert.Same(brush, button.Foreground); // resolved eagerly via ResolveStatic, ambient tier
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

    [Fact] // An external {StaticResource} in a Setter.Value resolves eagerly through ResourceScopes.ResolveStatic
           // (the lexical chain + app tail) — previously fenced. Pinned against the app-tier resource.
    public void Lowered_SetterValue_ExternalStaticResource_ResolvesViaResolveStatic()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.SetterExtView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"S\" Selector=\":is(Button)\">" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"{StaticResource AppInk}\"/>" + // AppInk lives in App.Resources
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SetterExtView : StackPanel { public SetterExtView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("global::Cursorial.UI.ResourceScopes.ResolveStatic(\"AppInk\"", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(20, 5) });
        try
        {
            var appInk = new SolidColorBrush { Color = Colors.Red };
            host.Application.Resources["AppInk"] = appInk;

            var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.SetterExtView")!)!;
            var style = Assert.IsType<Cursorial.UI.Style>(view.Resources["S"]);
            var setter = Assert.Single(style.Setters);
            Assert.Same(appInk, setter.Value); // the Setter captured the app-tier resource at construction
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact] // An external {Binding Converter={StaticResource …}} resolves eagerly through ResolveStatic — the
           // converter lives in App.Resources; previously the whole binding fenced.
    public void Lowered_BindingConverter_ExternalStaticResource_ResolvesViaResolveStatic()
    {
        var converter = @"
using Cursorial.UI.Data;
namespace GenApp
{
    public sealed class UpperConverter : IValueConverter
    {
        public object? Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => value?.ToString()?.ToUpperInvariant();
        public object? ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value;
    }
}";
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.ConvExtView\">" +
            "<TextBlock x:Name=\"T\" Text=\"{Binding Name, Converter={StaticResource Up}}\"/>" + // Up lives in App.Resources
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ConvExtView : StackPanel { public ConvExtView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(converter), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("Converter = global::Cursorial.UI.ResourceScopes.RequireConverter(global::Cursorial.UI.ResourceScopes.ResolveStatic(\"Up\"", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(20, 5) });
        try
        {
            host.Application.Resources["Up"] = System.Activator.CreateInstance(assembly.GetType("GenApp.UpperConverter")!);
            Assert.NotNull(System.Activator.CreateInstance(assembly.GetType("GenApp.ConvExtView")!)); // the binding installs; the converter resolved
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact] // A Converter={StaticResource} resolving to a NULL (or non-converter) resource THROWS, like the
           // loader's ResolveConverter — never a silent null converter (fail-open). RequireConverter closes it.
    public void Lowered_BindingConverter_NullResource_ThrowsNotSilentNull()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.ConvNullView\">" +
            "<TextBlock x:Name=\"T\" Text=\"{Binding Name, Converter={StaticResource Nope}}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ConvNullView : StackPanel { public ConvNullView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.Contains("RequireConverter(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(20, 5) });
        try
        {
            host.Application.Resources["Nope"] = null; // resolves to null — the loader throws ConversionFailed
            var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => System.Activator.CreateInstance(assembly.GetType("GenApp.ConvNullView")!));
            Assert.IsType<System.InvalidOperationException>(ex.InnerException); // loud, not a silent null converter
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact] // A MergedDictionaries child's key is INVISIBLE to a host-level {StaticResource} (own-entries-only,
           // the loader's scope semantics). Regression guard for the confirmed fail-open: the merged child's
           // entry var used to LEAK into the host scope's map, so a host key resolved to the merged child (a
           // silent wrong value). Now the sub-dict is scope-isolated, so the host reference does NOT wire the
           // merged child — it fences instead (fail-CLOSED; exact app-tail resolution of an also-merged key is
           // a follow-up refinement to the forward-key guard).
    public void Lowered_StaticResource_MergedDictionaryChild_NotVisibleToHost()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.MergedView\">" +
            "<StackPanel.Resources>" +
              "<ResourceDictionary>" +
                "<ResourceDictionary.MergedDictionaries>" +
                  "<ResourceDictionary><SolidColorBrush x:Key=\"Accent\" Color=\"Blue\"/></ResourceDictionary>" +
                "</ResourceDictionary.MergedDictionaries>" +
                "<Style x:Key=\"S\" Selector=\":is(Button)\">" +
                  "<Setter Property=\"TextElement.Foreground\" Value=\"{StaticResource Accent}\"/>" +
                "</Style>" +
              "</ResourceDictionary>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class MergedView : StackPanel { public MergedView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The Setter does NOT resolve to the merged child (the closed fail-open) — it fences instead.
        Assert.Contains("TODO X5", lowered);
        var setterLine = System.Array.Find(lowered.Split('\n'), l => l.Contains(".Setters.Add"));
        Assert.Null(setterLine); // no setter wired to the merged Blue brush

        // The document still compiles + constructs; the Style has no merged-child setter.
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.MergedView")!)!;
        Assert.Empty(Assert.IsType<Cursorial.UI.Style>(view.Resources["S"]).Setters);
    }

    [Fact] // Documented eager-resolution divergence (consistent with BasedOn): a Setter.Value external
           // {StaticResource} that resolves NOWHERE throws at construction, where the loader — which defers
           // dictionary-entry realization — loads the (never-realized) document without error. Fail-CLOSED
           // (a loud throw, not a silent wrong value); the resolve-success case is the common one.
    public void Lowered_SetterValue_MissingKey_ThrowsAtConstruction()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.SetterMissView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"Unused\" Selector=\":is(Border)\">" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"{StaticResource MissingInk}\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SetterMissView : StackPanel { public SetterMissView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.Contains("ResolveStatic(\"MissingInk\"", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        // Lowered: throws at construction (eager, like BasedOn). No host, so MissingInk misses the app tail too.
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => System.Activator.CreateInstance(assembly.GetType("GenApp.SetterMissView")!));
        Assert.IsType<Cursorial.UI.ResourceNotFoundException>(ex.InnerException);

        // Loader: the keyed Style is a deferred entry never realized (no Border), so Load succeeds.
        var runtime = (StackPanel)new Cursorial.UI.Xaml.XamlLoader(
            new Cursorial.UI.Xaml.XamlLoaderOptions { MetadataProvider = Cursorial.UI.Xaml.ReflectionXamlMetadata.Instance })
            .Load(xaml.Replace(" x:Class=\"GenApp.SetterMissView\"", ""));
        Assert.NotNull(runtime); // constructs without error — the divergence is the eager-vs-deferred timing
    }

    [Fact] // Resources-FIRST: a {StaticResource} on an element's OWN attribute member sees the element's OWN
           // <X.Resources>, even though the attribute precedes the <Element.Resources> property element in
           // document order (the loader's ApplyResourcesFirst). Shadowing case: an enclosing K is redefined in
           // the element's own Resources — the element's own value wins, matching the loader.
    public void Lowered_StaticResource_OwnAttributeSeesOwnResources_Shadowing()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.OwnResView\">" +
            "<StackPanel.Resources><SolidColorBrush x:Key=\"K\" Color=\"Red\"/></StackPanel.Resources>" +
            "<Border Background=\"{StaticResource K}\">" +           // attribute precedes Border.Resources
              "<Border.Resources><SolidColorBrush x:Key=\"K\" Color=\"Blue\"/></Border.Resources>" +
            "</Border>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class OwnResView : StackPanel { public OwnResView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.OwnResView")!)!;
        var loweredBg = (SolidColorBrush)Assert.IsType<Border>(view.Children[0]).Background!;

        var runtime = (StackPanel)new Cursorial.UI.Xaml.XamlLoader(
            new Cursorial.UI.Xaml.XamlLoaderOptions { MetadataProvider = Cursorial.UI.Xaml.ReflectionXamlMetadata.Instance })
            .Load(xaml.Replace(" x:Class=\"GenApp.OwnResView\"", ""));
        var runtimeBg = (SolidColorBrush)Assert.IsType<Border>(runtime.Children[0]).Background!;

        Assert.Equal(Colors.Blue, loweredBg.Color);       // the element's OWN K, NOT the enclosing Red
        Assert.Equal(runtimeBg.Color, loweredBg.Color);   // identical to the loader
    }

    [Fact] // Resources-FIRST regression guard: a {StaticResource} on an own attribute member referencing a key
           // defined ONLY in the element's own Resources resolves (the retired deferred FindResource used to
           // catch this at end-of-tree; the hoist makes the own scope visible at the attribute's emit point).
    public void Lowered_StaticResource_OwnAttribute_OwnResourceOnlyKey_Resolves()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.OwnOnlyView\">" +
            "<Border x:Name=\"B\" Background=\"{StaticResource K}\">" +
              "<Border.Resources><SolidColorBrush x:Key=\"K\" Color=\"Red\"/></Border.Resources>" +
            "</Border>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class OwnOnlyView : StackPanel { public OwnOnlyView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("TODO X5", lowered); // resolves — NOT dropped

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.OwnOnlyView")!)!;
        var border = Assert.IsType<Border>(view.Children[0]);
        Assert.Same(border.Resources["K"], border.Background); // the own-Resources brush, resolved onto Background
    }

    // A control with plain CLR (non-UIProperty) members — a settable string, and an init-only string.
    private const string ClrControl = @"
using Cursorial.UI.Controls;
namespace GenApp { public class ClrCtl : StackPanel { public string? Label { get; set; } public string? RoLabel { get; init; } } }";

    [Fact] // An external {StaticResource} on a UIElement CLR STRING property casts the object? result to string —
           // object? → string is not implicit (CS0266 the deleted deferred-FindResource path used to cast away).
    public void Lowered_StaticResource_UIElementClrStringProperty_Casts()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.ClrStrView\">" +
            "<g:ClrCtl Label=\"{StaticResource S}\"/>" + // S is external (app tier); Label is a CLR string property
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ClrStrView : StackPanel { public ClrStrView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(ClrControl), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains(".Label = (string)global::Cursorial.UI.ResourceScopes.ResolveStatic(\"S\"", lowered); // cast, not a bare object?

        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered))); // compiles (no CS0266)
    }

    [Fact] // An external {StaticResource} on a UIElement INIT-ONLY CLR member fences (a post-construction assign
           // is CS8852) — the compile-break guard, not uncompilable code.
    public void Lowered_StaticResource_UIElementInitOnlyClrMember_Fences()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.ClrRoView\">" +
            "<g:ClrCtl RoLabel=\"{StaticResource S}\"/>" + // RoLabel is init-only
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ClrRoView : StackPanel { public ClrRoView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(ClrControl), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("TODO X5", lowered);
        Assert.Contains("init-only", lowered);
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered))); // compiles (no CS8852)
    }

    [Fact] // Phase 4 — an in-template {StaticResource} resolves against the CAPTURED definition-site scope: a
           // Button inside a DataTemplate references a brush in the enclosing document Resources. The lowered
           // factory captures the document dict local (a closure); the loader re-pushes the captured chain.
    public void Lowered_StaticResource_InsideTemplate_ResolvesCapturedOuterResource()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.TplStaticView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"Ink\" Color=\"Red\"/>" +
              "<DataTemplate x:Key=\"Tpl\">" +
                "<Button Foreground=\"{StaticResource Ink}\"/>" + // captured from the enclosing StackPanel.Resources
              "</DataTemplate>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class TplStaticView : StackPanel { public TplStaticView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("TODO X5", lowered); // resolves — no longer fenced in-template

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.TplStaticView")!)!;
        var built = Assert.IsType<Button>(Assert.IsType<DataTemplate>(view.Resources["Tpl"]).Build(null));
        Assert.Same(view.Resources["Ink"], built.Foreground); // the captured outer brush

        // The loader resolves the identical shape through its re-pushed CapturedTemplateScope.
        var runtime = (StackPanel)new Cursorial.UI.Xaml.XamlLoader(
            new Cursorial.UI.Xaml.XamlLoaderOptions { MetadataProvider = Cursorial.UI.Xaml.ReflectionXamlMetadata.Instance })
            .Load(xaml.Replace(" x:Class=\"GenApp.TplStaticView\"", ""));
        var runtimeBuilt = Assert.IsType<Button>(Assert.IsType<DataTemplate>(runtime.Resources["Tpl"]).Build(null));
        Assert.Same(runtime.Resources["Ink"], runtimeBuilt.Foreground);
    }
}
