using System.Windows.Input;

using Cursorial.Gallery.Infrastructure;
using Cursorial.Output;
using Cursorial.UI;

namespace Cursorial.Gallery.ViewModels;

/// <summary>The shell view-model: the page catalog bound to the nav <c>ListBox</c> (<see cref="Pages"/>) and the
/// current selection (<see cref="SelectedPage"/>, two-way). The shell's <c>ContentControl</c> binds its Content to
/// <see cref="SelectedPage"/>, which an implicit <c>DataTemplate</c> resolves to the matching page view.</summary>
public sealed class ShellViewModel : ViewModelBase
{
    private PageViewModel? _selectedPage;

    public ShellViewModel()
    {
        // The ScrollViewer page is first — scrolling is the framework's biggest bug surface (project memory).
        Pages =
        [
            new ScrollViewerPageViewModel(),
            new InputsPageViewModel(),
        ];

        CycleThemeVariant = new RelayCommand(ExecuteCycleThemeVariant);
        CycleColorTier = new RelayCommand(ExecuteCycleColorTier);
        Quit = new RelayCommand<string?>(ExecuteQuit);

        _selectedPage = Pages[0];
    }

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
            app.RequestedColorTier = tier switch
                                     {
                                         null                 => ColorDepth.Ansi256,
                                         ColorDepth.Truecolor => ColorDepth.Ansi256,
                                         ColorDepth.Ansi256   => ColorDepth.Ansi16,
                                         ColorDepth.Ansi16    => ColorDepth.NoColor,
                                         _                    => ColorDepth.Truecolor
                                     };
        }
    }

    private void ExecuteCycleThemeVariant()
    {
        if (UIApplication.Current is { ActualThemeVariant: var variant } app)
            app.RequestedThemeBase = variant.IsDark ? ThemeBase.Light : ThemeBase.Dark;
    }

    private async void ExecuteQuit(string? confirm)
    {
        try
        {
            const string confirmMessage = "Are you sure you want to quit?";
            const MessageBoxButton confirmButtons = MessageBoxButton.Yes | MessageBoxButton.No;

            if (bool.TryParse(confirm, out var confirmValue) && confirmValue)
            {
                MessageBoxButton? result = await MessageBox.ShowAsync(confirmMessage,
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
}