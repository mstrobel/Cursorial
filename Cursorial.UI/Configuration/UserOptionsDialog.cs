using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Configuration;

/// <summary>
/// The settings dialog (FB-17 Stage B): one tab per <see cref="UserOptionCategory"/>, the
/// dialog-level scope switch (Global | This application), live preview with
/// <b>OK / Cancel / Reset</b> — Reset restores the open-time state without closing — and the
/// warning-gated Advanced tab whose capability overrides stage and offer the timed test.
/// Opened by the framework's Ctrl+Shift+O binding (configurable) or
/// <see cref="UIApplication.ShowUserOptionsDialogAsync"/>.
/// </summary>
public sealed class UserOptionsDialog : Window
{
    private readonly UserOptionsViewModel _model;
    private bool _saved;

    /// <summary>The dialog's model — the test seam (drives edits exactly as the bound controls would).</summary>
    internal UserOptionsViewModel ModelInternal => _model;

    /// <summary>Control themes resolve exact-key — opt into the base Window chrome (the GalleryRibbon pattern; without this the window has no template and measures 0×0).</summary>
    protected override object ControlThemeKey => typeof(Window);

    private UserOptionsDialog(UserOptionsViewModel model)
    {
        _model = model;

        Title = "Options";
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        SizeToContentMode = SizeToContentMode.Always;
        AutoFitToViewport = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Shadow = WindowShadow.Default;
        MaxWidth = 78;
        DataContext = model;
        Content = BuildContent();

        // ✕ / any non-OK close = Cancel (revert the live preview). OK sets _saved first.
        Closed += (_, _) =>
        {
            if (!_saved)
                _model.Cancel();

            _model.Dispose();
        };
    }

    /// <summary>
    /// Opens the dialog modally over <paramref name="application"/>'s active surface. Completes
    /// when it closes; at most one instance runs at a time
    /// (<see cref="UIApplication.ShowUserOptionsDialogAsync"/> guards).
    /// </summary>
    public static Task ShowAsync(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var dialog = new UserOptionsDialog(new UserOptionsViewModel(application));
        return dialog.ShowDialogAsync();
    }

    private UIElement BuildContent()
    {
        var nextTabIndex = 0;
        var root = new DockPanel { LastChildFill = true, Margin = new Margins(1, 0) };

        // ── scope switch (top) ─────────────────────────────────────────────────────────
        var scopeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Margins(0, 0, 0, 1)
        };

        scopeRow.Children.Add(new TextBlock { Text = "Editing:" });

        var globalScope = new RadioButton
                          {
                              Content = "_Global",
                              GroupName = "options-scope",
                              IsChecked = true,
                              TabIndex = nextTabIndex++
                          };

        var appScope = new RadioButton
                       {
                           Content = $"This _application ({_model.ApplicationId})",
                           GroupName = "options-scope",
                           TabIndex = nextTabIndex++
                       };

        globalScope.Checked += (_, _) => _model.IsEditingApplicationScope = false;
        appScope.Checked += (_, _) => _model.IsEditingApplicationScope = true;
        scopeRow.Children.Add(globalScope);
        scopeRow.Children.Add(appScope);
        DockPanel.SetDock(scopeRow, Dock.Top);
        root.Children.Add(scopeRow);

        // ── buttons + footer (bottom) ──────────────────────────────────────────────────
        var footer = new StackPanel { Margin = new Margins(0, 1, 0, 0) };

        var errorLine = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = WrapMode.WordWrap };
        errorLine.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.DangerBrush);
        footer.Children.Add(errorLine);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 1
        };

        var ok = new Button { Content = "_OK", IsDefault = true };
        ok.Click += (_, _) =>
        {
            try
            {
                _model.Save();
                _saved = true;
                errorLine.Visibility = Visibility.Collapsed;
                Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errorLine.Text = $"Could not save options: {ex.Message}";
                errorLine.Visibility = Visibility.Visible;
            }
        };

        var reset = new Button { Content = "_Reset" };
        reset.Click += (_, _) =>
        {
            _model.Reset();
            errorLine.Visibility = Visibility.Collapsed;
        };

        var cancel = new Button { Content = "_Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(); // Closed handler reverts (single revert path)

        buttons.Children.Add(ok);
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        footer.Children.Add(buttons);

        footer.Children.Add(new TextBlock
        {
            Text = $"Files: \n · {_model.GlobalFilePath}\n · {_model.ApplicationFilePath}",
            Opacity = 0.5,
            TextWrapping = WrapMode.WordWrap
        });

        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ── tabs (fill) ────────────────────────────────────────────────────────────────
        var tabs = new TabControl { TabIndex = nextTabIndex++ };

        foreach (var category in _model.Categories)
        {
            var page = new StackPanel { Margin = new Margins(1, 0) };

            if (category.IsAdvanced)
                page.Children.Add(OptionRowFactory.BuildAdvancedHeader(_model));

            if (category.Category == UserOptionCategory.General)
            {
                page.Children.Add(new TextBlock
                {
                    Text = $"Nerd Font sample: {_model.NerdFontProbeGlyphs}   (boxes/tofu ⇒ not a Nerd Font)",
                    Opacity = 0.75,
                    Margin = new Margins(0, 0, 0, 1)
                });
            }

            bool anyFuture = false;

            foreach (var option in category.Options)
            {
                if (option.Descriptor.ReservedForFuture)
                    anyFuture = true;

                page.Children.Add(OptionRowFactory.BuildRow(option, ref nextTabIndex));
            }

            if (anyFuture)
            {
                var futureFeatureText
                    = new TextBlock
                      {
                          Text = "* Takes effect in a future version",
                          Classes = { "info" },
                          Opacity = 0.5
                      };

                futureFeatureText.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.InfoBrush);
                page.Children.Add(futureFeatureText);
            }

            tabs.Items.Add(new TabItem
                           {
                               Header = "_" + category.DisplayName,
                               Content = new ScrollViewer
                                         {
                                             Content = page,
                                             MaxHeight = 30,
                                             HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                                         }
                           });

            foreach (var button in buttons.Children)
                button.TabIndex = nextTabIndex++;
        }

        root.Children.Add(tabs);
        return root;
    }
}
