using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.Xaml.XamlMatrix;

/// <summary>
/// §13.2–13.4 (X153–X168): the X3 build half — template instances/namescopes/provenance, lexical
/// resource-scope capture, access-key literal folding. The parse-half rows (X147–X152) are in
/// <see cref="Section13_DeferredContentParse"/>.
/// </summary>
public sealed class Section13_DeferredContentBuild : LoaderTestBase
{
    private static TemplateInstance InstantiateOn(ControlTemplate template, Control owner)
    {
        template.TargetType ??= typeof(Button);
        return template.Instantiate(owner);
    }

    // Applies a loaded ControlTemplate through the real Control.ApplyTemplate flow (which sets the
    // template scope on the templated parent), returning the owner.
    private static Button ApplyTo(ControlTemplate template)
    {
        template.TargetType ??= typeof(Button);
        var owner = new Button { Template = template };
        owner.ApplyTemplate();
        return owner;
    }

    [Fact] // X153
    public void X153_ControlTemplate_BuildsFreshSubtree()
    {
        var template = Load<ControlTemplate>("<ControlTemplate><Border><Button/></Border></ControlTemplate>");
        var owner = new Button();
        var instance = InstantiateOn(template, owner);
        var border = Assert.IsType<Border>(instance.Root);
        Assert.IsType<Button>(border.Child);
    }

    [Fact] // X154
    public void X154_DoubleBuild_DistinctInstances()
    {
        var template = Load<ControlTemplate>("<ControlTemplate><Border/></ControlTemplate>");
        var first = InstantiateOn(template, new Button());
        var second = InstantiateOn(template, new Button());
        Assert.NotSame(first.Root, second.Root); // distinct element instances (XD20)
    }

    [Fact] // X155
    public void X155_TemplatePartName_RegistersInTemplateScope()
    {
        var template = Load<ControlTemplate>("<ControlTemplate><Border x:Name=\"PART_Border\"/></ControlTemplate>");
        var owner = new Button();
        var instance = InstantiateOn(template, owner);
        // The part name registers in the per-Build template scope only (not the document scope, C-16).
        Assert.Same(instance.Root, instance.NameScope.Find("PART_Border"));
    }

    [Fact] // X156
    public void X156_TemplateScope_CarriedOnTemplatedParent()
    {
        var template = Load<ControlTemplate>("<ControlTemplate><Border x:Name=\"PART_Border\"/></ControlTemplate>");
        var owner = ApplyTo(template);
        // ApplyTemplate attaches the template scope on the templated parent (NameScope.SetTemplateNameScope).
        var scope = NameScope.GetTemplateNameScope(owner);
        Assert.NotNull(scope);
        var part = scope.RequireControl<Border>("PART_Border");
        Assert.Same(owner.TemplateInstance!.Root, part);
    }

    [Fact] // X157
    public void X157_DocumentChild_vs_TemplatePart_NoLeak()
    {
        // A document name and a template-part name with the same string do not leak across scopes.
        var docRoot = Load<Border>("<Border x:Name=\"Shared\"/>");
        Assert.Same(docRoot, NameScope.GetNameScope(docRoot)!.Find("Shared"));

        var template = Load<ControlTemplate>("<ControlTemplate><Border x:Name=\"Shared\"/></ControlTemplate>");
        var owner = new Button();
        var instance = InstantiateOn(template, owner);
        // The template part registered only in the template scope, never the document scope.
        Assert.Same(instance.Root, instance.NameScope.Find("Shared"));
        Assert.Null(NameScope.GetNameScope(instance.Root)); // no document scope on a template part
    }

    [Fact] // X158
    public void X158_DocumentLocalValue_OverridesTemplateShipped()
    {
        // A template sets Background; a document-level set on the SAME part overrides it (LocalValue > template).
        var template = Load<ControlTemplate>(
            "<ControlTemplate><Border Background=\"#ff0000\"/></ControlTemplate>");
        var owner = new Button();
        var instance = InstantiateOn(template, owner);
        var border = (Border)instance.Root;
        // The template-built value is present at LocalValue (the loader's value-source).
        Assert.Equal(BindingPriority.LocalValue, border.GetValueSource(Border.BackgroundProperty).Priority);
    }

    [Fact] // X160
    public void X160_TemplateBinding_TracksTemplatedParent()
    {
        var template = Load<ControlTemplate>(
            "<ControlTemplate><Border Background=\"{TemplateBinding Background}\"/></ControlTemplate>");
        var owner = new Button { Background = new TestBrush { Color = Color.FromRgb(1, 2, 3) } };
        var instance = InstantiateOn(template, owner);
        var part = (Border)instance.Root;
        Assert.Same(owner.Background, part.Background);
    }

    [Fact] // X161
    public void X161_TargetTypeMismatch_Throws()
    {
        var template = Load<ControlTemplate>("<ControlTemplate><Border/></ControlTemplate>");
        template.TargetType = typeof(Button);
        // Applying to a non-Button control throws (the ControlTemplate.Instantiate TargetType check).
        Assert.Throws<InvalidOperationException>(() => template.Instantiate(new CheckBox()));
    }

    [Fact] // X162 / X163
    public void X162_LexicalResourceCapture_DefinitionSiteWins()
    {
        // The template is defined inside Border.Resources with AccentBrush; its body uses
        // {StaticResource AccentBrush}. The captured lexical scope is the DEFINITION-site chain.
        var border = Load<Border>(
            "<Border>" +
            "<Border.Resources>" +
            "<TestBrush x:Key=\"AccentBrush\" Color=\"#ff0000\"/>" +
            "<ControlTemplate x:Key=\"T\"><Border Background=\"{StaticResource AccentBrush}\"/></ControlTemplate>" +
            "</Border.Resources>" +
            "</Border>");

        var template = (ControlTemplate)border.Resources["T"]!;
        var owner = new Button();
        var instance = InstantiateOn(template, owner);
        var part = (Border)instance.Root;
        Assert.IsType<TestBrush>(part.Background);
        Assert.Equal(Color.FromRgb(255, 0, 0), ((TestBrush)part.Background!).Color);
    }

    [Fact] // X164
    public void X164_TemplateOwnResources_ShadowDefinitionSite()
    {
        // The template's own ControlTemplate.Resources shadow a definition-site key of the same name.
        var border = Load<Border>(
            "<Border>" +
            "<Border.Resources>" +
            "<TestBrush x:Key=\"Accent\" Color=\"#ff0000\"/>" +
            "<ControlTemplate x:Key=\"T\">" +
            "<ControlTemplate.Resources><TestBrush x:Key=\"Accent\" Color=\"#0000ff\"/></ControlTemplate.Resources>" +
            "<Border Background=\"{StaticResource Accent}\"/>" +
            "</ControlTemplate>" +
            "</Border.Resources>" +
            "</Border>");

        var template = (ControlTemplate)border.Resources["T"]!;
        var instance = InstantiateOn(template, new Button());
        var part = (Border)instance.Root;
        // The template's own Resources win (innermost in the captured chain).
        Assert.Equal(Color.FromRgb(0, 0, 255), ((TestBrush)part.Background!).Color);
    }

    // ── §13.4 Access-key literal folding (XD11) ────────────────────────────────────────────────────

    [Fact] // X165
    public void X165_ButtonContent_FoldsAccessKey()
    {
        var button = Load<Button>("<Button Content=\"_Run\"/>");
        // The loader keeps the raw string; the three-identical-producers rule folds via AccessText.Parse.
        Assert.Equal(AccessText.Parse("_Run"), AccessText.Parse((string)button.Content!));
        Assert.Equal(new AccessText("Run", 'R', 0), AccessText.Parse((string)button.Content!));
    }

    [Fact] // X166
    public void X166_TextBlockText_NeverFolds()
    {
        var block = Load<TextBlock>("<TextBlock Text=\"_Run\"/>");
        Assert.Equal("_Run", block.Text); // verbatim — TextBlock.Text is unflagged
    }

    [Fact] // X167
    public void X167_LabelContent_FoldsAccessKey()
    {
        var label = Load<Label>("<Label Content=\"_File\"/>");
        Assert.Equal(new AccessText("File", 'F', 0), AccessText.Parse((string)label.Content!));
    }

    [Theory] // X168
    [InlineData("__Verbatim", "_Verbatim", '\0', -1)]
    [InlineData("_!bang", "_!bang", '\0', -1)]
    public void X168_AccessKeyEscapes(string content, string text, char key, int keyIndex)
    {
        var button = Load<Button>($"<Button Content=\"{content}\"/>");
        Assert.Equal(new AccessText(text, key, keyIndex), AccessText.Parse((string)button.Content!));
    }
}
