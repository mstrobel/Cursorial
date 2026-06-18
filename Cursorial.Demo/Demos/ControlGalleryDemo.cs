using Cursorial.Input;
using Cursorial.Output;             // ColorDepth
using Cursorial.Rendering;          // Margins
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

using Style = Cursorial.UI.Style;

// ReSharper disable CheckNamespace

// P9 control GALLERY — the visual acceptance artifact for the full P9 control set + theme hardening across
// tiers. One screen exercising every P9 control on the REAL UIApplication frame loop, default-themed (no
// explicit colors), so the cell-faithful default look shows UNMASKED and re-skins live:
//   menu bar (File/Edit + a submenu, access keys + gesture text),
//   a TabControl whose tabs hold a data form (TextBox + ToolTip, CheckBoxes, RadioButtons, a determinate +
//     an indeterminate ProgressBar, OK/Cancel Buttons) and a striped ListBox (a `:alternate` rule, W2) with
//     a right-click ContextMenu, framed by Separators.
//   't' cycles the color tier (Truecolor → Ansi256 → Ansi16 → NoColor), 'd' flips dark/light, F10/Alt enters
//   the menu, Tab moves focus, q / Esc quits. The companion to the `windows` demo (which shows the W1 chrome).
internal sealed class ControlGalleryDemo : IDemo
{
    public string Name => "gallery";
    public IReadOnlyList<string> Aliases => ["controls"];

    public string Description =>
        "P9 control gallery: every P9 control (menu, tabs, text box, list with :alternate striping + context menu, " +
        "progress bars, tooltips) on the real frame loop — 't' cycles color tier, 'd' flips dark/light, F10 the menu, q quits.";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("P9 control gallery. Opening alt screen — Tab moves focus, F10/Alt enters the menu, the " +
                          "tabs switch the form/list panes, right-click the list for a context menu, 't' cycles the " +
                          "color tier, 'd' flips dark/light; q / Esc / Ctrl+C exits.");

        var app = UIApplication.CreateBuilder().WithFrameRate(60).Build();
        var controller = new Controller(app);
        try
        {
            await app.RunAsync(controller.BuildTree);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private sealed class Controller(UIApplication app)
    {
        private TextBlock _status = null!;
        private string _lastAction = "(none)";
        private ProgressBar _progress = null!;

        public UIElement BuildTree()
        {
            // A page-level striping rule so the ListBox shows the :alternate pseudo-class (W2) — a SurfaceBrush
            // tint over the WellBrush well; DynamicResource so it re-skins on a tier/base flip.
            app.Styles.Add(new Style(Selectors.OfType<ListBoxItem>().PseudoClass("alternate"))
                .SetResource(Control.BackgroundProperty, ThemeKeys.SurfaceBrush));

            var root = new DockPanel();
            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.WindowBackground);
            root.AddHandler(UIElement.KeyDownEvent, OnRootKeyDown);
            root.AddHandler(UIElement.GotFocusEvent, (_, _) => UpdateStatus());

            var header = new TextBlock { Text = " Cursorial.UI — P9 control gallery", Height = 1 };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var menu = BuildMenu();
            DockPanel.SetDock(menu, Dock.Top);
            root.Children.Add(menu);

            var statusBar = new TextBlock { Height = 1 };
            _status = statusBar;
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            root.Children.Add(BuildTabs()); // fills the remainder

            UpdateStatus();
            return root;
        }

        private Menu BuildMenu()
        {
            var menu = new Menu();

            var file = new MenuItem { Header = "_File" };
            file.Items.Add(Leaf("_New", "Ctrl+N"));
            file.Items.Add(Leaf("_Open", "Ctrl+O"));
            file.Items.Add(Leaf("_Save", "Ctrl+S"));
            file.Items.Add(new Separator());
            file.Items.Add(Leaf("E_xit", null, () => app.Shutdown()));
            menu.Items.Add(file);

            var edit = new MenuItem { Header = "_Edit" };
            edit.Items.Add(Leaf("Cu_t", "Ctrl+X"));
            edit.Items.Add(Leaf("_Copy", "Ctrl+C"));
            edit.Items.Add(Leaf("_Paste", "Ctrl+V"));
            edit.Items.Add(new Separator());
            var find = new MenuItem { Header = "_Find" }; // a submenu (nesting)
            find.Items.Add(Leaf("Find _Next", "F3"));
            find.Items.Add(Leaf("Find _Previous", "Shift+F3"));
            edit.Items.Add(find);
            menu.Items.Add(edit);

            return menu;
        }

        private MenuItem Leaf(string header, string? gesture, Action? onClick = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null)
                item.InputGestureText = gesture;
            var label = header.Replace("_", string.Empty);
            item.Click += (_, _) => Action(onClick is null ? $"menu: {label}" : Run(onClick, label));
            return item;
        }

        private static string Run(Action a, string label) { a(); return $"menu: {label}"; }

        private TabControl BuildTabs()
        {
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "_Form", Content = BuildForm() });
            tabs.Items.Add(new TabItem { Header = "_List", Content = BuildList() });
            tabs.Items.Add(new TabItem { Header = "T_ree", Content = BuildTreeView() });
            tabs.Items.Add(new TabItem { Header = "_Date", Content = BuildCalendar() });
            return tabs;
        }

        private Border BuildCalendar()
        {
            // A live month calendar: click a day, the prev/next buttons or PageUp/PageDown change month, arrows move
            // the selection (today is highlighted in accent ink). Uses the real system date for "today".
            // The popup (calendar) variant: a field that drops a Calendar.
            var picker = new DatePicker { Width = 18, Watermark = "Select a date", HorizontalAlignment = HorizontalAlignment.Left };
            picker.SelectedDateChanged += (_, e) => Action($"picked {e.NewDate:yyyy-MM-dd}");

            // The inline variant: a standalone Calendar (always visible).
            var cal = new Calendar { HorizontalAlignment = HorizontalAlignment.Left };
            cal.SelectedDateChanged += (_, e) => Action($"date = {e.NewDate:yyyy-MM-dd}");

            var panel = new StackPanel { Spacing = 1, Margin = new Margins(1) };
            panel.Children.Add(new TextBlock { Text = "DatePicker (popup — Down/F4/click to drop):" });
            panel.Children.Add(picker);
            panel.Children.Add(new TextBlock { Text = "Calendar (inline — arrows / PageUp-Down / click):" });
            panel.Children.Add(cal);
            return new Border { Padding = new Margins(1), Child = panel };
        }

        private Border BuildTreeView()
        {
            // A small file-tree: parents expand/collapse (click the > / v twisty or press Right/Left); arrows
            // navigate the visible nodes; selection follows the keyboard cursor and is tree-wide single.
            TreeViewItem Node(string header, params TreeViewItem[] children)
            {
                var node = new TreeViewItem { Header = header };
                foreach (var child in children)
                    node.Items.Add(child);
                return node;
            }

            var tree = new TreeView { Width = 30, Height = 12 };
            tree.Items.Add(Node("src",
                Node("Controls", Node("TreeView.cs"), Node("ComboBox.cs"), Node("ListBox.cs")),
                Node("Themes", Node("ControlThemes.cs"))));
            tree.Items.Add(Node("docs", Node("ui-layer-design.md")));
            tree.Items.Add(new TreeViewItem { Header = "README.md" });
            ((TreeViewItem)tree.Items[0]!).IsExpanded = true; // open "src" so the tree shows structure at a glance

            tree.SelectionChanged += (_, e) => Action($"selected {(e.NewItem as TreeViewItem)?.Header}");

            var panel = new StackPanel { Spacing = 1, Margin = new Margins(1) };
            panel.Children.Add(new TextBlock { Text = "File tree (arrows / Right-Left, click a twisty):" });
            panel.Children.Add(tree);
            return new Border { Padding = new Margins(1), Child = panel };
        }

        private Border BuildForm()
        {
            var panel = new StackPanel { Spacing = 1, Margin = new Margins(1) };

            panel.Children.Add(new Label { Content = "User _name:" });
            var name = new TextBox { Width = 26, Placeholder = "username" };
            ToolTipService.SetTip(name, "Your login name (3–32 chars)");
            panel.Children.Add(name);

            panel.Children.Add(new Separator());

            var airplane = new CheckBox { Content = " Airplane mode" };
            var wifi = new CheckBox { Content = " Wi-Fi", IsChecked = true };
            airplane.Click += (_, _) => Action($"Airplane = {airplane.IsChecked}");
            wifi.Click += (_, _) => Action($"Wi-Fi = {wifi.IsChecked}");
            panel.Children.Add(airplane);
            panel.Children.Add(wifi);

            var low = new RadioButton { Content = " Low", GroupName = "q" };
            var med = new RadioButton { Content = " Medium", GroupName = "q", IsChecked = true };
            var high = new RadioButton { Content = " High", GroupName = "q" };
            panel.Children.Add(low);
            panel.Children.Add(med);
            panel.Children.Add(high);

            panel.Children.Add(new Separator());

            panel.Children.Add(new Label { Content = "_Theme:" });
            var theme = new ComboBox { ItemsSource = new[] { "Default", "Tokyo Night", "High Contrast" }, SelectedIndex = 0, Width = 26 };
            theme.SelectionChanged += (_, _) => Action($"Theme = {theme.SelectedItem}");
            panel.Children.Add(theme);

            panel.Children.Add(new Separator());

            panel.Children.Add(new TextBlock { Text = "Download (determinate):" });
            _progress = new ProgressBar { Value = 60, Maximum = 100, Height = 1, Width = 26 };
            panel.Children.Add(_progress);
            panel.Children.Add(new TextBlock { Text = "Working (indeterminate):" });
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 1, Width = 26 });

            var ok = new Button { Content = "_OK", IsDefault = true };
            var cancel = new Button { Content = "_Cancel", IsCancel = true };
            ok.Click += (_, _) => Action("OK");
            cancel.Click += (_, _) => Action("Cancel");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Margins(0, 1, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            return new Border { Padding = new Margins(1), Child = panel };
        }

        private Border BuildList()
        {
            var list = new ListBox
            {
                Width = 28,
                Height = 8,
                ItemsSource = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel" },
            };

            // A right-click context menu on the list (popup-rooted; on-close focus restore from W4).
            var ctx = new ContextMenu();
            ctx.Items.Add(Leaf("_Copy", "Ctrl+C"));
            ctx.Items.Add(Leaf("_Delete", "Del"));
            ctx.Items.Add(new Separator());
            ctx.Items.Add(Leaf("Select _All", "Ctrl+A"));
            ContextMenu.SetMenu(list, ctx);

            var panel = new StackPanel { Spacing = 1, Margin = new Margins(1) };
            panel.Children.Add(new TextBlock { Text = "Striped list (right-click for a menu):" });
            panel.Children.Add(list);
            return new Border { Padding = new Margins(1), Child = panel };
        }

        // ───────────────────────────── key handling ─────────────────────────────

        private void OnRootKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return;

            switch (e.Key)
            {
                case Key.Escape:
                    app.Shutdown();
                    e.Handled = true;
                    return;

                case Key.Character when e.Text.Length > 0:
                    switch (char.ToLowerInvariant(e.Text.Span[0]))
                    {
                        case 'q':
                            app.Shutdown();
                            e.Handled = true;
                            return;
                        case 't':
                            CycleColorTier();
                            e.Handled = true;
                            return;
                        case 'd':
                            app.RequestedThemeBase = app.ActualThemeVariant.IsDark ? ThemeBase.Light : ThemeBase.Dark;
                            Action($"theme base → {app.ActualThemeVariant.Base}");
                            e.Handled = true;
                            return;
                    }

                    break;
            }
        }

        private void CycleColorTier()
        {
            app.RequestedColorTier = app.RequestedColorTier switch
            {
                null                 => ColorDepth.Ansi256,
                ColorDepth.Truecolor => ColorDepth.Ansi256,
                ColorDepth.Ansi256   => ColorDepth.Ansi16,
                ColorDepth.Ansi16    => ColorDepth.NoColor,
                _                    => ColorDepth.Truecolor,
            };
            Action($"color tier → {app.ActualThemeVariant.Tier}");
        }

        private void Action(string what)
        {
            _lastAction = what;
            UpdateStatus();
        }

        private void UpdateStatus()
            => _status.Text = $" last: {_lastAction,-22} · theme: {app.ActualThemeVariant}" +
                              "   —   Tab focus · F10 menu · tabs switch panes · right-click list · t tier · d dark/light · q quit";
    }
}
