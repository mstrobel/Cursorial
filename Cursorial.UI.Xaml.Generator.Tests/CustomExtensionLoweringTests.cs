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
}
