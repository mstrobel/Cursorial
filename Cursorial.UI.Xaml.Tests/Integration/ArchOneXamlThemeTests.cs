// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine.
#pragma warning disable xUnit1031

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;
using Cursorial.UI.Themes.Default;

using UIControls = Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.Xaml.Integration;

/// <summary>
/// ARCH-1 (Task #10): the data-shipped <c>Cursorial.UI.Themes</c> assembly's control themes —
/// authored in embedded <c>.xaml</c> and loaded via <see cref="CursorialDefaultTheme.LoadControls"/> — render
/// byte-for-byte identically to the code-first <c>CursorialTheme.BuiltIn</c> ones. The button family is
/// checked at REST and FOCUSED (exercising the nested <c>^:focus</c> reverse-video sub-rule authored through
/// the Selector converter + <c>&lt;Style.Children&gt;</c>); the ScrollBar/ScrollViewer at rest (their
/// <c>Track</c>/arrow/SCP composition through the parameterless <c>Track()</c> + <c>TemplatedParent</c>
/// owner resolution). The dogfood proof that the declarative theme reproduces the code-first one end-to-end
/// on the real frame loop.
/// </summary>
public sealed class ArchOneXamlThemeTests
{
    [Theory]
    [InlineData("Button")]
    [InlineData("RepeatButton")]
    [InlineData("ToggleButton")]
    [InlineData("CheckBox")]    // unchecked: "[ ]" in both ASCII + caps-unicode, so byte-identity is tier-robust
    [InlineData("RadioButton")] // unchecked: "( )" likewise
    public void XamlControlTheme_RendersIdenticallyToCSharpBuiltIn_RestAndFocus(string control)
    {
        // The XAML theme (app.Theme) layers over the code-first BuiltIn; the typeof(control) entry it carries
        // replaces BuiltIn's. Compare the rendered cells (glyph + fg + bg) to the BuiltIn oracle, rest + focus.
        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 14, 3, () => MakeControl(control)),
            CaptureCells(xaml: true,  focus: false, 14, 3, () => MakeControl(control)));
        Assert.Equal(
            CaptureCells(xaml: false, focus: true, 14, 3, () => MakeControl(control)),
            CaptureCells(xaml: true,  focus: true, 14, 3, () => MakeControl(control)));
    }

    [Fact]
    public void XamlScrollBarTheme_RendersIdenticallyToCSharpBuiltIn()
    {
        // A vertical ScrollBar: ▲ rail+thumb ▼ down the column. The XAML twin authors the arrow RepeatButtons
        // + Track (parameterless ctor, owner resolved from TemplatedParent) and must match the code-first one.
        static UIControls.Control MakeScrollBar() => new UIControls.ScrollBar
        {
            Orientation = UIControls.Orientation.Vertical,
            Width = 1,
            Height = 10,
            Minimum = 0,
            Maximum = 90,
            ViewportSize = 10,
        };

        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 2, 12, MakeScrollBar),
            CaptureCells(xaml: true,  focus: false, 2, 12, MakeScrollBar));
    }

    [Fact]
    public void XamlScrollViewerTheme_RendersIdenticallyToCSharpBuiltIn()
    {
        // A ScrollViewer over tall content: the SCP fills, a vertical ScrollBar docks right. The XAML twin
        // composes ScrollBar + ScrollContentPresenter via the parameterless parts and must match.
        static UIControls.Control MakeScrollViewer() => new UIControls.ScrollViewer
        {
            Width = 13,
            Height = 5,
            Content = new UIControls.Border
            {
                Width = 12,
                Height = 40,
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            },
        };

        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 14, 6, MakeScrollViewer),
            CaptureCells(xaml: true,  focus: false, 14, 6, MakeScrollViewer));
    }

    [Fact]
    public void XamlGlyphSetCarrierResources_InstantiateFromXaml_WithBuiltInValues()
    {
        // GlyphSetCarrier (record class: parameterless ctor + init props) is authored as <GlyphSetCarrier>
        // resource elements in the theme XAML; prove the loader instantiates them and the dict carries the
        // same triples as the code-first CursorialTheme.BuiltIn (true-ASCII defaults). The x:Key is an
        // {x:Static ThemeKeys.*Glyphs} string key.
        var dict = CursorialDefaultTheme.LoadControls();

        AssertGlyphs(dict, ThemeKeys.CheckBoxGlyphs, "[ ]", "[x]", "[-]");
        AssertGlyphs(dict, ThemeKeys.RadioGlyphs, "( )", "(*)", "(-)");
        AssertGlyphs(dict, ThemeKeys.VerticalScrollArrowGlyphs, "^", "v", ""); // arrow pair: empty Indeterminate default
        AssertGlyphs(dict, ThemeKeys.HorizontalScrollArrowGlyphs, "<", ">", ""); // arrow pair: empty Indeterminate default

        static void AssertGlyphs(ResourceDictionary dict, string key, string @unchecked, string @checked, string indeterminate)
        {
            Assert.True(dict.TryGetValue(key, out var value), $"theme dict missing glyph resource '{key}'");
            var glyphs = Assert.IsType<GlyphSetCarrier>(value);
            Assert.Equal((@unchecked, @checked, indeterminate), (glyphs.Unchecked, glyphs.Checked, glyphs.Indeterminate));
        }
    }

    [Theory] // W5 (ARCH-1 parity): the P9 controls that render inline render identically to the code-first BuiltIn
    [InlineData("TextBox")]
    [InlineData("ProgressBar")]
    [InlineData("Separator")]
    [InlineData("ListBox")]       // exercises the ListBox AND ListBoxItem XAML themes
    [InlineData("ItemsControl")]
    [InlineData("Label")]         // the Label default theme (added with W6)
    public void XamlP9ControlTheme_RendersIdenticallyToCSharpBuiltIn_AtRest(string control)
    {
        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 16, 5, () => MakeP9Control(control)),
            CaptureCells(xaml: true,  focus: false, 16, 5, () => MakeP9Control(control)));
    }

    [Fact] // the Label DEFAULT THEME renders its caption — without a theme, ContentControl has no presenter → blank (gap fix)
    public void Label_DefaultTheme_RendersItsContent()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(12, 2) });
        host.ShowRoot(new UIControls.Label { Content = "Hi" });
        Assert.True(host.RunUntilIdle());
        Assert.Equal("H", host.GetCell(0, 0).Grapheme);
        Assert.Equal("i", host.GetCell(1, 0).Grapheme);
    }

    [Fact] // W5: every P9 control theme parses + is present (covers the popup-rooted ones that can't render inline)
    public void AllP9ControlThemes_Parse_AndArePresentInTheDictionary()
    {
        var dict = CursorialDefaultTheme.LoadControls();
        Type[] themed =
        [
            typeof(UIControls.ItemsControl), typeof(UIControls.ListBox), typeof(UIControls.ListBoxItem),
            typeof(UIControls.Menu), typeof(UIControls.MenuItem), typeof(UIControls.ContextMenu),
            typeof(UIControls.Separator), typeof(UIControls.ToolTip), typeof(UIControls.TabControl),
            typeof(UIControls.TabItem), typeof(UIControls.ProgressBar), typeof(UIControls.TextBox),
            // #81 — the post-P9 twins (incl. the popup-rooted ComboBox/DatePicker the inline byte-identity can't reach)
            typeof(UIControls.Image), typeof(UIControls.Chart),
            typeof(UIControls.CalendarDayButton), typeof(UIControls.CalendarButton),
            typeof(UIControls.TreeView), typeof(UIControls.TreeViewItem),
            typeof(UIControls.ComboBox), typeof(UIControls.ComboBoxItem),
            typeof(UIControls.Calendar), typeof(UIControls.DatePicker),
            typeof(UIControls.BreadcrumbBar), typeof(UIControls.BreadcrumbBarItem),
        ];
        foreach (var t in themed)
            Assert.True(dict.TryGetValue(t, out _), $"XAML theme missing the control theme for {t.Name}");
    }

    // ── W7 #7: RUNTIME behavior of the popup-rooted XAML control themes (Menu/ContextMenu/ToolTip) + TabControl ──
    // These exercise the themes the inline byte-identity theory can't reach: they render on popup surfaces or switch
    // content. The parse+presence check (AllP9ControlThemes_…) proves they LOAD; these prove they WORK at runtime.

    private static int Popups(UIHeadlessHost host) => host.Application.WindowManager!.Popups.Count;

    private static bool RenderContains(UIHeadlessHost host, string text, int cols, int rows)
    {
        for (var r = 0; r < rows; r++)
        {
            var line = string.Concat(Enumerable.Range(0, cols).Select(c => host.GetCell(c, r).Grapheme ?? " "));
            if (line.Contains(text))
                return true;
        }
        return false;
    }

    [Fact] // the XAML TabControl theme renders the selected tab's content (PART_ContentHost) and switches it live
    public void XamlTabControlTheme_RendersSelectedContent_AndSwitches()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 6) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var tabs = new UIControls.TabControl { Width = 20, Height = 6 };
        tabs.Items.Add(new UIControls.TabItem { Header = "A", Content = "AAA" });
        tabs.Items.Add(new UIControls.TabItem { Header = "B", Content = "BBB" });
        host.ShowRoot(tabs);
        Assert.True(host.RunUntilIdle());

        Assert.True(RenderContains(host, "AAA", 20, 6));  // selected (first) tab content via PART_ContentHost
        Assert.False(RenderContains(host, "BBB", 20, 6));

        tabs.SelectedIndex = 1;
        Assert.True(host.RunUntilIdle());
        Assert.True(RenderContains(host, "BBB", 20, 6));  // PART_ContentHost re-bound to the new SelectedContent
        Assert.False(RenderContains(host, "AAA", 20, 6));
    }

    [Fact] // the XAML Menu/MenuItem theme hosts a submenu on a popup surface (the XAML MenuItem template's PART_Popup)
    public void XamlMenuTheme_OpensSubmenuPopup()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 12) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var file = new UIControls.MenuItem { Header = "File" };
        file.Items.Add(new UIControls.MenuItem { Header = "New" });
        var menu = new UIControls.Menu();
        menu.Items.Add(file);
        host.ShowRoot(menu);
        Assert.True(host.RunUntilIdle());

        file.IsSubmenuOpen = true;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(1, Popups(host));
        Assert.True(file.ItemContainerGenerator.ContainerFromIndex(0)!.IsAttachedToTree); // child realized on the surface
    }

    [Fact] // the XAML ContextMenu theme opens its popup-rooted vertical menu under the overlay
    public void XamlContextMenuTheme_OpensPopup()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 12) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var owner = new UIControls.Border { Width = 20, Height = 6 };
        var ctx = new UIControls.ContextMenu();
        ctx.Items.Add(new UIControls.MenuItem { Header = "Copy" });
        UIControls.ContextMenu.SetMenu(owner, ctx);
        host.ShowRoot(owner);
        Assert.True(host.RunUntilIdle());

        ctx.Open(owner);
        Assert.True(host.RunUntilIdle());
        Assert.True(ctx.IsOpen);
        Assert.Equal(1, Popups(host));
    }

    [Fact] // the XAML BreadcrumbBar/BreadcrumbBarItem twins render the trail and fold it from the LEFT, like the code-first pair
    public void XamlBreadcrumbBarTheme_RendersTrail_AndFoldsFromTheLeft()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 4) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var bar = new UIControls.BreadcrumbBar
        {
            ItemsSource = new[] { "Home", "Projects", "assets" },
            Width = 30,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        host.ShowRoot(bar);
        Assert.True(host.RunUntilIdle());

        Assert.True(RenderContains(host, "Home", 40, 4));
        Assert.True(RenderContains(host, "▸", 40, 4)); // the XAML template's PART_Separator
        Assert.False(bar.HasOverflow);

        bar.Width = 18;
        Assert.True(host.RunUntilIdle());

        Assert.True(bar.HasOverflow);
        Assert.True(RenderContains(host, "…", 40, 4));    // the XAML template's PART_OverflowChip came up
        Assert.True(RenderContains(host, "assets", 40, 4));
        Assert.False(RenderContains(host, "Home", 40, 4)); // the ancestors folded away
    }

    [Fact] // the XAML ToolTip theme shows on the hit-transparent popup after the hover delay
    public void XamlToolTipTheme_ShowsOnHover()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 12) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var element = new UIControls.Border { Width = 16, Height = 4, Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)) };
        UIControls.ToolTipService.SetTip(element, "hint");
        host.ShowRoot(element);
        Assert.True(host.RunUntilIdle());

        var origin = element.TranslateToWindow(0, 0);
        host.SendMouseMove(origin.Column + 1, origin.Row + 1);
        host.RunFrame();
        host.AdvanceTime(TimeSpan.FromMilliseconds(550)); // past the 500ms initial delay
        host.RunFrame();

        Assert.Equal(1, Popups(host)); // the themed tooltip shows on its own popup surface
    }

    // ── #81: the post-P9 control-theme XAML twins (batch 1: inline-renderable) ──

    private sealed class StubChart : Drawing.Charts.IChart
    {
        public void Render(Drawing.DrawingContext context, in Rect area) =>
            context.Set(area.Column, area.Row, "X", default(CellStyle));
    }

    [Theory] // the post-P9 inline-renderable twins render identically to the code-first BuiltIn
    [InlineData("Chart")]             // ChartPresenter draws the stub chart's marker
    [InlineData("Image")]             // no source ⇒ the placeholder text (no graphics protocol in the default preset)
    [InlineData("CalendarDayButton")] // a fill-bounded ContentPresenter cell
    [InlineData("CalendarButton")]
    [InlineData("TreeView")]          // exercises the TreeView AND TreeViewItem XAML themes (twisty + header bar)
    [InlineData("ComboBox")]          // the closed face: [selected … 'v']
    [InlineData("ComboBoxItem")]
    [InlineData("DatePicker")]        // the closed field: [date … 'v']
    public void XamlPostP9ControlTheme_RendersIdenticallyToCSharpBuiltIn_AtRest(string control)
    {
        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 16, 5, () => MakePostP9Control(control)),
            CaptureCells(xaml: true,  focus: false, 16, 5, () => MakePostP9Control(control)));
    }

    private static UIControls.Control MakePostP9Control(string control) => control switch
    {
        "Chart"             => new UIControls.Chart { Source = new StubChart(), Width = 16, Height = 5 },
        "Image"             => new UIControls.Image { PlaceholderContent = "img" },
        "CalendarDayButton" => new UIControls.CalendarDayButton { Content = "5", Width = 4 },
        "CalendarButton"    => new UIControls.CalendarButton { Content = "Jun", Width = 7 },
        "TreeView"          => new UIControls.TreeView { ItemsSource = new[] { "a", "b" }, Width = 16, Height = 5 },
        "ComboBox"          => new UIControls.ComboBox { ItemsSource = new[] { "a", "b" }, SelectedIndex = 0, Width = 12 },
        "ComboBoxItem"      => new UIControls.ComboBoxItem { Content = "a", Width = 12 },
        "DatePicker"        => new UIControls.DatePicker { SelectedDate = new DateOnly(2026, 6, 18), Width = 14 },
        _                   => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    [Fact] // the XAML Calendar theme renders identically to the code-first BuiltIn (the header chrome + the code-built grid)
    public void XamlCalendarTheme_RendersIdenticallyToCSharpBuiltIn()
    {
        // Pin Today/DisplayDate/FirstDayOfWeek so the code-built month grid is deterministic across both renders.
        static UIControls.Control MakeCalendar() => new UIControls.Calendar
        {
            Today = new DateOnly(2026, 6, 18),
            DisplayDate = new DateOnly(2026, 6, 1),
            FirstDayOfWeek = DayOfWeek.Sunday,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        Assert.Equal(
            CaptureCells(xaml: false, focus: false, 30, 10, MakeCalendar),
            CaptureCells(xaml: true,  focus: false, 30, 10, MakeCalendar));
    }

    [Fact] // the XAML DatePicker theme drops a Calendar onto a popup surface (PART_Popup → PART_Calendar)
    public void XamlDatePickerTheme_OpensCalendarPopup()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 14) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var dp = new UIControls.DatePicker { DisplayDate = new DateOnly(2026, 6, 1), Width = 14 };
        host.ShowRoot(dp);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(0, Popups(host));

        dp.IsDropDownOpen = true;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(1, Popups(host)); // the calendar dropped onto its own popup surface
    }

    [Fact] // the XAML ComboBox theme opens its drop-down on a popup surface (PART_Popup) showing the items
    public void XamlComboBoxTheme_OpensDropDownPopup()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(20, 10) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var combo = new UIControls.ComboBox { ItemsSource = new[] { "Alpha", "Beta" }, SelectedIndex = 0, Width = 14 };
        host.ShowRoot(combo);
        Assert.True(host.RunUntilIdle());
        Assert.Equal(0, Popups(host));

        combo.IsDropDownOpen = true;
        Assert.True(host.RunUntilIdle());
        Assert.Equal(1, Popups(host));                  // the drop-down opened on its own surface
        Assert.True(RenderContains(host, "Beta", 20, 10)); // the non-selected item is visible in the open list
    }

    private static UIControls.Control MakeControl(string control) => control switch
    {
        "RepeatButton" => new UIControls.RepeatButton { Content = "OK" },
        "ToggleButton" => new UIControls.ToggleButton { Content = "OK" },
        "CheckBox"     => new UIControls.CheckBox { Content = "OK" },
        "RadioButton"  => new UIControls.RadioButton { Content = "OK" },
        _              => new UIControls.Button { Content = "OK" },
    };

    private static UIControls.Control MakeP9Control(string control) => control switch
    {
        "TextBox"      => new UIControls.TextBox { Width = 14 },
        "ProgressBar"  => new UIControls.ProgressBar { Width = 14, Height = 1 },
        "Separator"    => new UIControls.Separator { Width = 14 },
        "ListBox"      => new UIControls.ListBox { ItemsSource = new[] { "a", "b", "c" }, Width = 14, Height = 4 },
        "ItemsControl" => new UIControls.ItemsControl { ItemsSource = new[] { "a", "b" }, Width = 14, Height = 4 },
        "Label"        => new UIControls.Label { Content = "Caption" },
        _              => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    [Fact] // the XAML ListView theme renders the pinned header strip + the column grid, and follows a live view switch
    public void XamlListViewTheme_RendersColumnGrid_AndSwitchesView()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 8) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var list = new UIControls.ListView { ItemsSource = new[] { "alpha", "bravo" }, DisplayMemberPath = null };
        list.Columns.Add(new UIControls.ListViewColumn { Header = "Name", Width = UIControls.GridLength.Star() });
        host.ShowRoot(list);
        Assert.True(host.RunUntilIdle());

        // The declarative ListViewTemplate's PART_HeaderPresenter + the ListViewItemTemplate's PART_Cells.
        Assert.True(RenderContains(host, "Name", 40, 8));
        Assert.True(RenderContains(host, "alpha", 40, 8));

        // A sort click lights the indicator that the declarative ListViewColumnHeaderTemplate hosts.
        list.CycleSort(list.Columns[0]);
        Assert.True(host.RunUntilIdle());
        Assert.True(RenderContains(host, "\u25b2", 40, 8));

        // The view switch swaps the items panel underneath the same (declarative) template.
        list.View = UIControls.ListViewViewMode.List;
        Assert.True(host.RunUntilIdle());
        Assert.IsType<UIControls.UniformWrapPanel>(UIControls.ItemsControl.ItemsPanelFromItemsControl(list));
        Assert.True(RenderContains(host, "alpha", 40, 8));
    }

    [Fact] // the IndigoDusk twin carries the same ListView keys and renders the same column grid (both halves stay in step)
    public void IndigoDuskListViewTheme_CarriesTheSameKeys_AndRenders()
    {
        var dict = Cursorial.UI.Themes.IndigoDusk.IndigoDuskTheme.LoadControls();
        Assert.True(dict.ContainsKey(typeof(UIControls.ListView)));
        Assert.True(dict.ContainsKey(typeof(UIControls.ListViewItem)));
        Assert.True(dict.ContainsKey(typeof(UIControls.ListViewColumnHeader)));

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 8) });
        host.Application.Theme = dict;

        var list = new UIControls.ListView { ItemsSource = new[] { "alpha", "bravo" } };
        list.Columns.Add(new UIControls.ListViewColumn { Header = "Name", Width = UIControls.GridLength.Star() });
        host.ShowRoot(list);
        Assert.True(host.RunUntilIdle());

        Assert.True(RenderContains(host, "Name", 40, 8));
        Assert.True(RenderContains(host, "alpha", 40, 8));
    }

    private static /*(string Glyph, Color Fg, Color Bg)*/string[] CaptureCells(
        bool xaml, bool focus, int cols, int rows, Func<UIControls.Control> factory)
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(cols, rows) });
        if (xaml)
            host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var element = factory();
        host.ShowRoot(element);
        Assert.True(host.RunUntilIdle());
        if (focus)
        {
            Assert.True(element.Focus(FocusNavigationMethod.Tab));
            host.RunFrame();
        }

        var cells = new List<(string, Color, Color)>(cols * rows);
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            var cell = host.GetCell(c, r);
            cells.Add((cell.Grapheme ?? string.Empty, cell.Style.Foreground, cell.Style.Background));
        }
        // return cells.ToArray();
        return cells.Select(c => $"'{c.Item1}', #{c.Item2.Red:X2}{c.Item2.Green:X2}{c.Item2.Blue:X2}, #{c.Item3.Red:X2}{c.Item3.Green:X2}{c.Item3.Blue:X2}").ToArray();
    }
    [Fact] // the XAML CompletionPopup theme templates the overlay: PART_List on a popup surface, matches bolded
    public void XamlCompletionPopupTheme_OpensAndBoldsTheMatch()
    {
        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(60, 12) });
        host.Application.Theme = CursorialDefaultTheme.LoadControls();

        var box = new UIControls.TextBox
        {
            Width = 16,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var completion = new UIControls.CompletionPopup
        {
            Target = box,
            Provider = new UIControls.DelegateCompletionProvider(query => new UIControls.CompletionContext(
                0,
                query.Text.Length,
                query.Text,
                [new UIControls.CompletionItem("Price") { KindLabel = "field" }])),
        };

        var root = new UIControls.Grid();
        root.Children.Add(box);
        root.Children.Add(completion);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        box.Focus();
        Assert.True(host.RunUntilIdle());
        host.SendText("Pr");
        Assert.True(host.RunUntilIdle());

        // The declarative template really expanded: the required PART_Popup / PART_List parts resolved
        // (a missing one throws at instantiation), the list is hosted on its own popup surface, and the
        // header/footer strips the control pushes into are present.
        Assert.True(completion.IsOpen);
        Assert.Equal(1, Popups(host));
        Assert.True(RenderContains(host, "Price", 60, 12));
        Assert.True(RenderContains(host, "1 match", 60, 12));
        Assert.True(RenderContains(host, "⎋ cancel", 60, 12));

        // …and the fuzzy highlight survives the XAML half too: "Pr" bold, "ice" not.
        var (column, row) = FindCells(host, "Price", 60, 12);
        Assert.True((host.GetCell(column, row).Style.Attributes & TextAttributes.Bold) != 0);
        Assert.True((host.GetCell(column + 2, row).Style.Attributes & TextAttributes.Bold) == 0);
    }

    private static (int Column, int Row) FindCells(UIHeadlessHost host, string text, int cols, int rows)
    {
        for (var r = 0; r < rows; r++)
        {
            var line = string.Concat(Enumerable.Range(0, cols).Select(c => host.GetCell(c, r).Grapheme ?? " "));
            var index = line.IndexOf(text, StringComparison.Ordinal);

            if (index >= 0)
                return (index, r);
        }

        return (-1, -1);
    }

}
