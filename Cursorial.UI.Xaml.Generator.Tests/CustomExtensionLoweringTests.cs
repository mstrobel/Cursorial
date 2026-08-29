using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.UI;
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

    [Fact] // A {Binding} nested as a custom-extension ARGUMENT is input the LOADER hard-rejects
           // (ResolveNestedExtension Fatal: it produces a live/deferred value, not an argument value) —
           // an error-level marker for lane parity, and the rest still compiles.
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

        Assert.Contains("// ERROR X5", lowered);            // error-level: the loader Fatals on this document
        Assert.Contains("live/deferred", lowered);          // …and the marker names the loader's reason
        Assert.DoesNotContain("TODO X5", lowered);          // never a warning-level Todo for invalid input
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered))); // the marker is a comment — compiles
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

    [Fact] // A named arg targeting a get-only member is an ERROR-level fence (the loader raises its hard "no
           // setter" diagnostic on the identical document) — and never emits `new Ext { GetOnly = v }` (CS0200).
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

        Assert.Contains("// ERROR X5", lowered);           // error-level parity with the loader's "no setter"
        Assert.Contains("read-only", lowered);             // …naming the actual problem
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
            var appResource = new SolidColorBrush { Color = Colors.Red };
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

    [Fact] // Ambient-scope transport (depth-2) — a custom extension inside a NESTED template RESOLVES against the
           // enclosing template's <X.Resources>: the enclosing dict is captured at the definition site (the outer
           // FuncTemplateContent's captured chain) and re-exposed to the inner factory via
           // TemplateBuildContext.AmbientResources — the generated twin of the loader's XamlTemplateContent captured
           // scope. No fence, no fail-open to the app tail; the probed key binds the enclosing dict's value.
    public void Lowered_CustomExtension_NestedTemplate_ResolvesEnclosingScope_ViaTransport()
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
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.NestAmbView\">" +
            "<ContentControl Content=\"a\">" +                          // realizes its ContentTemplate (F1)
              "<ContentControl.ContentTemplate>" +
                "<DataTemplate>" +                                      // outer factory F1
                  "<ContentControl Content=\"b\">" +                    // F1's root; realizes F2; owns the enclosing scope
                    "<ContentControl.Resources>" +
                      "<SolidColorBrush x:Key=\"K\" Color=\"Red\"/>" +  // K lives in F1's factory scope (enclosing F2)
                    "</ContentControl.Resources>" +
                    "<ContentControl.ContentTemplate>" +
                      "<DataTemplate>" +                                // inner factory F2
                        "<Button Content=\"{g:NestProbe Key=K}\"/>" +   // resolves F1's Red via the transported chain
                      "</DataTemplate>" +
                    "</ContentControl.ContentTemplate>" +
                  "</ContentControl>" +
                "</DataTemplate>" +
              "</ContentControl.ContentTemplate>" +
            "</ContentControl>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class NestAmbView : StackPanel { public NestAmbView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // No fence — the extension IS lowered, and the enclosing chain is transported: the inner factory reads
        // __ctx.AmbientResources, and the outer FuncTemplateContent carries the captured enclosing dict.
        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("NestProbeExtension", lowered);
        Assert.Contains("__ctx.AmbientResources", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create(
            new Cursorial.UI.Hosting.Headless.UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(20, 5) });
        try
        {
            var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.NestAmbView")!)!;
            host.ShowRoot(view);
            host.RunUntilIdle();

            var button = FindDescendant<Button>(view);
            Assert.NotNull(button);
            var brush = Assert.IsType<SolidColorBrush>(button!.Content); // NOT the string "MISS"
            Assert.Equal(Colors.Red, brush.Color);                       // K resolved against the enclosing F1 scope
        }
        finally
        {
            host.Dispose();
        }
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        if (root is T match)
            return match;
        for (var i = 0; i < root.VisualChildrenCount; i++)
            if (FindDescendant<T>(root.GetVisualChild(i)) is { } found)
                return found;
        return null;
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

    [Fact] // Lift (T1) — a NESTED custom extension as another custom extension's ARGUMENT now COMPOSES
           // ({g:Outer Kind={g:Inner …}}): runtime parity with ResolveNestedExtension's default arm
           // (ProvideStandaloneCustomValue — target-less eager ProvideValue). Both ProvideValues run; the inner
           // result lands in the outer's property — raw for an object slot, (string)-cast for a string slot.
    public void Lowered_CustomExtension_NestedCustomArg_Composes()
    {
        var extensions = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class InnerExtension : MarkupExtension
    {
        public string? Val { get; set; }
        public override object? ProvideValue(System.IServiceProvider sp) => ""IN:"" + Val;
    }
    public sealed class OuterExtension : MarkupExtension
    {
        public object? Kind { get; set; }     // object slot — takes the inner's provide-value raw
        public string? Prefix { get; set; }   // string slot — takes it through the (string) cast
        public override object? ProvideValue(System.IServiceProvider sp) => Prefix + ""("" + Kind + "")"";
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.ComposeView\">" +
            "<Button x:Name=\"Ok\" Content=\"{g:Outer Prefix={g:Inner Val=p}, Kind={g:Inner Val=x}}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class ComposeView : StackPanel { public ComposeView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extensions), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::GenApp.OuterExtension", lowered);
        Assert.Contains("new global::GenApp.InnerExtension", lowered); // the nested extension is emitted, not fenced
        Assert.Contains("Prefix = (string)new global::GenApp.InnerExtension", lowered); // string slot casts

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.ComposeView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);
        Assert.Equal("IN:p(IN:x)", button.Content); // inner ProvideValues ran, results landed in the outer's members
    }

    [Fact] // Lift (T5) — {DynamicResource {g:CustomKey}}: a custom extension as the DynamicResource KEY lowers —
           // the key is an opaque object handed to SetResourceReference (the loader's ResolveResourceKey →
           // ResolveNestedExtension), so its eager target-less ProvideValue IS the key. Resolves live.
    public void Lowered_DynamicResource_CustomExtensionKey_LowersAndResolvesLive()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class KeyExtension : MarkupExtension
    {
        public override object? ProvideValue(System.IServiceProvider sp) => ""Accent""; // the runtime key object
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.DynKeyView\">" +
            "<Button x:Name=\"Ok\" Foreground=\"{DynamicResource {g:Key}}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class DynKeyView : StackPanel { public DynKeyView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        // SetResourceReference receives the ProvideValue expression as the key.
        Assert.Contains("global::Cursorial.UI.ResourceExtensions.SetResourceReference(", lowered);
        Assert.Contains("new global::GenApp.KeyExtension().ProvideValue(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.DynKeyView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);

        var brush = new SolidColorBrush(Color.FromRgb(0x30, 0x50, 0xc0));
        view.Resources["Accent"] = brush; // the key the extension PROVIDED at build time

        using var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create();
        host.ShowRoot(view);
        Assert.Same(brush, button.Foreground); // the provided key object drove the live resource reference
    }

    [Fact] // Lift (T6) — a custom extension feeding an INIT-ONLY CLR property (SolidColorBrush.Color) routes
           // through the init-only pre-scan into the CONSTRUCTION object initializer (a post-construction assign
           // would be CS8852 — non-compiling output), with the string ProvideValue result coerced to the slot type.
    public void Lowered_CustomExtension_InitOnlyProperty_LowersViaInitializer()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class AccentExtension : MarkupExtension
    {
        public override object? ProvideValue(System.IServiceProvider sp) => ""Red""; // a STRING result for a typed init-only slot
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.InitCustomView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"B\" Color=\"{g:Accent}\"/>" + // Color is { get; init; }
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class InitCustomView : StackPanel { public InitCustomView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        // The ProvideValue expression sits INSIDE the construction initializer, Coerce'd + cast to the slot type.
        Assert.Contains("Color = (global::Cursorial.Media.Color)global::Cursorial.UI.Xaml.LoweredExtensionServices.Coerce(new global::GenApp.AccentExtension", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.InitCustomView")!)!;
        var brush = Assert.IsType<SolidColorBrush>(view.Resources["B"]);
        Assert.Equal(Colors.Red, brush.Color); // "Red" ran the converter ladder into the init-only slot
    }

    [Fact] // Lift (T6, fence leg) — the UN-routable init-only case: an object with its OWN <X.Resources> can't take
           // the initializer route (the own scope would be missing from the ambient chain — a shadow fail-open), so
           // it fences with CURG3001 instead of emitting the CS8852 post-construction assign. EmitAndLoad succeeding
           // IS the no-CS8852 assertion.
    public void Lowered_CustomExtension_InitOnlyWithOwnResources_FencesNotCS8852()
    {
        var sources = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class AccentExtension : MarkupExtension
    {
        public override object? ProvideValue(System.IServiceProvider sp) => ""Red"";
    }
    public sealed class Holder
    {
        public Cursorial.UI.ResourceDictionary Resources { get; } = new();
        public string? Tint { get; init; }   // init-only — a post-construction assign is CS8852
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.HolderView\">" +
            "<StackPanel.Resources>" +
              "<g:Holder x:Key=\"H\" Tint=\"{g:Accent}\">" +
                "<g:Holder.Resources>" +
                  "<SolidColorBrush x:Key=\"X\" Color=\"Red\"/>" + // own scope — blocks the initializer route
                "</g:Holder.Resources>" +
              "</g:Holder>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class HolderView : StackPanel { public HolderView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(sources), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("TODO X5", lowered);
        Assert.Contains("init-only", lowered);          // the fence names the actual problem
        Assert.DoesNotContain("Tint =", lowered);        // neither an initializer entry nor the CS8852 assign
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered))); // compiles
    }

    [Fact] // Lift (T7) — DataCondition.Value = a custom extension inside <Style.When>: the runtime builds
           // <DataCondition> through the GENERIC object path (Value ← ApplyExtension → AttachCustom), so the
           // provide-value semantics are well-defined; the lowering provides it in the construction initializer
           // (Value is init-only; object-typed slot, no cast).
    public void Lowered_DataConditionValue_CustomExtension_Lowers()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class FlagExtension : MarkupExtension
    {
        public override object? ProvideValue(System.IServiceProvider sp) => true; // a boxed bool condition value
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.WhenView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"S\" Selector=\":is(Button)\">" +
                "<Style.When>" +
                  "<DataCondition Binding=\"{Binding RelativeSource={RelativeSource Self}, Path=IsEditable}\" Value=\"{g:Flag}\"/>" +
                "</Style.When>" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class WhenView : StackPanel { public WhenView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("When.Add(new global::Cursorial.UI.DataCondition", lowered);
        Assert.Contains("Value = new global::GenApp.FlagExtension().ProvideValue(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.WhenView")!)!;
        var style = Assert.IsType<Style>(view.Resources["S"]);
        var condition = Assert.Single(style.When);
        Assert.Equal(true, condition.Value); // the ProvideValue result landed in the condition, style not dropped
    }

    [Fact] // Lift — Setter.Value = a custom extension: BOTH lanes now provide the value eagerly and
           // target-less (the standalone-entry precedent); previously the style was dropped (lowered) /
           // silently valueless (loader). Object-typed slot, no cast; a fenced custom still DropStyles.
    public void Lowered_SetterValue_CustomExtension_Lowers()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class StampExtension : MarkupExtension
    {
        public override object? ProvideValue(System.IServiceProvider sp) => ""styled!"";
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.SetterView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"S\" Selector=\":is(Button)\">" +
                "<Setter Property=\"ContentControl.Content\" Value=\"{g:Stamp}\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SetterView : StackPanel { public SetterView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("new global::GenApp.StampExtension().ProvideValue(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.SetterView")!)!;
        var style = Assert.IsType<Style>(view.Resources["S"]);
        var setter = Assert.Single(style.Setters);
        Assert.Equal("styled!", setter.Value); // the provided value landed; the style was NOT dropped
    }

    [Fact] // Lift (T8) — double-report suppression: a FAILING custom extension entry in a dictionary emits exactly
           // ONE marker (CustomExtensionExpr's own — error-level here, since a read-only member is loader-Fatal
           // input) — not that PLUS the caller's generic "dictionary entry of this shape" one.
    public void Lowered_CustomExtension_FailingDictionaryEntry_ReportsExactlyOnce()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class FailExtension : MarkupExtension
    {
        public string? Computed { get; }   // get-only — the named arg targets a read-only member and fences
        public override object? ProvideValue(System.IServiceProvider sp) => Computed;
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.OnceView\">" +
            "<StackPanel.Resources>" +
              "<g:Fail x:Key=\"F\" Computed=\"hi\"/>" + // element-form custom extension entry that fences
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class OnceView : StackPanel { public OnceView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Equal(1, lowered.Split("ERROR X5").Length - 1);                      // exactly ONE marker (error-level)
        Assert.DoesNotContain("TODO X5", lowered);                                  // …no warning-level double-report either
        Assert.Contains("read-only", lowered);                                      // …and it names the real problem
        Assert.DoesNotContain("dictionary entry of this shape", lowered);           // the generic second Todo is suppressed
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered))); // still compiles
    }

    [Fact] // Lift (T2 safe subset) — ONE positional arg + EXACTLY ONE public settable property: with a single
           // candidate, the loader's reflection-declaration-order fallback and the lowered mapping agree
           // trivially (order-irrelevant), so the unannotated extension lowers and live-provides instead of
           // fencing.
    public void Lowered_CustomExtension_SinglePositionalUnannotated_Lowers()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class GreetExtension : MarkupExtension
    {
        public string? Who { get; set; }   // the ONLY public settable property — the unambiguous positional target
        public override object? ProvideValue(System.IServiceProvider sp) => ""hello "" + Who;
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.GreetView\">" +
            "<Button x:Name=\"Ok\" Content=\"{g:Greet world}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class GreetView : StackPanel { public GreetView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.DoesNotContain("ERROR X5", lowered);
        Assert.Contains("Who = \"world\"", lowered);        // the single positional mapped to the single property

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.GreetView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);
        Assert.Equal("hello world", button.Content);         // ProvideValue ran with the mapped positional
    }

    [Fact] // Fence-guard (T2) — TWO unannotated positionals keep the warning-level fence: with two candidate
           // properties the loader's reflection-declaration-order fallback is order-dependent, which source
           // order can't provably reproduce; the message points at the [ConstructorArgument] fix.
    public void Lowered_CustomExtension_TwoPositionalUnannotated_StillFences()
    {
        var extension = @"
using Cursorial.UI.Xaml;
namespace GenApp
{
    public sealed class SpanExtension : MarkupExtension
    {
        public SpanExtension() {}
        public SpanExtension(string a, string b) { A = a; B = b; }   // arity matches — but NO [ConstructorArgument]s
        public string? A { get; set; }
        public string? B { get; set; }
        public override object? ProvideValue(System.IServiceProvider sp) => A + B;
    }
}";
        var xaml =
            $"<StackPanel {Ns} xmlns:g=\"clr-namespace:GenApp;assembly=LoweringHost\" x:Class=\"GenApp.SpanView\">" +
            "<Button x:Name=\"Ok\" Content=\"{g:Span one, two}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SpanView : StackPanel { public SpanView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(extension), CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        Assert.Contains("TODO X5", lowered);                 // warning-level: VALID input the lowering declines
        Assert.Contains("[ConstructorArgument]", lowered);   // …and the message names the convention fix
        Assert.DoesNotContain("A = \"one\"", lowered);       // never a guessed mapping
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered))); // compiles
    }
}
