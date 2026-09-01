using System.Globalization;

using Cursorial.Animation;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

using Calendar = Cursorial.UI.Controls.Calendar;

// ReSharper disable AccessToStaticMemberViaDerivedType

namespace Cursorial.UI.Themes;

/// <summary>
/// The S8 default control themes authored into <see cref="CursorialTheme.BuiltIn"/>'s structure
/// (design doc §11.8/§12.7, CD30): one <see cref="Style"/> per control <see cref="Type"/> key, each a
/// selector-less theme rooted at <c>^</c> carrying a <see cref="ControlTemplate"/> setter plus
/// <c>:focus</c>/<c>:pressed</c>/<c>:checked</c>/<c>:disabled</c> child rules. The styling engine
/// resolves them by <see cref="Control.ControlThemeKey"/> (exact-key) and arms them at
/// <see cref="StyleLayer.ControlTheme"/> — the weakest style layer, so an app style always wins
/// (CD30). Templates are code-first <see cref="ControlTemplate"/>s (Fork C wires XAML at P6).
/// </summary>
internal static class ControlThemes
{
    // Cell-faithful default look (design doc §11.8a; default-theme-adoption-spec.md): controls are
    // FILL-BOUNDED, not line-bounded. A control's resting extent is a SurfaceBrush fill (not a stroked
    // border); the interactive states are brush-pair flips into the palette spine — :pointerover = a
    // HoverBrush fill, :focus = reverse-video (TextBrush fill + WindowBackground text), :pressed/:default =
    // accent reverse-video (AccentBrush fill + OnAccentBrush text), :disabled = DisabledBackgroundBrush
    // + DisabledForegroundBrush. No FocusPen ring, no Pens.Double :default weight bump. The NoColor
    // interactive-state distinction (where the brush pairs resolve to Default) rides the caps-nocolor
    // theme-styles rules in CursorialThemeStyles (inherited TextElement.TextAttributes — Inverse / Faint —
    // honored by the Border fill + content text). BorderPen/FocusPen survive only as opt-in chrome, unread here.

    internal static void Populate(ResourceDictionary dict)
    {
        dict[typeof(Button)] = ButtonTheme();
        dict[typeof(Label)] = LabelTheme();
        dict[typeof(ContentControl)] = ContentControlTheme();
        dict[typeof(UserControl)] = UserControlTheme();
        dict[typeof(RepeatButton)] = RepeatButtonTheme();
        dict[typeof(ToggleButton)] = ToggleButtonTheme();
        dict[typeof(CheckBox)] = ToggleGlyphTheme<CheckBox>("Theme.CheckBox", ThemeKeys.CheckBoxGlyphs, ThemeKeys.ToggleGlyphChecked, ThemeKeys.ToggleGlyphIndeterminate);
        dict[typeof(RadioButton)] = ToggleGlyphTheme<RadioButton>("Theme.RadioButton", ThemeKeys.RadioGlyphs, ThemeKeys.RadioGlyphChecked, ThemeKeys.ToggleGlyphIndeterminate);
        dict[typeof(ScrollBar)] = ScrollBarTheme();
        dict[typeof(ScrollViewer)] = ScrollViewerTheme();
        dict[typeof(ItemsControl)] = ItemsControlTheme();
        dict[typeof(ListBox)] = ListBoxTheme();
        dict[typeof(ListBoxItem)] = ListBoxItemTheme();
        dict[typeof(ListView)] = ListViewTheme();
        dict[typeof(ListViewItem)] = ListViewItemTheme();
        dict[typeof(ListViewColumnHeader)] = ListViewColumnHeaderTheme();
        dict[typeof(ComboBox)] = ComboBoxTheme();
        dict[typeof(ComboBoxItem)] = ComboBoxItemTheme();
        dict[typeof(BreadcrumbBar)] = BreadcrumbBarTheme();
        dict[typeof(BreadcrumbBarItem)] = BreadcrumbBarItemTheme();
        dict[typeof(CompletionPopup)] = CompletionPopupTheme();
        dict[typeof(CompletionList)] = CompletionListTheme();
        dict[typeof(CompletionListItem)] = CompletionListItemTheme();
        dict[typeof(TreeView)] = TreeViewTheme();
        dict[typeof(TreeViewItem)] = TreeViewItemTheme();
        dict[typeof(Calendar)] = CalendarTheme();
        dict[typeof(CalendarDayButton)] = CalendarDayButtonTheme();
        dict[typeof(CalendarButton)] = CalendarButtonTheme();
        dict[typeof(DatePicker)] = DatePickerTheme();
        dict[typeof(Menu)] = MenuTheme();
        dict[typeof(MenuItem)] = MenuItemTheme();
        dict[typeof(ContextMenu)] = ContextMenuTheme();
        dict[typeof(Separator)] = SeparatorTheme();
        dict[typeof(ToolTip)] = ToolTipTheme();
        dict[typeof(TabControl)] = TabControlTheme();
        dict[typeof(TabItem)] = TabItemTheme();
        dict[typeof(ProgressBar)] = ProgressBarTheme();
        dict[typeof(Slider)] = SliderTheme();
        dict[typeof(GridSplitter)] = GridSplitterTheme();
        dict[typeof(StatusBar)] = StatusBarTheme();
        dict[typeof(StatusBarItem)] = StatusBarItemTheme();
        dict[typeof(Expander)] = ExpanderTheme();
        dict[typeof(TextBox)] = TextBoxTheme();
        dict[typeof(PasswordBox)] = TextBoxTheme(); // a masked TextBox — same PART_TextPresenter chrome (the presenter masks)
        dict[typeof(Image)] = ImageTheme();
        dict[typeof(Icon)] = IconTheme();
        dict[typeof(Chart)] = ChartTheme();
        dict[typeof(Window)] = WindowTheme();
        dict[new DataTemplateKey(typeof(IconCarrier))] = IconCarrierTemplate();
    }

    // ───────────────────────────── Button / RepeatButton / ToggleButton ─────────────────────────────

    // A fill-bounded single-content button (design doc §11.8a): a Border with Padding (1,0) wrapping a
    // RecognizesAccessKey ContentPresenter that auto-aliases the button's Content. The fill IS the button
    // (no resting border) — Background carries the SurfaceBrush spine and flips per interactive state
    // (hover/focus/pressed/disabled); the content text inherits Foreground, which the reverse-video focus
    // and accent pressed/default rules swap. The BorderPen binding stays so an app can opt a frame back in.
    private static ControlTemplate ButtonContentTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = true, ShowTrimmedContentInToolTip = true };
        presenter.SetBinding(TextElement.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));
        ctx.RegisterName("PART_ContentPresenter", presenter);
        TextElement.ForwardInverse(presenter);
        var border = new Border { Child = presenter, Occludes = true };
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        border.SetBinding(TextElement.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));
        // The face consumes the Inverse AXIS for its fill (non-inheriting — "flows like Background"):
        // a live per-axis forward, the brush idiom verbatim (proposal §4.2; was the aggregate forward).
        border.SetBinding(TextElement.InverseProperty, TemplateBinding.From(TextElement.InverseProperty));
        // The face fill follows Button.Background (the WPF default-template wiring): a TemplateBinding
        // makes the resting SurfaceBrush + the per-state brush-pair flips paint the face, quantized per
        // the negotiated tier. No resting pen ⇒ no frame ⇒ a 1-row button (content at row 0).
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        // Opt-in border: the BorderPen binding is retained (unset by the default theme), so a consumer
        // that sets Control.BorderPen gets a framed button without a custom template.
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        return border;
    });

    private static Style ButtonTheme()
    {
        var theme = AddButtonStates<Button>(ApplyPaletteSpine(new Style { Key = "Theme.Button" }, ThemeKeys.ButtonForegroundNormal, ThemeKeys.ButtonBackgroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center)
            .Set(Control.TemplateProperty, ButtonContentTemplate()));
        // :default (the Enter-default cue) — a resting accent reverse-video fill so the primary action
        // stands out; :focus/:pressed override it when the user interacts. Reuses the Pressed per-control
        // keys (spec: "Pressed + IsDefault"). The ▸ OK ◂ gutter brackets are a deferred content nicety (spec §3).
        // theme.Children.Add(new Style("^:default")
        //     .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
        //     .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed));
        return theme;
    }

    // ───────────────────────────── Label ─────────────────────────────

    // A caption (design doc §12.5/§12.7): a RecognizesAccessKey ContentPresenter so the access-key mnemonic
    // underlines on Alt (Label.Content folds "_Name" → an AccessText mnemonic). No resting fill or frame — a
    // Label is a caption, not a control face; the content paints in the inherited TextBrush, muted when disabled.
    private static ControlTemplate LabelTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = true, ShowTrimmedContentInToolTip = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        presenter.SetBinding(TextElement.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));
        
        var border = new Border { Child = presenter };
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));

        return border;
    });

    private static Style LabelTheme()
    {
        var theme = new Style { Key = "Theme.Label" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, LabelTemplate());
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

    // The neutral content host (WPF/Avalonia parity): a bare ContentPresenter that auto-aliases the
    // ContentControl's Content (A22) and resolves it through the DataTemplate-by-type chain. Without this default
    // theme a ContentControl has no presenter and renders blank — the host an MVVM `Content="{Binding}"` needs.
    // (Keyed by the exact type, so ContentControl-derived controls keep their own themes.)
    private static ControlTemplate ContentControlTemplate() => new(ctx =>
    {
        var border = new Border();
        var presenter = new ContentPresenter();
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        ctx.RegisterName("PART_ContentPresenter", presenter);
        border.Child = presenter;
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        return border;
    });

    private static Style ContentControlTheme()
        => new Style { Key = "Theme.ContentControl" }
            .Set(Control.TemplateProperty, ContentControlTemplate());

    // UserControl shares the neutral presenter; derived x:Class views reach it through the
    // ControlThemeKey pin (UserControl overrides the exact-key lookup to typeof(UserControl)).
    private static Style UserControlTheme()
        => new Style { Key = "Theme.UserControl" }
            .Set(Control.TemplateProperty, ContentControlTemplate());

    // ───────────────────────────── ItemsControl ─────────────────────────────

    // A minimal items host (design doc §12.6 / C2): a Border (opt-in Background/BorderPen via TemplateBindings)
    // wrapping the PART_ItemsHost ItemsPresenter, which builds the ItemsPanel and lays out the containers.
    private static ControlTemplate ItemsControlTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var border = new Border { Child = host };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        return border;
    });

    private static Style ItemsControlTheme()
        => new Style { Key = "Theme.ItemsControl" }
            .Set(Control.TemplateProperty, ItemsControlTemplate())
            .Set(ItemsControl.ItemsPanelProperty, VirtualizingItemsPanelTemplate());

    private static ItemsPanelTemplate VirtualizingItemsPanelTemplate()
    {
        return new ItemsPanelTemplate(
            ctx =>
            {
                var virtualizingStackPanel = new VirtualizingStackPanel { IsItemsHost = true };

                virtualizingStackPanel.SetBinding(
                    VirtualizingPanel.IsVirtualizingProperty,
                    CompiledBinding.For(VirtualizingPanel.IsVirtualizingProperty,
                                         source: ctx.TemplatedParent));

                virtualizingStackPanel.SetBinding(
                    VirtualizingPanel.VirtualizationModeProperty,
                    CompiledBinding.For(VirtualizingPanel.VirtualizationModeProperty,
                                         source: ctx.TemplatedParent));

                virtualizingStackPanel.SetBinding(
                    VirtualizingPanel.ScrollUnitProperty,
                    CompiledBinding.For(VirtualizingPanel.ScrollUnitProperty,
                                         source: ctx.TemplatedParent));

                virtualizingStackPanel.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Once);

                // The items host is a focus scope (ND33): focus memory records HERE — the element the Once entry
                // ladder reads — so Tab-in lands on the selected/last item, not item 0 (no accidental reselect).
                virtualizingStackPanel.SetValue(FocusManager.IsFocusScopeProperty, true);

                return virtualizingStackPanel;
            });
    }

    // ───────────────────────────── ListBox / ListBoxItem ─────────────────────────────

    // A well-fill ListBox (design doc §11.8a): a Border (opt-in BorderPen) over a ScrollViewer whose content is the
    // PART_ItemsHost ItemsPresenter — so a long list scrolls (the SCP band, C3). The items host stays in the
    // ListBox's template namescope even nested under the ScrollViewer, so GetTemplatePart finds it.
    private static ControlTemplate ListBoxTemplate()
    {
        var t = new ControlTemplate(
            ctx =>
                 {
                     var host = new ItemsPresenter();
                     ctx.RegisterName("PART_ItemsHost", host);

                     var scroll = new ScrollViewer
                                  {
                                      Content = host
                                  }; // the ItemsPresenter resolves its owner up the visual tree (CD-P9-17)

                     scroll.SetBinding(ScrollViewer.VerticalScrollBarVisibilityProperty,
                                       TemplateBinding.From(ScrollViewer.VerticalScrollBarVisibilityProperty));

                     scroll.SetBinding(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                                       TemplateBinding.From(ScrollViewer.HorizontalScrollBarVisibilityProperty));

                     ctx.RegisterName("PART_ScrollViewer", scroll);
                     var border = new Border { Child = scroll };
                     border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
                     border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
                     
                     return border;
                 });

        t.Styles.Add(
            new Style(":is(ListBox):focus-within")
            {
                Children =
                {
                    new Style("^ :is(ListBoxItem):selected")
                       .SetResource(Panel.BackgroundProperty, ThemeKeys.ListItemBackgroundSelected)
                       .SetResource(TextElement.ForegroundProperty, ThemeKeys.ListItemForegroundSelected),
                    new Style("^ :is(ListBoxItem):focus-visible:selected")
                       .SetResource(Panel.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                       .SetResource(TextElement.ForegroundProperty, ThemeKeys.ListItemForegroundFocus)
                }
            }
        );

        return t;
    }

    private static Style ListBoxTheme()
        => new Style
           {
               Key = "Theme.ListBox",
               Children =
               {
                   new Style("^:is(ListBox):focus-within")
                   {
                       Children =
                       {
                           new Style("^ :is(ListBoxItem):selected")
                              .SetResource(Panel.BackgroundProperty, ThemeKeys.ListItemBackgroundSelected)
                              .SetResource(TextElement.ForegroundProperty,
                                           ThemeKeys.ListItemForegroundSelected),
                           new Style("^ :is(ListBoxItem):focus-visible:selected")
                              .SetResource(Panel.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                              .SetResource(TextElement.ForegroundProperty, ThemeKeys.ListItemForegroundFocus)
                       }
                   }
               }
           }
          .SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush) // a list is a recessed well
          .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
          .Set(Control.TemplateProperty, ListBoxTemplate())
          .Set(ItemsControl.ItemsPanelProperty, VirtualizingItemsPanelTemplate());

    // A list item is a full-width selection bar: a Border filling its row, content at row 0 (no frame). Per the
    // default-theme gallery mockup (.item.sel/.hov/.dis): selected = SelectionBrush fill + TextBrush ink (NOT the
    // OnAccent pair — selection is milder than pressed; adoption-spec line 14), hover = HoverBrush + TextBrush,
    // disabled = MutedBrush ink. Ordered hover → selected so a hovered-selected item reads as selected (document
    // order). The keyboard focus-row cue: brush-pair at color tiers (below); Inverse+Bold under
    // .caps-nocolor (CursorialThemeStyles.CapsNoColorListFocusCue — the landed P9.3b, the per-axis
    // composability proof).
    private static ControlTemplate ListBoxItemTemplate()
    {
        var t = new ControlTemplate(
            ctx =>
                 {
                     var panel = new Grid();
                     var presenter = new ContentPresenter { ShowTrimmedContentInToolTip = true };
                     ctx.RegisterName("PART_ContentPresenter", presenter);
                     var border = new Border { Child = presenter };
                     border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
                     border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
                     // The row face forwards the Inverse axis (the NoColor selection/focus-row cue paints the
                     // WHOLE bar via the opaque NoColor fill); the label rides the presenter forward.
                     border.SetBinding(TextElement.InverseProperty, TemplateBinding.From(TextElement.InverseProperty));

                     var selectionIndicator = new GlyphPresenter
                                              {
                                                  Orientation = Orientation.Vertical,
                                                  IsHitTestVisible = false
                                              };

                     ctx.RegisterName("PART_SelectionIndicator", selectionIndicator);

                     selectionIndicator.SetResourceReference(GlyphPresenter.GlyphProperty, ThemeKeys.ListItemSelectionGlyph);
                     selectionIndicator.SetResourceReference(GlyphPresenter.ForegroundProperty, ThemeKeys.AccentBrush);
                     
                     TextElement.ForwardInverse(presenter);
                     TextElement.ForwardInverse(selectionIndicator);

                     selectionIndicator.SetBinding(
                         UIElement.VisibilityProperty,
                         TemplateBinding.From(ListBoxItem.IsSelectedProperty,
                                            converter: BooleanToVisibilityConverter.Instance));

                     panel.Children.Add(border);
                     panel.Children.Add(selectionIndicator);

                     return panel;
                 });

        return t;
    }

    // ───────────────────────────── ListView / ListViewItem / ListViewColumnHeader ─────────────────────────────

    // A ListView is a ListBox with a PINNED header strip: the PART_HeaderPresenter docks to the top OUTSIDE the
    // ScrollViewer, so the column headings never scroll away vertically (and, symmetrically, why the control
    // disables horizontal scrolling in Details — the body would slide out from under them). The header strip
    // collapses itself in the three wrapping views; the ListView drives that from ApplyViewToTemplate.
    private static ControlTemplate ListViewTemplate() => new(ctx =>
    {
        var header = new ListViewHeaderPresenter();
        ctx.RegisterName("PART_HeaderPresenter", header);
        header.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.PanelBrush);
        DockPanel.SetDock(header, Dock.Top);

        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);

        var scroll = new ScrollViewer { Content = host }; // the ItemsPresenter resolves its owner up the UIParent bridge
        scroll.SetBinding(ScrollViewer.VerticalScrollBarVisibilityProperty,
                          TemplateBinding.From(ScrollViewer.VerticalScrollBarVisibilityProperty));
        scroll.SetBinding(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                          TemplateBinding.From(ScrollViewer.HorizontalScrollBarVisibilityProperty));
        ctx.RegisterName("PART_ScrollViewer", scroll);

        var layout = new DockPanel(); // LastChildFill: the scroll band takes everything under the header
        layout.Children.Add(header);
        layout.Children.Add(scroll);

        var border = new Border { Child = layout };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        return border;
    });

    private static Style ListViewTheme()
        => new Style
           {
               Key = "Theme.ListView",
               Children =
               {
                   new Style("^:focus-within")
                   {
                       Children =
                       {
                           new Style("^ :is(ListViewItem):selected")
                              .SetResource(Panel.BackgroundProperty, ThemeKeys.ListItemBackgroundSelected)
                              .SetResource(TextElement.ForegroundProperty, ThemeKeys.ListItemForegroundSelected),
                           new Style("^ :is(ListViewItem):focus-visible:selected")
                              .SetResource(Panel.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                              .SetResource(TextElement.ForegroundProperty, ThemeKeys.ListItemForegroundFocus)
                       }
                   }
               }
           }
          .SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush) // a list is a recessed well
          .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
          .Set(Control.TemplateProperty, ListViewTemplate());

    // The row face is the ListBoxItem selection bar with the ContentPresenter swapped for PART_Cells: a details
    // row cannot carry the ListBoxItem selection glyph in a gutter, because the gutter would shift every cell out
    // from under its heading. Selection reads purely as the fill/ink flip.
    private static ControlTemplate ListViewItemTemplate() => new(ctx =>
    {
        var cells = new ListViewCellsPresenter();
        ctx.RegisterName("PART_Cells", cells);

        var border = new Border { Child = cells };
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        // The row face forwards the Inverse axis so the NoColor selection/focus cue paints the WHOLE bar.
        border.SetBinding(TextElement.InverseProperty, TemplateBinding.From(TextElement.InverseProperty));
        TextElement.ForwardInverse(cells);
        return border;
    });

    private static Style ListViewItemTheme()
    {
        var theme = new Style { Key = "Theme.ListViewItem" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundNormal)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, ListViewItemTemplate());
        // Ordered hover → selected → focus-visible → disabled: document order breaks the pseudo-class tie, so a
        // hovered-selected row reads as selected and the keyboard current row reads as the reverse-video cue.
        theme.Children.Add(new Style("^:pointerover")
                              .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundHover)
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundHover));
        theme.Children.Add(new Style("^:selected")
                              .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundSelectedInactive)
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundSelectedInactive));
        theme.Children.Add(new Style("^:focus-visible")
                              .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundFocus));
        theme.Children.Add(new Style("^:disabled")
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundDisabled));
        // A tile is two rows tall and pads on both axes; a details/list row is one row with side padding only.
        theme.Children.Add(new Style("^:tiles").Set(Control.PaddingProperty, new Margins(1, 0, 1, 0)));
        return theme;
    }

    // A column heading: [Header … ▲] on a PanelBrush strip. The sort indicator docks RIGHT so it hugs the column
    // edge and the heading text keeps the leading cell — the arrangement a sorted table is read by.
    private static ControlTemplate ListViewColumnHeaderTemplate() => new(ctx =>
    {
        var indicator = new TextBlock { Visibility = Visibility.Collapsed };
        ctx.RegisterName("PART_SortIndicator", indicator);
        indicator.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.AccentBrush);
        DockPanel.SetDock(indicator, Dock.Right);

        var presenter = new ContentPresenter { ShowTrimmedContentInToolTip = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        presenter.SetBinding(TextElement.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));

        var row = new DockPanel();
        row.Children.Add(indicator); // docked right
        row.Children.Add(presenter); // fills the rest

        var border = new Border { Child = row };
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(TextElement.InverseProperty, TemplateBinding.From(TextElement.InverseProperty));
        TextElement.ForwardInverse(presenter);
        return border;
    });

    private static Style ListViewColumnHeaderTheme()
    {
        var theme = new Style { Key = "Theme.ListViewColumnHeader" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.PanelBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.PaddingProperty, Margins.Zero)
            .Set(Control.TemplateProperty, ListViewColumnHeaderTemplate());
        theme.Children.Add(new Style("^:pointerover")
                              .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundHover)
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundHover));
        theme.Children.Add(new Style("^:pressed")
                              .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

    // ───────────────────────────── ComboBox / ComboBoxItem ─────────────────────────────

    // A combo box face: a recessed WellBrush field [selected-item … 'v' drop glyph] that the control toggles on
    // click, plus the drop-down Popup (PART_Popup) whose Child is an occluding panel over the PART_ItemsHost
    // ItemsPresenter (the ListBox-in-Popup recipe, design doc §12.11). The glyph is ASCII-safe ('v', cf. the scroll
    // arrows) to avoid the ambiguous-width hazard.
    private static ControlTemplate ComboBoxTemplate() => new(ctx =>
    {
        // Read-only face: the selection-box value (visible when !IsEditable; the ComboBox toggles visibility). Bound
        // to SelectionBoxItem, NOT SelectedItem directly — when the item is its own ComboBoxItem container, the face
        // must show the unwrapped content, never the live container element (which belongs to the drop-down).
        var selected = new ContentPresenter { ShowTrimmedContentInToolTip = true };
        ctx.RegisterName("PART_ContentSite", selected);

        selected.SetBinding(ContentPresenter.ContentProperty,
                            TemplateBinding.From(ComboBox.SelectionBoxItemProperty));

        // Editable face: a text box (visible when IsEditable). Placeholder + read-only flow from the ComboBox.
        var editable = new TextBox { Visibility = Visibility.Collapsed, Padding = Margins.Zero, IsTabStop = false };
        ctx.RegisterName("PART_EditableTextBox", editable);
        editable.SetBinding(TextBox.PlaceholderProperty, TemplateBinding.From(ComboBox.PlaceholderTextProperty));
        editable.SetBinding(TextBox.IsReadOnlyProperty, TemplateBinding.From(ComboBox.IsReadOnlyProperty));
        TextElement.ForwardBaseTextStyle(editable);

        var content = new Grid(); // the two faces overlap in one cell; the collapsed one takes no space
        content.SetBinding(UIElement.MarginProperty, TemplateBinding.From(Control.PaddingProperty));
        content.Children.Add(selected);
        content.Children.Add(editable);

        // Drop caret (▾) — a bare 1-cell muted glyph at the right edge that toggles the list (the ComboBox wires
        // its Click), NOT a chromed button (the design guide shows a hugging caret, not a sub-button face).
        var drop = DropDownCaret();
        ctx.RegisterName("PART_DropDown", drop);
        DockPanel.SetDock(drop, Dock.Right);

        var row = new DockPanel();
        row.Children.Add(drop);    // docked right (the drop indicator + toggle)
        row.Children.Add(content); // fills the remaining width (the face)

        var face = new Border { Child = row };
        face.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        face.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));

        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var list = new Border { /*Occludes = true, */Child = host };
        list.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
        list.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
        list.SetBinding(UIElement.MaxHeightProperty, TemplateBinding.From(ComboBox.MaxDropDownHeightProperty)); // MaxDropDownHeight cap
        var popup = new Popup { Child = list };
        ctx.RegisterName("PART_Popup", popup);

        var root = new Grid(); // the Popup adds no layout (0×0); the face fills the cell
        root.Children.Add(face);
        root.Children.Add(popup);
        return root;
    });

    private static Style ComboBoxTheme()
        => new Style
           {
               Key = "Theme.ComboBox",
               Children =
               {
                   new Style("^:pointerover")
                      .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.InputHoverTextStyle),
                   new Style("^:focus, ^:focus-within")
                      .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.InputFocusTextStyle),
                   new Style("^:disabled")
                      .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.InputDisabledTextStyle)
               }
           }.SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.InputNormalTextStyle) // a recessed field
            .Set(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Once)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, ComboBoxTemplate());

    // The drop-down caret: a bare 1-cell ▾ (no button chrome — the BareGlyphButtonTemplate, like the scroll arrows),
    // still clickable so the consumer can toggle the list on it. The caret inherits the field's foreground. Shared
    // by ComboBox + DatePicker; the consumer registers it as PART_DropDown and wires Click. (▾ is the design-guide
    // glyph; geometric, like the scroll arrows — the renderer's ambiguous-width defense covers the edge terminals.)
    // 2026-07-12 lattice note: these part literals (Template + Padding=0) are now the caret's RESTING truth (§20 —
    // Template above resting Style), so Theme.Button's root Template/Padding setters no longer re-chrome it; the
    // bare face authored here is what renders (previously the theme's chromed mini-button won and this was latent).
    private static Button DropDownCaret()
        => new()
        {
            Content = "▾",
            // Template = BareGlyphButtonTemplate(),
            // Padding = Margins.Zero,
            Focusable = false,
            IsTabStop = false
        };

    // A drop-down item — the ListBoxItem selection bar verbatim (selected = SelectionBrush, hover = HoverBrush,
    // keyboard focus row = reverse-video :focus-visible, disabled = MutedBrush ink).
    private static ControlTemplate ComboBoxItemTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { ShowTrimmedContentInToolTip = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border { Padding = new Margins(1, 0), Child = presenter };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(TextElement.InverseProperty, TemplateBinding.From(TextElement.InverseProperty)); // the row-face cue axis
        return border;
    });

    private static Style ComboBoxItemTheme()
    {
        var theme = new Style { Key = "Theme.ComboBoxItem" }
            .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemNormalTextStyle)
            .Set(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)
            .Set(Control.TemplateProperty, ComboBoxItemTemplate());
        
        theme.Children.Add(new Style("^:pointerover")
                          .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemHoverTextStyle));
        theme.Children.Add(new Style("^:selected")
                          .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemSelectedInactiveTextStyle));
        theme.Children.Add(new Style("^:focus-visible")
                          .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemFocusTextStyle));
        theme.Children.Add(new Style("^:disabled")
                          .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemDisabledTextStyle));

        return theme;
    }

    // ───────────────────────────── BreadcrumbBar / BreadcrumbBarItem ─────────────────────────────

    /// <summary>Rows the "▸" / ellipsis drop-down shows before it scrolls. Twelve fits a 24-row terminal with the
    /// chrome (header + footer + the content border's two rules) and still leaves the trail itself visible.</summary>
    private const int BreadcrumbDropDownRows = 12;

    /// <summary>The drop-down's key hints. ASCII-only, like every other popup footer: the arrow glyphs are
    /// ambiguous-width and would mis-measure on the terminals the renderer's width defense exists for.</summary>
    private const string BreadcrumbDropDownFooter = //"^v move   type to filter   Enter open   Esc dismiss" +
                                                    $"[fg={ThemeKeys.TextDimBrush}]↑↓[/fg]  " +
                                                    $"[fg={ThemeKeys.TextDimBrush}]type[/fg] to filter  " +
                                                    $"[fg={ThemeKeys.TextDimBrush}]⏎[/fg] open  " +
                                                    $"[fg={ThemeKeys.TextDimBrush}]⎋[/fg] cancel";

    // A breadcrumb trail: [… ▸] [Home ▸] [Projects ▸] [assets], sitting ON the page (no resting fill — it is a
    // trail, not a field) with the chips carrying the only fills. The leading "…" chip is a real
    // BreadcrumbBarItem so it inherits the chip look for free; it is Collapsed until the panel's fold reports
    // elided ancestors (the BreadcrumbBar flips it). The chips band and the raw-text edit box overlap in a Grid,
    // exactly like the ComboBox's two faces — the collapsed one takes no space, and the drop-down picker joins
    // them there at zero size.
    private static ControlTemplate BreadcrumbBarTemplate() => new(ctx =>
    {
        var overflowChip = new BreadcrumbBarItem
        {
            Content = "…",
            Visibility = Visibility.Collapsed, // raised by BreadcrumbBar.SetOverflowState when the fold elides
            IsTabStop = false
        };
        ctx.RegisterName("PART_OverflowChip", overflowChip);
        DockPanel.SetDock(overflowChip, Dock.Left);

        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);

        // Docked LEFT (not in the items panel): the ellipsis is not a segment, and a non-item child inside the
        // items host would break the ItemsPresenter's index-aligned container adoption. Docking it also makes the
        // reserve automatic — the DockPanel hands the panel the width that is left, which is exactly the width the
        // fold must pack into.
        var chips = new DockPanel();
        chips.Children.Add(overflowChip);
        chips.Children.Add(host);
        ctx.RegisterName("PART_ChipsHost", chips);

        var edit = new TextBox { Visibility = Visibility.Collapsed };
        ctx.RegisterName("PART_EditBox", edit);

        // The "▸" / ellipsis drop-down: a CompletionPopup in PICKER mode (scrollable, fuzzy-filtered, sorted),
        // parked in the template because a Popup surface needs an element IN THE TREE to hang from. It costs
        // the trail nothing — the control's own template root is a Grid holding only its Popup, so it measures
        // 0x0 and is not hit-testable. MaxVisibleItems is the whole point of the swap over a ContextMenu: past
        // twelve rows the list SCROLLS instead of growing taller than the terminal. The footer is ASCII for the
        // same reason CompletionPopup's default is (^v, not the ambiguous-width arrows).
        var dropDown = new CompletionPopup
                       {
                           MaxVisibleItems = BreadcrumbDropDownRows,
                           FooterContent = new TextBlock
                                           {
                                               Markup = BreadcrumbDropDownFooter,
                                               TextTrimming = TextTrimming.CharacterEllipsis,
                                               TextWrapping = WrapMode.NoWrap
                                           }
                       };
        ctx.RegisterName("PART_DropDown", dropDown);

        var content = new Grid();
        content.Children.Add(chips);
        content.Children.Add(edit);
        content.Children.Add(dropDown);

        var root = new Border { Child = content, Occludes = true };
        root.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        root.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        root.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        return root;
    });

    private static Style BreadcrumbBarTheme()
        => new Style
           {
               Key = "Theme.BreadcrumbBar",
               Children =
               {
                   // In edit mode the bar IS a field, so it takes the recessed well (the trail has no resting fill —
                   // it sits on the page). This also keeps the gutter around PART_EditBox from reading as page.
                   new Style("^:editing").SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush)
               }
           }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, BreadcrumbBarTemplate());

    // A chip + its trailing "▸". The fill rides the chip Border only (bound to Control.Background) so the
    // separator stays on the page between two filled chips — a hover that swallowed the separator would read as
    // one wide button rather than a trail. The current (deepest) segment hides its separator; that is driven by
    // BreadcrumbBarItem itself, not by a /template/ rule, so both theme halves behave identically.
    private static ControlTemplate BreadcrumbBarItemTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = true, ShowTrimmedContentInToolTip = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        presenter.SetBinding(TextElement.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));
        TextElement.ForwardInverse(presenter);

        var chip = new Border { Child = presenter, Occludes = true };
        chip.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        chip.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        chip.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        chip.SetBinding(TextElement.InverseProperty, TemplateBinding.From(TextElement.InverseProperty));

        var separator = new TextBlock { Text = "▸" };
        separator.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.MutedBrush);
        ctx.RegisterName("PART_Separator", separator);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(chip);
        row.Children.Add(separator);
        return row;
    });

    private static Style BreadcrumbBarItemTheme()
    {
        // Ancestors are dim ink, the current segment is full-strength: the trail visibly brightens toward where
        // you are. Ordered :current → :pointerover → :pressed → :focus-visible → :disabled, so interaction always
        // wins the pseudo-class tie (document order breaks it).
        var theme = new Style { Key = "Theme.BreadcrumbBarItem" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextDimBrush)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(Control.TemplateProperty, BreadcrumbBarItemTemplate());
        theme.Children.Add(new Style("^:current").SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush));
        theme.Children.Add(new Style("^:pointerover")
                              .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundHover)
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundHover));
        theme.Children.Add(new Style("^:pressed")
                              .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed));
        theme.Children.Add(new Style("^:focus-visible")
                              .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                              .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundFocus));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundDisabled));
        return theme;
    }

    // ───────────────────────────── CompletionPopup / CompletionListItem ─────────────────────────────

    // A completion overlay: an elevated occluding-by-caps panel over [header | PART_List | footer]. The control
    // element itself contributes nothing to the host layout — the template root is a Grid holding only the Popup,
    // which measures 0x0 — so a host can drop one in the same Grid cell as the field it decorates.
    //
    // Occludes is left FALSE here, exactly like the ComboBox drop-down: the blanket caps rules in
    // CursorialThemeStyles (:is(Border) under RequiresCapabilities="NoColor"/"Ansi16") already force it true on
    // every Border wholesale, and a per-control override would fight them on the tiers that can paint a real fill.
    //
    // NOTE — no {TemplateBinding} below the Popup. A Popup's Child is a LOGICAL child, and the template's
    // TemplatedParent stamp (ControlTemplate.StampTemplatedParent) walks VISUAL children only, so a
    // TemplateBinding authored inside the popup's content never resolves. DynamicResource / inheritance DO
    // cross the seam (they walk LogicalParent ?? TemplatedParent), which is why the palette references here are
    // fine. The three values that must reach the content — header text, footer text, the list's height cap —
    // are pushed onto the parts by CompletionPopup.UpdateChrome instead.
    private static ControlTemplate CompletionPopupTemplate() => new(ctx =>
    {
        // The "3 matches" strip. Collapsed until the control pushes text into it (ShowHeader + a live count) —
        // Collapsed rather than Hidden because the popup surface is sized to its content and a Hidden strip would
        // reserve a blank row.
        var header = new ContentPresenter { Visibility = Visibility.Collapsed, ShowTrimmedContentInToolTip = true };
        header.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        header.SetValue(TextBlock.TextWrappingProperty, WrapMode.NoWrap);
        header.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.MutedBrush);
        ctx.RegisterName("PART_Header", header);
        DockPanel.SetDock(header, Dock.Top);

        var footer = new ContentPresenter
                     {
                         Visibility = Visibility.Collapsed,
                         Margin = new(1, 0),
                         ShowTrimmedContentInToolTip = true
                     };
        footer.SetValue(TextElement.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        footer.SetValue(TextElement.TextWrappingProperty, WrapMode.NoWrap);
        footer.SetResourceReference(TextElement.BaseTextStyleProperty, ThemeKeys.FaintTextStyle);
        ctx.RegisterName("PART_Footer", footer);
        DockPanel.SetDock(footer, Dock.Bottom);

        var list = new CompletionList();
        ctx.RegisterName("PART_List", list);

        var stack = new DockPanel();
        stack.Children.Add(header);
        stack.Children.Add(footer);
        stack.Children.Add(list); // last child fills what the docked strips leave

        var content = new Border { Occludes = false, MinWidth = 16, Child = stack };
        content.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
        content.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);

        // StaysOpen: a TEXT-mode hint overlay is not light-dismissable — an outside press is the user dismissing
        // the FIELD, and the field's focus-out is what closes us. The control OWNS this value and rewrites it
        // before every open (CompletionPopup.UpdateDismissPolicy), because a target-less PICKER has no field for
        // that argument to protect and DOES light-dismiss, like every other chooser. What is set here is only the
        // initial value, and a custom template cannot accidentally settle the question either way.
        var popup = new Popup { Child = content, StaysOpen = true };
        popup.SetBinding(Popup.ShadowProperty, TemplateBinding.From(CompletionPopup.ShadowProperty));
        ctx.RegisterName("PART_Popup", popup);

        // The Popup adds no layout (0x0), so the control is a zero-size overlay anchor — but a Grid STRETCHES
        // to its cell, and the hit test gates IsHitTestVisible on the LEAF only (RenderTree §5.8: children stay
        // hittable, deliberately unlike WPF's subtree semantics). So the control's own IsHitTestVisible=false
        // does NOT cover this root, and an overlay parked in the same cell as the thing it decorates would eat
        // every press over that cell — the field it hangs off would stop taking clicks entirely. The popup's
        // Child lives on its own surface layer, so the rows themselves stay clickable.
        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(popup);
        return root;
    });

    private static Style CompletionPopupTheme()
        => new Style { Key = "Theme.CompletionPopup" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, CompletionPopupTemplate());

    // The list inside the popup: the ListBox chrome (Border > ScrollViewer > PART_ItemsHost) with NO recessed
    // WellBrush fill and no frame — the popup's own content Border already carries the elevation and the rule,
    // and a second well inside it reads as a box-in-a-box. It is its own theme entry rather than a
    // ControlThemeKey alias to ListBox because the declarative twin authors that rule as
    // <Style TargetType="ListBox">, which does not reach a subclass — aliasing the key would leave the XAML
    // half's list untemplated (no items host, so no visible rows) while the code-first half worked.
    private static Style CompletionListTheme()
        => new Style { Key = "Theme.CompletionList" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, ListBoxTemplate())
            .Set(ItemsControl.ItemsPanelProperty, VirtualizingItemsPanelTemplate());

    // A completion row: [icon] display-with-the-match-bolded … description  kind. Each side column carries its own
    // gutter margin and the control collapses it when its value is absent, so an icon-less / label-less candidate
    // costs no column. The display TextBlock is fed MARKUP (not Text) — that is how the fuzzy matcher's MatchSpans
    // become bold cells.
    private static ControlTemplate CompletionListItemTemplate() => new(ctx =>
    {
        var icon = new ContentPresenter { Margin = new Margins(0, 0, 1, 0), Visibility = Visibility.Collapsed };
        TextElement.ForwardInverse(icon);
        ctx.RegisterName("PART_Icon", icon);
        DockPanel.SetDock(icon, Dock.Left);

        var kind = new TextBlock { Margin = new Margins(1, 0, 0, 0), Visibility = Visibility.Collapsed };
        kind.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.MutedBrush);
        TextElement.ForwardInverse(kind);
        ctx.RegisterName("PART_KindLabel", kind);
        DockPanel.SetDock(kind, Dock.Right);

        var description = new TextBlock { Margin = new Margins(1, 0, 0, 0), Visibility = Visibility.Collapsed };
        description.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.FaintBrush);
        TextElement.ForwardInverse(description);
        ctx.RegisterName("PART_Description", description);
        DockPanel.SetDock(description, Dock.Right);

        var display = new TextBlock(); // inherits the row Foreground so the selection bar's flip reaches it
        ctx.RegisterName("PART_Display", display);
        TextElement.ForwardInverse(display);

        var row = new DockPanel();
        row.Children.Add(icon);
        row.Children.Add(kind);
        row.Children.Add(description);
        row.Children.Add(display);

        var border = new Border { Padding = new Margins(1, 0), Child = row };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        TextElement.ForwardInverse(border);
        return border;
    });

    private static Style CompletionListItemTheme()
    {
        // No :focus-visible rule: completion rows are never focusable (focus stays in the completed field), so the
        // keyboard cue rides :selected alone — the one place this row differs from the ListBoxItem bar it copies.
        var theme = new Style { Key = "Theme.CompletionListItem" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundNormal)
            .Set(Control.TemplateProperty, CompletionListItemTemplate());
        theme.Children.Add(new Style("^:pointerover")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundHover)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundHover));
        theme.Children.Add(new Style("^:selected")
            .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundSelected)
            .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundSelected));
        theme.Children.Add(new Style("^:disabled")
            .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundDisabled));
        return theme;
    }

    // ───────────────────────────── TreeView / TreeViewItem ─────────────────────────────

    // A TreeView: a Border over a ScrollViewer whose content is the PART_ItemsHost ItemsPresenter (so a tall tree
    // scrolls — the SCP band, C3). The tree sits on the page (no resting fill); the per-node selection bars are the
    // only fills.
    private static ControlTemplate TreeViewTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var scroll = new ScrollViewer { Content = host }; // the ItemsPresenter resolves its owner up the visual tree (CD-P9-17)
        scroll.SetBinding(ScrollViewer.VerticalScrollBarVisibilityProperty, TemplateBinding.From(ScrollViewer.VerticalScrollBarVisibilityProperty));
        scroll.SetBinding(ScrollViewer.HorizontalScrollBarVisibilityProperty, TemplateBinding.From(ScrollViewer.HorizontalScrollBarVisibilityProperty));
        ctx.RegisterName("PART_ScrollViewer", scroll);
        var border = new Border { Child = scroll };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        return border;
    });

    private static Style TreeViewTheme()
        => new Style
           {
               Key = "Theme.TreeView",
               Children =
               {
                   new Style("^:focus-within")
                   {
                       Children =
                       {
                           new Style("^ :is(TreeViewItem):selected")
                              .SetResource(Panel.BackgroundProperty, ThemeKeys.ListItemBackgroundSelected)
                              .SetResource(TextElement.ForegroundProperty, ThemeKeys.ListItemForegroundSelected),
                           new Style("^ :is(TreeViewItem):focus-visible:selected")
                              .SetResource(Panel.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                              .SetResource(TextElement.ForegroundProperty, ThemeKeys.ListItemForegroundFocus)
                       }
                   }

               }
           }
          .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
          .Set(Control.TemplateProperty, TreeViewTemplate());

    // A tree node: a header row [twisty(2) … Header] over the indented PART_ItemsHost (the children). The twisty
    // (PART_Twisty, fixed 2-wide; '>'/'v'/leaf-blank, set by the control) is the expander; the children host is
    // indented 2 cells so a child's twisty aligns under its parent's header — recursive nesting indents with no
    // depth math. Only the header bar carries Control.Background, so a :selected/:pointerover/:focus-visible fill
    // highlights the row, not the whole subtree.
    private static ControlTemplate TreeViewItemTemplate() => new(ctx =>
    {
        var twisty = new TextBlock { Width = 2 };
        twisty.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TreeItemForegroundNormal);
        ctx.RegisterName("PART_Twisty", twisty);
        DockPanel.SetDock(twisty, Dock.Left);

        // NOT RecognizesAccessKey — a tree node has no access-key activation, and an underscore in data (e.g. "my_file") must render literally.
        var header = new ContentPresenter { ShowTrimmedContentInToolTip = true }; 
        ctx.RegisterName("PART_HeaderPresenter", header);
        header.SetBinding(ContentPresenter.ContentProperty, TemplateBinding.From(HeaderedItemsControl.HeaderProperty));
        header.SetBinding(ContentPresenter.ContentTemplateProperty, TemplateBinding.From(HeaderedItemsControl.HeaderTemplateProperty)); // HDT renders the header

        var headerFace = new Border { Child = header };
        var headerRow = new DockPanel();
        headerRow.Children.Add(twisty);     // docked left (the expander)
        headerRow.Children.Add(headerFace); // fills the remaining width
        var bar = new Border { Child = headerRow };

        TextElement.ForwardInverse(headerFace);
        headerFace.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        headerFace.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        headerFace.SetValue(UIElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);

        var host = new ItemsPresenter { Margin = new Margins(2, 0, 0, 0) }; // recursive indent
        ctx.RegisterName("PART_ItemsHost", host);

        var root = new StackPanel(); // vertical: the header bar, then the (indented) children
        root.Children.Add(bar);
        root.Children.Add(host);
        return root;
    });

    private static Style TreeViewItemTheme()
    {
        var theme = new Style { Key = "Theme.TreeViewItem" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TreeItemForegroundNormal)
            .Set(Control.TemplateProperty, TreeViewItemTemplate());
        // NO :pointerover fill (WPF-faithful): InteractionState.PointerOver is set on EVERY ancestor of the hovered
        // leaf, and TreeViewItems nest, so a hover rule would light the whole ancestor header-bar chain. A tree node
        // highlights on selection + keyboard focus only.
        theme.Children.Add(new Style("^:selected")
            .SetResource(Control.BackgroundProperty, ThemeKeys.SelectionInactiveBrush));
        theme.Children.Add(new Style("^:focus")
            .SetResource(Control.BackgroundProperty, ThemeKeys.TreeItemBackgroundSelected));
        // The keyboard focus-row cue (reverse-video), ordered after :selected so a focused+selected node reads as
        // focused; :focus-visible (not :focus) so a mouse click shows :selected while keyboard nav shows the row.
        theme.Children.Add(new Style("^:focus-visible")
            .SetResource(Control.BackgroundProperty, ThemeKeys.TreeItemBackgroundFocus)
            .SetResource(Control.ForegroundProperty, ThemeKeys.TreeItemForegroundFocus));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.TreeItemForegroundDisabled));
        return theme;
    }

    // ───────────────────────────── Calendar / CalendarDayButton ─────────────────────────────

    // A Calendar: a bordered panel with a header row [< │ Month Year │ >] over the PART_MonthView StackPanel (the
    // Calendar populates the day grid in code). The whole widget is a single tab stop (Once); arrow keys are handled
    // by the Calendar to move the selected day. The prev/next glyphs are ASCII-safe (< >).
    private static ControlTemplate CalendarTemplate() => new(ctx =>
    {
        // The prev/next chrome are mouse-only (Focusable=false/IsTabStop=false, like the ScrollBar line buttons):
        // keyboard month nav is PageUp/PageDown, and a focusable arrow button would let focus sit off the day grid.
        var prev = new CalendarButton { Content = "<", Focusable = false, IsTabStop = false, Padding = new(1, 0)};
        ctx.RegisterName("PART_PreviousButton", prev);
        DockPanel.SetDock(prev, Dock.Left);

        var next = new CalendarButton { Content = ">", Focusable = false, IsTabStop = false, Padding = new(1, 0) };
        ctx.RegisterName("PART_NextButton", next);
        DockPanel.SetDock(next, Dock.Right);

        var headerText = new TextBlock { TextAlignment = TextAlignment.Center };
        ctx.RegisterName("PART_HeaderText", headerText);

        // The header label is a button (clicking it drills up Month→Year→Decade); mouse-only like the prev/next chrome
        // so keyboard focus stays on the grid cells. It hosts the PART_HeaderText label and fills the center.
        var headerButton = new CalendarButton { Content = headerText, Focusable = false, IsTabStop = false };
        ctx.RegisterName("PART_HeaderButton", headerButton);

        var header = new DockPanel { Margin = new(0, 0, 0, 1) };
        header.Children.Add(prev);         // docked left
        header.Children.Add(next);         // docked right
        header.Children.Add(headerButton); // fills the center
        DockPanel.SetDock(header, Dock.Top);

        var monthView = new StackPanel(); // the Calendar fills this (the day-of-week header row + six week rows)
        ctx.RegisterName("PART_MonthView", monthView);

        var root = new DockPanel();
        root.Children.Add(header);    // docked top (the month nav)
        root.Children.Add(monthView); // fills the rest (the day grid)
        KeyboardNavigation.SetTabNavigation(root, KeyboardNavigationMode.Once); // the whole calendar is one tab stop

        var border = new Border { /*Padding = new Margins(1, 0), */Child = root };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        return border;
    });

    private static Style CalendarTheme()
        => new Style { Key = "Theme.Calendar" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            // .SetResource(Control.BackgroundProperty, ThemeKeys.SurfaceBrush)
            .SetResource(Control.BorderPenProperty, ThemeKeys.BorderPen)
            // .Set(Control.BackgroundProperty, null)
            .Set(Control.TemplateProperty, CalendarTemplate());

    // A day cell: a fill-bounded ContentPresenter over the day number. :inactive (adjacent-month) is muted; :today is
    // accent ink; :selected is a SelectionBrush fill; :focus-visible (keyboard cursor) is reverse-video. Ordered so a
    // focused cell reads as focused, then selected over today over hover. Day cells don't nest, so :pointerover is safe.
    // A calendar cell (day, or a Year/Decade month/year): a fill-bounded ContentPresenter. Shared by CalendarDayButton
    // + CalendarButton — same visual contract.
    private static ControlTemplate CalendarCellTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border { Child = presenter };
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        return border;
    });

    // The shared :inactive/:pointerover/:today/:selected/:focus-visible/:disabled cell looks (ordered so a focused
    // cell reads as focused, then selected over today over hover). Cells don't nest, so ^:pointerover is safe.
    private static Style CalendarCellTheme(string key)
    {
        var theme = new Style { Key = key }
            .SetResource(Control.ForegroundProperty, ThemeKeys.CalendarDayForeground)
            .Set(Control.TemplateProperty, CalendarCellTemplate());
        theme.Children.Add(new Style("^:inactive").SetResource(Control.ForegroundProperty, ThemeKeys.CalendarDayInactiveForeground));
        theme.Children.Add(new Style("^:pointerover")
            .SetResource(Control.BackgroundProperty, ThemeKeys.CalendarDayBackgroundHover)
            .SetResource(Control.ForegroundProperty, ThemeKeys.CalendarDayForegroundHover));
        theme.Children.Add(new Style("^:today").SetResource(Control.ForegroundProperty, ThemeKeys.CalendarDayTodayForeground));
        theme.Children.Add(new Style("^:selected")
            .SetResource(Control.BackgroundProperty, ThemeKeys.CalendarDayBackgroundSelected)
            .SetResource(Control.ForegroundProperty, ThemeKeys.CalendarDayForegroundSelected));
        theme.Children.Add(new Style("^:focus-visible")
            .SetResource(Control.BackgroundProperty, ThemeKeys.CalendarDayBackgroundFocus)
            .SetResource(Control.ForegroundProperty, ThemeKeys.CalendarDayForegroundFocus));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.CalendarDayForegroundDisabled));
        return theme;
    }

    private static Style CalendarDayButtonTheme() => CalendarCellTheme("Theme.CalendarDayButton");

    // The Year/Decade month/year cell — identical state looks to a day cell.
    private static Style CalendarButtonTheme() => CalendarCellTheme("Theme.CalendarButton");

    // ───────────────────────────── Image ─────────────────────────────

    // An Image: its template hosts a PART_ImagePresenter with Source/placeholder one-way TemplateBound from the
    // control. The presenter draws the graphics-protocol image (or the placeholder when none renders).
    private static ControlTemplate ImageTemplate() => new(ctx =>
    {
        var presenter = new ImagePresenter();
        ctx.RegisterName("PART_ImagePresenter", presenter);
        presenter.SetBinding(ImagePresenter.SourceProperty, TemplateBinding.From(Image.SourceProperty));
        presenter.SetBinding(ImagePresenter.SourceUriProperty, TemplateBinding.From(Image.SourceUriProperty));
        presenter.SetBinding(ImagePresenter.PlaceholderContentProperty, TemplateBinding.From(Image.PlaceholderContentProperty));
        presenter.SetBinding(ImagePresenter.PlaceholderTemplateProperty, TemplateBinding.From(Image.PlaceholderTemplateProperty));
        return presenter;
    });

    private static Style ImageTheme()
        => new Style { Key = "Theme.Image" }.Set(Control.TemplateProperty, ImageTemplate());

    // A tiered Icon: a ContentPresenter renders the control's resolved tier (a glyph/text string, or a hosted
    // ImagePresenter). The Icon itself picks the tier (Glyph/Image/Text) from the terminal's capabilities; the
    // presenter just shows the result and inherits the icon's Foreground for the glyph/text tiers.
    private static ControlTemplate IconTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter();
        var border = new Border { Child = presenter, Occludes = true, Background = Brushes.Transparent };
        presenter.SetBinding(ContentPresenter.ContentProperty, TemplateBinding.From(Icon.ResolvedContentProperty));
        presenter.SetBinding(TextElement.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));
        TextElement.ForwardInverse(presenter);
        return border;
    });

    // Foreground is deliberately NOT pinned here (unlike Button/Label/StatusBarItem): an Icon is CONTENT, and
    // the doc contract is that it takes its host's ink by INHERITANCE. A pin sits at BindingPriority.Style,
    // which beats Inherited, so an icon inside a focused/pressed control would keep the resting TextBrush
    // while the face reverse-videos around it — TextBrush ink on a TextBrush fill, an invisible glyph. The
    // theme-flip staleness a pin would have papered over is fixed at its real source instead: the Icon's
    // cached EffectiveIconBrush (see Icon.OnApplicationResourcesChanged).
    private static Style IconTheme()
        => new Style { Key = "Theme.Icon" }
          .Set(Control.TemplateProperty, IconTemplate());

    // A Chart: its template hosts a PART_ChartPresenter with Source/placeholder one-way TemplateBound from the control.
    // The presenter draws the cell-rendered chart (or the placeholder when none is set).
    private static ControlTemplate ChartTemplate() => new(ctx =>
    {
        var presenter = new ChartPresenter();
        ctx.RegisterName("PART_ChartPresenter", presenter);
        presenter.SetBinding(ChartPresenter.SourceProperty, TemplateBinding.From(Chart.SourceProperty));
        presenter.SetBinding(ChartPresenter.PlaceholderContentProperty, TemplateBinding.From(Chart.PlaceholderContentProperty));
        presenter.SetBinding(ChartPresenter.PlaceholderTemplateProperty, TemplateBinding.From(Chart.PlaceholderTemplateProperty));
        return presenter;
    });

    private static Style ChartTheme()
        => new Style { Key = "Theme.Chart" }.Set(Control.TemplateProperty, ChartTemplate());

    // ───────────────────────────── DatePicker ─────────────────────────────

    // A DatePicker: a recessed WellBrush field [date text … 'v' drop glyph] that toggles a Popup hosting a Calendar
    // (PART_Calendar) — the ListBox-in-Popup recipe with a Calendar instead of a list. ASCII-safe drop glyph.
    private static ControlTemplate DatePickerTemplate()
    {
        var t = new ControlTemplate(
            ctx =>
                 {
                     // Read-only face: the selected date / watermark (visible when !IsEditable; the DatePicker toggles visibility).
                     var text = new TextBlock();
                     ctx.RegisterName("PART_DisplayText", text);

                     // Editable face: a text box to type a date (collapsed unless IsEditable).
                     var editable = new TextBox
                                    {
                                        Visibility = Visibility.Collapsed,
                                        Padding = Margins.Zero,
                                        Background = null,
                                        Cursor = MouseCursorShape.Text
                                    };

                     ctx.RegisterName("PART_EditableTextBox", editable);

                     editable.SetBinding(TextBox.PlaceholderProperty,
                                         TemplateBinding.From(DatePicker.WatermarkProperty));

                     var content = new Grid(); // the two faces overlap in one cell; the collapsed one takes no space
                     content.Children.Add(text);
                     content.Children.Add(editable);

                     // Drop caret (▾) — the bare muted glyph that toggles the calendar (the DatePicker wires its Click).
                     var drop = DropDownCaret();
                     ctx.RegisterName("PART_DropDown", drop);
                     DockPanel.SetDock(drop, Dock.Right);

                     var row = new DockPanel();
                     row.Children.Add(drop);    // docked right (the drop indicator + toggle)
                     row.Children.Add(content); // fills the remaining width (the face)

                     var field = new Border { Child = row };
                     field.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
                     field.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
                     field.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));

                     // No BorderPen literal: the popup surface Border carries the themed border; a literal here
                     // would be the part's resting truth under the amended lattice (§20, 2026-07-12) and the
                     // Calendar theme's resting BorderPen could no longer replace it. (A red debug pen lived
                     // here until the 2026-07-12 pre-cleanup — masked under the old ladder, visible under the new.)
                     var calendar = new Calendar();
                     ctx.RegisterName("PART_Calendar", calendar);

                     var surface = new Border { /*Occludes = true, */Child = calendar };

                     surface.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
                     surface.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
                     var popup = new Popup { Child = surface };
                     ctx.RegisterName("PART_Popup", popup);

                     var root = new Grid(); // the Popup adds no layout (0×0); the field fills the cell
                     root.Children.Add(field);
                     root.Children.Add(popup);
                     root.Styles.Add(new Style(Selectors.OfType<Calendar>()).Set(Control.BorderPenProperty, null));
                     return root;
                 });

        t.Styles.Add(
            new Style(Selectors.OfType<DatePicker>())
            {
                Children =
                {
                    new Style("^:focus /template/ TextBox#PART_EditableTextBox")
                       .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                       .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundFocus),
                    new Style("^:pointerover /template/ TextBox#PART_EditableTextBox")
                       .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundHover)
                       .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundHover),
                    new Style("^:disabled /template/ TextBox#PART_EditableTextBox")
                       .SetResource(Control.BackgroundProperty, ThemeKeys.DisabledBackgroundBrush)
                       .SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush)
                }
            });
        
        return t;
    }

    private static Style DatePickerTheme()
    =>  new Style
        {
            Key = "Theme.DatePicker",
            Children =
            {
                new Style("^:focus")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundFocus)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundFocus),
                new Style("^:pointerover")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.ListItemBackgroundHover)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.ListItemForegroundHover),
                new Style("^:disabled")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.DisabledBackgroundBrush)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush),
                new Style("^")
                    {
                        When =
                        {
                            new DataCondition(CompiledBinding.For(DatePicker.IsEditableProperty,
                                                                  relativeSource: RelativeSource.Self), value: false),
                            new DataCondition(CompiledBinding.For(DatePicker.SelectedDateProperty,
                                                                  relativeSource: RelativeSource.Self,
                                                                  fallbackValue: BindingBase.NullValue), value: null)
                        }
                    }
                   .SetResource(Control.ForegroundProperty, ThemeKeys.FaintBrush)
            }
        }.SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush) // a recessed field
         .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
         .Set(Control.PaddingProperty, new Margins(1,0,0,0))
         .Set(UIElement.MinWidthProperty, 15)
         .Set(Control.TemplateProperty, DatePickerTemplate());

    // ───────────────────────────── Menu / MenuItem / Separator ─────────────────────────────

    // The horizontal menu bar: a SurfaceBrush strip hosting the top-level items (the Menu's ItemsPanel is a
    // horizontal StackPanel — set in its ctor).
    private static ControlTemplate MenuTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var border = new Border { Child = host };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        return border;
    });

    private static Style MenuTheme()
        => new Style
           {
               Key = "Theme.Menu",
               Children =
               {
                   // Light separator inside menu
                   // new Style(Selectors.Nesting().OfType<Separator>()).SetResource(Control.BorderPenProperty, ThemeKeys.MenuSeparatorPen)
               }
           }
            .SetResource(Control.BackgroundProperty, ThemeKeys.MenuBarBackground)
            .SetResource(Control.ForegroundProperty, ThemeKeys.MenuForegroundNormal)
            .Set(Control.TemplateProperty, MenuTemplate());

    // A ContextMenu: a popup-rooted occluding panel (overwrites the content it floats over) hosting the
    // PART_ItemsHost ItemsPresenter; the default vertical StackPanel stacks its MenuItems (design doc §12.7).
    private static ControlTemplate ContextMenuTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var border = new Border { /*Occludes = true, */Child = host };
        border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
        border.SetResourceReference(Border.BorderPenProperty, ThemeKeys.MenuBorderPen);
        return border;
    });

    private static Style ContextMenuTheme()
        => new Style { Key = "Theme.ContextMenu" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.MenuForegroundNormal)
            .Set(Control.TemplateProperty, ContextMenuTemplate());

    // A ToolTip: an occluding bordered panel (overwrites the content it floats over) wrapping a ContentPresenter;
    // capped at 40 cells wide with the content wrapping (design doc §12.7).
    private static ControlTemplate ToolTipTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border { /*Occludes = true, */Padding = new Margins(1, 0), Child = presenter };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        return border;
    });

    private static Style ToolTipTheme()
        => new Style { Key = "Theme.ToolTip" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.BackgroundProperty, ThemeKeys.ElevationPopup)
            .SetResource(Control.BorderPenProperty, ThemeKeys.ToolTipBorderPen)
            .Set(UIElement.MaxWidthProperty, ToolTipService.MaxToolTipWidth) // spec §12.7: max 40 cells, content wraps
            .Set(Control.TemplateProperty, ToolTipTemplate());

    // ───────────────────────────── TabControl / TabItem ─────────────────────────────

    // A TabControl: a DockPanel with the tab strip (PART_TabStrip ItemsPresenter, docked top — the row of headers)
    // over a bordered content host (PART_ContentHost ContentPresenter showing the selected tab's SelectedContent).
    private static ControlTemplate TabControlTemplate() =>
        new(ctx =>
            {
                var strip = new Border
                            {
                                Child = new ItemsPresenter(),
                                Background = Brushes.Transparent,
                                Occludes = true,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                Margin = new Margins(1, 0, 1, 0),
                                IsRenderBoundary = true
                            };

                // strip.SetBinding(Border.BackgroundProperty, TemplateBinding.From<TabControl, IBrush?>(Control.BackgroundProperty));
                
                ctx.RegisterName("PART_TabStrip", strip);
                DockPanel.SetDock(strip, Dock.Top);

                var content = new ContentPresenter();
                ctx.RegisterName("PART_ContentHost", content);
                content.SetBinding(ContentPresenter.ContentProperty, TemplateBinding.From(TabControl.SelectedContentProperty));
                content.SetBinding(ContentPresenter.ContentTemplateProperty, TemplateBinding.From(TabControl.ContentTemplateProperty));
                content.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextBrush);
                var body = new Border { Child = content };
                body.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
                // strip.SetBinding(Border.BorderPenProperty, TemplateBinding.From<TabControl, Pen?>(Control.BorderPenProperty));

                var panel = new DockPanel();
                panel.Children.Add(strip); // docked top (the header row)
                panel.Children.Add(body);  // fills the rest (the selected tab's body)

                var border = new Border();
                border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
                border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));

                var root = new Grid { Children = { border, panel } };
                return root;
            });

    private static Style TabControlTheme()
        => new Style { Key = "Theme.TabControl" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.BorderPenProperty, ThemeKeys.TabControlBorderPen)
            .Set(Control.PaddingProperty, new Margins(2, 1))
            .Set(Control.TemplateProperty, TabControlTemplate());

    // A tab header (gallery §TabControl): inactive tabs are --text-dim ink; the active tab is a --surface fill +
    // --text ink marked by an --accent underline bar below the header; :pointerover = --hover. The bar is a 1-row
    // Separator under the header (the "active tab marked by accent bar (━ cells)" cue).
    private static ControlTemplate TabItemTemplate()
    {
        var t = new ControlTemplate(
        ctx =>
           {
               var header = new ContentPresenter { RecognizesAccessKey = true, ShowTrimmedContentInToolTip = true };
               ctx.RegisterName("PART_ContentPresenter", header);
               header.SetBinding(ContentPresenter.ContentProperty, TemplateBinding.From(HeaderedContentControl.HeaderProperty));
               // The fill rides the HEADER row only — not the underline row (which sits on the page like the gallery bar).
               var headerHost = new Border { Padding = new Margins(1, 0), Child = header, Background = Brushes.Transparent, Occludes = true };
               headerHost.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
               headerHost.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
               // The header face forwards the Inverse axis: the NoColor selection rule now targets the
               // TABITEM (control-level — non-inheriting axes cannot leak into the page content), and
               // this forward + the header presenter's leaf forward deliver it to fill + label.
               TextElement.ForwardInverse(header);
               TextElement.ForwardInverse(headerHost);
               ctx.RegisterName("PART_HeaderSite", headerHost);

               // The active-tab accent underline (gallery "active tab marked by accent bar (━ cells)"): a 1-row
               // Separator below the header, with the SAME (1,0) side padding as the header so the ━ aligns under the
               // label. TabItem.OnApplyTemplate paints its Heavy --accent pen at LocalValue (a /template/ theme rule
               // can't override the Separator's OWN control-theme pen) and toggles its visibility on selection (the
               // bar row stays laid out so the strip aligns). Named for GetTemplatePart.
               var underline = new Separator { Margin = new Margins(1, 0, 1, 0) }; // matching side padding (no fill row)
               underline.SetResourceReference(Control.BorderPenProperty, ThemeKeys.TabUnderlinePen);
               ctx.RegisterName("PART_Underline", underline);

               var stack = new StackPanel(); // vertical: the filled header row, then the page-bg underline row
               stack.Children.Add(headerHost);
               stack.Children.Add(underline);
               return new Border { Child = stack }; // root container (no fill — the fill rides headerHost)
           });

        t.Styles.Add(
            new Style(Selectors.OfType<TabItem>())
            {
                Children =
                {
                    new Style(Selectors.Nesting().PseudoClass(":focus").Template().OfType<Separator>().Name("PART_Underline"))
                       .SetResource(Border.BorderPenProperty, ThemeKeys.TabFocusedUnderlinePen), // focus underline pen

                    new Style(Selectors.Nesting().Template().Name("PART_HeaderSite"))
                        .SetResource(TextElement.ForegroundProperty, ThemeKeys.TabForegroundNormal),

                    new Style(Selectors.Nesting().PseudoClass(":pointerover").Template().Name("PART_HeaderSite"))
                        .SetResource(TextElement.ForegroundProperty, ThemeKeys.TabForegroundHover)
                        .SetResource(Panel.BackgroundProperty, ThemeKeys.TabBackgroundHover),
                    
                    new Style(Selectors.Nesting().PseudoClass(":selected").Template().Name("PART_HeaderSite"))
                        .SetResource(Panel.BackgroundProperty, ThemeKeys.TabBackgroundSelected) // --surface fill
                        .SetResource(TextElement.ForegroundProperty, ThemeKeys.TabForegroundSelected),  // --text ink

                    new Style(Selectors.Nesting().PseudoClass(":focus").Template().Name("PART_HeaderSite"))
                       .SetResource(TextElement.ForegroundProperty, ThemeKeys.TabForegroundFocused)
                       .SetResource(Panel.BackgroundProperty, ThemeKeys.TabBackgroundFocused),

                    new Style(Selectors.Nesting().PseudoClass(":disabled").Template().Name("PART_HeaderSite"))
                        .SetResource(TextElement.ForegroundProperty, ThemeKeys.TabForegroundDisabled)
                }
            }
        );

        return t;
    }

    private static Style TabItemTheme()
    {
        var theme = new Style { Key = "Theme.TabItem" }
            .Set(Control.TemplateProperty, TabItemTemplate());
        // theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.TabBackgroundHover));
        // theme.Children.Add(new Style("^:selected")
        //     .SetResource(Control.BackgroundProperty, ThemeKeys.TabBackgroundSelected)  // --surface fill
        //     .SetResource(Control.ForegroundProperty, ThemeKeys.TabForegroundSelected));  // --text ink
        // theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.TabForegroundDisabled));
        return theme;
    }

    // ───────────────────────────── ProgressBar ─────────────────────────────

    // ProgressBar paints itself (no template): the track is the recessed WellBrush, the determinate fill is green,
    // and the indeterminate sweep is accent (the gallery mockup — green determinate, accent indeterminate).
    private static Style ProgressBarTheme()
    {
        var theme = new Style { Key = "Theme.ProgressBar" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.ProgressTrackBrush)
            .SetResource(ProgressBar.FillProperty, ThemeKeys.ProgressFillNormal);
        theme.Children.Add(new Style("^:indeterminate").SetResource(ProgressBar.FillProperty, ThemeKeys.ProgressFillIndeterminate));
        return theme;
    }

    // ───────────────────────────── Slider ─────────────────────────────

    // A RangeBase value picker: Slider.Render draws two heavy ━ rail segments — the FILLED (value) side in the
    // accent SliderFilledPen, the empty remainder in the faint SliderTrackPen (= BorderPen here); the PART_Thumb
    // marker fills with the accent and rides the split. Disabled collapses the filled side to the faint track.
    private static Style SliderTheme()
    {
        var theme = new Style { Key = "Theme.Slider" }
            .Set(Control.TemplateProperty, SliderTemplate())
            .SetResource(Control.ForegroundProperty, ThemeKeys.AccentBrush)
            .SetResource(Slider.FilledPenProperty, ThemeKeys.SliderFilledPen)
            .SetResource(Control.BorderPenProperty, ThemeKeys.SliderTrackPen);
        theme.Children.Add(new Style("^:disabled").SetResource(Slider.FilledPenProperty, ThemeKeys.SliderTrackPen));
        theme.Children.Add(new Style("^:focus").SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush));
        return theme;
    }

    // The thumb's accent fill is authored as a `^ /template/ Thumb` rule in the TEMPLATE's Styles — the Template(1)
    // layer (CD30), the engine's sanctioned crossing into a template part. (The same rule placed in the control
    // theme's Children would be element-addressed and never match the part — #115; ControlTemplate.Styles is the
    // canonical home for part rules, and the resource reference still re-skins live on a theme flip.)
    private static ControlTemplate SliderTemplate()
    {
        var template = new ControlTemplate(ctx =>
        {
            var thumb = new Thumb();
            ctx.RegisterName("PART_Thumb", thumb);
            thumb.SetBinding(Control.BackgroundProperty, TemplateBinding.From(Control.ForegroundProperty));
            return thumb;
        });
        // template.Styles.Add(new Style(Selectors.Nesting().Template().OfType<Thumb>())
        //     .SetResource(Control.BackgroundProperty, ThemeKeys.SuccessBrush));
        return template;
    }

    // ───────────────────────────── GridSplitter ─────────────────────────────

    // A draggable divider (a Thumb that fills its grid cell with its Background): a muted groove that brightens to
    // the accent on hover so the resize affordance reads. No template — Thumb.Render paints the Background.
    private static Style GridSplitterTheme()
    {
        var theme = new Style { Key = "Theme.GridSplitter" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.MutedBrush);
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.AccentBrush));
        return theme;
    }

    // ───────────────────────────── StatusBar / StatusBarItem ─────────────────────────────

    // A one-row docked bar: a Border (StatusBarBackground) wrapping the PART_ItemsHost; the StatusBar's own
    // DockPanel ItemsPanel docks the StatusBarItems left-to-right (right-dockable per item).
    private static Style StatusBarTheme()
        => new Style { Key = "Theme.StatusBar" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.StatusBarBackground)
            .Set(Control.TemplateProperty, StatusBarTemplate());

    private static ControlTemplate StatusBarTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var border = new Border { Child = host };
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        return border;
    });

    // A status-bar cell: a padded ContentPresenter.
    private static Style StatusBarItemTheme()
        => new Style
           {
               Key = "Theme.StatusBarItem",
               Children =
               {
                   new Style(Selectors.Nesting().Class("alternate"))
                      .SetResource(Control.ForegroundProperty, ThemeKeys.StatusBarAltForeground)
                      .SetResource(Control.BackgroundProperty, ThemeKeys.StatusBarAltBackground)
               }
           }.SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush)
            .SetResource(Control.BackgroundProperty, ThemeKeys.StatusBarBackground)
            .Set(Control.TemplateProperty, StatusBarItemTemplate());

    private static ControlTemplate StatusBarItemTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter{ ShowTrimmedContentInToolTip = true };
        var root = new Border { Child = presenter };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        root.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        root.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        root.SetBinding(TextElement.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));
        return root;
    });

    // ───────────────────────────── Expander ─────────────────────────────

    // A collapsible region: a clickable header Button (PART_HeaderButton) carrying the >/v twisty (PART_Glyph) +
    // the Header, over a PART_Content presenter the Expander shows/hides by IsExpanded. Down-expanding (v1).
    private static Style ExpanderTheme()
        => new Style { Key = "Theme.Expander" }
            .Set(Control.TemplateProperty, ExpanderTemplate());

    private static ControlTemplate ExpanderTemplate()
    {
        var t = new ControlTemplate( 
            ctx =>
           {
               // The header presenter binds the Header — directly in the template (TemplatedParent is the Expander), NOT
               // nested in a Button, or the TemplateBinding would resolve against the button instead.
               var header = new ToggleButton { Name = "PART_Header" };

               header.SetBinding(ContentControl.ContentProperty, 
                                 TemplateBinding.From(HeaderedContentControl.HeaderProperty));

               // TwoWay is outside TemplateBinding's contract (BD15) — the compiled TemplatedParent
               // form carries it: the GetValue leaf synthesizes the SetValue write-back.
               header.SetBinding(ToggleButton.IsCheckedProperty,
                                 CompiledBinding.For(Expander.IsExpandedProperty,
                                                      mode: BindingMode.TwoWay,
                                                      source: ctx.TemplatedParent));

               DockPanel.SetDock(header, Dock.Top);
               
               ctx.RegisterName("PART_Header", header);

               var content = new ContentPresenter(); // auto-aliases to the Expander's Content (A22)
               
               content.SetBinding(UIElement.HorizontalAlignmentProperty, 
                                  TemplateBinding.From(ContentControl.HorizontalContentAlignmentProperty));

               content.SetBinding(UIElement.VerticalAlignmentProperty, 
                                  TemplateBinding.From(ContentControl.VerticalContentAlignmentProperty));
               
               ctx.RegisterName("PART_Content", content);

               var root = new DockPanel { LastChildFill = true };
               root.Children.Add(header);
               root.Children.Add(content);

               return root;
           });

        //                                v-- Use ButtonBase as the type argument so we don't get state coloring.
        var ht = new ControlTemplate(
            build: tbCtx =>
                   {
                       var border = new Border();

                       border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
                       border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
                       border.SetBinding(TextElement.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));

                       var glyph = new TextBlock { Margin = new(0, 0, 1, 0) }; // collapsed caret (U+25B8); Expander flips to ▾ when expanded

                       var headerPresenter = new ContentPresenter { RecognizesAccessKey = true, ShowTrimmedContentInToolTip = true };
                       var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(1, 0) };

                       glyph.SetBinding(TextBlock.ForegroundProperty, TemplateBinding.From(Control.ForegroundProperty));

                       glyph.SetBinding(
                           TextBlock.TextProperty,
                           CompiledBinding.For(ToggleButton.IsCheckedProperty,
                                               mode: BindingMode.OneWay,
                                               converter: new ExpandedToGlyphConverter(),
                                               relativeSource: RelativeSource.TemplatedParent)
                       );

                       tbCtx.RegisterName("PART_Glyph", glyph);

                       headerRow.Children.Add(glyph);
                       headerRow.Children.Add(headerPresenter);

                       border.Child = headerRow;
                       return border;
                   });
        
        var headerStyle = AddButtonStates<ButtonBase>(
                new Style(Selectors.Nesting().Template().OfType<ToggleButton>().Name("PART_Header"))
            )
           .Set(Control.TemplateProperty, ht);

        t.Styles.Add(new Style(Selectors.OfType<Expander>()) { Children = { headerStyle } });

        return t;
    }

    private sealed class ExpandedToGlyphConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (targetType != typeof(string))
                return UIProperty.UnsetValue;

            return value switch
                   {
                       true  => "⏷",
                       false => "⏵",
                       _     => "?"
                   };
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    // ───────────────────────────── TextBox ─────────────────────────────

    // A fill-bounded text field (gallery TextBox mockup, lines 605–609): a Border (Padding 1,0) over the
    // PART_TextPresenter that paints the line. Per-state brush pairs come from the spine — resting
    // SurfaceBrush + TextBrush, :pointerover HoverBrush, :focus the recessed WellBrush (text focus is the
    // well + the blinking-bar caret, NOT reverse-video — adoption-spec §1/§7), :disabled the disabled pair.
    // Selection (SelectionBrush) + placeholder (MutedBrush/Faint) are painted by the presenter; the caret is
    // the real terminal cursor it publishes. MinWidth gives an unconstrained empty field a usable width.
    private static ControlTemplate TextBoxTemplate() => new(ctx =>
    {
        var presenter = new TextPresenter();
        ctx.RegisterName("PART_TextPresenter", presenter);
        var border = new Border { Child = presenter };
        border.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, TemplateBinding.From(Control.BorderPenProperty));
        border.SetBinding(Border.PaddingProperty, TemplateBinding.From(Control.PaddingProperty));
        TextElement.ForwardInverse(border);
        TextElement.ForwardBaseTextStyle(presenter);
        TextElement.ForwardAllAxes(presenter);
        TextElement.ForwardTypography(presenter);
        return border;
    });

    private static Style TextBoxTheme()
    {
        var theme = new Style { Key = "Theme.TextBox" }
                   .SetResource(TextBox.BaseTextStyleProperty, ThemeKeys.InputNormalTextStyle)
                   .SetResource(TextBox.SelectedTextStyleProperty, ThemeKeys.InputSelectionInactiveTextStyle)
                   .Set(Control.PaddingProperty, new Margins(1, 0))
                   .Set(UIElement.MinWidthProperty, 12)
                   .Set(Control.TemplateProperty, TextBoxTemplate());

        theme.Children.Add(new Style("^:pointerover")
                              .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.InputHoverTextStyle));

        theme.Children.Add(new Style("^:focus")
                          .SetResource(TextBox.SelectedTextStyleProperty, ThemeKeys.InputSelectionTextStyle)
                          .SetResource(TextBox.BaseTextStyleProperty, ThemeKeys.InputFocusTextStyle));

        theme.Children.Add(new Style("^:disabled")
                              .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.InputDisabledTextStyle));
        return theme;
    }

    // A menu item: a fill-bounded header row [header … gesture], plus the submenu Popup (PART_Popup) whose Child is
    // an occluding PanelBrush surface hosting the sub-items (PART_ItemsHost). The Popup contributes no layout to the
    // row (a Grid cell stacks it behind the face at 0×0). :highlighted = SelectionBrush fill; :disabled = muted.
    private static ControlTemplate MenuItemTemplate()
    {
        var t = new ControlTemplate(
            ctx =>
                 {
                     var header = new ContentPresenter { RecognizesAccessKey = true };
                     ctx.RegisterName("PART_ContentPresenter", header);

                     header.SetBinding(ContentPresenter.ContentProperty,
                                       TemplateBinding.From(HeaderedItemsControl.HeaderProperty));

                     var icon = new ContentPresenter
                                {
                                    Visibility = Visibility.Collapsed,
                                    ForwardsFromTemplatedParent = false
                                };

                     var iconTray = new Border { Width = 2, Height = 1, Child = icon, Margin = new Margins(0, 0, 1, 0) };

                     ctx.RegisterName("PART_IconTray", iconTray);
                     ctx.RegisterName("PART_Icon", icon);
                     
                     DockPanel.SetDock(iconTray, Dock.Left);

                     iconTray.SetBinding(UIElement.VisibilityProperty,
                                         TemplateBinding.From(
                                             MenuItem.IsIconTrayVisibleProperty,
                                             converter: BooleanToVisibilityConverter.Instance));
                     
                     var submenuIndicator = new TextBlock { RenderOffsetColumn = 1, Text = "▸" };
                     ctx.RegisterName("PART_SubmenuIndicator", submenuIndicator);

                     submenuIndicator.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.MenuAcceleratorForeground);

                     submenuIndicator.SetBinding(UIElement.VisibilityProperty,
                                                 CompiledBinding.For(MenuItem.HasItemsProperty,
                                                                     relativeSource: RelativeSource.TemplatedParent,
                                                                     converter: BooleanToVisibilityConverter.Instance));

                     DockPanel.SetDock(submenuIndicator, Dock.Right);

                     var gesture = new TextBlock { Margin = new Margins(2, 0, 0, 0) };
                     ctx.RegisterName("PART_GestureText", gesture);

                     gesture.SetBinding(TextBlock.TextProperty, TemplateBinding.From(MenuItem.InputGestureTextProperty));
                     gesture.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MenuAcceleratorForeground);

                     DockPanel.SetDock(gesture, Dock.Right);

                     var row = new DockPanel();
                     row.Children.Add(iconTray);         // docked far-right
                     row.Children.Add(submenuIndicator); // docked far-right
                     row.Children.Add(gesture);          // docked right (faint gesture hint)
                     row.Children.Add(header);           // fills the remaining width

                     var face = new Border { Padding = new Margins(1, 0), Child = row };
                     face.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
                     
                     TextElement.ForwardInverse(face);
                     TextElement.ForwardInverse(gesture);
                     TextElement.ForwardInverse(iconTray);
                     TextElement.ForwardInverse(submenuIndicator);

                     var itemsHost = new ItemsPresenter();
                     ctx.RegisterName("PART_ItemsHost", itemsHost);

                     var submenu = new Border
                                   {
                                       /*Occludes = true, */Child = itemsHost
                                   };

                     submenu.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationPopup);
                     submenu.SetResourceReference(Border.BorderPenProperty, ThemeKeys.MenuBorderPen);
                     var popup = new Popup { Child = submenu };
                     ctx.RegisterName("PART_Popup", popup);

                     var root = new Grid(); // the Popup adds no layout (0×0); the face fills the cell
                     root.Children.Add(face);
                     root.Children.Add(popup);
                     return root;
                 });

        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().Template().OfType<Separator>().Name("PART_Separator"))
               .SetResource(Control.BorderPenProperty, ThemeKeys.MenuSeparatorPen)
        );

        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":highlighted").Template().OfType<TextBlock>().Name("PART_GestureText")
                               .Or(Selectors.OfType<MenuItem>().PseudoClass(":highlighted").Template().OfType<TextBlock>().Name("PART_SubmenuIndicator"))
                               .Or(Selectors.OfType<MenuItem>().PseudoClass(":open").Template().OfType<TextBlock>().Name("PART_GestureText"))
                               .Or(Selectors.OfType<MenuItem>().PseudoClass(":open").Template().OfType<TextBlock>().Name("PART_SubmenuIndicator")))
               .SetResource(TextElement.ForegroundProperty, ThemeKeys.MenuAcceleratorHoverForeground)
        );

        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":top-level").Template().OfType<TextBlock>().Name("PART_SubmenuIndicator"))
               .Set(UIElement.VisibilityProperty, Visibility.Collapsed)
        );

        // By default, dim the foreground of the icon/checkmark when checkable but unchecked
        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":checkable").Template().OfType<Border>().Name("PART_IconTray"))
               .SetResource(Icon.IconBrushProperty, ThemeKeys.MenuIconUncheckedForeground)
        );

        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":checkable").PseudoClass(":highlighted").Template().OfType<Border>().Name("PART_IconTray")
                               .Or(Selectors.OfType<MenuItem>().PseudoClass(":checkable").PseudoClass(":open").Template().OfType<Border>().Name("PART_IconTray")))
               .SetResource(Icon.IconBrushProperty, ThemeKeys.MenuIconUncheckedHoverForeground)
        );

        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":checked").PseudoClass(":highlighted").Template().OfType<Border>().Name("PART_IconTray")
                               .Or(Selectors.OfType<MenuItem>().PseudoClass(":checked").PseudoClass(":open").Template().OfType<Border>().Name("PART_IconTray")))
               .SetResource(Icon.IconBrushProperty, ThemeKeys.MenuIconCheckedForeground)
        );

        // By default, toggle the foreground ONLY of the icon/checkmark when checked
        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":checked").Template().OfType<Border>().Name("PART_IconTray"))
               .SetResource(Icon.IconBrushProperty, ThemeKeys.MenuIconCheckedForeground)
        );

        // By default, toggle text weight of icon/checkmark to BOLD when checked
        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":checked").Template().OfType<ContentPresenter>().Name("PART_Icon"))
               .Set(TextElement.TextWeightProperty, TextWeight.Bold)
        );

        // If emoji are enabled but NOT nerd fonts, toggle the foreground AND background of the icon/checkmark when checked
        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":checked").Template().OfType<Border>().Name("PART_IconTray"))
                {
                    RequiresCapabilities = StyleCapabilities.Emoji
                }
               .SetResource(Icon.IconBrushProperty, ThemeKeys.MenuIconCheckedForeground)
               .SetResource(Border.BackgroundProperty, ThemeKeys.SuccessInverseBrush)
        );
        // If nerd fonts are enabled, toggle the foreground ONLY of the icon/checkmark when checked (overrides the emoji rule when both are present)
        t.Styles.Add(
            new Style(Selectors.OfType<MenuItem>().PseudoClass(":checked").Template().OfType<Border>().Name("PART_IconTray"))
                {
                    RequiresCapabilities = StyleCapabilities.NerdFont
                }
               .SetResource(Icon.IconBrushProperty, ThemeKeys.MenuIconCheckedForeground)
               .Set(Border.BackgroundProperty, null)
        );

        // t.Styles.Add(
        //     new Style(Selectors.OfType<MenuItem>().PseudoClass(":highlighted").Template().OfType<Border>().Name("PART_IconTray"))
        //        .SetResource(Border.BackgroundProperty, ThemeKeys.MenuBackgroundHighlighted)
        // );
        //
        // t.Styles.Add(
        //     new Style(Selectors.OfType<MenuItem>().PseudoClass(":pointerover").Template().OfType<Border>().Name("PART_IconTray"))
        //        .SetResource(Border.BackgroundProperty, ThemeKeys.MenuBackgroundHover)
        // );
        
        return t;
    }

    private static Style MenuItemTheme()
    {
        var theme = new Style { Key = "Theme.MenuItem" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.MenuForegroundNormal)
            .Set(Control.TemplateProperty, MenuItemTemplate());
        theme.Children.Add(new Style("^:pointerover")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.MenuBackgroundHover)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.MenuForegroundHover));
        theme.Children.Add(new Style("^:highlighted")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.MenuBackgroundHighlighted)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.MenuForegroundHighlighted));
        theme.Children.Add(new Style("^:open")
                          .SetResource(Control.BackgroundProperty, ThemeKeys.MenuBackgroundHighlighted)
                          .SetResource(Control.ForegroundProperty, ThemeKeys.MenuForegroundHighlighted));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.MenuForegroundDisabled));
        return theme;
    }

    // A 1-row muted rule between items.
    private static Style SeparatorTheme()
        => new Style
           {
               Key = "Theme.Separator",
               Children =
               {
                   new Style(Selectors.Nesting().PseudoClass(":within-menu"))
                       .SetResource(Control.BorderPenProperty, ThemeKeys.MenuSeparatorPen)
               }
           }
            .Set(Control.BackgroundProperty, null)
            .SetResource(Control.BorderPenProperty, ThemeKeys.SeparatorPen)
            .Set(UIElement.HeightProperty, 1);

    private static Style ListBoxItemTheme()
    {
        var theme = new Style { Key = "Theme.ListBoxItem" }
            .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemNormalTextStyle)
            .Set(Control.PaddingProperty, new(1, 0))
            .Set(Control.TemplateProperty, ListBoxItemTemplate());

        theme.Children.Add(new Style("^:pointerover")
                              .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemHoverTextStyle));
        theme.Children.Add(new Style("^:selected")
                              .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemSelectedInactiveTextStyle));
        // The keyboard focus-row cue (gallery .item.rev): reverse-video — ordered AFTER :selected so a
        // focused+selected current item reads as focused (adoption-spec lines 108-110). :focus-visible (not :focus)
        // so a mouse click — Pointer modality — shows :selected, while keyboard nav shows the reverse row.
        theme.Children.Add(new Style("^:focus-visible")
                              .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemFocusTextStyle));
        theme.Children.Add(new Style("^:disabled")
                              .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemDisabledTextStyle));
        return theme;
    }

    private static Style RepeatButtonTheme()
        => AddButtonStates<RepeatButton>(ApplyPaletteSpine(new Style { Key = "Theme.RepeatButton" }, ThemeKeys.ButtonForegroundNormal, ThemeKeys.ButtonBackgroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center)
            .Set(Control.TemplateProperty, ButtonContentTemplate()));

    private static Style ToggleButtonTheme()
        => AddButtonStates<ToggleButton>(ApplyPaletteSpine(new Style { Key = "Theme.ToggleButton" }, ThemeKeys.ButtonForegroundNormal, ThemeKeys.ButtonBackgroundNormal)
            .Set(Control.PaddingProperty, new Margins(1, 0))
            .Set(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center)
            .Set(Control.TemplateProperty, ButtonContentTemplate()));

    // The cell-faithful interactive states shared by the button family (design doc §11.8a): hover = a
    // fill swap; focus = reverse-video (TextBrush fill + WindowBackground text); pressed = accent reverse-video;
    // disabled = disabled fill + muted text. All brush-pair setters are ResourceReferences into the
    // palette spine (color tiers); the NoColor interactive-state distinction rides the caps-nocolor
    // theme-styles rules (the per-axis TextElement.Inverse / TextWeight cue, delivered by the face +
    // presenter forwards; see CursorialThemeStyles).
    // Ordered hover → focus → pressed → disabled so the higher-intent state wins on a pseudo-class tie.
    private static Style AddButtonStates<TButton>(Style theme) where TButton : ButtonBase
    {
        theme.SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundNormal)
             .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundNormal);

        theme.Children.Add(
            new Style("^:default")
               .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundNormal)
               .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundDefault));

        theme.Children.Add(
            new Style("^:pointerover")
               .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundHover)
               .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundHover));

        theme.Children.Add(
            new Style("^:focus")
               .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
               .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundFocus));

        theme.Children.Add(
            new Style("^:pressed")
               .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundPressed)
               .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundPressed));

        if (typeof(ToggleButton).IsAssignableFrom(typeof(TButton)))
        {
            theme.Children.Add(
                new Style("^.toggle-colors:checked")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.SuccessBrush)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
            
            theme.Children.Add(
                new Style("^.toggle-colors:checked:pointerover")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.Success2Brush)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));

            theme.Children.Add(
                new Style("^.toggle-colors:checked:focus")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.SuccessInverseBrush));

            theme.Children.Add(
                new Style("^.toggle-colors:checked:pressed")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.SuccessDarkBrush)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));

            theme.Children.Add(
                new Style("^.toggle-colors:indeterminate")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.WarningBrush)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
            
            theme.Children.Add(
                new Style("^.toggle-colors:indeterminate:pointerover")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.Warning2Brush)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
            
            theme.Children.Add(
                new Style("^.toggle-colors:indeterminate:focus")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundFocus)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.WarningInverseBrush));

            theme.Children.Add(
                new Style("^.toggle-colors:indeterminate:pressed")
                   .SetResource(Control.BackgroundProperty, ThemeKeys.WarningDarkBrush)
                   .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
        }
        
        theme.Children.Add(
            new Style("^:disabled")
               .SetResource(Control.BackgroundProperty, ThemeKeys.ButtonBackgroundDisabled)
               .SetResource(Control.ForegroundProperty, ThemeKeys.ButtonForegroundDisabled));

        return theme;
    }

    // The R2 palette spine (design doc §11.5 / §11.8a): a default-look control (no explicit Foreground /
    // Background) resolves its resting fill + ink from the ThemeKeys palette as DynamicResources, so it
    // re-skins per-variant on a dark/light flip or a single ThemeKeys override with zero template work.
    // The setters arm at the ControlTheme layer (below LocalValue), so an explicit value the consumer
    // sets always wins. Cell-faithful reversal of the WPF transparent-Background default: the resting
    // SurfaceBrush fill IS the control's extent (no border). The toggle-glyph controls (CheckBox/Radio)
    // do NOT take this spine — they stay transparent at rest (gallery: their normal fill is the page bg).
    private static Style ApplyPaletteSpine(Style theme, string foregroundKey, string backgroundKey)
        => theme
            .SetResource(Control.ForegroundProperty, foregroundKey)
            .SetResource(Control.BackgroundProperty, backgroundKey);

    // ───────────────────────────── CheckBox / RadioButton ─────────────────────────────

    // glyph cell + 1 space + ContentPresenter (spec line 660), wrapped in a fill Border. The glyph element
    // reads the owning toggle's IsChecked + the glyph-set resource (ASCII default, overridable).
    private static ControlTemplate ToggleGlyphTemplate<T>(string glyphKey, string checkedMarkKey, string indeterminateMarkKey)
        where T : UIElement
    {
        var t = new ControlTemplate(
            ctx =>
               {
                   var row = new DockPanel();

                   // The glyph cell overlays a zero-size Caret on the glyph's INNER cell (column 1 — the space inside
                   // [ ] / ( )). A single-cell Grid stacks both; the Caret is Left/Top-pinned with a 1-column left
                   // margin so it lands on the box's middle cell. It shows only under :focus-visible (driven in code by
                   // ToggleButton). Check/radio focus is this IN-BOX CARET, not reverse-video: a check/radio stretches
                   // to its StackPanel's width, so a reverse fill would span the whole row like a selection bar.
                   var glyphCell = new Grid
                                   {
                                       Margin = new Margins(0, 0, 1, 0),
                                       VerticalAlignment = VerticalAlignment.Top
                                   };
                   var glyph = new ToggleGlyph(glyphKey, checkedMarkKey, indeterminateMarkKey);
                   // A checkbox/radio indicator is a GLYPH, not text — only Inverse forwards, so the box
                   // swaps fg/bg in unison with an inverted face and never leaves a half-inverted hole
                   // (owner rule 2026-07-13). Weight/style/underline are meaningless on a symbol.
                   TextElement.ForwardInverse(glyph);
                   ctx.RegisterName("PART_Glyph", glyph);

                   var caret = new Caret
                               {
                                   HorizontalAlignment = HorizontalAlignment.Left,
                                   VerticalAlignment = VerticalAlignment.Top,
                                   Margin = new Margins(1, 0, 0, 0)
                               };

                   ctx.RegisterName("PART_Caret", caret);
                   glyphCell.Children.Add(glyph);
                   glyphCell.Children.Add(caret);

                   var presenter = new ContentPresenter
                                   {
                                       RecognizesAccessKey = true,
                                       ShowTrimmedContentInToolTip = true
                                   };
                   ctx.RegisterName("PART_ContentPresenter", presenter);

                   DockPanel.SetDock(glyphCell, Dock.Left);

                   row.Children.Add(glyphCell);
                   row.Children.Add(presenter);

                   // The face follows Control.Background — unset (transparent) at rest and there is no fill setter:
                   // check/radio never fill (a stretched row would fill full-width like a selection bar). The binding
                   // stays so a consumer that sets Background still paints. Focus is the in-box caret, not a fill.
                   var face = new Border { Child = row };
                   TextElement.ForwardInverse(face);
                   face.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));
                   return face;
               });
        
        // Hover brightens the ink to the accent (the design guide's box-brightens-on-hover cue) — these fill-less
        // controls can't use a background highlight (it would span the full stretched width), so the foreground IS
        // the affordance. (The mark keeps its own checked color, set by the ToggleGlyph leaf.)
        t.Styles.Add(
            new Style(Selectors.OfType<T>())
            {
                Children =
                {
                    new Style(Selectors.Nesting().Template().Name("PART_Glyph"))
                        .SetResource(ToggleGlyph.GlyphForegroundProperty, ThemeKeys.MutedBrush),
                    new Style(Selectors.Nesting().PseudoClass(":focus").Template().Name("PART_Glyph"))
                        .SetResource(ToggleGlyph.GlyphForegroundProperty, ThemeKeys.TextBrush),
                    new Style(Selectors.Nesting().PseudoClass(":pointerover").Template().Name("PART_Glyph"))
                        .SetResource(ToggleGlyph.GlyphForegroundProperty, ThemeKeys.AccentBrush),
                    new Style(Selectors.Nesting().PseudoClass(":disabled").Template().Name("PART_Glyph"))
                        .SetResource(ToggleGlyph.GlyphForegroundProperty, ThemeKeys.DisabledForegroundBrush)
                }
            }
        );

        return t;
    }

    // themeKey is the diagnostic Style.Key (e.g. "Theme.CheckBox"); glyphKey is the ThemeKeys glyph-set
    // resource the ToggleGlyph leaf reads (already a fully-qualified "Theme.*" constant — never re-prefix it).
    // No fill at all: transparent at rest AND on hover (a stretched check/radio would fill full-width); the
    // glyph + content paint in the inherited Foreground, disabled = muted ink, focus = the in-box caret
    // (driven by ToggleButton), never reverse-video.
    private static Style ToggleGlyphTheme<T>(string themeKey, string glyphKey, string checkedMarkKey, string indeterminateMarkKey)
        where T : UIElement
    {
        var theme = new Style { Key = themeKey }
            .SetResource(Control.ForegroundProperty, ThemeKeys.ToggleForegroundNormal)
            .Set(Control.TemplateProperty, ToggleGlyphTemplate<T>(glyphKey, checkedMarkKey, indeterminateMarkKey));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.ToggleForegroundDisabled));
        return theme;
    }

    // ───────────────────────────── ScrollBar / ScrollViewer ─────────────────────────────

    // A borderless glyph-button template: a single glyph (no border/padding), so a 1-cell-wide part
    // fits (the bordered ButtonContentTemplate would draw a │ frame). No built-in template uses it
    // today (the ScrollBar's arrow line-buttons were retired for the bare-track rendering); kept for
    // the ComboBox caret and future 1-cell glyph parts.
    private static ControlTemplate BareGlyphButtonTemplate()
    {
        var t = new ControlTemplate(
            ctx =>
            {
                var presenter = new ContentPresenter { RecognizesAccessKey = false };
                ctx.RegisterName("PART_ContentPresenter", presenter);
                return presenter;
            });

        t.Styles.Add(
            new Style(Selectors.OfType<RepeatButton>())
            {
                Children =
                {
                    new Style("^:pointerover /template/ #PART_ContentPresenter")
                       .SetResource(Control.ForegroundProperty, ThemeKeys.AccentBrush),
                            
                    new Style("^:pressed /template/ #PART_ContentPresenter")
                       .SetResource(Control.ForegroundProperty, ThemeKeys.AccentInverseBrush)
                }
            });

        return t;
    }

    // PART_Track (required, CD19/C231/C236) — the bare-track rendering: no arrow line-buttons (a
    // custom template may still register PART_LineUpButton/PART_LineDownButton; the ScrollBar wiring
    // stays live). The track is the internal Track primitive in Fill display mode, filling the bar.
    private static ControlTemplate ScrollBarTemplate(bool horizontal)
    {
        var t = new ControlTemplate(
            ctx =>
            {
               var dock = new DockPanel();

               var owner = (ScrollBar) (ctx.TemplatedParent ??
                                        throw new InvalidOperationException("The ScrollBar theme template requires a templated parent."));

               var track = new Track(owner) { TrackDisplayMode = TrackDisplayMode.Fill };

               if (horizontal)
                   track.Height = 1; // ensure vertical measure
               else
                   track.Width = 1; // ensure horizontal measure
               
               ctx.RegisterName("PART_Track", track);

               dock.Children.Add(track); // last child fills the remaining space (DockPanel default)
               return dock;
            });

        return t;
    }

    private static Style ScrollBarTheme()
    {
        var theme = new Style { Key = "Theme.ScrollBar" }
            .Set(Control.TemplateProperty, ScrollBarTemplate(horizontal: false))
            .SetResource(Control.BorderPenProperty, ThemeKeys.BorderPen);

        // A horizontal bar uses the rotated template (a 1-row track along the long axis).
        theme.Children.Add(new Style("^:horizontal").Set(Control.TemplateProperty, ScrollBarTemplate(horizontal: true)));

        return theme;
    }

    // PART_ScrollContentPresenter (required) + a PART_VerticalScrollBar docked right (CD28/C235).
    private static ControlTemplate ScrollViewerTemplate() => new(ctx =>
    {
        var dock = new DockPanel();
        dock.SetBinding(Panel.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));

        var vBar = new ScrollBar { Orientation = Orientation.Vertical };
        ctx.RegisterName("PART_VerticalScrollBar", vBar);
        DockPanel.SetDock(vBar, Dock.Right);

        var hBar = new ScrollBar { Orientation = Orientation.Horizontal };
        ctx.RegisterName("PART_HorizontalScrollBar", hBar);
        DockPanel.SetDock(hBar, Dock.Bottom);

        var presenter = new ScrollContentPresenter();
        ctx.RegisterName("PART_ScrollContentPresenter", presenter);

        dock.Children.Add(vBar);
        dock.Children.Add(hBar);
        dock.Children.Add(presenter); // fills the remaining space
        return dock;
    });

    private static Style ScrollViewerTheme()
        => new Style { Key = "Theme.ScrollViewer" }.Set(Control.TemplateProperty, ScrollViewerTemplate());

    // DataTemplate for instantiating an Icon element from an IconCarrier.
    private static DataTemplate IconCarrierTemplate()
        => new()
           {
               DataType = typeof(IconCarrier),
               Content = new FuncTemplateContent(
                   ctx =>
                   {
                       var icon = new Icon();

                       icon.SetBinding(Icon.GlyphProperty,
                                       CompiledBinding.Build((IconCarrier c) => c.Glyph)
                                                      .Step(nameof(IconCarrier.Glyph))
                                                      .Build());

                       icon.SetBinding(Icon.GlyphWidthProperty,
                                       CompiledBinding.Build((IconCarrier c) => c.GlyphWidth)
                                                      .Step(nameof(IconCarrier.GlyphWidth))
                                                      .Build());

                       icon.SetBinding(Icon.ImageUriProperty,
                                       CompiledBinding.Build((IconCarrier c) => c.ImageUri)
                                                      .Step(nameof(IconCarrier.ImageUri))
                                                      .Build());

                       icon.SetBinding(Icon.EmojiProperty,
                                       CompiledBinding.Build((IconCarrier c) => c.Emoji)
                                                      .Step(nameof(IconCarrier.Emoji))
                                                      .Build());

                       icon.SetBinding(Icon.TextProperty,
                                       CompiledBinding.Build((IconCarrier c) => c.Text)
                                                      .Step(nameof(IconCarrier.Text))
                                                      .Build());

                       icon.SetBinding(Icon.IconBrushProperty,
                                       CompiledBinding.Build((IconCarrier c) => c.IconBrush,
                                                             targetNullValue: Binding.DoNothing)
                                                      .Step(nameof(IconCarrier.IconBrush))
                                                      .Build());
                       
                       return icon;
                   })
           };
    // ───────────────────────────── Window (S4 chrome — C4, punch 36) ─────────────────────────────

    // The themed window chrome (design doc §8.3) — the C4 replacement for S4's interim default template,
    // now resolved through the control-theme chain (CursorialTheme.BuiltIn is the backstop), so an app
    // Window style overrides it (the b13fc2a Template-lane precedence fix makes part overrides land
    // correctly). Borderless, fill-bounded (the tokyo-night "borderless windows" model): a window separates
    // from the desktop by tonal ELEVATION, not a drawn frame — the root is an opaque occluding fill with NO
    // resting pen (the body defaults to PanelBrush via the theme setter; an explicit Window.BorderPen opts a
    // frame back in via the surviving binding). Drop-shadow elevation is WM/surface-applied (W5), not painted
    // here. The title band doubles as the Drag grab; its fill + ink track the window's active state via the
    // active-look below (the interim's Activated/Deactivated mechanism, relocated into the theme template; a
    // declarative `^:active-window /template/ …` rule is a future refinement now the Template lane exists).
    private static ControlTemplate WindowChromeTemplate()
    {
        var t = new ControlTemplate(
            ctx =>
            {
                var window = (Window) (ctx.TemplatedParent
                                       ?? throw new InvalidOperationException(
                                           "The window chrome template requires a templated parent."));

                var presenter = new ContentPresenter();
                ctx.RegisterName("PART_ContentPresenter", presenter);

                presenter.SetBinding(UIElement.MarginProperty,
                                     TemplateBinding.From(Control.PaddingProperty)); // opt-in frame only

                var root = new Border();
                ctx.RegisterName("PART_Root", root);
                root.SetBinding(Border.BackgroundProperty, TemplateBinding.From(Control.BackgroundProperty));

                root.SetBinding(Border.BorderPenProperty,
                                TemplateBinding.From(Control.BorderPenProperty)); // opt-in frame only

                if (window.WindowStyle == WindowStyle.None)
                {
                    root.Child = presenter; // chrome-less, still an opaque occluding fill
                    return root;
                }

                var layout = new DockPanel();

                // The title bar is a filled band that doubles as the Drag grab area (§8.3); its fill + ink track the
                // window's active state (ApplyActiveLook): an accent band + on-accent ink when active, a recessed
                // surface band + dim ink when inactive — the borderless focus cue is the band colour, not a border.
                var titleBar = new Border { Padding = new Margins(1, 0) };

                titleBar.SetResourceReference(Border.BackgroundProperty, ThemeKeys.WindowTitleBarBackground);

                titleBar.SetResourceReference(TextElement.ForegroundProperty,
                                              ThemeKeys.WindowTitleBarForeground);

                WindowChrome.SetHitTestRole(titleBar, WindowHitTestRole.Drag);
                ctx.RegisterName("PART_TitleBar", titleBar);

                var titleBarContent = new DockPanel(); // LastChildFill → the title fills the remainder

                var icon = new ContentPresenter
                           {
                               Margin = new Margins(0, 0, 1, 0),
                               Visibility = Visibility.Collapsed,
                               ForwardsFromTemplatedParent = false,
                               ForwardTextInverse =
                                   false // we toggle the icon's inverse directly; see `IconToggleStyle()`.
                           };

                icon.SetBinding(ContentPresenter.ContentProperty, TemplateBinding.From(Window.IconProperty));
                ctx.RegisterName("PART_Icon", icon);
                DockPanel.SetDock(icon, Dock.Left);
                titleBarContent.Children.Add(icon);

                // Close ✕ — a bare glyph on the band: a transparent local Background beats the Button theme's fills,
                // so it reads as a glyph, not a button face. Close stays the button's own Click (the role switch
                // intentionally leaves Close off).
                var closeButton = new Button { Focusable = false, IsTabStop = false, Content = "✕" };

                closeButton.SetBinding(UIElement.VisibilityProperty,
                                       TemplateBinding.From(Window.CanCloseProperty,
                                                          converter: BooleanToVisibilityConverter.Instance));

                WindowChrome.SetHitTestRole(closeButton, WindowHitTestRole.Close);

                closeButton.Click += (_, _) =>
                                     {
                                         if (window.CanClose)
                                             window.Close(WindowCloseReason.ChromeAction);
                                     };

                DockPanel.SetDock(closeButton, Dock.Right);
                titleBarContent.Children.Add(closeButton); // docked right → rightmost control
                ctx.RegisterName("PART_CloseButton", closeButton);

                // Maximize ▢ — a role-driven glyph (its press bubbles to the window, which toggles Maximized via the
                // Maximize role, like the ◢ grip's ResizeSE). Only when the window can resize.
                Button? maximizeButton = null;

                if (window.CanResize)
                {
                    maximizeButton = new Button { Focusable = false, IsTabStop = false };

                    maximizeButton.SetBinding(ContentControl.ContentProperty,
                                              TemplateBinding.From(
                                                  Window.WindowStateProperty,
                                                  converter: new WindowStateToGlyphConverter()));

                    // WindowChrome.SetHitTestRole(maximizeGlyph, WindowHitTestRole.Maximize);
                    DockPanel.SetDock(maximizeButton, Dock.Right);
                    titleBarContent.Children.Add(maximizeButton); // docked right → left of the close glyph
                    ctx.RegisterName("PART_MaximizeButton", maximizeButton);

                    maximizeButton.Click += (_, _) =>
                                            {
                                                window.WindowState = window.WindowState is WindowState.Maximized
                                                                         ? WindowState.Normal
                                                                         : WindowState.Maximized;

                                                maximizeButton.Content =
                                                    window.WindowState is WindowState.Maximized ? "⧈" : "⛶";
                                            };
                }

                var titleText = new ContentPresenter
                                {
                                    Content = window.Title ?? string.Empty,
                                    ShowTrimmedContentInToolTip = true
                                };

                titleText.SetBinding(ContentPresenter.ContentProperty,
                                     CompiledBinding.For(Window.TitleProperty, source: window));

                titleBarContent.Children.Add(titleText); // last child → fills the drag area, shows the title
                ctx.RegisterName("PART_Title", titleText);

                titleBar.Child = titleBarContent;
                DockPanel.SetDock(titleBar, Dock.Top);
                layout.Children.Add(titleBar);

                // The active-state look (the band/ink that tracks Window.IsActive) is wired in Window.OnApplyTemplate
                // against the PART_TitleBar/PART_Title/PART_CloseButton/PART_MaximizeButton parts and unhooked in
                // Window.OnTemplateDetaching — so re-templating a live window never leaks the Activated/Deactivated
                // handlers (the P9-closeout fix for the interim's lifetime-of-the-window subscription edge).

                if (window.CanResize)
                {
                    // The ◢ resize grip overlays the content's bottom-right corner (ResizeSE), stacked over the
                    // presenter in a single-cell Grid so it costs no layout row.
                    var body = new Grid();
                    body.Children.Add(presenter);

                    var grip = new TextBlock
                               {
                                   Text = "◢",
                                   HorizontalAlignment = HorizontalAlignment.Right,
                                   VerticalAlignment = VerticalAlignment.Bottom,
                                   Cursor = MouseCursorShape.NwseResize
                               };

                    grip.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
                    WindowChrome.SetHitTestRole(grip, WindowHitTestRole.ResizeSE);
                    ctx.RegisterName("PART_ResizeGrip", grip);
                    body.Children.Add(grip);

                    layout.Children.Add(body); // fills the remainder
                }
                else
                {
                    layout.Children.Add(presenter); // fills the remainder
                }

                root.Child = layout;

                var overlayHost = new Border
                                  {
                                      Name = "PART_ObscuredOverlay",
                                      Visibility = Visibility.Collapsed,
                                      IsRenderBoundary = true,
                                      IsHitTestVisible = true
                                  };

                overlayHost.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ObscuredOverlayBrush);
                ctx.RegisterName("PART_ObscuredOverlay", overlayHost);

                var chrome = new Grid { Children = { root, overlayHost } };

                closeButton.Style = new Style
                                    {
                                        Children =
                                        {
                                            new Style(Selectors.Nesting().PseudoClass(":pointerover"))
                                               .SetResource(Control.BackgroundProperty, ThemeKeys.RedBrush)
                                               .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush),
                                            new Style(Selectors.Nesting().PseudoClass(":pressed"))
                                               .SetResource(Control.BackgroundProperty, ThemeKeys.ElevationHighest)
                                               .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextBrush)
                                        }
                                    };

                maximizeButton?.Style = new Style
                                        {
                                            Children =
                                            {
                                                new Style(Selectors.Nesting().PseudoClass(":pointerover"))
                                                   .SetResource(Control.BackgroundProperty, ThemeKeys.GreenBrush)
                                                   .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush),
                                                new Style(Selectors.Nesting().PseudoClass(":pressed"))
                                                   .SetResource(Control.BackgroundProperty, ThemeKeys.ElevationHighest)
                                                   .SetResource(TextElement.ForegroundProperty, ThemeKeys.TextBrush)
                                            }
                                        };

                chrome.Styles.Add(
                    new Style(Selectors.Is<Window>().Class("obscured"))
                    {
                        Children =
                        {
                            new Style(Selectors.Nesting().Template().Name("PART_ObscuredOverlay"))
                               .Set(UIElement.VisibilityProperty, Visibility.Visible)
                        }
                    });

               return chrome;
            });

        t.Styles.Add(
            new Style(Selectors.OfType<Window>()
                               .PseudoClass(":has-icon")
                               .Template()
                               .Name("PART_TitleBar")
                               .Descendant()
                               .Name("PART_Icon"))
               .Set(UIElement.VisibilityProperty, Visibility.Visible));

        t.Styles.Add(
            new Style(Selectors.Is<Window>())
            {
                Children =
                {
                    new Style(Selectors.Nesting().PseudoClass(":active-window").Template().Name("PART_TitleBar"))
                       .SetResource(Border.BackgroundProperty, ThemeKeys.WindowTitleBarActiveBackground)
                       .SetResource(TextElement.ForegroundProperty, ThemeKeys.WindowTitleBarActiveForeground),
                    new Style(Selectors.Nesting().PseudoClass(":active-window").Template().Name("PART_TitleBar").Descendant().OfType<Button>())
                       .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush),
                }
            });

        t.Styles.Add(
            new Style(Selectors.Is<Window>())
            {
                Children =
                {
                    new Style(Selectors.Nesting().Template().Name("PART_CloseButton"))
                       .Set(Control.BackgroundProperty, null)
                       .SetResource(Control.ForegroundProperty, ThemeKeys.RedBrush),
                    new Style(Selectors.Nesting().PseudoClass(":active-window").Template().Name("PART_CloseButton"))
                       .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush)
                }
            });

        t.Styles.Add(
            new Style(Selectors.Is<Window>())
            {
                Children =
                {
                    new Style(Selectors.Nesting().Template().Name("PART_MaximizeButton"))
                       .Set(Control.BackgroundProperty, null)
                       .SetResource(Control.ForegroundProperty, ThemeKeys.GreenBrush),
                    new Style(Selectors.Nesting().PseudoClass(":active-window").Template().Name("PART_MaximizeButton"))
                       .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush)
                }
            });

        return t;
    }

    private static Style WindowTheme()
        => new Style
           {
               Key = "Theme.Window",
               Children =
               {
                   new Style(Selectors.Nesting().Is<Window>().Class("modal"))
                      .Set(UIElement.OpacityProperty, 0.95)
                      .SetResource(Control.BackgroundProperty, ThemeKeys.ElevationDialog),
                   new Style(Selectors.Nesting().OfType<Window>())
                       {
                           When = { new DataCondition(CompiledBinding.For(Window.IsActiveProperty,
                                                                          relativeSource: RelativeSource.Self),
                                                      false) },
                           Setters =
                           {
                               new Setter(Transition.TransitionsProperty,
                                          new TransitionCollection
                                          {
                                              new DoubleTransition(UIElement.OpacityProperty)
                                              {
                                                  Delay = TimeSpan.FromMilliseconds(500),
                                                  Easing = Easings.QuadOut,
                                                  Duration = TimeSpan.FromMilliseconds(2000)
                                              }
                                          }),
                               new Setter(UIElement.OpacityProperty, 0.70)
                           }
                       }
               }
           }
            .SetResource(Control.BackgroundProperty, ThemeKeys.ElevationWindow) // borderless body surface; a consumer LocalValue Background wins
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.PaddingProperty, new(2, 1))
            .Set(Control.TemplateProperty, WindowChromeTemplate());
    
    private sealed class WindowStateToGlyphConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return (value as WindowState?) switch { WindowState.Maximized => "⧈", _ => "⛶" };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value switch
                   {
                       "⧈" => WindowState.Maximized,
                       _   => WindowState.Normal
                   };
        }
    }
}

/// <summary>
/// The check/radio glyph cell of a <see cref="CheckBox"/>/<see cref="RadioButton"/> default template
/// (design doc §12.7, spec line 660): a leaf that reads the owning toggle's
/// <see cref="ToggleButton.IsChecked"/> and the glyph-set resource (<see cref="ThemeKeys.CheckBoxGlyphs"/>
/// / <see cref="ThemeKeys.RadioGlyphs"/>; ASCII <c>[ ] [x] [-]</c> / <c>( ) (*)</c> by default,
/// overridable at any chain scope) and draws the matching glyph. The glyph is foreground text in the
/// toggle's <see cref="Control.Foreground"/> (inherited). Public + XAML-authorable so the control themes
/// can be authored declaratively (Cursorial.UI.Themes): set <see cref="GlyphKey"/> /
/// <see cref="CheckedMarkKey"/> / <see cref="IndeterminateMarkKey"/> in the template.
/// </summary>
public sealed class ToggleGlyph : UIElement, IValueObserver<bool?>
{
    /// <summary>The <see cref="ThemeKeys"/> glyph-set resource key (the <c>(Unchecked, Checked, Indeterminate)</c> <see cref="GlyphSetCarrier"/> base).</summary>
    public string? GlyphKey { get; set; }

    /// <summary>The <see cref="ThemeKeys"/> brush key coloring the CHECKED inner mark (e.g., GreenBrush ✓ / AccentBrush ●); <c>null</c> leaves it in the foreground.</summary>
    public string? CheckedMarkKey { get; set; }

    /// <summary>The <see cref="ThemeKeys"/> brush key coloring the INDETERMINATE inner mark (e.g., AmberBrush ▪); <c>null</c> leaves it in the foreground.</summary>
    public string? IndeterminateMarkKey { get; set; }

    /// <inheritdoc cref="GlyphForegroundProperty"/>
    public IBrush? GlyphForeground
    {
        get => GetValue(GlyphForegroundProperty);
        set => SetValue(GlyphForegroundProperty, value);
    }
    
    // The caps-unicode glyph-set override (design doc §12.7 / SD14): CursorialThemeStyles' `.caps-unicode`
    // rules set this per control type to opt the marks UP from the ASCII resource base to Unicode (✓/▪/●);
    // ToggleGlyph reads it off its Owner, falling back to the glyph-set resource when unset (a caps-ascii
    // terminal, or no caps-unicode source). Hosted on ToggleButton so a `.caps-unicode CheckBox`/
    // `RadioButton` theme-styles rule can set it. AffectsRender — the ASCII↔Unicode marks are equal-width.
    public static readonly AttachedProperty<GlyphSetCarrier?> GlyphsProperty =
        UIProperty.RegisterAttached<ToggleGlyph, ToggleButton, GlyphSetCarrier?>("Glyphs");

    /// <summary>
    /// The foreground brush with which to render the glyph brackets.
    /// </summary>
    public static readonly StyledProperty<IBrush?> GlyphForegroundProperty =
        UIProperty.Register<ToggleGlyph, IBrush?>(nameof(GlyphForeground));

    public static readonly StyledProperty<bool?> IsCheckedProperty =
        ToggleButton.IsCheckedProperty.AddOwner<ToggleGlyph>();
    
    static ToggleGlyph()
    {
        AddGlobalEffects(PropertyEffects.AffectsRender, GlyphsProperty);
        AffectsRender<ToggleGlyph>(GlyphForegroundProperty, IsCheckedProperty);
    }

    /// <summary>Parameterless constructor for XAML; set <see cref="GlyphKey"/> + the mark keys via properties.</summary>
    public ToggleGlyph()
    {
        this.SetBinding(IsCheckedProperty,
                        TemplateBinding.From(IsCheckedProperty, mode: BindingMode.OneWay));

        this.SetBinding(GlyphsProperty,
                        TemplateBinding.From(GlyphsProperty, mode: BindingMode.OneWay));
    }

    /// <summary>
    /// The code-first constructor. <paramref name="checkedMarkKey"/> / <paramref name="indeterminateMarkKey"/>
    /// are ThemeKeys brush resources that color the INNER mark for the checked / indeterminate states
    /// (CheckBox ✓ = GreenBrush, ▪ = AmberBrush; RadioButton ● = AccentBrush); null leaves the mark in the
    /// inherited foreground. The brackets always paint in the foreground.
    /// </summary>
    public ToggleGlyph(string glyphKey, string? checkedMarkKey = null, string? indeterminateMarkKey = null) : this()
    {
        GlyphKey = glyphKey;
        CheckedMarkKey = checkedMarkKey;
        IndeterminateMarkKey = indeterminateMarkKey;
    }

    private ToggleButton? Owner => TemplatedParent as ToggleButton;

    /// <inheritdoc/>
    void IValueObserver<bool?>.OnPropertyChanged(UIObject source, UIProperty property, bool? oldValue, bool? newValue, BindingPriority priority)
        => InvalidateVisual();

    private GlyphSetCarrier Glyphs
    {
        get
        {
            // The .caps-unicode override (set on the Owner by a theme-styles rule) wins; else the glyph-set
            // resource (the ASCII base, themeable at any scope); else the hard ASCII fallback.
            if (GetValue(GlyphsProperty) is { } unicode)
                return unicode;

            return GlyphKey is {} key &&
                   this.TryFindResource(key, out var value) &&
                   value is GlyphSetCarrier glyphs
                       ? glyphs
                       : new GlyphSetCarrier("[ ]", "[x]", "[-]");
        }
    }

    // The owner's checked state, defaulting to unchecked only when there is no owner — a null here is
    // the genuine *indeterminate* value, which the '?? false' coalesce must NOT collapse.
    public bool? IsChecked => GetValue(IsCheckedProperty);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var glyph = Glyphs.ForChecked(IsChecked);
        return new Size(Text.GraphemeWidth.StringWidth(glyph), 1);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (context.Bounds.IsEffectivelyEmpty)
            return;

        var glyph = Glyphs.ForChecked(IsChecked);
        var foreground = GlyphForeground ?? Owner?.Foreground;
        // The glyph honors the effective TextElement attributes (None for an ordinary control), so a
        // NoColor disabled check/radio dims with Faint to match its (Faint) content text — the whole control
        // reads as disabled, not just its label (review #1 follow-up).
        var attrs = TextElement.ComposeAttributes(this).Flags;

        // Bracket-neutral colored mark (gallery idiom): the box "[ ]" / "( )" paints in the inherited
        // foreground while the inner mark (✓ / ▪ / ●) takes its state color — but only when a mark color
        // resolves AND the glyph is the canonical open+mark+close triple. Otherwise the whole glyph paints
        // in the foreground (e.g. unchecked, a custom non-triple glyph, or no mark color configured).
        if (MarkBrush(IsChecked) is { } mark && glyph.Length >= 3)
        {
            var open = glyph[..1];
            var inner = glyph[1..^1];
            var openWidth = Text.GraphemeWidth.StringWidth(open);
            DrawAt(context, 0, open, foreground, attrs);
            DrawAt(context, openWidth, inner, mark, attrs);
            DrawAt(context, openWidth + Text.GraphemeWidth.StringWidth(inner), glyph[^1..], foreground, attrs);
            return;
        }

        DrawAt(context, 0, glyph, foreground, attrs);
    }

    private static void DrawAt(RenderContext context, int column, string text, IBrush? brush, TextAttributes attributes)
    {
        var style = new CellStyle().WithAttributes(attributes);
        var styleTemplate = BrushedStyle.Identity.Composed(PartialStyle.From(style));

        if (brush != null)
            styleTemplate = styleTemplate.WithForeground(brush);

        context.DrawText(column, 0, text, styleTemplate);
    }

    // The mark color for the checked / indeterminate states (a ThemeKeys brush resource resolved through
    // the chain); null for the unchecked state, when no key was configured, or when it does not resolve.
    private IBrush? MarkBrush(bool? state)
    {
        var key = state switch { true => CheckedMarkKey, null => IndeterminateMarkKey, _ => null };
        return key is not null && this.TryFindResource(key, out var value) && value is IBrush brush ? brush : null;
    }
}
