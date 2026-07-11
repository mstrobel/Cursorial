using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using static Cursorial.Tests.UI.StyleMatrix.StyleMatrixFixture;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.StyleMatrix;

/// <summary>
/// Style matrix §5 (cont.) — where a template-part rule belongs (#115). The engine reaches a template
/// part's <c>^ /template/ part</c> rule through the part-arming pass, which consumes
/// <see cref="ControlTemplate.Styles"/> (the <c>Template(1)</c> layer, CD30). A control theme is a
/// selector-less <c>^</c>-rooted <see cref="Style"/> armed <b>element-addressed</b> against the control
/// itself; a multi-compound reach-in rule placed in its <c>Children</c> can never match — it is dropped
/// with a DEBUG diagnostic rather than silently ignored.
/// </summary>
public class Section05_PartRuleAuthoring
{
    // A theme that roots the control's template at a single named Widget part, with the accent rule
    // authored where <paramref name="inTemplateStyles"/> chooses: the template's Styles (canonical) or
    // the theme Style's Children (the footgun).
    private static Style PartThemingTheme(bool inTemplateStyles)
    {
        var template = new ControlTemplate(ctx =>
        {
            var part = new Widget();
            ctx.RegisterName("part", part);
            return part;
        });

        var partRule = new Style(Selectors.Nesting().Template().OfType<Widget>()).Set(Widget.P, 5);

        if (inTemplateStyles)
            template.Styles.Add(partRule);

        var theme = new Style { Key = "Theme.PartHost" }.Set(Control.TemplateProperty, template);
        if (!inTemplateStyles)
            theme.Children.Add(partRule);
        return theme;
    }

    private static Widget ShowPartHost(Style theme, out UIHeadlessHost host)
    {
        host = UIHeadlessHost.Create();
        var button = new Button { Theme = theme }; // the Theme override wins the effective control theme (CD13)
        host.ShowRoot(button);
        host.RunFrame();
        return (Widget)button.TemplateInstance!.NameScope.Find("part")!;
    }

    [Fact] // #115 — the sanctioned home: a part rule in ControlTemplate.Styles arms at Template(1) and matches.
    public void S089_PartRuleInTemplateStyles_MatchesThePart()
    {
        var diagnostics = new List<string>();
        Action<string, string> handler = (category, _) => diagnostics.Add(category);
        StyleDebugDiagnostics.DiagnosticEmitted += handler;
        try
        {
            var part = ShowPartHost(PartThemingTheme(inTemplateStyles: true), out var host);
            using var _ = host;

            Assert.Equal(5, part.GetValue(Widget.P)); // the /template/ rule crossed into the part

            var armed = Assert.Single(StyleDiagnostics.MatchedRules(part));
            Assert.Equal("^ /template/ Widget", armed.SelectorText);
            Assert.Equal(StyleLayer.Template, armed.Layer);
            Assert.True(armed.IsActive);

            Assert.DoesNotContain(StyleDebugDiagnostics.ElementAddressedReachInCategory, diagnostics);
        }
        finally
        {
            StyleDebugDiagnostics.DiagnosticEmitted -= handler;
        }
    }

    [Fact] // #115 — the footgun: a part rule in the control theme's Children is element-addressed → never matches + warns.
    public void S090_PartRuleInControlThemeChildren_DoesNotMatch_AndWarns()
    {
        var diagnostics = new List<string>();
        Action<string, string> handler = (category, _) => diagnostics.Add(category);
        StyleDebugDiagnostics.DiagnosticEmitted += handler;
        try
        {
            var part = ShowPartHost(PartThemingTheme(inTemplateStyles: false), out var host);
            using var _ = host;

            // Zero frames — no matching style; the value falls to the Default lane (the #115 root cause:
            // not precedence, an outright non-match).
            Assert.Equal(0, part.GetValue(Widget.P));
            Assert.Empty(StyleDiagnostics.MatchedRules(part));
            Assert.Equal(new ValueSource(BindingPriority.Default, false), part.GetValueSource(Widget.P));

#if DEBUG
            Assert.Contains(StyleDebugDiagnostics.ElementAddressedReachInCategory, diagnostics);
#else
            Assert.DoesNotContain(StyleDebugDiagnostics.ElementAddressedReachInCategory, diagnostics);
#endif
        }
        finally
        {
            StyleDebugDiagnostics.DiagnosticEmitted -= handler;
        }
    }
}
