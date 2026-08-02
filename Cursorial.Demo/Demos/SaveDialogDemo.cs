using Cursorial.Input;
using Cursorial.Rendering; // Margins
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Dialogs; // Binding / BindingMode
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

// P9.8 — the SaveDialog canary: the whole P9 control set wired together on the REAL UIApplication frame
// loop, modelled on the "Save As" mockup (docs/ui-layer-design/tokyo-night-terminal-colorpicker-filedialogs.html).
// Press 's' (or click the desktop button) to open a MODAL Save dialog (S4 ShowDialogAsync — the frame loop is
// the pump). Inside:
//   • a TextBox filename field (Placeholder, two-way {Binding FileName}) — the Save button ENABLES per keystroke
//     because the bound VM raises the command's CanExecuteChanged on every change (the §3.9 per-change push),
//   • a recent-files ListBox (selecting a row fills the field),
//   • an "Overwrite existing" CheckBox,
//   • Save (IsDefault → Enter / Alt+S) and Cancel (IsCancel → Esc) buttons.
// Saving over an existing name without Overwrite raises a nested modal overwrite-confirm (modal-over-modal).
// Everything draws from the cell-faithful default theme (no explicit colors). q / Esc on the desktop exits.
internal sealed class SaveDialogDemo : IDemo
{
    public string Name => "save";
    public IReadOnlyList<string> Aliases => ["savedialog", "saveas"];

    public string Description =>
        "Cursorial.UI P9 SaveDialog canary (modal dialog: a {Binding} TextBox whose value enables Save per keystroke, " +
        "a recent-files ListBox, an Overwrite CheckBox, IsDefault/IsCancel buttons, and an overwrite-confirm; 's' opens it).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Save dialog demo. Opening alt screen — press 's' (or click Save As…) to open the modal " +
                          "Save dialog: type a name (Save enables per keystroke), pick a recent file, toggle Overwrite, " +
                          "then Save (Enter / Alt+S) or Cancel (Esc). Saving over an existing name without Overwrite " +
                          "asks to replace. q / Esc exits.");

        var app = UIApplication.DefaultBuilder().Build();
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
        private TextBlock _status = null!;
        private string _lastResult = "—";

        public UIElement BuildDesktop()
        {
            var root = new DockPanel();
            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.WindowBackground);
            root.AddHandler(UIElement.KeyDownEvent, OnHotkey);

            var header = new Border
                         {
                             Child = new TextBlock(
                                 " Cursorial.UI — P9.8 Save dialog canary   ·   s = Save As…   ·   q = quit")
                         };

            header.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var statusBar = new Border();
            statusBar.SetResourceReference(Border.BackgroundProperty, ThemeKeys.SurfaceBrush);
            _status = new TextBlock();
            statusBar.Child = _status;
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);

            var openButton = new Button { Content = "Save _As…" };
            openButton.Click += (_, _) => OpenSaveDialog();

            var hint = new StackPanel { Margin = new Margins(2, 1), Spacing = 1 };

            hint.Children.Add(new TextBlock(
                                  "\n   Press 's' or click the button below to open the modal Save dialog." +
                                  "\n   It exercises the full P9 control set — TextBox, ListBox, CheckBox, Buttons — on the real frame loop.\n"));

            hint.Children.Add(openButton);
            root.Children.Add(hint); // DockPanel last child fills the desktop

            UpdateStatus();
            return root;
        }

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

                case 's':
                    OpenSaveDialog();
                    e.Handled = true;
                    break;
            }
        }

        private async void OpenSaveDialog()
        {
            try
            {
                var result = await FileSaveDialog.ShowAsync(app,
                                                            new FileSaveDialogRequest("Save As")
                                                            {
                                                                CanCreateDirectories = true,
                                                                ConfirmOverwrite = true,
                                                                FileSystem = PhysicalFileSystemProvider.Instance,
                                                                View = ListViewViewMode.SmallIcons
                                                            },
                                                            app.Dispatcher.ShutdownToken);

                _lastResult = result.IsDismissed ? "Dismissed without saving." : $"Saved to {result.FilePath}";
            }
            catch (Exception ex)
            {
                _lastResult = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                UpdateStatus();
            }
        }

        private void UpdateStatus()
            => _status.Text = $" last: {_lastResult}    —    s = Save As…  ·  q = quit";
    }
}