using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

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
    public string Description => "Cursorial.UI S4 windowing showcase (n new window, d modal dialog, m popup menu, f fit-all, c close-all; drag/resize/maximize chrome; resize the terminal for the fit badge).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Windowing demo. Opening alt screen — 'n' new window (drag title bar / ◢ grip / " +
                          "double-click to maximize / ✕ close), 'd' modal dialog, 'm' popup menu, 'f' fit-all, " +
                          "'c' close-all; shrink the terminal while a window overhangs for the fit badge; q / Esc exits.");

        var app = UIApplication.CreateBuilder().WithFrameRate(60).Build();
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

    private static readonly Color DesktopBg = Color.FromRgb(24, 26, 36);
    private static readonly Color HeaderBg = Color.FromRgb(86, 120, 220);
    private static readonly Color HeaderText = Color.FromRgb(240, 244, 255);
    private static readonly Color StatusBg = Color.FromRgb(30, 33, 44);
    private static readonly Color StatusText = Color.FromRgb(160, 170, 205);
    private static readonly Color WindowBg = Color.FromRgb(40, 44, 60);
    private static readonly Color WindowText = Color.FromRgb(210, 216, 238);
    private static readonly Color MenuBg = Color.FromRgb(48, 52, 70);

    private sealed class Controller(UIApplication app)
    {
        private TextBlock _status = null!;
        private Border _header = null!;
        private Popup _menu = null!;
        private int _windowCount;
        private string _lastResult = "—";

        public UIElement BuildDesktop()
        {
            var root = new DockPanel { Background = new SolidColorBrush(DesktopBg) };
            AttachHotkeys(root);

            _header = new Border
            {
                Background = new SolidColorBrush(HeaderBg),
                Child = new TextBlock(" Cursorial.UI — S4 windowing  ·  n new · d dialog · m menu · f fit-all · c close-all · q quit")
                {
                    Foreground = new SolidColorBrush(HeaderText),
                },
            };
            DockPanel.SetDock(_header, Dock.Top);
            root.Children.Add(_header);

            var statusBar = new Border { Background = new SolidColorBrush(StatusBg) };
            _status = new TextBlock { Foreground = new SolidColorBrush(StatusText) };
            statusBar.Child = _status;
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            var hint = new TextBlock(
                "\n   Press 'n' to open a window, then drag its title bar to move it, drag the ◢ corner to" +
                "\n   resize, or double-click the title bar to maximize. 'd' opens a modal dialog; 'm' a" +
                "\n   light-dismiss menu. Shrink the terminal while a window overhangs → the fit badge" +
                "\n   appears top-right (your window size is preserved — click it or press 'f' to refit).")
            {
                Foreground = new SolidColorBrush(WindowText),
            };
            root.Children.Add(hint); // DockPanel last child fills the desktop

            // The popup menu lives in the desktop's logical tree (so Escape routes back here and it inherits
            // context); it is placed under the header. Built once, opened/closed on 'm'.
            _menu = BuildMenu();
            root.Children.Add(_menu);

            app.WindowManager!.ActiveWindowChanged += (_, _) => UpdateStatus();
            UpdateStatus();
            return root;
        }

        private Popup BuildMenu()
        {
            var items = new StackPanel { Background = new SolidColorBrush(MenuBg) };
            items.Children.Add(MenuItem("New window", OpenWindow));
            items.Children.Add(MenuItem("Modal dialog", OpenDialog));
            items.Children.Add(MenuItem("Fit all windows", () => app.WindowManager!.FitAllWindowsToViewport()));
            items.Children.Add(MenuItem("Close all windows", CloseAll));

            return new Popup
            {
                Placement = PlacementMode.Bottom,
                Child = new Border
                {
                    Background = new SolidColorBrush(MenuBg),
                    BorderPen = Pens.Light,
                    Occludes = true,
                    Child = items,
                },
            };

            Button MenuItem(string label, Action action)
            {
                var button = new Button { Content = label };
                button.Click += (_, _) =>
                {
                    _menu.Close();
                    action();
                };
                return button;
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
                case 'q': app.Shutdown(); e.Handled = true; break;
                case 'n': OpenWindow(); e.Handled = true; break;
                case 'd': OpenDialog(); e.Handled = true; break;
                case 'm': _menu.PlacementTarget = _header; _menu.IsOpen = !_menu.IsOpen; e.Handled = true; break;
                case 'f': app.WindowManager!.FitAllWindowsToViewport(); e.Handled = true; break;
                case 'c': CloseAll(); e.Handled = true; break;
            }
        }

        private void OpenWindow()
        {
            var n = ++_windowCount;
            var body = new StackPanel { Background = new SolidColorBrush(WindowBg) };
            body.Children.Add(new TextBlock($"  Window #{n}") { Foreground = new SolidColorBrush(WindowText) });
            body.Children.Add(new TextBlock("  Drag the title bar to move me;\n  drag the ◢ corner to resize;\n  double-click the title bar to maximize.")
            {
                Foreground = new SolidColorBrush(WindowText),
            });
            var close = new Button { Content = "Close" };

            var window = new Window
            {
                Title = $"Window #{n}",
                Content = body,
                Background = new SolidColorBrush(WindowBg),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 6 + n * 3 % 24, // cascade
                Top = 3 + n * 2 % 12,
                Width = 34,
                Height = 9,
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
            var prompt = new StackPanel { Background = new SolidColorBrush(WindowBg) };
            prompt.Children.Add(new TextBlock("  Save changes before closing?") { Foreground = new SolidColorBrush(WindowText) });

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
                Background = new SolidColorBrush(WindowBg),
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 36,
                Height = 7,
                CanResize = false,
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
            _status.Text = $" windows: {wm.Windows.Count}  ·  active: {wm.ActiveWindow?.Title ?? "(desktop)"}  ·  " +
                           $"last dialog: {_lastResult}  ·  badge: {(wm.IsFitBadgeVisible ? "shown" : "hidden")}  " +
                           "—  n new · d dialog · m menu · f fit · c close-all · q quit";
        }
    }
}
