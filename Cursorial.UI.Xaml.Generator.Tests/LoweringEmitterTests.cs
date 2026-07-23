using System.Reflection;

using Cursorial.Drawing.Media;
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

    [Fact] // Style.When — a <Style.When>/<DataCondition> conjunction lowers to When.Add(new DataCondition{…}) and matches the loader
    public void Lowered_StyleRequiresCapabilities_MatchesLoader()
    {
        // The capability gate must survive FULL lowering — a silently dropped attribute turns a
        // tier-gated rule into an unconditional one (the occlusion-everywhere regression this pins).
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.CapsView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"CapsStyle\" Selector=\":is(Border)\" RequiresCapabilities=\"NoColor, Motion\">" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class CapsView : StackPanel { public CapsView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains("RequiresCapabilities = global::Cursorial.UI.StyleCapabilities.NoColor | global::Cursorial.UI.StyleCapabilities.Motion", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.CapsView")!)!;
        var loweredStyle = Assert.IsType<Style>(view.Resources["CapsStyle"]);
        Assert.Equal(StyleCapabilities.NoColor | StyleCapabilities.Motion, loweredStyle.RequiresCapabilities);
    }

    [Fact] // {x:Static} RequiresCapabilities lowers without evaluation: the frontend folds a const enum
           // member (→ the typed cast), and an unfolded static bakes as a member-access REFERENCE the C#
           // compiler resolves — reading a static field is neither parsing nor reflection.
    public void Lowered_StyleRequiresCapabilities_XStatic_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.CapsStaticView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"CapsStyle\" Selector=\":is(Border)\" RequiresCapabilities=\"{x:Static StyleCapabilities.NoColor}\">" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class CapsStaticView : StackPanel { public CapsStaticView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        var todoLine = lowered.Split('\n').FirstOrDefault(l => l.Contains("TODO X5"));
        Assert.True(todoLine is null, $"unexpected TODO: {todoLine}"); // never dropped — the reference bakes

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.CapsStaticView")!)!;
        var loweredStyle = Assert.IsType<Style>(view.Resources["CapsStyle"]);
        Assert.Equal(StyleCapabilities.NoColor, loweredStyle.RequiresCapabilities);
    }

    [Fact] // A PREFIXED {x:Static co:Colors.Red} member value bakes as a member-access reference: the type
           // token binds through the document xmlns table (like x:DataType), not just the default UI uri —
           // the palette's tier dictionaries author every Ansi16 color this way.
    public void Lowered_PrefixedXStaticMemberValue_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:co=\"clr-namespace:Cursorial.Output;assembly=Cursorial.Core\" x:Class=\"GenApp.StaticColorView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"Ink\" Color=\"{x:Static co:Colors.Red}\"/>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class StaticColorView : StackPanel { public StaticColorView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains("global::Cursorial.Output.Colors.Red", lowered);
        var todoLine = lowered.Split('\n').FirstOrDefault(l => l.Contains("TODO X5"));
        Assert.True(todoLine is null, $"unexpected TODO: {todoLine}");

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.StaticColorView")!)!;
        var loweredBrush = Assert.IsType<SolidColorBrush>(view.Resources["Ink"]);

        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml.Replace(" x:Class=\"GenApp.StaticColorView\"", ""));
        var runtimeBrush = Assert.IsType<SolidColorBrush>(runtime.Resources["Ink"]);
        Assert.Equal(runtimeBrush.Color, loweredBrush.Color);
        Assert.Equal(Cursorial.Output.Colors.Red, loweredBrush.Color);
    }

    [Fact] // Same-dictionary BasedOn: the ONE working form — pinned for the first time (identity with the
           // built base entry, matching StaticResource's load-time-snapshot semantics), so the fail-closed
           // path added for every OTHER form can't regress the working one.
    public void Lowered_StyleBasedOn_SameDictionary_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.BasedOnView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"Base\" Selector=\":is(Border)\">" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
              "<Style x:Key=\"Derived\" Selector=\":is(Border)\" BasedOn=\"{StaticResource Base}\">" +
                "<Setter Property=\"TextElement.Strikethrough\" Value=\"True\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class BasedOnView : StackPanel { public BasedOnView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.BasedOnView")!)!;
        var baseStyle = Assert.IsType<Style>(view.Resources["Base"]);
        var derived = Assert.IsType<Style>(view.Resources["Derived"]);
        Assert.Same(baseStyle, derived.BasedOn);

        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml.Replace(" x:Class=\"GenApp.BasedOnView\"", ""));
        Assert.Same(runtime.Resources["Base"], Assert.IsType<Style>(runtime.Resources["Derived"]).BasedOn);
    }

    [Fact] // A BasedOn whose base lives in an OUTER dictionary of the same document resolves through the
           // document-flat entry map (the entry's var — StaticResource's load-time snapshot); previously the
           // cross-dictionary shape emitted the style WITHOUT its base, silently dropping every inherited
           // setter with no marker.
    public void Lowered_StyleBasedOn_OuterDictionary_ProbesLiveChain()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.BasedOnOuterView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"Base\" Selector=\":is(Border)\">" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Border>" +
              "<Border.Resources>" +
                "<Style x:Key=\"Derived\" Selector=\":is(Border)\" BasedOn=\"{StaticResource Base}\">" +
                  "<Setter Property=\"TextElement.Strikethrough\" Value=\"True\"/>" +
                "</Style>" +
              "</Border.Resources>" +
            "</Border>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class BasedOnOuterView : StackPanel { public BasedOnOuterView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.BasedOnOuterView")!)!;
        var baseStyle = Assert.IsType<Style>(view.Resources["Base"]);
        var border = Assert.IsType<Border>(view.Children[0]);
        Assert.Same(baseStyle, Assert.IsType<Style>(border.Resources["Derived"]).BasedOn);

        // The loader resolves the identical shape through its lexical stack.
        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml.Replace(" x:Class=\"GenApp.BasedOnOuterView\"", ""));
        Assert.Same(runtime.Resources["Base"],
            Assert.IsType<Style>(Assert.IsType<Border>(runtime.Children[0]).Resources["Derived"]).BasedOn);
    }

    [Fact] // A BasedOn whose base lives OUTSIDE the document — App.Resources / App.Theme /
           // ThemeContributions / CursorialTheme.BuiltIn — probes the LIVE chain at build time, anchored on
           // the view root (the BasedOn="{StaticResource {x:Type …}}" app pattern). Previously: silently
           // base-less.
    public void Lowered_StyleBasedOn_AmbientTheme_ProbesLiveChain()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.BasedOnAmbientView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"Derived\" Selector=\":is(Border)\" BasedOn=\"{StaticResource AppBase}\">" +
                "<Setter Property=\"TextElement.Strikethrough\" Value=\"True\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class BasedOnAmbientView : StackPanel { public BasedOnAmbientView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.DoesNotContain("TODO X5", lowered);
        // Resolved eagerly against the lexical chain + app tail (ResourceScopes.ResolveStatic — the loader's
        // XamlResourceScopeStack), NOT the live-tree FindResource walk.
        Assert.Contains("global::Cursorial.UI.ResourceScopes.ResolveStatic(\"AppBase\"", lowered);
        Assert.DoesNotContain("FindResource(this,", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));

        // The resolve walks the app tiers — host the instantiation so UIApplication.Resources carries the base.
        var host = Cursorial.UI.Hosting.Headless.UIHeadlessHost.Create(
            new Cursorial.UI.Hosting.Headless.UIHeadlessHostOptions { InitialSize = new Cursorial.Rendering.Size(20, 5) });
        try
        {
            var appBase = new Style(Selectors.Is<Border>());
            host.Application.Resources["AppBase"] = appBase;

            var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.BasedOnAmbientView")!)!;
            Assert.Same(appBase, Assert.IsType<Style>(view.Resources["Derived"]).BasedOn);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact] // A BasedOn key that resolves NOWHERE throws ResourceNotFoundException at InitializeComponent —
           // eager + loud, the loader's exact miss behavior (never a silently base-less style).
    public void Lowered_StyleBasedOn_MissingKey_ThrowsLikeTheLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.BasedOnMissView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"Orphan\" Selector=\":is(Border)\" BasedOn=\"{StaticResource NotBuiltAnywhere}\">" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class BasedOnMissView : StackPanel { public BasedOnMissView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.DoesNotContain("TODO X5", lowered); // probed, not fenced

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => System.Activator.CreateInstance(assembly.GetType("GenApp.BasedOnMissView")!));
        Assert.IsType<ResourceNotFoundException>(ex.InnerException);

        // The loader defers resource realization — its identical throw lands on first entry ACCESS.
        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml.Replace(" x:Class=\"GenApp.BasedOnMissView\"", ""));
        Assert.Throws<XamlParseException>(() => runtime.Resources["Orphan"]);
    }

    [Fact] // A FORWARD same-dictionary BasedOn — base defined AFTER the derived style — the loader resolves
           // (it realizes entries lazily, order-independent, matrix X115), but the inline probe would run
           // during construction before the base is built and CRASH. Fail closed (drop + marker), never a
           // runtime ResourceNotFoundException on a document the loader loads. (Regression pin: the probe
           // must distinguish a forward intra-document key from a genuinely external one.)
    public void Lowered_StyleBasedOn_ForwardReference_ResolvesViaDeferredEntry()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.BasedOnFwdView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"Derived\" Selector=\":is(Border)\" BasedOn=\"{StaticResource Base}\">" +
                "<Setter Property=\"TextElement.Strikethrough\" Value=\"True\"/>" +
              "</Style>" +
              "<Style x:Key=\"Base\" Selector=\":is(Border)\">" + // defined AFTER the derived — forward ref
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class BasedOnFwdView : StackPanel { public BasedOnFwdView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        // The dict has a forward reference, so it defers (lazy slots) — the forward BasedOn resolves at realize
        // time, matching the loader's lazy entry realization; no longer fenced.
        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains(".SetDeferred(", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.BasedOnFwdView")!)!;
        var derived = Assert.IsType<Style>(view.Resources["Derived"]);
        var baseStyle = Assert.IsType<Style>(view.Resources["Base"]);
        Assert.Same(baseStyle, derived.BasedOn); // forward reference resolved to the later sibling
    }

    [Fact] // A missing {x:Reference} in a template THROWS at build time (the loader's ReferenceNotFound
           // Fatal) instead of silently assigning null — the regression the review caught: my new template
           // and deferred-Install lanes had reintroduced the silent-null rebind.
    public void Lowered_XReference_Missing_ThrowsNotSilentNull()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.RefMissView\">" +
            "<StackPanel.Resources>" +
              "<DataTemplate x:Key=\"Tpl\">" +
                "<Label Content=\"lbl\" Target=\"{x:Reference Nope}\"/>" + // no element named Nope
              "</DataTemplate>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class RefMissView : StackPanel { public RefMissView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("NameScopeExtensions.Require(__ctx.NameScope, \"Nope\")", lowered); // throw-on-miss, not Find

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.RefMissView")!)!;
        var tpl = Assert.IsType<DataTemplate>(view.Resources["Tpl"]);
        // Building the template resolves x:References — the miss throws (never a silent null Target).
        Assert.ThrowsAny<System.Exception>(() => tpl.Build(null));
    }

    [Fact] // A document-level {Binding Source={x:Reference X}} in a document with NO document-scope x:Names
           // has no __scope to resolve through — the reference can never resolve (the loader Fatals too).
           // Fence at build time instead of emitting a reference to an undeclared __scope (CS0103).
    public void Lowered_XReference_NoDocumentScope_FencesNotCS0103()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.NoScopeView\">" +
            "<TextBlock Text=\"{Binding Source={x:Reference Ghost}, Path=Text}\"/>" + // no x:Name anywhere
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class NoScopeView : StackPanel { public NoScopeView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains("TODO X5", lowered);
        Assert.DoesNotContain("__scope", lowered); // never references an undeclared scope

        // And the generated source compiles + instantiates (the binding is dropped, not mangled).
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(System.Activator.CreateInstance(assembly.GetType("GenApp.NoScopeView")!));
    }

    [Fact] // A <Style.When> whose condition subtree records deferred end-of-scope work ({x:Reference}) and
           // then DROPS must roll that work back — else the document-end flush references a local that was
           // emitted into the discarded When buffer (CS0103). The whole document must still compile.
    public void Lowered_StyleWhenDrop_RollsBackDeferredSideState()
    {
        // The FIRST condition lowers fine and records a deferred {x:Reference} whose target local lives in
        // the When buffer; the SECOND condition (no Binding) fails, dropping the whole style — so the
        // recorded reference must roll back, else the document-end flush references the discarded local.
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.WhenLeakView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"Gated\" TargetType=\"DatePicker\">" +
                "<Style.When>" +
                  "<DataCondition Binding=\"{Binding IsEditable}\">" +
                    "<DataCondition.Value>" +
                      "<Label Target=\"{x:Reference Ok}\"/>" + // records a deferred reference into the When buffer
                    "</DataCondition.Value>" +
                  "</DataCondition>" +
                  "<DataCondition/>" + // no Binding → unlowerable → the whole style drops
                "</Style.When>" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class WhenLeakView : StackPanel { public WhenLeakView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains("TODO X5", lowered); // style dropped

        // The document must still COMPILE — the discarded reference must not survive into the flush.
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(System.Activator.CreateInstance(assembly.GetType("GenApp.WhenLeakView")!));
    }

    [Fact] // One unlowerable <Style.When> condition drops the WHOLE style: a partially-gated style would
           // apply more broadly than authored (fail-open) — the inverse of the RequiresCapabilities rule.
    public void Lowered_StyleWhen_UnlowerableCondition_DropsWholeStyle()
    {
        // A DataCondition with no {Binding} descriptor is the canonical unlowerable form.
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.WhenDropView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"Gated\" TargetType=\"DatePicker\">" +
                "<Style.When>" +
                  "<DataCondition Value=\"{x:Null}\"/>" +
                "</Style.When>" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class WhenDropView : StackPanel { public WhenDropView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains("a <Style.When> condition is not lowerable", lowered);
        Assert.DoesNotContain("When.Add", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.WhenDropView")!)!;
        // Dropped WHOLE: an inert empty placeholder (zero setters ⇒ zero compiled rules) — never a
        // half-gated style that would apply more broadly than authored.
        var placeholder = Assert.IsType<Style>(view.Resources["Gated"]);
        Assert.Empty(placeholder.Setters);
        Assert.Empty(placeholder.When);
    }

    [Fact] // {x:Reference} inside a template resolves against the PER-BUILD template scope, flushed before the
           // factory returns (the loader's end-of-slice ResolveDeferredReferences) — forward refs included.
    public void Lowered_XReference_InsideTemplate_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.RefTplView\">" +
            "<StackPanel.Resources>" +
              "<DataTemplate x:Key=\"Tpl\">" +
                "<StackPanel>" +
                  "<Label Content=\"lbl\" Target=\"{x:Reference Input}\"/>" + // FORWARD reference
                  "<TextBox x:Name=\"Input\"/>" +
                "</StackPanel>" +
              "</DataTemplate>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class RefTplView : StackPanel { public RefTplView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.RefTplView")!)!;
        var tpl = Assert.IsType<DataTemplate>(view.Resources["Tpl"]);

        var builtRoot = Assert.IsType<StackPanel>(tpl.Build(null));
        var label = Assert.IsType<Label>(builtRoot.Children[0]);
        var input = Assert.IsType<TextBox>(builtRoot.Children[1]);
        Assert.Same(input, label.Target); // resolved within THIS build's scope

        // A second build resolves within ITS OWN scope — never the first build's elements.
        var secondRoot = Assert.IsType<StackPanel>(tpl.Build(null));
        var secondLabel = Assert.IsType<Label>(secondRoot.Children[0]);
        Assert.Same(secondRoot.Children[1], secondLabel.Target);
        Assert.NotSame(input, secondLabel.Target);

        // The loader builds the same shape.
        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml.Replace(" x:Class=\"GenApp.RefTplView\"", ""));
        var runtimeRoot = Assert.IsType<StackPanel>(Assert.IsType<DataTemplate>(runtime.Resources["Tpl"]).Build(null));
        Assert.Same(runtimeRoot.Children[1], Assert.IsType<Label>(runtimeRoot.Children[0]).Target);
    }

    [Fact] // {Binding Source={x:Reference X}} anchors on a named element: the whole Install defers past every
           // x:Name registration (Binding.Source is init-only — the loader's exact DeferNameResolution shape).
           // Previously the nested Source read as null text and SILENTLY dropped — the binding rebound
           // against DataContext with no marker.
    public void Lowered_BindingSource_XReference_DefersInstallPastRegistration()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.SrcRefView\">" +
            "<TextBlock Text=\"{Binding Source={x:Reference Src}, Path=Text}\"/>" + // forward reference
            "<TextBox x:Name=\"Src\" Text=\"hello\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SrcRefView : StackPanel { public SrcRefView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("NameScopeExtensions.Require(__scope, \"Src\")", lowered);
        // The Install embedding the scope lookup comes AFTER the name registration.
        Assert.True(lowered.IndexOf("__scope.Register(\"Src\"", System.StringComparison.Ordinal) <
                    lowered.IndexOf("NameScopeExtensions.Require(__scope, \"Src\")", System.StringComparison.Ordinal));

        // And the emitted view compiles + instantiates.
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(System.Activator.CreateInstance(assembly.GetType("GenApp.SrcRefView")!));
    }

    [Fact] // {Binding Source={x:Static …}} resolves eagerly to the member reference (the loader's
           // ResolveNestedExtension) — the other previously-silent nested-Source shape.
    public void Lowered_BindingSource_NestedXStatic_ResolvesEagerly()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:co=\"clr-namespace:Cursorial.Output;assembly=Cursorial.Core\" x:Class=\"GenApp.SrcStaticView\">" +
            "<TextBlock Text=\"{Binding Source={x:Static co:Colors.Red}, Path=R}\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class SrcStaticView : StackPanel { public SrcStaticView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("Source = global::Cursorial.Output.Colors.Red", lowered);
    }

    [Fact] // {TemplateBinding Property=Header} — the named form the loader accepts (WPF parity) alongside the
           // positional; previously only the positional lowered.
    public void Lowered_TemplateBinding_NamedPropertyForm()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.TbNamedView\">" +
            "<StackPanel.Resources>" +
              "<ControlTemplate x:Key=\"Tpl\" TargetType=\"HeaderedContentControl\">" +
                "<TextBlock Text=\"{TemplateBinding Property=Header}\"/>" +
              "</ControlTemplate>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class TbNamedView : StackPanel { public TbNamedView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.DoesNotContain("TODO X5", lowered);
        Assert.Contains("HeaderProperty", lowered);
        Assert.Contains("TemplateBinding", lowered);
    }

    [Fact] // A standalone element-form <Binding> in a value position is input the runtime LOADER rejects
           // (Fatal) — the lowered build now fails with an error-level marker instead of warn-and-skip.
    public void Lowered_StandaloneElementFormBinding_IsRejected()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.ElemBindingView\">" +
            "<Binding Path=\"Foo\"/>" +
            "</StackPanel>";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains("// ERROR X5", lowered);
    }

    [Fact] // An {x:Static} naming a member the type doesn't have degrades to the fail-closed TODO — the
           // generator must never emit a member access the C# compile of the view would then reject.
    public void Lowered_XStaticUnknownMember_FailsClosed()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:co=\"clr-namespace:Cursorial.Output;assembly=Cursorial.Core\" x:Class=\"GenApp.StaticMissView\">" +
            "<StackPanel.Resources>" +
              "<SolidColorBrush x:Key=\"Ink\" Color=\"{x:Static co:Colors.NotAColor}\"/>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class StaticMissView : StackPanel { public StaticMissView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains("TODO X5", lowered);
        Assert.DoesNotContain("NotAColor", string.Join("\n", lowered.Split('\n').Where(l => !l.Contains("TODO"))));

        // The lowered source still compiles and instantiates (the entry is dropped, not mangled).
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        Assert.NotNull(System.Activator.CreateInstance(assembly.GetType("GenApp.StaticMissView")!));
    }

    [Fact]
    public void Lowered_StyleWhen_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.WhenView\">" +
            "<StackPanel.Resources>" +
              "<Style x:Key=\"DPStyle\" TargetType=\"DatePicker\">" +
                "<Style.When>" +
                  "<DataCondition Binding=\"{Binding RelativeSource={RelativeSource Self}, Path=IsEditable}\">" +
                    "<DataCondition.Value><x:Boolean>false</x:Boolean></DataCondition.Value>" +
                  "</DataCondition>" +
                  "<DataCondition Binding=\"{Binding RelativeSource={RelativeSource Self}, Path=SelectedDate}\" Value=\"{x:Null}\" Negate=\"True\"/>" +
                "</Style.When>" +
                "<Setter Property=\"TextElement.Foreground\" Value=\"Red\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class WhenView : StackPanel { public WhenView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains(".When.Add(new global::Cursorial.UI.DataCondition", lowered); // the conjunction is emitted
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.WhenView")!)!;
        var loweredStyle = Assert.IsType<Style>(view.Resources["DPStyle"]);

        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);
        var runtimeStyle = Assert.IsType<Style>(runtime.Resources["DPStyle"]);

        // Same conjunction shape + typed values as the loader.
        Assert.Equal(runtimeStyle.When.Count, loweredStyle.When.Count);
        Assert.Equal(2, loweredStyle.When.Count);

        Assert.Equal(false, loweredStyle.When[0].Value);            // <x:Boolean>false</x:Boolean>
        Assert.False(loweredStyle.When[0].Negate);
        var b0 = Assert.IsType<Binding>(loweredStyle.When[0].Binding);
        Assert.Equal("IsEditable", b0.Path.Path);
        Assert.Equal(RelativeSourceMode.Self, b0.RelativeSource!.Mode);

        Assert.Null(loweredStyle.When[1].Value);                    // Value="{x:Null}"
        Assert.True(loweredStyle.When[1].Negate);                   // Negate="True"
        Assert.Equal("SelectedDate", ((Binding)loweredStyle.When[1].Binding).Path.Path);
    }

    [Fact] // Style.When — a DataCondition Value="{StaticResource …}" resolves eagerly (not dropped to null); matches the loader
    public void Lowered_StyleWhen_StaticResourceValue_MatchesLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.WhenResView\">" +
            "<StackPanel.Resources>" +
              "<x:String x:Key=\"Target\">special</x:String>" +
              "<Style x:Key=\"S\" TargetType=\"Button\">" +
                "<Style.When>" +
                  "<DataCondition Binding=\"{Binding Name}\" Value=\"{StaticResource Target}\"/>" +
                "</Style.When>" +
                "<Setter Property=\"MinWidth\" Value=\"20\"/>" +
              "</Style>" +
            "</StackPanel.Resources>" +
            "<Button x:Name=\"Ok\"/>" +
            "</StackPanel>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class WhenResView : StackPanel { public WhenResView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.DoesNotContain("TODO X5", lowered); // the {StaticResource} Value is lowered, not silently dropped

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.WhenResView")!)!;
        var loweredStyle = Assert.IsType<Style>(view.Resources["S"]);

        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);
        var runtimeStyle = Assert.IsType<Style>(runtime.Resources["S"]);

        // The condition's Value is the resolved resource ("special") in BOTH lanes — not a silent null.
        Assert.Equal("special", loweredStyle.When[0].Value);
        Assert.Equal(runtimeStyle.When[0].Value, loweredStyle.When[0].Value);
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

    [Fact] // #153 — a namespace-PREFIXED type-qualified binding path `(ui:Grid.Row)` in the reflective lane: the
    // emitted `new Binding("...")` carries no xmlns resolver, so the generator bakes the resolved owner into a
    // LookupPathTypeResolver so it binds `(ui:Grid.Row)` identically to the loader's xmlns-aware resolver.
    public void Lowered_PrefixedBindingPath_BakesResolvedOwnerResolver()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:ui=\"https://cursorial.dev/ui\" x:Class=\"TestApp.PrefixView\">" +
            "<Button x:Name=\"B\" Width=\"{Binding (ui:Grid.Row), Mode=OneWay}\"/>" +
            "</StackPanel>";

        var lowered = Lower(xaml, GeneratorHarness.ReferencedCompilation("PrefixRewriteHost"));

        // The raw prefixed path is preserved AND a resolver baking the resolved owner (Grid) is emitted.
        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"(ui:Grid.Row)\")", lowered);
        Assert.Contains(
            "TypeResolver = new global::Cursorial.UI.Data.LookupPathTypeResolver((\"ui:Grid\", typeof(global::Cursorial.UI.Controls.Grid)))",
            lowered);
        Assert.DoesNotContain("TODO X5", lowered);
    }

    [Fact] // #153 — end-to-end: the rewritten prefixed path binds live to the SAME owner as the runtime loader.
    public void Lowered_PrefixedBindingPath_BindsSameAsLoader()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:ui=\"https://cursorial.dev/ui\" x:Class=\"TestApp.PrefixBindView\">" +
            "<Button x:Name=\"B\" Width=\"{Binding (ui:Grid.Row), Mode=OneWay}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class PrefixBindView : StackPanel { public PrefixBindView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("PrefixBindHost");
        var lowered = Lower(xaml, compilation);
        var assembly = GeneratorHarness.EmitAndLoad(
            compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));

        // A DataContext UIElement carrying Grid.Row = 3 — the binding reads (Grid.Row) off it.
        var source = new Border();
        Grid.SetRow(source, 3);

        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("TestApp.PrefixBindView")!)!;
        var loweredButton = Assert.IsType<Button>(view.Children[0]);
        view.DataContext = source;

        // The loader builds the reference tree from the same XAML (reflection provider, xmlns-aware path resolver).
        var runtime = (StackPanel)new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);
        var runtimeButton = Assert.IsType<Button>(runtime.Children[0]);
        var runtimeSource = new Border();
        Grid.SetRow(runtimeSource, 3);
        runtime.DataContext = runtimeSource;

        Assert.Equal(3, loweredButton.Width);              // the rewritten (Grid.Row) resolved the owner and bound 3
        Assert.Equal(runtimeButton.Width, loweredButton.Width); // ... identically to the loader
    }

    [Fact] // #153 — an UNBOUND prefix can't be resolved at build: the binding still emits with the RAW path (the
    // runtime surfaces the same unresolved-token error the loader would) — no crash, no silent drop, no TODO.
    public void Lowered_UnboundPrefixBindingPath_KeepsRawPath()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" x:Class=\"TestApp.UnboundView\">" +
            "<Button Width=\"{Binding (zzz:Grid.Row), Mode=OneWay}\"/>" +
            "</StackPanel>";

        var lowered = Lower(xaml, GeneratorHarness.ReferencedCompilation("UnboundPrefixHost"));

        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"(zzz:Grid.Row)\")", lowered); // raw path preserved
        Assert.DoesNotContain("TODO X5", lowered);                                             // emitted, not dropped
    }

    [Fact] // #153 — a MIXED path keeps the raw path and bakes only the prefixed group's owner; plain hops are verbatim.
    public void Lowered_MixedBindingPath_BakesOnlyPrefixedOwner()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:ui=\"https://cursorial.dev/ui\" x:Class=\"TestApp.MixedPrefixView\">" +
            "<Button Width=\"{Binding Sub.(ui:Grid.Row), Mode=OneWay}\"/>" +
            "</StackPanel>";

        var lowered = Lower(xaml, GeneratorHarness.ReferencedCompilation("MixedPrefixHost"));

        Assert.Contains("new global::Cursorial.UI.Data.Binding(\"Sub.(ui:Grid.Row)\")", lowered);
        Assert.Contains("(\"ui:Grid\", typeof(global::Cursorial.UI.Controls.Grid))", lowered);
    }

    [Fact] // #153 (hardening) — a resolvable prefixed pattern INSIDE a string-indexer key must NOT be collected: the
    // scanner skips indexer contents, so NO resolver is baked (a naive scanner would resolve ui:Grid there).
    public void Lowered_IndexerKeyWithParens_NotCollected()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:ui=\"https://cursorial.dev/ui\" x:Class=\"TestApp.IndexerParenView\">" +
            "<Button Width=\"{Binding Map['a(ui:Grid.Row)'], Mode=OneWay}\"/>" +
            "</StackPanel>";

        var lowered = Lower(xaml, GeneratorHarness.ReferencedCompilation("IndexerParenHost"));

        Assert.Contains("Map['a(ui:Grid.Row)']", lowered);       // the indexer key is preserved verbatim
        Assert.DoesNotContain("LookupPathTypeResolver", lowered); // the parens inside the key were NOT treated as a segment
    }

    [Fact] // #153 (the audit's high finding) — a prefixed owner that registered NO UIProperty (a plain CLR property
    // qualified for clarity) is baked via typeof(...) too, so it resolves — where a simple-name rewrite would throw.
    public void Lowered_PrefixedBindingPath_UnregisteredOwner_BindsSameAsLoader()
    {
        var xaml =
            "<StackPanel xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\" " +
            "xmlns:t=\"using:TestApp\" x:Class=\"TestApp.UnregOwnerView\">" +
            "<TextBlock x:Name=\"Label\" Text=\"{Binding (t:Person.FullName), Mode=OneWay}\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp
{
    public sealed class Person { public string FullName { get; set; } = """"; }   // a plain VM — NO registered UIProperty
    public partial class UnregOwnerView : StackPanel { public UnregOwnerView() => InitializeComponent(); }
}";

        var compilation = GeneratorHarness.ReferencedCompilation("UnregOwnerHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = Lower(xaml, compilation);

        // The unregistered owner is baked by typeof — no simple-name rewrite (which would make the runtime throw).
        Assert.Contains("(\"t:Person\", typeof(global::TestApp.Person))", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("TestApp.UnregOwnerView")!)!;
        var label = Assert.IsType<TextBlock>(view.Children[0]);

        var personType = assembly.GetType("TestApp.Person")!;
        var person = System.Activator.CreateInstance(personType)!;
        personType.GetProperty("FullName")!.SetValue(person, "Ada");
        view.DataContext = person;

        Assert.Equal("Ada", label.Text); // the qualified plain CLR property resolved — the generated view did NOT throw
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
        Assert.Null(LoweringEmitter.Emit(document, "x.xaml", "x.xaml", new XamlSymbolResolver(compilation)));
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

    [Fact] // {x:Reference Name} lowers to a name-scope resolution matching the runtime loader (forward reference)
    public void Lowered_XReference_MatchesRuntimeLoader()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"TestApp.RefView\">" +
            "<Label x:Name=\"Fwd\" Target=\"{x:Reference Box}\"/>" + // forward ref — Box appears later
            "<TextBox x:Name=\"Box\"/>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class RefView : StackPanel { public RefView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("RefHost");
        var lowered = Lower(xaml, compilation);
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel) System.Activator.CreateInstance(assembly.GetType("TestApp.RefView")!)!;

        var runtime = (StackPanel) new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        Assert.Same(view.Children[1], ((Label) view.Children[0]).Target);       // lowered: x:Reference → the named TextBox
        Assert.Same(runtime.Children[1], ((Label) runtime.Children[0]).Target); // …matching the runtime loader
    }

    [Fact] // {Binding Converter={StaticResource Conv}} lowers the Converter (a same-dict resource), not a // TODO drop
    public void Lowered_BindingConverter_WiresSameDictResource()
    {
        var xaml =
            $"<StackPanel {Ns} xmlns:local=\"using:TestApp\" x:Class=\"TestApp.ConvView\">" +
            "<StackPanel.Resources><local:PassConverter x:Key=\"conv\"/></StackPanel.Resources>" +
            "<Border x:Name=\"B\" Width=\"{Binding Height, RelativeSource={RelativeSource Self}, Converter={StaticResource conv}}\"/>" +
            "</StackPanel>";

        const string code = @"
using System; using System.Globalization;
using Cursorial.UI.Controls; using Cursorial.UI.Data;
namespace TestApp {
  public sealed class PassConverter : IValueConverter {
    public object? Convert(object? v, Type t, object? p, CultureInfo c) => v;
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => v;
  }
  public partial class ConvView : StackPanel { public ConvView() => InitializeComponent(); }
}";

        // The converter + code-behind must be in the compilation BEFORE lowering, so the symbol resolver sees them.
        var compilation = GeneratorHarness.ReferencedCompilation("ConvHost").AddSyntaxTrees(CSharpSyntaxTree.ParseText(code));
        var lowered = Lower(xaml, compilation);
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel) System.Activator.CreateInstance(assembly.GetType("TestApp.ConvView")!)!;

        var border = (Border) view.Children[0];
        var binding = (Binding) BindingOperations.GetBindingExpression(border, UIElement.WidthProperty)!.ParentBinding!;
        Assert.NotNull(binding.Converter);                            // the Converter was lowered + set (not dropped to a TODO)
        Assert.Same(view.Resources["conv"], binding.Converter);       // …and it is the same-dictionary resource instance
    }

    [Fact] // XD27/XD28 — <x:Array Type="x:String"> with <x:String> items lowers to new string[]{…}, matching the loader
    public void Lowered_XArray_StringItems_MatchesLoader()
    {
        var xaml =
            $"<ListBox {Ns} x:Class=\"TestApp.ArrView\">" +
            "<ListBox.ItemsSource>" +
            "<x:Array Type=\"x:String\"><x:String>one</x:String><x:String>two</x:String></x:Array>" +
            "</ListBox.ItemsSource></ListBox>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class ArrView : ListBox { public ArrView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        Assert.Contains("new string[] {", lowered); // a typed array literal — not a TODO drop
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (ListBox) System.Activator.CreateInstance(assembly.GetType("TestApp.ArrView")!)!;

        var array = Assert.IsType<string[]>(view.ItemsSource);
        Assert.Equal(["one", "two"], array);

        var runtime = (ListBox) new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);
        Assert.Equal((string[]) runtime.ItemsSource!, array); // byte-identical to the reflective loader
    }

    [Fact] // an x:Array of x:Int32 lowers each item through the converter (new int[]{…}) and matches the loader
    public void Lowered_XArray_Int32Items_MatchesLoader()
    {
        var xaml =
            $"<ListBox {Ns} x:Class=\"TestApp.IntArrView\">" +
            "<ListBox.ItemsSource>" +
            "<x:Array Type=\"x:Int32\"><x:Int32>7</x:Int32><x:Int32>42</x:Int32></x:Array>" +
            "</ListBox.ItemsSource></ListBox>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class IntArrView : ListBox { public IntArrView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.Contains("new int[] {", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (ListBox) System.Activator.CreateInstance(assembly.GetType("TestApp.IntArrView")!)!;

        var array = Assert.IsType<int[]>(view.ItemsSource);
        Assert.Equal([7, 42], array);

        var runtime = (ListBox) new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);
        Assert.Equal((int[]) runtime.ItemsSource!, array);
    }

    // ── Audit regressions (the adversarial pass found these generator bugs; happy-path tests missed them) ──

    private const BindingFlags FieldFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact] // AUDIT: a NAMED <x:Array> must declare a T[] field (not T) and assign + register it (CS0029 before)
    public void Lowered_NamedXArray_FieldTypedArray_AndAssigned()
    {
        var xaml =
            $"<ListBox {Ns} x:Class=\"TestApp.NamedArrView\">" +
            "<ListBox.ItemsSource><x:Array x:Name=\"Names\" Type=\"x:String\"><x:String>a</x:String></x:Array></ListBox.ItemsSource></ListBox>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class NamedArrView : ListBox { public NamedArrView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.Contains("internal string[] Names", lowered); // field typed string[], not string (CS0029 otherwise)

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var viewType = assembly.GetType("TestApp.NamedArrView")!;
        var view = (ListBox) System.Activator.CreateInstance(viewType)!;

        var names = (string[]) viewType.GetField("Names", FieldFlags)!.GetValue(view)!;
        Assert.Equal(["a"], names);
        Assert.Same(view.ItemsSource, names);
    }

    [Fact] // AUDIT: a NAMED value-type built-in primitive compiles (default!, not null! → CS0037) + sets field + registers
    public void Lowered_NamedValueTypePrimitive_CompilesSetsFieldRegisters()
    {
        var xaml =
            $"<ContentControl {Ns} x:Class=\"TestApp.NamedIntView\">" +
            "<ContentControl.Content><x:Int32 x:Name=\"N\">42</x:Int32></ContentControl.Content></ContentControl>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class NamedIntView : ContentControl { public NamedIntView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.Contains("internal int N = default!;", lowered); // default!, not null! (CS0037 on a value type)

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var viewType = assembly.GetType("TestApp.NamedIntView")!;
        var view = (ContentControl) System.Activator.CreateInstance(viewType)!;

        Assert.Equal(42, viewType.GetField("N", FieldFlags)!.GetValue(view));          // field set
        Assert.Equal(42, Cursorial.UI.NameScope.GetNameScope(view)!.Find("N"));         // registered in the name scope
    }

    [Fact] // AUDIT: a NAMED reference-type built-in primitive sets its field + registers (hasScope was hardcoded false)
    public void Lowered_NamedStringPrimitive_SetsFieldAndRegisters()
    {
        var xaml =
            $"<ContentControl {Ns} x:Class=\"TestApp.NamedStrView\">" +
            "<ContentControl.Content><x:String x:Name=\"S\">hi</x:String></ContentControl.Content></ContentControl>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class NamedStrView : ContentControl { public NamedStrView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var viewType = assembly.GetType("TestApp.NamedStrView")!;
        var view = (ContentControl) System.Activator.CreateInstance(viewType)!;

        Assert.Equal("hi", viewType.GetField("S", FieldFlags)!.GetValue(view));         // field set (was null before)
        Assert.Equal("hi", Cursorial.UI.NameScope.GetNameScope(view)!.Find("S"));       // registered (missing before)
    }

    [Fact] // AUDIT: a built-in primitive with an embedded newline (xml:space=preserve) lowers to a COMPILING literal (CS1010 before)
    public void Lowered_PrimitiveWithEmbeddedNewline_Compiles()
    {
        var xaml =
            $"<ContentControl {Ns} x:Class=\"TestApp.NlView\">" +
            "<ContentControl.Content><x:String xml:space=\"preserve\">line1\nline2</x:String></ContentControl.Content></ContentControl>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class NlView : ContentControl { public NlView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        // The lowered literal escapes the newline (\n), so it compiles (a raw newline would be CS1010).
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (ContentControl) System.Activator.CreateInstance(assembly.GetType("TestApp.NlView")!)!;

        // … and round-trips to the same two-line string the reflection loader produces.
        var runtime = (ContentControl) new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);
        Assert.Equal(runtime.Content, view.Content);
        Assert.Contains("\n", (string) view.Content!);
    }

    [Fact] // an init-only CLR property set via a PROPERTY ELEMENT is hoisted into the construction initializer (was CS8852)
    public void Lowered_InitOnlyProperty_AsPropertyElement_CompilesAndMatchesLoader()
    {
        var xaml =
            $"<Border {Ns} x:Class=\"TestApp.PenView\">" +
            "<Border.BorderPen>" +
            "<Pen Weight=\"Heavy\"><Pen.Brush><SolidColorBrush Color=\"Red\"/></Pen.Brush></Pen>" +
            "</Border.BorderPen>" +
            "</Border>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class PenView : Border { public PenView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);

        // The init-only Pen.Brush (a property element) is hoisted into the object initializer — `new Pen { … Brush =
        // <child> }` — never a post-construction `<pen>.Brush = <child>` (CS8852).
        Assert.Contains("Brush =", lowered);
        Assert.DoesNotContain(".Brush =", lowered);

        // Compiles (no CS8852) ⇒ the init-only members went into initializers; instantiate + match the loader.
        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (Border) System.Activator.CreateInstance(assembly.GetType("TestApp.PenView")!)!;

        var runtime = (Border) new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        Assert.NotNull(view.BorderPen?.Brush);
        Assert.Equal(runtime.BorderPen!.Value.Brush!.GetType(), view.BorderPen!.Value.Brush!.GetType()); // SolidColorBrush
        Assert.Equal(runtime.BorderPen!.Value.Weight, view.BorderPen!.Value.Weight);                     // init-only attribute too
    }

    [Fact] // Classes="accent primary" lowers to Classes.Add per name (the read-only ClassSet — no setter)
    public void Lowered_Classes_AddsStyleClasses()
    {
        var xaml = $"<Button {Ns} x:Class=\"TestApp.ClassesView\" Classes=\"accent primary\"/>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class ClassesView : Button { public ClassesView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.Contains("Classes.Add(\"accent\")", lowered);
        Assert.Contains("Classes.Add(\"primary\")", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
        var view = (Button) System.Activator.CreateInstance(assembly.GetType("TestApp.ClassesView")!)!;

        var runtime = (Button) new XamlLoader(
            new XamlLoaderOptions { MetadataProvider = ReflectionXamlMetadata.Instance }).Load(xaml);

        Assert.Contains("accent", view.Classes);
        Assert.Contains("primary", view.Classes);
        Assert.Contains("accent", runtime.Classes); // same set as the loader
    }

    [Fact] // Classes inside a DataTemplate lowers to Classes.Add in the template factory (compiles)
    public void Lowered_Classes_InsideDataTemplate()
    {
        var xaml =
            $"<ContentControl {Ns} x:Class=\"TestApp.ClassesTemplateView\" Content=\"d\">" +
            "<ContentControl.ContentTemplate><DataTemplate>" +
            "<Button Classes=\"accent\"/>" +
            "</DataTemplate></ContentControl.ContentTemplate>" +
            "</ContentControl>";
        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace TestApp { public partial class ClassesTemplateView : ContentControl { public ClassesTemplateView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost");
        var lowered = Lower(xaml, compilation);
        Assert.Contains("Classes.Add(\"accent\")", lowered); // emitted inside the template factory

        // Compiles ⇒ the factory's Classes.Add is valid C#.
        GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(codeBehind), CSharpSyntaxTree.ParseText(lowered)));
    }
}
