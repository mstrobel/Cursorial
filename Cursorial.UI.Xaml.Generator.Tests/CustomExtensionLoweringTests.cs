using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5 — custom markup-extension lowering. A <c>{Foo …}</c> user extension lowers to
/// <c>new FooExtension { args }.ProvideValue(new LoweredExtensionServices(…))</c> — the AOT-clean twin of
/// the loader's <c>ActivateCustomExtension</c> + <c>XamlServiceProvider</c>. These tests lower a real view,
/// compile + instantiate it, and assert the provided value matches what the runtime loader produces.
/// </summary>
public class CustomExtensionLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // {Icon Glyph=… Text=… GlyphWidth=…} — the Shell.xaml Content="{Icon …}" pattern (named args).
    public void Lowered_IconExtension_NamedArgs_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.IconView\">" +
            "<Button x:Name=\"Ok\" Content=\"{Icon Glyph=folder, Text=DIR, GlyphWidth=2}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class IconView : StackPanel { public IconView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::Cursorial.UI.Xaml.Markup.IconExtension", lowered);
        Assert.Contains(".ProvideValue(new global::Cursorial.UI.Xaml.LoweredExtensionServices(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.IconView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);
        var icon = Assert.IsType<Icon>(button.Content);
        Assert.Equal("folder", icon.Glyph);
        Assert.Equal("DIR", icon.Text);
        Assert.Equal(2, icon.GlyphWidth);

        // The loader produces the same Content — an Icon with identical tiers.
        var runtime = (StackPanel)new Cursorial.UI.Xaml.XamlLoader(
            new Cursorial.UI.Xaml.XamlLoaderOptions { MetadataProvider = Cursorial.UI.Xaml.ReflectionXamlMetadata.Instance })
            .Load(xaml.Replace(" x:Class=\"GenApp.IconView\"", ""));
        var runtimeIcon = Assert.IsType<Icon>(Assert.IsType<Button>(runtime.Children[0]).Content);
        Assert.Equal(icon.Glyph, runtimeIcon.Glyph);
        Assert.Equal(icon.Text, runtimeIcon.Text);
        Assert.Equal(icon.GlyphWidth, runtimeIcon.GlyphWidth);
    }

    [Fact] // A custom extension with a {Binding} argument (no standalone value) fences to a // TODO — the rest compiles.
    public void Lowered_CustomExtension_BindingArg_FencesCleanly()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.IconBadView\">" +
            "<Button x:Name=\"Ok\" Content=\"{Icon Glyph={Binding Whatever}}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class IconBadView : StackPanel { public IconBadView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("TODO X5", lowered);
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered))); // the TODO is a comment — compiles
    }

    [Fact] // A [ConstructorArgument] positional arg + a nested {x:Type} named arg — the general convention, via a
           // test-defined extension in the compilation. Positional maps by the attribute, {x:Type} bakes typeof(…).
           // (No loader-compare leg: the runtime ReflectionXamlMetadata can't reflect a dynamically-compiled
           // extension type — the Icon test covers loader parity; this pins the lowered mechanics.)
    public void Lowered_CustomExtension_PositionalAndTypeArg_LowersCorrectly()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class TagExtension : MarkupExtension
    {
        public TagExtension() {}
        public TagExtension(string label) { Label = label; }
        [ConstructorArgument(""label"")] public string? Label { get; set; }
        public System.Type? Kind { get; set; }
        public override object? ProvideValue(System.IServiceProvider sp) => Label + "":"" + (Kind?.Name ?? ""?"");
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.TagView\">" +
            "<Button x:Name=\"Ok\" Content=\"{g:Tag hello, Kind={x:Type Button}}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class TagView : StackPanel { public TagView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("Label = \"hello\"", lowered);
        Assert.Contains("Kind = typeof(global::Cursorial.UI.Controls.Button)", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.TagView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);
        Assert.Equal("hello:Button", button.Content); // positional Label="hello" + Kind=typeof(Button), ProvideValue ran
    }

    [Fact] // A custom extension INSIDE a template: the services carry `this` (the document root), which forces the
           // template factory non-static — else `this` in a static local function is CS8422. (Regression pin: the
           // capture flag was missing, so {Icon …} inside a DataTemplate — the commit's own motivating pattern —
           // produced non-compiling C#.)
    public void Lowered_CustomExtension_InsideTemplate_Compiles()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.IconTplView\">" +
            "<StackPanel.Resources>" +
              "<DataTemplate x:Key=\"Tpl\">" +
                "<Button Content=\"{Icon Glyph=folder, Text=DIR}\"/>" +
              "</DataTemplate>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class IconTplView : StackPanel { public IconTplView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        // The factory that carries `this` must NOT be static.
        Assert.DoesNotContain("static global::Cursorial.UI.UIElement __Factory", lowered);

        // The critical assertion: the generated C# compiles (a static factory referencing `this` is CS8422).
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.IconTplView")!)!;
        var built = Assert.IsType<Button>(Assert.IsType<DataTemplate>(view.Resources["Tpl"]).Build(null));
        var icon = Assert.IsType<Icon>(built.Content);
        Assert.Equal("folder", icon.Glyph);
    }

    [Fact] // A named arg targeting a get-only member fences (the loader raises a clean "no setter" diagnostic) —
           // never emits `new Ext { GetOnly = v }` (CS0200).
    public void Lowered_CustomExtension_ReadOnlyMember_FencesNotCS0200()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class ReadOnlyExtension : MarkupExtension
    {
        public string? Computed { get; }   // get-only — not settable in an object initializer
        public override object? ProvideValue(System.IServiceProvider sp) => Computed ?? ""x"";
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.ROView\">" +
            "<Button x:Name=\"Ok\" Content=\"{g:ReadOnly Computed=hi}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ROView : StackPanel { public ROView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("TODO X5", lowered);
        Assert.DoesNotContain("Computed =", lowered);      // never emitted into the object initializer
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered))); // compiles
    }

    [Fact] // A string ProvideValue result destined for a typed slot is coerced through the converter ladder
           // (the loader's AssignResolvedValue) — not an unchecked (double)"…" cast.
    public void Lowered_CustomExtension_StringResultTypedSlot_Coerces()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class NumExtension : MarkupExtension
    {
        public override object? ProvideValue(System.IServiceProvider sp) => ""42""; // a STRING result
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.NumView\">" +
            "<Button x:Name=\"Ok\" Width=\"{g:Num}\"/>" + // Width is a typed (int?) styled slot
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class NumView : StackPanel { public NumView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("global::Cursorial.UI.Xaml.LoweredExtensionServices.Coerce(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.NumView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);
        Assert.Equal(42, button.Width); // "42" → 42 via the converter ladder, not an InvalidCastException
    }

    [Fact] // A custom extension that probes IAmbientResources resolves an enclosing <X.Resources> key — the
           // lexical ambient chain is reconstructed from the enclosing dictionary locals (innermost-first),
           // matching the loader's XamlResourceScopeStack instead of returning a fail-open null.
    public void Lowered_CustomExtension_AmbientResources_ResolvesEnclosingResource()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class AmbientExtension : MarkupExtension
    {
        public string? Key { get; set; }
        public override object? ProvideValue(System.IServiceProvider sp)
            => sp.GetService(typeof(IAmbientResources)) is IAmbientResources ar && ar.TryFindResource(Key!, out var v) ? v : ""MISS"";
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.AmbView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"Accent\" Color=\"Red\"/>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\" Content=\"{g:Ambient Key=Accent}\"/>" + // resolves Accent from the enclosing Resources
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class AmbView : StackPanel { public AmbView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::Cursorial.UI.ResourceDictionary[] {", lowered); // the ambient chain is passed

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.AmbView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);
        Assert.Same(view.Resources["Accent"], button.Content); // resolved via IAmbientResources, NOT a fail-open null/"MISS"
    }

    [Fact] // Captured definition-site scope: a custom extension INSIDE a template SEES the enclosing document's
           // <X.Resources> — the loader re-pushes the template's captured chain at build time
           // (BuildTemplateSlice → PushChain(CapturedTemplateScope)), and the lowered factory captures the
           // document dict locals as closures. (Earlier this asserted the opposite, an under-resolution bug.)
    public void Lowered_CustomExtension_AmbientResources_SeesCapturedOuterScope()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class ProbeExtension : MarkupExtension
    {
        public string? Key { get; set; }
        public override object? ProvideValue(System.IServiceProvider sp)
            => sp.GetService(typeof(IAmbientResources)) is IAmbientResources ar && ar.TryFindResource(Key!, out var v) ? v : ""MISS"";
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.AmbTplView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"Outer\" Color=\"Red\"/>" + // in the enclosing document scope
              "<DataTemplate x:Key=\"Tpl\">" +
                "<Button Content=\"{g:Probe Key=Outer}\"/>" + // resolves the captured outer resource
              "</DataTemplate>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class AmbTplView : StackPanel { public AmbTplView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.AmbTplView")!)!;
        var built = Assert.IsType<Button>(Assert.IsType<DataTemplate>(view.Resources["Tpl"]).Build(null));
        Assert.Same(view.Resources["Outer"], built.Content); // the captured outer document resource, NOT "MISS"
    }

    [Fact] // IAmbientResources also walks the APPLICATION tail (App.Resources → Theme → Contributions → BuiltIn),
           // exactly like the loader's XamlResourceScopeStack (ambient defaults to ForApplication()). A key in NO
           // document dictionary but in App.Resources resolves — previously it fail-open MISSed (the ambient
           // bundle omitted the app tail).
    public void Lowered_CustomExtension_AmbientResources_WalksApplicationTail()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class AppProbeExtension : MarkupExtension
    {
        public string? Key { get; set; }
        public override object? ProvideValue(System.IServiceProvider sp)
            => sp.GetService(typeof(IAmbientResources)) is IAmbientResources ar && ar.TryFindResource(Key!, out var v) ? v : ""MISS"";
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.AppAmbView\">" +
            "<Button x:Name=\"Ok\" Content=\"{g:AppProbe Key=AppKey}\"/>" + // AppKey is in NO document dictionary
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class AppAmbView : StackPanel { public AppAmbView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create(
            new Cursorial.UI.Hosting.Headless.UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(20, 5) });
        try
        {
            var appResource = new Cursorial.Drawing.Media.SolidColorBrush { Color = Cursorial.Output.Colors.Red };
            host.Application.Resources["AppKey"] = appResource;

            var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.AppAmbView")!)!;
            var button = Assert.IsType<Button>(view.Children[0]);
            Assert.Same(appResource, button.Content); // resolved through the application tail, NOT a fail-open "MISS"
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact] // Review follow-up (depth-2) — a custom extension inside a NESTED template must FENCE when an
           // enclosing-factory <X.Resources> scope is unreachable, even when it is the OUTERMOST scope on the
           // ambient stack (the root has no <Resources>, so no document scope encloses it). The IAmbientResources
           // chain we could hand the extension (reachable dicts only) silently DROPS that scope, so a probed key
           // defined there resolves to the app tail instead of the enclosing dict — a fail-open. Fencing is the
           // only faithful option; the extension probes arbitrary keys, so we can't fence per-key.
    public void Lowered_CustomExtension_NestedTemplate_UnreachableEnclosingScope_Fences()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class NestProbeExtension : MarkupExtension
    {
        public string? Key { get; set; }
        public override object? ProvideValue(System.IServiceProvider sp)
            => sp.GetService(typeof(IAmbientResources)) is IAmbientResources ar && ar.TryFindResource(Key!, out var v) ? v : ""MISS"";
    }
}";
        var xaml =
            $"<ContentControl {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.NestAmbView\">" + // root: NO <Resources>
            "<ContentControl.ContentTemplate>" +
              "<DataTemplate>" +                                      // outer factory F1
                "<ContentControl>" +
                  "<ContentControl.Resources>" +
                    "<SolidColorBrush x:Key=\"K\" Color=\"Red\"/>" +  // F1-scope K — unreachable AND outermost (no doc scope)
                  "</ContentControl.Resources>" +
                  "<ContentControl.ContentTemplate>" +
                    "<DataTemplate>" +                                // inner factory F2
                      "<Button Content=\"{g:NestProbe Key=K}\"/>" +   // loader → F1's Red; the chain would miss it → fence
                    "</DataTemplate>" +
                  "</ContentControl.ContentTemplate>" +
                "</ContentControl>" +
              "</DataTemplate>" +
            "</ContentControl.ContentTemplate>" +
            "</ContentControl>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class NestAmbView : ContentControl { public NestAmbView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // Fences (the ambient chain would drop the unreachable enclosing scope) — NEVER emitted with a truncated
        // ambient that silently mis-resolves. The rest of the generated code still compiles.
        Assert.Contains("TODO X5", lowered);
        Assert.DoesNotContain("NestProbeExtension", lowered); // not lowered at all — fenced, not emitted with ambient:null
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
    }

    [Fact] // A nested {StaticResource} ARGUMENT to a custom extension that's EXTERNAL (app-tail) resolves via
           // ResolveStatic — previously only a same-dict entry var resolved (an external key fenced the whole ext).
    public void Lowered_CustomExtension_ExternalStaticResourceArg_ResolvesViaResolveStatic()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.IconArgView\">" +
            "<Button x:Name=\"Ok\" Content=\"{Icon Glyph={StaticResource GlyphKey}}\"/>" + // nested external {StaticResource} arg
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class IconArgView : StackPanel { public IconArgView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("ResolveStatic(\"GlyphKey\"", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        using var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create();
        host.Application.Resources["GlyphKey"] = "folder";
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.IconArgView")!)!;
        var icon = Assert.IsType<Icon>(Assert.IsType<Button>(view.Children[0]).Content);
        Assert.Equal("folder", icon.Glyph); // the app-tier resource resolved into the custom-extension arg
    }
}
