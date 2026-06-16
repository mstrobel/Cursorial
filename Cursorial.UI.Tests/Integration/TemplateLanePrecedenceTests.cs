using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Testing;

using Style = Cursorial.UI.Style;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// End-to-end proof of the Template lane (precedence-matrix §20 / PD24) through the <em>real</em>
/// <see cref="ControlTemplate"/> machinery: a value a template authors on a part (a literal, a
/// <c>{TemplateBinding}</c>) lands at <see cref="BindingPriority.Template"/> — one rung below Style —
/// so an applied Style overrides it. This is the regression guard for the reported bug ("a Style on
/// the close button's Background never applied unless the template Background binding was removed").
/// </summary>
public sealed class TemplateLanePrecedenceTests
{
    private sealed class Shell : ContentControl;

    private static readonly IBrush TemplateBrush = new SolidColorBrush(Color.FromRgb(10, 10, 10));
    private static readonly IBrush StyleBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90));
    private static readonly IBrush ControlBrush = new SolidColorBrush(Color.FromRgb(50, 50, 50));

    [Fact] // a template literal on a part lands at Template (proves ControlTemplate.Instantiate opens the scope)
    public void TemplateLiteral_OnPart_LandsAtTemplate()
    {
        Border? part = null;
        var shell = new Shell
        {
            Template = new ControlTemplate(_ => part = new Border { Background = TemplateBrush }),
        };

        using var host = UITestHost.Create();
        host.ShowRoot(shell);
        host.RunFrame(); // ApplyTemplate → ControlTemplate.Instantiate (the scope) builds the part

        Assert.NotNull(part);
        Assert.Equal(BindingPriority.Template, part!.GetValueSource(Border.BackgroundProperty).Priority);
        Assert.Same(TemplateBrush, part.Background);
    }

    [Fact] // a {TemplateBinding} forwarding the control's Background to a part also lands at Template
    public void TemplateBinding_OnPart_LandsAtTemplate()
    {
        Border? part = null;
        var shell = new Shell
        {
            Background = ControlBrush,
            Template = new ControlTemplate(_ =>
            {
                part = new Border();
                part.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                return part;
            }),
        };

        using var host = UITestHost.Create();
        host.ShowRoot(shell);
        host.RunFrame();

        Assert.NotNull(part);
        Assert.Same(ControlBrush, part!.Background); // the forwarded value
        Assert.Equal(BindingPriority.Template, part.GetValueSource(Border.BackgroundProperty).Priority);
    }

    [Fact] // a SetResourceReference inside a template lands at Template with TemplateResource provenance (PD25)
    public void TemplateResource_OnPart_LandsAtTemplate()
    {
        var resourceBrush = new SolidColorBrush(Color.FromRgb(7, 7, 7));
        Border? part = null;
        var shell = new Shell
        {
            Template = new ControlTemplate(_ =>
            {
                part = new Border();
                part.SetResourceReference(Border.BackgroundProperty, "TemplateLaneBrush");
                return part;
            }),
        };

        using var host = UITestHost.Create();
        host.Application.Resources["TemplateLaneBrush"] = resourceBrush;
        host.ShowRoot(shell);
        host.RunFrame();

        Assert.NotNull(part);
        Assert.Same(resourceBrush, part!.Background);
        var source = part.GetValueSource(Border.BackgroundProperty);
        Assert.Equal(BindingPriority.Template, source.Priority);
        Assert.Equal(ValueSourceKind.TemplateResource, source.Kind);
    }

    [Fact] // the reported repro: a Style crossing the template barrier overrides a part's template literal
    public void Style_OverridesTemplateLiteral_OnPart()
    {
        Border? part = null;
        var shell = new Shell
        {
            Template = new ControlTemplate(_ => part = new Border { Background = TemplateBrush }),
        };

        // The page Style: a Border that is a template child of a Shell (the sanctioned /template/ crossing).
        var style = new Style(Selectors.OfType<Shell>().Template().OfType<Border>())
            .Set(Border.BackgroundProperty, StyleBrush);

        using var host = UITestHost.Create();
        host.Application.Styles.Add(style);
        host.ShowRoot(shell);
        host.RunFrame();

        Assert.NotNull(part);
        // The fix: Style (the page rule) beats Template (the part's literal). Before the lane existed
        // the literal sat at LocalValue and the Style could not win — the reported bug.
        Assert.Equal(BindingPriority.Style, part!.GetValueSource(Border.BackgroundProperty).Priority);
        Assert.Same(StyleBrush, part.Background);
    }
}
