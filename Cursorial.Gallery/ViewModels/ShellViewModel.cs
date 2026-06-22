using Cursorial.Gallery.Infrastructure;

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
        _selectedPage = Pages[0];
    }

    /// <summary>The page catalog (the nav-list source).</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>The selected page (two-way with the nav <c>ListBox.SelectedItem</c>; drives the content host).</summary>
    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set => Set(ref _selectedPage, value);
    }
}
