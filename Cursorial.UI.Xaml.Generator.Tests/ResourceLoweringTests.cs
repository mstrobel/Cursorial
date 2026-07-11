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

        // The dict was populated via Add(key, value); a same-document {StaticResource} (define-before-use)
        // resolves to the entry's var directly — the load-time snapshot, the same instance FindResource would
        // find, with no runtime ancestor walk (consistent with the top-level dictionary var shortcut).
        Assert.Contains(".Resources.Add(\"Accent\", ", lowered);
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
}
