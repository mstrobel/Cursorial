using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Hosting.Headless;

using Style = Cursorial.UI.Style;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// End-to-end proof of the Template lane (precedence-matrix §20 / PD24 as amended 2026-07-12)
/// through the <em>real</em> <see cref="ControlTemplate"/> machinery: a value a template authors on
/// a part (a literal, a <c>{TemplateBinding}</c>) lands at <see cref="BindingPriority.Template"/> —
/// below the conditional <see cref="BindingPriority.StyleTrigger"/> slot but ABOVE resting
/// <see cref="BindingPriority.Style"/>. A resting page rule cannot wreck template wiring; a
/// conditional (pseudo-class/.class/When-gated) rule pierces while active and retracts cleanly.
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

        using var host = UIHeadlessHost.Create();
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

        using var host = UIHeadlessHost.Create();
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

        using var host = UIHeadlessHost.Create();
        host.Application.Resources["TemplateLaneBrush"] = resourceBrush;
        host.ShowRoot(shell);
        host.RunFrame();

        Assert.NotNull(part);
        Assert.Same(resourceBrush, part!.Background);
        var source = part.GetValueSource(Border.BackgroundProperty);
        Assert.Equal(BindingPriority.Template, source.Priority);
        Assert.Equal(ValueSourceKind.TemplateResource, source.Kind);
    }

    [Fact] // re-pinned 2026-07-12: a RESTING /template/ rule no longer overrides a part's template literal
    public void RestingStyle_DoesNotOverrideTemplateLiteral_OnPart()
    {
        Border? part = null;
        var shell = new Shell
        {
            Template = new ControlTemplate(_ => part = new Border { Background = TemplateBrush }),
        };

        // The resting page rule: a Border that is a template child of a Shell (the sanctioned
        // /template/ crossing) — purely structural, so it arbitrates BELOW the Template lane.
        var style = new Style(Selectors.OfType<Shell>().Template().OfType<Border>())
            .Set(Border.BackgroundProperty, StyleBrush);

        using var host = UIHeadlessHost.Create();
        host.Application.Styles.Add(style);
        host.ShowRoot(shell);
        host.RunFrame();

        Assert.NotNull(part);
        // The completed lattice (§0.3, 2026-07-12): the template literal is the part's resting
        // truth — the resting rule is masked. Re-skinning at rest styles the CONTROL's property
        // (the {TemplateBinding} forwarding spine) or uses a conditional rule (the test below).
        Assert.Equal(BindingPriority.Template, part!.GetValueSource(Border.BackgroundProperty).Priority);
        Assert.Same(TemplateBrush, part.Background);
    }

    [Fact] // the trigger direction: a CLASS-gated /template/ rule pierces the literal while active
    public void ConditionalStyle_OverridesTemplateLiteral_OnPart_WhileActive()
    {
        Border? part = null;
        var shell = new Shell
        {
            Template = new ControlTemplate(_ => part = new Border { Background = TemplateBrush }),
        };

        // Class-gated ⇒ conditional ⇒ the StyleTrigger slot (§0.3): pierces the Template lane while
        // the class is present and retracts cleanly back to the literal when it leaves.
        var style = new Style(Selectors.OfType<Shell>().Class("alert").Template().OfType<Border>())
            .Set(Border.BackgroundProperty, StyleBrush);

        using var host = UIHeadlessHost.Create();
        host.Application.Styles.Add(style);
        host.ShowRoot(shell);
        host.RunFrame();

        Assert.NotNull(part);
        Assert.Same(TemplateBrush, part!.Background); // resting: the literal holds

        shell.Classes.Add("alert");
        host.RunFrame();
        Assert.Same(StyleBrush, part.Background); // active: the conditional rule pierces
        Assert.Equal(BindingPriority.StyleTrigger, part.GetValueSource(Border.BackgroundProperty).Priority);

        shell.Classes.Remove("alert");
        host.RunFrame();
        Assert.Same(TemplateBrush, part.Background); // clean retraction to the literal
        Assert.Equal(BindingPriority.Template, part.GetValueSource(Border.BackgroundProperty).Priority);
    }
}
