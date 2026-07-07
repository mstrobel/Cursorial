using Cursorial.Rendering;
using Cursorial.Rendering.Text;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Configuration;

/// <summary>
/// The first-run wizard (FB-17 Stage B): a modal pager over the SAME shared options view-model
/// the settings dialog uses, pinned to the global layer. Welcome (framework key bindings) →
/// Terminal essentials → Appearance → configuration-file locations. Finish persists; Skip (or
/// closing the window) keeps nothing; either way the caller marks the first run completed.
/// </summary>
public sealed class FirstRunWizard : Window
{
    private readonly FirstRunWizardViewModel _model;
    private readonly ContentPresenter _pageHost;
    private readonly Button _back;
    private readonly Button _next;
    private readonly TextBlock _errorLine;
    private readonly Dictionary<WizardPageViewModel, UIElement> _pageCache = [];
    private bool _finished;

    /// <summary>Control themes resolve exact-key — opt into the base Window chrome (the GalleryRibbon pattern; without this the window has no template and measures 0×0).</summary>
    protected override object ControlThemeKey => typeof(Window);

    private FirstRunWizard(FirstRunWizardViewModel model)
    {
        _model = model;

        Title = "Welcome to Cursorial";
        CanResize = false;
        CanClose = true; // ✕ = Skip
        SizeToContent = SizeToContent.WidthAndHeight;
        SizeToContentMode = SizeToContentMode.Always;
        AutoFitToViewport = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Shadow = WindowShadow.Default;
        MaxWidth = 72;
        DataContext = model;

        _pageHost = new ContentPresenter();
        _back = new Button { Content = "_Back" };
        _next = new Button { Content = "_Next", IsDefault = true };
        _errorLine = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = WrapMode.WordWrap };
        _errorLine.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.DangerBrush);

        Content = BuildChrome();
        ShowCurrentPage();

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirstRunWizardViewModel.PageIndex))
                ShowCurrentPage();
        };

        Closed += (_, _) =>
        {
            if (!_finished)
                _model.Skip(); // ✕ / Esc: discard edits; the caller still writes the marker

            _model.Dispose();
        };
    }

    /// <summary>
    /// Completes the wizard programmatically — persists the edits and closes. The Finish button's
    /// exact path; without it, a Close() after a manual model save would run the Skip semantics
    /// (whose Reset discards the store state the save just wrote). A failed save (unwritable
    /// config root — the environment first-run onboarding is MOST likely to meet) surfaces on the
    /// error line and keeps the wizard open instead of crashing the first-run experience.
    /// </summary>
    public void Finish()
    {
        try
        {
            _model.Finish();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _errorLine.Text = $"Could not save options: {ex.Message} — you can Skip and configure later.";
            _errorLine.Visibility = Visibility.Visible;
            return;
        }

        _finished = true;
        Close();
    }

    /// <summary>Shows the wizard modally; completes when it closes (finished OR skipped).</summary>
    public static Task ShowAsync(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var wizard = new FirstRunWizard(new FirstRunWizardViewModel(application));
        return wizard.ShowDialogAsync();
    }

    private UIElement BuildChrome()
    {
        var root = new DockPanel { LastChildFill = true, Margin = new Margins(1, 0) };

        // Header: page title + progress.
        var headerRow = new DockPanel { LastChildFill = true, Margin = new Margins(0, 0, 0, 1) };
        var progress = new TextBlock { Opacity = 0.6 };
        progress.SetBinding(TextBlock.TextProperty, new Binding(nameof(FirstRunWizardViewModel.ProgressText)));
        DockPanel.SetDock(progress, Dock.Right);
        headerRow.Children.Add(progress);

        var pageTitle = new TextBlock();
        pageTitle.SetValue(TextElement.TextAttributesProperty, Output.TextAttributes.Bold);
        pageTitle.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentBrush);
        pageTitle.SetBinding(TextBlock.TextProperty, new Binding("CurrentPage.Title"));
        headerRow.Children.Add(pageTitle);
        DockPanel.SetDock(headerRow, Dock.Top);
        root.Children.Add(headerRow);

        // Nav buttons (bottom).
        var nav = new DockPanel { LastChildFill = false, Margin = new Margins(0, 1, 0, 0) };

        var skip = new Button { Content = "_Skip for now", IsCancel = true };
        skip.Click += (_, _) => Close(); // Closed handler runs Skip()
        nav.Children.Add(skip);

        var forward = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _back.Click += (_, _) => _model.Back();
        _back.IsEnabled = false;
        forward.Children.Add(_back);

        _next.Click += (_, _) =>
        {
            if (_model.IsLastPage)
                Finish();
            else
                _model.Next();
        };
        forward.Children.Add(_next);

        DockPanel.SetDock(forward, Dock.Right);
        nav.Children.Add(forward);
        DockPanel.SetDock(nav, Dock.Bottom);
        root.Children.Add(nav);

        DockPanel.SetDock(_errorLine, Dock.Bottom); // docked after nav ⇒ stacks just above it
        root.Children.Add(_errorLine);

        root.Children.Add(_pageHost);
        return root;
    }

    private void ShowCurrentPage()
    {
        var page = _model.CurrentPage;

        // Pages are built ONCE and reused across Back/Next — each option-row build subscribes to
        // its view-model, so rebuilding per navigation would accumulate orphaned row sets that
        // keep refreshing on every write for the wizard's lifetime.
        if (!_pageCache.TryGetValue(page, out var content))
        {
            content = page.Kind switch
            {
                WizardPageKind.Welcome => BuildWelcomePage(),
                WizardPageKind.Files => BuildFilesPage(),
                _ => BuildOptionsPage(page)
            };

            _pageCache[page] = content;
        }

        _pageHost.Content = content;
        _back.IsEnabled = _model.CanGoBack;
        _next.Content = _model.IsLastPage ? "_Finish" : "_Next";
    }

    private int _maxWidth, _maxHeight;

    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);

        _maxWidth = FittedWidth is int fw ? Math.Max(_maxWidth, fw) : _maxWidth;
        _maxHeight = FittedHeight is int fh ? Math.Max(_maxHeight, fh) : _maxHeight;
        Title = $"Cursorial Setup - {_maxWidth}x{_maxHeight}";
    }

    private UIElement BuildWelcomePage()
    {
        var page = new StackPanel { Margin = new Margins(1) };

        page.Children.Add(new TextBlock
        {
            Text = "This looks like the first time a Cursorial application has run on this system. "
                 + "A minute of setup tunes every Cursorial app to your terminal. "
                 + "First, the key bindings every Cursorial application shares:",
            TextWrapping = WrapMode.WordWrap,
            Margin = new Margins(0, 0, 0, 1)
        });

        var grid = new Grid { Margin = new Margins(2, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star() });

        var row = 0;
        foreach (var (gesture, description) in _model.KeyBindingSummary)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var chord = new TextBlock { Text = gesture, Margin = new Margins(0, 0, 2, 0) };
            chord.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentBrush);
            Grid.SetRow(chord, row);
            Grid.SetColumn(chord, 0);
            grid.Children.Add(chord);

            var what = new TextBlock { Text = description, TextWrapping = WrapMode.WordWrap };
            Grid.SetRow(what, row);
            Grid.SetColumn(what, 1);
            grid.Children.Add(what);
            row++;
        }

        page.Children.Add(grid);
        return page;
    }

    private UIElement BuildOptionsPage(WizardPageViewModel page)
    {
        var panel = new StackPanel { Margin = new Margins(1) };

        if (page.Options.Any(static o => o.Descriptor.Key == UserOptionKeys.NerdFont))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Nerd Font sample: {_model.Options.NerdFontProbeGlyphs}   (boxes/tofu ⇒ not a Nerd Font)",
                Opacity = 0.75,
                Margin = new Margins(0, 0, 0, 1)
            });
        }

        foreach (var option in page.Options)
        {
            if (option.Descriptor.ReservedForFuture)
                continue; // the wizard shows only options that do something today

            panel.Children.Add(OptionRowFactory.BuildRow(option));
        }

        return panel;
    }

    private UIElement BuildFilesPage()
    {
        var page = new StackPanel { Margin = new Margins(1) };

        page.Children.Add(
            new TextBlock
            {
                Text = "Your choices are saved as plain JSON — hand-edit them any time (comments and "
                       + "trailing commas are tolerated):",
                TextWrapping = WrapMode.WordWrap,
                Margin = new Margins(0, 0, 0, 1)
            });

        var global = new TextBlock
                     {
                         Markup = $"All Cursorial apps:[br/][brush {ThemeKeys.TextDimBrush}]" +
                                  $" · " + _model.Options.GlobalFilePath + "[/brush]",
                         TextWrapping = WrapMode.WordWrap
                     };
        global.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentBrush);
        page.Children.Add(global);

        var app = new TextBlock
        {
            Markup = $"This app only:[br/][brush {ThemeKeys.TextDimBrush}]" +
                     $" · " + _model.Options.ApplicationFilePath + "[/brush]",
            TextWrapping = WrapMode.WordWrap,
            Margin = new Margins(0, 0, 0, 1)
        };
        app.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentBrush);
        page.Children.Add(app);

        page.Children.Add(new TextBlock
        {
            Text = "Per-app values override global ones only for the keys they set — clear a key to "
                 + "inherit again. Press Finish to save, or Skip to keep everything as it was.",
            TextWrapping = WrapMode.WordWrap
        });

        return page;
    }
}
