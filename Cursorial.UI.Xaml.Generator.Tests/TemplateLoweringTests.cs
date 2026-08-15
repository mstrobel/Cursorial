using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.UI.Controls;

using Microsoft.CodeAnalysis.CSharp;

namespace Cursorial.Tests.UI.Xaml.Generator;

/// <summary>
/// WS-X5.4 — template lowering. A deferred template body (<c>ControlTemplate</c>/<c>DataTemplate</c> Content)
/// lowers to a static local-function factory wrapped in a <c>FuncTemplateContent</c> that builds a fresh
/// subtree per instantiation, with <c>x:Name</c> parts registering into the per-build template name scope
/// (never the document scope). These tests lower a real template, compile it, instantiate it, and assert the
/// factory produced the templated tree.
/// </summary>
public class TemplateLoweringTests
{
    private const string Ns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    [Fact] // a ControlTemplate body lowers to a FuncTemplateContent factory that builds the part on Instantiate
    public void Lowered_ControlTemplate_BuildsViaFactory()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.TplView\">" +
            "<Button x:Name=\"Btn\">" +
              "<Button.Template>" +
                "<ControlTemplate><Border x:Name=\"PART_Chrome\"/></ControlTemplate>" +
              "</Button.Template>" +
            "</Button>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class TplView : StackPanel { public TplView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The deferred body lowered to a factory + FuncTemplateContent; the part name registers into the template scope.
        Assert.Contains("static global::Cursorial.UI.UIElement __Factory0(global::Cursorial.UI.Controls.TemplateBuildContext __ctx)", lowered);
        Assert.Contains("new global::Cursorial.UI.Controls.FuncTemplateContent(__Factory0)", lowered);
        Assert.Contains("__ctx.RegisterName(\"PART_Chrome\"", lowered);
        Assert.DoesNotContain("this.PART_Chrome", lowered); // a template-scope name is NOT a document field
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.TplView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);

        // The ControlTemplate was assigned; instantiating it runs the factory and produces a fresh Border part.
        var template = Assert.IsType<ControlTemplate>(button.Template);
        var instance = template.Instantiate(button);
        var part = Assert.IsType<Border>(instance.Root);
        Assert.Equal("PART_Chrome", part.Name);          // x:Name registered into the template scope (sets Name)
        Assert.Same(button, instance.Root.TemplatedParent); // built under the owner as TemplatedParent

        // A second instantiation produces a DISTINCT subtree (fresh per Build — no shared elements).
        var instance2 = template.Instantiate(button);
        Assert.NotSame(instance.Root, instance2.Root);
    }

    [Fact] // {TemplateBinding} inside a ControlTemplate one-way-tracks the templated parent's property
    public void Lowered_TemplateBinding_TracksTemplatedParent()
    {
        var xaml =
            $"<StackPanel {Ns} x:Class=\"GenApp.TbView\">" +
            "<Button x:Name=\"Btn\">" +
              "<Button.Template>" +
                "<ControlTemplate><Border x:Name=\"PART_Chrome\" Background=\"{TemplateBinding Background}\"/></ControlTemplate>" +
              "</Button.Template>" +
            "</Button>" +
            "</StackPanel>";

        const string codeBehind = @"
using Cursorial.UI.Controls;
namespace GenApp { public partial class TbView : StackPanel { public TbView() => InitializeComponent(); } }";

        var compilation = GeneratorHarness.ReferencedCompilation("LoweringHost")
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(codeBehind));
        var lowered = GeneratorHarness.LowerView(compilation, xaml);

        // The source property resolved against the template target (Button → Control.BackgroundProperty).
        Assert.Contains("new global::Cursorial.UI.Data.TemplateBinding(", lowered);
        Assert.Contains("BackgroundProperty));", lowered);
        Assert.DoesNotContain("TODO X5", lowered);

        var assembly = GeneratorHarness.EmitAndLoad(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(lowered)));
        var view = (StackPanel)System.Activator.CreateInstance(assembly.GetType("GenApp.TbView")!)!;
        var button = Assert.IsType<Button>(view.Children[0]);

        var brush = new SolidColorBrush(Color.FromRgb(7, 8, 9));
        button.Background = brush;

        // Instantiating stamps TemplatedParent on the part; the TemplateBinding one-way-tracks Button.Background.
        var instance = button.Template!.Instantiate(button);
        var border = Assert.IsType<Border>(instance.Root);
        Assert.Same(brush, border.Background);
    }
}
