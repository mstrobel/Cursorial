using Cursorial.Input;
using Cursorial.Rendering.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

// ReSharper disable CheckNamespace

// P7 Cursorial.UI windowing showcase on the REAL UIApplication frame loop. A chrome-less desktop root
// hosts the S4 window system: 'n' opens draggable / resizable / maximizable Windows (drag the title bar,
// drag the ◢ grip, double-click the title bar to maximize, ✕ to close — all driven by the interim chrome
// template's WindowHitTestRoles), 'd' shows a MODAL dialog through ShowDialogAsync (the frame loop is the
// pump — the await never blocks the UI thread; OK/Cancel close it and the result is reported), 'm' opens a
// light-dismiss Popup menu (press outside or Esc dismisses — the Escape route crosses the popup surface
// back to its logical host), 'f' fits every clipped window, 'c' closes all. Shrink the TERMINAL while a
// window overhangs and the WM's top-right fit badge appears (no auto-shrink — your window size is kept;
// click "Fit windows" or press 'f'). q / Esc exits. The hotkeys are attached to every surface root because
// keys route to the focused surface — each window carries them too.
internal sealed class WindowingDemo : IDemo
{
    public string Name => "windows";
    public IReadOnlyList<string> Aliases => ["win", "windowing"];

    public string Description =>
        "Cursorial.UI S4 windowing showcase (n new window, d modal dialog, m popup menu, f fit-all, c close-all; drag/resize/maximize chrome; resize the terminal for the fit badge).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Windowing demo. Opening alt screen — 'n' new window (drag title bar / ◢ grip / " +
                          "double-click to maximize / ✕ close), 'd' modal dialog, 'm' popup menu, 'f' fit-all, " +
                          "'c' close-all; shrink the terminal while a window overhangs for the fit badge; q / Esc exits.");

        var app = UIApplication.DefaultBuilder().Build();
        // app.Theme = Cursorial.UI.Themes.IndigoDusk.IndigoDuskTheme.LoadTheme();
        var controller = new Controller(app);

        try
        {
            await app.RunAsync(controller.BuildDesktop);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private sealed class Controller(UIApplication app)
    {
        private StatusBarItem _status = null!;
        private Border _header = null!;
        private ContextMenu _menu = null!;
        private int _windowCount;
        private string _lastResult = "—";

        public UIElement BuildDesktop()
        {
            var root = new DockPanel();

            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.ElevationDesktop);

            AttachHotkeys(root);

            _header = new Border
                      {
                          Child = new TextBlock("Cursorial.UI — S4 windowing  ·  n new · d dialog · m menu · f fit-all · c close-all · q quit")
                                  {
                                      Margin = new(1, 0),
                                      TextTrimming = TextTrimming.CharacterEllipsis
                                  }
                      };

            _header.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.MutedBrush);
            _header.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationRaised);

            DockPanel.SetDock(_header, Dock.Top);
            root.Children.Add(_header);

            _status = new StatusBarItem();

            var statusBar = new StatusBar { Items = { _status } };

            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            var hint = new TextBlock(
                "\n   Press 'n' to open a window, then drag its title bar to move it, drag the ◢ corner to" +
                "\n   resize, or double-click the title bar to maximize. 'd' opens a modal dialog; 'm' a" +
                "\n   light-dismiss menu. Shrink the terminal while a window overhangs → the fit badge" +
                "\n   appears top-right (your window size is preserved — click it or press 'f' to refit).");

            hint.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.MutedBrush);

            root.Children.Add(hint); // DockPanel last child fills the desktop

            // The popup menu lives in the desktop's logical tree (so Escape routes back here and it inherits
            // context); it is placed under the header. Built once, opened/closed on 'm'.
            _menu = BuildMenu();

            app.WindowManager!.ActiveWindowChanged += (_, _) => UpdateStatus();
            UpdateStatus();
            return root;
        }

        private ContextMenu BuildMenu()
        {
            return new ContextMenu
                   {
                       Items =
                       {
                           MenuItem("New window", OpenWindow),
                           MenuItem("Modal dialog", OpenDialog),
                           MenuItem("Fit all windows", () => app.WindowManager!.FitAllWindowsToViewport()),
                           MenuItem("Close all windows", CloseAll)
                       }
                   };

            MenuItem MenuItem(string label, Action action)
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => action();
                return item;
            }
        }

        private void AttachHotkeys(UIElement surfaceRoot)
            => surfaceRoot.AddHandler(UIElement.KeyDownEvent, OnHotkey);

        private void OnHotkey(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return; // leave Ctrl+C for the S6 default exit gesture

            if (e.Key == Key.Escape)
            {
                app.Shutdown();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Character || e.Text.Length == 0)
                return;

            switch (char.ToLowerInvariant(e.Text.Span[0]))
            {
                case 'q':
                    app.Shutdown();
                    e.Handled = true;
                    break;

                case 'n':
                    OpenWindow();
                    e.Handled = true;
                    break;

                case 'd':
                    OpenDialog();
                    e.Handled = true;
                    break;

                case 'm':
                    _menu.Open(_header, new CellPosition(0, 0));
                    e.Handled = true;
                    break;

                case 'f':
                    app.WindowManager!.FitAllWindowsToViewport();
                    e.Handled = true;
                    break;

                case 'c':
                    CloseAll();
                    e.Handled = true;
                    break;
            }
        }

        private void OpenWindow()
        {
            var n = ++_windowCount;
            var body = new StackPanel();
            body.Children.Add(new TextBlock($"  Window #{n}"));

            body.Children.Add(new TextBlock("  Drag the title bar to move me;\n  drag the ◢ corner to resize;\n" +
                                            "  double-click the title bar to maximize."));

            var close = new Button { Content = "Close" };

            var window = new Window
                         {
                             Title = $"Window #{n}",
                             Content = body,
                             WindowStartupLocation = WindowStartupLocation.Manual,
                             Left = 6 + n * 3 % 24, // cascade
                             Top = 3 + n * 2 % 12,
                             Width = 34,
                             Height = 9
                         };

            close.Click += (_, _) => window.Close();
            body.Children.Add(close);
            AttachHotkeys(body); // keys route to the focused surface — the window carries the hotkeys too
            window.Closed += (_, _) => UpdateStatus();

            window.Show();
            UpdateStatus();
        }

        private async void OpenDialog()
        {
            var prompt = new StackPanel();
            prompt.Children.Add(new TextBlock("  Save changes before closing?"));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var ok = new Button { Content = "OK" };
            var cancel = new Button { Content = "Cancel" };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            prompt.Children.Add(buttons);

            var dialog = new Window
                         {
                             Title = "Confirm",
                             Content = prompt,
                             // Background = new SolidColorBrush(WindowBg, 0.95),
                             WindowStartupLocation = WindowStartupLocation.CenterScreen,
                             Width = 36,
                             Height = 7,
                             CanResize = false
                         };

            ok.Click += (_, _) => dialog.Close("OK");
            cancel.Click += (_, _) => dialog.Close("Cancel");
            AttachHotkeys(prompt);

            // The frame loop is the pump — awaiting does not block the UI thread; the continuation resumes
            // on the UI dispatcher (the captured sync context), so touching _status here is thread-safe.
            var result = await dialog.ShowDialogAsync();
            _lastResult = result?.ToString() ?? "(closed)";
            UpdateStatus();
        }

        private void CloseAll()
        {
            _ = app.WindowManager!.CloseAllAsync();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var wm = app.WindowManager!;

            _status.Content = $" windows: {wm.Windows.Count}  ·  active: {wm.ActiveWindow?.Title ?? "(desktop)"}  ·  " +
                              $"last dialog: {_lastResult}  ·  badge: {(wm.IsFitBadgeVisible ? "shown" : "hidden")}  " +
                              "—  n new · d dialog · m menu · f fit · c close-all · q quit";
        }
    }
}