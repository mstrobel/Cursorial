using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Bars;

/// <summary>
/// The code-first control themes for the bar controls, reusing the existing <see cref="ThemeKeys"/> spine so the
/// bars track the active palette and dark/light flips. Each bar control installs its theme as a type-default
/// <see cref="Control.Theme"/> (the explicit per-control override the style engine prefers over the resource-chain
/// lookup), so the suite renders self-contained — no consumer dictionary merge — over <c>CursorialTheme.BuiltIn</c>.
/// (A XAML theme-overlay twin, and the split/popup tinted-zone + Ribbon tokens, follow in later phases.)
/// </summary>
internal static class CursorialBarsTheme
{
    // ───────────────────────────── BarButton / BarToggleButton ─────────────────────────────

    // The shared bar-button face: a Background-filled Border (padding 1,0) over [icon] [label]. The fill IS the
    // state (no resting border) — :pointerover/:pressed flip the Background, :checked flips to the accent fill, and
    // the label/icon inherit Foreground. The label ContentPresenter auto-aliases the button's Content (access-key
    // literals folded); the icon presenter shows the (shared-identity) Icon property.
    private static ControlTemplate BarItemTemplate() => new(ctx =>
    {
        var border = new Border();
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        border.SetBinding(Border.PaddingProperty, new TemplateBinding(Control.PaddingProperty));

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var icon = new ContentPresenter();
        icon.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(BarButton.IconProperty));
        icon.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));

        var label = new ContentPresenter { RecognizesAccessKey = true };
        label.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));

        row.Children.Add(icon);
        row.Children.Add(label);
        border.Child = row;
        return border;
    });

    // A bar button is flat on the toolbar at rest (no resting Background — the surface shows through); only the
    // interactive states fill, using the Button-specific brush keys (style-guide KEYS) so the bars re-skin in step
    // with regular buttons. The toggle's checked "on" look is the accent whole-cell fill (the guide's toggle state).
    public static Style BarButtonStyle()
    {
        var theme = new Style { Key = "Bars.BarButton" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, BarItemTemplate());
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundHover));
        theme.Children.Add(new Style("^:pressed")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundDisabled));
        return theme;
    }

    public static Style BarToggleButtonStyle()
    {
        var theme = new Style { Key = "Bars.BarToggleButton" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, BarItemTemplate());
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundHover));
        // Checked = the accent whole-cell fill (the guide's toggle "on" look), text inverted to on-accent.
        theme.Children.Add(new Style("^:checked")
            .SetResource(Control.BackgroundProperty, ThemeKeys.AccentBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundDisabled));
        return theme;
    }

    // ───────────────────────────── BarSeparator ─────────────────────────────

    public static Style SeparatorStyle()
        => new Style { Key = "Bars.BarSeparator" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.FaintBrush)
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var glyph = new TextBlock { Text = "│" };
                glyph.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                return glyph;
            }));

    // ───────────────────────────── Toolbar ─────────────────────────────

    public static Style ToolbarStyle()
        => new Style { Key = "Bars.Toolbar" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.PanelBrush)
            .Set(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(static _ => new StackPanel { Orientation = Orientation.Horizontal }))
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var host = new ItemsPresenter();
                ctx.RegisterName("PART_ItemsHost", host);
                var border = new Border { Child = host };
                border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                return border;
            }));
}
