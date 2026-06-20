using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Input;

using CellStyle = Cursorial.Output.Style;

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
        dict[typeof(RepeatButton)] = RepeatButtonTheme();
        dict[typeof(ToggleButton)] = ToggleButtonTheme();
        dict[typeof(CheckBox)] = ToggleGlyphTheme("Theme.CheckBox", ThemeKeys.CheckBoxGlyphs, ThemeKeys.GreenBrush, ThemeKeys.AmberBrush);
        dict[typeof(RadioButton)] = ToggleGlyphTheme("Theme.RadioButton", ThemeKeys.RadioGlyphs, ThemeKeys.AccentBrush, ThemeKeys.AmberBrush);
        dict[typeof(ScrollBar)] = ScrollBarTheme();
        dict[typeof(ScrollViewer)] = ScrollViewerTheme();
        dict[typeof(ItemsControl)] = ItemsControlTheme();
        dict[typeof(ListBox)] = ListBoxTheme();
        dict[typeof(ListBoxItem)] = ListBoxItemTheme();
        dict[typeof(ComboBox)] = ComboBoxTheme();
        dict[typeof(ComboBoxItem)] = ComboBoxItemTheme();
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
        dict[typeof(TextBox)] = TextBoxTheme();
        dict[typeof(Image)] = ImageTheme();
        dict[typeof(Chart)] = ChartTheme();
        dict[typeof(Window)] = WindowTheme();
    }

    // ───────────────────────────── Button / RepeatButton / ToggleButton ─────────────────────────────

    // A fill-bounded single-content button (design doc §11.8a): a Border with Padding (1,0) wrapping a
    // RecognizesAccessKey ContentPresenter that auto-aliases the button's Content. The fill IS the button
    // (no resting border) — Background carries the SurfaceBrush spine and flips per interactive state
    // (hover/focus/pressed/disabled); the content text inherits Foreground, which the reverse-video focus
    // and accent pressed/default rules swap. The BorderPen binding stays so an app can opt a frame back in.
    private static ControlTemplate ButtonContentTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border { Padding = new Margins(1, 0), Child = presenter };
        // The face fill follows Button.Background (the WPF default-template wiring): a TemplateBinding
        // makes the resting SurfaceBrush + the per-state brush-pair flips paint the face, quantized per
        // the negotiated tier. No resting pen ⇒ no frame ⇒ a 1-row button (content at row 0).
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        // Opt-in border: the BorderPen binding is retained (unset by the default theme), so a consumer
        // that sets Control.BorderPen gets a framed button without a custom template.
        border.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));
        return border;
    });

    private static Style ButtonTheme()
    {
        var theme = AddButtonStates(ApplyPaletteSpine(new Style { Key = "Theme.Button" })
            .Set(Control.TemplateProperty, ButtonContentTemplate()));
        // :default (the Enter-default cue) — a resting accent reverse-video fill so the primary action
        // stands out; :focus/:pressed override it when the user interacts. The ▸ OK ◂ gutter brackets are
        // a deferred content nicety (spec §3).
        theme.Children.Add(new Style("^:default")
            .SetResource(Control.BackgroundProperty, ThemeKeys.AccentBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
        return theme;
    }

    // ───────────────────────────── Label ─────────────────────────────

    // A caption (design doc §12.5/§12.7): a RecognizesAccessKey ContentPresenter so the access-key mnemonic
    // underlines on Alt (Label.Content folds "_Name" → an AccessText mnemonic). No resting fill or frame — a
    // Label is a caption, not a control face; the content paints in the inherited TextBrush, muted when disabled.
    private static ControlTemplate LabelTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = true };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        return presenter;
    });

    private static Style LabelTheme()
    {
        var theme = new Style { Key = "Theme.Label" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, LabelTemplate());
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

    // ───────────────────────────── ItemsControl ─────────────────────────────

    // A minimal items host (design doc §12.6 / C2): a Border (opt-in Background/BorderPen via TemplateBindings)
    // wrapping the PART_ItemsHost ItemsPresenter, which builds the ItemsPanel and lays out the containers.
    private static ControlTemplate ItemsControlTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var border = new Border { Child = host };
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));
        return border;
    });

    private static Style ItemsControlTheme()
        => new Style { Key = "Theme.ItemsControl" }.Set(Control.TemplateProperty, ItemsControlTemplate());

    // ───────────────────────────── ListBox / ListBoxItem ─────────────────────────────

    // A well-fill ListBox (design doc §11.8a): a Border (opt-in BorderPen) over a ScrollViewer whose content is the
    // PART_ItemsHost ItemsPresenter — so a long list scrolls (the SCP band, C3). The items host stays in the
    // ListBox's template namescope even nested under the ScrollViewer, so GetTemplatePart finds it.
    private static ControlTemplate ListBoxTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var scroll = new ScrollViewer { Content = host }; // the ItemsPresenter resolves its owner up the visual tree (CD-P9-17)
        ctx.RegisterName("PART_ScrollViewer", scroll);
        var border = new Border { Child = scroll };
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));
        return border;
    });

    private static Style ListBoxTheme()
        => new Style { Key = "Theme.ListBox" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush) // a list is a recessed well
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, ListBoxTemplate());

    // A list item is a full-width selection bar: a Border filling its row, content at row 0 (no frame). Per the
    // default-theme gallery mockup (.item.sel/.hov/.dis): selected = SelectionBrush fill + TextBrush ink (NOT the
    // OnAccent pair — selection is milder than pressed; adoption-spec line 14), hover = HoverBrush + TextBrush,
    // disabled = MutedBrush ink. Ordered hover → selected so a hovered-selected item reads as selected (document
    // order). The keyboard focus-row reverse-video cue (gallery .item.rev / Inverse+Bold) lands with P9.3b.
    private static ControlTemplate ListBoxItemTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border { Padding = new Margins(1, 0), Child = presenter };
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        return border;
    });

    // ───────────────────────────── ComboBox / ComboBoxItem ─────────────────────────────

    // A combo box face: a recessed WellBrush field [selected-item … 'v' drop glyph] that the control toggles on
    // click, plus the drop-down Popup (PART_Popup) whose Child is an occluding panel over the PART_ItemsHost
    // ItemsPresenter (the ListBox-in-Popup recipe, design doc §12.11). The glyph is ASCII-safe ('v', cf. the scroll
    // arrows) to avoid the ambiguous-width hazard.
    private static ControlTemplate ComboBoxTemplate() => new(ctx =>
    {
        // Read-only face: the selected item (visible when !IsEditable; the ComboBox toggles visibility).
        var selected = new ContentPresenter();
        ctx.RegisterName("PART_ContentSite", selected);
        selected.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(SelectingItemsControl.SelectedItemProperty));

        // Editable face: a text box (visible when IsEditable). Placeholder + read-only flow from the ComboBox.
        var editable = new TextBox { Visibility = Visibility.Collapsed };
        ctx.RegisterName("PART_EditableTextBox", editable);
        editable.SetBinding(TextBox.PlaceholderProperty, new TemplateBinding(ComboBox.PlaceholderTextProperty));
        editable.SetBinding(TextBox.IsReadOnlyProperty, new TemplateBinding(ComboBox.IsReadOnlyProperty));

        var content = new Grid(); // the two faces overlap in one cell; the collapsed one takes no space
        content.Children.Add(selected);
        content.Children.Add(editable);

        // Drop button (the 'v' glyph) — toggles the list (the ComboBox wires its Click). ASCII-safe glyph.
        var drop = new Button { Content = "v", Focusable = false, IsTabStop = false };
        ctx.RegisterName("PART_DropDown", drop);
        DockPanel.SetDock(drop, Dock.Right);

        var row = new DockPanel();
        row.Children.Add(drop);    // docked right (the drop indicator + toggle)
        row.Children.Add(content); // fills the remaining width (the face)

        var face = new Border { Padding = new Margins(1, 0), Child = row };
        face.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        face.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));

        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var list = new Border { Occludes = true, Child = host };
        list.SetResourceReference(Border.BackgroundProperty, ThemeKeys.PanelBrush);
        list.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
        list.SetBinding(UIElement.MaxHeightProperty, new TemplateBinding(ComboBox.MaxDropDownHeightProperty)); // MaxDropDownHeight cap
        var popup = new Popup { Child = list };
        ctx.RegisterName("PART_Popup", popup);

        var root = new Grid(); // the Popup adds no layout (0×0); the face fills the cell
        root.Children.Add(face);
        root.Children.Add(popup);
        return root;
    });

    private static Style ComboBoxTheme()
        => new Style { Key = "Theme.ComboBox" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush) // a recessed field
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, ComboBoxTemplate());

    // A drop-down item — the ListBoxItem selection bar verbatim (selected = SelectionBrush, hover = HoverBrush,
    // keyboard focus row = reverse-video :focus-visible, disabled = MutedBrush ink).
    private static ControlTemplate ComboBoxItemTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border { Padding = new Margins(1, 0), Child = presenter };
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        return border;
    });

    private static Style ComboBoxItemTheme()
    {
        var theme = new Style { Key = "Theme.ComboBoxItem" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, ComboBoxItemTemplate());
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.HoverBrush));
        theme.Children.Add(new Style("^:selected").SetResource(Control.BackgroundProperty, ThemeKeys.SelectionBrush));
        theme.Children.Add(new Style("^:focus-visible")
            .SetResource(Control.BackgroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.WindowBackground));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush));
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
        ctx.RegisterName("PART_ScrollViewer", scroll);
        var border = new Border { Child = scroll };
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));
        return border;
    });

    private static Style TreeViewTheme()
        => new Style { Key = "Theme.TreeView" }
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
        ctx.RegisterName("PART_Twisty", twisty);
        DockPanel.SetDock(twisty, Dock.Left);

        var header = new ContentPresenter(); // NOT RecognizesAccessKey — a tree node has no access-key activation, and an underscore in data (e.g. "my_file") must render literally
        ctx.RegisterName("PART_ContentPresenter", header);
        header.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(HeaderedItemsControl.HeaderProperty));
        header.SetBinding(ContentPresenter.ContentTemplateProperty, new TemplateBinding(HeaderedItemsControl.HeaderTemplateProperty)); // HDT renders the header

        var headerRow = new DockPanel();
        headerRow.Children.Add(twisty); // docked left (the expander)
        headerRow.Children.Add(header); // fills the remaining width
        var bar = new Border { Child = headerRow };
        bar.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));

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
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, TreeViewItemTemplate());
        // NO :pointerover fill (WPF-faithful): InteractionState.PointerOver is set on EVERY ancestor of the hovered
        // leaf, and TreeViewItems nest, so a hover rule would light the whole ancestor header-bar chain. A tree node
        // highlights on selection + keyboard focus only.
        theme.Children.Add(new Style("^:selected").SetResource(Control.BackgroundProperty, ThemeKeys.SelectionBrush));
        // The keyboard focus-row cue (reverse-video), ordered after :selected so a focused+selected node reads as
        // focused; :focus-visible (not :focus) so a mouse click shows :selected while keyboard nav shows the row.
        theme.Children.Add(new Style("^:focus-visible")
            .SetResource(Control.BackgroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.WindowBackground));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush));
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
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        border.SetBinding(Border.PaddingProperty, new TemplateBinding(Control.PaddingProperty));
        border.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));
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
        border.SetBinding(Border.PaddingProperty, new TemplateBinding(Control.PaddingProperty));
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        return border;
    });

    // The shared :inactive/:pointerover/:today/:selected/:focus-visible/:disabled cell looks (ordered so a focused
    // cell reads as focused, then selected over today over hover). Cells don't nest, so ^:pointerover is safe.
    private static Style CalendarCellTheme(string key)
    {
        var theme = new Style { Key = key }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, CalendarCellTemplate());
        theme.Children.Add(new Style("^:inactive").SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush));
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.HoverBrush));
        theme.Children.Add(new Style("^:today").SetResource(Control.ForegroundProperty, ThemeKeys.AccentBrush));
        theme.Children.Add(new Style("^:selected")
            .SetResource(Control.BackgroundProperty, ThemeKeys.SelectionBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush));
        theme.Children.Add(new Style("^:focus-visible")
            .SetResource(Control.BackgroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.WindowBackground));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush));
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
        presenter.SetBinding(ImagePresenter.SourceProperty, new TemplateBinding(Image.SourceProperty));
        presenter.SetBinding(ImagePresenter.SourceUriProperty, new TemplateBinding(Image.SourceUriProperty));
        presenter.SetBinding(ImagePresenter.PlaceholderContentProperty, new TemplateBinding(Image.PlaceholderContentProperty));
        presenter.SetBinding(ImagePresenter.PlaceholderTemplateProperty, new TemplateBinding(Image.PlaceholderTemplateProperty));
        return presenter;
    });

    private static Style ImageTheme()
        => new Style { Key = "Theme.Image" }.Set(Control.TemplateProperty, ImageTemplate());

    // A Chart: its template hosts a PART_ChartPresenter with Source/placeholder one-way TemplateBound from the control.
    // The presenter draws the cell-rendered chart (or the placeholder when none is set).
    private static ControlTemplate ChartTemplate() => new(ctx =>
    {
        var presenter = new ChartPresenter();
        ctx.RegisterName("PART_ChartPresenter", presenter);
        presenter.SetBinding(ChartPresenter.SourceProperty, new TemplateBinding(Chart.SourceProperty));
        presenter.SetBinding(ChartPresenter.PlaceholderContentProperty, new TemplateBinding(Chart.PlaceholderContentProperty));
        presenter.SetBinding(ChartPresenter.PlaceholderTemplateProperty, new TemplateBinding(Chart.PlaceholderTemplateProperty));
        return presenter;
    });

    private static Style ChartTheme()
        => new Style { Key = "Theme.Chart" }.Set(Control.TemplateProperty, ChartTemplate());

    // ───────────────────────────── DatePicker ─────────────────────────────

    // A DatePicker: a recessed WellBrush field [date text … 'v' drop glyph] that toggles a Popup hosting a Calendar
    // (PART_Calendar) — the ListBox-in-Popup recipe with a Calendar instead of a list. ASCII-safe drop glyph.
    private static ControlTemplate DatePickerTemplate() => new(ctx =>
    {
        // Read-only face: the selected date / watermark (visible when !IsEditable; the DatePicker toggles visibility).
        var text = new TextBlock();
        ctx.RegisterName("PART_DisplayText", text);

        // Editable face: a text box to type a date (collapsed unless IsEditable).
        var editable = new TextBox { Visibility = Visibility.Collapsed };
        ctx.RegisterName("PART_EditableTextBox", editable);
        editable.SetBinding(TextBox.PlaceholderProperty, new TemplateBinding(DatePicker.WatermarkProperty));

        var content = new Grid(); // the two faces overlap in one cell; the collapsed one takes no space
        content.Children.Add(text);
        content.Children.Add(editable);

        // Drop button (the 'v' glyph) — toggles the calendar (the DatePicker wires its Click). ASCII-safe glyph.
        var drop = new Button { Content = "v", Focusable = false, IsTabStop = false };
        ctx.RegisterName("PART_DropDown", drop);
        DockPanel.SetDock(drop, Dock.Right);

        var row = new DockPanel();
        row.Children.Add(drop);    // docked right (the drop indicator + toggle)
        row.Children.Add(content); // fills the remaining width (the face)

        var field = new Border { Padding = new Margins(1, 0), Child = row };
        field.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        field.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));

        var calendar = new Calendar { BorderPen = Pens.Light.WithBrush(Brushes.Red) };
        ctx.RegisterName("PART_Calendar", calendar);
        var surface = new Border { Occludes = true, Child = calendar };
        surface.SetResourceReference(Border.BackgroundProperty, ThemeKeys.PanelBrush);
        surface.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
        var popup = new Popup { Child = surface };
        ctx.RegisterName("PART_Popup", popup);

        var root = new Grid(); // the Popup adds no layout (0×0); the field fills the cell
        root.Children.Add(field);
        root.Children.Add(popup);
        root.Styles.Add(new Style(Selectors.OfType<Calendar>()).Set(Control.BorderPenProperty, null));
        return root;
    });

    private static Style DatePickerTheme()
        => new Style { Key = "Theme.DatePicker" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush) // a recessed field
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(UIElement.MinWidthProperty, 12)
            .Set(Control.TemplateProperty, DatePickerTemplate());

    // ───────────────────────────── Menu / MenuItem / Separator ─────────────────────────────

    // The horizontal menu bar: a SurfaceBrush strip hosting the top-level items (the Menu's ItemsPanel is a
    // horizontal StackPanel — set in its ctor).
    private static ControlTemplate MenuTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var border = new Border { Child = host };
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
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
            .SetResource(Control.BackgroundProperty, ThemeKeys.SurfaceBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, MenuTemplate());

    // A ContextMenu: a popup-rooted occluding panel (overwrites the content it floats over) hosting the
    // PART_ItemsHost ItemsPresenter; the default vertical StackPanel stacks its MenuItems (design doc §12.7).
    private static ControlTemplate ContextMenuTemplate() => new(ctx =>
    {
        var host = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", host);
        var border = new Border { Occludes = true, Child = host };
        border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.PanelBrush);
        border.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
        return border;
    });

    private static Style ContextMenuTheme()
        => new Style { Key = "Theme.ContextMenu" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, ContextMenuTemplate());

    // A ToolTip: an occluding bordered panel (overwrites the content it floats over) wrapping a ContentPresenter;
    // capped at 40 cells wide with the content wrapping (design doc §12.7).
    private static ControlTemplate ToolTipTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_ContentPresenter", presenter);
        var border = new Border { Occludes = true, Padding = new Margins(1, 0), Child = presenter };
        border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.PanelBrush);
        border.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
        return border;
    });

    private static Style ToolTipTheme()
        => new Style { Key = "Theme.ToolTip" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(UIElement.MaxWidthProperty, 40) // spec §12.7: max 40 cells, content wraps
            .Set(Control.TemplateProperty, ToolTipTemplate());

    // ───────────────────────────── TabControl / TabItem ─────────────────────────────

    // A TabControl: a DockPanel with the tab strip (PART_TabStrip ItemsPresenter, docked top — the row of headers)
    // over a bordered content host (PART_ContentHost ContentPresenter showing the selected tab's SelectedContent).
    private static ControlTemplate TabControlTemplate() => new(ctx =>
    {
        var strip = new ItemsPresenter();
        ctx.RegisterName("PART_TabStrip", strip);
        DockPanel.SetDock(strip, Dock.Top);

        var content = new ContentPresenter();
        ctx.RegisterName("PART_ContentHost", content);
        content.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(TabControl.SelectedContentProperty));
        content.SetBinding(ContentPresenter.ContentTemplateProperty, new TemplateBinding(TabControl.ContentTemplateProperty));
        var body = new Border { Padding = new Margins(1, 0), Child = content };
        body.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);

        var root = new DockPanel();
        root.Children.Add(strip); // docked top (the header row)
        root.Children.Add(body);  // fills the rest (the selected tab's body)
        return root;
    });

    private static Style TabControlTheme()
        => new Style { Key = "Theme.TabControl" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, TabControlTemplate());

    // A tab header: a fill-bounded ContentPresenter over the TabItem.Header (RecognizesAccessKey). :selected =
    // SelectionBrush fill, :pointerover = HoverBrush, :disabled muted (the gallery item-bar idiom).
    private static ControlTemplate TabItemTemplate() => new(ctx =>
    {
        var header = new ContentPresenter { RecognizesAccessKey = true };
        ctx.RegisterName("PART_ContentPresenter", header);
        header.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(HeaderedContentControl.HeaderProperty));
        var face = new Border { Padding = new Margins(1, 0), Child = header };
        face.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        return face;
    });

    private static Style TabItemTheme()
    {
        var theme = new Style { Key = "Theme.TabItem" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, TabItemTemplate());
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.HoverBrush));
        theme.Children.Add(new Style("^:selected").SetResource(Control.BackgroundProperty, ThemeKeys.SelectionBrush));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush));
        return theme;
    }

    // ───────────────────────────── ProgressBar ─────────────────────────────

    // ProgressBar paints itself (no template): the track is the recessed WellBrush, the determinate fill is green,
    // and the indeterminate sweep is accent (the gallery mockup — green determinate, accent indeterminate).
    private static Style ProgressBarTheme()
    {
        var theme = new Style { Key = "Theme.ProgressBar" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush)
            .SetResource(ProgressBar.FillProperty, ThemeKeys.GreenBrush);
        theme.Children.Add(new Style("^:indeterminate").SetResource(ProgressBar.FillProperty, ThemeKeys.AccentBrush));
        return theme;
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
        var border = new Border { Padding = new Margins(1, 0), Child = presenter };
        border.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        border.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty));
        return border;
    });

    private static Style TextBoxTheme()
    {
        var theme = ApplyPaletteSpine(new Style { Key = "Theme.TextBox" })
            .Set(UIElement.MinWidthProperty, 12)
            .SetResource(TextBox.SelectionBrushProperty, ThemeKeys.SelectionBrush)
            .Set(Control.TemplateProperty, TextBoxTemplate());
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.HoverBrush));
        theme.Children.Add(new Style("^:focus").SetResource(Control.BackgroundProperty, ThemeKeys.WellBrush));
        theme.Children.Add(new Style("^:disabled")
            .SetResource(Control.BackgroundProperty, ThemeKeys.DisabledBackgroundBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

    // A menu item: a fill-bounded header row [header … gesture], plus the submenu Popup (PART_Popup) whose Child is
    // an occluding PanelBrush surface hosting the sub-items (PART_ItemsHost). The Popup contributes no layout to the
    // row (a Grid cell stacks it behind the face at 0×0). :highlighted = SelectionBrush fill; :disabled = muted.
    private static ControlTemplate MenuItemTemplate() => new(ctx =>
    {
        var header = new ContentPresenter { RecognizesAccessKey = true };
        ctx.RegisterName("PART_ContentPresenter", header);
        header.SetBinding(ContentPresenter.ContentProperty, new TemplateBinding(HeaderedItemsControl.HeaderProperty));

        var gesture = new TextBlock { Margin = new Margins(2, 0, 0, 0) };
        gesture.SetBinding(TextBlock.TextProperty, new TemplateBinding(MenuItem.InputGestureTextProperty));
        gesture.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
        DockPanel.SetDock(gesture, Dock.Right);

        var row = new DockPanel();
        row.Children.Add(gesture); // docked right (faint gesture hint)
        row.Children.Add(header);  // fills the remaining width

        var face = new Border { Padding = new Margins(1, 0), Child = row };
        face.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));

        var itemsHost = new ItemsPresenter();
        ctx.RegisterName("PART_ItemsHost", itemsHost);
        var submenu = new Border { Occludes = true, Child = itemsHost };
        submenu.SetResourceReference(Border.BackgroundProperty, ThemeKeys.PanelBrush);
        submenu.SetResourceReference(Border.BorderPenProperty, ThemeKeys.BorderPen);
        var popup = new Popup { Child = submenu };
        ctx.RegisterName("PART_Popup", popup);

        var root = new Grid(); // the Popup adds no layout (0×0); the face fills the cell
        root.Children.Add(face);
        root.Children.Add(popup);
        return root;
    });

    private static Style MenuItemTheme()
    {
        var theme = new Style { Key = "Theme.MenuItem" }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, MenuItemTemplate());
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.HoverBrush));
        theme.Children.Add(new Style("^:highlighted").SetResource(Control.BackgroundProperty, ThemeKeys.SelectionBrush));
        theme.Children.Add(new Style("^:open").SetResource(Control.BackgroundProperty, ThemeKeys.SelectionBrush));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush));
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
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, ListBoxItemTemplate());
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.HoverBrush));
        theme.Children.Add(new Style("^:selected").SetResource(Control.BackgroundProperty, ThemeKeys.SelectionBrush));
        // The keyboard focus-row cue (gallery .item.rev): reverse-video — ordered AFTER :selected so a
        // focused+selected current item reads as focused (adoption-spec lines 108-110). :focus-visible (not :focus)
        // so a mouse click — Pointer modality — shows :selected, while keyboard nav shows the reverse row.
        theme.Children.Add(new Style("^:focus-visible")
            .SetResource(Control.BackgroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.WindowBackground));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.MutedBrush));
        return theme;
    }

    private static Style RepeatButtonTheme()
        => AddButtonStates(ApplyPaletteSpine(new Style { Key = "Theme.RepeatButton" })
            .Set(Control.TemplateProperty, ButtonContentTemplate()));

    private static Style ToggleButtonTheme()
        => AddButtonStates(ApplyPaletteSpine(new Style { Key = "Theme.ToggleButton" })
            .Set(Control.TemplateProperty, ButtonContentTemplate()));

    // The cell-faithful interactive states shared by the button family (design doc §11.8a): hover = a
    // fill swap; focus = reverse-video (TextBrush fill + WindowBackground text); pressed = accent reverse-video;
    // disabled = disabled fill + muted text. All brush-pair setters are ResourceReferences into the
    // palette spine (color tiers); the NoColor interactive-state distinction rides the caps-nocolor
    // theme-styles rules (inherited TextElement.TextAttributes — Inverse / Faint; see CursorialThemeStyles).
    // Ordered hover → focus → pressed → disabled so the higher-intent state wins on a pseudo-class tie.
    private static Style AddButtonStates(Style theme)
    {
        theme.Children.Add(new Style("^:pointerover").SetResource(Control.BackgroundProperty, ThemeKeys.HoverBrush));
        theme.Children.Add(new Style("^:focus")
            .SetResource(Control.BackgroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.WindowBackground));
        theme.Children.Add(new Style("^:pressed")
            .SetResource(Control.BackgroundProperty, ThemeKeys.AccentBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.OnAccentBrush));
        theme.Children.Add(new Style("^:disabled")
            .SetResource(Control.BackgroundProperty, ThemeKeys.DisabledBackgroundBrush)
            .SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

    // The R2 palette spine (design doc §11.5 / §11.8a): a default-look control (no explicit Foreground /
    // Background) resolves its resting fill + ink from the ThemeKeys palette as DynamicResources, so it
    // re-skins per-variant on a dark/light flip or a single ThemeKeys override with zero template work.
    // The setters arm at the ControlTheme layer (below LocalValue), so an explicit value the consumer
    // sets always wins. Cell-faithful reversal of the WPF transparent-Background default: the resting
    // SurfaceBrush fill IS the control's extent (no border). The toggle-glyph controls (CheckBox/Radio)
    // do NOT take this spine — they stay transparent at rest (gallery: their normal fill is the page bg).
    private static Style ApplyPaletteSpine(Style theme)
        => theme
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .SetResource(Control.BackgroundProperty, ThemeKeys.SurfaceBrush);

    // ───────────────────────────── CheckBox / RadioButton ─────────────────────────────

    // glyph cell + 1 space + ContentPresenter (spec line 660), wrapped in a fill Border. The glyph element
    // reads the owning toggle's IsChecked + the glyph-set resource (ASCII default, overridable).
    private static ControlTemplate ToggleGlyphTemplate(string glyphKey, string checkedMarkKey, string indeterminateMarkKey)
        => new(ctx =>
               {
                   var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };

                   // The glyph cell overlays a zero-size Caret on the glyph's INNER cell (column 1 — the space inside
                   // [ ] / ( )). A single-cell Grid stacks both; the Caret is Left/Top-pinned with a 1-column left
                   // margin so it lands on the box's middle cell. It shows only under :focus-visible (driven in code by
                   // ToggleButton). Check/radio focus is this IN-BOX CARET, not reverse-video: a check/radio stretches
                   // to its StackPanel's width, so a reverse fill would span the whole row like a selection bar.
                   var glyphCell = new Grid();
                   var glyph = new ToggleGlyph(glyphKey, checkedMarkKey, indeterminateMarkKey);
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

                   var presenter = new ContentPresenter { RecognizesAccessKey = true };
                   ctx.RegisterName("PART_ContentPresenter", presenter);
                   row.Children.Add(glyphCell);
                   row.Children.Add(presenter);

                   // The face follows Control.Background — unset (transparent) at rest and there is no fill setter:
                   // check/radio never fill (a stretched row would fill full-width like a selection bar). The binding
                   // stays so a consumer that sets Background still paints. Focus is the in-box caret, not a fill.
                   var face = new Border { Child = row };
                   face.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
                   return face;
               });

    // themeKey is the diagnostic Style.Key (e.g. "Theme.CheckBox"); glyphKey is the ThemeKeys glyph-set
    // resource the ToggleGlyph leaf reads (already a fully-qualified "Theme.*" constant — never re-prefix it).
    // No fill at all: transparent at rest AND on hover (a stretched check/radio would fill full-width); the
    // glyph + content paint in the inherited Foreground, disabled = muted ink, focus = the in-box caret
    // (driven by ToggleButton), never reverse-video.
    private static Style ToggleGlyphTheme(string themeKey, string glyphKey, string checkedMarkKey, string indeterminateMarkKey)
    {
        var theme = new Style { Key = themeKey }
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, ToggleGlyphTemplate(glyphKey, checkedMarkKey, indeterminateMarkKey));
        theme.Children.Add(new Style("^:disabled").SetResource(Control.ForegroundProperty, ThemeKeys.DisabledForegroundBrush));
        return theme;
    }

    // ───────────────────────────── ScrollBar / ScrollViewer ─────────────────────────────

    // A borderless line-step RepeatButton template: a single arrow glyph (no border/padding), so a
    // 1-cell-wide ScrollBar's arrows fit (the bordered ButtonContentTemplate would draw a │ frame).
    private static ControlTemplate BareGlyphButtonTemplate() => new(ctx =>
    {
        var presenter = new ContentPresenter { RecognizesAccessKey = false };
        ctx.RegisterName("PART_ContentPresenter", presenter);
        return presenter;
    });

    // PART_Track (required) + optional PART_LineUpButton/PART_LineDownButton arrow RepeatButtons
    // (CD19/C231/C236). The arrows are borderless RepeatButtons with arrow-glyph content; the track is
    // the internal Track primitive. A DockPanel docks the arrows to the ends, the track filling the rest.
    private static ControlTemplate ScrollBarTemplate(bool horizontal) => new(ctx =>
    {
        var dock = new DockPanel();
        var bareTemplate = BareGlyphButtonTemplate();

        // The line buttons drop out of Tab navigation (Focusable = false, IsTabStop = false): a
        // ScrollBar and its parts are driven by the scrolled content's keyboard + the mouse, never by
        // tabbing onto the arrows (WPF/Avalonia parity — ButtonBase opts Focusable in by default, so
        // these scrollbar parts must opt back out). The Track is already a non-focusable UIElement.
        var lineUp = new RepeatButton { Content = horizontal ? "◀" : "▲", Template = bareTemplate, Focusable = false, IsTabStop = false };
        ctx.RegisterName("PART_LineUpButton", lineUp);
        DockPanel.SetDock(lineUp, horizontal ? Dock.Left : Dock.Top);

        var lineDown = new RepeatButton { Content = horizontal ? "▶" : "▼", Template = bareTemplate, Focusable = false, IsTabStop = false };
        ctx.RegisterName("PART_LineDownButton", lineDown);
        DockPanel.SetDock(lineDown, horizontal ? Dock.Right : Dock.Bottom);

        var owner = (ScrollBar)(ctx.TemplatedParent
                                ?? throw new InvalidOperationException("The ScrollBar theme template requires a templated parent."));
        var track = new Track(owner);
        ctx.RegisterName("PART_Track", track);

        dock.Children.Add(lineUp);
        dock.Children.Add(lineDown);
        dock.Children.Add(track); // last child fills the remaining space (DockPanel default)
        return dock;
    });

    private static Style ScrollBarTheme()
    {
        var theme = new Style { Key = "Theme.ScrollBar" }
            .Set(Control.TemplateProperty, ScrollBarTemplate(horizontal: false))
            .SetResource(Control.BorderPenProperty, ThemeKeys.BorderPen);
        // A horizontal bar uses the rotated template (arrows on the ends of the long axis).
        theme.Children.Add(new Style("^:horizontal").Set(Control.TemplateProperty, ScrollBarTemplate(horizontal: true)));
        return theme;
    }

    // PART_ScrollContentPresenter (required) + a PART_VerticalScrollBar docked right (CD28/C235).
    private static ControlTemplate ScrollViewerTemplate() => new(ctx =>
    {
        var dock = new DockPanel();

        var bar = new ScrollBar { Orientation = Orientation.Vertical };
        ctx.RegisterName("PART_VerticalScrollBar", bar);
        DockPanel.SetDock(bar, Dock.Right);

        var presenter = new ScrollContentPresenter();
        ctx.RegisterName("PART_ScrollContentPresenter", presenter);

        dock.Children.Add(bar);
        dock.Children.Add(presenter); // fills the remaining space
        return dock;
    });

    private static Style ScrollViewerTheme()
        => new Style { Key = "Theme.ScrollViewer" }.Set(Control.TemplateProperty, ScrollViewerTemplate());

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
    private static ControlTemplate WindowChromeTemplate() => new(ctx =>
    {
        var window = (Window) (ctx.TemplatedParent
                               ?? throw new InvalidOperationException("The window chrome template requires a templated parent."));

        var presenter = new ContentPresenter();
        ctx.RegisterName("PART_ContentPresenter", presenter);

        var root = new Border();
        root.SetBinding(Border.BackgroundProperty, new TemplateBinding(Control.BackgroundProperty));
        root.SetBinding(Border.BorderPenProperty, new TemplateBinding(Control.BorderPenProperty)); // opt-in frame only

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
        WindowChrome.SetHitTestRole(titleBar, WindowHitTestRole.Drag);
        ctx.RegisterName("PART_TitleBar", titleBar);

        var titleBarContent = new DockPanel(); // LastChildFill → the title fills the remainder

        // Close ✕ — a bare glyph on the band: a transparent local Background beats the Button theme's fills,
        // so it reads as a glyph, not a button face. Close stays the button's own Click (the role switch
        // intentionally leaves Close off).
        var closeButton = new Button { Content = "✕", Background = Brushes.Transparent, Focusable = false, IsTabStop = false };
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
        TextBlock? maximizeGlyph = null;
        if (window.CanResize)
        {
            maximizeGlyph = new TextBlock { Text = "▢" };
            WindowChrome.SetHitTestRole(maximizeGlyph, WindowHitTestRole.Maximize);
            DockPanel.SetDock(maximizeGlyph, Dock.Right);
            titleBarContent.Children.Add(maximizeGlyph); // docked right → left of the close glyph
            ctx.RegisterName("PART_MaximizeButton", maximizeGlyph);
        }

        var titleText = new TextBlock { Text = window.Title ?? string.Empty };
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
                           VerticalAlignment = VerticalAlignment.Bottom
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
        return root;
    });

    private static Style WindowTheme()
        => new Style { Key = "Theme.Window" }
            .SetResource(Control.BackgroundProperty, ThemeKeys.PanelBrush) // borderless body surface; a consumer LocalValue Background wins
            .SetResource(Control.ForegroundProperty, ThemeKeys.TextBrush)
            .Set(Control.TemplateProperty, WindowChromeTemplate());
}

/// <summary>
/// The check/radio glyph cell of a <see cref="CheckBox"/>/<see cref="RadioButton"/> default template
/// (design doc §12.7, spec line 660): a leaf that reads the owning toggle's
/// <see cref="ToggleButton.IsChecked"/> and the glyph-set resource (<see cref="ThemeKeys.CheckBoxGlyphs"/>
/// / <see cref="ThemeKeys.RadioGlyphs"/>; ASCII <c>[ ] [x] [-]</c> / <c>( ) (*)</c> by default,
/// overridable at any chain scope) and draws the matching glyph. The glyph is foreground text in the
/// toggle's <see cref="Control.Foreground"/> (inherited). Public + XAML-authorable so the control themes
/// can be authored declaratively (Cursorial.UI.Themes.Xaml): set <see cref="GlyphKey"/> /
/// <see cref="CheckedMarkKey"/> / <see cref="IndeterminateMarkKey"/> in the template.
/// </summary>
public sealed class ToggleGlyph : UIElement, IValueObserver<bool?>
{
    private IDisposable? _checkedObserver;

    /// <summary>The <see cref="ThemeKeys"/> glyph-set resource key (the <c>(Unchecked, Checked, Indeterminate)</c> <see cref="GlyphSetCarrier"/> base).</summary>
    public string? GlyphKey { get; set; }

    /// <summary>The <see cref="ThemeKeys"/> brush key coloring the CHECKED inner mark (e.g., GreenBrush ✓ / AccentBrush ●); <c>null</c> leaves it in the foreground.</summary>
    public string? CheckedMarkKey { get; set; }

    /// <summary>The <see cref="ThemeKeys"/> brush key coloring the INDETERMINATE inner mark (e.g., AmberBrush ▪); <c>null</c> leaves it in the foreground.</summary>
    public string? IndeterminateMarkKey { get; set; }

    // The caps-unicode glyph-set override (design doc §12.7 / SD14): CursorialThemeStyles' `.caps-unicode`
    // rules set this per control type to opt the marks UP from the ASCII resource base to Unicode (✓/▪/●);
    // ToggleGlyph reads it off its Owner, falling back to the glyph-set resource when unset (a caps-ascii
    // terminal, or no caps-unicode source). Hosted on ToggleButton so a `.caps-unicode CheckBox`/
    // `RadioButton` theme-styles rule can set it. AffectsRender — the ASCII↔Unicode marks are equal-width.
    public static readonly AttachedProperty<GlyphSetCarrier?> GlyphsProperty =
        UIProperty.RegisterAttached<ToggleGlyph, ToggleButton, GlyphSetCarrier?>("Glyphs");

    static ToggleGlyph() => AddGlobalEffects(PropertyEffects.AffectsRender, GlyphsProperty);

    /// <summary>Parameterless constructor for XAML; set <see cref="GlyphKey"/> + the mark keys via properties.</summary>
    public ToggleGlyph() { }

    /// <summary>
    /// The code-first constructor. <paramref name="checkedMarkKey"/> / <paramref name="indeterminateMarkKey"/>
    /// are ThemeKeys brush resources that color the INNER mark for the checked / indeterminate states
    /// (CheckBox ✓ = GreenBrush, ▪ = AmberBrush; RadioButton ● = AccentBrush); null leaves the mark in the
    /// inherited foreground. The brackets always paint in the foreground.
    /// </summary>
    public ToggleGlyph(string glyphKey, string? checkedMarkKey = null, string? indeterminateMarkKey = null)
    {
        GlyphKey = glyphKey;
        CheckedMarkKey = checkedMarkKey;
        IndeterminateMarkKey = indeterminateMarkKey;
    }

    private ToggleButton? Owner => TemplatedParent as ToggleButton;

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        // Re-render when the owning toggle's checked state flips (the glyph swap is render-only — the
        // ASCII triple is equal-width; a Unicode swap that changed width would also need re-measure).
        if (Owner is { } owner)
            _checkedObserver = owner.AddObserver(ToggleButton.IsCheckedProperty, this);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        _checkedObserver?.Dispose();
        _checkedObserver = null;
        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    void IValueObserver<bool?>.OnPropertyChanged(UIObject source, UIProperty property, bool? oldValue, bool? newValue, BindingPriority priority)
        => InvalidateVisual();

    private GlyphSetCarrier Glyphs
    {
        get
        {
            // The .caps-unicode override (set on the Owner by a theme-styles rule) wins; else the glyph-set
            // resource (the ASCII base, themeable at any scope); else the hard ASCII fallback.
            if (Owner?.GetValue(GlyphsProperty) is { } unicode)
                return unicode;
            return GlyphKey is { } key && this.TryFindResource(key, out var value) && value is GlyphSetCarrier glyphs
                ? glyphs
                : new GlyphSetCarrier("[ ]", "[x]", "[-]");
        }
    }

    // The owner's checked state, defaulting to unchecked only when there is no owner — a null here is
    // the genuine *indeterminate* value, which the '?? false' coalesce must NOT collapse.
    private bool? CheckedState => Owner is { } owner ? owner.IsChecked : false;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var glyph = Glyphs.ForChecked(CheckedState);
        return new Size(Text.GraphemeWidth.StringWidth(glyph), 1);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (context.Bounds.IsEmpty)
            return;

        var glyph = Glyphs.ForChecked(CheckedState);
        var foreground = Owner?.Foreground;
        // The glyph honors the inherited TextElement.TextAttributes (None for an ordinary control), so a
        // NoColor disabled check/radio dims with Faint to match its (Faint) content text — the whole control
        // reads as disabled, not just its label (review #1 follow-up).
        var attrs = TextElement.GetTextAttributes(this);

        // Bracket-neutral colored mark (gallery idiom): the box "[ ]" / "( )" paints in the inherited
        // foreground while the inner mark (✓ / ▪ / ●) takes its state color — but only when a mark color
        // resolves AND the glyph is the canonical open+mark+close triple. Otherwise the whole glyph paints
        // in the foreground (e.g. unchecked, a custom non-triple glyph, or no mark color configured).
        if (MarkBrush(CheckedState) is { } mark && glyph.Length >= 3)
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
        if (brush is { })
            context.DrawText(column, 0, text, brush, baseStyle: style);
        else
            context.DrawText(column, 0, text, Colors.Default, baseStyle: style);
    }

    // The mark color for the checked / indeterminate states (a ThemeKeys brush resource resolved through
    // the chain); null for the unchecked state, when no key was configured, or when it does not resolve.
    private IBrush? MarkBrush(bool? state)
    {
        var key = state switch { true => CheckedMarkKey, null => IndeterminateMarkKey, _ => null };
        return key is not null && this.TryFindResource(key, out var value) && value is IBrush brush ? brush : null;
    }
}
