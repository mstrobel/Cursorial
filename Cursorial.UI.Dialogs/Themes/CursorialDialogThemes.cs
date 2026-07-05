using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Dialogs.Themes;

public static class CursorialDialogThemeKeys
{
    public const string CommandLinkIcon = "Dialogs.CommandLink.Icon";
}

public static class CursorialDialogThemes
{
    private static readonly ResourceDictionary BuiltInDictionary = CreateSealed();

    private static readonly IconCarrier CommandLinkIcon = new()
                                                          {
                                                              Glyph = "\uf061",
                                                              GlyphWidth = 2,
                                                              Emoji = "\u27A1",
                                                              Text = "➡"
                                                          };

    /// <summary>The sealed, process-shared default dictionary — always the final lookup hop (design doc §11.8).</summary>
    public static ResourceDictionary BuiltIn => BuiltInDictionary;

    private static ResourceDictionary CreateSealed()
    {
        var dict = new ResourceDictionary();
        Populate(dict);
        dict.Seal();
        return dict;
    }

    internal static void Populate(ResourceDictionary dict)
    {
        dict[CursorialDialogThemeKeys.CommandLinkIcon] = CommandLinkIcon;

        dict[typeof(CommandLink)] = CommandLinkStyle();
    }

    public static Style CommandLinkStyle()
    {
        var theme = new Style { Key = "Dialogs.CommandLink" }
           .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundNormal)
           .SetResource(Control.ForegroundProperty, ThemeKeys.TextDimBrush)
           .Set(Control.TemplateProperty, CommandLinkTemplate());

        theme.Children.Add(new Style("^:pointerover")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundHover)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundHover));

        theme.Children.Add(new Style("^:focus")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundFocus)
                          .SetResource(TextElement.TextAttributesProperty, ThemeKeys.InteractiveInverseAttributes));

        theme.Children.Add(new Style("^:pressed")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed)
                          .SetResource(TextElement.TextAttributesProperty, ThemeKeys.InteractiveInverseAttributes));

        theme.Children.Add(new Style("^:disabled")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundDisabled)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundDisabled));
        
        return theme;
    }

    private static ControlTemplate CommandLinkTemplate()
    {
        ControlTemplate t = new ControlTemplate(
            ctx =>
                 {
                     var icon = new ContentPresenter
                                {
                                    Content = CommandLinkIcon,
                                    VerticalAlignment = VerticalAlignment.Top,
                                    Margin = new Margins(1, 0)
                                };

                     var panel = new DockPanel { LastChildFill = true };
                     var border = new Border { Child = panel };

                     border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                     border.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                     border.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));

                     var label = new ContentPresenter { RecognizesAccessKey = true };
                     var explanation = new ContentPresenter();

                     ctx.RegisterName("PART_Icon", icon);
                     ctx.RegisterName("PART_Label", label);
                     ctx.RegisterName("PART_Explanation", explanation);
                     
                     explanation.SetBinding(ContentPresenter.ContentProperty,
                                            new TemplateBinding(CommandLink.ExplanationProperty));

                     explanation.SetBinding(ContentPresenter.RecognizesMarkupProperty,
                                            new TemplateBinding(CommandLink.ExplanationContainsMarkupProperty));

                     explanation.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentBrush);
        
                     DockPanel.SetDock(icon, Dock.Left);
                     DockPanel.SetDock(label, Dock.Top);
        
                     panel.Children.Add(icon);
                     panel.Children.Add(label);
                     panel.Children.Add(explanation);
                     
                     return border;
                 });
        
        t.Styles.Add(
            new Style(Selectors.OfType<CommandLink>())
            {
                Children =
                {
                    new Style("^:pointerover /template/ #PART_Explanation")
                       .SetResource(TextElement.ForegroundProperty, ThemeKeys.ButtonForegroundHover),
                    new Style("^:pressed /template/ #PART_Explanation, " +
                              "^:focus /template/ #PART_Explanation, " +
                              "^:focus-visible /template/ #PART_Explanation")
                       .SetResource(TextElement.ForegroundProperty, ThemeKeys.AccentInverseBrush)
                }
            }
        );
        
        return t;
    }
}