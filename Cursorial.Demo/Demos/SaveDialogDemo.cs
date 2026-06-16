using System.ComponentModel;
using System.Windows.Input;
using Cursorial.Input;
using Cursorial.Rendering;            // Margins
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;              // Binding / BindingMode
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

// ReSharper disable CheckNamespace

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

    private sealed class Controller(UIApplication app)
    {
        private TextBlock _status = null!;
        private string _lastResult = "—";

        public UIElement BuildDesktop()
        {
            var root = new DockPanel();
            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.WindowBackground);
            root.AddHandler(UIElement.KeyDownEvent, OnHotkey);

            var header = new Border { Child = new TextBlock(" Cursorial.UI — P9.8 Save dialog canary   ·   s = Save As…   ·   q = quit") };
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
            var vm = new SaveViewModel();

            var nameBox = new TextBox { Placeholder = "untitled.txt" };
            nameBox.SetBinding(TextBox.TextProperty, new Binding(nameof(SaveViewModel.FileName)) { Mode = BindingMode.TwoWay });

            var overwrite = new CheckBox { Content = "_Overwrite existing" };

            var recent = new ListBox { ItemsSource = vm.RecentFiles, Height = 5 };
            recent.SelectionChanged += (_, _) =>
            {
                if (recent.SelectedItem is string picked)
                    vm.FileName = picked; // selecting a recent file fills the field (the two-way binding updates the box)
            };

            var save = new Button { Content = "_Save", IsDefault = true };
            var cancel = new Button { Content = "_Cancel", IsCancel = true, Margin = new Margins(1, 0, 0, 0) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Margins(0, 1, 0, 0),
            };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);

            var body = new StackPanel { Margin = new Margins(1), DataContext = vm };
            body.Children.Add(new Label { Content = "File _name:" }); // access key folds + targets the next focusable (the field)
            body.Children.Add(nameBox);
            body.Children.Add(overwrite);
            body.Children.Add(new TextBlock("\nRecent files:"));
            body.Children.Add(recent);
            body.Children.Add(buttons);

            var dialog = new Window
            {
                Title = "Save As",
                Content = body,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 46,
                Height = 17,
                CanResize = false,
            };
            dialog.SetResourceReference(Control.BackgroundProperty, ThemeKeys.PanelBrush);

            // Save is enabled only for a non-blank name, re-evaluated per keystroke: the two-way binding pushes
            // FileName to the VM on every change, the VM raises PropertyChanged, and we re-raise the command's
            // CanExecuteChanged so the bound button's effective-enabled flips live (the §3.9 demonstration).
            var saveCommand = new RelayCommand(
                _ => _ = AttemptSaveAsync(vm, overwrite, dialog),
                _ => !string.IsNullOrWhiteSpace(vm.FileName));
            vm.PropertyChanged += (_, _) => saveCommand.RaiseCanExecuteChanged();
            save.Command = saveCommand;
            cancel.Click += (_, _) => dialog.Close(null);

            // The first tab stop (the filename TextBox) auto-focuses on activation (the P6.1 post-layout focus).
            var result = await dialog.ShowDialogAsync();
            _lastResult = result is string fileName ? $"saved \"{fileName}\"" : "cancelled";
            UpdateStatus();
        }

        private async Task AttemptSaveAsync(SaveViewModel vm, CheckBox overwrite, Window dialog)
        {
            var name = vm.FileName.Trim();
            if (name.Length == 0)
                return; // the command's CanExecute guards this; belt-and-braces

            var exists = vm.RecentFiles.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
            if (exists && overwrite.IsChecked != true)
            {
                var replace = await ConfirmOverwriteAsync(name);
                if (!replace)
                    return; // keep the Save dialog open
            }

            dialog.Close(name);
        }

        private static async Task<bool> ConfirmOverwriteAsync(string name)
        {
            var replace = new Button { Content = "_Replace", IsDefault = true };
            var keep = new Button { Content = "_Cancel", IsCancel = true, Margin = new Margins(1, 0, 0, 0) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Margins(0, 1, 0, 0),
            };
            buttons.Children.Add(replace);
            buttons.Children.Add(keep);

            var body = new StackPanel { Margin = new Margins(1) };
            body.Children.Add(new TextBlock($"⚠  \"{name}\" already exists in this folder.\n   Do you want to replace it?"));
            body.Children.Add(buttons);

            var confirm = new Window
            {
                Title = "Confirm Save As",
                Content = body,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Width = 42,
                Height = 8,
                CanResize = false,
            };
            confirm.SetResourceReference(Control.BackgroundProperty, ThemeKeys.PanelBrush);

            replace.Click += (_, _) => confirm.Close(true);
            keep.Click += (_, _) => confirm.Close(false);

            var result = await confirm.ShowDialogAsync(); // modal over the (already modal) Save dialog
            return result is true;
        }

        private void UpdateStatus()
            => _status.Text = $" last: {_lastResult}    —    s = Save As…  ·  q = quit";
    }

    private sealed class SaveViewModel : INotifyPropertyChanged
    {
        private string _fileName = "";

        public string FileName
        {
            get => _fileName;
            set
            {
                if (_fileName == value)
                    return;
                _fileName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
            }
        }

        public IReadOnlyList<string> RecentFiles { get; } = ["report-final.png", "notes.pdf", "budget.xlsx", "draft.md"];

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ICommand with a live CanExecute (the Ctrl+R RelayCommand in UIControlsDemo is execute-only).
    private sealed class RelayCommand(Action<object?> execute, Predicate<object?> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute(parameter);
        public void Execute(object? parameter) => execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
