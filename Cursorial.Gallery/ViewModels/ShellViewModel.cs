using System.Collections.ObjectModel;
using System.Windows.Input;

using Cursorial.Gallery.Infrastructure;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.UI;

namespace Cursorial.Gallery.ViewModels;

/// <summary>The shell view-model: the page catalog bound to the nav <c>ListBox</c> (<see cref="Pages"/>) and the
/// current selection (<see cref="SelectedPage"/>, two-way). The shell's <c>ContentControl</c> binds its Content to
/// <see cref="SelectedPage"/>, which an implicit <c>DataTemplate</c> resolves to the matching page view.</summary>
public sealed class ShellViewModel : ViewModelBase
{
    private PageViewModel? _selectedPage;

    public ShellViewModel(UIApplication app)
    {
        // The ScrollViewer page is first — scrolling is the framework's biggest bug surface (project memory).
        Pages =
        [
            new ButtonsViewModel(),
            new DialogsViewModel(app),
            new InputsPageViewModel(),
            new MenusViewModel(),
            new BarsViewModel(),
            new RibbonViewModel(),
            new TabControlViewModel(),
            new ScrollViewerPageViewModel()
        ];

        CycleThemeVariant = new RelayCommand(ExecuteCycleThemeVariant);
        CycleColorTier = new RelayCommand(ExecuteCycleColorTier);
        Quit = new RelayCommand<string?>(ExecuteQuit);
        
        _selectedPage = Pages[0];
    }

    public string ThemeDescription => $"{UIApplication.Current?.ActualThemeVariant.ToString().ToLowerInvariant()}";

    public bool DiagnosticsOverflowed { get; private set; }

    public LifoList<string> Diagnostics { get; } = new(new ObservableCollection<string>());

    public ICommand CycleThemeVariant { get; }

    public ICommand CycleColorTier { get; }

    public ICommand Quit { get; }

    /// <summary>The page catalog (the nav-list source).</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>The selected page (two-way with the nav <c>ListBox.SelectedItem</c>; drives the content host).</summary>
    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set => Set(ref _selectedPage, value);
    }

    private void ExecuteCycleColorTier()
    {
        if (UIApplication.Current is { RequestedColorTier: var tier } app)
        {
            if (tier is null)
                tier = app.ActualThemeVariant.Tier;


            var newTier = tier switch
                          {
                              null                 => ColorDepth.Ansi256,
                              ColorDepth.Truecolor => ColorDepth.Ansi256,
                              ColorDepth.Ansi256   => ColorDepth.Ansi16,
                              ColorDepth.Ansi16    => ColorDepth.NoColor,
                              _                    => ColorDepth.Truecolor
                          };

            app.OnCapabilitiesChanged(app.Capabilities with
                                      {
                                          Output = app.Capabilities.Output with
                                                   {
                                                       Color = app.Capabilities.Output.Color with
                                                               {
                                                                   Depth = newTier
                                                               }
                                                   }
                                      });

            Raise(nameof(ThemeDescription));
        }
    }

    private void ExecuteCycleThemeVariant()
    {
        if (UIApplication.Current is { ActualThemeVariant: var variant } app)
        {
            app.RequestedThemeBase = variant.IsDark ? ThemeBase.Light : ThemeBase.Dark;
            RefreshTheme();
        }
    }

    public void RefreshTheme()
    {
        Raise(nameof(ThemeDescription));
    }

    private async void ExecuteQuit(string? confirm)
    {
        try
        {
            const string confirmTitle = "Confirm Quit";
            const string confirmMessage = "Are you sure you want to quit?";
            const MessageBoxButton confirmButtons = MessageBoxButton.Yes | MessageBoxButton.No;

            if (bool.TryParse(confirm, out var confirmValue) && confirmValue)
            {

                MessageBoxButton? result = await MessageBox.ShowAsync(confirmMessage,
                                                                      title: confirmTitle,
                                                                      buttons: confirmButtons,
                                                                      defaultButton: MessageBoxButton.Yes,
                                                                      cancelButton: MessageBoxButton.No,
                                                                      focusedButton: MessageBoxButton.No);

                if (result is not MessageBoxButton.Yes)
                    return;
            }
        }
        catch (OperationCanceledException) {}

        if (UIApplication.Current is {} app)
            app.Shutdown();
    }

    public void AddDiagnostic(string message)
    {
        if (DiagnosticsOverflowed)
            return;
        
        Diagnostics.Add(message);
        
        if (Diagnostics.Count >= 999)
        {
            DiagnosticsOverflowed = true;
            Diagnostics.Add("[WARNING  ] 1,000 diagnostics have been reported. No more will be displayed.");
        }
    }
}