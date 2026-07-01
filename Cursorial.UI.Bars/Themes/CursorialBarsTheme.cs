using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
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
        ctx.RegisterName(PartMediumFace, row);

        // Large (the ribbon glyph-over-label form): a centered vertical [glyph][label], glyph re-inked --accent-2.
        // Hidden until :size-large; a collapsed face contributes nothing so Medium is unaffected.
        var largeGlyph = new ContentPresenter { HorizontalAlignment = HorizontalAlignment.Center };
        largeGlyph.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(BarButton.IconProperty));
        largeGlyph.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.Accent2Brush);

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
                new Style(Selectors.Nesting().PseudoClass(":size-large").Template().Name(PartLargeFace))
                    .Set(UIElement.VisibilityProperty, Visibility.Visible),
                new Style(Selectors.Nesting().PseudoClass(":size-large").Template().Name(PartMediumFace))
                    .Set(UIElement.VisibilityProperty, Visibility.Collapsed),
            },
        });
        return border;
    });

    private const string PartMediumFace = "PART_MediumFace";
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

    public static Style SeparatorStyle()
        => new Style { Key = "Bars.BarSeparator" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.FaintBrush)
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var glyph = new TextBlock { Text = "│" };
                glyph.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                return glyph;
            }));

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
            .SetResource(Control.BackgroundProperty, ThemeKeys.PanelBrush)
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

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var label = new ContentPresenter { RecognizesAccessKey = true };
                label.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                var caret = new TextBlock { Text = " ▾" };
                caret.SetBinding(TextElement.ForegroundProperty, new TemplateBinding(Control.ForegroundProperty));
                row.Children.Add(BuildIcon());
                row.Children.Add(label);
                row.Children.Add(caret);
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
                    Content = "▾",
                    Focusable = false, // a mouse target only — Down on the split button opens the dropdown by keyboard
                    Theme = DropZoneStyle(),
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                ctx.RegisterName("PART_DropDown", dropZone);

                var band = new StackPanel { Orientation = Orientation.Horizontal };
                band.Children.Add(primary);
                band.Children.Add(dropZone);

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
        => new Style { Key = "Bars.BarComboBox" }
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

                var caret = new Button { Content = "▾", Focusable = false, IsTabStop = false, Theme = ComboCaretStyle() };
                ctx.RegisterName("PART_DropDown", caret); // the ComboBox wires its Click to toggle the list
                DockPanel.SetDock(caret, Dock.Right);

                var row = new DockPanel();
                row.Children.Add(caret);        // docked right (the ▾ toggle)
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
        => new Style { Key = "Bars.Ribbon" }
            .Set(Control.TemplateProperty, new ControlTemplate(ctx =>
            {
                var itemsHost = new ItemsPresenter();
                ctx.RegisterName("PART_ItemsHost", itemsHost);
                var strip = new Border { Child = itemsHost, Occludes = true };
                strip.SetResourceReference(Border.BackgroundProperty, ThemeKeys.RibbonTabStripBrush);
                DockPanel.SetDock(strip, Dock.Top);

                var content = new ContentPresenter();
                ctx.RegisterName("PART_ContentHost", content);
                content.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(TabControl.SelectedContentProperty));
                content.SetBinding(ContentPresenter.ContentTemplateProperty, new TemplateBinding(TabControl.ContentTemplateProperty));
                content.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);
                var body = new Border { Padding = new Margins(1, 0), Child = content, Occludes = true };
                body.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
                DockPanel.SetDock(body, Dock.Bottom);

                var panel = new DockPanel();
                panel.Children.Add(strip);
                panel.Children.Add(body);
                return panel;
            }));

    // A ribbon tab (: TabItem): the strip label + the inherited accent underline. Inactive = --text-dim; the active
    // tab drops to --tab-active + --text; the File tab is accent-filled. RibbonTab reuses TabItem.OnApplyTemplate to
    // paint PART_Underline, so the template keeps the header-over-underline shape.
    public static Style RibbonTabStyle()
    {
        var template = new ControlTemplate(ctx =>
        {
            var header = new ContentPresenter { RecognizesAccessKey = true };
            ctx.RegisterName("PART_ContentPresenter", header);
            header.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(HeaderedContentControl.HeaderProperty));

            var headerHost = new Border { Padding = new Margins(1, 0), Child = header, Occludes = true };
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

        var theme = new Style { Key = "Bars.RibbonTab" }
            .Set(Control.TemplateProperty, template);

        theme.Children.Add(new Style(Selectors.Nesting().Template().Name("PART_HeaderSite"))
            .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextDimBrush));
        theme.Children.Add(new Style(Selectors.Nesting().PseudoClass(":pointerover").Template().Name("PART_HeaderSite"))
            .SetResource(Panel.BackgroundProperty, ThemeKeys.HoverBrush)
            .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextBrush));
        theme.Children.Add(new Style(Selectors.Nesting().PseudoClass(":selected").Template().Name("PART_HeaderSite"))
            .SetResource(Panel.BackgroundProperty, ThemeKeys.RibbonTabActiveBrush)
            .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextBrush));
        theme.Children.Add(new Style(Selectors.Nesting().PseudoClass(":ribbon-file").Template().Name("PART_HeaderSite"))
            .SetResource(Panel.BackgroundProperty, ThemeKeys.AccentBrush)
            .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush));
        return theme;
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
}
