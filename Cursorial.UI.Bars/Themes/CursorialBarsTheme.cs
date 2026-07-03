using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;
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

        // Medium / Small (the default): the horizontal [icon][label] face — the Toolbar bar-button face verbatim.
        // With no ribbon size context this is the ONLY visible face (the large face collapses via the size-cascade rules), so a
        // Toolbar button renders byte-for-byte as before.
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var icon = new ContentPresenter();
        icon.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(BarButton.IconProperty));
        icon.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));

        var label = new ContentPresenter { RecognizesAccessKey = true };
        label.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));

        row.Children.Add(icon);
        row.Children.Add(label);
        ctx.RegisterName(PartMediumLabel, label);
        ctx.RegisterName(PartSmallMediumFace, row);

        // Large (the ribbon glyph-over-label form): a centered vertical [glyph][label], glyph re-inked --accent-2.
        // Hidden until :size-large; a collapsed face contributes nothing so Medium is unaffected.
        var largeGlyph = new ContentPresenter { HorizontalAlignment = HorizontalAlignment.Center };
        largeGlyph.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(BarButton.IconProperty));
        // TODO: Revisit large glyph color; currently it clashes horribly with the :focus-visible background.
        // largeGlyph.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.Accent2Brush);
        largeGlyph.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));

        var largeLabel = new ContentPresenter { RecognizesAccessKey = true, HorizontalAlignment = HorizontalAlignment.Center };
        largeLabel.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));

        var largeCol = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        largeCol.Children.Add(largeGlyph);
        largeCol.Children.Add(largeLabel);
        ctx.RegisterName(PartLargeFace, largeCol);

        border.Child = new Grid { Children = { row, largeCol } };

        // The size cascade lives in the TEMPLATE's own styles (part-targeting /template/ rules must — the TabItem /
        // obscured-overlay precedent). The LARGE face is hidden by default (Medium/Small show the [icon][label] row);
        // a :size-large control swaps to it. Because Medium collapses the large face, a Medium control renders
        // byte-identically to the plain Toolbar face.
        border.Styles.Add(new Style(Selectors.Is<ButtonBase>())
        {
            Children =
            {
                new Style(Selectors.Nesting().Template().Name(PartLargeFace))
                    .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                new Style(Selectors.Nesting().PseudoClass(":size-small").Template().Name(PartMediumLabel))
                    .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                new Style(Selectors.Nesting().PseudoClass(":size-large").Template().Name(PartLargeFace))
                    .Set(UIElement.VisibilityProperty, Visibility.Visible),
                new Style(Selectors.Nesting().PseudoClass(":size-large").Template().Name(PartSmallMediumFace))
                    .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
            },
        });
        return border;
    });

    private const string PartSmallMediumFace = "PART_SmallMediumFace";
    private const string PartMediumLabel = "PART_MediumLabel";
    private const string PartLargeFace = "PART_LargeFace";

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
        theme.Children.Add(new Style("^:focus")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundFocus)
            .SetResource(TextElement.TextAttributesProperty, ThemeKeys.InteractiveInverseAttributes));
        theme.Children.Add(new Style("^:pressed")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed)
            .SetResource(TextElement.TextAttributesProperty, ThemeKeys.InteractiveInverseAttributes));
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
        theme.Children.Add(new Style("^:focus")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundFocus)
                          .SetResource(TextElement.TextAttributesProperty, ThemeKeys.InteractiveInverseAttributes));
        // Checked = the accent whole-cell fill (the guide's toggle "on" look), text inverted to on-accent.
        theme.Children.Add(new Style("^:checked")
            .SetResource(Control.BackgroundProperty, ThemeKeys.AccentBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundDisabled));
        return theme;
    }

    // ───────────────────────────── BarSeparator ─────────────────────────────

    // A thin faint rule (BarSeparator renders a │ / ─ glyph run colored by Foreground — orientation-aware, no glyph
    // template, robust for a single-cell-tall bar row where a DrawLine rule degenerates).
    public static Style SeparatorStyle()
        => new Style { Key = "Bars.BarSeparator" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.FaintBrush);

    // ───────────────────────────── BarLabel ─────────────────────────────

    public static Style BarLabelStyle()
        => new Style { Key = "Bars.BarLabel" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var border = new Border();
                border.SetBinding(Border.PaddingProperty, new TemplateBinding(Control.PaddingProperty));
                var label = new ContentPresenter { RecognizesAccessKey = true }; // auto-aliases the caption Content
                label.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                border.Child = label;
                return border;
            }));

    // ───────────────────────────── Toolbar + overflow chevron ─────────────────────────────

    // The overflow chevron ('»'): a bare bar-button-faced toggle docked at the row's right edge, shown only when
    // something overflows. It reuses the Button-specific brush keys so it tracks the bar buttons' hover/pressed look.
    private static Style OverflowChevronStyle()
    {
        var theme = new Style { Key = "Bars.OverflowChevron" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var border = new Border();
                border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                border.SetBinding(Border.PaddingProperty, new TemplateBinding(Control.PaddingProperty));
                var label = new ContentPresenter(); // auto-aliases the button's Content ("»")
                label.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                border.Child = label;
                return border;
            }));
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundHover));
        // Keyboard-only focus look (:focus-visible — never on pointer focus): the focus brush-pair fill only.
        // The no-color cue is NOT hand-rolled here (inverse video belongs only to .caps-nocolor) — the chevron is a
        // plain Button, so the shared `.caps-nocolor Button:focus` reverse-video layer (CapsNoColorInteractiveInverse)
        // already covers it. Setting Inverse here too would double-invert over the focus fill under color.
        theme.Children.Add(new Style("^:focus-visible")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundFocus));
        theme.Children.Add(new Style("^:pressed")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed));
        return theme;
    }

    public static Style ToolbarStyle()
        => new Style { Key = "Bars.Toolbar" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.ToolBarBrush)
            // The overflow engine: a ToolbarOverflowPanel splits the live containers between the row and the popup.
            .Set(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(static _ => new ToolbarOverflowPanel()))
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                // The row host (fills the bar). The panel reserves the chevron's width when it folds, so the row
                // content stops short of the right edge where the (overlaid) chevron sits.
                var itemsHost = new ItemsPresenter();
                ctx.RegisterName("PART_ItemsHost", itemsHost);

                // The chevron — overlaid at the row's right edge (a shared Grid cell, so it never steals row width).
                // Idle Visibility is Hidden (still measured — the panel needs its width), flipped Visible on overflow.
                var chevron = new Button
                {
                    Content = "»",
                    Theme = OverflowChevronStyle(),
                    Focusable = true,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Visibility = Visibility.Hidden,
                };
                // The chevron is a drop-OPENER, not a command: a retaining focus scope so the toolbar's return
                // doesn't yank focus to the editor when it opens the popup (FindReturningScope barrier). Full
                // keyboard entry into the overflow popup is the bars keyboard-navigation follow-up.
                FocusManager.SetIsFocusScope(chevron, true);
                ctx.RegisterName("PART_OverflowToggle", chevron);

                // The overflow band: a vertical items-host StackPanel inside the popup. IsItemsHost makes its Children
                // adopt visual-only, so the overflowed live controls stay logical children of the Toolbar.
                var overflowHost = new StackPanel { Orientation = Orientation.Vertical, IsItemsHost = true };
                // Up/Down navigate between overflowed items inside the popup (directional scoring on the vertical
                // stack); the chevron↔popup boundary hops are explicit in Toolbar.OnKeyDown (cross-surface).
                KeyboardNavigation.SetDirectionalNavigation(overflowHost, DirectionalNavigationMode.Cycle);
                ctx.RegisterName("PART_OverflowHost", overflowHost);
                var popupBorder = new Border { Child = overflowHost };
                popupBorder.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
                popupBorder.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
                var popup = new Popup { Child = popupBorder };
                ctx.RegisterName("PART_OverflowPopup", popup);

                var grid = new Grid(); // single cell: items host (fill) + chevron (right) + popup (0×0)
                grid.Children.Add(itemsHost);
                grid.Children.Add(chevron);
                grid.Children.Add(popup);

                var border = new Border { Child = grid };
                border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                return border;
            }));

    // ───────────────────────────── BarPopupButton / BarSplitButton ─────────────────────────────

    // The dropdown Popup (PART_Popup): a bordered elevation surface hosting the button's DropDownContent. The content
    // presenter is a NAMED PART (PART_DropDownContent) whose Content the control sets in code — a TemplateBinding
    // inside Popup.Child does not resolve (the popup-child subtree carries no TemplatedParent stamp).
    private static Popup BuildDropDownPopup(TemplateBuildContext ctx)
    {
        var content = new ContentPresenter();
        ctx.RegisterName("PART_DropDownContent", content);
        var popupBorder = new Border { Child = content };
        popupBorder.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
        popupBorder.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
        var popup = new Popup { Child = popupBorder };
        ctx.RegisterName("PART_Popup", popup);
        return popup;
    }

    private static ContentPresenter BuildIcon()
    {
        var icon = new ContentPresenter();
        icon.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(BarDropDownButton.IconProperty));
        icon.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
        return icon;
    }

    // The whole-control popup-button face: a Background-filled Border over [icon] [label] [ ▾ caret].
    public static Style BarPopupButtonStyle()
    {
        var theme = new Style { Key = "Bars.BarPopupButton" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var border = new Border();
                border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                border.SetBinding(Border.PaddingProperty, new TemplateBinding(Control.PaddingProperty));

                var row = new DockPanel { LastChildFill = true };
                var label = new ContentPresenter { RecognizesAccessKey = true };
                label.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                var caret = new TextBlock { Margin = new Margins(1, 0, 0, 0) }; // leading gap (was the space in " ▾")
                caret.SetBinding(TextBlock.TextProperty, new TemplateBinding(BarDropDownButton.CaretGlyphProperty));
                caret.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));

                var icon = BuildIcon();
                
                DockPanel.SetDock(icon, Dock.Left);

                BindingOperations.SetBinding(
                    caret,
                    DockPanel.DockProperty,
                    new Binding(nameof(BarPopupButton.DropDownPlacement))
                    {
                        RelativeSource = RelativeSource.TemplatedParent,
                        Converter = ValueConverter.Create((value, _, _, _) =>
                                                          {
                                                              return value switch
                                                                     {
                                                                         PlacementMode.Left => Dock.Left,
                                                                         _                  => Dock.Right
                                                                     };
                                                          })
                    }
                );

                // Order matters with LastChildFill: the LAST child FILLS the remaining space and its Dock is IGNORED.
                // The caret must therefore NOT be last (that's why its placement-driven Dock never applied on the popup
                // button, unlike the split button which adds its ▾ zone first). Add the caret first (Dock honored →
                // right for Bottom, left for a Left placement), then the icon (Dock.Left), and the LABEL last so IT is
                // the fill child that takes the middle — mirroring the split button's dropZone-first / primary-last order.
                row.Children.Add(caret);
                row.Children.Add(icon);
                row.Children.Add(label);
                border.Child = row;

                var grid = new Grid();
                grid.Children.Add(border);
                grid.Children.Add(BuildDropDownPopup(ctx));
                return grid;
            }));
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundHover));
        theme.Children.Add(new Style("^:focus")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundFocus));
        theme.Children.Add(new Style("^:pressed")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed));
        theme.Children.Add(new Style("^:open").SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)); // active while its dropdown is open
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundDisabled));
        return theme;
    }

    // The two-zone split-button face: a [icon] [label] PRIMARY zone (runs the action) + a tinted ▾ zone (PART_DropDown)
    // that opens the dropdown. The interactive fills apply to the primary zone; the ▾ zone owns its own tint.
    public static Style BarSplitButtonStyle()
    {
        var theme = new Style { Key = "Bars.BarSplitButton" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundNormal)
            .Set(Control.PaddingProperty, new Margins(0, 0)) // the zones own their own padding
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var primary = new Border { Padding = new Margins(1, 0) };
                primary.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var label = new ContentPresenter { RecognizesAccessKey = true };
                label.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                row.Children.Add(BuildIcon());
                row.Children.Add(label);
                primary.Child = row;

                var dropZone = new Button
                {
                    Focusable = false, // a mouse target only — Down on the split button opens the dropdown by keyboard
                    Theme = DropZoneStyle(),
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                dropZone.SetBinding(ContentControl.ContentProperty, new TemplateBinding(BarDropDownButton.CaretGlyphProperty));
                ctx.RegisterName("PART_DropDown", dropZone);

                var band = new DockPanel { LastChildFill = true};

                BindingOperations.SetBinding(
                    dropZone,
                    DockPanel.DockProperty,
                    new Binding(nameof(BarSplitButton.DropDownPlacement))
                    {
                        RelativeSource = RelativeSource.TemplatedParent,
                        Converter = ValueConverter.Create((value, _, _, _) =>
                                                          {
                                                              return value switch
                                                                     {
                                                                         PlacementMode.Left => Dock.Left,
                                                                         _                  => Dock.Right
                                                                     };
                                                          })
                    }
                );

                band.Children.Add(dropZone);
                band.Children.Add(primary);

                var grid = new Grid();
                grid.Children.Add(band);
                grid.Children.Add(BuildDropDownPopup(ctx));
                return grid;
            }));
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundHover));
        theme.Children.Add(new Style("^:focus")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundFocus));
        theme.Children.Add(new Style("^:pressed")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundDisabled));
        return theme;
    }

    // The split button's ▾ zone: a tinted caret button (resting tint = the guide's --ddzone), non-focusable.
    private static Style DropZoneStyle()
    {
        var theme = new Style { Key = "Bars.SplitDropZone" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundNormal)
            .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundHover)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var border = new Border();
                border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                border.SetBinding(Border.PaddingProperty, new TemplateBinding(Control.PaddingProperty));
                var caret = new ContentPresenter();
                caret.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                border.Child = caret;
                return border;
            }));
        theme.Children.Add(new Style("^Button:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed));
        theme.Children.Add(new Style("^Button:pressed").SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed));
        return theme;
    }

    // ───────────────────────────── BarComboBox ─────────────────────────────

    // The bar combobox face: a FLAT recessed field (a WellBrush fill, NO chrome border — the bar deviates from the
    // default ComboBox's bordered field) over [ value | ▾ ], dropping the standard ComboBox list (ElevationPopup +
    // ComboBoxItem rows — the built-in item theme). Provides the ComboBox template parts (content site, editable text
    // box, drop caret, items host, popup) so the inherited ComboBox behavior works unchanged.
    public static Style BarComboBoxStyle()
    {
        var theme = new Style { Key = "Bars.BarComboBox" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var selected = new ContentPresenter(); // the read-only face value (visible when !IsEditable)
                ctx.RegisterName("PART_ContentSite", selected);
                selected.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(ComboBox.SelectionBoxItemProperty));
                selected.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));

                var editable = new TextBox { Visibility = Visibility.Collapsed }; // the editable face (visible when IsEditable)
                ctx.RegisterName("PART_EditableTextBox", editable);
                editable.SetBinding(TextBox.PlaceholderProperty, new TemplateBinding(ComboBox.PlaceholderTextProperty));
                editable.SetBinding(TextBox.IsReadOnlyProperty, new TemplateBinding(ComboBox.IsReadOnlyProperty));

                var faceContent = new Grid { Margin = new Margins(1, 0) }; // the two faces overlap; the collapsed one is 0-wide
                faceContent.Children.Add(selected);
                faceContent.Children.Add(editable);

                var caret = new Button { Focusable = false, IsTabStop = false, Theme = ComboCaretStyle() };
                ctx.RegisterName("PART_DropDown", caret); // the ComboBox wires its Click to toggle the list
                // The caret docks right (▾) for the default Bottom placement and LEFT (◂) when the combo is folded into a
                // vertical overflow menu (DropDownPlacement == Left), pointing toward its side flyout — matching the
                // drop-opener buttons. (The caret is added FIRST so it is not the LastChildFill child and its Dock is
                // honored; faceContent is the fill.)
                BindingOperations.SetBinding(caret, DockPanel.DockProperty,
                    new Binding(nameof(ComboBox.DropDownPlacement))
                    {
                        RelativeSource = RelativeSource.TemplatedParent,
                        Converter = ValueConverter.Create((value, _, _, _) => value is PlacementMode.Left ? Dock.Left : Dock.Right),
                    });
                BindingOperations.SetBinding(caret, ContentControl.ContentProperty,
                    new Binding(nameof(ComboBox.DropDownPlacement))
                    {
                        RelativeSource = RelativeSource.TemplatedParent,
                        Converter = ValueConverter.Create((value, _, _, _) => value is PlacementMode.Left ? "◂" : "▾"),
                    });

                var row = new DockPanel();
                row.Children.Add(caret);        // docked right (Bottom) / left (Left placement) — the ▾ / ◂ toggle
                row.Children.Add(faceContent);  // fills the remaining width

                var face = new Border { Child = row }; // FLAT: no BorderPen
                face.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));

                var host = new ItemsPresenter();
                ctx.RegisterName("PART_ItemsHost", host);
                var list = new Border { Child = host };
                list.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
                list.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
                list.SetBinding(UIElement.MaxHeightProperty, new TemplateBinding(ComboBox.MaxDropDownHeightProperty));
                var popup = new Popup { Child = list };
                ctx.RegisterName("PART_Popup", popup);

                var rootGrid = new Grid(); // the Popup adds no layout (0×0); the face fills the cell
                rootGrid.Children.Add(face);
                rootGrid.Children.Add(popup);
                return rootGrid;
            }));
        // A focused field gets the input-focus fill (the face Border binds Background to Control.Background). Also
        // covers BarGallery (a BarComboBox-themed ComboBox), so both show a keyboard-focus cue.
        theme.Children.Add(new Style("^:focus")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundFocus));
        theme.Children.Add(new Style("^:pointerover")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundHover)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundHover));
        theme.Children.Add(new Style("^:disabled")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.DisabledBackgroundBrush)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

    // A bare 1-cell ▾ caret button for the combobox face — no chrome, foreground-inheriting, non-focusable.
    private static Style ComboCaretStyle()
        => new Style { Key = "Bars.ComboCaret" }
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var caret = new ContentPresenter();
                caret.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                return caret;
            }));

    // ───────────────────────────── MiniToolbar ─────────────────────────────

    // The right-click floating strip: an elevation-popup Border over a horizontal ItemsPresenter (the hosted bar
    // controls). No resting fill on the bar controls themselves — the strip's ElevationPopup panel is the surface.
    public static Style MiniToolbarStyle()
        => new Style { Key = "Bars.MiniToolbar" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.ElevationPopup)
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var host = new ItemsPresenter();
                ctx.RegisterName("PART_ItemsHost", host);
                var border = new Border { Child = host };
                border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                border.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
                return border;
            }));

    // ───────────────────────────── Ribbon (Surface B) ─────────────────────────────

    // The ribbon frame: a tab strip (--tabstrip recess) docked over the selected tab's band (--ribbon/--surface). It
    // re-skins the inherited TabControl strip/content-host split — the selected RibbonTab's Content (its groups band)
    // shows in PART_ContentHost, exactly as a TabControl hosts the selected tab's content.
    public static Style RibbonStyle()
    {
        var template = new ControlTemplate(ctx =>
        {
            // ── The trailing QAT/pin column: the compact ribbon spends no caption row — the QAT fills the tab strip's
            // right-side slack instead (bars guide §"QAT placement in the compact ribbon"). The strip is two rows tall
            // (tab LABEL over selection UNDERLINE); this column mirrors that: the QAT cluster sits on the label row,
            // the minimize pin on the underline row, both right-hugging the strip's dead space. ──
            var qatAbove = new Toolbar();
            ctx.RegisterName("PART_QuickAccessAbove", qatAbove);

            var customize = new BarButton { Content = "▾" };
            ctx.RegisterName("PART_QatCustomize", customize);

            var checklistHost = new StackPanel { Orientation = Orientation.Vertical };
            // Up/Down move between the candidate rows once the checklist is keyboard-entered (Ribbon.OnQatPopupOpened
            // drops focus into the first row on a keyboard open); Contained keeps arrows inside the vertical list.
            KeyboardNavigation.SetDirectionalNavigation(checklistHost, DirectionalNavigationMode.Contained);
            ctx.RegisterName("PART_QatChecklistHost", checklistHost);
            var checklistBorder = new Border { Child = checklistHost };
            checklistBorder.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
            checklistBorder.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
            // KeepOpenOnAnchorPress: the ▾ owns the open/close toggle (Ribbon.OnQatCustomizeClick), so a press on it
            // while open must NOT light-dismiss (else the press closes it and the release-Click reopens it — the
            // dismiss-then-reopen race Toolbar/BarDropDownButton guard against; without it the ▾ can never close by mouse).
            var qatPopup = new Popup
            {
                Child = checklistBorder, PlacementTarget = customize, Placement = PlacementMode.Bottom, KeepOpenOnAnchorPress = true,
            };
            ctx.RegisterName("PART_QatPopup", qatPopup);

            // The QAT cluster (label row): the QAT commands + the customize ▾, as a right-hugging unit.
            var qatCluster = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            qatCluster.Children.Add(qatAbove);   // the QAT commands
            qatCluster.Children.Add(customize);  // the customize ▾
            qatCluster.Children.Add(qatPopup);   // logical-only (0 size in the strip layout)
            ctx.RegisterName("PART_QatCluster", qatCluster);

            // The collapse-first ⋯▾ (bars guide §"QAT placement in the compact ribbon" [DECISION]): when the tabs and
            // the inline QAT would compete for cells, RibbonStripPanel stamps :qat-collapsed and the inline cluster
            // hides for this popup button, which drops the QAT commands (re-hosted by the ribbon into PART_QuickAccess-
            // Collapsed) as a floating mini-bar. Tabs keep their cells; the QAT is one click away.
            var qatCollapsedBar = new Toolbar();
            ctx.RegisterName("PART_QuickAccessCollapsed", qatCollapsedBar);
            var qatCollapsedSurface = new Border { Child = qatCollapsedBar };
            qatCollapsedSurface.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
            qatCollapsedSurface.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
            var qatCollapsedButton = new BarPopupButton { Content = "⋯", DropDownContent = qatCollapsedSurface };
            ctx.RegisterName("PART_QatCollapsed", qatCollapsedButton);

            var pin = new BarButton { Content = "⌃", Focusable = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Right };
            ctx.RegisterName("PART_PinButton", pin);

            // ── Tab strip: left-packed tabs FILL the left; the trailing QAT/pin column right-hugs the strip's dead space
            // (RibbonStripPanel: QAT on the label row, pin on the underline row). The itemsHost is added FIRST so the
            // tabs precede the QAT in tab order (tabbing INTO the ribbon lands on a tab). The panel owns the collapse-
            // first fold: the inline cluster gives way to ⋯▾ before the tabs ever clip. ──
            var itemsHost = new ItemsPresenter();
            ctx.RegisterName("PART_ItemsHost", itemsHost);
            var stripInner = new RibbonStripPanel();
            stripInner.Children.Add(itemsHost);          // FIRST ⇒ precedes the QAT in tab order
            stripInner.Children.Add(qatCluster);         // inline QAT (label row)
            stripInner.Children.Add(qatCollapsedButton); // ⋯▾ (label row, shown only when :qat-collapsed)
            stripInner.Children.Add(pin);                // minimize pin (underline row)
            stripInner.Tabs = itemsHost;
            stripInner.QatFull = qatCluster;
            stripInner.QatCollapsed = qatCollapsedButton;
            stripInner.Pin = pin;
            ctx.RegisterName("PART_Strip", stripInner);
            var strip = new Border { Child = stripInner, Occludes = true };
            strip.SetResourceReference(Border.BackgroundProperty, ThemeKeys.RibbonTabStripBrush);
            DockPanel.SetDock(strip, Dock.Top);

            // ── Below-ribbon QAT band (shown only when :qat-below) ──
            var qatBelow = new Toolbar();
            ctx.RegisterName("PART_QuickAccessBelow", qatBelow);
            var belowBand = new Border { Child = qatBelow, Occludes = true };
            belowBand.SetResourceReference(Border.BackgroundProperty, ThemeKeys.RibbonTabStripBrush);
            ctx.RegisterName("PART_QatBelowBand", belowBand);
            DockPanel.SetDock(belowBand, Dock.Bottom);

            // ── Body (the selected tab's band) ──
            var content = new ContentPresenter();
            ctx.RegisterName("PART_ContentHost", content);
            content.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(TabControl.SelectedContentProperty));
            content.SetBinding(ContentPresenter.ContentTemplateProperty, new TemplateBinding(TabControl.ContentTemplateProperty));
            content.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);
            var body = new Border { Padding = new Margins(1, 0), Child = content, Occludes = true };
            ctx.RegisterName("PART_Body", body);
            body.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            DockPanel.SetDock(body, Dock.Bottom);

            var panel = new DockPanel();
            panel.Children.Add(strip);     // Dock.Top — the tab strip (with the trailing QAT/pin column)
            panel.Children.Add(belowBand); // Dock.Bottom — bottommost (below the body)
            panel.Children.Add(body);      // Dock.Bottom — above belowBand, below strip

            // Part-targeting rules live in the template's own styles under a type-anchored wrapper (the RibbonTabStyle
            // precedent). The caption/below-band defaults are Collapsed via a plain rule; a :has-qat / :qat-below
            // compound rule (higher classLike specificity) flips it Visible — NOT a local Visibility set, which
            // (LocalValue lane) would out-rank and defeat these Style rules. The wrapper is Is<Ribbon> (ASSIGNABLE),
            // NOT OfType<Ribbon> (exact): a Ribbon SUBCLASS (e.g. an app's GalleryRibbon) must still match, or its
            // :minimized body-collapse / :has-qat caption / :qat-below rules silently never apply.
            panel.Styles.Add(new Style(Selectors.Is<Ribbon>())
            {
                Children =
                {
                    // The trailing QAT cluster shows only when the ribbon populated the QAT (:has-qat); else collapsed.
                    new Style(Selectors.Nesting().Template().Name("PART_QatCluster"))
                        .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                    new Style(Selectors.Nesting().PseudoClass(":has-qat").Template().Name("PART_QatCluster"))
                        .Set(UIElement.VisibilityProperty, Visibility.Visible),
                    // NOTE: :qat-below does NOT collapse the cluster — it moves the COMMANDS to the below band (the
                    // generator re-hosts them, leaving qatAbove empty), so the cluster keeps just the customize ▾ at its
                    // stable top-right home. Collapsing the whole cluster here would strand the ▾ (and its checklist
                    // popup), leaving below-placement with no way to customize the QAT.
                    // The now-empty above toolbar IS collapsed, though — an empty Toolbar still reserves ~3 cells for its
                    // Hidden overflow chevron and paints its fill there, a discolored box left of the ▾ (Collapsed
                    // measures 0; Hidden does not). Collapsing PART_QuickAccessAbove specifically drops the box while the
                    // ▾ stays. (Above/uncollapsed, qatAbove hosts the commands, so :has-qat leaves it Visible.)
                    new Style(Selectors.Nesting().PseudoClass(":qat-below").Template().Name("PART_QuickAccessAbove"))
                        .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                    // :qat-collapsed (the strip is too tight — RibbonStripPanel's collapse-first verdict, gated by the
                    // ribbon on has-qat + Above placement) hides the inline cluster for the ⋯▾ popup button. Ordered
                    // AFTER :has-qat so it wins the PART_QatCluster contest at equal specificity (1 pseudo + 1 name).
                    new Style(Selectors.Nesting().PseudoClass(":qat-collapsed").Template().Name("PART_QatCluster"))
                        .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                    // The ⋯▾ collapse button is hidden by default and shown only under :qat-collapsed (the ribbon never
                    // stamps it without an inline QAT to collapse, so it can't appear on a QAT-less ribbon).
                    new Style(Selectors.Nesting().Template().Name("PART_QatCollapsed"))
                        .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                    new Style(Selectors.Nesting().PseudoClass(":qat-collapsed").Template().Name("PART_QatCollapsed"))
                        .Set(UIElement.VisibilityProperty, Visibility.Visible),
                    new Style(Selectors.Nesting().Template().Name("PART_QatBelowBand"))
                        .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                    new Style(Selectors.Nesting().PseudoClass(":has-qat").PseudoClass(":qat-below").Template().Name("PART_QatBelowBand"))
                        .Set(UIElement.VisibilityProperty, Visibility.Visible),
                    // Minimized: collapse the body band, leaving the tab strip (with its trailing QAT/pin) only.
                    new Style(Selectors.Nesting().PseudoClass(":minimized").Template().Name("PART_Body"))
                        .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                    // …but a FLOATED minimized band (transient keyboard/click reveal — Ribbon.FloatBand) shows the body:
                    // this 2-pseudo rule out-specifies the 1-pseudo collapse above, so :floating wins WITHOUT clearing
                    // IsMinimized. The float re-collapses (Esc / focus leaving the ribbon) by clearing :floating.
                    new Style(Selectors.Nesting().PseudoClass(":minimized").PseudoClass(":floating").Template().Name("PART_Body"))
                        .Set(UIElement.VisibilityProperty, Visibility.Visible),
                },
            });
            return panel;
        });

        return new Style { Key = "Bars.Ribbon" }
            .Set(Control.TemplateProperty, template);
    }

    // A ribbon tab (: TabItem): the strip label + the inherited accent underline. Inactive = --text-dim; the active
    // tab drops to --tab-active + --text; the File tab is accent-filled. RibbonTab reuses TabItem.OnApplyTemplate to
    // paint PART_Underline, so the template keeps the header-over-underline shape.
    public static Style RibbonTabStyle()
    {
        var template = new ControlTemplate(ctx =>
        {
            // A contextual tab reads as `│ Table ▾` (guide §5): a leading divider, the header, a trailing caret. The
            // divider/caret are Collapsed on an ordinary tab (measure 0 ⇒ a normal tab renders byte-identically) and
            // flipped Visible for `:ribbon-contextual`. They inherit the header's Foreground (purple when contextual).
            var divider = new TextBlock { Text = "│ ", Visibility = Visibility.Collapsed };
            ctx.RegisterName("PART_ContextDivider", divider);

            var header = new ContentPresenter { RecognizesAccessKey = true };
            ctx.RegisterName("PART_ContentPresenter", header);
            header.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(HeaderedContentControl.HeaderProperty));

            // A LEFT MARGIN, not a leading space: TextBlock drops leading whitespace (its paragraph packs WordWrap), so
            // " ▾" would render as a gap-less `▾`. The divider's TRAILING space in "│ " survives, so it needs no margin.
            var caret = new TextBlock { Text = "▾", Margin = new Margins(1, 0, 0, 0), Visibility = Visibility.Collapsed };
            ctx.RegisterName("PART_ContextCaret", caret);

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            headerRow.Children.Add(divider);
            headerRow.Children.Add(header);
            headerRow.Children.Add(caret);

            var headerHost = new Border { Padding = new Margins(1, 0), Child = headerRow, Occludes = true };
            headerHost.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
            ctx.RegisterName("PART_HeaderSite", headerHost);

            var underline = new Separator { Margin = new Margins(1, 0, 1, 0) };
            underline.SetResourceReference(Control.BorderPenProperty, ThemeKeys.TabUnderlinePen);
            ctx.RegisterName("PART_Underline", underline);

            var stack = new StackPanel();
            stack.Children.Add(headerHost);
            stack.Children.Add(underline);
            return new Border { Child = stack };
        });

        // Part-targeting rules live in the TEMPLATE's .Styles under a type-anchored wrapper (Selectors.OfType), NOT the
        // theme Style's .Children — a `^`/Template().Name() rule with no type context does not match (the base TabItem
        // theme, ControlThemes.TabItemTheme, is the precedent). Setting them here is what makes the header fill/ink
        // actually paint.
        template.Styles.Add(new Style(Selectors.OfType<RibbonTab>())
        {
            Children =
            {
                new Style(Selectors.Nesting().Template().Name("PART_HeaderSite"))
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextDimBrush),
                // The File tab's resting identity (accent fill). Declared BEFORE the interaction cues so a hovered /
                // keyboard-focused File tab still reads as hovered/focused — an equal-specificity tie is broken by
                // declaration order (the ListBoxItem convention: identity first, interaction cues after). Without this
                // order the File tab shows NO focus cue when reached by arrow keys, defeating "File is reachable".
                new Style(Selectors.Nesting().PseudoClass(":ribbon-file").Template().Name("PART_HeaderSite"))
                    .SetResource(Panel.BackgroundProperty, ThemeKeys.AccentBrush)
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush),
                new Style(Selectors.Nesting().PseudoClass(":pointerover").Template().Name("PART_HeaderSite"))
                    .SetResource(Panel.BackgroundProperty, ThemeKeys.HoverBrush)
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextBrush),
                new Style(Selectors.Nesting().PseudoClass(":selected").Template().Name("PART_HeaderSite"))
                    .SetResource(Panel.BackgroundProperty, ThemeKeys.RibbonTabActiveBrush)
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextBrush),
                // Keyboard focus on the strip: the active tab reads as FOCUSED (a hover-strength fill over the dropped
                // active look) so it is distinct from a selected-but-focus-elsewhere tab — the "which tab has keyboard
                // focus" cue.
                new Style(Selectors.Nesting().PseudoClass(":focus-visible").Template().Name("PART_HeaderSite"))
                    .SetResource(Panel.BackgroundProperty, ThemeKeys.HoverBrush)
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextBrush),

                // Contextual tab (guide §5): purple ink on a tinted well (resting) / on the dropped band fill (active).
                // The compound `:ribbon-contextual:selected` out-specifies the plain `:selected` rule (2 classLike vs 1,
                // SD5) so the active contextual tab keeps its purple ink instead of --text. The purple underline is the
                // pen swap in RibbonTab.OnApplyTemplate (the template lane pins it; a Style rule can't). Divider + caret
                // (Collapsed by default) show for a contextual tab.
                new Style(Selectors.Nesting().PseudoClass(":ribbon-contextual").Template().Name("PART_HeaderSite"))
                    .SetResource(Panel.BackgroundProperty, ThemeKeys.RibbonContextualFillBrush)
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.PurpleBrush),
                new Style(Selectors.Nesting().PseudoClass(":ribbon-contextual").PseudoClass(":selected").Template().Name("PART_HeaderSite"))
                    .SetResource(Panel.BackgroundProperty, ThemeKeys.RibbonTabActiveBrush)
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.PurpleBrush),
                // Hover + keyboard-focus cues on a contextual tab keep the purple ink but take the hover-strength fill,
                // mirroring the normal-tab cues. Compound (classLike 2) so they out-specify plain :pointerover /
                // :focus-visible (classLike 1); declared AFTER :ribbon-contextual:selected (also classLike 2) so a
                // hovered/focused ACTIVE contextual tab reads as hovered/focused (declaration-order tiebreak).
                new Style(Selectors.Nesting().PseudoClass(":ribbon-contextual").PseudoClass(":pointerover").Template().Name("PART_HeaderSite"))
                    .SetResource(Panel.BackgroundProperty, ThemeKeys.HoverBrush)
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.PurpleBrush),
                new Style(Selectors.Nesting().PseudoClass(":ribbon-contextual").PseudoClass(":focus-visible").Template().Name("PART_HeaderSite"))
                    .SetResource(Panel.BackgroundProperty, ThemeKeys.HoverBrush)
                    .SetResource(TextElement.ForegroundProperty, ThemeKeys.PurpleBrush),
                new Style(Selectors.Nesting().PseudoClass(":ribbon-contextual").Template().Name("PART_ContextDivider"))
                    .Set(UIElement.VisibilityProperty, Visibility.Visible),
                new Style(Selectors.Nesting().PseudoClass(":ribbon-contextual").Template().Name("PART_ContextCaret"))
                    .Set(UIElement.VisibilityProperty, Visibility.Visible),
            },
        });

        return new Style { Key = "Bars.RibbonTab" }
            .Set(Control.TemplateProperty, template);
    }

    // A ribbon group: the controls row (PART_ItemsHost = a RibbonGroupPanel) over a muted group-name footer with an
    // optional ⋰ launcher, and a trailing │ separator the band hides on the last group. HeaderedItemsControl has no
    // default theme, so this is mandatory or the group renders as a bare ItemsControl with no label.
    public static Style RibbonGroupStyle()
    {
        var template = new ControlTemplate(ctx =>
        {
            var host = new ItemsPresenter();
            ctx.RegisterName("PART_ItemsHost", host);

            var name = new ContentPresenter { HorizontalAlignment = HorizontalAlignment.Center };
            ctx.RegisterName("PART_GroupName", name);
            name.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(HeaderedItemsControl.HeaderProperty));
            name.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);

            var launcher = new BarButton { Content = "⋰", Focusable = false, IsTabStop = false };
            ctx.RegisterName("PART_Launcher", launcher);

            var footer = new DockPanel();
            DockPanel.SetDock(launcher, Dock.Right);
            footer.Children.Add(launcher);
            footer.Children.Add(name);

            var column = new StackPanel { Orientation = Orientation.Vertical, Margin = new Margins(1, 0) };
            column.Children.Add(host);
            column.Children.Add(footer);

            var separator = new TextBlock { Text = "│", VerticalAlignment = VerticalAlignment.Center };
            separator.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.FaintBrush);
            ctx.RegisterName("PART_GroupSeparator", separator);

            var outer = new StackPanel { Orientation = Orientation.Horizontal };
            outer.Children.Add(column);
            outer.Children.Add(separator);

            // The ⋰ launcher is hidden unless the group opts in (HasDialogLauncher ⇒ :has-launcher). Part-targeting
            // rules live in the template's own styles (the size-cascade / obscured-overlay precedent).
            outer.Styles.Add(new Style(Selectors.Is<RibbonGroup>())
            {
                Children =
                {
                    new Style(Selectors.Nesting().Template().Name("PART_Launcher"))
                        .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                    new Style(Selectors.Nesting().PseudoClass(":has-launcher").Template().Name("PART_Launcher"))
                        .Set(UIElement.VisibilityProperty, Visibility.Visible),
                },
            });
            return outer;
        });

        return new Style { Key = "Bars.RibbonGroup" }
            .Set(Control.TemplateProperty, template);
    }

    // ───────────────────────────── Backstage (the File view) ─────────────────────────────

    // The Backstage frame: a left RAIL (a ◂ back button over the vertical destinations host, on the --panel recess)
    // beside a detail PANE that swaps to the selected destination's Content (the --surface fill). It re-skins the
    // inherited TabControl strip/content-host split — PART_ItemsHost is the rail's ItemsPresenter (a VERTICAL stack;
    // the Backstage's ItemsPanel), PART_ContentHost shows the selected BackstageItem's Content, exactly as a
    // TabControl hosts the selected tab's content. Both surfaces occlude (a full-window Window or a File-anchored
    // Popup hosts the Backstage — the cells beneath must not bleed through).
    public static Style BackstageStyle()
    {
        var template = new ControlTemplate(ctx =>
        {
            // The rail: a ◂ back button docked at the top over the destinations list. PART_BackButton is wired to
            // BackRequested in Backstage.OnApplyTemplate (the ◂ / Escape twin). It is NOT a tab stop (Focusable=false) —
            // Escape is its keyboard twin — so a freshly-shown Backstage auto-focuses the first DESTINATION, not the ◂.
            var back = new BarButton { Content = "◂", Focusable = false, IsTabStop = false };
            ctx.RegisterName("PART_BackButton", back);
            DockPanel.SetDock(back, Dock.Top);

            var rail = new ItemsPresenter();
            ctx.RegisterName("PART_ItemsHost", rail);

            var railStack = new DockPanel { LastChildFill = true };
            railStack.Children.Add(back); // docked top
            railStack.Children.Add(rail); // fills the rest of the rail column

            var railBorder = new Border { Child = railStack, Occludes = true };
            railBorder.SetResourceReference(Border.BackgroundProperty, ThemeKeys.PanelBrush);
            DockPanel.SetDock(railBorder, Dock.Left);

            // The detail pane: the selected destination's Content (the rail persists across a swap).
            var detail = new ContentPresenter { Margin = new Margins(2, 1) };
            ctx.RegisterName("PART_ContentHost", detail);
            detail.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(TabControl.SelectedContentProperty));
            detail.SetBinding(ContentPresenter.ContentTemplateProperty, new TemplateBinding(TabControl.ContentTemplateProperty));
            detail.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);
            var detailBorder = new Border { Child = detail, Occludes = true };
            detailBorder.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);

            var root = new DockPanel { LastChildFill = true };
            root.Children.Add(railBorder);   // docked left
            root.Children.Add(detailBorder); // fills the remaining width

            // Menu display mode (:backstage-menu) compaction: a File-anchored menu is light-dismissed (Escape /
            // click-away), so the ◂ back button is redundant chrome — collapse it (FullScreen keeps it; a maximized
            // surface has no ambient dismiss affordance). Part-targeting rules live in the template's own styles under
            // a type-anchored wrapper (the RibbonGroup / TabItem precedent — a bare ^/Template().Name() rule with no
            // type context does not match).
            root.Styles.Add(new Style(Selectors.Is<Backstage>())
            {
                Children =
                {
                    new Style(Selectors.Nesting().PseudoClass(":backstage-menu").Template().Name("PART_BackButton"))
                        .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
                    new Style(Selectors.Nesting().PseudoClass(":backstage-menu").Template().Name("PART_ContentHost"))
                        .Set(TextBlock.TextWrappingProperty, WrapMode.WordWrap),
                },
            });
            return root;
        });

        return new Style { Key = "Bars.Backstage" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.SurfaceBrush)
            .Set(Control.TemplateProperty, template);
    }

    // A Backstage rail row (: TabItem): a header-only row over the interactive fills, mirroring the ComboBoxItem /
    // ListBoxItem state ladder (resting --text, :pointerover --hover, :selected --sel, keyboard focus = reverse-video
    // :focus-visible, :disabled --muted ink). Its Header is the rail label (access-key folded); its Content is the
    // detail pane, shown in the Backstage's PART_ContentHost when selected — so the row's own template renders only
    // the Header (single-hosting, like a TabItem).
    public static Style BackstageItemStyle()
    {
        var theme = new Style { Key = "Bars.BackstageItem" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var header = new ContentPresenter { RecognizesAccessKey = true };
                ctx.RegisterName("PART_ContentPresenter", header);
                header.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(HeaderedContentControl.HeaderProperty));
                header.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                var border = new Border { Child = header };
                border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                border.SetBinding(Border.PaddingProperty, new TemplateBinding(Control.PaddingProperty));
                return border;
            }));
        theme.Children.Add(new Style("^")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundNormal)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundNormal));
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundHover));
        theme.Children.Add(new Style("^:selected").SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundSelected));
        theme.Children.Add(new Style("^:focus-visible")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundFocus));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundDisabled));
        return theme;
    }

    // ───────────────────────────── SuperTip (rich hover help) ─────────────────────────────

    // The SuperTip layout (shown inside the shared ToolTip popup, which supplies the elevation chrome): a header row
    // [bold Title … muted accelerator], a muted Description block, and a faint Footer. Empty fields (null Text/Content)
    // render nothing, so a title-only SuperTip collapses to one line.
    public static Style SuperTipStyle()
        => new Style { Key = "Bars.SuperTip" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var title = new TextBlock();
                title.SetValue(TextElement.TextAttributesProperty, TextAttributes.Bold);
                title.SetBinding(TextBlock.TextProperty, new TemplateBinding(SuperTip.TitleProperty));
                title.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);

                var gesture = new TextBlock { Margin = new Margins(2, 0, 0, 0) };
                gesture.SetBinding(TextBlock.TextProperty, new TemplateBinding(SuperTip.InputGestureTextProperty));
                gesture.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
                DockPanel.SetDock(gesture, Dock.Right);

                var header = new DockPanel();
                header.Children.Add(gesture); // Dock.Right — the accelerator
                header.Children.Add(title);   // fills — the command name

                var description = new ContentPresenter();
                description.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(SuperTip.DescriptionProperty));
                description.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);

                var footer = new ContentPresenter { Margin = new Margins(0, 1, 0, 0) };
                footer.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(SuperTip.FooterProperty));
                footer.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.FaintBrush);

                var stack = new StackPanel { Orientation = Orientation.Vertical };
                stack.Children.Add(header);
                stack.Children.Add(description);
                stack.Children.Add(footer);
                return stack;
            }));
}
