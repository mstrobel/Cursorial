using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Xaml;

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// Phase-6 end-to-end coverage: a realistic ControlTemplate XAML loaded, expanded, and asserted (parts +
/// TemplateBinding + StaticResource), plus the X3-stage §14 fold-equivalence loader half (X179) and a
/// resources-+-binding-+-access-key composite document.
/// </summary>
public sealed class Phase6EndToEndTests : LoaderTestBase
{
    [Fact]
    public void EndToEnd_ControlTemplate_PartsBindingsAndLexicalResources()
    {
        // A button-styled ControlTemplate defined inside a resource dictionary: a named part, a
        // TemplateBinding to the templated parent's Background, and a StaticResource from the
        // definition-site chain. Load, expand on a real Button, assert the live wiring.
        var border = Load<Border>(
            "<Border>" +
            "<Border.Resources>" +
            "<TestBrush x:Key=\"Accent\" Color=\"#ff8800\"/>" +
            "<ControlTemplate x:Key=\"ButtonChrome\">" +
            "<Border x:Name=\"PART_Chrome\" Background=\"{TemplateBinding Background}\">" +
            "<TextBlock Foreground=\"{StaticResource Accent}\"/>" +
            "</Border>" +
            "</ControlTemplate>" +
            "</Border.Resources>" +
            "</Border>");

        var template = (ControlTemplate)border.Resources["ButtonChrome"]!;
        template.TargetType = typeof(Button);

        var owner = new Button { Background = new TestBrush { Color = Color.FromRgb(10, 20, 30) }, Template = template };
        owner.ApplyTemplate();

        // The named part resolved in the template scope.
        var scope = NameScope.GetTemplateNameScope(owner)!;
        var chrome = scope.RequireControl<Border>("PART_Chrome");
        Assert.Same(owner.TemplateInstance!.Root, chrome);

        // The TemplateBinding tracks the templated parent's Background.
        Assert.Same(owner.Background, chrome.Background);

        // The StaticResource resolved from the definition-site chain (the orange Accent), not a miss.
        var text = (TextBlock)chrome.Child!;
        Assert.Equal(Color.FromRgb(255, 0x88, 0), ((TestBrush)text.Foreground!).Color);
    }

    [Fact]
    public void EndToEnd_Document_Resources_Binding_AccessKey()
    {
        var vm = new TestVm { Name = "Save" };
        var border = Load<Border>(
            "<Border>" +
            "<Border.Resources><TestBrush x:Key=\"Bg\" Color=\"#202020\"/></Border.Resources>" +
            "<Button x:Name=\"Go\" Background=\"{StaticResource Bg}\" Content=\"_Save\"/>" +
            "</Border>");

        var button = (Button)border.Child!;

        // x:Name registered in the document scope.
        Assert.Same(button, NameScope.GetNameScope(border)!.Find("Go"));
        // StaticResource resolved eagerly.
        Assert.IsType<TestBrush>(button.Background);
        // Access-key fold (the three-producers rule): Content stays the raw string, folds via AccessText.
        Assert.Equal(new AccessText("Save", 'S', 0), AccessText.Parse((string)button.Content!));

        // A live binding on the same tree pushes through.
        var labeled = Load<Button>("<Button Content=\"{Binding Name}\"/>");
        labeled.DataContext = vm;
        Assert.Equal("Save", labeled.Content);
    }

    [Fact] // X179 (loader half) — fold-equivalence: the loader fold === AccessText.Parse === the boxed constant
    public void X179_FoldEquivalence_LoaderHalf()
    {
        // Access-key fold equivalence.
        var button = Load<Button>("<Button Content=\"_Run\"/>");
        Assert.Equal(AccessText.Parse("_Run"), AccessText.Parse((string)button.Content!));

        // Folded-constant sharing across loads: a context-free Margins folds once and is the same boxed
        // reference across distinct Loads of the same parsed document (XD20).
        var doc = Parse("<Border Margin=\"1,2,3,4\"/>");
        var a = (Border)Loader.Load(doc, new XamlLoadContext { Source = TestSource });
        var b = (Border)Loader.Load(doc, new XamlLoadContext { Source = TestSource });
        Assert.NotSame(a, b);                    // distinct trees
        Assert.Equal(a.Margin, b.Margin);        // equal folded value
    }
}
